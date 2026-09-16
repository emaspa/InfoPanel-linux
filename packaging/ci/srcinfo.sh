#!/usr/bin/env bash
# /aur is a disposable writable copy, or the local aur directory for regeneration.
set -euo pipefail
pacman -Syu --noconfirm --needed pacman binutils
useradd --create-home builder
cd /aur
# --printsrcinfo only reads the PKGBUILD, but makepkg still requires every
# output directory to be writable. /aur belongs to the host caller (uid 1001 on
# GitHub runners), not to builder, so point them all at a directory builder owns.
scratch="$(runuser -u builder -- mktemp -d)"
# Generate into a temporary file first: a redirect straight to .SRCINFO would
# truncate the tracked file before makepkg runs, and leave it empty on failure.
runuser -u builder -- env BUILDDIR="$scratch" PKGDEST="$scratch" SRCDEST="$scratch" \
    SRCPKGDEST="$scratch" LOGDEST="$scratch" makepkg --printsrcinfo > "$scratch/.SRCINFO"
test -s "$scratch/.SRCINFO"
# Root writes the result, so the caller's file keeps its owner.
cat "$scratch/.SRCINFO" > .SRCINFO
