#!/usr/bin/env python3
"""Feed/publication regression checks; all fixtures remain local and unpublished."""
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

from velopack_release import CHECKSUMS, PACK_ID, RIDS, finalize_stage, package_name, windows_assets

spec = importlib.util.spec_from_file_location("base_guard", Path(__file__).with_name("prepare-velopack-base.py"))
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)
publish_spec = importlib.util.spec_from_file_location("publisher", Path(__file__).with_name("publish-windows-release.py"))
publisher = importlib.util.module_from_spec(publish_spec)
publish_spec.loader.exec_module(publisher)


def temporary_directory():
    root = Path(__file__).resolve().parents[1]
    directory = (root / 'artifacts' / 'velopack-unit-temp').resolve()
    if not directory.is_relative_to(root):
        raise ValueError('Unsafe fixture cleanup target')
    directory.mkdir(parents=True, exist_ok=True)
    return tempfile.TemporaryDirectory(dir=directory)


def package(path, version="1.3.10", rid="win-x64", kind="full"):
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr(PACK_ID + ".nuspec", f"<package><metadata><id>{PACK_ID}</id><version>{version}</version></metadata></package>")
        archive.writestr("lib/app/test.txt", "fixture payload")
    payload = path.read_bytes()
    return {"PackageId": PACK_ID, "Version": version, "Type": kind.title(), "FileName": path.name,
            "Size": len(payload), "SHA1": hashlib.sha1(payload).hexdigest(), "SHA256": hashlib.sha256(payload).hexdigest()}


def stage(directory, version="1.3.10"):
    feeds = {}
    for rid in RIDS:
        entry = package(directory / package_name(version, rid), version, rid)
        feeds[rid] = {"Assets": [entry]}
        (directory / f"releases.{rid}.json").write_text(json.dumps(feeds[rid]))
        for name in (f"ToastifyReloaded-Setup-{rid}.exe", f"{PACK_ID}-{rid}.msi", f"RELEASES-{rid}"):
            (directory / name).write_bytes(b"fixture")
    return feeds


