"""Codex account views via the local app-server. Account snapshots stay in memory."""
from __future__ import annotations

import datetime as dt
from decimal import Decimal, InvalidOperation
import json
import math
import os
from pathlib import Path
import queue
import shutil
import subprocess
import sys
import threading
import time
import unicodedata

HOST_LINE_LIMIT = 16 * 1024
SERVER_LINE_LIMIT = 1024 * 1024
CREATE_HIDDEN = getattr(subprocess, "CREATE_NO_WINDOW", 0)


def finite(value):
    try:
        return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)
    except OverflowError:
        return False


def display(value, fallback="未知", limit=64):
    if not isinstance(value, (str, int, float)) or isinstance(value, bool):
        return fallback
    if isinstance(value, float) and not math.isfinite(value):
        return fallback
    result = "".join(character for character in str(value) if character.isprintable()).strip()
    return result[:limit] or fallback


def timestamp(value, include_date=True):
    if not finite(value):
        return "未知"
    try:
        return dt.datetime.fromtimestamp(value).strftime("%m/%d %H:%M" if include_date else "%H:%M:%S")
    except (ValueError, OSError, OverflowError):
        return "未知"


def remaining(window):
    value = window.get("usedPercent") if isinstance(window, dict) else None
    return max(0.0, min(100.0, 100.0 - value)) if finite(value) else None


def credit_balance(value):
    if not isinstance(value, (str, int, float)) or isinstance(value, bool):
        return "未知"
    literal = str(value).strip()
    if len(literal) > 128:
        return "未知"
    try:
        number = Decimal(literal)
        if not number.is_finite() or number < 0:
            return "未知"
        return literal if len(literal) <= 24 else "≈" + format(number, ".6g")
    except InvalidOperation:
        return "未知"


def estimated_width(value, font_size):
    # Conservative Noto Sans SC glyph widths, used only to reserve extra wrapped-line space.
    return sum(font_size if unicodedata.east_asian_width(character) in "WFA" else
               font_size * (.3 if character.isspace() else .6) for character in value)


def window_name(window, fallback):
    minutes = window.get("windowDurationMins")
    if finite(minutes) and minutes > 0:
        if minutes % 1440 == 0:
            return f"{minutes / 1440:g} 天窗口"
        if minutes % 60 == 0:
            return f"{minutes / 60:g} 小时窗口"
        return f"{minutes:g} 分钟窗口"
    return fallback


def text(content, size=14, color="#CACAD2", **extra):
    return {"type": "text", "text": str(content), "fontSize": size, "color": color, **extra}


def ring(value, label, size, color):
    return {"type": "ring", "width": size, "height": size, "value": value,
            "text": label, "fontSize": 12 if size == 28 else 16,
            "color": color, "trackColor": "#292D30", "thickness": 3}


