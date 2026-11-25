import datetime as dt
import importlib.util
import json
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
import unittest
from unittest.mock import patch


SOURCE = Path(__file__).resolve().parents[2] / "samples/gemini-balance/plugin.py"
SPEC = importlib.util.spec_from_file_location("gemini_sample", SOURCE)
PLUGIN = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PLUGIN)


def envelope(name, data, **changes):
    value = {"status": "SUCCESS", "num_turns": 0, "command": {"name": name, "data": data}}
    value.update(changes)
    return json.dumps(value).encode()


USAGE = {"groups": [
    {"name": "Claude and GPT models", "buckets": [{"id": "other-weekly", "window": "weekly", "remaining_fraction": 0}]},
    {"name": "Gemini Models", "buckets": [
        {"id": "gemini-weekly", "window": "weekly", "remaining_fraction": .42, "reset_time": "2030-01-07T00:00:00Z"},
        {"id": "gemini-5h", "window": "5h", "remaining_fraction": .76, "reset_time": "2030-01-01T00:00:00Z"},
        {"id": "gemini-disabled", "window": "monthly", "remaining_fraction": 0, "disabled": True},
    ]},
]}


def texts(node):
    values = [node.get("text", "")]
    for child in node.get("children", []):
        values.extend(texts(child))
    return values


