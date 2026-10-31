"""Owned synthetic JSONL process: all identifiers and numbers are test fixtures."""
import json
import os
from pathlib import Path
import sys
import threading
import time

sys.stdout.reconfigure(encoding="utf-8", newline="\n", line_buffering=True)
mode, folder = sys.argv[1], Path(sys.argv[2])
counter = folder / "starts.txt"
generation = int(counter.read_text() or "0") + 1 if counter.exists() else 1
counter.write_text(str(generation))
with (folder / "pids.txt").open("a") as stream:
    stream.write(str(os.getpid()) + "\n")


def send(message):
    print(json.dumps(message), flush=True)


def notify():
    time.sleep(.15)
    if mode == "swap":
        send({"method": "account/updated", "params": {"authMode": "apikey", "planType": None}})
    elif mode == "success":
        send({"method": "account/rateLimits/updated", "params": {"rateLimits": {
            "limitId": "codex", "primary": {"usedPercent": 40}, "credits": None, "planType": None}}})


reads = 0
accounts = 0
for line in sys.stdin:
    message = json.loads(line)
    with (folder / "requests.jsonl").open("a", encoding="utf-8") as stream:
        stream.write(json.dumps(message) + "\n")
    method = message["method"]
    if method == "initialized":
        continue
    if method == "initialize":
        if mode == "bootstrap":
            send({"method": "account/updated", "params": {"authMode": "chatgpt", "planType": "plus"}})
        send({"id": message["id"], "result": {"userAgent": "synthetic"}})
        if mode == "bootstrap":
            send({"method": "account/updated", "params": {"authMode": "chatgpt", "planType": "plus"}})
    elif method == "account/read":
        accounts += 1
        account = {"type": "apiKey"} if mode == "apikey" or (mode == "swap" and accounts > 1) else \
            {"type": "chatgpt", "email": "synthetic@example.invalid", "id": "fixture-account", "planType": "plus"}
        send({"id": message["id"], "result": {"account": account}})
        if mode == "bootstrap" and accounts == 1:
            send({"method": "account/updated", "params": {"authMode": "chatgpt", "planType": "plus"}})
    elif method == "account/rateLimits/read":
        reads += 1
        if mode == "error":
            send({"id": message["id"], "error": {"message": "PRIVATE_FIXTURE_ERROR_DO_NOT_DISPLAY"}})
        elif mode == "malformed":
            send(["unexpected", "array"])
        elif mode == "oversize":
            print("x" * (1024 * 1024 + 1), flush=True)
        elif mode == "unknown":
            send({"id": message["id"], "result": {"rateLimits": {"limitId": "other-model"}}})
        else:
            send({"id": message["id"], "result": {"rateLimitsByLimitId": {"codex": {
                "limitId": "codex", "planType": "plus", "primary": {
                    "usedPercent": 25 if reads == 1 or mode == "bootstrap" else 100, "windowDurationMins": 300, "resetsAt": 1800000000},
                "secondary": {"usedPercent": 10, "windowDurationMins": 10080},
                "credits": {"balance": "0", "hasCredits": False, "unlimited": False}}},
                "rateLimitResetCredits": {"availableCount": 0}}})
            if reads == 1:
                threading.Thread(target=notify, daemon=True).start()
