#!/usr/bin/env python3
"""Pinned draft recovery tests. No network, installer, app or publication runs."""
import copy
import hashlib
import importlib.util
import io
import os
from pathlib import Path
import unittest
from unittest.mock import patch
from urllib.request import Request


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


recovery = load("recovery", "recover-windows-draft.py")
fixtures = load("publication_fixtures", "test-velopack-release.py")


def original_run():
    return {"id": recovery.BUILD_RUN_ID, "head_sha": recovery.SOURCE_SHA, "status": "completed",
            "conclusion": "success", "event": "push", "head_branch": "main", "name": "build",
            "path": ".github/workflows/build.yml", "repository": {"full_name": recovery.REPOSITORY},
            "head_repository": {"full_name": recovery.REPOSITORY}}


def fixture_publication(directory):
    fixtures.stage(directory)
    fixtures.finalize_stage(directory, "1.3.10")
    # Explicit CRLF, independently of the OS executing these unit tests.
    for name in ("releases.win-x64.json", "releases.win-arm64.json", "RELEASES-win-x64", "RELEASES-win-arm64"):
        path = directory / name
        path.write_bytes(path.read_bytes().replace(b"\r\n", b"\n").replace(b"\n", b"\r\n"))
    names = sorted(path.name for path in directory.iterdir() if path.name != fixtures.CHECKSUMS)
    manifest = "".join(hashlib.sha256((directory / name).read_bytes()).hexdigest() + "  " + name + "\r\n" for name in names)
    (directory / fixtures.CHECKSUMS).write_bytes(manifest.encode())
    bodies = {path.name: path.read_bytes() for path in directory.iterdir()}
    pins = {name: (1000 + index, len(body), hashlib.sha256(body).hexdigest())
            for index, (name, body) in enumerate(sorted(bodies.items()))}
    release = {"id": recovery.RELEASE_ID, "tag_name": recovery.TAG, "target_commitish": recovery.SOURCE_SHA,
               "draft": True, "prerelease": False, "html_url": "https://github.com/example/release",
               "assets": [{"id": asset_id, "name": name, "size": size, "digest": "sha256:" + sha256, "state": "uploaded"}
                          for name, (asset_id, size, sha256) in pins.items()]}
    return bodies, pins, release


