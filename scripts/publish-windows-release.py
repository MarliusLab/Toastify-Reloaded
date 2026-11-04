#!/usr/bin/env python3
"""Publish a fully uploaded, digest-verified Velopack draft; never move a tag."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import time
from urllib.error import HTTPError
from urllib.parse import quote
from urllib.request import Request, urlopen

from velopack_release import CHECKSUMS, digest, validate_stage, windows_assets


def verify_uploaded_release(release, directory: Path, tag: str, source_sha: str, draft: bool):
    if (release.get("tag_name") != tag or release.get("target_commitish") != source_sha or
            release.get("draft") is not draft or release.get("prerelease") is not False):
        raise ValueError("Release is not the expected stable publication at the exact checked source SHA")
    expected = {path.name: path for path in directory.iterdir() if path.is_file()}
    if not windows_assets(tag).issubset(expected) or CHECKSUMS not in expected:
        raise ValueError("Local publication stage is incomplete")
    assets = {asset["name"]: asset for asset in release.get("assets", [])}
    if len(assets) != len(release.get("assets", [])) or set(assets) != set(expected):
        raise ValueError("Remote release has missing, duplicate or unexpected assets")
    for name, path in expected.items():
        asset = assets[name]
        if asset.get("state") != "uploaded" or asset.get("size") != path.stat().st_size:
            raise ValueError("Remote asset is incomplete: " + name)
        if asset.get("digest", "").lower() != "sha256:" + digest(path, "sha256"):
            raise ValueError("Remote asset digest differs from the validated local file: " + name)
    return [{"name": name, "size": assets[name]["size"], "digest": assets[name]["digest"]}
            for name in sorted(assets)]


def verify_uploaded_draft(release, directory: Path, tag: str, source_sha: str):
    return verify_uploaded_release(release, directory, tag, source_sha, True)


def verify_tag(api, tag, source_sha, optional=False):
    ref = api("git/ref/tags/" + quote(tag, safe=""))
    if ref is None and optional:
        return
    obj = (ref or {}).get("object", {})
    for _ in range(5):
        if obj.get("type") == "commit":
            if obj.get("sha") != source_sha:
                raise ValueError("Release tag points to a different source commit")
            return
        if obj.get("type") != "tag":
            break
        obj = (api("git/tags/" + obj["sha"]) or {}).get("object", {})
    raise ValueError("Cannot verify release tag source")


def publish(repository, tag, source_sha, directory, api, delay=time.sleep, release_id=None):
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository) or not re.fullmatch(r"[0-9a-f]{40}", source_sha):
        raise ValueError("Invalid publication provenance")
    if not re.fullmatch(r"v\d+\.\d+\.\d+", tag):
        raise ValueError("Invalid Windows release tag")
    if release_id is not None and (type(release_id) is not int or release_id <= 0):
        raise ValueError("Invalid upload release ID")
    validate_stage(directory, tag[1:])
    verify_tag(api, tag, source_sha, optional=True)
    release_path = "releases/" + str(release_id) if release_id is not None else "releases/tags/" + quote(tag, safe="")
    # Asset digest metadata may briefly lag an otherwise completed upload.
    for attempt in range(6):
        release = api(release_path)
        if release is None:
            raise ValueError("Expected upload draft is missing")
        if release_id is not None and release.get("id") != release_id:
            raise ValueError("GitHub returned a different release ID")
        if release.get("draft") is False:
            assets = verify_uploaded_release(release, directory, tag, source_sha, False)
            verify_tag(api, tag, source_sha)
            if (api("releases/latest") or {}).get("tag_name") != tag:
                raise ValueError("Existing Windows publication is not latest")
            return {"release_url": release["html_url"], "source_sha": source_sha,
                    "assets": assets, "already_published": True}
        try:
            assets = verify_uploaded_draft(release, directory, tag, source_sha)
            break
        except ValueError:
            if attempt == 5:
                raise
            delay(3)
    result = api("releases/" + str(release["id"]), "PATCH", {"draft": False, "make_latest": "true"})
    if result.get("draft") is not False or result.get("prerelease") is not False or result.get("tag_name") != tag:
        raise ValueError("GitHub did not publish the expected stable release")
    verify_tag(api, tag, source_sha)
    if (api("releases/latest") or {}).get("tag_name") != tag:
        raise ValueError("Published Windows release was not selected as latest")
    return {"release_url": result["html_url"], "source_sha": source_sha, "assets": assets}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--repository", default="MarliusLab/Toastify-Reloaded")
    parser.add_argument("--tag", required=True)
    parser.add_argument("--sha", required=True)
    parser.add_argument("--release-id", type=int)
    parser.add_argument("--directory", type=Path, default=Path("dist/windows-release"))
    args = parser.parse_args()

    def api(path, method="GET", body=None):
        request = Request("https://api.github.com/repos/" + args.repository + "/" + path,
                          data=json.dumps(body).encode() if body is not None else None, method=method,
                          headers={"Authorization": "Bearer " + os.environ["GH_TOKEN"],
                                   "Accept": "application/vnd.github+json", "Content-Type": "application/json",
                                   "User-Agent": "ToastifyVerifiedPublisher", "X-GitHub-Api-Version": "2022-11-28"})
        try:
            with urlopen(request, timeout=30) as response:
                return json.load(response)
        except HTTPError as error:
            if method == "GET" and error.code == 404:
                return None
            raise RuntimeError("GitHub publication API failed: HTTP " + str(error.code)) from None

    print(json.dumps(publish(args.repository, args.tag, args.sha, args.directory.resolve(), api,
                             release_id=args.release_id)))
