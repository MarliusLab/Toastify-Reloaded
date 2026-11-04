#!/usr/bin/env python3
"""Publish an exact, validated native Linux/macOS draft by upload ID only."""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import time
from urllib.error import HTTPError
from urllib.parse import quote
from urllib.request import Request, urlopen
import xml.etree.ElementTree as ET
import zipfile


def load_sibling(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


native = load_sibling("portable_release", "portable-release.py")
windows = load_sibling("windows_publisher", "publish-windows-release.py")


def validate_stage(directory: Path, platform: str, version: str):
    """Read only: never normalize feeds, repair a manifest or remove extra files."""
    native.version_key(version)
    if platform not in native.CONFIG:
        raise ValueError("Unknown native platform")
    files = list(directory.iterdir())
    if any(path.is_symlink() or not path.is_file() for path in files):
        raise ValueError("Native publication stage must contain only regular asset files")
    expected = set(native.legacy_names(platform, version))
    package_id, runtimes, checksum = native.CONFIG[platform]
    try:
        for runtime in runtimes:
            feed = json.loads((directory / f"releases.{runtime}.json").read_text(encoding="utf-8-sig"))
            if not isinstance(feed.get("Assets"), list) or any(entry.get("Version") != version for entry in feed["Assets"]):
                raise ValueError("Published native feeds must contain only the current version")
            expected |= native.validate_channel(directory, platform, version, runtime, prune=False)
            for entry in feed["Assets"]:
                with zipfile.ZipFile(directory / entry["FileName"]) as archive:
                    manifest = next(name for name in archive.namelist() if name.endswith(".nuspec") and "/" not in name)
                    metadata = {node.tag.rsplit("}", 1)[-1]: node.text for node in ET.fromstring(archive.read(manifest)).iter()}
                if (metadata.get("id") != package_id or metadata.get("channel") != runtime or
                        metadata.get("rid") != runtime or metadata.get("os") != ("linux" if platform == "linux" else "osx")):
                    raise ValueError("Native NuGet package manifest has a different identity or architecture")
    except (KeyError, TypeError, StopIteration, json.JSONDecodeError, zipfile.BadZipFile, ET.ParseError) as error:
        raise ValueError("Malformed native package or feed") from error
    if {path.name for path in files} != expected | {checksum}:
        raise ValueError("Native publication stage has missing or unexpected assets")
    if any(path.stat().st_size <= 0 for path in files):
        raise ValueError("Native publication asset is empty")
    hashes = {}
    for line in (directory / checksum).read_text(encoding="utf-8-sig").splitlines():
        match = re.fullmatch(r"([0-9a-fA-F]{64})[ \t]+\*?([A-Za-z0-9_.-]+)", line)
        if not match or match[2] in hashes:
            raise ValueError("Malformed or duplicate native checksum entry")
        hashes[match[2]] = match[1].lower()
    if set(hashes) != expected or any(hashes[name] != native.digest(directory / name) for name in expected):
        raise ValueError("Native checksum manifest differs from the exact staged files")
    return {path.name: path for path in files}


def verify_release_metadata(release, repository, tag, source_sha, release_id, prerelease, draft):
    if (not isinstance(release, dict) or release.get("id") != release_id or release.get("tag_name") != tag or
            release.get("target_commitish") != source_sha or release.get("prerelease") is not prerelease or
            release.get("draft") is not draft):
        raise ValueError("Native release ID, tag, source, draft or channel differs from the authorized upload")
    if not draft and release.get("html_url") != f"https://github.com/{repository}/releases/tag/{tag}":
        raise ValueError("Published release URL differs from the expected repository/tag")


def verify_assets(release, expected, repository, tag, draft):
    rows = release.get("assets", [])
    if not isinstance(rows, list) or any(not isinstance(asset, dict) or not native.trusted_name(asset.get("name")) for asset in rows):
        raise ValueError("Native release asset list is malformed")
    assets = {asset.get("name"): asset for asset in rows}
    if len(assets) != len(rows) or set(assets) != set(expected):
        raise ValueError("Native release has missing, duplicate or unexpected assets")
    for name, path in expected.items():
        asset = assets[name]
        if (asset.get("state") != "uploaded" or type(asset.get("size")) is not int or asset["size"] != path.stat().st_size or
                not isinstance(asset.get("digest"), str) or asset["digest"].lower() != "sha256:" + native.digest(path)):
            raise ValueError("Native uploaded asset is incomplete or has the wrong digest: " + name)
        url = asset.get("browser_download_url", "")
        if not isinstance(url, str): raise ValueError("Malformed uploaded asset URL")
        expected_url = f"https://github.com/{repository}/releases/download/{quote(tag, safe='')}/{name}"
        # A private draft can use GitHub's temporary untagged URL before its tag
        # exists. Publication must resolve to the exact platform tag afterwards.
        temporary = re.fullmatch(r"https://github\.com/" + re.escape(repository) +
            r"/releases/download/untagged-[0-9a-f]+/" + re.escape(name), url)
        if url != expected_url and not (draft and temporary):
            raise ValueError("Native uploaded asset URL belongs to another source: " + name)
    return [{"name": name, "size": assets[name]["size"], "digest": assets[name]["digest"]} for name in sorted(assets)]


def publish(platform, tag, source_sha, directory, api, release_id, repository=native.REPO, delay=time.sleep):
    if repository != native.REPO or not re.fullmatch(r"[0-9a-f]{40}", source_sha):
        raise ValueError("Invalid native publication provenance")
    if platform not in native.CONFIG or type(release_id) is not int or release_id <= 0:
        raise ValueError("Invalid native platform or upload release ID")
    version = native.tag_version(tag, platform)
    if version is None:
        raise ValueError("Release tag does not match its native platform")
    prerelease = "-rc." in version or "-preview." in version
    expected = validate_stage(directory, platform, version)
    windows.verify_tag(api, tag, source_sha, optional=True)
    release_path = "releases/" + str(release_id)
    for attempt in range(6):
        release = api(release_path)
        if release is None:
            raise ValueError("Expected native upload draft is missing")
        draft = release.get("draft") is not False
        verify_release_metadata(release, repository, tag, source_sha, release_id, prerelease, draft)
        try:
            assets = verify_assets(release, expected, repository, tag, draft)
            break
        except ValueError:
            if attempt == 5 or not draft:
                raise
            delay(3)
    if not draft:
        windows.verify_tag(api, tag, source_sha)
        return {"release_url": release["html_url"], "source_sha": source_sha, "assets": assets, "already_published": True}
    # Keep the Windows stable release as the repository-wide latest; platform
    # update services select their own stable/RC channel from the release list.
    result = api(release_path, "PATCH", {"draft": False, "prerelease": prerelease, "make_latest": "false"})
    verify_release_metadata(result, repository, tag, source_sha, release_id, prerelease, False)
    verified = verify_assets(result, expected, repository, tag, False)
    windows.verify_tag(api, tag, source_sha)
    return {"release_url": result["html_url"], "source_sha": source_sha, "assets": verified}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", required=True, choices=native.CONFIG)
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--release-id", required=True, type=int)
    args = parser.parse_args()

    def api(path, method="GET", body=None):
        request = Request(f"https://api.github.com/repos/{native.REPO}/{path}", method=method,
            data=json.dumps(body).encode() if body is not None else None,
            headers={"Authorization": "Bearer " + os.environ["GH_TOKEN"], "Accept": "application/vnd.github+json",
                     "Content-Type": "application/json", "User-Agent": "ToastifyNativeVerifiedPublisher",
                     "X-GitHub-Api-Version": "2022-11-28"})
        try:
            with urlopen(request, timeout=30) as response: return json.load(response)
        except HTTPError as error:
            if method == "GET" and error.code == 404: return None
            raise RuntimeError("Native publication API failed: HTTP " + str(error.code)) from None

    print(json.dumps(publish(args.platform, args.tag, args.source_sha, args.directory.resolve(), api, args.release_id)))


if __name__ == "__main__": main()
