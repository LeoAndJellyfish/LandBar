"""Run the public provider against a synthetic app-server, never the user's CLI."""
import importlib.util
from pathlib import Path
import sys

source = Path(__file__).resolve().parents[2] / "samples/codex-balance/plugin.py"
spec = importlib.util.spec_from_file_location("public_codex_sample", source)
plugin = importlib.util.module_from_spec(spec)
spec.loader.exec_module(plugin)
sys.stdout.reconfigure(encoding="utf-8", newline="\n", line_buffering=True)
factory = lambda: plugin.AppServer([sys.executable, "-u", str(Path(__file__).with_name("server_fixture.py")), *sys.argv[1:]])
plugin.Plugin(server_factory=factory).run()
