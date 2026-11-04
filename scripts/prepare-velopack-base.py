#!/usr/bin/env python3
"""Download a verified, immutable previous Windows channel; never invent a delta base."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
from urllib.parse import quote, urlparse
from urllib.request import HTTPRedirectHandler, Request, build_opener, urlopen

from velopack_release import PACK_ID, RIDS, package_name, verify_entry, windows_assets, windows_version


def github_api(repository: str, path: str):
    headers = {"Accept": "application/vnd.github+json", "User-Agent": "ToastifyReleaseBase",
               "X-GitHub-Api-Version": "2022-11-28"}
    if os.environ.get("GH_TOKEN"):
        headers["Authorization"] = "Bearer " + os.environ["GH_TOKEN"]
    request = Request(f"https://api.github.com/repos/{repository}/{path}", headers=headers)
    with urlopen(request, timeout=30) as response:
        return json.load(response)


def download_checked(request, asset: dict, destination: Path, open_request=None):
    size = asset.get("size", 0)
    digest = asset.get("digest", "")
    if size <= 0 or not re.fullmatch(r"sha256:[0-9a-fA-F]{64}", digest or ""):
        raise ValueError("Previous Velopack release asset lacks verified size/SHA-256 metadata")
    actual = hashlib.sha256()
    received = 0
    partial = destination.with_name(destination.name + ".partial")
    try:
        with (open_request or urlopen)(request, timeout=60) as response, partial.open("xb") as output:
            while block := response.read(1024 * 1024):
                received += len(block)
                if received > size:
                    raise ValueError("Previous release asset exceeds its declared size")
                actual.update(block)
                output.write(block)
        if received != size or actual.hexdigest() != digest.split(":", 1)[1].lower():
            raise ValueError("Previous release asset failed its published digest/size check")
        partial.replace(destination)
    finally:
        if partial.exists():
            partial.unlink()


def download_asset(repository: str, asset: dict, destination: Path):
    url = asset["browser_download_url"]
    parsed = urlparse(url)
    if parsed.scheme != "https" or parsed.netloc != "github.com" or not parsed.path.startswith(f"/{repository}/releases/download/"):
        raise ValueError("Untrusted release asset URL")
    # Public assets need no API token, including during redirects.
    download_checked(Request(url, headers={"User-Agent": "ToastifyReleaseBase"}), asset, destination)


class TokenSafeAssetRedirect(HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        target = urlparse(new_url)
        if target.scheme != "https" or target.netloc not in {
                "github.com", "api.github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"}:
            raise ValueError("Untrusted private release asset redirect")
        redirected = super().redirect_request(request, response, code, message, headers, new_url)
        if redirected is not None:
            # The first API request is authenticated. Every redirect is a new,
            # token-free request to the presigned content URL.
            for name in list(redirected.headers):
                if name.lower() in ("authorization", "cookie"):
                    del redirected.headers[name]
            redirected.unredirected_hdrs.clear()
        return redirected


def download_api_asset(repository: str, asset: dict, destination: Path):
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Invalid asset repository")
    asset_id = asset.get("id")
    if type(asset_id) is not int or asset_id <= 0:
        raise ValueError("Invalid private release asset ID")
    # Construct the only authenticated origin ourselves. Never authenticate a
    # URL supplied by release metadata or a redirect response.
    request = Request(f"https://api.github.com/repos/{repository}/releases/assets/{asset_id}", headers={
        "Accept": "application/octet-stream", "Authorization": "Bearer " + os.environ["GH_TOKEN"],
        "User-Agent": "ToastifyDraftRecovery", "X-GitHub-Api-Version": "2022-11-28"})
    download_checked(request, asset, destination, build_opener(TokenSafeAssetRedirect()).open)


def prepare_base(repository: str, version: str, rid: str, output: Path, api, download):
    target = windows_version("v" + version)
    if target is None or rid not in RIDS or not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Invalid base request")
    output.mkdir(parents=True, exist_ok=True)
    if any(output.iterdir()):
        raise ValueError("Previous-release output must be empty to prevent stale delta bases")
    candidates = []
    for page in range(1, 4):
        releases = api(f"releases?per_page=100&page={page}")
        for release in releases:
            previous = windows_version(release.get("tag_name", ""))
            if previous is None or previous >= target or release.get("draft") or release.get("prerelease"):
                continue
            assets = {asset["name"]: asset for asset in release.get("assets", []) if asset.get("size", 0) > 0}
            old_version = ".".join(map(str, previous))
            if not windows_assets(release["tag_name"]).issubset(assets):
                continue
            if f"releases.{rid}.json" in assets and package_name(old_version, rid) in assets:
                candidates.append((previous, release, assets))
        if len(releases) < 100:
            break
    if not candidates:
        result = {"channel": rid, "target_version": version, "base_available": False,
                  "reason": "No earlier complete published Velopack release for this channel"}
    else:
        _, release, assets = max(candidates, key=lambda item: item[0])
        tag = release["tag_name"]
        previous_version = ".".join(map(str, windows_version(tag)))
        obj = api("git/ref/tags/" + quote(tag, safe=""))["object"]
        for _ in range(5):
            if obj.get("type") == "commit":
                break
            if obj.get("type") != "tag":
                raise ValueError("Cannot verify previous release tag")
            obj = api("git/tags/" + obj["sha"])["object"]
        if obj.get("type") != "commit" or not re.fullmatch(r"[0-9a-f]{40}", obj.get("sha", "")):
            raise ValueError("Previous release has no immutable source commit")
        feed_name = f"releases.{rid}.json"
        full_name = package_name(previous_version, rid)
        download(assets[feed_name], output / feed_name)
        feed = json.loads((output / feed_name).read_text(encoding="utf-8-sig"))
        entries = [entry for entry in feed.get("Assets", []) if entry.get("Version") == previous_version and
                   entry.get("Type", "").lower() == "full" and entry.get("FileName") == full_name]
        if len(entries) != 1:
            raise ValueError("Previous channel feed has no unique matching full package")
        download(assets[full_name], output / full_name)
        verify_entry(entries[0], output, previous_version, rid)
        feed["Assets"] = entries
        (output / feed_name).write_text(json.dumps(feed, indent=2) + "\n", encoding="utf-8")
        entry = entries[0]
        (output / f"RELEASES-{rid}").write_text(f"{entry['SHA1']} {full_name} {entry['Size']}\n", encoding="utf-8")
        result = {"channel": rid, "target_version": version, "base_available": True,
                  "source_tag": tag, "source_commit": obj["sha"], "package": full_name,
                  "package_sha256": assets[full_name]["digest"]}
    (output / "base.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--repository", default="MarliusLab/Toastify-Reloaded")
    parser.add_argument("--version", required=True)
    parser.add_argument("--runtime", choices=RIDS, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(prepare_base(args.repository, args.version, args.runtime, args.output.resolve(),
                    lambda path: github_api(args.repository, path),
                    lambda asset, target: download_asset(args.repository, asset, target))))
