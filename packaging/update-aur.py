#!/usr/bin/env python3
"""Update the two source checksums and version. Does not generate .SRCINFO."""
import hashlib
import re
import sys
from pathlib import Path

from version import version


def replace_once(pattern, replacement, content):
    result, count = re.subn(pattern, replacement, content, flags=re.MULTILINE)
    if count != 1:
        raise ValueError(f"Expected one {pattern!r}, found {count}")
    return result


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


if __name__ == "__main__":
    if len(sys.argv) != 4:
        sys.exit("Usage: update-aur.py AUR_DIR VERSION TARBALL")
    directory, requested, tarball = Path(sys.argv[1]), version(sys.argv[2]), Path(sys.argv[3])
    pkgbuild = directory / "PKGBUILD"
    content = replace_once(r"^pkgver=.*$", f"pkgver={requested}", pkgbuild.read_text())
    content = replace_once(r"^pkgrel=.*$", "pkgrel=1", content)
    content = replace_once(
        r"^sha256sums=\([^)]*\)",
        f"sha256sums=('{digest(tarball)}'\n            '{digest(directory / 'stage-package.sh')}')",
        content,
    )
    pkgbuild.write_text(content)
