#!/usr/bin/env python3
"""Refuse stale-tag downgrades and overwriting recipe changes made on main."""
import re
import sys
from pathlib import Path

from version import version


def field(text, name):
    match = re.search(rf"^{name}=(.*)$", text, flags=re.MULTILINE)
    if not match:
        raise ValueError(f"Missing {name}")
    return match[1].strip("'\"")


def recipe(text):
    text = re.sub(r"^(pkgver|pkgrel)=.*$", "", text, flags=re.MULTILINE)
    return re.sub(r"^sha256sums=\([^)]*\)", "", text, flags=re.MULTILINE)


if __name__ == "__main__":
    tagged, main, remote = map(Path, sys.argv[1:4])
    requested = version(sys.argv[4])
    wanted = tuple(map(int, requested.split(".")))
    for directory in (main, remote):
        content = (directory / "PKGBUILD").read_text()
        if field(content, "pkgname") != "infopanel-bin":
            sys.exit("Refusing to update a different AUR package")
        current = version(field(content, "pkgver"))
        if tuple(map(int, current.split("."))) > wanted:
            sys.exit(f"Refusing AUR downgrade from {current} to {requested}")
        if current == requested and int(field(content, "pkgrel")) > 1:
            sys.exit("Refusing to reset an already revised package of this version")
    if recipe((tagged / "PKGBUILD").read_text()) != recipe((main / "PKGBUILD").read_text()):
        sys.exit("aur/PKGBUILD changed on main since this tag; reconcile it before retrying")
    for name in ("stage-package.sh", "infopanel-bin.install"):
        if (tagged / name).read_bytes() != (main / name).read_bytes():
            sys.exit(f"aur/{name} changed on main since this tag; reconcile it before retrying")
