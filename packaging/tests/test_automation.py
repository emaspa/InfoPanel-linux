"""Offline checks for release guards, retries and metadata updates. No publishing."""
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

PACKAGING = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PACKAGING))
import release
from version import version


class VersionTests(unittest.TestCase):
    def test_reject_non_package_versions(self):
        for value in ("01.2.3", "1.2", "1.2.3-rc1", "1.2.3\n", "../1.2.3"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                version(value)

    def test_mismatch_fails(self):
        result = subprocess.run(
            [sys.executable, str(PACKAGING / "version.py"), "v999.0.0"],
            text=True, capture_output=True,
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("VERSION MISMATCH", result.stderr)


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.previous = Path.cwd()
        os.chdir(self.temp.name)
        self.addCleanup(self.temp.cleanup)
        self.addCleanup(os.chdir, self.previous)
        Path("artifacts").mkdir()
        self.names = release.asset_names("1.2.3")
        for name in self.names:
            Path("artifacts", name).write_bytes(b"validated bytes")
        self.calls = []
        self.metadata = {
            "draft": False, "body": "Human-written release notes",
            "html_url": "https://example.invalid/release",
            "assets": [{"name": name} for name in self.names],
        }

    def fake_gh(self, *args):
        self.calls.append(args)
        if args[0] == "api":
            return json.dumps({"private": False, "default_branch": "main"})
        if args[:2] == ("release", "edit"):
            self.metadata["draft"] = False
        return ""

    def run_publish(self, metadata=None, download_bytes=b"validated bytes"):
        def download(repo, tag, name, directory):
            Path(directory, name).write_bytes(download_bytes)

        with patch.dict(os.environ, {"GH_REPO": "owner/repo"}), \
                patch.object(sys, "argv", ["release.py", "publish", "v1.2.3"]), \
                patch.object(release, "project_version", return_value="1.2.3"), \
                patch.object(release, "release", side_effect=metadata or [self.metadata, self.metadata]), \
                patch.object(release, "gh", side_effect=self.fake_gh), \
                patch.object(release, "download", side_effect=download):
            release.main()

    def test_existing_release_is_noop(self):
        self.run_publish()
        self.assertEqual([call for call in self.calls if call[0] == "release"], [])
        self.assertEqual(self.metadata["body"], "Human-written release notes")

    def test_existing_asset_changed_aborts(self):
        with self.assertRaisesRegex(ValueError, "refusing to overwrite"):
            self.run_publish(download_bytes=b"different bytes")
        self.assertFalse(any(call[0] == "release" for call in self.calls))

    def test_draft_publishes_without_notes_edit(self):
        self.metadata["draft"] = True
        self.run_publish()
        edits = [call for call in self.calls if call[:2] == ("release", "edit")]
        self.assertEqual(edits, [("release", "edit", "v1.2.3", "--repo", "owner/repo", "--draft=false")])

    def test_new_release_is_draft_until_uploads_complete(self):
        empty = {**self.metadata, "draft": True, "assets": []}
        self.run_publish(metadata=[None, empty, self.metadata])
        writes = [call for call in self.calls if call[0] == "release"]
        self.assertEqual(writes[0][:2], ("release", "create"))
        self.assertIn("--verify-tag", writes[0])
        self.assertIn("--draft", writes[0])
        self.assertEqual([call[1] for call in writes], ["create", "upload", "upload", "upload", "edit"])
        self.assertFalse(any("--clobber" in call for call in writes))

    def test_draft_release_is_found_by_listing_not_tag_endpoint(self):
        draft = {"tag_name": "v1.2.3", "draft": True, "assets": []}
        other = {"tag_name": "v1.2.2", "draft": False, "assets": []}
        listing = "\n".join(json.dumps(item) for item in (draft,)) + "\n"
        ok = subprocess.CompletedProcess([], 0, listing, "")
        with patch.object(subprocess, "run", return_value=ok) as run:
            self.assertEqual(release.release("owner/repo", "v1.2.3"), draft)
        command = run.call_args.args[0]
        self.assertIn("repos/owner/repo/releases", command)
        self.assertNotIn("releases/tags", " ".join(command))
        self.assertIn("--paginate", command)
        self.assertNotEqual(other["tag_name"], draft["tag_name"])

    def test_missing_release_is_none_and_duplicates_abort(self):
        empty = subprocess.CompletedProcess([], 0, "", "")
        with patch.object(subprocess, "run", return_value=empty):
            self.assertIsNone(release.release("owner/repo", "v1.2.3"))
        item = json.dumps({"tag_name": "v1.2.3", "draft": True, "assets": []})
        twice = subprocess.CompletedProcess([], 0, f"{item}\n{item}\n", "")
        with patch.object(subprocess, "run", return_value=twice), self.assertRaisesRegex(RuntimeError, "duplicates"):
            release.release("owner/repo", "v1.2.3")

    def test_api_auth_failure_is_not_treated_as_absent_release(self):
        failure = subprocess.CompletedProcess([], 1, "", "gh: Forbidden (HTTP 403)")
        with patch.object(subprocess, "run", return_value=failure), self.assertRaises(RuntimeError):
            release.release("owner/repo", "v1.2.3")


class AurTests(unittest.TestCase):
    def test_update_hashes_given_tarball_and_helper_and_is_idempotent(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "PKGBUILD").write_text("pkgver=0.3.0\npkgrel=4\nsha256sums=('old'\n 'old-helper')\n# keep me\n")
            (root / "stage-package.sh").write_bytes(b"helper")
            tarball = root / "downloaded.tar.gz"
            tarball.write_bytes(b"published asset")
            command = [sys.executable, str(PACKAGING / "update-aur.py"), str(root), "1.2.3", str(tarball)]
            subprocess.run(command, check=True)
            content = (root / "PKGBUILD").read_text()
            self.assertIn("pkgver=1.2.3\npkgrel=1", content)
            self.assertIn(release.digest(tarball), content)
            self.assertIn(release.digest(root / "stage-package.sh"), content)
            self.assertIn("# keep me", content)
            subprocess.run(command, check=True)
            self.assertEqual(content, (root / "PKGBUILD").read_text())

    def test_sync_rejects_downgrades_and_main_recipe_changes(self):
        with tempfile.TemporaryDirectory() as temp:
            directories = [Path(temp, name) for name in ("tag", "main", "remote")]
            recipe = "pkgname=infopanel-bin\npkgver=0.3.0\npkgrel=1\nsha256sums=('checksum')\n"
            for directory in directories:
                directory.mkdir()
                (directory / "PKGBUILD").write_text(recipe)
                for name in ("stage-package.sh", "infopanel-bin.install"):
                    (directory / name).write_text("same")
            command = [sys.executable, str(PACKAGING / "check-aur-sync.py"), *map(str, directories), "0.3.0"]
            self.assertEqual(subprocess.run(command, capture_output=True).returncode, 0)
            (directories[1] / "PKGBUILD").write_text(recipe.replace("0.3.0", "0.4.0"))
            result = subprocess.run(command, text=True, capture_output=True)
            self.assertIn("downgrade", result.stderr)
            (directories[1] / "PKGBUILD").write_text(recipe + "depends=('changed')\n")
            result = subprocess.run(command, text=True, capture_output=True)
            self.assertIn("changed on main", result.stderr)


if __name__ == "__main__":
    unittest.main()
