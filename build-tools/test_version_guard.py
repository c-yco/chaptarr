import contextlib
import io
import json
import subprocess
import tempfile
import unittest
from pathlib import Path

from version_guard import guard_commit_hygiene, guard_monotonic, resolve_compare_ref


class CompareRefTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name) / "repo"
        self.root.mkdir()
        self.git("init", "--initial-branch=develop")
        self.git("config", "user.name", "Version Guard Test")
        self.git("config", "user.email", "version-guard@example.invalid")
        self.write_version("1.0.0")
        self.commit("Initial baseline")
        self.baseline = self.git("rev-parse", "HEAD")
        # A local repository acting as origin reproduces a fork with no tags.
        self.git("remote", "add", "origin", str(self.root))

    def git(self, *args):
        result = subprocess.run(
            ["git", "-C", str(self.root), *args],
            check=True, capture_output=True, text=True,
        )
        return result.stdout.strip()

    def write_version(self, version):
        (self.root / "package.json").write_text(json.dumps({"version": version}), encoding="utf-8")
        props = self.root / "src" / "Directory.Build.props"
        props.parent.mkdir(exist_ok=True)
        props.write_text(
            f"<Project><PropertyGroup><AssemblyVersion>{version}</AssemblyVersion>"
            f"<AssemblyFileVersion>{version}</AssemblyFileVersion></PropertyGroup></Project>",
            encoding="utf-8",
        )

    def commit(self, subject):
        self.git("add", "-A")
        self.git("commit", "--allow-empty", "-m", subject)

    def test_feature_branch_without_tags_uses_branch_point(self):
        self.git("switch", "-c", "feature/shelfmark")
        self.commit("Add integration")
        self.commit("Fix integration")
        self.assertEqual(resolve_compare_ref(self.root, None), self.baseline)

    def test_develop_without_tags_checks_latest_commit(self):
        self.commit("Fix build")
        self.assertEqual(resolve_compare_ref(self.root, None), self.baseline)

    def test_reachable_release_tag_takes_precedence_over_branch_point(self):
        self.git("tag", "v1.0.0")
        self.commit("Upstream change")
        self.git("switch", "-c", "feature/shelfmark")
        self.commit("Add integration")
        self.assertEqual(resolve_compare_ref(self.root, None), "v1.0.0")

    def test_pull_request_base_takes_precedence_over_tag(self):
        self.git("tag", "v1.0.0")
        self.git("switch", "-c", "feature/shelfmark")
        self.commit("Add integration")
        self.assertEqual(resolve_compare_ref(self.root, "develop"), "origin/develop")

    def test_tagless_comparison_still_rejects_mixed_version_only_commits(self):
        self.git("switch", "-c", "feature/shelfmark")
        (self.root / "feature.txt").write_text("feature", encoding="utf-8")
        self.commit("Version bump")
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            guard_commit_hygiene(self.root, resolve_compare_ref(self.root, None))

    def test_tagless_develop_comparison_still_rejects_version_decrease(self):
        self.write_version("0.9.0")
        self.commit("Change version")
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            guard_monotonic(self.root, resolve_compare_ref(self.root, None), None, "[allow-version-decrease]")

    def test_missing_base_fails_explicitly(self):
        self.git("remote", "remove", "origin")
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            resolve_compare_ref(self.root, None)

    def test_initial_commit_without_a_comparison_fails_explicitly(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            resolve_compare_ref(self.root, None)


if __name__ == "__main__":
    unittest.main()
