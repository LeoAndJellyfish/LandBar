#!/usr/bin/env python3
"""LandBar views backed by Antigravity's read-only CLI quota commands."""

from dataclasses import dataclass
import datetime as dt
from decimal import Decimal, InvalidOperation
import json
import math
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import threading
import time
from urllib.request import getproxies


HOST_LIMIT = 16 * 1024
CLI_LIMIT = 128 * 1024
POLL_SECONDS = 60
MIN_REFRESH_SECONDS = 5
STALE_SECONDS = 150
WINDOWS_PER_PAGE = 2
MIN_AGY_VERSION = (1, 1, 11)
HIDDEN = getattr(subprocess, "CREATE_NO_WINDOW", 0)
WINDOW_NAMES = {
    "5h": "5 小时窗口", "weekly": "每周窗口", "daily": "每日窗口", "monthly": "每月窗口",
}
WINDOW_ORDER = {"5h": 0, "daily": 1, "weekly": 2, "monthly": 3}
ACCENT, AMBER, MUTED = "#B7C8FF", "#E2B87C", "#85858F"


class QueryError(Exception):
    def __init__(self, message, clear=False):
        super().__init__(message)
        self.clear = clear


class Stopped(Exception):
    pass


@dataclass(frozen=True, slots=True)
class Window:
    title: str
    fraction: float | None
    amount: str | None
    resets: dt.datetime | None


@dataclass(frozen=True, slots=True)
class Snapshot:
    windows: tuple[Window, ...]
    credits: str | None
    fetched_at: float
    credits_failed: bool = False


def find_agy():
    """Use the native CLI, never a shell shim or an Antigravity credential file."""
    override = os.environ.get("LANDBAR_AGY_PATH")
    if override:
        path = Path(override).expanduser()
        if not path.is_absolute() or not path.is_file() or path.suffix.lower() != ".exe":
            raise QueryError("请检查 LANDBAR_AGY_PATH", clear=True)
        return path
    local = os.environ.get("LOCALAPPDATA")
    if local:
        path = Path(local) / "agy" / "bin" / "agy.exe"
        if path.is_file():
            return path
    found = shutil.which("agy.exe")
    if found and Path(found).is_absolute() and Path(found).suffix.lower() == ".exe":
        return Path(found)
    raise QueryError("请安装 Antigravity CLI", clear=True)


def cli_environment():
    environment = os.environ.copy()
    for key in ("GEMINI_API_KEY", "GOOGLE_API_KEY", "AGY_ADC_AUTH", "GOOGLE_APPLICATION_CREDENTIALS",
                "AGY_CLI_INTERACTIVE_HEADLESS"):
        environment.pop(key, None)
    # Print mode can still start OAuth when its session expires. The CLI's
    # non-interactive mode reports that sign-in is needed without opening it.
    environment["AGY_CLI_NONINTERACTIVE_HEADLESS"] = "1"
    # The updater can spawn its own console even when this process is hidden.
    environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true"
    # Go's proxy lookup uses environment variables. Pass through the configured
    # Windows proxy for this child only when no explicit override was supplied.
    configured = getproxies()
    for scheme, key in (("http", "HTTP_PROXY"), ("https", "HTTPS_PROXY"), ("no", "NO_PROXY")):
        if configured.get(scheme) and key not in environment and key.lower() not in environment:
            environment[key] = configured[scheme]
    return environment


def login_error(diagnostic):
    if any(marker in diagnostic for marker in ("api key", "gemini_api_key", "application default credentials")):
        return QueryError("请在 Antigravity CLI 使用 Google 账号登录", clear=True)
    if any(marker in diagnostic for marker in ("not signed in", "not logged in", "not logged into", "not authenticated",
                                                "unauthenticated", "no credentials", "please sign in", "please log in",
                                                "login required", "authentication required", "agy auth login", "agy acp --login")):
        return QueryError("请在终端登录 Antigravity 后刷新", clear=True)
    return None


