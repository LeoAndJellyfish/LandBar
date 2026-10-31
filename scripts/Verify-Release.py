"""Verify a published release by downloading its public assets; never executes archive contents."""
import argparse
import ast
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
import zipfile


def approved_plugin_files(version):
    """Versioned public examples, with exact paths rather than provider-directory wildcards."""
    providers = {}
    if version >= (0, 4, 0):
        providers["focus-timer"] = ("plugin.json", "plugin.ps1", "README.md")
    if version >= (0, 5, 0):
        providers["pixel-pet"] = ("plugin.json", "plugin.ps1", "README.md")
    if version >= (0, 6, 0):
        providers["codex-balance"] = ("plugin.json", "plugin.py", "README.md")
    return {f"app/PluginExamples/{provider}/{filename}"
            for provider, filenames in providers.items() for filename in filenames}


def verify_plugin_examples(archive, version, check):
    expected = approved_plugin_files(version)
    actual = {name for name in archive.namelist() if name.casefold().startswith("app/pluginexamples/")}
    check("Only exact approved public plugin files are bundled", actual == expected,
          {"missing": sorted(expected - actual), "unexpected": sorted(actual - expected)})
    for provider in ("focus-timer", "pixel-pet", "codex-balance"):
        prefix = f"app/PluginExamples/{provider}/"
        if prefix + "plugin.json" not in expected:
            continue
        manifest_bytes = archive.read(prefix + "plugin.json")
        check(f"{provider} manifest size", len(manifest_bytes) <= 16 * 1024)
        manifest = json.loads(manifest_bytes.decode("utf-8-sig"))
        check(f"{provider} declares supported protocol", manifest.get("id") == provider and
              manifest.get("protocolVersion") == 1)
        if provider == "codex-balance":
            check("Codex example uses a portable Python launcher", manifest.get("command") == "py.exe" and
                  manifest.get("arguments") == ["-3", "-u", "plugin.py"])
            source = archive.read(prefix + "plugin.py")
            check("Codex example source size", 0 < len(source) <= 128 * 1024)
            ast.parse(source.decode("utf-8-sig"), filename="codex-balance/plugin.py")
            check("Codex example contains valid Python source", True)
        else:
            check(f"{provider} uses Windows PowerShell", manifest.get("command") == "powershell.exe")
            check(f"{provider} preserves PowerShell encoding",
                  archive.read(prefix + "plugin.ps1").startswith(b"\xef\xbb\xbf"))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--repository", required=True)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--expected-commit", default="")
    parser.add_argument("--output", default="artifacts/release-verification")
    args = parser.parse_args()
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    report = {"repository": args.repository, "tag": args.tag, "passed": False,
              "downloadAuthentication": "none", "checks": []}

    def check(name, condition, detail=None):
        report["checks"].append({"name": name, "passed": bool(condition), "detail": detail})
        if not condition:
            raise ValueError(name)

    def api(path):
        headers = {"Accept": "application/vnd.github+json", "User-Agent": "LandBar-release-verifier",
                   "X-GitHub-Api-Version": "2022-11-28"}
        token = os.environ.get("GH_TOKEN")
        if token:
            headers["Authorization"] = "Bearer " + token
        request = urllib.request.Request("https://api.github.com/" + path, headers=headers)
        with urllib.request.urlopen(request, timeout=45) as response:
            return json.load(response)

    def digest(path):
        result = hashlib.sha256()
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                result.update(chunk)
        return result.hexdigest()

    def download(asset, filename):
        url = asset["browser_download_url"]
        expected = f"https://github.com/{args.repository}/releases/download/{args.tag}/{filename}"
        check(f"Public download URL for {filename}", url == expected)
        destination = output / filename
        environment = {key: value for key, value in os.environ.items()
                       if key not in ("GH_TOKEN", "GITHUB_TOKEN")}
        # No token or Authorization header is supplied to curl. --disable ignores curlrc files.
        command = ["curl", "--disable", "--fail", "--location", "--silent", "--show-error",
                   "--retry", "3", "--retry-all-errors", "--retry-delay", "2",
                   "--retry-max-time", "600", "--connect-timeout", "30", "--max-time", "300",
                   "--proto", "=https", "--proto-redir", "=https", "--output", str(destination), url]
        result = subprocess.run(command, env=environment, text=True, capture_output=True)
        check(f"Unauthenticated download of {filename}", result.returncode == 0,
              {"curlExitCode": result.returncode})
        check(f"Download length for {filename}", destination.stat().st_size == asset["size"],
              {"expected": asset["size"], "actual": destination.stat().st_size})
        asset_digest = asset.get("digest") or ""
        if asset_digest.startswith("sha256:"):
            check(f"GitHub asset digest for {filename}", digest(destination) == asset_digest[7:])
        return destination

    try:
        check("Repository format", bool(re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", args.repository)))
        check("Version tag format", bool(re.fullmatch(r"v\d+\.\d+\.\d+", args.tag)))
        if args.expected_commit:
            check("Expected commit format", bool(re.fullmatch(r"[0-9a-fA-F]{40}", args.expected_commit)))
        repository = api(f"repos/{args.repository}")
        check("Repository is public", repository.get("private") is False)
        release = api(f"repos/{args.repository}/releases/tags/{args.tag}")
        check("Published stable release", release["tag_name"] == args.tag and
              release["draft"] is False and release["prerelease"] is False)
        tagged = api(f"repos/{args.repository}/commits/{urllib.parse.quote(args.tag, safe='')}")["sha"]
        target = release["target_commitish"]
        target_commit = api(f"repos/{args.repository}/commits/{urllib.parse.quote(target, safe='')}")["sha"]
        check("Release target matches tag commit", target_commit == tagged)
        if args.expected_commit:
            check("Release workflow commit matches tag", tagged.lower() == args.expected_commit.lower())
        report.update({"releaseUrl": release["html_url"], "tagCommit": tagged,
                       "targetCommit": target_commit, "releaseId": release["id"]})
        archive_name = f"LandBar-{args.tag}-win-x64.zip"
        assets = {asset["name"]: asset for asset in release["assets"]}
        check("Expected release asset set", set(assets) == {archive_name, "SHA256SUMS.txt"}, sorted(assets))
        check("Archive has a bounded size", 0 < assets[archive_name]["size"] < 512 * 1024 * 1024)
        check("Checksum file has a bounded size", 0 < assets["SHA256SUMS.txt"]["size"] < 64 * 1024)
        checksum_path = download(assets["SHA256SUMS.txt"], "SHA256SUMS.txt")
        archive_path = download(assets[archive_name], archive_name)
        expected_hashes = []
        for line in checksum_path.read_text(encoding="utf-8-sig").splitlines():
            match = re.fullmatch(r"([0-9a-fA-F]{64})[ \t]+\*?(.+)", line)
            if match and match[2] == archive_name:
                expected_hashes.append(match[1].lower())
        check("Checksum identifies exactly one archive", len(expected_hashes) == 1)
        actual_hash = digest(archive_path)
        check("Archive SHA256 matches published checksum", actual_hash == expected_hashes[0])
        report.update({"archiveBytes": archive_path.stat().st_size, "sha256": actual_hash,
                       "publicDownloadUrl": assets[archive_name]["browser_download_url"]})

        with zipfile.ZipFile(archive_path) as archive:
            infos = archive.infolist()
            names = [entry.filename for entry in infos]
            check("Archive paths are relative and unique", len(names) == len({name.casefold() for name in names}) and
                  all(not name.startswith("/") and "\\" not in name and
                      ".." not in PurePosixPath(name).parts and ":" not in name for name in names))
            check("Archive expansion is bounded", len(names) < 2000 and
                  sum(entry.file_size for entry in infos) < 1024 * 1024 * 1024)
            check("Archive CRC", archive.testzip() is None)
            required = {"app/LandBar.exe", "app/LandBar.dll", "app/LandBar.Core.dll", "app/coreclr.dll",
                        "app/hostfxr.dll", "app/PresentationFramework.dll", "app/System.Windows.Forms.dll",
                        "app/LandBar.deps.json", "app/LandBar.runtimeconfig.json", "app/Assets/Fonts/OFL.txt",
                        "app/Assets/Fonts/README.md", "LandBar.identity.msix", "identity/AppxManifest.xml",
                        "Install.ps1", "Start.ps1", "Uninstall.ps1", "安装并启动.cmd", "启动.cmd", "卸载.cmd",
                        "README.md", "THIRD_PARTY_NOTICES.md", "docs/installation.md", "docs/development.md",
                        "third_party/dotnet/COMPONENTS.txt", "third_party/windows-sdk/sdk_license.rtf"}
            if tuple(map(int, args.tag[1:].split("."))) >= (0, 4, 0):
                required.update({"app/LandBar.Plugins.dll", "docs/plugins.md",
                                 "app/PluginExamples/focus-timer/plugin.json",
                                 "app/PluginExamples/focus-timer/plugin.ps1",
                                 "app/PluginExamples/focus-timer/README.md"})
            check("Required application, identity, documentation and license files", required.issubset(names),
                  {"missing": sorted(required.difference(names))})
            verify_plugin_examples(archive, tuple(map(int, args.tag[1:].split("."))), check)
            blocked = {"smoke-result.json", "notification-test-result.json", "settings.json", "deployment.json", "diagnostics.log",
                       "auth.json", "accounts.json", "config.toml"}
            forbidden = [name for name in names if name.lower().endswith(".pdb") or
                         name.lower().endswith((".pyc", ".pyo", ".bak")) or
                         PurePosixPath(name).name.lower() in blocked or
                         any(part.lower() in ("artifacts", "obj", "bin", "artwork", "__pycache__", ".codex") or
                             part.lower().startswith("evidence-v") for part in PurePosixPath(name).parts)]
            check("No symbols, runtime cache or local diagnostic evidence", not forbidden, forbidden)
            check("README image assets present", any(name.startswith("docs/images/") for name in names))
            executable = archive.read("app/LandBar.exe")
            pe_offset = int.from_bytes(executable[60:64], "little")
            check("Application is Windows x64", executable[:2] == b"MZ" and
                  executable[pe_offset:pe_offset + 4] == b"PE\x00\x00" and
                  int.from_bytes(executable[pe_offset + 4:pe_offset + 6], "little") == 0x8664)
            namespace = "{http://schemas.microsoft.com/appx/manifest/foundation/windows10}"
            identity = ET.fromstring(archive.read("identity/AppxManifest.xml")).find(namespace + "Identity")
            expected_version = args.tag[1:] + ".0"
            check("Identity manifest version matches release", identity is not None and
                  identity.get("Name") == "LandBar.Desktop" and identity.get("Version") == expected_version)
            with zipfile.ZipFile(io.BytesIO(archive.read("LandBar.identity.msix"))) as package:
                check("Identity package CRC", package.testzip() is None)
                embedded = ET.fromstring(package.read("AppxManifest.xml")).find(namespace + "Identity")
                check("Identity package and external manifest agree", embedded is not None and
                      embedded.get("Version") == expected_version and embedded.get("Name") == "LandBar.Desktop")
            deps = json.loads(archive.read("app/LandBar.deps.json"))
            runtime_packs = [name for name in deps["libraries"] if name.startswith("runtimepack.")]
            for package_name in ("Microsoft.NETCore.App.Runtime.win-x64", "Microsoft.WindowsDesktop.App.Runtime.win-x64"):
                matches = [name for name in runtime_packs if name.startswith("runtimepack." + package_name + "/")]
                check(f"Self-contained {package_name}", len(matches) == 1)
                version = matches[0].split("/")[1]
                prefix = f"third_party/dotnet/{package_name}/{version}/"
                files = [name for name in names if name.startswith(prefix)]
                check(f"Actual runtime license for {package_name}", any("license" in PurePosixPath(name).name.lower() for name in files))
                if package_name.startswith("Microsoft.NETCore"):
                    check("Runtime third-party notices", any("notice" in PurePosixPath(name).name.lower() for name in files))
            check("Windows SDK license is complete RTF", len(archive.read("third_party/windows-sdk/sdk_license.rtf")) > 10000 and
                  archive.read("third_party/windows-sdk/sdk_license.rtf").startswith(b"{\\rtf"))
            report.update({"archiveEntries": len(names), "identityVersion": expected_version,
                           "runtimePacks": runtime_packs})
        report["passed"] = True
    except Exception as error:
        report["error"] = f"{type(error).__name__}: {error}"
    finally:
        (output / "verification.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: report.get(key) for key in ("passed", "tag", "tagCommit", "sha256", "archiveBytes", "error")}, ensure_ascii=False))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
