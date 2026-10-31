"""Exercise the production release whitelist with synthetic, non-executed ZIP payloads."""
import importlib.util
import io
import json
from pathlib import Path
import unittest
import zipfile


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("verify_release", ROOT / "scripts/Verify-Release.py")
VERIFY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFY)


def fixture(version=(0, 6, 0)):
    result = {}
    for name in VERIFY.approved_plugin_files(version):
        provider = name.split("/")[2]
        if name.endswith("plugin.json"):
            manifest = {"id": provider, "protocolVersion": 1, "command": "powershell.exe", "arguments": []}
            if provider == "codex-balance":
                manifest.update(command="py.exe", arguments=["-3", "-u", "plugin.py"])
            result[name] = json.dumps(manifest).encode()
        elif name.endswith("plugin.ps1"):
            result[name] = b"\xef\xbb\xbf# synthetic PowerShell source\r\n"
        elif name.endswith("plugin.py"):
            result[name] = b"# synthetic Python source\npass\n"
        else:
            result[name] = b"Synthetic public documentation\n"
    return result


class PluginArchiveChecks(unittest.TestCase):
    def verify(self, files, version=(0, 6, 0)):
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w") as archive:
            for name, data in files.items():
                archive.writestr(name, data)
        buffer.seek(0)

        def check(name, condition, detail=None):
            if not condition:
                raise ValueError(name)

        with zipfile.ZipFile(buffer) as archive:
            VERIFY.verify_plugin_examples(archive, version, check)

    def test_current_release_requires_exactly_three_public_examples(self):
        files = fixture()
        self.assertEqual(9, len(files))
        self.verify(files)

    def test_older_versions_retain_their_original_example_sets(self):
        for version, count in (((0, 3, 10), 0), ((0, 4, 0), 3), ((0, 5, 1), 6)):
            with self.subTest(version=version):
                files = fixture(version)
                self.assertEqual(count, len(files))
                self.verify(files, version)

    def test_missing_codex_program_is_rejected(self):
        files = fixture()
        del files["app/PluginExamples/codex-balance/plugin.py"]
        with self.assertRaisesRegex(ValueError, "exact approved public plugin files"):
            self.verify(files)

    def test_private_files_and_nested_programs_are_rejected(self):
        for extra in ("auth.json", "settings.json", "plugin.py.bak", "helper.exe", "__pycache__/plugin.pyc", "nested/plugin.py"):
            with self.subTest(extra=extra):
                files = fixture()
                files["app/PluginExamples/codex-balance/" + extra] = b"synthetic rejected payload"
                with self.assertRaisesRegex(ValueError, "exact approved public plugin files"):
                    self.verify(files)

    def test_unapproved_provider_is_rejected(self):
        files = fixture()
        files["app/PluginExamples/unapproved/plugin.json"] = b"{}"
        with self.assertRaisesRegex(ValueError, "exact approved public plugin files"):
            self.verify(files)

    def test_alternate_windows_path_casing_cannot_bypass_the_allowlist(self):
        files = fixture()
        files["app/pluginexamples/codex-balance/plugin.py"] = b"# rejected Windows path alias"
        with self.assertRaisesRegex(ValueError, "exact approved public plugin files"):
            self.verify(files)

    def test_portable_python_launcher_and_arguments_are_required(self):
        path = "app/PluginExamples/codex-balance/plugin.json"
        for change in ({"command": "C:/Users/Example/python.exe"},
                       {"arguments": ["-3", "-u", "other.py"]}, {"command": "cmd.exe"}):
            with self.subTest(change=change):
                files = fixture()
                manifest = json.loads(files[path])
                manifest.update(change)
                files[path] = json.dumps(manifest).encode()
                with self.assertRaisesRegex(ValueError, "portable Python launcher"):
                    self.verify(files)

    def test_invalid_or_oversized_python_source_is_rejected_without_execution(self):
        path = "app/PluginExamples/codex-balance/plugin.py"
        for source, error in ((b"invalid syntax )", SyntaxError), (b"#" * (128 * 1024 + 1), ValueError)):
            with self.subTest(error=error):
                files = fixture()
                files[path] = source
                with self.assertRaises(error):
                    self.verify(files)

    def test_powershell_encoding_is_preserved(self):
        files = fixture()
        files["app/PluginExamples/pixel-pet/plugin.ps1"] = b"# missing BOM"
        with self.assertRaisesRegex(ValueError, "PowerShell encoding"):
            self.verify(files)

    def test_repository_samples_match_the_release_contract(self):
        files = {}
        for path in VERIFY.approved_plugin_files((0, 6, 0)):
            source = ROOT / "samples" / Path(path).relative_to("app/PluginExamples")
            files[path] = source.read_bytes()
        self.verify(files)


if __name__ == "__main__":
    unittest.main()
