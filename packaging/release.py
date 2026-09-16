#!/usr/bin/env python3
"""GitHub release operations. Uses gh's credential handling, never prints tokens."""
import hashlib
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

from version import project_version, version


def gh(*args):
    return subprocess.check_output(["gh", *args], text=True)


def release(repo, tag):
    # releases/tags/{tag} never returns draft releases, so a draft created by an
    # earlier attempt would look absent and a retry would create a duplicate.
    # List all releases (drafts included for a token with push access) instead.
    result = subprocess.run(
        ["gh", "api", "--paginate", f"repos/{repo}/releases",
         "--jq", f'.[] | select(.tag_name == "{tag}") | @json'],
        text=True, capture_output=True,
    )
    if result.returncode:
        raise RuntimeError(result.stderr)
    matches = [json.loads(line) for line in result.stdout.splitlines() if line.strip()]
    if len(matches) > 1:
        raise RuntimeError(f"{len(matches)} releases use tag {tag}; delete the duplicates and retry")
    return matches[0] if matches else None


def asset_names(value):
    return [f"infopanel-{value}-linux-x64.tar.gz", f"infopanel_{value}_amd64.deb", f"infopanel-{value}-1.x86_64.rpm"]


# Everything after the initial lookup addresses the release by id. The release
# list is eventually consistent, so a draft created moments earlier can be
# missing from it, and gh's `release upload/edit/download <tag>` resolve drafts
# through that same list.


def tag_exists(repo, tag):
    result = subprocess.run(["gh", "api", f"repos/{repo}/git/ref/tags/{tag}"], text=True, capture_output=True)
    if result.returncode:
        if "(HTTP 404)" in result.stderr:
            return False
        raise RuntimeError(result.stderr)
    return True


def create_draft(repo, tag):
    # The REST create call returns the release, id included; no re-read needed.
    return json.loads(gh("api", "--method", "POST", f"repos/{repo}/releases",
                         "-f", f"tag_name={tag}", "-F", "draft=true", "-F", "generate_release_notes=true"))


def get_release(repo, release_id):
    return json.loads(gh("api", f"repos/{repo}/releases/{release_id}"))


def upload(repo, release_id, path):
    gh("api", "--method", "POST", "-H", "Content-Type: application/octet-stream",
       f"https://uploads.github.com/repos/{repo}/releases/{release_id}/assets?name={path.name}",
       "--input", str(path))


def publish_draft(repo, release_id):
    gh("api", "--method", "PATCH", f"repos/{repo}/releases/{release_id}", "-F", "draft=false")


def download(repo, asset, directory):
    target = Path(directory) / asset["name"]
    with target.open("wb") as stream:
        subprocess.run(["gh", "api", "-H", "Accept: application/octet-stream",
                        f"repos/{repo}/releases/assets/{asset['id']}"], stdout=stream, check=True)
    return target


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    if len(sys.argv) != 3 or sys.argv[1] not in {"fetch-existing", "publish"}:
        sys.exit("Usage: release.py fetch-existing|publish vX.Y.Z")
    mode, tag = sys.argv[1:]
    if not tag.startswith("v"):
        raise ValueError("Release tags must start with v")
    value = version(tag[1:])
    if value != project_version():
        raise ValueError("VERSION MISMATCH between release tag and csproj")
    repo = os.environ["GH_REPO"]
    info = json.loads(gh("api", f"repos/{repo}"))
    if info["private"] or info["default_branch"] != "main":
        raise ValueError("Expected a public repository with default branch main")
    metadata = release(repo, tag)
    artifacts = Path("artifacts")
    artifacts.mkdir(exist_ok=True)
    names = asset_names(value)
    if mode == "fetch-existing":
        existing = {asset["name"]: asset for asset in (metadata or {}).get("assets", [])}
        for name in names:
            if name in existing:
                # This checkout is disposable. Always download the authoritative copy.
                (artifacts / name).unlink(missing_ok=True)
                download(repo, existing[name], artifacts)
                print(f"Reusing release asset: {name}")
        return

    for name in names:
        if not (artifacts / name).is_file():
            raise ValueError(f"Missing validated artifact: {name}")
    if metadata is None:
        # The API creates a missing tag from the default branch; never let it.
        if not tag_exists(repo, tag):
            raise ValueError(f"Tag {tag} does not exist; push it before publishing")
        metadata = create_draft(repo, tag)
    release_id = metadata["id"]
    existing = {asset["name"]: asset for asset in metadata["assets"]}
    for name in names:
        if name in existing:
            with tempfile.TemporaryDirectory() as directory:
                copy = download(repo, existing[name], directory)
                if digest(copy) != digest(artifacts / name):
                    raise ValueError(f"Existing asset changed since validation: {name}; refusing to overwrite")
            print(f"Already uploaded: {name}")
        else:
            upload(repo, release_id, artifacts / name)
    # Existing title/body are never edited. Publish drafts only after all uploads.
    if metadata["draft"]:
        publish_draft(repo, release_id)
    metadata = get_release(repo, release_id)
    if metadata["draft"] or not set(names) <= {asset["name"] for asset in metadata["assets"]}:
        raise ValueError("Release is not public with all three required assets")
    print(metadata["html_url"])


if __name__ == "__main__":
    main()
