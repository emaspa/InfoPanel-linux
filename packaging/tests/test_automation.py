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
            "id": 42, "draft": False, "body": "Human-written release notes",
            "html_url": "https://example.invalid/release",
            "assets": [{"id": 100 + index, "name": name} for index, name in enumerate(self.names)],
        }

    def run_publish(self, lookup, created=None, download_bytes=b"validated bytes", tag_exists=True):
        state = {"draft": (lookup or created or {}).get("draft", False)}

        def gh(*args):
            return json.dumps({"private": False, "default_branch": "main"})

        def download(repo, asset, directory):
            self.calls.append(("download", asset["id"]))
            target = Path(directory, asset["name"])
            target.write_bytes(download_bytes)
            return target

        def create_draft(repo, tag):
            self.calls.append(("create", tag))
            return created

        def upload(repo, release_id, path):
            self.calls.append(("upload", release_id, path.name))

        def publish_draft(repo, release_id):
            self.calls.append(("publish", release_id))
            state["draft"] = False

        def get_release(repo, release_id):
            self.calls.append(("get", release_id))
            return {**self.metadata, "id": release_id, "draft": state["draft"]}

        with patch.dict(os.environ, {"GH_REPO": "owner/repo"}), \
                patch.object(sys, "argv", ["release.py", "publish", "v1.2.3"]), \
                patch.object(release, "project_version", return_value="1.2.3"), \
                patch.object(release, "release", return_value=lookup) as lookup_mock, \
                patch.object(release, "gh", side_effect=gh), \
                patch.object(release, "tag_exists", return_value=tag_exists), \
                patch.object(release, "create_draft", side_effect=create_draft), \
                patch.object(release, "upload", side_effect=upload), \
                patch.object(release, "publish_draft", side_effect=publish_draft), \
                patch.object(release, "get_release", side_effect=get_release), \
                patch.object(release, "download", side_effect=download):
            release.main()
        return lookup_mock

    def writes(self):
        return [call for call in self.calls if call[0] in {"create", "upload", "publish"}]

    def test_existing_release_is_noop(self):
        self.run_publish(self.metadata)
        self.assertEqual(self.writes(), [])
        self.assertEqual(self.metadata["body"], "Human-written release notes")

    def test_existing_asset_changed_aborts(self):
        with self.assertRaisesRegex(ValueError, "refusing to overwrite"):
            self.run_publish(self.metadata, download_bytes=b"different bytes")
        self.assertEqual(self.writes(), [])

    def test_existing_draft_is_published_by_id_without_notes_edit(self):
        draft = {**self.metadata, "draft": True, "assets": self.metadata["assets"][:1]}
        self.run_publish(draft)
        self.assertEqual(self.writes(), [("upload", 42, self.names[1]), ("upload", 42, self.names[2]), ("publish", 42)])

    def test_new_release_is_never_re_read_by_listing(self):
        # Regression: the release list lags behind a just-created draft, so a
        # re-read by tag returned None and publishing crashed (v0.3.1, v0.3.2).
        created = {**self.metadata, "id": 77, "draft": True, "assets": []}
        lookup = self.run_publish(None, created=created)
        self.assertEqual(lookup.call_count, 1)
        self.assertEqual(self.writes(), [("create", "v1.2.3"), *[("upload", 77, name) for name in self.names], ("publish", 77)])
        self.assertEqual(self.calls[-1], ("get", 77))

    def test_missing_tag_never_creates_a_release(self):
        with self.assertRaisesRegex(ValueError, "does not exist"):
            self.run_publish(None, created={"id": 1}, tag_exists=False)
        self.assertEqual(self.writes(), [])

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