def run_cli(command, stop, timeout):
    """Collect a bounded response and reclaim the owned process on failure or stop."""
    if stop.is_set():
        raise Stopped()
    with subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                          stderr=subprocess.PIPE, bufsize=0, env=cli_environment(), creationflags=HIDDEN) as process:
        output, errors = bytearray(), bytearray()

        def read(pipe, buffer):
            try:
                while len(buffer) <= CLI_LIMIT:
                    chunk = pipe.read(min(8192, CLI_LIMIT + 1 - len(buffer)))
                    if not chunk:
                        return
                    buffer.extend(chunk)
            except (OSError, ValueError):
                pass

        readers = [threading.Thread(target=read, args=(pipe, buffer), daemon=True)
                   for pipe, buffer in ((process.stdout, output), (process.stderr, errors))]
        for reader in readers:
            reader.start()
        deadline = time.monotonic() + timeout
        try:
            while process.poll() is None or any(reader.is_alive() for reader in readers):
                if stop.wait(.05):
                    raise Stopped()
                if len(output) > CLI_LIMIT or len(errors) > CLI_LIMIT:
                    raise QueryError("Antigravity CLI 响应过长")
                if time.monotonic() >= deadline:
                    auth = login_error((output + errors).decode("utf-8", errors="replace").casefold())
                    raise auth or QueryError("额度查询超时")
            if len(output) > CLI_LIMIT or len(errors) > CLI_LIMIT:
                raise QueryError("Antigravity CLI 响应过长")
            return process.returncode, bytes(output), bytes(errors)
        finally:
            if process.poll() is None:
                process.kill()
            process.wait(timeout=2)


def checked_version(output):
    match = re.search(rb"(?:^|[\s/v])([0-9]{1,5})\.([0-9]{1,5})\.([0-9]{1,5})(?=[\s+\-]|$)", output)
    if match is None or tuple(map(int, match.groups())) < MIN_AGY_VERSION:
        raise QueryError("Antigravity CLI 需更新至 1.1.11 或更高版本", clear=True)


def command_data(output, name, exit_code=0, stderr=b""):
    """Require the non-agent command envelope, including the zero-turn marker."""
    try:
        payload = json.loads(output)
    except (ValueError, UnicodeError, RecursionError):
        payload = None
    if isinstance(payload, dict):
        command = payload.get("command")
        if (exit_code == 0 and payload.get("status") == "SUCCESS"
                and type(payload.get("num_turns")) is int and payload["num_turns"] == 0
                and isinstance(command, dict) and command.get("name") == name
                and isinstance(command.get("data"), dict)):
            return command["data"]
    # Only locally classified messages reach the island. Raw CLI output stays in memory.
    error = str(payload.get("error", "")) if isinstance(payload, dict) else output.decode("utf-8", errors="replace")
    diagnostic = (error + " " + stderr.decode("utf-8", errors="replace")).casefold()
    auth = login_error(diagnostic)
    if auth:
        raise auth
    if exit_code:
        raise QueryError("Antigravity 额度暂不可用")
    raise QueryError("Antigravity CLI 返回了无法识别的额度数据")


def query_data(path, name, stop, runner=run_cli):
    if name not in ("usage", "credits"):
        raise ValueError("Unsupported query")
    result, output, errors = runner([str(path), "--print", "/" + name, "--output-format", "json",
                                     "--print-timeout", "30s", "--disable-slash-commands=false",
                                     "--log-file", os.devnull], stop, 45)
    return command_data(output, name, result, errors)


def numeric_label(value):
    if type(value) not in (int, float, str):
        return None
    literal = str(value).strip()
    if len(literal) > 96 or not re.fullmatch(r"[0-9]+(?:\.[0-9]+)?(?:[eE][+-]?[0-9]{1,5})?", literal):
        return None
    try:
        number = Decimal(literal)
    except InvalidOperation:
        return None
    if not number.is_finite() or number < 0:
        return None
    if number == 0:
        return "0"
    if number.adjusted() >= 15 or number.adjusted() < -3:
        return "≈" + format(number, ".5g")
    exact = format(number, "f")
    if "." in exact:
        exact = exact.rstrip("0").rstrip(".")
    return exact if len(exact) <= 20 else "≈" + format(number, ".5g")


def reset_time(value):
    if type(value) is not str or len(value) > 48:
        return None
    try:
        instant = dt.datetime.fromisoformat(value[:-1] + "+00:00" if value.endswith("Z") else value)
        if instant.tzinfo is not None and instant.year > 1970:
            return instant
    except (ValueError, OverflowError):
        pass
    return None