class GeminiPluginChecks(unittest.TestCase):
    def test_gemini_windows_only_with_correct_remaining_fraction_and_reset(self):
        windows = PLUGIN.gemini_windows(USAGE)
        self.assertEqual(["5 小时窗口", "每周窗口"], [window.title for window in windows])
        self.assertEqual([.76, .42], [window.fraction for window in windows])
        self.assertEqual(dt.datetime(2030, 1, 7, tzinfo=dt.timezone.utc), windows[1].resets)

    def test_missing_zero_amount_and_disabled_are_distinct(self):
        rows = [{"id": "gemini-5h", "window": "5h", "remaining_fraction": 0, "reset_time": "1970-01-01T00:00:00Z"},
                {"id": "gemini-daily", "window": "daily", "remaining_amount": "120"},
                {"id": "gemini-weekly", "window": "weekly", "remaining_fraction": True},
                {"id": "gemini-unknown", "window": ["daily"], "remaining_fraction": 1.1}]
        window, amount, unknown, invalid = PLUGIN.gemini_windows({"groups": [{"buckets": rows}]})
        self.assertEqual(0, window.fraction)
        self.assertIsNone(window.resets)
        self.assertIsNone(amount.fraction)
        self.assertEqual("120", amount.amount)
        self.assertIsNone(unknown.fraction)
        self.assertIsNone(invalid.fraction)

    def test_credits_preserve_valid_zero_and_decimal_and_discard_other_fields(self):
        self.assertEqual("0", PLUGIN.credit_balance({"remaining_credits": 0}))
        self.assertEqual("42.125", PLUGIN.credit_balance({"remaining_credits": "42.125", "email": "fixture@example.invalid"}))
        self.assertEqual("不限额", PLUGIN.credit_balance({"unlimited": True}))
        for invalid in (None, False, -1, float("inf"), "https://example.invalid", "no balance"):
            with self.subTest(invalid=invalid):
                self.assertIsNone(PLUGIN.credit_balance({"remaining_credits": invalid}))

    def test_zero_turn_command_envelope_required(self):
        self.assertEqual(USAGE, PLUGIN.command_data(envelope("usage", USAGE), "usage"))
        for wrong in (envelope("usage", USAGE, num_turns=1), envelope("usage", USAGE, num_turns=False),
                      envelope("usage", USAGE, status="ERROR"), envelope("credits", USAGE), b"{}", b"invalid"):
            with self.subTest(wrong=wrong):
                with self.assertRaises(PLUGIN.QueryError):
                    PLUGIN.command_data(wrong, "usage")

    def test_unsupported_or_signed_out_session_clears_old_account_data(self):
        for message in ("You are not signed in: fixture@example.invalid", "Gemini API key mode does not support usage"):
            with self.assertRaises(PLUGIN.QueryError) as raised:
                PLUGIN.command_data(b"", "usage", 2, message.encode())
            self.assertTrue(raised.exception.clear)
            self.assertNotIn("fixture@example.invalid", str(raised.exception))
        with self.assertRaises(PLUGIN.QueryError) as raised:
            PLUGIN.command_data(b"", "usage", 2, b"private transport error")
        self.assertFalse(raised.exception.clear)
        self.assertNotIn("private transport", str(raised.exception))

    def test_verifies_cli_version_before_either_read_only_command(self):
        path = Path(sys.executable)
        calls = []

        def runner(command, stop, timeout):
            calls.append(command[1:])
            if command[1:] == ["--version"]:
                return 0, b"agy version 1.1.10\n", b""
            self.fail("An old CLI must never receive a print invocation.")

        provider = PLUGIN.Antigravity(runner, lambda: path)
        with self.assertRaises(PLUGIN.QueryError):
            provider.fetch(threading.Event())
        self.assertEqual([["--version"]], calls)

    def test_credit_failure_does_not_hide_successful_account_quota(self):
        calls = []

        def runner(command, stop, timeout):
            calls.append(command[1:])
            if command[1:] == ["--version"]:
                return 0, b"agy 1.5.17\n", b""
            self.assertEqual("--print", command[1])
            self.assertIn("--disable-slash-commands=false", command)
            if command[2] == "/usage":
                return 0, envelope("usage", USAGE), b""
            self.assertEqual("/credits", command[2])
            return 2, b"", b"private transient error"

        provider = PLUGIN.Antigravity(runner, lambda: Path(sys.executable))
        snapshot = provider.fetch(threading.Event())
        provider.fetch(threading.Event())
        self.assertTrue(snapshot.credits_failed)
        self.assertIsNone(snapshot.credits)
        self.assertEqual(2, len(snapshot.windows))
        self.assertEqual(1, calls.count(["--version"]))

    def test_views_display_lowest_ratio_staleness_and_separate_credits(self):
        snapshot = PLUGIN.Snapshot(PLUGIN.gemini_windows(USAGE), "0", 1000)
        ring, page = PLUGIN.make_views(snapshot, now=1020)
        self.assertEqual(("42", .42, 28), (ring["root"]["text"], ring["root"]["value"], ring["root"]["width"]))
        self.assertEqual(["left", "right"], ring["placement"]["allowedSides"])
        self.assertTrue(ring["allowStandalone"])
        self.assertEqual("Gemini", page["title"])
        page_text = texts(page["root"])
        self.assertIn("额外 AI 积分  0", page_text)
        self.assertIn("76%", page_text)
        self.assertIn("42%", page_text)
        self.assertNotIn("0%", page_text)
        ring, page = PLUGIN.make_views(snapshot, now=1200)
        self.assertEqual(PLUGIN.MUTED, ring["root"]["color"])
        self.assertIn("额度数据已过期", texts(page["root"]))
        unknown, _ = PLUGIN.make_views(None, "请安装 Antigravity CLI")
        self.assertEqual("—", unknown["root"]["text"])

    def test_only_real_zero_and_full_get_zero_or_hundred_labels(self):
        for fraction, expected in ((None, "—"), (0, "0"), (.001, "<1"), (.58, "58"), (.999, "99"), (1, "100")):
            self.assertEqual(expected, PLUGIN.percent(fraction))

    def test_bounded_cli_timeout_and_output_limit(self):
        stop = threading.Event()
        start = time.monotonic()
        with self.assertRaisesRegex(PLUGIN.QueryError, "超时"):
            PLUGIN.run_cli([sys.executable, "-c", "import time; time.sleep(60)"], stop, .25)
        self.assertLess(time.monotonic() - start, 5)
        with self.assertRaisesRegex(PLUGIN.QueryError, "响应过长"):
            PLUGIN.run_cli([sys.executable, "-c", "print('X' * 200000)"], stop, 3)

    def test_shutdown_reclaims_provider_and_owned_query_promptly(self):
        fixture = """
import importlib.util, sys, time
spec = importlib.util.spec_from_file_location('sample', sys.argv[1])
sample = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sample)
class HaltingCLI:
    def fetch(self, stop):
        sample.run_cli([sys.executable, '-c', 'import time; time.sleep(60)'], stop, 45)
sample.Antigravity = HaltingCLI
sample.main()
"""
        with subprocess.Popen([sys.executable, "-c", fixture, str(SOURCE)], stdin=subprocess.PIPE,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE) as process:
            lines = queue.Queue()
            reader = threading.Thread(target=lambda: [lines.put(line) for line in process.stdout], daemon=True)
            reader.start()
            try:
                process.stdin.write(b'{"type":"hello","protocolVersion":1}\n')
                process.stdin.flush()
                initial = [json.loads(lines.get(timeout=5)) for _ in range(3)]
                self.assertEqual(["hello", "view", "view"], [message["type"] for message in initial])
                self.assertEqual("—", initial[1]["root"]["text"])
                process.stdin.write(b'{"type":"shutdown"}\n')
                process.stdin.flush()
                self.assertEqual(0, process.wait(timeout=5))
            finally:
                if process.poll() is None:
                    process.kill()
                reader.join(timeout=5)

    def test_auth_failure_pauses_polling_until_refresh(self):
        fixture = """
import importlib.util, sys
spec = importlib.util.spec_from_file_location('sample', sys.argv[1])
sample = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sample)
sample.POLL_SECONDS = .1
sample.MIN_REFRESH_SECONDS = .05
class LoginFixture:
    def __init__(self):
        self.first = True
    def fetch(self, stop):
        if self.first:
            self.first = False
            raise sample.QueryError('需要登录', clear=True)
        return sample.Snapshot((sample.Window('每周窗口', .42, None, None),), '0', sample.time.time())
sample.Antigravity = LoginFixture
sample.main()
"""
        with subprocess.Popen([sys.executable, "-c", fixture, str(SOURCE)], stdin=subprocess.PIPE,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE) as process:
            lines = queue.Queue()

            def read():
                for line in process.stdout:
                    lines.put(json.loads(line))

            reader = threading.Thread(target=read, daemon=True)
            reader.start()
            try:
                process.stdin.write(b'{"type":"hello","protocolVersion":1}\n')
                process.stdin.flush()
                initial = [lines.get(timeout=5) for _ in range(5)]
                self.assertIn("需要登录", texts(initial[-1]["root"]))
                with self.assertRaises(queue.Empty):
                    lines.get(timeout=.65)
                process.stdin.write(b'{"type":"viewAction","viewId":"quota-page","actionId":"refresh"}\n')
                process.stdin.flush()
                result = [lines.get(timeout=5) for _ in range(2)]
                self.assertEqual("42", result[0]["root"]["text"])
                self.assertIn("额外 AI 积分  0", texts(result[1]["root"]))
                process.stdin.write(b'{"type":"shutdown"}\n')
                process.stdin.flush()
                self.assertEqual(0, process.wait(timeout=5))
            finally:
                if process.poll() is None:
                    process.kill()
                reader.join(timeout=5)

    def test_api_key_and_adc_are_excluded_from_child_environment(self):
        with patch.dict("os.environ", {"GEMINI_API_KEY": "synthetic", "GOOGLE_API_KEY": "synthetic",
                                       "AGY_ADC_AUTH": "true", "GOOGLE_APPLICATION_CREDENTIALS": "synthetic",
                                       "AGY_CLI_INTERACTIVE_HEADLESS": "1"}):
            actual = PLUGIN.cli_environment()
        self.assertTrue({"GEMINI_API_KEY", "GOOGLE_API_KEY", "AGY_ADC_AUTH", "GOOGLE_APPLICATION_CREDENTIALS"}.isdisjoint(actual))
        self.assertEqual("1", actual["AGY_CLI_NONINTERACTIVE_HEADLESS"])
        self.assertNotIn("AGY_CLI_INTERACTIVE_HEADLESS", actual)

    def test_proxy_settings_are_limited_to_the_child_and_respect_explicit_values(self):
        with patch.dict("os.environ", {"HTTPS_PROXY": "http://explicit.invalid:8080"}, clear=True), \
                patch.object(PLUGIN, "getproxies", return_value={"http": "http://system.invalid:8080",
                                                                "https": "http://system.invalid:8080"}):
            actual = PLUGIN.cli_environment()
            self.assertEqual("http://explicit.invalid:8080", actual["HTTPS_PROXY"])
            self.assertEqual("http://system.invalid:8080", actual["HTTP_PROXY"])
        self.assertEqual("1", actual["AGY_CLI_NONINTERACTIVE_HEADLESS"])


if __name__ == "__main__":
    unittest.main()
