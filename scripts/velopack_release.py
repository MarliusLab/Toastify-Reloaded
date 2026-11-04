"""Shared, versioned publication contract for Windows Velopack assets."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET
import zipfile

PACK_ID = "ToastifyReloaded"
VPK_VERSION = "1.2.161"
RIDS = ("win-x64", "win-arm64")
CHECKSUMS = "SHA256SUMS-Windows.txt"
FIRST_VELOPACK_VERSION = (1, 3, 10)


def windows_version(tag: str) -> tuple[int, int, int] | None:
    match = re.fullmatch(r"v\.?(\d+)\.(\d+)\.(\d+)", tag)
    return tuple(map(int, match.groups())) if match else None


def package_name(version: str, rid: str, kind: str = "full") -> str:
    if rid not in RIDS or kind not in ("full", "delta") or not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError("Invalid package identity")
    return f"{PACK_ID}-{version}-{rid}-{kind}.nupkg"


def windows_assets(tag: str) -> set[str]:
    assets = {f"ToastifyReloaded-Setup-{rid}.exe" for rid in RIDS} | {CHECKSUMS}
    version = windows_version(tag)
    if version is None or version < FIRST_VELOPACK_VERSION:
        return assets
    text = ".".join(map(str, version))
    for rid in RIDS:
        assets.update({package_name(text, rid), f"{PACK_ID}-{rid}.msi",
                       f"releases.{rid}.json", f"RELEASES-{rid}"})
    return assets


def digest(path: Path, algorithm: str) -> str:
    value = hashlib.new(algorithm)
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def verify_entry(entry: dict, directory: Path, version: str, rid: str) -> Path:
    kind = entry.get("Type", "").lower()
    if kind not in ("full", "delta"):
        raise ValueError("Unexpected Velopack feed asset type")
    name = package_name(version, rid, kind)
    if entry.get("FileName") != name or entry.get("PackageId") != PACK_ID or entry.get("Version") != version:
        raise ValueError("Velopack feed identity or architecture mismatch")
    path = directory / name
    if not path.is_file() or path.stat().st_size <= 0 or entry.get("Size") != path.stat().st_size:
        raise ValueError("Velopack feed references a missing or incorrectly sized package: " + name)
    for key, algorithm, length in (("SHA1", "sha1", 40), ("SHA256", "sha256", 64)):
        value = entry.get(key, "")
        if not isinstance(value, str) or not re.fullmatch(r"[0-9a-fA-F]{" + str(length) + "}", value):
            raise ValueError("Missing package digest: " + key)
        if digest(path, algorithm) != value.lower():
            raise ValueError("Package digest mismatch: " + name)
    with zipfile.ZipFile(path) as archive:
        nuspecs = [item for item in archive.namelist() if item.endswith(".nuspec")]
        if len(nuspecs) != 1:
            raise ValueError("Package must contain exactly one NuGet manifest")
        manifest = ET.fromstring(archive.read(nuspecs[0]))
        metadata = {node.tag.rsplit("}", 1)[-1]: node.text for node in manifest.iter()}
        if metadata.get("id") != PACK_ID or metadata.get("version") != version:
            raise ValueError("NuGet package identity differs from its feed")
    return path


def finalize_stage(directory: Path, version: str) -> dict:
    """Prune copied historical feed entries, then hash only this release's assets."""
    if windows_version("v" + version) is None:
        raise ValueError("Invalid release version")
    names = windows_assets("v" + version) - {CHECKSUMS}
    deltas = []
    for rid in RIDS:
        feed_path = directory / f"releases.{rid}.json"
        feed = json.loads(feed_path.read_text(encoding="utf-8-sig"))
        if not isinstance(feed.get("Assets"), list):
            raise ValueError("Invalid Velopack feed")
        current = [entry for entry in feed["Assets"] if entry.get("Version") == version]
        kinds = [entry.get("Type", "").lower() for entry in current]
        if kinds.count("full") != 1 or len(kinds) != len(set(kinds)):
            raise ValueError("Each architecture requires one current full package and at most one delta")
        for entry in current:
            path = verify_entry(entry, directory, version, rid)
            names.add(path.name)
            if entry["Type"].lower() == "delta":
                deltas.append(path.name)
        # The GitHub source resolves prior versions in their original releases;
        # do not republish old packages or leave dangling links in the new feed.
        feed["Assets"] = current
        feed_path.write_text(json.dumps(feed, indent=2) + "\n", encoding="utf-8")
        release_lines = [f"{entry['SHA1']} {entry['FileName']} {entry['Size']}" for entry in current]
        (directory / f"RELEASES-{rid}").write_text("\n".join(release_lines) + "\n", encoding="utf-8")
    for name in names:
        if not (directory / name).is_file() or (directory / name).stat().st_size <= 0:
            raise ValueError("Missing publication asset: " + name)
    unexpected = {path.name for path in directory.iterdir() if path.is_file()} - names - {CHECKSUMS}
    if unexpected:
        raise ValueError("Unexpected files in the publication staging directory: " + ", ".join(sorted(unexpected)))
    (directory / CHECKSUMS).write_text("".join(f"{digest(directory / name, 'sha256')}  {name}\n"
                                                  for name in sorted(names)), encoding="utf-8")
    return {"version": version, "channels": list(RIDS), "asset_count": len(names) + 1,
            "delta_packages": deltas, "first_velopack_release": windows_version("v" + version) == FIRST_VELOPACK_VERSION}