def gemini_windows(data):
    groups = data.get("groups")
    if type(groups) is not list:
        raise QueryError("Antigravity CLI 返回了无法识别的额度数据")
    windows = []
    seen = set()
    for group in groups[:48]:
        if not isinstance(group, dict) or not isinstance(group.get("buckets"), list):
            continue
        name = group.get("name")
        gemini_group = isinstance(name, str) and bool(re.match(r"^gemini(?:\b|[-_])", name.strip(), re.IGNORECASE))
        # Keep the shared Gemini pool compact. Distinguish separately reported named pools.
        family = ""
        if gemini_group and re.fullmatch(r"Gemini [A-Za-z0-9. -]{1,24}", name, re.IGNORECASE):
            if name.casefold() not in ("gemini model", "gemini models"):
                family = name[7:]
        for bucket in group["buckets"][:48]:
            if not isinstance(bucket, dict) or bucket.get("disabled") is True:
                continue
            identity = bucket.get("id")
            gemini_id = isinstance(identity, str) and bool(re.match(r"^gemini(?:\b|[-_])", identity, re.IGNORECASE))
            if not (gemini_group or gemini_id):
                continue
            period = bucket.get("window")
            if not isinstance(period, str):
                period = ""
            key = identity if isinstance(identity, str) and identity else (family, period)
            if key in seen:
                continue
            seen.add(key)
            fraction = bucket.get("remaining_fraction")
            if type(fraction) not in (int, float) or not 0 <= fraction <= 1:
                fraction = None
            amount = numeric_label(bucket.get("remaining_amount"))
            caption = WINDOW_NAMES.get(period, "套餐窗口")
            title = f"{family} · {caption}" if family else caption
            window = Window(title, fraction, amount, reset_time(bucket.get("reset_time")))
            windows.append((WINDOW_ORDER.get(period, 9), window))
    windows.sort(key=lambda entry: (entry[0], entry[1].title))
    return tuple(window for _, window in windows)


def credit_balance(data):
    return "不限额" if data.get("unlimited") is True else numeric_label(data.get("remaining_credits"))


class Antigravity:
    def __init__(self, runner=run_cli, discover=find_agy):
        self.runner = runner
        self.discover = discover
        self.verified_cli = None

    def fetch(self, stop):
        path = self.discover()
        stat = path.stat()
        signature = (path, stat.st_size, stat.st_mtime_ns)
        if signature != self.verified_cli:
            result, output, errors = self.runner([str(path), "--version"], stop, 12)
            if result:
                raise QueryError("Antigravity CLI 无法启动", clear=True)
            checked_version(output or errors)
            self.verified_cli = signature
        windows = gemini_windows(query_data(path, "usage", stop, self.runner))
        fetched_at = time.time()
        try:
            credits = credit_balance(query_data(path, "credits", stop, self.runner))
            return Snapshot(windows, credits, fetched_at)
        except QueryError as error:
            if error.clear:
                raise
            return Snapshot(windows, None, fetched_at, credits_failed=True)


def timestamp(value, fmt):
    try:
        return value.astimezone().strftime(fmt) if value is not None else "未知"
    except (ValueError, OSError, OverflowError):
        return "未知"


def percent(fraction):
    if fraction is None:
        return "—"
    if fraction == 1:
        return "100"
    if 0 < fraction < .01:
        return "<1"
    return str(min(99, math.floor(fraction * 100 + 1e-9)))


def text(value, size=14, color="#CACAD2", **style):
    return {"type": "text", "text": value, "fontSize": size, "color": color, **style}


def ring(fraction, label, size, color):
    return {"type": "ring", "width": size, "height": size, "value": fraction or 0, "text": label,
            "fontSize": 12 if size == 28 else 15, "color": color, "trackColor": "#292D38", "thickness": 3}


def button(label, action, width=100):
    return {"type": "button", "actionId": action, "text": label, "width": width, "height": 36,
            "fontSize": 14, "background": "#24272B", "color": "#E2E2E8", "cornerRadius": 12}


