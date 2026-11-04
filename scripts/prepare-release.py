#!/usr/bin/env python3
"""Validate an explicit release plan after trusted main CI, without moving tags."""
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from urllib.error import HTTPError
from urllib.parse import quote
from urllib.request import Request, urlopen
import xml.etree.ElementTree as ET

from velopack_release import FIRST_VELOPACK_VERSION, VPK_VERSION, windows_assets, windows_version

PLATFORMS = {
    "windows": ("ToastifyReloaded", "", "build"),
    "linux": ("ToastifyReloaded.Linux", "LinuxUpdateService.cs", "Linux Build"),
    "macos": ("ToastifyReloaded.Mac", "MacUpdateService.cs", "macOS Build"),
}


def validate_plan(root, platform):
    project, service, _ = PLATFORMS[platform]
    plan = json.loads((root / "docs/releases/release-plan.json").read_text(encoding="utf-8"))
    tag = plan[platform]["tag"]
    patterns = {
        "windows": r"v(\d+\.\d+\.\d+)",
        "linux": r"v(\d+\.\d+\.\d+)-linux(?:-(?:preview|rc)\.[1-9][0-9]*)?",
        "macos": r"v(\d+\.\d+\.\d+)-macos(?:-(?:preview|rc)\.[1-9][0-9]*)?",
    }
    match = re.fullmatch(patterns[platform], tag)
    if not match:
        raise ValueError("Invalid platform release tag")
    folder = root / "src" / project
    project_tree = ET.parse(folder / (project + ".csproj"))
    version = project_tree.findtext("./PropertyGroup/Version")
    if version != match.group(1):
        raise ValueError("Release tag does not match assembly version")
    if platform == "windows" and windows_version(tag) >= FIRST_VELOPACK_VERSION:
        references = project_tree.findall("./ItemGroup/PackageReference[@Include='Velopack']")
        if len(references) != 1 or references[0].get("Version") != VPK_VERSION:
            raise ValueError("Windows release requires the pinned Velopack SDK " + VPK_VERSION)
    if service:
        source = (folder / "Services" / service).read_text(encoding="utf-8-sig")
        current = re.search(r'public const string CurrentTag = "([^"]+)";', source)
        if not current or current.group(1) != tag:
            raise ValueError("Release tag does not match updater CurrentTag")
    notes = Path("docs/releases") / (tag + ".md")
    if not (root / notes).is_file() or not (root / notes).read_text(encoding="utf-8").strip():
        raise ValueError("Version-specific release notes are missing")
    return {"release_tag": tag, "notes_path": notes.as_posix(),
            "prerelease": str("-rc." in tag or "-preview." in tag).lower()}


def validate_event(platform, event_name, event, repository, ref_name, checked_sha):
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Invalid repository")
    if not re.fullmatch(r"[0-9a-f]{40}", checked_sha):
        raise ValueError("Invalid checked commit")
    if event_name == "workflow_run":
        run = event["workflow_run"]
        head_repo = run.get("head_repository") or {}
        if (run.get("conclusion") != "success" or run.get("status") != "completed"
                or run.get("event") != "push" or run.get("head_branch") != "main"
                or run.get("name") != PLATFORMS[platform][2]
                or head_repo.get("full_name") != repository
                or event.get("repository", {}).get("full_name") != repository
                or run.get("head_sha") != checked_sha):
            raise ValueError("Only a successful same-repository main push build can publish")
    elif event_name == "push":
        if (event.get("ref") != "refs/tags/" + ref_name
                or event.get("repository", {}).get("full_name") != repository
                or not re.fullmatch(r"[0-9a-f]{40}", event.get("after", ""))):
            raise ValueError("Only a matching tag push can publish")
    else:
        raise ValueError("Unsupported publication event")


def expected_assets(tag):
    if "-linux" in tag:
        version = re.match(r"v(\d+\.\d+\.\d+)-linux", tag).group(1)
        return {"ToastifyReloaded-Linux-x64.tar.gz", "ToastifyReloaded-Linux-arm64.tar.gz",
                "ToastifyReloaded-Linux-x64.AppImage", f"toastify-reloaded_{version}_amd64.deb",
                f"toastify-reloaded_{version}_arm64.deb", "SHA256SUMS.txt"}
    if "-macos" in tag:
        return {f"ToastifyReloaded-macOS-{arch}.{kind}" for arch in ("x64", "arm64")
                for kind in ("zip", "dmg")} | {"SHA256SUMS-macOS.txt"}
    return windows_assets(tag)


