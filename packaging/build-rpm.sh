#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
version="$(python3 packaging/version.py "${1:?Usage: build-rpm.sh VERSION}")"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work"/{BUILD,BUILDROOT,RPMS,SOURCES,SPECS,SRPMS}
cp "artifacts/infopanel-$version-linux-x64.tar.gz" aur/stage-package.sh "$work/SOURCES/"
# Match the deb's upstream release entry and timestamp, without depending on
# the build host's locale for RPM's required English changelog date format.
cp packaging/rpm/infopanel.spec "$work/SPECS/infopanel.spec"
cat >> "$work/SPECS/infopanel.spec" <<EOF

%changelog
* $(LC_ALL=C date -u -d "@${SOURCE_DATE_EPOCH:-$(date +%s)}" '+%a %b %d %Y') Emanuele Sparvoli <sparvoli@gmail.com> - $version-1
- Upstream release $version.
EOF
rpmbuild -bb --define "_topdir $work" --define "app_version $version" \
    --define "_tmppath $work" --dbpath "$work/rpmdb" \
    --define '_build_id_links none' "$work/SPECS/infopanel.spec"
cp "$work/RPMS/x86_64/infopanel-$version-1.x86_64.rpm" artifacts/
