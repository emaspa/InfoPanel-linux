#!/usr/bin/env bash
# /aur is a disposable writable copy, or the local aur directory for regeneration.
set -euo pipefail
pacman -Syu --noconfirm --needed pacman binutils
useradd --create-home builder
cd /aur
# --printsrcinfo only reads the PKGBUILD. Root's redirection writes the result,
# so no change of ownership is necessary on the caller's files.
runuser -u builder -- makepkg --printsrcinfo > .SRCINFO