class RecoveryTests(unittest.TestCase):
    def test_original_run_requires_every_pinned_provenance_field(self):
        recovery.verify_build(original_run())
        changes = {"id": 1, "head_sha": "2" * 40, "status": "in_progress", "conclusion": "failure",
                   "event": "pull_request", "head_branch": "feature", "name": "other",
                   "path": "untrusted.yml", "repository": {"full_name": "other/repo"},
                   "head_repository": {"full_name": "other/fork"}}
        for field, value in changes.items():
            with self.subTest(field=field), self.assertRaises(ValueError):
                recovery.verify_build(dict(original_run(), **{field: value}))

    def test_current_recovery_is_pinned_to_eleven_specific_original_assets(self):
        self.assertEqual(len(recovery.ASSETS), 11)
        self.assertEqual(set(recovery.ASSETS), recovery.windows_assets(recovery.TAG))
        self.assertEqual(recovery.RELEASE_ID, 433628164)
        self.assertEqual(recovery.SOURCE_SHA, "d28e935887531d35b90c123a0e277888b4dea625")
        self.assertEqual(recovery.BUILD_RUN_ID, 41792181155)

    def test_release_rejects_replaced_assets_and_foreign_identity(self):
        with fixtures.temporary_directory() as temporary:
            _, pins, release = fixture_publication(Path(temporary))
            with patch.object(recovery, "ASSETS", pins):
                recovery.verify_recovery_release(release)
                for field, value in (("id", 5), ("tag_name", "v1.3.11"), ("target_commitish", "2" * 40),
                                     ("prerelease", True), ("draft", None)):
                    with self.subTest(field=field), self.assertRaises(ValueError):
                        recovery.verify_recovery_release(dict(release, **{field: value}))
                for field, value in (("id", 9), ("size", 1), ("digest", "sha256:" + "0" * 64), ("state", "new")):
                    changed = copy.deepcopy(release)
                    changed["assets"][0][field] = value
                    with self.subTest(asset_field=field), self.assertRaises(ValueError):
                        recovery.verify_recovery_release(changed)

    def test_recovery_uses_id_and_keeps_all_downloaded_crlf_bytes_unchanged(self):
        self.run_recovery_fixture()

    def test_recovery_is_idempotent_after_exact_publication_without_patch(self):
        self.run_recovery_fixture(already_published=True)

    def run_recovery_fixture(self, already_published=False):
        with fixtures.temporary_directory() as temporary:
            source = Path(temporary) / "source"
            target = Path(temporary) / "download"
            source.mkdir()
            target.mkdir()
            bodies, pins, release = fixture_publication(source)
            release["draft"] = not already_published
            patches = []
            def api(path, method="GET", body=None):
                if path.startswith("releases/tags/"):
                    self.fail("Draft-by-tag returns404: recovery must use its pinned ID")
                if method == "PATCH":
                    self.assertEqual(path, "releases/" + str(recovery.RELEASE_ID))
                    patches.append(body)
                    release["draft"] = False
                    return copy.deepcopy(release)
                if path.startswith("actions/runs/"):
                    return original_run()
                if path.startswith("git/ref/"):
                    return {"object": {"type": "commit", "sha": recovery.SOURCE_SHA}} if not release["draft"] else None
                if path == "releases/latest": return {"tag_name": recovery.TAG}
                self.assertEqual(path, "releases/" + str(recovery.RELEASE_ID))
                return copy.deepcopy(release)
            with patch.object(recovery, "ASSETS", pins):
                result = recovery.recover(target, api, lambda asset, destination: destination.write_bytes(bodies[asset["name"]]))
            self.assertEqual(len(patches), 0 if already_published else 1)
            self.assertEqual(result["source_sha"], recovery.SOURCE_SHA)
            self.assertEqual({path.name: path.read_bytes() for path in target.iterdir()}, bodies)

    def test_foreign_tag_aborts_before_asset_download_or_patch(self):
        with fixtures.temporary_directory() as temporary:
            def api(path, method="GET", body=None):
                self.assertEqual(method, "GET")
                if path.startswith("actions/runs/"): return original_run()
                return {"object": {"type": "commit", "sha": "2" * 40}}
            with self.assertRaises(ValueError):
                recovery.recover(Path(temporary), api, lambda *_: self.fail("No download may start"))

    def test_downloaded_byte_change_aborts_without_publishing(self):
        with fixtures.temporary_directory() as temporary:
            source = Path(temporary) / "source"
            target = Path(temporary) / "target"
            source.mkdir()
            target.mkdir()
            _, pins, release = fixture_publication(source)
            def api(path, method="GET", body=None):
                self.assertEqual(method, "GET")
                if path.startswith("actions/runs/"): return original_run()
                if path.startswith("git/ref/"): return None
                return release
            with patch.object(recovery, "ASSETS", pins), self.assertRaises(ValueError):
                recovery.recover(target, api, lambda asset, destination: destination.write_bytes(b"corrupt"))

    def test_private_asset_authenticates_only_fixed_api_origin(self):
        with fixtures.temporary_directory() as temporary:
            body = b"private fixture"
            asset = {"id": 123, "url": "https://evil.invalid/untrusted", "size": len(body),
                     "digest": "sha256:" + hashlib.sha256(body).hexdigest()}
            with patch.dict(os.environ, {"GH_TOKEN": "fixture-api-token"}), patch.object(recovery.base, "build_opener") as builder:
                builder.return_value.open.return_value = io.BytesIO(body)
                destination = Path(temporary) / "asset"
                recovery.base.download_api_asset(recovery.REPOSITORY, asset, destination)
                request = builder.return_value.open.call_args.args[0]
                self.assertEqual(request.full_url, "https://api.github.com/repos/MarliusLab/Toastify-Reloaded/releases/assets/123")
                self.assertEqual(request.get_header("Authorization"), "Bearer fixture-api-token")
                self.assertEqual(destination.read_bytes(), body)

    def test_signed_redirect_is_a_new_request_without_authorization_or_cookie(self):
        request = Request("https://api.github.com/repos/MarliusLab/Toastify-Reloaded/releases/assets/123",
                          headers={"Authorization": "Bearer fixture", "Cookie": "secret=fixture"})
        redirected = recovery.base.TokenSafeAssetRedirect().redirect_request(
            request, None, 302, "Found", {}, "https://release-assets.githubusercontent.com/asset?sig=fixture")
        self.assertNotIn("authorization", {name.lower() for name in redirected.headers})
        self.assertNotIn("cookie", {name.lower() for name in redirected.headers})
        self.assertEqual(redirected.unredirected_hdrs, {})
        self.assertEqual(request.get_header("Authorization"), "Bearer fixture")

    def test_unknown_or_insecure_redirect_is_rejected(self):
        request = Request("https://api.github.com/repos/MarliusLab/Toastify-Reloaded/releases/assets/123")
        for url in ("https://evil.invalid/a", "http://release-assets.githubusercontent.com/a"):
            with self.subTest(url=url), self.assertRaises(ValueError):
                recovery.base.TokenSafeAssetRedirect().redirect_request(request, None, 302, "Found", {}, url)


if __name__ == "__main__":
    unittest.main(verbosity=2)
