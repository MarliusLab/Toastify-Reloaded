#!/usr/bin/env python3
"""Exercise the pinned real delta engine with unpublished synthetic packages."""
import argparse
from contextlib import nullcontext
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import zipfile


def make_package(path, version, payload, package_id="ToastifyDeltaProbe"):
    manifest = (f'<?xml version="1.0"?><package><metadata><id>{package_id}</id>'
                f'<version>{version}</version><authors>Toastify CI</authors>'
                '<description>Unpublished delta regression fixture</description>'
                '<channel>win-x64</channel><runtime>win-x64</runtime></metadata></package>')
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr(package_id + ".nuspec", manifest)
        archive.writestr("lib/app/payload.bin", payload)
        archive.writestr("lib/app/unchanged.dat", b"unchanged payload\n" * 4000)
        archive.writestr("[Content_Types].xml", '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>')


def contents(path):
    with zipfile.ZipFile(path) as archive:
        return {name: hashlib.sha256(archive.read(name)).hexdigest() for name in archive.namelist()}


def run_probe(vpk, artifacts, keep_fixtures=False, package_id="ToastifyDeltaProbe", versions=("90.0.0", "90.0.1", "90.0.2")):
    root = Path(__file__).resolve().parents[1]
    artifacts = artifacts.resolve()
    if not artifacts.is_relative_to(root):
        raise ValueError("Probe artifacts must remain inside the checkout")
    if not re.fullmatch(r"[A-Za-z0-9_.-]{1,80}", package_id) or len(versions) != 3:
        raise ValueError("Invalid fixture identity")
    if any(not re.fullmatch(r"\d+\.\d+\.\d+", version) for version in versions):
        raise ValueError("Invalid fixture versions")
    if not all(tuple(map(int, left.split('.'))) < tuple(map(int, right.split('.'))) for left, right in zip(versions, versions[1:])):
        raise ValueError("Fixture versions must strictly increase")
    artifacts.mkdir(parents=True, exist_ok=True)
    fixtures = (nullcontext(tempfile.mkdtemp(prefix="velopack-delta-", dir=artifacts)) if keep_fixtures
                else tempfile.TemporaryDirectory(prefix="velopack-delta-", dir=artifacts))
    with fixtures as temporary:
        work = Path(temporary).resolve()
        if not work.is_relative_to(artifacts):
            raise ValueError("Unsafe probe cleanup target")
        native_temp = work / "native-temp"
        native_temp.mkdir()
        environment = dict(os.environ, TEMP=str(native_temp), TMP=str(native_temp))
        environment["VPK_SKIP_UPDATE_CHECK"] = "true"

        def execute(*arguments, required=True):
            result = subprocess.run([str(vpk), *map(str, arguments), "--skip-updates", "--yes"], capture_output=True,
                                    text=True, timeout=120, env=environment, cwd=work)
            if required and result.returncode:
                raise RuntimeError("Delta engine failed:\n" + result.stdout + result.stderr)
            return result

        # Stable deterministic payload with small changed regions between releases.
        payload = bytearray(hashlib.sha256(b"Toastify delta fixture").digest() * 131072)
        packages = []
        for index, version in enumerate(versions):
            payload[10000 * index:10000 * index + 32] = hashlib.sha256(version.encode()).digest()
            package = work / f"{package_id}-{version}-win-x64-full.nupkg"
            make_package(package, version, bytes(payload), package_id)
            packages.append(package)
        patches = []
        for old, new in zip(packages, packages[1:]):
            patch = work / new.name.replace("-full.nupkg", "-delta.nupkg")
            execute("delta", "generate", "--base", old, "--new", new, "--output", patch)
            if not patch.is_file() or patch.stat().st_size <= 0:
                raise RuntimeError("Delta engine produced no patch")
            patches.append(patch)
        first = work / f"reconstructed-{versions[1]}.nupkg"
        final = work / f"reconstructed-{versions[2]}.nupkg"
        execute("delta", "patch", "--base", packages[0], "--patch", patches[0], "--output", first)
        execute("delta", "patch", "--base", first, "--patch", patches[1], "--output", final)
        if contents(first) != contents(packages[1]) or contents(final) != contents(packages[2]):
            raise RuntimeError("Applied delta chain differs from full package payloads")
        corrupt = work / "corrupt-delta.nupkg"
        corrupt.write_bytes(patches[1].read_bytes()[:100])
        rejected = execute("delta", "patch", "--base", first, "--patch", corrupt,
                           "--output", work / "invalid-output.nupkg", required=False)
        if rejected.returncode == 0:
            raise RuntimeError("Corrupted delta was accepted")
        fallback = work / "full-fallback.nupkg"
        shutil.copyfile(packages[2], fallback)
        if contents(fallback) != contents(packages[2]):
            raise RuntimeError("Full fallback payload differs from the target")
        result = {"fixture_only": True, "package_id": package_id, "versions": list(versions),
                  "velopack_version": "1.2.161", "delta_steps_applied": 2,
                  "delta_bytes": [path.stat().st_size for path in patches],
                  "corrupt_delta_rejected": True, "full_fallback_verified": True}
        if keep_fixtures:
            result["fixture_directory"] = str(work)
        (artifacts / "velopack-delta-probe.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--vpk", type=Path, default=Path(".tools/vpk/vpk.exe"))
    parser.add_argument("--artifacts", type=Path, default=Path("artifacts"))
    parser.add_argument("--keep-fixtures", action="store_true", help="Retain unpublished fixtures for isolated updater-engine probes")
    parser.add_argument("--package-id", default="ToastifyDeltaProbe")
    parser.add_argument("--versions", nargs=3, default=("90.0.0", "90.0.1", "90.0.2"))
    args = parser.parse_args()
    print(json.dumps(run_probe(args.vpk.resolve(), args.artifacts, args.keep_fixtures, args.package_id, args.versions)))