def make_views(snapshot, failure=None, page=0, now=None):
    now = time.time() if now is None else now
    windows = snapshot.windows if snapshot else ()
    lowest = min((window.fraction for window in windows if window.fraction is not None), default=None)
    stale = bool(snapshot and (failure or now - snapshot.fetched_at > STALE_SECONDS))
    color = MUTED if stale or lowest is None else AMBER if lowest <= .15 else ACCENT
    compact = {"type": "view", "id": "quota-ring", "slot": "idle.widget", "title": "Gemini 额度",
               "placement": {"defaultSide": "right", "allowedSides": ["left", "right"]}, "allowStandalone": True,
               "root": ring(lowest, percent(lowest), 28, color)}

    nodes = [text("Gemini 额度", 20, "#F2F2F4", weight="semibold")]
    page_count = max(1, math.ceil(len(windows) / WINDOWS_PER_PAGE))
    page %= page_count
    visible = windows[page * WINDOWS_PER_PAGE:(page + 1) * WINDOWS_PER_PAGE]
    for window in visible:
        label = percent(window.fraction)
        color = MUTED if stale or window.fraction is None else AMBER if window.fraction <= .15 else ACCENT
        details = "重置 " + timestamp(window.resets, "%m/%d %H:%M")
        if window.fraction is None and window.amount is not None:
            details = "剩余 " + window.amount + " · " + details
        nodes.append({"type": "row", "spacing": 10, "children": [
            ring(window.fraction, label if label == "—" else label + "%", 48, color),
            {"type": "column", "spacing": 2, "children": [
                text(window.title, 16, "#DFDFE5", weight="semibold"), text(details, 14, "#9696A2")]}]})
    if not windows:
        nodes.append(text("暂无 Gemini 套餐额度" if snapshot else "套餐额度未知", 16, "#A9A9B4"))

    credits = snapshot.credits if snapshot else None
    credit_label = "额外 AI 积分  " + (credits if credits is not None else "未知")
    nodes.append(text(credit_label, 16, "#DFDFE5"))
    status = failure or ("额度数据已过期" if stale else "积分暂不可用" if snapshot and snapshot.credits_failed else None)
    if status:
        nodes.append(text(status + (" · 已过期" if stale and failure else ""), 14, "#D3AF79"))
    elif not snapshot:
        nodes.append(text("正在读取 Antigravity CLI…", 14, "#9696A2"))
    if snapshot:
        updated = dt.datetime.fromtimestamp(snapshot.fetched_at, tz=dt.timezone.utc)
        nodes.append(text("更新 " + timestamp(updated, "%H:%M:%S"), 14, "#9696A2"))
    controls = [button("刷新额度", "refresh")]
    if page_count > 1:
        controls.append(button(f"额度 {page + 1}/{page_count} ›", "next", 110))
    nodes.append({"type": "row", "spacing": 8, "children": controls})
    height = (160 + 58 * len(visible)) if visible else 190
    height += sum(20 for window in visible if len(window.title) > 18)
    height += sum(20 for window in visible if window.fraction is None and window.amount is not None)
    if status:
        height += 36
    detail = {"type": "view", "id": "quota-page", "slot": "activity.tab", "title": "Gemini",
              "root": {"type": "column", "width": 340, "height": height, "spacing": 6,
                       "padding": 12, "children": nodes}}
    return compact, detail


def send(message):
    line = json.dumps(message, ensure_ascii=False, separators=(",", ":"), allow_nan=False) + "\n"
    if len(line.encode("utf-8")) > HOST_LIMIT:
        raise ValueError("LandBar message is too large")
    sys.stdout.write(line)
    sys.stdout.flush()


def controls(stop, refresh, next_page, ready, wake):
    try:
        while not stop.is_set():
            line = sys.stdin.buffer.readline(HOST_LIMIT + 1)
            if not line or len(line) > HOST_LIMIT:
                return
            try:
                message = json.loads(line)
            except (ValueError, UnicodeError, RecursionError):
                continue
            if not isinstance(message, dict):
                continue
            kind = message.get("type")
            if kind == "shutdown":
                return
            if kind == "hello":
                if message.get("protocolVersion") != 1:
                    return
                ready.set()
                wake.set()
            if kind == "viewAction" and message.get("viewId") == "quota-page":
                if message.get("actionId") == "refresh":
                    refresh.set()
                    wake.set()
                if message.get("actionId") == "next":
                    next_page.set()
                    wake.set()
    finally:
        stop.set()
        wake.set()


def main():
    sys.stdout.reconfigure(encoding="utf-8", newline="\n", line_buffering=True)
    stop, refresh, next_page, ready, wake = (threading.Event() for _ in range(5))
    threading.Thread(target=controls, args=(stop, refresh, next_page, ready, wake), daemon=True).start()
    while not ready.is_set() and not stop.is_set():
        wake.wait(.25)
        wake.clear()
    if stop.is_set():
        return
    provider = Antigravity()
    snapshot = failure = None
    page = 0
    last_started = -math.inf
    next_attempt = 0.0
    previous_views = None
    try:
        send({"type": "hello", "protocolVersion": 1})
        while not stop.is_set():
            if next_page.is_set():
                next_page.clear()
                page += 1
            views = make_views(snapshot, failure, page)
            if views != previous_views:
                for view in views:
                    send(view)
                previous_views = views
            now = time.monotonic()
            if now >= next_attempt or (refresh.is_set() and now >= last_started + MIN_REFRESH_SECONDS):
                refresh.clear()
                last_started = now
                pause_polling = False
                try:
                    snapshot = provider.fetch(stop)
                    failure = None
                    page %= max(1, math.ceil(len(snapshot.windows) / WINDOWS_PER_PAGE))
                except QueryError as error:
                    if error.clear:
                        snapshot, page = None, 0
                        pause_polling = True
                    failure = str(error)
                except (OSError, subprocess.SubprocessError):
                    failure = "Antigravity CLI 暂不可用"
                next_attempt = math.inf if pause_polling else time.monotonic() + POLL_SECONDS
                continue
            wake.wait(.25)
            wake.clear()
    except (Stopped, OSError):
        pass
    finally:
        stop.set()


if __name__ == "__main__":
    main()
