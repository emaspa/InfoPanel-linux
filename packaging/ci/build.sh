#!/usr/bin/env bash
# Run only inside the documented disposable build container (/repo:ro, /out:rw).
set -euo pipefail
target="${1:?Usage: build.sh deb|rpm|arch}"
bash /repo/packaging/ci/check-target.sh "$target"
export DEBIAN_FRONTEND=noninteractive DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
case "$target" in
    deb)
        apt-get update
        apt-get install -y --no-install-recommends dotnet-sdk-10.0 python3 dpkg-dev lintian ca-certificates
        ;;
    rpm)
        dnf install -y python3 rpm-build rpmlint tar gzip diffutils
        ;;
    arch)
        pacman -Syu --noconfirm --needed base-devel namcap python
        ;;
esac
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
inputs=(src packaging aur Directory.Build.props InfoPanel.slnx LICENSE LICENSES.md)
for config in Directory.Build.targets Directory.Packages.props global.json NuGet.Config nuget.config; do
    if [[ -f "/repo/$config" ]]; then inputs+=("$config"); fi
done
tar -C /repo --exclude=bin --exclude=obj -cf - "${inputs[@]}" | tar -C "$work" -xf -
cd "$work"
version="$(python3 packaging/version.py "${VERSION:?Set VERSION}")"
mkdir artifacts
tarball="infopanel-$version-linux-x64.tar.gz"
case "$target" in
    deb)
        if [[ "${REUSE_RELEASE_ASSETS:-0}" == 1 && -f "/out/$tarball" ]]; then
            cp "/out/$tarball" artifacts/
        else
            bash packaging/publish.sh "$version"
        fi
        deb="infopanel_${version}_amd64.deb"
        if [[ "${REUSE_RELEASE_ASSETS:-0}" == 1 && -f "/out/$deb" ]]; then
            cp "/out/$deb" artifacts/
        else
            bash packaging/build-deb.sh "$version"
        fi
        lintian --fail-on error "artifacts/$deb"
        cp "artifacts/$tarball" "artifacts/$deb" /out/
        outputs=("/out/$tarball" "/out/$deb")
        ;;
    rpm)
        cp "/out/$tarball" artifacts/
        rpm="infopanel-$version-1.x86_64.rpm"
        if [[ "${REUSE_RELEASE_ASSETS:-0}" == 1 && -f "/out/$rpm" ]]; then
            cp "/out/$rpm" artifacts/
        else
            bash packaging/build-rpm.sh "$version"
        fi
        # rpmlint exits nonzero on errors; warnings remain visible for review.
        rpmlint -c packaging/rpm/rpmlint.toml "artifacts/$rpm"
        cp "artifacts/$rpm" /out/
        outputs=("/out/$rpm")
        ;;
    arch)
        # makepkg must run as an ordinary user. All edits happen in this copy.
        useradd --create-home builder
        chmod 755 "$work"
        chown -R builder:builder aur
        (cd aur; runuser -u builder -- makepkg --printsrcinfo > "$work/current.SRCINFO")
        diff -u aur/.SRCINFO "$work/current.SRCINFO"
        # Verify the checked-in local-source checksum before updating the tarball.
        (cd aur; bash -c 'set -euo pipefail; source PKGBUILD; printf "%s  %s\n" "${sha256sums[1]}" stage-package.sh | sha256sum -c -')
        cp "/out/$tarball" "aur/$tarball"
        python3 packaging/update-aur.py aur "$version" "aur/$tarball"
        (cd aur; runuser -u builder -- makepkg --printsrcinfo > .SRCINFO)
        namcap aur/PKGBUILD | tee "$work/namcap.log"
        if grep -qE '(^|[[:space:]])E:' "$work/namcap.log"; then exit 1; fi
        # Dependencies belong in the fresh install container, not the builder.
        (cd aur; runuser -u builder -- makepkg --nodeps --cleanbuild --force --noconfirm)
        cp "aur/infopanel-bin-$version-1-x86_64.pkg.tar.zst" /out/
        outputs=("/out/infopanel-bin-$version-1-x86_64.pkg.tar.zst")
        ;;
esac
# Keep local artifacts usable by the owner of the bind-mounted output directory.
chown --reference=/out "${outputs[@]}"
