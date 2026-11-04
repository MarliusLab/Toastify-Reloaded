#!/usr/bin/env python3
"""Check cross-platform integration contracts; native CI and acceptance remain separate.

Uses only the standard library. XML is parsed as XML; the release gate is exercised
with synthetic GitHub responses. C# checks inspect tokens with comments/literals
removed, so formatting and translated UI text do not define feature parity.
"""
from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PROJECTS = {"windows": "ToastifyReloaded", "linux": "ToastifyReloaded.Linux", "macos": "ToastifyReloaded.Mac"}
XAML_NAME = "{http://schemas.microsoft.com/winfx/2006/xaml}Name"
CS_LEXER = re.compile(r'//[^\n]*|/\*.*?\*/|@"(?:""|[^"])*"|\$?"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'|[A-Za-z_][A-Za-z0-9_]*|[^\s]', re.S)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def tokens(path):
    return [value for value in CS_LEXER.findall(read(path))
            if not value.startswith(("//", "/*", '"', '@"', '$"', "'"))]


def require_tokens(path, *identifiers):
    missing = set(identifiers) - set(tokens(path))
    require(not missing, f"{path}: missing integration contracts {sorted(missing)}")


def method_tokens(path, name):
    values = tokens(path)
    for index in range(len(values) - 1):
        if values[index:index + 2] != [name, "("]:
            continue
        end, depth = index + 2, 1
        while end < len(values) and depth:
            depth += (values[end] == "(") - (values[end] == ")")
            end += 1
        if end >= len(values) or values[end] != "{":
            continue
        start, depth = end + 1, 1
        end += 1
        while end < len(values) and depth:
            depth += (values[end] == "{") - (values[end] == "}")
            end += 1
        return values[start:end - 1]
    raise ValueError(f"{path}: missing method body {name}")


def named_controls(path):
    return {node.get(XAML_NAME) or node.get("Name") for node in ET.fromstring(read(path)).iter()}


def yaml_block(text, key):
    """Read a top-level mapping block, without claiming to validate all YAML."""
    lines = text.splitlines()
    start = next((index for index, line in enumerate(lines) if line == key + ":"), None)
    require(start is not None, "Workflow is missing top-level " + key)
    end = start + 1
    while end < len(lines) and (not lines[end].strip() or lines[end].startswith((" ", "#"))):
        end += 1
    return "\n".join(lines[start + 1:end])


def verify_projects_and_navigation():
    shared = {path.resolve() for path in (ROOT / "src/ToastifyReloaded.Shared").glob("*.cs")}
    for platform, project in PROJECTS.items():
        directory = ROOT / "src" / project
        tree = ET.parse(directory / (project + ".csproj"))
        pins = tree.findall("./ItemGroup/PackageReference[@Include='Velopack']")
        require(len(pins) == 1 and pins[0].get("Version") == "1.2.161", project + ": Velopack pin drift")
        included = set()
        for item in tree.findall("./ItemGroup/Compile"):
            pattern = item.get("Include", "").replace("\\", "/")
            if "ToastifyReloaded.Shared/" in pattern:
                included.update(path.resolve() for path in directory.glob(pattern))
        require(shared and shared <= included, project + ": all common source files must be compiled")
        main = f"src/{project}/MainWindow." + ("xaml.cs" if platform == "windows" else "axaml.cs")
        toast = f"src/{project}/ToastWindow." + ("xaml.cs" if platform == "windows" else "axaml.cs")
        require_tokens(main, "ToastShowHotkeyController", "Attach", "ExecuteAsync", "RequestLyricsPlusAsync")
        require_tokens(toast, "CanHandleLyricsPlusRequest", "RequestLyricsPlusAsync", "OpenLyricsPlusAsync")
        if platform == "windows":
            require_tokens(toast, "ToastNowPlayingHotkeyCoordinator", "RunSuspendedAsync")
            require_tokens("src/ToastifyReloaded/Models/AppSettings.cs", "Hotkeys", "ShowToast")
        else:
            require_tokens(main, "ToastNavigationGate", "HotkeyShowToast")
            require_tokens(toast, "ToastNavigationGate", "RunAsync")
            model = "LinuxSettings.cs" if platform == "linux" else "MacSettings.cs"
            require_tokens(f"src/{project}/Models/{model}", "HotkeyShowToast")
        opener = f"src/{project}/Services/SpotifyTrackOpenService.cs"
        route = re.search(r'public\s+const\s+string\s+LyricsPlusUri\s*=\s*"([^"]+)"', read(opener))
        require(route and route[1] == "spotify:app:lyrics-plus", project + ": Lyrics Plus must use the fixed route")
        require_tokens(opener, "CancellationToken", "OpenLyricsPlusAsync", "LyricsPlusUri")
    print("PASS: all three projects compile common source and pinned SDK; toast/navigation/settings contracts are wired")


def verify_maintenance():
    for platform, project in PROJECTS.items():
        directory = f"src/{project}"
        main = directory + ("/MainWindow.xaml.cs" if platform == "windows" else "/MainWindow.axaml.cs")
        xaml = directory + ("/MainWindow.xaml" if platform == "windows" else "/MainWindow.axaml")
        require({"UpdateProgressBar", "CancelUpdateButton"} <= named_controls(xaml), project + ": update progress/cancel controls missing")
        require_tokens(main, "SuppressAutomaticInstall", "CancellationTokenSource")
        program = directory + "/Program.cs"
        require_tokens(program, "SetAutoApplyOnStartup", "false", "MarkUpdaterRestart")
        if platform == "windows":
            body = method_tokens(main, "RunCompatibilityCheckAsync")
            require({"WhenAll", "GetInfoAsync", "GetSpicetifyVersionAsync"} <= set(body), "Windows: independent version probe integration missing")
            require_tokens(directory + "/MainWindow.Maintenance.cs", "CancelUpdate_Click", "CanCancel", "ReportMaintenanceProgress", "_updateHandoffPending")
            require_tokens(directory + "/Services/WindowsUpdateService.cs", "IsInstalled", "DownloadAsync", "ApplyAndRestartAsync", "CancellationToken", "IProgress")
            require_tokens(directory + "/Services/PowerShellService.cs", "WaitForExitAsync", "CancellationTokenSource", "StopProcessBoundedAsync")
        else:
            body = method_tokens(main, "RefreshStatusAsync")
            require({"WhenAll", "_spicetify", "_spotifyVersion", "GetVersionAsync"} <= set(body), project + ": independent version probe integration missing")
            require_tokens(main, "CancelUpdate_Click", "UpdateOperationProgress", "_canCancelUpdate", "_updateHandoffPending")
            installer = "LinuxUpdateInstallerService.cs" if platform == "linux" else "MacUpdateInstallerService.cs"
            require_tokens(directory + "/Services/" + installer, "PortablePackageUpdateService", "IsInstalled", "SafeUpdateDownloader", "DownloadAsync", "ApplyAndRestartAsync", "CancellationToken", "IProgress")
            require_tokens(directory + "/Services/ProcessService.cs", "BoundedProcessRunner", "RunAsync")
            spice = "SpicetifyLinuxService.cs" if platform == "linux" else "SpicetifyMacService.cs"
            require_tokens(directory + "/Services/" + spice, "CancellationToken", "GetVersionAsync", "RepairAfterSpotifyUpdateAsync", "SemaphoreSlim", "CancellationTokenSource")
    print("PASS: independent version probes, progress/cancel, bounded helpers and updater-restart policy remain integrated")


def verify_build_and_release_policy():
    spec = importlib.util.spec_from_file_location("parity_release_guard", ROOT / "scripts/prepare-release.py")
    guard = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(guard)
    expected = {"windows": ("build", ".github/workflows/build.yml"),
                "linux": ("Linux Build", ".github/workflows/linux-build.yml"),
                "macos": ("macOS Build", ".github/workflows/macos-build.yml")}
    require(guard.BUILD_WORKFLOWS == expected, "Release gate must trust exactly the three expected build workflows")
    repository, sha = "MarliusLab/Toastify-Reloaded", "1" * 40
    builds = [{"id": index, "name": name, "path": path, "head_sha": sha, "head_branch": "main",
               "event": "push", "status": "completed", "conclusion": "success",
               "repository": {"full_name": repository}, "head_repository": {"full_name": repository}}
              for index, (name, path) in enumerate(expected.values(), 1)]
    require(guard.trusted_builds(repository, sha, lambda _: {"workflow_runs": builds})["ready"], "Three successful main builds must satisfy the release gate")
    for index in range(3):
        for field, value in (("head_sha", "2" * 40), ("conclusion", "failure"), ("status", "in_progress"),
                             ("event", "pull_request"), ("path", "untrusted.yml")):
            changed = [dict(run) for run in builds]
            changed[index][field] = value
            def api(path):
                require("/actions/runs?" in path, "Incomplete joint gate must not touch release/tag endpoints")
                return {"workflow_runs": changed}
            require(guard.release_eligibility(repository, "v1.3.12", sha, "workflow_run", api)["publish"] == "false", "A missing/failed/foreign build bypassed the joint release gate")
    release_paths = {"windows": "release.yml", "linux": "linux-release.yml", "macos": "macos-release.yml"}
    for platform, (_, path) in expected.items():
        guard.validate_plan(ROOT, platform)
        workflow = read(path)
        events = yaml_block(workflow, "on")
        require(not re.search(r"^\s+paths(?:-ignore)?:", events, re.M), path + ": platform build path filters violate the standing parity policy")
        require(re.search(r"^  push:\s*\n\s+branches:\s*\[\s*['\"]?main['\"]?\s*\]", events), path + ": every main push must build")
        require(re.search(r"^  pull_request:\s*\n\s+branches:\s*\[\s*['\"]?main['\"]?\s*\]", events, re.M), path + ": every main pull request must build")
        require("scripts/verify-platform-parity.py" in workflow and "tests/ToastifyReloaded.TrackLink.Tests" in workflow, path + ": parity verification and behavioral harness must run")
        release = read(".github/workflows/" + release_paths[platform])
        release_events = yaml_block(release, "on")
        names = re.search(r"^    workflows:\s*(\[[^\n]+\])", release_events, re.M)
        require(names and set(json.loads(names[1])) == {name for name, _ in expected.values()}, platform + ": all three build completions must trigger publication checks")
        permissions = yaml_block(release, "permissions")
        require(re.search(r"^  actions: read$", permissions, re.M) and re.search(r"^  contents: write$", permissions, re.M), platform + ": release needs explicit artifact-read and contents-write permissions")
        require("steps.prepare.outputs." + platform + "_build_run" in release and "actions/download-artifact@v5" in release,
                platform + ": publication must reuse the verified platform build run")
        require(re.search(r"^\s+draft: true$", release, re.M) and "--release-id" in release,
                platform + ": verify the complete private draft before publication")
    require(len(guard.expected_assets("v1.4.2-linux")) == 13 and len(guard.expected_assets("v1.5.0-macos-rc.3")) == 11,
            "First native Unix baselines must include both architectures and full feeds")
    print("PASS: every main/PR builds all platforms; publication requires three trusted successes at one SHA and reuses tested artifacts")


def main():
    verify_projects_and_navigation()
    verify_maintenance()
    verify_build_and_release_policy()
    subprocess.run([sys.executable, str(ROOT / "scripts/validate-macos-parity.py")], cwd=ROOT, check=True)
    print("CROSS-PLATFORM CONTRACT RESULT: PASS (static integration and synthetic release provenance; native acceptance is separate)")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, ET.ParseError, subprocess.CalledProcessError) as error:
        print("CROSS-PLATFORM CONTRACT RESULT: FAIL: " + str(error), file=sys.stderr)
        raise SystemExit(1)
