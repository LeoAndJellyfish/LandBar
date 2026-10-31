import ctypes
import importlib.util
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "samples/codex-balance/plugin.py"
spec = importlib.util.spec_from_file_location("codex_sample", SOURCE)
plugin = importlib.util.module_from_spec(spec)
spec.loader.exec_module(plugin)


def texts(node):
    return [node.get("text", "")] + [value for child in node.get("children", []) for value in texts(child)]


def alive(pid):
    if os.name == "nt":
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
        kernel.OpenProcess.restype = ctypes.c_void_p
        kernel.GetExitCodeProcess.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong)]
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        handle = kernel.OpenProcess(0x1000, False, pid)
        if not handle:
            return False
        try:
            code = ctypes.c_ulong()
            return bool(kernel.GetExitCodeProcess(handle, ctypes.byref(code))) and code.value == 259
        finally:
            kernel.CloseHandle(handle)
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False


class Provider:
    def __init__(self, mode):
        self.temp = tempfile.TemporaryDirectory(prefix="landbar-codex-synthetic-")
        self.folder = Path(self.temp.name)
        self.process = subprocess.Popen([sys.executable, "-u", str(Path(__file__).with_name("provider_fixture.py")), mode, str(self.folder)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=plugin.CREATE_HIDDEN)
        self.messages = queue.Queue()
        self.output = []
        self.errors = []
        self.reader = threading.Thread(target=self.read, daemon=True)
        self.reader.start()
        self.error_reader = threading.Thread(target=lambda: self.errors.append(self.process.stderr.read()), daemon=True)
        self.error_reader.start()

    def read(self):
        for raw in self.process.stdout:
            self.output.append(raw)
            self.messages.put(json.loads(raw))

    def send(self, message):
        self.process.stdin.write((json.dumps(message) + "\n").encode("utf-8"))
        self.process.stdin.flush()

    def until(self, predicate, timeout=8):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            try:
                message = self.messages.get(timeout=.1)
            except queue.Empty:
                if self.process.poll() is not None:
                    raise AssertionError("Synthetic provider exited before its expected view")
                continue
            if predicate(message):
                return message
        raise AssertionError("Synthetic provider timed out")

    def hello(self):
        self.send({"type": "hello", "protocolVersion": 1, "features": ["views", "tabs", "widgetPlacement"]})
        return self.until(lambda item: item.get("type") == "hello")

    def close(self, eof=False):
        if self.process.poll() is None:
            if not eof:
                self.send({"type": "shutdown"})
            self.process.stdin.close()
            self.process.wait(timeout=8)
        self.reader.join(timeout=1)
        self.error_reader.join(timeout=1)
        for name in ("stdout", "stderr"):
            getattr(self.process, name).close()
        pids = [int(value) for value in (self.folder / "pids.txt").read_text().splitlines()] if (self.folder / "pids.txt").exists() else []
        deadline = time.monotonic() + 2
        while any(alive(pid) for pid in pids) and time.monotonic() < deadline:
            time.sleep(.05)
        assert not any(alive(pid) for pid in pids), "An owned synthetic app-server survived shutdown"
        assert self.process.returncode == 0, "The provider returned a failure exit code"
        assert not any(self.errors), "The provider wrote unexpected diagnostic payloads"

    def cleanup(self):
        try:
            self.close()
        finally:
            self.temp.cleanup()


class SampleTests(unittest.TestCase):
    def provider(self, mode):
        value = Provider(mode)
        self.addCleanup(value.cleanup)
        value.hello()
        return value

    def test_percentage_boundaries(self):
        for value in (None, True, "50", float("nan"), float("inf"), int("9" * 500)):
            self.assertIsNone(plugin.remaining({"usedPercent": value}))
        self.assertEqual(0, plugin.remaining({"usedPercent": 100}))
        self.assertEqual(100, plugin.remaining({"usedPercent": -1}))
        self.assertEqual(0, plugin.remaining({"usedPercent": 200}))
        self.assertEqual("未知", plugin.timestamp(float("inf")))

    def test_zero_unlimited_unknown_and_stale(self):
        base = {"plan": "plus", "fetchedAt": 1000, "bucket": {"primary": {"usedPercent": 100}, "credits": {"balance": "0"}}, "resetCount": 0}
        compact, page = plugin.make_views(base, None, now=1001)
        self.assertEqual("0", compact["root"]["text"])
        self.assertIn("额外积分  0", texts(page["root"]))
        self.assertIn("可用重置次数  0", texts(page["root"]))
        stale, _ = plugin.make_views(base, None, now=1151)
        self.assertEqual("#85858F", stale["root"]["color"])
        self.assertEqual("0", stale["root"]["text"])
        base["bucket"]["credits"] = {"unlimited": True, "balance": "0"}
        self.assertIn("额外积分  不限额", texts(plugin.make_views(base, None, now=1001)[1]["root"]))
        unknown = plugin.make_views(None, None, now=1001)
        self.assertEqual("—", unknown[0]["root"]["text"])
        self.assertEqual("#85858F", unknown[0]["root"]["color"])
        self.assertIn("额外积分  未知", texts(unknown[1]["root"]))

    def test_view_contract_and_bounded_values(self):
        snapshot = {"plan": "x" * 10000, "bucket": {"credits": {"balance": "x" * 10000}}, "fetchedAt": 1000}
        compact, page = plugin.make_views(snapshot, "合成错误", now=1001)
        self.assertEqual({"defaultSide": "right", "allowedSides": ["left", "right"]}, compact["placement"])
        self.assertEqual("idle.widget", compact["slot"])
        self.assertEqual("Codex", page["title"])
        for view in (compact, page):
            self.assertLess(len(json.dumps(view, ensure_ascii=False).encode("utf-8")), 16 * 1024)
            self.assertTrue(all(len(value) <= 512 for value in texts(view["root"])))
        for value in (True, "NaN", "Infinity", "-1", {}, "x" * 10000):
            self.assertEqual("未知", plugin.credit_balance(value))
        self.assertEqual("0.00", plugin.credit_balance("0.00"))
        self.assertTrue(plugin.credit_balance("1" * 30).startswith("≈"))

    def test_sparse_updates_keep_known_metadata(self):
        previous = {"primary": {"usedPercent": 10, "windowDurationMins": 300}, "credits": {"balance": "0"}, "planType": "plus"}
        merged = plugin.clean_bucket({"primary": {"usedPercent": 25, "resetsAt": None}, "credits": None, "planType": None, "email": "ignored"}, previous)
        self.assertEqual(25, merged["primary"]["usedPercent"])
        self.assertEqual(300, merged["primary"]["windowDurationMins"])
        self.assertEqual("0", merged["credits"]["balance"])
        self.assertEqual("plus", merged["planType"])
        self.assertNotIn("email", merged)
        self.assertEqual({}, plugin.clean_bucket({"primary": None, "credits": None}))
        self.assertIsNone(plugin.bucket_from({"rateLimits": {"limitId": "other"}}))
        self.assertEqual({"limitId": "codex"}, plugin.bucket_from({"rateLimitsByLimitId": {"codex": {"limitId": "codex"}}}))

    def test_discovery_explicit_native_path(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "codex.exe"
            path.write_bytes(b"synthetic executable placeholder")
            with patch.dict(os.environ, {"LANDBAR_CODEX_PATH": str(path)}):
                self.assertEqual(path, plugin.find_codex())
            with patch.dict(os.environ, {"LANDBAR_CODEX_PATH": str(path.with_suffix(".cmd"))}):
                self.assertRaises(FileNotFoundError, plugin.find_codex)

    def test_real_process_handshake_push_refresh_and_cleanup(self):
        provider = self.provider("success")
        provider.until(lambda item: item.get("id") == "quota-ring" and item["root"]["text"] == "75")
        provider.until(lambda item: item.get("id") == "quota-ring" and item["root"]["text"] == "60")
        page = provider.until(lambda item: item.get("id") == "quota-page" and "额外积分  0" in texts(item["root"]))
        self.assertIn("plus", texts(page["root"]))
        for _ in range(5):
            provider.send({"type": "viewAction", "viewId": "quota-page", "actionId": "refresh"})
        provider.until(lambda item: item.get("id") == "quota-ring" and item["root"]["text"] == "0", timeout=8)
        provider.close()
        requests = [json.loads(line) for line in (provider.folder / "requests.jsonl").read_text().splitlines()]
        self.assertTrue(all(item["method"] in {"initialize", "initialized", "account/read", "account/rateLimits/read"} for item in requests))
        self.assertEqual({"refreshToken": False}, next(item["params"] for item in requests if item["method"] == "account/read"))
        self.assertEqual(2, sum(item["method"] == "account/rateLimits/read" for item in requests))
        output = b"".join(provider.output).decode("utf-8")
        self.assertNotIn("synthetic@example.invalid", output)
        self.assertNotIn("fixture-account", output)

    def test_error_payload_is_private_and_views_stay_unknown(self):
        provider = self.provider("error")
        provider.until(lambda item: item.get("id") == "quota-page" and any("读取失败" in value for value in texts(item["root"])))
        provider.close()
        self.assertNotIn(b"PRIVATE_FIXTURE_ERROR_DO_NOT_DISPLAY", b"".join(provider.output))

    def test_unsupported_account_does_not_read_quota(self):
        provider = self.provider("apikey")
        provider.until(lambda item: item.get("id") == "quota-page" and any("ChatGPT 登录" in value for value in texts(item["root"])))
        provider.close()
        self.assertNotIn("account/rateLimits/read", (provider.folder / "requests.jsonl").read_text())

    def test_account_change_clears_previous_account(self):
        provider = self.provider("swap")
        provider.until(lambda item: item.get("id") == "quota-ring" and item["root"]["text"] == "75")
        unknown = provider.until(lambda item: item.get("id") == "quota-ring" and item["root"]["text"] == "—")
        self.assertEqual("#85858F", unknown["root"]["color"])
        provider.until(lambda item: item.get("id") == "quota-page" and any("ChatGPT 登录" in value for value in texts(item["root"])))
        provider.close()
        self.assertEqual("1", (provider.folder / "starts.txt").read_text())
        requests = [json.loads(line) for line in (provider.folder / "requests.jsonl").read_text().splitlines()]
        self.assertEqual(2, sum(item["method"] == "account/read" for item in requests))

    def test_bootstrap_account_notification_does_not_restart_initialization(self):
        provider = self.provider("bootstrap")
        provider.until(lambda item: item.get("id") == "quota-ring" and item["root"]["text"] == "75")
        provider.close()
        self.assertEqual("1", (provider.folder / "starts.txt").read_text())

    def test_unavailable_codex_bucket_remains_unknown(self):
        provider = self.provider("unknown")
        provider.until(lambda item: item.get("id") == "quota-page" and any("尚未提供" in value for value in texts(item["root"])))

    def test_malformed_or_oversize_upstream_is_bounded(self):
        for mode in ("malformed", "oversize"):
            with self.subTest(mode=mode):
                provider = self.provider(mode)
                provider.until(lambda item: item.get("id") == "quota-page" and any("读取失败" in value for value in texts(item["root"])))
                provider.close()

    def test_host_eof_shuts_down_owned_server(self):
        provider = self.provider("success")
        provider.until(lambda item: item.get("id") == "quota-ring" and item["root"]["text"] == "75")
        provider.close(eof=True)

    def test_unresponsive_owned_server_is_stopped_within_bound(self):
        server = plugin.AppServer([sys.executable, "-u", "-c", "import time; time.sleep(30)"])
        started = time.monotonic()
        server.close()
        self.assertIsNotNone(server.process.poll())
        self.assertLess(time.monotonic() - started, 6)
        self.assertTrue(all(not reader.is_alive() for reader in server.readers))


if __name__ == "__main__":
    unittest.main()
