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
    result = subprocess.run(
        ["gh", "api", f"repos/{repo}/releases/tags/{tag}"],
        text=True, capture_output=True,
    )
    if result.returncode:
        if "(HTTP 404)" in result.stderr:
            return None
        raise RuntimeError(result.stderr)
    return json.loads(result.stdout)


def asset_names(value):
    return [f"infopanel-{value}-linux-x64.tar.gz", f"infopanel_{value}_amd64.deb", f"infopanel-{value}-1.x86_64.rpm"]


def download(repo, tag, name, directory):
    gh("release", "download", tag, "--repo", repo, "--pattern", name, "--dir", str(directory))


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
        available = {asset["name"] for asset in (metadata or {}).get("assets", [])}
        for name in names:
            if name in available:
                # This checkout is disposable. Always download the authoritative copy.
                (artifacts / name).unlink(missing_ok=True)
                download(repo, tag, name, artifacts)
                print(f"Reusing release asset: {name}")
        return

    for name in names:
        if not (artifacts / name).is_file():
            raise ValueError(f"Missing validated artifact: {name}")
    if metadata is None:
        gh("release", "create", tag, "--repo", repo, "--verify-tag", "--draft", "--generate-notes")
        metadata = release(repo, tag)
    available = {asset["name"] for asset in metadata["assets"]}
    for name in names:
        if name in available:
            with tempfile.TemporaryDirectory() as directory:
                download(repo, tag, name, directory)
                if digest(Path(directory) / name) != digest(artifacts / name):
                    raise ValueError(f"Existing asset changed since validation: {name}; refusing to overwrite")
            print(f"Already uploaded: {name}")
        else:
            gh("release", "upload", tag, str(artifacts / name), "--repo", repo)
    # Existing title/body are never edited. Publish drafts only after all uploads.
    if metadata["draft"]:
        gh("release", "edit", tag, "--repo", repo, "--draft=false")
    metadata = release(repo, tag)
    if metadata["draft"] or not set(names) <= {asset["name"] for asset in metadata["assets"]}:
        raise ValueError("Release is not public with all three required assets")
    print(metadata["html_url"])


if __name__ == "__main__":
    main()
