#!/usr/bin/env bash
set +x
set -euo pipefail
cd "$(dirname "$0")/.."
tag="${1:?Usage: publish-aur.sh vX.Y.Z}"
[[ "$tag" == v* ]]
version="$(python3 packaging/version.py "$tag")"
: "${GH_REPO:?Set GH_REPO}" "${GH_TOKEN:?Set GH_TOKEN}" "${AUR_SSH_PRIVATE_KEY:?Set AUR_SSH_PRIVATE_KEY}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
umask 077
printf '%s\n' "$AUR_SSH_PRIVATE_KEY" > "$work/aur-key"
unset AUR_SSH_PRIVATE_KEY

# Pin the fingerprint published by Arch, not whatever ssh-keyscan returns.
# https://archlinux.org/news/aur-migration-new-ssh-hostkeys/
ssh-keyscan -T 20 -t ed25519 aur.archlinux.org > "$work/known_hosts" 2>/dev/null
fingerprints="$(ssh-keygen -lf "$work/known_hosts" -E sha256 | awk '{print $2}' | sort -u)"
[[ "$fingerprints" == 'SHA256:RFzBCUItH9LZS0cKB5UE6ceAYhBD5C8GeOBip8Z11+4' ]] || {
    echo 'AUR host key does not match the pinned Ed25519 fingerprint' >&2; exit 1;
}
printf -v GIT_SSH_COMMAND 'ssh -i %q -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=yes -o UserKnownHostsFile=%q -o HostKeyAlgorithms=ssh-ed25519' "$work/aur-key" "$work/known_hosts"
export GIT_SSH_COMMAND

# Fail if the existing package has disappeared; never initialize a new AUR repo.
git clone ssh://aur@aur.archlinux.org/infopanel-bin.git "$work/aur-remote"
git -C "$work/aur-remote" rev-parse --verify HEAD >/dev/null
test -s "$work/aur-remote/PKGBUILD"
test -s "$work/aur-remote/.SRCINFO"
git -c credential.helper='!gh auth git-credential' clone --branch main --single-branch \
    "https://github.com/$GH_REPO.git" "$work/main"
python3 packaging/check-aur-sync.py aur "$work/main/aur" "$work/aur-remote" "$version"

# Download anonymously from the public release URL. This deliberately does not
# use the local build or a token that could see an unpublished draft asset.
tarball="$work/infopanel-$version-linux-x64.tar.gz"
curl --fail --location --retry 5 --proto '=https' --proto-redir '=https' \
    "https://github.com/$GH_REPO/releases/download/$tag/infopanel-$version-linux-x64.tar.gz" -o "$tarball"
python3 packaging/update-aur.py "$work/main/aur" "$version" "$tarball"
# Allow the container's unprivileged makepkg process to read this one directory.
chmod 755 "$work" "$work/main" "$work/main/aur"
chmod 644 "$work/main/aur/PKGBUILD"
docker run --rm --platform linux/amd64 \
    -v "$PWD:/repo:ro" -v "$work/main/aur:/aur" \
    archlinux:base bash /repo/packaging/ci/srcinfo.sh

for file in PKGBUILD .SRCINFO infopanel-bin.install stage-package.sh; do
    install -m644 "$work/main/aur/$file" "$work/aur-remote/$file"
done
for repo in "$work/main" "$work/aur-remote"; do
    git -C "$repo" config user.name 'InfoPanel release bot'
    git -C "$repo" config user.email '41898282+github-actions[bot]@users.noreply.github.com'
done
# main first: a protected-branch rejection must not leave AUR ahead of this repo.
# No force pushes. A concurrent main update fails safely; rerun after reviewing.
git -C "$work/main" add aur/PKGBUILD aur/.SRCINFO
if ! git -C "$work/main" diff --cached --quiet; then
    git -C "$work/main" commit -m "aur: update infopanel-bin to $version"
    git -C "$work/main" -c credential.helper='!gh auth git-credential' push origin HEAD:main
fi
git -C "$work/aur-remote" add PKGBUILD .SRCINFO infopanel-bin.install stage-package.sh
if ! git -C "$work/aur-remote" diff --cached --quiet; then
    git -C "$work/aur-remote" commit -m "Update infopanel-bin to $version"
    git -C "$work/aur-remote" push origin HEAD:master
fi
echo "AUR and main are synchronized at $version"
