#!/usr/bin/env bash
# Fresh runtime container: install only the package and its declared dependencies.
set -euo pipefail
target="${1:?Usage: install.sh deb|rpm|arch}"
bash /repo/packaging/ci/check-target.sh "$target"
version="${VERSION:?Set VERSION}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]
case "$target" in
    deb)
        export DEBIAN_FRONTEND=noninteractive
        # The minimized Docker image excludes most documentation at unpack time.
        # Model a desktop install so the package's licence files are installed.
        rm -f /etc/dpkg/dpkg.cfg.d/excludes
        apt-get update
        apt-get install -y --no-install-recommends "/out/infopanel_${version}_amd64.deb"
        [[ "$(dpkg-query -W -f='${Version}' infopanel)" == "$version" ]]
        licenses=/usr/share/doc/infopanel
        ;;
    rpm)
        dnf install -y --setopt=install_weak_deps=False "/out/infopanel-$version-1.x86_64.rpm"
        [[ "$(rpm -q --qf '%{VERSION}-%{RELEASE}' infopanel)" == "$version-1" ]]
        licenses=/usr/share/licenses/infopanel
        ;;
    arch)
        pacman -Syu --noconfirm
        pacman -U --noconfirm "/out/infopanel-bin-$version-1-x86_64.pkg.tar.zst"
        [[ "$(pacman -Q infopanel-bin)" == "infopanel-bin $version-1" ]]
        licenses=/usr/share/licenses/infopanel-bin
        ;;
esac
# Check before installing any test tools: their dependencies could otherwise
# hide a missing Avalonia X11 runtime dependency in the package metadata.
library_cache="$(ldconfig -p)"
for soname in libXcursor.so.1 libXext.so.6 libXi.so.6 libXrandr.so.2; do
    if ! awk -v soname="$soname" '$1 == soname { found = 1 } END { exit !found }' <<< "$library_cache"; then
        echo "Missing declared runtime library: $soname" >&2
        exit 1
    fi
done
case "$target" in
    deb) apt-get install -y --no-install-recommends passwd util-linux ;;
    rpm) dnf install -y --setopt=install_weak_deps=False shadow-utils util-linux ;;
esac
[[ "$(readlink /usr/bin/infopanel)" == /opt/infopanel/infopanel ]]
[[ -x /usr/lib/infopanel/infopanel-smart-dump.sh ]]
grep -qxF 'ExecStart=/usr/lib/infopanel/infopanel-smart-dump.sh' /usr/lib/systemd/system/infopanel-smart.service
grep -qxF 'Exec=/opt/infopanel/infopanel' /usr/share/applications/infopanel.desktop
for file in /usr/lib/udev/rules.d/99-infopanel.rules \
    /usr/lib/systemd/system/infopanel-smart.timer \
    /usr/share/icons/hicolor/256x256/apps/infopanel.png "$licenses/LICENSE" "$licenses/LICENSES.md"; do
    test -s "$file"
done
# Exercise a fresh desktop account, including creation of its XDG data directory.
# Do not pre-create ~/.local/share or redirect data to INFOPANEL_DATA_DIR.
useradd --create-home smoke
smoke_log="$(mktemp)"
trap 'rm -f "$smoke_log"' EXIT
runuser -u smoke -- env -u INFOPANEL_DATA_DIR -u XDG_DATA_HOME HOME=/home/smoke LC_ALL=C \
    bash -c 'set -euo pipefail; cd "$HOME"; test ! -e "$HOME/.local/share"; timeout 120 /usr/bin/infopanel --dump-sensors' \
    2>&1 | tee "$smoke_log"
test -d /home/smoke/.local/share/InfoPanel/plugins
test ! -e /home/smoke/InfoPanel
if ! grep -qE 'hwmon sensors, [1-9][0-9]* plugin sensors' "$smoke_log"; then
    echo 'Smoke test failed: expected more than zero plugin sensors' >&2
    exit 1
fi
echo "PASS: $target $version installed; fresh-user --dump-sensors exited 0 with plugin sensors"
