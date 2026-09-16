#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
version="$(python3 packaging/version.py "${1:?Usage: build-deb.sh VERSION}")"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
tar -xzf "artifacts/infopanel-$version-linux-x64.tar.gz" -C "$work"
root="$work/package"
bash aur/stage-package.sh "$work/infopanel-$version-linux-x64" "$root" usr/share/doc/infopanel
install -d "$root/DEBIAN"
installed_size="$(du -sk "$root" | cut -f1)"
cat > "$root/DEBIAN/control" <<EOF
Package: infopanel
Version: $version
Section: utils
Priority: optional
Architecture: amd64
Maintainer: Emanuele Sparvoli <sparvoli@gmail.com>
Installed-Size: $installed_size
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libicu78, libfontconfig1, libx11-6, libice6, libsm6, libxcursor1, libxext6, libxi6, libxrandr2
Suggests: ffmpeg, smartmontools, pipewire-pulse | pulseaudio, pulseaudio-utils
Homepage: https://github.com/emaspa/InfoPanel-linux
Description: Hardware monitoring dashboards for Linux
 Display hardware sensors on desktop overlays, USB LCD panels and web browsers.
 Includes the .NET runtime and bundled InfoPanel plugins.
EOF
cat > "$root/usr/share/doc/infopanel/copyright" <<'EOF'
Upstream-Name: InfoPanel Linux
Source: https://github.com/emaspa/InfoPanel-linux
License: GPL-3.0-only
Copyright: InfoPanel contributors; bundled libraries are credited in LICENSES.md.
 On Debian systems the GPL version 3 is in /usr/share/common-licenses/GPL-3.
 See LICENSE in this directory for the complete GPL version 3 text.
 See LICENSES.md for bundled third-party copyright and license notices.
EOF
cat > "$work/changelog" <<EOF
infopanel ($version) resolute; urgency=medium

  * Upstream release $version.

 -- Emanuele Sparvoli <sparvoli@gmail.com>  $(date -u -d "@${SOURCE_DATE_EPOCH:-$(date +%s)}" -R)
EOF
gzip -n -9 -c "$work/changelog" > "$root/usr/share/doc/infopanel/changelog.gz"
install -Dm644 packaging/debian/lintian-overrides "$root/usr/share/lintian/overrides/infopanel"
install -m755 packaging/debian/postinst packaging/debian/prerm packaging/debian/postrm "$root/DEBIAN/"
(cd "$root"; find opt usr -type f -print0 | sort -z | xargs -0 md5sum > DEBIAN/md5sums)
chmod -R go-w "$root"
mkdir -p artifacts
dpkg-deb --root-owner-group --build "$root" "artifacts/infopanel_${version}_amd64.deb"
