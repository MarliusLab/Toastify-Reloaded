#!/usr/bin/env python3
"""Validate native Linux/macOS Velopack feeds and retrieve immutable prior baselines."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import tempfile
import time
from urllib.error import HTTPError
from urllib.parse import quote, urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
REPO = "MarliusLab/Toastify-Reloaded"
CONFIG = {
    "linux": ("ToastifyReloadedLinux", ("linux-x64", "linux-arm64"), "SHA256SUMS.txt"),
    "macos": ("ToastifyReloadedMac", ("osx-x64", "osx-arm64"), "SHA256SUMS-macOS.txt"),
}
MAX_ASSET_BYTES = 2 * 1024 * 1024 * 1024
MAX_JSON_BYTES = 16 * 1024 * 1024
DOWNLOAD_SECONDS = 300
ASSET_HOSTS = {"github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"}


def trusted_asset_url(url):
    parsed = urlsplit(url)
    return (parsed.scheme == "https" and parsed.hostname in ASSET_HOSTS and
            parsed.port in (None, 443) and parsed.username is None and parsed.password is None)


class AssetRedirects(HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, newurl):
        if not trusted_asset_url(newurl): raise ValueError("Untrusted native asset redirect")
        redirected = super().redirect_request(request, fp, code, message, headers, newurl)
        if redirected is not None:
            for name in list(redirected.headers) + list(redirected.unredirected_hdrs):
                if name.lower() in ("authorization", "proxy-authorization"): redirected.remove_header(name)
        return redirected


class NoRedirects(HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, newurl):
        raise ValueError("Unexpected GitHub API redirect")


def read_bounded(response, limit, deadline, output=None):
    """read1 avoids an indefinitely trickled large read; each socket also times out."""
    data = bytearray() if output is None else None
    received = 0
    read = getattr(response, "read1", response.read)
    while True:
        if time.monotonic() >= deadline: raise TimeoutError("Native download deadline exceeded")
        block = read(min(1024 * 1024, limit - received + 1))
        if time.monotonic() >= deadline: raise TimeoutError("Native download deadline exceeded")
        if not block: break
        received += len(block)
        if received > limit: raise ValueError("Native response exceeds its permitted size")
        if output is None: data.extend(block)
        else: output.write(block)
    return bytes(data) if output is None else received

def digest(path, algorithm="sha256"):
    h = hashlib.new(algorithm)
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""): h.update(block)
    return h.hexdigest()

def version_key(value):
    match = re.fullmatch(r"(\d+)\.(\d+)\.(\d+)(?:-(preview|rc)\.([1-9][0-9]*))?", value)
    if not match: raise ValueError("Invalid native package version")
    return tuple(map(int, match.group(1, 2, 3))) + ({None: 2, "rc": 1, "preview": 0}[match[4]], int(match[5] or 0))

def tag_version(tag, platform):
    match = re.fullmatch(r"v(\d+\.\d+\.\d+)-" + ("linux" if platform == "linux" else "macos") + r"(?:-(preview|rc)\.([1-9][0-9]*))?", tag)
    return None if not match else match[1] + (f"-{match[2]}.{match[3]}" if match[2] else "")

def trusted_name(name):
    return isinstance(name, str) and bool(re.fullmatch(r"[A-Za-z0-9_.-]+", name)) and name not in (".", "..")

def legacy_names(platform, version):
    if platform == "linux":
        return {f"ToastifyReloaded-Linux-{arch}.{suffix}" for arch in ("x64", "arm64") for suffix in ("tar.gz", "AppImage")} | {f"toastify-reloaded_{version}_{arch}.deb" for arch in ("amd64", "arm64")}
    return {f"ToastifyReloaded-macOS-{arch}.{suffix}" for arch in ("x64", "arm64") for suffix in ("zip", "dmg")}

def package_names(platform, version, runtime):
    package_id = CONFIG[platform][0]
    return {f"{package_id}-{version}-{runtime}-full.nupkg", f"releases.{runtime}.json", f"RELEASES-{runtime}"}

def validate_channel(directory, platform, version, runtime, prune=False):
    package_id = CONFIG[platform][0]
    feed_path = directory / f"releases.{runtime}.json"
    feed = json.loads(feed_path.read_text(encoding="utf-8-sig"))
    entries = [entry for entry in feed["Assets"] if entry.get("Version") == version]
    kinds = [entry.get("Type", "").lower() for entry in entries]
    if kinds.count("full") != 1 or len(kinds) != len(set(kinds)) or set(kinds) - {"full", "delta"}:
        raise ValueError("Native feed requires one current full and at most one delta")
    names = {feed_path.name, f"RELEASES-{runtime}"}
    for entry in entries:
        kind = entry["Type"].lower()
        expected = f"{package_id}-{version}-{runtime}-{kind}.nupkg"
        if entry.get("PackageId") != package_id or entry.get("FileName") != expected:
            raise ValueError("Native package identity/channel differs")
        path = directory / expected
        if not path.is_file() or entry.get("Size") != path.stat().st_size or entry.get("SHA256", "").lower() != digest(path) or entry.get("SHA1", "").lower() != digest(path, "sha1"):
            raise ValueError("Native feed size/hash differs from package")
        with zipfile.ZipFile(path) as archive:
            nuspecs = [name for name in archive.namelist() if name.endswith(".nuspec") and "/" not in name]
            if len(nuspecs) != 1: raise ValueError("Missing package manifest")
            metadata = ET.fromstring(archive.read(nuspecs[0]))
            values = {node.tag.rsplit("}", 1)[-1]: node.text for node in metadata.iter()}
            if values.get("id") != package_id or values.get("version") != version or values.get("channel") != runtime or values.get("rid") != runtime or values.get("os") != ("linux" if platform == "linux" else "osx"):
                raise ValueError("Native NuGet manifest differs")
        names.add(expected)
    lines = [f"{entry['SHA1']} {entry['FileName']} {entry['Size']}" for entry in entries]
    if prune:
        feed["Assets"] = entries
        feed_path.write_text(json.dumps(feed, indent=2) + "\n", encoding="utf-8")
        (directory / f"RELEASES-{runtime}").write_text("\n".join(lines) + "\n", encoding="utf-8")
    else:
        if (directory / f"RELEASES-{runtime}").read_text(encoding="utf-8-sig").splitlines() != lines:
            raise ValueError("Native RELEASES differs from JSON feed")
    return names

def finalize(directory, platform, version, runtime=None):
    runtimes = (runtime,) if runtime else CONFIG[platform][1]
    names = set()
    for rid in runtimes:
        if rid not in CONFIG[platform][1]: raise ValueError("Wrong native architecture")
        names |= validate_channel(directory, platform, version, rid, prune=True)
    legacy = legacy_names(platform, version)
    if runtime:
        arch = runtime.rsplit("-", 1)[1]
        legacy = {name for name in legacy if (arch in name or ("amd64" if arch == "x64" else "arm64") in name) and (directory / name).is_file()}
    for name in legacy:
        if not (directory / name).is_file() or (directory / name).stat().st_size <= 0: raise ValueError("Missing native distribution: " + name)
    names |= legacy
    checksum = f"SHA256SUMS-{runtime}.txt" if runtime else CONFIG[platform][2]
    (directory / checksum).write_text("".join(f"{digest(directory / name)}  {name}\n" for name in sorted(names)), encoding="utf-8")
    return {"platform": platform, "version": version, "asset_count": len(names) + 1, "names": sorted(names | {checksum}), "deltas": sorted(name for name in names if name.endswith("-delta.nupkg"))}

def request_json(path):
    if not isinstance(path, str) or not path.startswith(f"repos/{REPO}/") or "#" in path or "\\" in path:
        raise ValueError("Untrusted native API request")
    headers = {"User-Agent": "ToastifyNativeRelease", "Accept": "application/vnd.github+json"}
    if os.environ.get("GH_TOKEN"): headers["Authorization"] = "Bearer " + os.environ["GH_TOKEN"]
    deadline = time.monotonic() + 60
    try:
        with build_opener(NoRedirects()).open(Request("https://api.github.com/" + path, headers=headers), timeout=30) as response:
            return json.loads(read_bounded(response, MAX_JSON_BYTES, deadline))
    except HTTPError as error:
        if error.code == 404: return None
        raise

def validate_asset(asset, tag):
    if not isinstance(asset, dict): raise ValueError("Malformed native baseline asset")
    name = asset.get("name")
    if not trusted_name(name): raise ValueError("Untrusted native baseline asset name")
    expected = f"https://github.com/{REPO}/releases/download/{quote(tag, safe='')}/{name}"
    value = asset.get("digest")
    if (asset.get("browser_download_url") != expected or asset.get("state") != "uploaded" or
            type(asset.get("size")) is not int or not 0 < asset["size"] <= MAX_ASSET_BYTES or
            not isinstance(value, str) or not re.fullmatch(r"sha256:[0-9a-fA-F]{64}", value)):
        raise ValueError("Untrusted native baseline asset")
    return expected


def download(asset, destination, tag):
    expected = validate_asset(asset, tag)
    destination = Path(destination)
    if destination.name != asset["name"] or destination.is_symlink(): raise ValueError("Unexpected native download path")
    deadline = time.monotonic() + DOWNLOAD_SECONDS
    temporary = None
    try:
        # Never send the API token to the release-asset/CDN download chain.
        with build_opener(AssetRedirects()).open(Request(expected, headers={"User-Agent": "ToastifyNativeRelease"}), timeout=30) as response:
            if not trusted_asset_url(response.url): raise ValueError("Untrusted native asset response")
            length = response.headers.get("Content-Length")
            if length is not None and (not length.isdecimal() or int(length) != asset["size"]):
                raise ValueError("Native baseline response size differs")
            with tempfile.NamedTemporaryFile(dir=destination.parent, prefix=".native-download-", delete=False) as output:
                temporary = Path(output.name)
                received = read_bounded(response, asset["size"], deadline, output)
        if received != asset["size"] or digest(temporary) != asset["digest"][7:].lower():
            raise ValueError("Native baseline download hash differs")
        temporary.replace(destination)
        temporary = None
    finally:
        if temporary is not None: temporary.unlink(missing_ok=True)


def parse_sums(path):
    result = {}
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        match = re.fullmatch(r"([0-9a-fA-F]{64})[ \t]+\*?([A-Za-z0-9_.-]+)", line)
        if not match or not trusted_name(match[2]) or match[2] in result:
            raise ValueError("Malformed or duplicate native checksum entry")
        result[match[2]] = match[1].lower()
    return result


def baseline_source(tag, expected):
    if not isinstance(expected, str) or not re.fullmatch(r"[0-9a-f]{40}", expected):
        raise ValueError("Native baseline source must be a full commit SHA")
    ref = request_json(f"repos/{REPO}/git/ref/tags/{quote(tag, safe='')}")
    node = ref.get("object") if isinstance(ref, dict) else None
    for depth in range(6):
        if not isinstance(node, dict) or not isinstance(node.get("sha"), str) or not re.fullmatch(r"[0-9a-f]{40}", node["sha"]): break
        if node.get("type") == "commit":
            if node["sha"] == expected: return expected
            break
        if node.get("type") != "tag" or depth == 5: break
        obj = request_json(f"repos/{REPO}/git/tags/{node['sha']}")
        node = obj.get("object") if isinstance(obj, dict) else None
    raise ValueError("Native baseline source tag is not immutable/verifiable")

def prepare_base(output, platform, version, runtime):
    version_key(version)
    if platform not in CONFIG or runtime not in CONFIG[platform][1]: raise ValueError("Wrong native runtime")
    output.mkdir(parents=True, exist_ok=True)
    if any(output.iterdir()): raise ValueError("Native baseline output must be empty")
    candidates = []
    for release in request_json(f"repos/{REPO}/releases?per_page=100") or []:
        if not isinstance(release, dict): raise ValueError("Malformed native release list")
        tag = release.get("tag_name", "")
        previous = tag_version(tag, platform)
        if not previous or release.get("draft") or version_key(previous) >= version_key(version): continue
        if version_key(version)[3] == 2 and version_key(previous)[3] != 2: continue
        rows = release.get("assets", [])
        if not isinstance(rows, list) or any(not isinstance(asset, dict) for asset in rows): raise ValueError("Malformed native baseline asset list")
        assets = {asset.get("name"): asset for asset in rows}
        required = legacy_names(platform, previous) | {CONFIG[platform][2]}
        for rid in CONFIG[platform][1]: required |= package_names(platform, previous, rid)
        if not required.issubset(assets): continue  # Legacy releases cannot produce real deltas.
        optional = {f"{CONFIG[platform][0]}-{previous}-{rid}-delta.nupkg" for rid in CONFIG[platform][1]}
        if len(assets) != len(rows) or set(assets) - required - optional: raise ValueError("Native baseline asset set is ambiguous")
        if (release.get("draft") is not False or type(release.get("id")) is not int or release["id"] <= 0 or
                release.get("prerelease") is not ("-rc." in previous or "-preview." in previous) or
                release.get("html_url") != f"https://github.com/{REPO}/releases/tag/{tag}"):
            raise ValueError("Native baseline release provenance differs")
        for asset in rows: validate_asset(asset, tag)
        candidates.append((version_key(previous), release, previous, assets))
    if not candidates: return {"first_baseline": True, "runtime": runtime}
    _, release, previous, assets = max(candidates, key=lambda item: item[0])
    tag = release["tag_name"]
    source_sha = baseline_source(tag, release.get("target_commitish"))
    checksum = CONFIG[platform][2]
    download(assets[checksum], output / checksum, tag)
    sums = parse_sums(output / checksum)
    if set(sums) != set(assets) - {checksum} or any(sums[name] != assets[name]["digest"][7:].lower() for name in sums):
        raise ValueError("Native baseline checksum manifest differs")
    for rid in CONFIG[platform][1]:
        for name in (f"releases.{rid}.json", f"RELEASES-{rid}"): download(assets[name], output / name, tag)
        feed = json.loads((output / f"releases.{rid}.json").read_text(encoding="utf-8-sig"))
        entries = feed.get("Assets")
        if not isinstance(entries, list) or any(not isinstance(entry, dict) for entry in entries): raise ValueError("Malformed baseline feed")
        expected_names = {f"{CONFIG[platform][0]}-{previous}-{rid}-{kind}.nupkg" for kind in ("full", "delta")} & set(assets)
        if len(entries) != len(expected_names) or {entry.get("FileName") for entry in entries} != expected_names:
            raise ValueError("Native baseline feed has missing or historical assets")
        for entry in entries:
            name = entry["FileName"]
            kind = "Delta" if name.endswith("-delta.nupkg") else "Full"
            if (entry.get("PackageId") != CONFIG[platform][0] or entry.get("Version") != previous or entry.get("Type") != kind or
                    entry.get("Size") != assets[name]["size"] or not isinstance(entry.get("SHA1"), str) or
                    not re.fullmatch(r"[0-9a-fA-F]{40}", entry["SHA1"]) or
                    not isinstance(entry.get("SHA256"), str) or entry["SHA256"].lower() != assets[name]["digest"][7:].lower()):
                raise ValueError("Native baseline feed differs from its uploaded assets")
        lines = [f"{entry['SHA1']} {entry['FileName']} {entry['Size']}" for entry in entries]
        if (output / f"RELEASES-{rid}").read_text(encoding="utf-8-sig").splitlines() != lines:
            raise ValueError("Native baseline RELEASES differs from its feed")
    full_name = f"{CONFIG[platform][0]}-{previous}-{runtime}-full.nupkg"
    download(assets[full_name], output / full_name, tag)
    # Earlier deltas belong to their original release. Only the verified full
    # baseline is needed for generating this release; do not leave dangling
    # previous delta entries in the local producer input.
    feed_path = output / f"releases.{runtime}.json"
    feed = json.loads(feed_path.read_text(encoding="utf-8-sig"))
    full = [entry for entry in feed["Assets"] if entry.get("Type", "").lower() == "full" and entry.get("Version") == previous]
    if len(full) != 1: raise ValueError("Native baseline full entry is ambiguous")
    feed["Assets"] = full
    feed_path.write_text(json.dumps(feed, indent=2) + "\n", encoding="utf-8")
    (output / f"RELEASES-{runtime}").write_text(f"{full[0]['SHA1']} {full[0]['FileName']} {full[0]['Size']}\n", encoding="utf-8")
    validate_channel(output, platform, previous, runtime)
    # Only this runtime's base package/feeds are producer inputs.
    for rid in CONFIG[platform][1]:
        if rid != runtime:
            (output / f"releases.{rid}.json").unlink()
            (output / f"RELEASES-{rid}").unlink()
    (output / checksum).unlink()
    return {"first_baseline": False, "runtime": runtime, "previous_version": previous, "source_sha": source_sha}

def verify_inputs(directory, platform):
    manifests = list(directory.glob("SHA256SUMS*.txt"))
    if not manifests: raise ValueError("The tested artifacts have no checksum manifest")
    seen = set()
    for manifest in manifests:
        for line in manifest.read_text(encoding="utf-8-sig").splitlines():
            value, name = line.split(maxsplit=1)
            name = name.lstrip(" *")
            if not trusted_name(name) or not re.fullmatch(r"[0-9a-fA-F]{64}", value): raise ValueError("Invalid artifact checksum line")
            path = directory / name
            if not path.is_file() or path.is_symlink() or digest(path) != value.lower(): raise ValueError("Tested artifact checksum differs: " + name)
            seen.add(name)
    for rid in CONFIG[platform][1]:
        if f"releases.{rid}.json" not in seen: raise ValueError("Missing tested native architecture")

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", required=True, choices=CONFIG)
    parser.add_argument("--version", required=True)
    parser.add_argument("--directory", type=Path, default=ROOT / "dist")
    parser.add_argument("--runtime")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--verify-inputs", action="store_true")
    parser.add_argument("--stage-directory", type=Path)
    action = parser.add_mutually_exclusive_group(required=True)
    action.add_argument("--prepare-base", action="store_true")
    action.add_argument("--finalize", action="store_true")
    args = parser.parse_args()
    version_key(args.version)
    if args.prepare_base:
        if not args.output or not args.runtime: parser.error("--prepare-base requires --output and --runtime")
        result = prepare_base(args.output.resolve(), args.platform, args.version, args.runtime)
    else:
        directory = args.directory.resolve()
        if args.verify_inputs: verify_inputs(directory, args.platform)
        result = finalize(directory, args.platform, args.version, args.runtime)
        if args.stage_directory:
            target = args.stage_directory.resolve()
            if not target.is_relative_to(ROOT) or target == directory or (target.exists() and any(target.iterdir())): raise ValueError("Publication staging must be empty and inside the checkout")
            target.mkdir(parents=True, exist_ok=True)
            for name in result["names"]:
                source = directory / name
                if source.is_symlink(): raise ValueError("Publication assets must be regular files")
                shutil.copyfile(source, target / name)
    print(json.dumps(result))

if __name__ == "__main__": main()
