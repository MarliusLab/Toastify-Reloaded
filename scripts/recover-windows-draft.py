#!/usr/bin/env python3
"""One-time recovery of the already built Windows 1.3.10 draft; no new binaries."""
from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import tempfile
from urllib.error import HTTPError
from urllib.request import Request, urlopen

from velopack_release import digest, validate_stage, windows_assets

REPOSITORY = "MarliusLab/Toastify-Reloaded"
RELEASE_ID = 433628164
SOURCE_SHA = "d28e935887531d35b90c123a0e277888b4dea625"
BUILD_RUN_ID = 41792181155
TAG = "v1.3.10"
# Immutable identities/digests observed on the failed, fully uploaded draft.
# Never rebuild, replace or adopt other assets in this recovery.
ASSETS = {
    "RELEASES-win-arm64": (708660106, 96, "1c192bf1599b0c4f21351685ccb7ae86c86b529d218ec99f42ade1fda935337b"),
    "RELEASES-win-x64": (708660113, 94, "27798258b7934ff79b8f59f7fbf687d7c0e41734484095bc915a00326055fe61"),
    "releases.win-arm64.json": (708660108, 4922, "1bb05295384905b77b9efb69b3375c175641530907f4b599219cd621ea9a68ff"),
    "releases.win-x64.json": (708660111, 4920, "522ab56eb678e58cd51094d7dcfbacdb2f5b97c73573af852c8c26915dfbba92"),
    "SHA256SUMS-Windows.txt": (708660112, 972, "4bc923c0c304c9ab362bbb43526e55a0115ac04ac369729e348a0c85199474d1"),
    "ToastifyReloaded-1.3.10-win-arm64-full.nupkg": (708660115, 77616878, "63ba1101b285f9fb6835b0ac06246078516f5d1c5fa0faa5015f5c5e3835b4ce"),
    "ToastifyReloaded-1.3.10-win-x64-full.nupkg": (708660114, 82300296, "c30595c7e68461015c3aa5e60edc11d4e59654ebc2b4d116ff0dab65434a6e77"),
    "ToastifyReloaded-Setup-win-arm64.exe": (708660116, 140087799, "c5c5fa1409cebda73174cf6bfa3e7552c16eb6db4e12f968e06626bee109d1d4"),
    "ToastifyReloaded-Setup-win-x64.exe": (708660117, 150003625, "6849a460ecb366e41d04139d4e670d2ddca99a9bb10119d611b8e7e72a71bafe"),
    "ToastifyReloaded-win-arm64.msi": (708660110, 65520188, "af188b34a287dc93e5a50be29cde5e575eb16fa87b208c81309e0602ca4c4c82"),
    "ToastifyReloaded-win-x64.msi": (708660109, 71004748, "c4f35f88521f9a0fd67c3606f42176692cfa0b9633ddb7a8a2a0e3100e23aff3"),
}


def load_helper(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


base = load_helper("draft_asset_transport", "prepare-velopack-base.py")
publisher = load_helper("draft_publisher", "publish-windows-release.py")


def verify_build(run):
    if (run.get("id") != BUILD_RUN_ID or run.get("head_sha") != SOURCE_SHA or
            run.get("status") != "completed" or run.get("conclusion") != "success" or
            run.get("event") != "push" or run.get("head_branch") != "main" or
            run.get("name") != "build" or run.get("path") != ".github/workflows/build.yml" or
            run.get("repository", {}).get("full_name") != REPOSITORY or
            run.get("head_repository", {}).get("full_name") != REPOSITORY):
        raise ValueError("Original Windows main build does not have the pinned successful provenance")


def verify_recovery_release(release):
    if (release.get("id") != RELEASE_ID or release.get("tag_name") != TAG or
            release.get("target_commitish") != SOURCE_SHA or release.get("prerelease") is not False or
            type(release.get("draft")) is not bool):
        raise ValueError("Recovery release differs from the pinned original draft")
    assets = release.get("assets", [])
    if len(assets) != len(ASSETS) or {asset.get("name") for asset in assets} != set(ASSETS):
        raise ValueError("Original draft no longer has the exact eleven pinned assets")
    for asset in assets:
        expected_id, size, sha256 = ASSETS[asset["name"]]
        if (asset.get("id") != expected_id or asset.get("size") != size or
                asset.get("digest") != "sha256:" + sha256 or asset.get("state") != "uploaded"):
            raise ValueError("Original draft asset was replaced or changed: " + asset["name"])
    return assets


def recover(directory, api, download):
    verify_build(api("actions/runs/" + str(BUILD_RUN_ID)) or {})
    publisher.verify_tag(api, TAG, SOURCE_SHA, optional=True)
    release = api("releases/" + str(RELEASE_ID)) or {}
    assets = verify_recovery_release(release)
    if any(directory.iterdir()):
        raise ValueError("Recovery destination must be empty")
    for asset in assets:
        destination = directory / asset["name"]
        download(asset, destination)
        _, size, sha256 = ASSETS[asset["name"]]
        if destination.stat().st_size != size or digest(destination, "sha256") != sha256:
            raise ValueError("Downloaded recovery asset differs from the pinned bytes")
    if set(path.name for path in directory.iterdir()) != windows_assets(TAG):
        raise ValueError("Recovery downloaded an unexpected publication set")
    validate_stage(directory, TAG[1:])
    # Validation is read-only: downloaded metadata stays byte-identical,
    # including the Windows producer's original line endings.
    for name, (_, size, sha256) in ASSETS.items():
        path = directory / name
        if path.stat().st_size != size or digest(path, "sha256") != sha256:
            raise ValueError("Original uploaded feed/checksum contract is not canonical: " + name)
    verify_recovery_release(api("releases/" + str(RELEASE_ID)) or {})
    return publisher.publish(REPOSITORY, TAG, SOURCE_SHA, directory, api, release_id=RELEASE_ID)


def github_api(path, method="GET", body=None):
    request = Request("https://api.github.com/repos/" + REPOSITORY + "/" + path,
        data=json.dumps(body).encode() if body is not None else None, method=method,
        headers={"Authorization": "Bearer " + os.environ["GH_TOKEN"],
                 "Accept": "application/vnd.github+json", "Content-Type": "application/json",
                 "User-Agent": "ToastifyPinnedDraftRecovery", "X-GitHub-Api-Version": "2022-11-28"})
    try:
        with urlopen(request, timeout=30) as response:
            return json.load(response)
    except HTTPError as error:
        if method == "GET" and error.code == 404:
            return None
        raise RuntimeError("GitHub recovery API failed: HTTP " + str(error.code)) from None


if __name__ == "__main__":
    if (os.environ.get("GITHUB_REPOSITORY") != REPOSITORY or
            os.environ.get("GITHUB_EVENT_NAME") != "push" or os.environ.get("GITHUB_REF") != "refs/heads/main"):
        raise ValueError("One-time recovery is authorized only by a same-repository main push")
    root = Path(__file__).resolve().parents[1]
    artifacts = root / "artifacts"
    artifacts.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="windows-draft-recovery-", dir=artifacts) as temporary:
        report = recover(Path(temporary), github_api,
                         lambda asset, target: base.download_api_asset(REPOSITORY, asset, target))
    report.update(release_id=RELEASE_ID, original_build_run=BUILD_RUN_ID)
    (artifacts / "windows-draft-recovery.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report))
