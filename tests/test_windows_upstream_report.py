#!/usr/bin/env python3
"""Behavioral tests for the Windows port's read-only upstream report."""

import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "scripts/windows/upstream_report.py"
SPEC = importlib.util.spec_from_file_location("windows_upstream_report", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class UpstreamReportTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="cmux upstream ")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.repo = self.root / "repo with spaces"
        self.repo.mkdir()
        self.env = {
            **os.environ, "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": os.devnull,
            "GIT_AUTHOR_NAME": "Upstream Report Test", "GIT_COMMITTER_NAME": "Upstream Report Test",
            "GIT_AUTHOR_EMAIL": "test@example.invalid", "GIT_COMMITTER_EMAIL": "test@example.invalid",
        }
        self.git("init", "-q")
        (self.repo / "README.md").write_text("initial\n", encoding="utf-8")
        self.base = self.commit("initial")
        self.git("update-ref", "refs/remotes/upstream/main", self.base)
        self.baseline = self.root / "baseline.json"
        self.baseline.write_text(json.dumps({
            "repository": MODULE.REPOSITORY, "branch": "main", "commit": self.base,
        }), encoding="utf-8")

    def git(self, *args):
        return subprocess.run(["git", "-c", "core.hooksPath=" + str(self.root / "no-hooks"),
                               "-C", str(self.repo), *args], env=self.env, check=True,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE).stdout.decode("utf-8").strip()

    def commit(self, message):
        self.git("add", "--all")
        self.git("commit", "-qm", message)
        return self.git("rev-parse", "HEAD")

    def run_report(self, *arguments):
        return subprocess.run([sys.executable, str(SCRIPT), "--repo", str(self.repo),
                               "--baseline", str(self.baseline), *arguments],
                              env=self.env, stdout=subprocess.PIPE, stderr=subprocess.PIPE)

    def report(self):
        result = self.run_report()
        self.assertEqual(result.returncode, 0, result.stderr.decode())
        return json.loads(result.stdout)

    def test_no_incoming_commits_does_not_claim_parity(self):
        report = self.report()
        self.assertEqual(report["baseline_commit"], self.base)
        self.assertEqual(report["target_commit"], self.base)
        self.assertEqual(report["windows_commit"], self.base)
        self.assertEqual(report["commits"], [])
        self.assertEqual(report["changes"], [])
        self.assertFalse(report["windows_worktree_dirty"])
        self.assertFalse(report["feature_inventory_review_required"])
        self.assertEqual(report["parity_verdict"], "not-evaluated")

    def test_reports_commit_and_feature_hints_without_modifying_baseline(self):
        original = self.baseline.read_bytes()
        source = self.repo / "Sources" / "BrowserPanel.swift"
        source.parent.mkdir()
        source.write_text("browser behavior\n")
        target = self.commit("Browser: 新しい操作")
        self.git("update-ref", "refs/remotes/upstream/main", target)
        report = self.report()
        self.assertEqual(report["incoming_commit_count"], 1)
        self.assertEqual(report["commits"], [{"commit": target, "subject": "Browser: 新しい操作"}])
        self.assertEqual(report["changes"], [{
            "status": "A", "path": "Sources/BrowserPanel.swift",
            "feature_hints": ["F-06", "F-07", "F-16"], "review": "required",
        }])
        self.assertTrue(report["feature_inventory_review_required"])
        self.assertEqual(self.baseline.read_bytes(), original)
        self.assertEqual(self.git("rev-parse", "HEAD"), target)

    def test_unicode_and_control_characters_in_paths_remain_data(self):
        # Newlines are illegal in Windows paths; tabs and Markdown delimiters
        # are covered by the serializer directly below on every platform.
        path = self.repo / "日本語 file [test].md"
        path.write_text("new\n", encoding="utf-8")
        target = self.commit("Unicode paths")
        self.git("update-ref", "refs/remotes/upstream/main", target)
        self.assertEqual(self.report()["changes"][0]["path"], path.name)

    def test_rename_includes_old_and_new_path_for_impact_review(self):
        self.git("mv", "README.md", "Browser.md")
        target = self.commit("rename")
        self.git("update-ref", "refs/remotes/upstream/main", target)
        changes = {change["path"]: change for change in self.report()["changes"]}
        self.assertEqual(changes["README.md"]["status"], "D")
        self.assertEqual(changes["Browser.md"]["status"], "A")
        self.assertIn("F-07", changes["Browser.md"]["feature_hints"])

    def test_empty_net_diff_still_requires_commit_review(self):
        source = self.repo / "README.md"
        source.write_text("changed\n")
        self.commit("intermediate behavior")
        source.write_text("initial\n")
        target = self.commit("restore original content")
        self.git("update-ref", "refs/remotes/upstream/main", target)
        report = self.report()
        self.assertEqual(report["changes"], [])
        self.assertEqual(report["incoming_commit_count"], 2)
        self.assertTrue(report["feature_inventory_review_required"])

    def test_uncommitted_windows_work_is_explicit(self):
        (self.repo / "local.txt").write_text("local only")
        self.assertTrue(self.report()["windows_worktree_dirty"])
        self.assertEqual(self.report()["incoming_commit_count"], 0)

    def test_missing_ref_fails_and_does_not_write_a_success_report(self):
        output = self.root / "report.json"
        result = self.run_report("--target", "refs/remotes/upstream/missing", "--output", str(output))
        self.assertEqual(result.returncode, 1)
        self.assertFalse(output.exists())
        self.assertEqual(result.stdout, b"")

    def test_nonancestor_target_is_rejected(self):
        (self.repo / "README.md").write_text("future\n")
        future = self.commit("future")
        value = json.loads(self.baseline.read_text())
        value["commit"] = future
        self.baseline.write_text(json.dumps(value))
        result = self.run_report()
        self.assertEqual(result.returncode, 1)
        self.assertIn(b"not an ancestor", result.stderr)

    def test_target_cannot_inject_an_option(self):
        result = self.run_report("--target=--help")
        self.assertEqual(result.returncode, 1)
        self.assertEqual(result.stdout, b"")

    def test_foreign_repository_baseline_is_rejected(self):
        value = json.loads(self.baseline.read_text())
        value["repository"] = "https://github.com/example/other.git"
        self.baseline.write_text(json.dumps(value))
        self.assertEqual(self.run_report().returncode, 1)

    def test_abbreviated_commit_is_rejected(self):
        value = json.loads(self.baseline.read_text())
        value["commit"] = self.base[:10]
        self.baseline.write_text(json.dumps(value))
        self.assertEqual(self.run_report().returncode, 1)

    def test_baseline_cannot_be_overwritten_by_report(self):
        original = self.baseline.read_bytes()
        result = self.run_report("--output", str(self.baseline))
        self.assertEqual(result.returncode, 1)
        self.assertEqual(self.baseline.read_bytes(), original)

    def test_hard_link_cannot_overwrite_baseline(self):
        original = self.baseline.read_bytes()
        output = self.root / "baseline-link.json"
        try:
            os.link(self.baseline, output)
        except OSError as error:
            self.skipTest(f"File system does not support hard links: {error}")
        result = self.run_report("--output", str(output))
        self.assertEqual(result.returncode, 1)
        self.assertEqual(self.baseline.read_bytes(), original)

    def test_json_and_markdown_output_can_be_saved(self):
        output = self.root / "report.json"
        result = self.run_report("--output", str(output))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(output.read_text())["target_commit"], self.base)
        result = self.run_report("--format", "markdown")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(self.base.encode(), result.stdout)
        self.assertIn(b"Feature parity: not evaluated", result.stdout)

    def test_markdown_control_content_is_escaped(self):
        report = self.report()
        report["changes"] = [{"path": "```\n# heading\n", "status": "A"}]
        rendered = MODULE.markdown(report)
        self.assertIn(r"```\n# heading\n", rendered)
        self.assertNotIn("\n# heading\n", rendered)


if __name__ == "__main__":
    unittest.main()