class VelopackPublicationTests(unittest.TestCase):
    def test_first_release_has_eleven_assets_without_pretend_delta(self):
        with temporary_directory() as temporary:
            directory = Path(temporary)
            stage(directory)
            report = finalize_stage(directory, "1.3.10")
            self.assertEqual(report["asset_count"], 11)
            self.assertEqual(report["delta_packages"], [])
            self.assertEqual(len((directory / CHECKSUMS).read_text().splitlines()), 10)

    def test_historical_feed_entries_are_pruned_without_republishing_old_packages(self):
        with temporary_directory() as temporary:
            directory = Path(temporary)
            feeds = stage(directory)
            feeds["win-x64"]["Assets"].append(dict(feeds["win-x64"]["Assets"][0], Version="1.3.9"))
            (directory / "releases.win-x64.json").write_text(json.dumps(feeds["win-x64"]))
            finalize_stage(directory, "1.3.10")
            self.assertEqual(len(json.loads((directory / "releases.win-x64.json").read_text())["Assets"]), 1)

    def test_wrong_architecture_feed_is_rejected(self):
        with temporary_directory() as temporary:
            directory = Path(temporary)
            feeds = stage(directory)
            (directory / "releases.win-x64.json").write_text(json.dumps(feeds["win-arm64"]))
            with self.assertRaises(ValueError):
                finalize_stage(directory, "1.3.10")

    def test_corrupt_package_is_rejected_by_feed_digest(self):
        with temporary_directory() as temporary:
            directory = Path(temporary)
            stage(directory)
            with (directory / package_name("1.3.10", "win-x64")).open("ab") as output:
                output.write(b"corrupt")
            with self.assertRaises(ValueError):
                finalize_stage(directory, "1.3.10")

    def test_uploaded_stage_does_not_remove_or_mask_unexpected_files(self):
        with temporary_directory() as temporary:
            directory = Path(temporary)
            stage(directory)
            finalize_stage(directory, "1.3.10")
            extra = directory / "TestCDirFile.txt"
            extra.write_bytes(b"unexpected fixture")
            before = {path.name: path.read_bytes() for path in directory.iterdir()}
            with self.assertRaises(ValueError):
                publisher.validate_stage(directory, "1.3.10")
            self.assertEqual(before, {path.name: path.read_bytes() for path in directory.iterdir()})

    def test_uploaded_stage_does_not_repair_an_invalid_checksum_manifest(self):
        with temporary_directory() as temporary:
            directory = Path(temporary)
            stage(directory)
            finalize_stage(directory, "1.3.10")
            manifest = directory / CHECKSUMS
            damaged = manifest.read_bytes().replace(b"  RELEASES-win-arm64", b"  changed-feed-name")
            manifest.write_bytes(damaged)
            with self.assertRaises(ValueError):
                publisher.validate_stage(directory, "1.3.10")
            self.assertEqual(manifest.read_bytes(), damaged)

    def test_optional_real_delta_is_included_in_feed_and_checksum_manifest(self):
        with temporary_directory() as temporary:
            directory = Path(temporary)
            feeds = stage(directory, "1.3.11")
            name = package_name("1.3.11", "win-x64", "delta")
            feeds["win-x64"]["Assets"].append(package(directory / name, "1.3.11", kind="delta"))
            (directory / "releases.win-x64.json").write_text(json.dumps(feeds["win-x64"]))
            report = finalize_stage(directory, "1.3.11")
            self.assertEqual(report["delta_packages"], [name])
            self.assertIn(name, (directory / CHECKSUMS).read_text())

    def test_legacy_previous_release_is_not_invented_as_a_delta_base(self):
        with temporary_directory() as temporary:
            release = {"tag_name": "v1.3.9", "assets": [{"name": name, "size": 1} for name in windows_assets("v1.3.9")]}
            result = base.prepare_base("MarliusLab/Toastify-Reloaded", "1.3.10", "win-x64", Path(temporary),
                                       lambda _: [release], lambda *_: self.fail("No legacy package may be downloaded"))
            self.assertFalse(result["base_available"])

    def test_real_previous_channel_requires_complete_release_and_immutable_source(self):
        with temporary_directory() as temporary:
            source = Path(temporary) / "published"
            source.mkdir()
            stage(source)
            finalize_stage(source, "1.3.10")
            bodies = {path.name: path.read_bytes() for path in source.iterdir()}
            release = {"tag_name": "v1.3.10", "draft": False, "prerelease": False,
                       "assets": [{"name": name, "size": len(body), "digest": "sha256:" + hashlib.sha256(body).hexdigest()}
                                  for name, body in bodies.items()]}
            sha = "1" * 40
            def api(path):
                if path.startswith("releases?"):
                    return [release]
                if path.startswith("git/ref/"):
                    return {"object": {"type": "commit", "sha": sha}}
                self.fail("Unexpected API path: " + path)
            output = Path(temporary) / "base"
            result = base.prepare_base("MarliusLab/Toastify-Reloaded", "1.3.11", "win-arm64", output,
                                       api, lambda asset, destination: destination.write_bytes(bodies[asset["name"]]))
            self.assertTrue(result["base_available"])
            self.assertEqual(result["source_commit"], sha)
            self.assertEqual(result["source_tag"], "v1.3.10")
            self.assertEqual(result["package"], package_name("1.3.10", "win-arm64"))
            self.assertNotIn("win-x64", " ".join(path.name for path in output.iterdir()))

    def test_asset_download_checks_digest_and_never_forwards_api_token(self):
        with temporary_directory() as temporary:
            body = b"published bytes"
            asset = {"browser_download_url": "https://github.com/MarliusLab/Toastify-Reloaded/releases/download/v1.3.10/a",
                     "size": len(body), "digest": "sha256:" + hashlib.sha256(body).hexdigest()}
            with patch.object(base, "urlopen", return_value=io.BytesIO(body)) as opener:
                destination = Path(temporary) / "asset"
                base.download_asset("MarliusLab/Toastify-Reloaded", asset, destination)
                self.assertEqual(destination.read_bytes(), body)
                self.assertNotIn("Authorization", opener.call_args.args[0].headers)

    def test_untrusted_asset_host_and_wrong_digest_are_rejected(self):
        with temporary_directory() as temporary:
            asset = {"browser_download_url": "https://evil.invalid/release", "size": 4, "digest": "sha256:" + "0" * 64}
            with self.assertRaises(ValueError):
                base.download_asset("MarliusLab/Toastify-Reloaded", asset, Path(temporary) / "asset")
            asset["browser_download_url"] = "https://github.com/MarliusLab/Toastify-Reloaded/releases/download/v1.3.10/a"
            with patch.object(base, "urlopen", return_value=io.BytesIO(b"test")), self.assertRaises(ValueError):
                base.download_asset("MarliusLab/Toastify-Reloaded", asset, Path(temporary) / "asset")

    def test_draft_is_published_only_after_every_uploaded_digest_matches(self):
        with temporary_directory() as temporary:
            directory = Path(temporary)
            stage(directory)
            finalize_stage(directory, "1.3.10")
            sha = "1" * 40
            draft = self.draft(directory, sha)
            patches = []
            def api(path, method="GET", body=None):
                if method == "PATCH":
                    patches.append(body)
                    return dict(draft, draft=False, html_url="https://github.com/MarliusLab/Toastify-Reloaded/releases/tag/v1.3.10")
                if path.startswith("git/ref/"):
                    return {"object": {"type": "commit", "sha": sha}} if patches else None
                if path == "releases/latest":
                    return {"tag_name": "v1.3.10"}
                return draft
            result = publisher.publish("MarliusLab/Toastify-Reloaded", "v1.3.10", sha, directory, api, lambda _: None)
            self.assertEqual(patches, [{"draft": False, "make_latest": "true"}])
            self.assertEqual(len(result["assets"]), 11)

    def test_wrong_remote_digest_keeps_release_draft(self):
        with temporary_directory() as temporary:
            directory = Path(temporary)
            stage(directory)
            finalize_stage(directory, "1.3.10")
            sha = "1" * 40
            draft = self.draft(directory, sha)
            draft["assets"][0]["digest"] = "sha256:" + "0" * 64
            patches = []
            def api(path, method="GET", body=None):
                if method == "PATCH":
                    patches.append(body)
                return None if path.startswith("git/ref/") else draft
            with self.assertRaises(ValueError):
                publisher.publish("MarliusLab/Toastify-Reloaded", "v1.3.10", sha, directory, api, lambda _: None)
            self.assertEqual(patches, [])

    def test_untagged_draft_is_loaded_by_upload_id_when_tag_lookup_is_missing(self):
        with temporary_directory() as temporary:
            directory = Path(temporary)
            stage(directory)
            finalize_stage(directory, "1.3.10")
            sha = "1" * 40
            draft = self.draft(directory, sha)
            patches = []
            def api(path, method="GET", body=None):
                if path.startswith("releases/tags/"):
                    self.fail("Unpublished drafts must use the exact upload ID")
                if method == "PATCH":
                    self.assertEqual(path, "releases/123")
                    patches.append(body)
                    return dict(draft, draft=False, html_url="https://github.com/example/release")
                if path.startswith("git/ref/"):
                    return {"object": {"type": "commit", "sha": sha}} if patches else None
                if path == "releases/latest": return {"tag_name": "v1.3.10"}
                self.assertEqual(path, "releases/123")
                return draft
            result = publisher.publish("MarliusLab/Toastify-Reloaded", "v1.3.10", sha, directory, api,
                                       lambda _: None, release_id=123)
            self.assertEqual(len(patches), 1)
            self.assertEqual(len(result["assets"]), 11)

    def test_already_published_upload_id_is_verified_without_a_second_patch(self):
        with temporary_directory() as temporary:
            directory = Path(temporary)
            stage(directory)
            finalize_stage(directory, "1.3.10")
            sha = "1" * 40
            release = dict(self.draft(directory, sha), draft=False, html_url="https://github.com/example/release")
            def api(path, method="GET", body=None):
                self.assertEqual(method, "GET")
                if path.startswith("git/ref/"): return {"object": {"type": "commit", "sha": sha}}
                if path == "releases/latest": return {"tag_name": "v1.3.10"}
                self.assertEqual(path, "releases/123")
                return release
            result = publisher.publish("MarliusLab/Toastify-Reloaded", "v1.3.10", sha, directory, api,
                                       release_id=123)
            self.assertTrue(result["already_published"])

    def test_upload_id_cannot_adopt_a_different_release(self):
        with temporary_directory() as temporary:
            directory = Path(temporary)
            stage(directory)
            finalize_stage(directory, "1.3.10")
            draft = self.draft(directory, "1" * 40)
            def api(path, method="GET", body=None):
                self.assertEqual(method, "GET")
                return None if path.startswith("git/ref/") else dict(draft, id=456)
            with self.assertRaises(ValueError):
                publisher.publish("MarliusLab/Toastify-Reloaded", "v1.3.10", "1" * 40, directory, api,
                                   release_id=123)

    def test_foreign_source_tag_is_never_retargeted(self):
        with temporary_directory() as temporary:
            directory = Path(temporary)
            stage(directory)
            finalize_stage(directory, "1.3.10")
            calls = []
            def api(path, method="GET", body=None):
                calls.append(method)
                return {"object": {"type": "commit", "sha": "2" * 40}}
            with self.assertRaises(ValueError):
                publisher.publish("MarliusLab/Toastify-Reloaded", "v1.3.10", "1" * 40, directory, api, lambda _: None)
            self.assertNotIn("PATCH", calls)

    @staticmethod
    def draft(directory, sha):
        return {"id": 123, "tag_name": "v1.3.10", "target_commitish": sha, "draft": True, "prerelease": False,
                "assets": [{"name": path.name, "size": path.stat().st_size, "state": "uploaded",
                            "digest": "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest()}
                           for path in directory.iterdir()]}


if __name__ == "__main__":
    unittest.main(verbosity=2)
