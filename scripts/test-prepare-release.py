#!/usr/bin/env python3
"""Regression coverage for publication authorization and immutable tags."""
import importlib.util
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("release_guard", ROOT / "scripts/prepare-release.py")
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)
REPO = "MarliusLab/Toastify-Reloaded"
SHA = "1" * 40


def event():
    return {"repository": {"full_name": REPO}, "workflow_run": {
        "conclusion": "success", "status": "completed", "event": "push",
        "head_branch": "main", "head_sha": SHA, "name": "Linux Build",
        "path": ".github/workflows/linux-build.yml",
        "head_repository": {"full_name": REPO}}}


class ReleaseGuardTests(unittest.TestCase):
    def test_all_prepared_versions_have_source_and_notes(self):
        expected = {"windows": ("v1.3.12", "false"), "linux": ("v1.4.2-linux", "false"),
                    "macos": ("v1.5.0-macos-rc.3", "true")}
        for platform, (tag, prerelease) in expected.items():
            with self.subTest(platform=platform):
                plan = guard.validate_plan(ROOT, platform)
                self.assertEqual(plan["release_tag"], tag)
                self.assertEqual(plan["prerelease"], prerelease)

    def test_successful_main_push_is_allowed(self):
        guard.validate_event("linux", "workflow_run", event(), REPO, "main", SHA)

    def test_any_trusted_platform_completion_may_trigger_each_release_guard(self):
        for name, path in guard.BUILD_WORKFLOWS.values():
            for platform in guard.PLATFORMS:
                data = event()
                data["workflow_run"].update(name=name, path=path)
                guard.validate_event(platform, "workflow_run", data, REPO, "main", SHA)

    def test_untrusted_or_failed_runs_are_rejected(self):
        for key, value in (("conclusion", "failure"), ("status", "in_progress"),
                           ("event", "pull_request"), ("event", "workflow_dispatch"),
                           ("head_branch", "feature"), ("name", "macOS Build"),
                           ("head_sha", "2" * 40),
                           ("head_repository", {"full_name": "other/fork"})):
            with self.subTest(key=key, value=value):
                data = event()
                data["workflow_run"][key] = value
                with self.assertRaises(ValueError):
                    guard.validate_event("linux", "workflow_run", data, REPO, "main", SHA)

    def test_wrong_repository_or_commit_is_rejected(self):
        for repository, sha in (("evil/repo", SHA), (REPO, "bad"), ("../unsafe", SHA)):
            with self.subTest(repository=repository, sha=sha):
                with self.assertRaises(ValueError):
                    guard.validate_event("linux", "workflow_run", event(), repository, "main", sha)

    def test_tag_push_uses_exact_ref_and_checks_annotated_target_later(self):
        data = {"repository": {"full_name": REPO}, "ref": "refs/tags/v1.4.1-linux", "after": "2" * 40}
        guard.validate_event("linux", "push", data, REPO, "v1.4.1-linux", SHA)
        data["ref"] = "refs/heads/main"
        with self.assertRaises(ValueError):
            guard.validate_event("linux", "push", data, REPO, "v1.4.1-linux", SHA)

    def test_manual_and_pull_request_events_cannot_publish(self):
        for name in ("workflow_dispatch", "pull_request", "schedule"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                guard.validate_event("linux", name, event(), REPO, "main", SHA)

    def test_new_tag_may_be_created_only_after_green_build(self):
        self.assertTrue(guard.check_remote(REPO, "v1.4.1-linux", SHA, "workflow_run", lambda _: None))
        with self.assertRaises(ValueError):
            guard.check_remote(REPO, "v1.4.1-linux", SHA, "push", lambda _: None)

    def test_existing_release_is_never_changed_on_future_main(self):
        complete = {"id": 1, "assets": [{"name": name, "size": 1}
                    for name in guard.expected_assets("v1.4.1-linux")]}
        self.assertFalse(guard.check_remote(REPO, "v1.4.1-linux", SHA, "workflow_run", lambda _: complete))

    def test_legacy_windows_releases_keep_their_original_three_asset_contract(self):
        legacy_names = {"ToastifyReloaded-Setup-win-x64.exe", "ToastifyReloaded-Setup-win-arm64.exe", "SHA256SUMS-Windows.txt"}
        for tag in ("v.1.3.5", "v1.3.6", "v1.3.7", "v1.3.8", "v1.3.9"):
            with self.subTest(tag=tag):
                self.assertEqual(guard.expected_assets(tag), legacy_names)
                complete = {"id": 1, "assets": [{"name": name, "size": 1} for name in legacy_names]}
                self.assertFalse(guard.check_remote(REPO, tag, SHA, "workflow_run", lambda _: complete))

    def test_first_velopack_release_requires_full_feeds_and_msis_but_not_deltas(self):
        assets = guard.expected_assets("v1.3.10")
        self.assertEqual(len(assets), 11)
        for rid in ("win-x64", "win-arm64"):
            self.assertIn(f"ToastifyReloaded-1.3.10-{rid}-full.nupkg", assets)
            self.assertIn(f"ToastifyReloaded-{rid}.msi", assets)
            self.assertIn(f"releases.{rid}.json", assets)
            self.assertIn(f"RELEASES-{rid}", assets)
        self.assertFalse(any("delta" in name for name in assets))

    def test_native_unix_asset_contracts_start_at_explicit_baselines(self):
        self.assertEqual(len(guard.expected_assets("v1.4.1-linux")), 6)
        self.assertEqual(len(guard.expected_assets("v1.5.0-macos-rc.2")), 5)
        linux = guard.expected_assets("v1.4.2-linux")
        mac = guard.expected_assets("v1.5.0-macos-rc.3")
        self.assertEqual(len(linux), 13)
        self.assertEqual(len(mac), 11)
        self.assertIn("ToastifyReloaded-Linux-arm64.AppImage", linux)
        for rid in ("linux-x64", "linux-arm64"):
            self.assertIn(f"ToastifyReloadedLinux-1.4.2-{rid}-full.nupkg", linux)
            self.assertIn(f"releases.{rid}.json", linux)
            self.assertIn(f"RELEASES-{rid}", linux)
        for rid in ("osx-x64", "osx-arm64"):
            self.assertIn(f"ToastifyReloadedMac-1.5.0-rc.3-{rid}-full.nupkg", mac)
        self.assertFalse(any("-delta.nupkg" in name for name in linux | mac))
        self.assertEqual(len(guard.expected_assets("v1.5.0-macos")), 11)
        self.assertEqual(len(guard.expected_assets("v1.5.1-macos-preview.1")), 11)

    @staticmethod
    def build(platform, identifier):
        name, path = guard.BUILD_WORKFLOWS[platform]
        return {"id": identifier, "name": name, "path": path, "head_sha": SHA, "head_branch": "main",
                "event": "push", "status": "completed", "conclusion": "success",
                "repository": {"full_name": REPO}, "head_repository": {"full_name": REPO}}

    def test_all_three_same_source_builds_are_required_and_output_artifact_run_ids(self):
        builds = [self.build(platform, index) for index, platform in enumerate(guard.BUILD_WORKFLOWS, 100)]
        result = guard.trusted_builds(REPO, SHA, lambda _: {"workflow_runs": builds})
        self.assertTrue(result["ready"])
        self.assertEqual(result["windows_build_run"], "100")
        self.assertEqual(result["linux_build_run"], "101")
        self.assertEqual(result["macos_build_run"], "102")

    def test_joint_success_still_requires_an_immutable_tag_target(self):
        builds = [self.build(platform, index) for index, platform in enumerate(guard.BUILD_WORKFLOWS, 100)]
        for target in (None, SHA, "2" * 40):
            def api(path):
                if "/actions/runs?" in path: return {"workflow_runs": builds}
                if "/git/ref/" in path and target: return {"object": {"type": "commit", "sha": target}}
                return None
            with self.subTest(target=target):
                if target and target != SHA:
                    with self.assertRaises(ValueError):
                        guard.release_eligibility(REPO, "v1.3.12", SHA, "workflow_run", api)
                else:
                    result = guard.release_eligibility(REPO, "v1.3.12", SHA, "workflow_run", api)
                    self.assertEqual(result["publish"], "true")
                    self.assertEqual(result["windows_build_run"], "100")

    def test_missing_pending_or_failed_builds_never_reach_publication_tag_checks(self):
        for status, conclusion in (("in_progress", None), ("queued", None), ("completed", "failure"), ("completed", "cancelled")):
            builds = [self.build(platform, index) for index, platform in enumerate(guard.BUILD_WORKFLOWS, 100)]
            builds[-1].update(status=status, conclusion=conclusion)
            def api(path):
                self.assertIn("/actions/runs?", path)
                return {"workflow_runs": builds}
            with self.subTest(status=status, conclusion=conclusion):
                result = guard.release_eligibility(REPO, "v1.3.12", SHA, "workflow_run", api)
                self.assertEqual(result["publish"], "false")
        self.assertFalse(guard.trusted_builds(REPO, SHA, lambda _: {"workflow_runs": builds[:2]})["ready"])

    def test_spoofed_or_wrong_source_builds_cannot_satisfy_the_joint_gate(self):
        for key, value in (("path", "untrusted.yml"), ("name", "other"), ("head_sha", "2" * 40),
                           ("head_branch", "feature"), ("event", "pull_request"),
                           ("repository", {"full_name": "other/repo"}),
                           ("head_repository", {"full_name": "other/fork"}), ("id", "123")):
            builds = [self.build(platform, index) for index, platform in enumerate(guard.BUILD_WORKFLOWS, 100)]
            builds[0][key] = value
            with self.subTest(key=key):
                self.assertFalse(guard.trusted_builds(REPO, SHA, lambda _: {"workflow_runs": builds})["ready"])

    def test_newer_failed_push_blocks_an_older_success_of_the_same_workflow(self):
        builds = [self.build(platform, index) for index, platform in enumerate(guard.BUILD_WORKFLOWS, 100)]
        builds.append(dict(self.build("windows", 999), conclusion="failure"))
        self.assertFalse(guard.trusted_builds(REPO, SHA, lambda _: {"workflow_runs": builds})["ready"])
        builds[-1].update(conclusion="success")
        result = guard.trusted_builds(REPO, SHA, lambda _: {"workflow_runs": builds})
        self.assertTrue(result["ready"])
        self.assertEqual(result["windows_build_run"], "999")

    def test_joint_gate_paginates_without_accepting_unrelated_runs(self):
        def api(path):
            if path.endswith("&page=1"): return {"workflow_runs": [{"name": "other"}] * 100}
            return {"workflow_runs": [self.build(platform, index) for index, platform in enumerate(guard.BUILD_WORKFLOWS, 100)]}
        self.assertTrue(guard.trusted_builds(REPO, SHA, api)["ready"])

    def test_incomplete_release_can_only_resume_at_original_source(self):
        for target in (SHA, "2" * 40):
            def api(path):
                if "/releases/" in path: return {"id": 1, "assets": []}
                return {"object": {"type": "commit", "sha": target}}
            if target == SHA:
                self.assertTrue(guard.check_remote(REPO, "v1.4.1-linux", SHA, "workflow_run", api))
            else:
                with self.assertRaises(ValueError):
                    guard.check_remote(REPO, "v1.4.1-linux", SHA, "workflow_run", api)

    def test_incomplete_release_with_missing_tag_fails(self):
        def api(path):
            return {"id": 1} if "/releases/" in path else None
        with self.assertRaises(ValueError):
            guard.check_remote(REPO, "v1.4.1-linux", SHA, "workflow_run", api)

    def test_draft_without_tag_requires_exact_full_source_sha(self):
        for target in (SHA, "2" * 40, "main"):
            def api(path):
                return {"id": 1, "draft": True, "target_commitish": target} if "/releases/" in path else None
            if target == SHA:
                self.assertTrue(guard.check_remote(REPO, "v1.4.1-linux", SHA, "workflow_run", api))
            else:
                with self.assertRaises(ValueError):
                    guard.check_remote(REPO, "v1.4.1-linux", SHA, "workflow_run", api)

    def test_existing_lightweight_tag_must_match(self):
        for target in (SHA, "2" * 40):
            def api(path):
                return None if "/releases" in path else {"object": {"type": "commit", "sha": target}}
            if target == SHA:
                self.assertTrue(guard.check_remote(REPO, "v1.4.1-linux", SHA, "workflow_run", api))
            else:
                with self.assertRaises(ValueError):
                    guard.check_remote(REPO, "v1.4.1-linux", SHA, "workflow_run", api)

    def test_existing_annotated_tag_is_peeled(self):
        def api(path):
            if "/releases" in path: return None
            if "/git/ref/" in path: return {"object": {"type": "tag", "sha": "2" * 40}}
            return {"object": {"type": "commit", "sha": SHA}}
        self.assertTrue(guard.check_remote(REPO, "v1.4.1-linux", SHA, "workflow_run", api))

    def test_unverifiable_tag_is_rejected(self):
        def api(path):
            return None if "/releases" in path else {"object": {"type": "tree", "sha": SHA}}
        with self.assertRaises(ValueError):
            guard.check_remote(REPO, "v1.4.1-linux", SHA, "workflow_run", api)

    def test_untagged_draft_is_found_in_list_and_resumes_only_exact_source(self):
        draft = {"id": 433628164, "tag_name": "v1.3.10", "draft": True,
                 "target_commitish": SHA, "prerelease": False, "assets": []}
        def api(path):
            return [draft] if "/releases?" in path else None
        self.assertTrue(guard.check_remote(REPO, "v1.3.10", SHA, "workflow_run", api))
        with self.assertRaises(ValueError):
            guard.check_remote(REPO, "v1.3.10", "2" * 40, "workflow_run", api)

    def test_ambiguous_draft_list_is_rejected(self):
        def api(path):
            return [{"tag_name": "v1.3.10"}, {"tag_name": "v1.3.10"}] if "/releases?" in path else None
        with self.assertRaises(ValueError):
            guard.find_release(REPO, "v1.3.10", api)

    def test_draft_lookup_scans_later_release_pages(self):
        draft = {"id": 1, "tag_name": "v1.3.10"}
        def api(path):
            if path.endswith("&page=1"): return [{"tag_name": "other"}] * 100
            if path.endswith("&page=2"): return [draft]
            return None
        self.assertEqual(guard.find_release(REPO, "v1.3.10", api), draft)


if __name__ == "__main__":
    unittest.main(verbosity=2)