def find_release(repository, tag, api):
    release = api(f"repos/{repository}/releases/tags/{quote(tag, safe='')}")
    if release is not None:
        return release
    # GitHub's tag endpoint omits an unpublished draft if its tag does not yet
    # exist. The release list includes authenticated drafts, including that case.
    matches = []
    for page in range(1, 4):
        releases = api(f"repos/{repository}/releases?per_page=100&page={page}") or []
        if not isinstance(releases, list):
            raise ValueError("Invalid GitHub release list")
        matches.extend(item for item in releases if item.get("tag_name") == tag)
        if len(releases) < 100:
            break
    if len(matches) > 1:
        raise ValueError("Multiple releases use the planned tag; refusing ambiguous publication")
    return matches[0] if matches else None


def check_remote(repository, tag, checked_sha, event_name, api):
    # Existing releases are immutable: later main commits must not republish
    # or move their tags. Failed unpublished tag attempts may only resume at
    # exactly the same commit, including when an annotated tag is involved.
    release = find_release(repository, tag, api)
    assets = {a["name"] for a in (release or {}).get("assets", []) if a.get("size", 0) > 0}
    desired_prerelease = "-rc." in tag or "-preview." in tag
    if (release is not None and not release.get("draft", False)
            and release.get("prerelease", False) == desired_prerelease
            and expected_assets(tag).issubset(assets)):
        return False
    ref = api(f"repos/{repository}/git/ref/tags/{quote(tag, safe='')}")
    if ref is None:
        if release is not None:
            if release.get("draft") and release.get("target_commitish") == checked_sha:
                # Stable release actions can create a draft before creating its
                # tag. Resume that draft only with explicit full-SHA provenance.
                return True
            raise ValueError("Incomplete release has no verifiable tag")
        if event_name == "push":
            raise ValueError("Pushed release tag is missing")
        return True
    obj = ref["object"]
    for _ in range(5):
        if obj.get("type") == "commit":
            if obj.get("sha") != checked_sha:
                raise ValueError("Existing tag points to another commit; refusing to move it")
            # A failed upload may be resumed only at the very same source SHA.
            # Fully published releases above are never rebuilt or overwritten.
            return True
        if obj.get("type") != "tag":
            break
        annotated = api(f"repos/{repository}/git/tags/{obj['sha']}")
        if annotated is None:
            break
        obj = annotated["object"]
    raise ValueError("Cannot verify existing release tag")


def main():
    platform = sys.argv[1]
    root = Path(__file__).resolve().parent.parent
    result = validate_plan(root, platform)
    checked_sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    event_name = os.environ["GITHUB_EVENT_NAME"]
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text(encoding="utf-8"))
    repository = os.environ["GITHUB_REPOSITORY"]
    ref_name = os.environ["GITHUB_REF_NAME"]
    validate_event(platform, event_name, event, repository, ref_name, checked_sha)
    if event_name == "push" and ref_name != result["release_tag"]:
        raise ValueError("Pushed tag differs from explicit release plan")

    def api(path):
        request = Request("https://api.github.com/" + path, headers={
            "Authorization": "Bearer " + os.environ["GH_TOKEN"],
            "Accept": "application/vnd.github+json", "User-Agent": "ToastifyReleaseGuard",
            "X-GitHub-Api-Version": "2022-11-28"})
        try:
            with urlopen(request, timeout=30) as response:
                return json.load(response)
        except HTTPError as error:
            if error.code == 404:
                return None
            raise RuntimeError("GitHub release preflight failed: HTTP " + str(error.code)) from None

    publish = check_remote(repository, result["release_tag"], checked_sha, event_name, api)
    result.update(release_sha=checked_sha, publish=str(publish).lower())
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        for key, value in result.items():
            output.write(f"{key}={value}\n")
    with open(os.environ["GITHUB_ENV"], "a", encoding="utf-8") as output:
        output.write("RELEASE_TAG=" + result["release_tag"] + "\n")
    print(json.dumps(result))


if __name__ == "__main__":
    main()