def make_views(snapshot, failure, now=None):
    now = time.time() if now is None else now
    bucket = snapshot.get("bucket", {}) if snapshot else {}
    known = [(key, window, remaining(window)) for key in ("primary", "secondary")
             if isinstance(window := bucket.get(key), dict) and remaining(window) is not None]
    lowest = min((value for _, _, value in known), default=None)
    fetched = snapshot.get("fetchedAt") if snapshot else None
    stale = bool(failure) or bool(finite(fetched) and now - fetched > 150)
    color = "#85858F" if stale or lowest is None else "#E2B87C" if lowest <= 15 else "#BEDDC9"
    compact = {"type": "view", "id": "quota-ring", "slot": "idle.widget",
               "placement": {"defaultSide": "right", "allowedSides": ["left", "right"]},
               "root": ring(0 if lowest is None else lowest / 100,
                            "—" if lowest is None else str(int(round(lowest))), 28, color)}
    plan = display(bucket.get("planType") or (snapshot.get("plan") if snapshot else None), "套餐未知", 24)
    nodes = [{"type": "row", "spacing": 12, "children": [
        text("Codex 额度", 20, "#F2F2F4", weight="semibold"), text(plan, 14, "#92929E")]}]
    for key, window, value in known:
        caption = window_name(window, "主窗口" if key == "primary" else "次窗口")
        nodes.append({"type": "row", "spacing": 12, "children": [
            ring(value / 100, f"{value:g}%", 48, color),
            {"type": "column", "spacing": 3, "children": [
                text(f"{caption} · 剩余 {value:g}%", 16, "#DFDFE5", weight="semibold"),
                text("重置 " + timestamp(window.get("resetsAt")), 14, "#9696A2")]}]})
    if not known:
        nodes.append(text("套餐额度：未知", 16, "#A9A9B4"))
    credits = bucket.get("credits")
    balance = "不限额" if isinstance(credits, dict) and credits.get("unlimited") is True else \
        credit_balance(credits.get("balance")) if isinstance(credits, dict) else "未知"
    resets = snapshot.get("resetCount") if snapshot else None
    nodes.append({"type": "row", "spacing": 8, "children": [
        text("额外积分  " + balance, 16, "#DFDFE5"),
        text("可用重置次数  " + display(resets), 14, "#A8A8B4")]})
    status = bool(failure) or stale or not snapshot
    if failure:
        nodes.append(text(failure + (" · 已过期" if snapshot else ""), 14, "#D3AF79"))
    elif stale:
        nodes.append(text("数据已过期，请刷新", 14, "#D3AF79"))
    elif not snapshot:
        nodes.append(text("正在读取本机 Codex 账号…", 14, "#9696A2"))
    nodes.append(text("更新 " + (timestamp(fetched, False) if finite(fetched) else "尚未成功"), 14, "#9696A2"))
    nodes.append({"type": "button", "actionId": "refresh", "text": "刷新额度",
                  "width": 100, "height": 36, "fontSize": 14, "background": "#24272B",
                  "color": "#E2E2E8", "cornerRadius": 12})
    # Noto Sans SC line boxes, 48-DIP rows, six-DIP gaps and the root padding.
    # The host scrolls the page when the complete content exceeds its expanded viewport.
    content_height = (210 + max(0, len(known) - 1) * 54) if known else 185
    content_height += 26 if status else 0
    content_height += 40 if estimated_width(plan, 14) > 140 else 0
    content_height += 40 if max(estimated_width("额外积分  " + balance, 16),
                                estimated_width("可用重置次数  " + display(resets), 14)) > 140 else 0
    page = {"type": "view", "id": "quota-page", "slot": "activity.tab", "title": "Codex",
            "root": {"type": "column", "width": 340,
                     "height": min(420, content_height),
                     "spacing": 6, "padding": 12, "children": nodes}}
    return compact, page


def find_codex():
    """Resolve a native executable; never execute a PATH shell shim as a command string."""
    override = os.environ.get("LANDBAR_CODEX_PATH")
    if override:
        path = Path(override).expanduser()
        if not path.is_absolute() or not path.is_file() or path.suffix.lower() != ".exe":
            raise FileNotFoundError("Invalid Codex executable override")
        return path
    candidates = []
    local = os.environ.get("LOCALAPPDATA")
    if local:
        candidates.append(Path(local) / "Programs/OpenAI/Codex/bin/codex.exe")
    for name in ("codex.exe", "codex"):
        found = shutil.which(name)
        if not found:
            continue
        shim = Path(found)
        if shim.suffix.lower() == ".exe":
            candidates.append(shim)
        elif shim.suffix.lower() in (".cmd", ".bat", ".ps1", ""):
            # npm keeps the native platform package beside the shim. Search only known roots.
            package = shim.parent / "node_modules/@openai/codex"
            architecture = "aarch64" if os.environ.get("PROCESSOR_ARCHITECTURE", "").upper() == "ARM64" else "x86_64"
            target = architecture + "-pc-windows-msvc/codex/codex.exe"
            platform = "codex-win32-arm64" if architecture == "aarch64" else "codex-win32-x64"
            candidates.extend((package / "vendor" / target,
                               package / "node_modules/@openai" / platform / "vendor" / target))
    for path in candidates:
        if path.is_file():
            return path
    raise FileNotFoundError("Codex native executable unavailable")


