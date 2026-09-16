#!/usr/bin/env bash
set -euo pipefail
source /etc/os-release
case "${1:?Missing target}:$ID:${VERSION_ID:-}" in
    # Arch is rolling; official images may set VERSION_ID to a build date.
    deb:ubuntu:26.04|rpm:fedora:44|arch:arch:*) ;;
    *) echo "Wrong container: ${1} requires Ubuntu 26.04, Fedora 44 or Arch respectively; got $PRETTY_NAME" >&2; exit 1 ;;
esac
[[ "$(uname -m)" == x86_64 ]]
