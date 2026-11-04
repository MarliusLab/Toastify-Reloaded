#!/usr/bin/env python3
"""Native feed/draft publication guards using local fixtures and a fake API."""
import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile
from urllib.request import Request


def sibling(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


publisher = sibling("native_publisher", "publish-portable-release.py")
native = publisher.native
SHA = "1" * 40
PLATFORMS = (("linux", "1.4.2", "v1.4.2-linux"), ("macos", "1.5.0-rc.3", "v1.5.0-macos-rc.3"))


def fixture_directory():
    root = Path(__file__).resolve().parents[1]
    target = (root / "artifacts/native-release-unit-temp").resolve()
    if not target.is_relative_to(root): raise ValueError("Unsafe fixture path")
    target.mkdir(parents=True, exist_ok=True)
    return tempfile.TemporaryDirectory(dir=target)


def make_stage(directory, platform, version, deltas=False):
    package_id, runtimes, _ = native.CONFIG[platform]
    for runtime in runtimes:
        entries = []
        for kind in (("full", "delta") if deltas else ("full",)):
            name = f"{package_id}-{version}-{runtime}-{kind}.nupkg"
            with zipfile.ZipFile(directory / name, "w") as archive:
                archive.writestr(package_id + ".nuspec", f"<package><metadata><id>{package_id}</id><version>{version}</version><channel>{runtime}</channel><rid>{runtime}</rid><os>{'linux' if platform == 'linux' else 'osx'}</os></metadata></package>")
                archive.writestr("lib/app/fixture.txt", "isolated fixture payload")
            path = directory / name
            entries.append({"PackageId": package_id, "Version": version, "Type": kind.title(), "FileName": name,
                            "Size": path.stat().st_size, "SHA1": native.digest(path, "sha1"), "SHA256": native.digest(path)})
        (directory / f"releases.{runtime}.json").write_text(json.dumps({"Assets": entries}))
    for name in native.legacy_names(platform, version): (directory / name).write_bytes(b"native distribution fixture")
    return native.finalize(directory, platform, version)


def release_for(directory, platform, tag, draft=True):
    return {"id": 321, "tag_name": tag, "target_commitish": SHA, "draft": draft,
            "prerelease": "-rc." in tag or "-preview." in tag,
            "html_url": f"https://github.com/{native.REPO}/releases/tag/{tag}",
            "assets": [{"name": path.name, "state": "uploaded", "size": path.stat().st_size,
                        "digest": "sha256:" + native.digest(path),
                        "browser_download_url": f"https://github.com/{native.REPO}/releases/download/{tag}/{path.name}"}
                       for path in directory.iterdir() if path.is_file()]}


class FakeApi:
    def __init__(self, release, tag_source=None):
        self.release = release
        self.tag_source = tag_source
        self.patches = []
        self.gets = []
    def __call__(self, path, method="GET", body=None):
        if method == "PATCH":
            self.patches.append((path, body))
            self.release = dict(self.release, draft=False, prerelease=body["prerelease"])
            self.tag_source = SHA
            return copy.deepcopy(self.release)
        self.gets.append(path)
        if path.startswith("git/ref/tags/"):
            return None if self.tag_source is None else {"object": {"type": "commit", "sha": self.tag_source}}
        if path == "releases/321": return copy.deepcopy(self.release)
        return None


class NativePublicationTests(unittest.TestCase):
    def test_first_native_baselines_have_real_full_feeds_without_deltas(self):
        for platform, version, _ in PLATFORMS:
            with self.subTest(platform=platform), fixture_directory() as temp:
                directory = Path(temp)
                result = make_stage(directory, platform, version)
                self.assertEqual(result["asset_count"], 13 if platform == "linux" else 11)
                self.assertEqual(result["deltas"], [])
                self.assertEqual(len(publisher.validate_stage(directory, platform, version)), result["asset_count"])

    def test_both_real_delta_entries_are_preserved_in_feeds_and_manifest(self):
        for platform, version, _ in PLATFORMS:
            with self.subTest(platform=platform), fixture_directory() as temp:
                directory = Path(temp)
                result = make_stage(directory, platform, version, True)
                self.assertEqual(len(result["deltas"]), 2)
                self.assertEqual(len(publisher.validate_stage(directory, platform, version)), 15 if platform == "linux" else 13)

    def test_wrong_channel_and_corrupt_package_are_rejected(self):
        for corrupt in (False, True):
            with self.subTest(corrupt=corrupt), fixture_directory() as temp:
                directory = Path(temp)
                make_stage(directory, "linux", "1.4.2")
                feed_path = directory / "releases.linux-x64.json"
                feed = json.loads(feed_path.read_text())
                if corrupt:
                    with (directory / feed["Assets"][0]["FileName"]).open("ab") as stream: stream.write(b"damaged")
                else:
                    feed["Assets"][0]["FileName"] = feed["Assets"][0]["FileName"].replace("linux-x64", "linux-arm64")
                    feed_path.write_text(json.dumps(feed))
                with self.assertRaises(ValueError): publisher.validate_stage(directory, "linux", "1.4.2")

    def test_historical_feed_entries_are_not_silently_pruned_during_publication(self):
        with fixture_directory() as temp:
            directory = Path(temp); make_stage(directory, "linux", "1.4.2")
            path = directory / "releases.linux-x64.json"
            feed = json.loads(path.read_text()); feed["Assets"].append(dict(feed["Assets"][0], Version="1.4.1"))
            path.write_text(json.dumps(feed)); before = path.read_bytes()
            with self.assertRaises(ValueError): publisher.validate_stage(directory, "linux", "1.4.2")
            self.assertEqual(path.read_bytes(), before)

    def test_unexpected_files_and_damaged_manifest_are_never_removed_or_repaired(self):
        for damage_manifest in (False, True):
            with self.subTest(damage_manifest=damage_manifest), fixture_directory() as temp:
                directory = Path(temp); make_stage(directory, "macos", "1.5.0-rc.3")
                if damage_manifest: (directory / "SHA256SUMS-macOS.txt").write_text("0" * 64 + "  wrong-file\n")
                else: (directory / "unexpected.txt").write_bytes(b"keep this fixture")
                before = {p.name: p.read_bytes() for p in directory.iterdir()}
                with self.assertRaises(ValueError): publisher.validate_stage(directory, "macos", "1.5.0-rc.3")
                self.assertEqual(before, {p.name: p.read_bytes() for p in directory.iterdir()})

    def test_publication_preserves_crlf_feed_and_manifest_bytes(self):
        with fixture_directory() as temp:
            directory = Path(temp); make_stage(directory, "linux", "1.4.2")
            for path in directory.glob("RELEASES-*"): path.write_bytes(path.read_bytes().replace(b"\r\n", b"\n").replace(b"\n", b"\r\n"))
            for path in directory.glob("releases.*.json"): path.write_bytes(path.read_bytes().replace(b"\r\n", b"\n").replace(b"\n", b"\r\n"))
            checksum = directory / "SHA256SUMS.txt"
            checksum.write_bytes("".join(f"{native.digest(p)}  {p.name}\r\n" for p in sorted(directory.iterdir()) if p != checksum).encode())
            before = {p.name: p.read_bytes() for p in directory.iterdir()}
            api = FakeApi(release_for(directory, "linux", "v1.4.2-linux"))
            publisher.publish("linux", "v1.4.2-linux", SHA, directory, api, 321, delay=lambda _: None)
            self.assertEqual(before, {p.name: p.read_bytes() for p in directory.iterdir()})

    def test_publish_by_exact_upload_id_works_without_a_tag_and_preserves_channel(self):
        for platform, version, tag in PLATFORMS:
            with self.subTest(platform=platform), fixture_directory() as temp:
                directory = Path(temp); make_stage(directory, platform, version)
                api = FakeApi(release_for(directory, platform, tag))
                result = publisher.publish(platform, tag, SHA, directory, api, 321, delay=lambda _: None)
                self.assertEqual(len(result["assets"]), 13 if platform == "linux" else 11)
                self.assertEqual(api.patches, [("releases/321", {"draft": False, "prerelease": platform == "macos", "make_latest": "false"})])
                self.assertNotIn("releases/tags/" + tag, api.gets)

    def test_wrong_id_source_tag_or_prerelease_never_publishes(self):
        for key, value in (("id", 999), ("target_commitish", "2" * 40), ("tag_name", "v1.4.1-linux"), ("prerelease", True)):
            with self.subTest(key=key), fixture_directory() as temp:
                directory = Path(temp); make_stage(directory, "linux", "1.4.2")
                release = release_for(directory, "linux", "v1.4.2-linux"); release[key] = value
                api = FakeApi(release)
                with self.assertRaises(ValueError): publisher.publish("linux", "v1.4.2-linux", SHA, directory, api, 321, delay=lambda _: None)
                self.assertEqual(api.patches, [])

    def test_missing_duplicate_extra_incomplete_or_corrupt_remote_assets_stay_private(self):
        def mutate(release, kind):
            if kind == "missing": release["assets"].pop()
            elif kind == "duplicate": release["assets"].append(copy.deepcopy(release["assets"][0]))
            elif kind == "extra": release["assets"].append(dict(release["assets"][0], name="extra.bin"))
            elif kind == "state": release["assets"][0]["state"] = "new"
            elif kind == "size": release["assets"][0]["size"] += 1
            elif kind == "digest": release["assets"][0]["digest"] = "sha256:" + "0" * 64
            else: release["assets"][0]["browser_download_url"] = "https://evil.invalid/file"
        for kind in ("missing", "duplicate", "extra", "state", "size", "digest", "url"):
            with self.subTest(kind=kind), fixture_directory() as temp:
                directory = Path(temp); make_stage(directory, "macos", "1.5.0-rc.3")
                release = release_for(directory, "macos", "v1.5.0-macos-rc.3"); mutate(release, kind)
                api = FakeApi(release)
                with self.assertRaises(ValueError): publisher.publish("macos", "v1.5.0-macos-rc.3", SHA, directory, api, 321, delay=lambda _: None)
                self.assertEqual(api.patches, [])

    def test_existing_foreign_tag_is_immutable(self):
        with fixture_directory() as temp:
            directory = Path(temp); make_stage(directory, "linux", "1.4.2")
            api = FakeApi(release_for(directory, "linux", "v1.4.2-linux"), "2" * 40)
            with self.assertRaises(ValueError): publisher.publish("linux", "v1.4.2-linux", SHA, directory, api, 321, delay=lambda _: None)
            self.assertEqual(api.patches, [])

    def test_already_published_exact_release_is_verified_without_a_second_patch(self):
        with fixture_directory() as temp:
            directory = Path(temp); make_stage(directory, "macos", "1.5.0-rc.3", True)
            api = FakeApi(release_for(directory, "macos", "v1.5.0-macos-rc.3", draft=False), SHA)
            result = publisher.publish("macos", "v1.5.0-macos-rc.3", SHA, directory, api, 321, delay=lambda _: None)
            self.assertTrue(result["already_published"]); self.assertEqual(api.patches, [])

    def test_invalid_inputs_and_missing_upload_are_rejected(self):
        with fixture_directory() as temp:
            directory = Path(temp); make_stage(directory, "linux", "1.4.2")
            api = FakeApi(release_for(directory, "linux", "v1.4.2-linux"))
            for platform, tag, sha, identifier in (("linux", "v1.4.2-macos", SHA, 321),
                    ("linux", "v1.4.2-linux/../other", SHA, 321), ("linux", "v1.4.2-linux", "main", 321),
                    ("linux", "v1.4.2-linux", SHA, 0), ("linux", "v1.4.2-linux", SHA, True)):
                with self.subTest(tag=tag, identifier=identifier), self.assertRaises(ValueError):
                    publisher.publish(platform, tag, sha, directory, api, identifier, delay=lambda _: None)
            with self.assertRaises(ValueError): publisher.publish("linux", "v1.4.2-linux", SHA, directory, lambda _: None, 321)
            self.assertEqual(api.patches, [])

    def test_legacy_only_release_is_not_a_fabricated_delta_baseline(self):
        with fixture_directory() as temp, patch.object(native, "request_json", return_value=[{"tag_name": "v1.4.1-linux", "draft": False, "assets": []}]), patch.object(native, "download") as download:
            result = native.prepare_base(Path(temp), "linux", "1.4.2", "linux-x64")
            self.assertTrue(result["first_baseline"]); download.assert_not_called()

    def test_verified_previous_feed_keeps_only_full_base_not_unavailable_historical_delta(self):
        with fixture_directory() as temp:
            previous = Path(temp) / "previous"; previous.mkdir()
            make_stage(previous, "linux", "1.4.2", True)
            release = release_for(previous, "linux", "v1.4.2-linux", draft=False)
            bodies = {path.name: path.read_bytes() for path in previous.iterdir()}
            def api(path):
                return [release] if "/releases?" in path else {"object": {"type": "commit", "sha": SHA}}
            output = Path(temp) / "baseline"
            with patch.object(native, "request_json", side_effect=api), patch.object(native, "download", side_effect=lambda asset, path, _: path.write_bytes(bodies[asset["name"]])):
                result = native.prepare_base(output, "linux", "1.4.3", "linux-x64")
            self.assertFalse(result["first_baseline"])
            feed = json.loads((output / "releases.linux-x64.json").read_text())
            self.assertEqual([entry["Type"] for entry in feed["Assets"]], ["Full"])
            self.assertFalse(list(output.glob("*-delta.nupkg")))
            native.validate_channel(output, "linux", "1.4.2", "linux-x64")

    def test_baseline_rejects_incomplete_assets_wrong_url_and_unrelated_manifest_hash(self):
        for mutation in ("state", "size", "digest", "url", "duplicate", "source", "manifest"):
            with self.subTest(mutation=mutation), fixture_directory() as temp:
                previous = Path(temp) / "previous"; previous.mkdir()
                make_stage(previous, "linux", "1.4.2", True)
                if mutation == "manifest":
                    manifest = previous / "SHA256SUMS.txt"
                    lines = manifest.read_text().splitlines()
                    index = next(i for i, line in enumerate(lines) if line.endswith("AppImage"))
                    lines[index] = "0" * 64 + lines[index][64:]
                    manifest.write_text("\n".join(lines) + "\n")
                release = release_for(previous, "linux", "v1.4.2-linux", draft=False)
                asset = release["assets"][0]
                if mutation == "state": asset["state"] = "new"
                elif mutation == "size": asset["size"] = 0
                elif mutation == "digest": asset["digest"] = None
                elif mutation == "url": asset["browser_download_url"] = "https://evil.invalid/" + asset["name"]
                elif mutation == "duplicate": release["assets"].append(dict(asset))
                elif mutation == "source": release["target_commitish"] = "main"
                bodies = {p.name: p.read_bytes() for p in previous.iterdir()}
                def api(path):
                    return [release] if "/releases?" in path else {"object": {"type": "commit", "sha": SHA}}
                with patch.object(native, "request_json", side_effect=api), patch.object(native, "download", side_effect=lambda asset, path, _: path.write_bytes(bodies[asset["name"]])):
                    with self.assertRaises(ValueError): native.prepare_base(Path(temp) / "baseline", "linux", "1.4.3", "linux-x64")

    def test_annotated_tags_are_peeled_but_foreign_source_and_deep_cycles_rejected(self):
        annotated = "2" * 40
        def api(path):
            return {"object": {"type": "tag", "sha": annotated}} if "/ref/" in path else {"object": {"type": "commit", "sha": SHA}}
        with patch.object(native, "request_json", side_effect=api):
            self.assertEqual(native.baseline_source("v1.4.2-linux", SHA), SHA)
            with self.assertRaises(ValueError): native.baseline_source("v1.4.2-linux", "3" * 40)
        with patch.object(native, "request_json", return_value={"object": {"type": "tag", "sha": annotated}}) as api_mock:
            with self.assertRaises(ValueError): native.baseline_source("v1.4.2-linux", SHA)
            self.assertEqual(api_mock.call_count, 6)  # reference plus at most five annotated tags

    def test_download_redirect_allowlist_removes_credentials_and_api_never_redirects(self):
        request = Request("https://github.com/MarliusLab/Toastify-Reloaded/releases/download/v1/file", headers={"Authorization": "do-not-forward", "Proxy-Authorization": "also-private"})
        redirected = native.AssetRedirects().redirect_request(request, None, 302, "Found", {}, "https://release-assets.githubusercontent.com/approved")
        self.assertIsNone(redirected.get_header("Authorization"))
        self.assertIsNone(redirected.get_header("Proxy-authorization"))
        for url in ("http://github.com/file", "https://evil.invalid/file", "https://github.com.evil.invalid/file", "https://user@github.com/file", "https://github.com:444/file"):
            with self.subTest(url=url), self.assertRaises(ValueError): native.AssetRedirects().redirect_request(request, None, 302, "Found", {}, url)
        with self.assertRaises(ValueError): native.NoRedirects().redirect_request(request, None, 302, "Found", {}, "https://github.com/file")

    def test_download_is_bounded_checks_stream_hash_and_keeps_existing_file_on_failure(self):
        class Response(io.BytesIO):
            def __init__(self, body, length=None, url="https://release-assets.githubusercontent.com/approved"):
                super().__init__(body); self.url = url; self.headers = {} if length is None else {"Content-Length": str(length)}
        class Opener:
            def __init__(self, response): self.response = response; self.requests = []
            def open(self, request, **kwargs): self.requests.append(request); return self.response
        for case in ("success", "oversize", "length", "hash", "host"):
            with self.subTest(case=case), fixture_directory() as temp:
                path = Path(temp) / "fixture.bin"; path.write_bytes(b"existing")
                body = b"abcd" if case == "oversize" else b"abc"
                asset = {"name": path.name, "size": 3, "state": "uploaded", "digest": "sha256:" + hashlib.sha256(b"wrong" if case == "hash" else b"abc").hexdigest(),
                         "browser_download_url": f"https://github.com/{native.REPO}/releases/download/v1.4.2-linux/{path.name}"}
                response = Response(body, 2 if case == "length" else None, "https://evil.invalid/file" if case == "host" else "https://release-assets.githubusercontent.com/approved")
                opener = Opener(response)
                with patch.object(native, "build_opener", return_value=opener):
                    if case == "success": native.download(asset, path, "v1.4.2-linux")
                    else:
                        with self.assertRaises(ValueError): native.download(asset, path, "v1.4.2-linux")
                self.assertEqual(path.read_bytes(), b"abc" if case == "success" else b"existing")
                self.assertEqual(list(Path(temp).glob(".native-download-*")), [])
                self.assertIsNone(opener.requests[0].get_header("Authorization"))
        with self.assertRaises(TimeoutError): native.read_bounded(io.BytesIO(b"abc"), 3, 0)
        with self.assertRaises(ValueError): native.read_bounded(io.BytesIO(b"abcd"), 3, float("inf"))


if __name__ == "__main__": unittest.main(verbosity=2)