class AppServer:
    def __init__(self, command=None):
        self.process = subprocess.Popen(command or [str(find_codex()), "app-server", "--stdio"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=CREATE_HIDDEN)
        self.messages = queue.Queue(maxsize=64)
        self.next_id = 0
        self.stopping = threading.Event()
        self.readers = [threading.Thread(target=self.read, daemon=True),
                        threading.Thread(target=self.drain_errors, daemon=True)]
        for reader in self.readers:
            reader.start()

    def read(self):
        try:
            while not self.stopping.is_set():
                raw = self.process.stdout.readline(SERVER_LINE_LIMIT + 1)
                if not raw:
                    break
                if len(raw) > SERVER_LINE_LIMIT or not raw.endswith(b"\n"):
                    break
                message = json.loads(raw.decode("utf-8"))
                if not isinstance(message, dict):
                    break
                try:
                    self.messages.put(message, timeout=.25)
                except queue.Full:
                    pass
        except (OSError, ValueError, UnicodeError):
            pass
        finally:
            try:
                self.messages.put_nowait({"_closed": True})
            except queue.Full:
                pass

    def drain_errors(self):
        try:
            while self.process.stderr.read(4096):
                pass
        except (OSError, ValueError):
            pass

    def write(self, message):
        self.process.stdin.write((json.dumps(message) + "\n").encode("utf-8"))
        self.process.stdin.flush()

    def request(self, method, params=None):
        self.next_id += 1
        message = {"id": self.next_id, "method": method}
        if params is not None:
            message["params"] = params
        self.write(message)
        return self.next_id

    def receive(self, timeout=.25):
        try:
            message = self.messages.get(timeout=timeout)
        except queue.Empty:
            if self.process.poll() is not None:
                raise ConnectionError()
            return None
        if message.get("_closed"):
            raise ConnectionError()
        return message

    def wait(self, request_id, stop):
        deadline = time.monotonic() + 25
        while not stop.is_set() and time.monotonic() < deadline:
            message = self.receive()
            if message is not None and message.get("id") == request_id:
                if "error" in message or not isinstance(message.get("result"), dict):
                    raise RuntimeError("Codex request failed")
                return message["result"]
        raise TimeoutError()

    def close(self):
        self.stopping.set()
        try:
            self.process.stdin.close()
        except OSError:
            pass
        try:
            self.process.wait(timeout=1)
        except subprocess.TimeoutExpired:
            if self.process.poll() is None:
                if os.name == "nt":
                    try:
                        subprocess.run(["taskkill.exe", "/PID", str(self.process.pid), "/T", "/F"],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                            creationflags=CREATE_HIDDEN, timeout=2)
                    except (OSError, subprocess.TimeoutExpired):
                        pass
                    if self.process.poll() is None:
                        self.process.kill()
                else:
                    self.process.kill()
            self.process.wait(timeout=2)
        finally:
            for reader in self.readers:
                reader.join(timeout=.25)
            if self.process.poll() is not None:
                self.process.stdout.close()
                self.process.stderr.close()


def bucket_from(payload):
    if not isinstance(payload, dict):
        return None
    by_id = payload.get("rateLimitsByLimitId")
    if isinstance(by_id, dict) and isinstance(by_id.get("codex"), dict):
        return by_id["codex"]
    value = payload.get("rateLimits")
    if isinstance(value, dict) and value.get("limitId") in (None, "codex"):
        return value
    return None


def clean_bucket(bucket, previous=None):
    """Retain display fields only. Sparse push updates preserve earlier non-null values."""
    result = dict(previous or {})
    for field in ("primary", "secondary", "credits"):
        item = bucket.get(field)
        if not isinstance(item, dict):
            continue
        allowed = ("balance", "unlimited", "hasCredits") if field == "credits" else \
            ("usedPercent", "windowDurationMins", "resetsAt")
        result[field] = {**result.get(field, {}), **{key: item[key] for key in allowed if item.get(key) is not None}}
    if bucket.get("planType") is not None:
        result["planType"] = display(bucket["planType"], "套餐未知", 24)
    return result


class Plugin:
    def __init__(self, server_factory=AppServer):
        self.server_factory = server_factory
        self.events = queue.Queue(maxsize=64)
        self.stop = threading.Event()
        self.refresh = threading.Event()

    def event(self, kind, value=None):
        try:
            self.events.put((kind, value), timeout=.25)
        except queue.Full:
            if kind == "control":
                self.stop.set()

    def controls(self):
        try:
            while not self.stop.is_set():
                line = sys.stdin.buffer.readline(HOST_LINE_LIMIT + 1)
                if not line or len(line) > HOST_LINE_LIMIT or not line.endswith(b"\n"):
                    break
                value = json.loads(line.decode("utf-8"))
                if not isinstance(value, dict):
                    break
                if value.get("type") == "shutdown":
                    self.stop.set()
                    break
                self.event("control", value)
        except (ValueError, OSError, UnicodeError):
            pass
        finally:
            self.stop.set()

    def worker(self):
        retry = 5
        while not self.stop.is_set():
            server = None
            try:
                server = self.server_factory()
                server.wait(server.request("initialize", {"clientInfo": {
                    "name": "landbar_codex_balance", "title": "LandBar Codex Balance", "version": "1.0.0"}}), self.stop)
                server.write({"method": "initialized"})
                snapshot = self.read_account(server)
                if snapshot is None:
                    self.stop.wait(60)
                    continue
                pending = server.request("account/rateLimits/read")
                deadline = time.monotonic() + 25
                next_poll = time.monotonic() + 60
                last_request = time.monotonic()
                self.refresh.clear()
                while not self.stop.is_set():
                    now = time.monotonic()
                    if pending is not None and now > deadline:
                        raise TimeoutError()
                    if pending is None and (now >= next_poll or (self.refresh.is_set() and now - last_request >= 5)):
                        pending = server.request("account/rateLimits/read")
                        deadline = now + 25
                        last_request = now
                        next_poll = now + 60
                        self.refresh.clear()
                    message = server.receive()
                    if message is None:
                        continue
                    if message.get("method") == "account/updated":
                        # Clear old-account views first, then read the current account on this connection.
                        # Reusing the initialized server also tolerates bootstrap account notifications.
                        self.event("reset")
                        snapshot = self.read_account(server)
                        if snapshot is None:
                            self.stop.wait(60)
                            break
                        pending = server.request("account/rateLimits/read")
                        last_request = time.monotonic()
                        deadline, next_poll = last_request + 25, last_request + 60
                        self.refresh.clear()
                        continue
                    notification = message.get("method") == "account/rateLimits/updated"
                    full_read = pending is not None and message.get("id") == pending
                    if not (notification or full_read):
                        continue
                    if full_read:
                        pending = None
                        if "error" in message:
                            raise RuntimeError("Codex account read failed")
                    payload = message.get("params") if notification else message.get("result")
                    bucket = bucket_from(payload)
                    if bucket is None:
                        if full_read:
                            self.event("error", "服务尚未提供 Codex 额度")
                        continue
                    snapshot = {**snapshot, "bucket": clean_bucket(bucket, snapshot["bucket"] if notification else None),
                                "fetchedAt": time.time()}
                    if "rateLimitResetCredits" in payload:
                        reset_info = payload["rateLimitResetCredits"]
                        count = reset_info.get("availableCount") if isinstance(reset_info, dict) else None
                        snapshot["resetCount"] = count if isinstance(count, int) and not isinstance(count, bool) and 0 <= count <= 2**63 - 1 else None
                    retry = 5
                    self.event("snapshot", snapshot.copy())
            except FileNotFoundError:
                if not self.stop.is_set():
                    self.event("error", "未找到 Codex，可配置 LANDBAR_CODEX_PATH 后重新启用")
            except (OSError, ValueError, TimeoutError, RuntimeError, ConnectionError):
                if not self.stop.is_set():
                    self.event("error", "额度读取失败，正在重试")
            finally:
                if server is not None:
                    try:
                        server.close()
                    except (OSError, subprocess.TimeoutExpired):
                        pass
            self.stop.wait(retry)
            retry = min(60, retry * 2)

    def read_account(self, server):
        account = server.wait(server.request("account/read", {"refreshToken": False}), self.stop).get("account") or {}
        if not isinstance(account, dict) or account.get("type") != "chatgpt":
            self.event("error", "请先在 Codex CLI 中使用 ChatGPT 登录")
            return None
        # Keep only display fields; account identifiers and email addresses are discarded.
        return {"plan": display(account.get("planType")), "bucket": {}, "resetCount": None}

    @staticmethod
    def send(message):
        line = json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n"
        if len(line.encode("utf-8")) > HOST_LINE_LIMIT:
            raise ValueError("Host message limit exceeded")
        sys.stdout.write(line)
        sys.stdout.flush()

    def run(self):
        threading.Thread(target=self.controls, daemon=True).start()
        runner = None
        snapshot = None
        failure = None
        stale_drawn = False
        try:
            while not self.stop.is_set():
                try:
                    kind, value = self.events.get(timeout=.25)
                except queue.Empty:
                    if snapshot and not stale_drawn and time.time() - snapshot["fetchedAt"] > 150:
                        for view in make_views(snapshot, failure):
                            self.send(view)
                        stale_drawn = True
                    continue
                if kind == "control":
                    if value.get("type") == "hello" and runner is None:
                        if value.get("protocolVersion") != 1:
                            return
                        self.send({"type": "hello", "protocolVersion": 1})
                        for view in make_views(None, None):
                            self.send(view)
                        runner = threading.Thread(target=self.worker, daemon=True)
                        runner.start()
                    elif runner is not None and value.get("type") == "viewAction" and \
                            value.get("viewId") == "quota-page" and value.get("actionId") == "refresh":
                        self.refresh.set()
                elif kind == "snapshot":
                    snapshot, failure, stale_drawn = value, None, False
                    for view in make_views(snapshot, None):
                        self.send(view)
                elif kind == "error":
                    failure = value
                    for view in make_views(snapshot, failure):
                        self.send(view)
                elif kind == "reset":
                    snapshot, failure, stale_drawn = None, None, False
                    for view in make_views(None, None):
                        self.send(view)
        except (BrokenPipeError, OSError):
            pass
        finally:
            self.stop.set()
            if runner is not None:
                runner.join(timeout=5)


def main():
    sys.stdout.reconfigure(encoding="utf-8", newline="\n", line_buffering=True)
    Plugin().run()


if __name__ == "__main__":
    main()
