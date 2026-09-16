# Packaging and releases

`packaging/publish.sh` is the only .NET publish/payload assembly step. Ubuntu
26.04 builds the self-contained `linux-x64` tarball and deb. Fedora 44 builds
the rpm from that same tarball. Arch builds `infopanel-bin` from it for testing.
`aur/stage-package.sh` stages all three packages, including the one shared
SMART unit path adaptation and desktop entry adaptation. It is an AUR source
file with a checksum. The tarball's name, layout, installer and `/usr/local`
SMART service path stay unchanged.

Before publishing, `publish.sh` restores Extras, AudioSpectrum and StopWatch
individually: App builds them through MSBuild tasks rather than project
references, so its implicit restore does not include them. Separate restore
processes avoid MSBuild's same-evaluation restore/build pitfalls and work with
the CI source copy, which omits `tests/`. Hand-run publishing still accepts a
dirty developer tree.

The tarball and packages use `packaging/infopanel.png`, a real 256×256 RGBA PNG
resampled once from `src/InfoPanel.App/Assets/logo.png` with Pillow's Lanczos
filter. Regenerate this asset when the logo changes; packaging needs no image
tools. The SMART helper uses `/bin/bash` directly, including in the tarball,
so its root systemd unit does not resolve the interpreter through `PATH`.

The package layout is `/opt/infopanel`, `/usr/bin/infopanel`, and the same
`/usr/lib` and `/usr/share` integration files as AUR. Licences are in
`/usr/share/licenses/infopanel` (rpm), `/usr/share/licenses/infopanel-bin` (AUR)
and `/usr/share/doc/infopanel` (deb). The SMART timer is opt-in. Package removal
does not remove user profiles.

## Dependencies and build targets

| AUR dependency | Ubuntu 26.04 | Fedora 44 |
| --- | --- | --- |
| glibc | libc6 | glibc |
| gcc-libs | libgcc-s1, libstdc++6 | libgcc, libstdc++ |
| zlib | zlib1g | zlib-ng-compat |
| icu | libicu78 | libicu |
| fontconfig | libfontconfig1 | fontconfig |
| libx11 | libx11-6 | libX11 |
| libice | libice6 | libICE |
| libsm | libsm6 | libSM |
| libxcursor | libxcursor1 | libXcursor |
| libxext | libxext6 | libXext |
| libxi | libxi6 | libXi |
| libxrandr | libxrandr2 | libXrandr |
| ffmpeg (optional) | ffmpeg | ffmpeg-free |
| smartmontools (optional) | smartmontools | smartmontools |
| pipewire-pulse or pulseaudio (optional) | pipewire-pulse or pulseaudio | pipewire-pulseaudio or pulseaudio |
| Audio CLI tools (optional) | pulseaudio-utils | pulseaudio-utils |

The runtime dependency list starts with `aur/PKGBUILD`; keep this mapping and
the deb/rpm metadata in sync when it changes. Optional features use `Suggests`
on deb/rpm and `optdepends` on Arch. Fedora's `ffmpeg-free` has a restricted
codec set. RPM automatic ELF dependencies/provides and stripping are disabled
because private .NET/NuGet libraries must remain private and intact. Clean
container installation tests check the explicit runtime dependencies. Both
package changelogs use the release version and `SOURCE_DATE_EPOCH` (or current
UTC time when unset); the RPM entry uses release `1` and an English date.
Debian's systemd/udev maintainer actions are best effort, including when those
commands are absent or fail, so they cannot block installation or removal.

The [Ubuntu official tags](https://hub.docker.com/_/ubuntu/tags) list `26.04`
and the [Fedora official image](https://hub.docker.com/_/fedora) lists `44`,
including amd64. Both were checked on 2026-09-16; no Fedora fallback was used.
Ubuntu's archive lists [libicu78](https://packages.ubuntu.com/resolute/libicu-dev)
and [dotnet-sdk-10.0](https://packages.ubuntu.com/km/resolute/dotnet-sdk-10.0).
Fedora lists [zlib-ng-compat for 44](https://packages.fedoraproject.org/pkgs/zlib-ng/zlib-ng-compat/fedora-44-updates-testing.html).

Builds and installs assert `/etc/os-release` and x86_64. The hosted runner is
only a Docker/Actions orchestrator; it is not the target OS. Distribution tags
and repository updates are mutable, so this pins the tested distribution/ABI
family, not a byte-for-byte snapshot of every dependency. Arch is rolling, so
its check accepts any `VERSION_ID`, including official image build dates.

## One-time maintainer setup

1. Confirm the existing AUR package is still owned by AUR account
   `emaspa`: visit `https://aur.archlinux.org/packages/infopanel-bin` while logged
   in. Do not create a second package. Its push URL is
   `ssh://aur@aur.archlinux.org/infopanel-bin.git`.
2. Generate a dedicated automation key on your own machine, outside the repo:

   ```bash
   ssh-keygen -t ed25519 -N '' -C 'InfoPanel GitHub Actions AUR' \
     -f ~/.ssh/infopanel-aur-actions
   ```

   In the AUR website, log in as `emaspa`, open **My Account**, and add the full
   contents of `~/.ssh/infopanel-aur-actions.pub` to **SSH Public Key**. Keep the
   private key private. This key authenticates as the AUR account, so it has
   that account's package-maintainer access.
3. In `emaspa/InfoPanel-linux` → **Settings → Secrets and variables → Actions →
   New repository secret**, create **`AUR_SSH_PRIVATE_KEY`**. Paste the entire
   private file, including its BEGIN/END lines and newlines. Or, authenticated
   with `gh` as the repository owner, use:

   ```bash
   gh secret set AUR_SSH_PRIVATE_KEY --repo emaspa/InfoPanel-linux \
     < ~/.ssh/infopanel-aur-actions
   ```

   The secret currently holds the maintainer's existing AUR key
   (`~/.ssh/aur`), which is registered account-wide, so a dedicated key is
   optional. Whichever key is in the secret is the one to pass as
   `AUR_SSH_PRIVATE_KEY` when running `publish-aur.sh` by hand (see
   [Retries and partial failures](#retries-and-partial-failures)).

   There are no other custom secrets. `GITHUB_TOKEN` is supplied by GitHub;
   build jobs request `contents: read`, and only GitHub release/AUR sync jobs
   request `contents: write`. PR jobs receive neither this SSH key nor a write
   token. No shell tracing is enabled around the key, and its temporary file is
   deleted on exit.
4. Enable Actions and permit the pinned `actions/checkout`, `upload-artifact`
   and `download-artifact` actions. Repository/organization policies must allow
   the publishing jobs' requested token permissions. Check `main` branch
   protections/rulesets: they must permit the Actions bot's direct metadata
   commit. A rule requiring every commit to arrive through a PR, signed commits,
   or required status checks can reject that push unless explicitly configured
   to allow it. This workflow does not bypass protections or use a PAT. Resolve
   that policy before releasing; otherwise the AUR job will fail before its AUR
   push. (Optional, not currently configured.) Protect `v*` tag creation so
   only trusted maintainers can release.
5. Run **Actions → Packaging check → Run workflow** on `main` and review all
   three package jobs and lint output. If configuring required PR checks, note
   that this workflow has path filters; do not require a skipped workflow on
   documentation-only PRs without an always-running wrapper check.

Read-only commands to confirm the live upstream facts before setup:

```bash
gh api repos/emaspa/InfoPanel-linux --jq '{private, default_branch}'
gh release view --repo emaspa/InfoPanel-linux --json tagName,assets
curl -fsSL 'https://aur.archlinux.org/rpc/v5/info?arg%5B%5D=infopanel-bin'
git ls-remote https://aur.archlinux.org/infopanel-bin.git HEAD refs/heads/master
```

The AUR RPC result should name `infopanel-bin` and `Maintainer: emaspa`.
Publishing jobs are restricted to `emaspa/InfoPanel-linux`; forks can run the
build checks without publishing.

The SSH connection verifies the Ed25519 fingerprint
`SHA256:RFzBCUItH9LZS0cKB5UE6ceAYhBD5C8GeOBip8Z11+4`, published by
[Arch](https://archlinux.org/news/aur-migration-new-ssh-hostkeys/).
`ssh-keyscan` only retrieves a candidate key; a mismatched fingerprint aborts
before authentication. After a legitimate AUR host-key rotation, independently
verify Arch's announcement and update the pin in `packaging/publish-aur.sh`.

## Release procedure

1. Set `<Version>` in `src/InfoPanel.App/InfoPanel.App.csproj` to the next
   `X.Y.Z`, with no prerelease suffix or leading zeroes. Push (or merge) the
   change, then run **Actions → Packaging check → Run workflow** on `main` and
   wait for all three package jobs, unless the change arrived through a pull
   request that already ran it: the check never runs on direct pushes. Do not
   manually bump AUR `pkgver` to an unpublished release: the publisher handles
   that after upload.
2. From up-to-date `main`, run these commands, substituting the chosen version:

   ```bash
   python3 packaging/version.py v0.3.1
   git tag -a v0.3.1 -m 'InfoPanel 0.3.1'
   git push linux v0.3.1
   ```

   Push the tag to the `emaspa/InfoPanel-linux` remote. In the maintainer's
   clone that remote is named `linux`; `origin` there is the Windows fork, and a
   tag pushed to it triggers nothing.

   These are maintainer instructions; the automation implementation does not
   create or push your tag. The tag must contain the release workflow.
3. Optionally prepare a GitHub draft release for that tag with your title and
   notes. The workflow preserves existing title/body, uploads missing assets,
   and publishes a draft after all assets are present. With no existing release
   it generates notes in a new draft, attaches assets, then publishes it.
4. Check that the release has exactly these required files:

   ```text
   infopanel-<version>-linux-x64.tar.gz
   infopanel_<version>_amd64.deb
   infopanel-<version>-1.x86_64.rpm
   ```

   The tested Arch package is retained as an Actions artifact, not a fourth
   GitHub release asset. The AUR recipe references the tarball.
5. Check the AUR job. It clones the existing AUR repository and current `main`,
   refuses downgrades or unrelated recipe drift, downloads the **public**
   tarball without authentication, hashes it, resets `pkgrel=1`, and regenerates
   `.SRCINFO` with `makepkg --printsrcinfo` in Arch. It commits the two metadata
   files to `main`, then pushes the same recipe, `.SRCINFO`, staging helper and
   install script to the AUR `master` branch. Pull `main` before your next change.

All artifact builders check the explicit version against the csproj. A pushed
tag matching the broad `v*` trigger but not strict `vX.Y.Z`, or any mismatch,
fails before publishing.

## Retries and partial failures

Use GitHub's **Re-run failed jobs** or **Re-run all jobs** for the same tag.
Existing assets are downloaded before packaging, reused and linted/installed
again. This avoids replacing an already consumed tarball with a nonidentical
rebuild. The publisher compares existing assets against the validated copies;
a concurrent change fails instead of overwriting. Existing notes are never
regenerated. Fixes to already released binaries require a new version/tag.

A re-run always executes the workflow and the `packaging/` scripts **from the
tagged commit**, so it only helps with transient failures. It cannot pick up a
script fix made on `main` afterwards.

`main` is pushed before AUR. If that push is blocked, AUR remains unchanged.
If the AUR push fails for a transient reason (network, AUR outage, a concurrent
`main` commit), `main` may be ahead temporarily; re-running the `aur` job
repairs the AUR without an empty commit on `main`.

If the GitHub release is already public and the AUR job failed because of a
bug in the scripts, do **not** delete or move the tag. Fix the script on
`main`, then run the AUR step locally from an up-to-date `main` checkout with
Docker available:

```bash
GH_REPO=emaspa/InfoPanel-linux GH_TOKEN="$(gh auth token)" \
  AUR_SSH_PRIVATE_KEY="$(cat ~/.ssh/aur)" bash packaging/publish-aur.sh vX.Y.Z
```

It clones `main` fresh and downloads the public tarball, so it does not depend
on the tagged checkout. It applies the same guards, commits `aur/PKGBUILD` and
`aur/.SRCINFO` to `main`, and pushes the AUR. This is how `v0.3.1` reached the
AUR, after a runner file-ownership bug in `packaging/ci/srcinfo.sh`.

Only while the release is still an unpublished draft may the tag be deleted
and re-pushed at a fixed commit. That was done for `v0.3.1` after the
`release.py` draft-lookup fix, before anything had been published.

Neither repository is force-pushed.
Concurrent changes fail safely and require review/retry. An old tag cannot
downgrade a newer `pkgver` or reset an existing same-version `pkgrel > 1`.
Changes to the recipe/staging helper/install script on `main` after the tag
also stop publishing, rather than being overwritten.

Release workflows share a concurrency group. Push one release tag at a time:
GitHub retains only one pending run per group and may replace an older pending
run when another tag arrives. Wait for release and AUR completion before the
next tag.

## Local validation before trusting a release

Run from the repository root on a machine with working Docker and networking.
The release/version/retry checks can also run without Docker or network:
`python3 -m unittest discover -s packaging/tests -v`.
The build containers mount the source read-only, copy only explicit inputs
(never `graphify-out/`), and write packages to `artifacts/`. They install their
own SDK/lint tools. `VERSION` comes from the csproj. These commands publish
nothing and do not require secrets:

```bash
cd /home/emanuele/infopanel-v2
export VERSION="$(python3 packaging/version.py)"
export SOURCE_DATE_EPOCH="$(git show -s --format=%ct HEAD)"
mkdir -p artifacts

docker pull --platform linux/amd64 ubuntu:26.04
docker pull --platform linux/amd64 fedora:44

# Publish tarball, build deb, and run lintian in Ubuntu 26.04.
docker run --rm --platform linux/amd64 \
  -v "$PWD:/repo:ro" -v "$PWD/artifacts:/out" \
  -e VERSION -e SOURCE_DATE_EPOCH \
  ubuntu:26.04 bash /repo/packaging/ci/build.sh deb

# Fresh Ubuntu: install declared dependencies and run the installed binary.
docker run --rm --platform linux/amd64 \
  -v "$PWD:/repo:ro" -v "$PWD/artifacts:/out:ro" -e VERSION \
  ubuntu:26.04 bash /repo/packaging/ci/install.sh deb

# Repackage the same tarball and run rpmlint inside Fedora 44.
docker run --rm --platform linux/amd64 \
  -v "$PWD:/repo:ro" -v "$PWD/artifacts:/out" \
  -e VERSION -e SOURCE_DATE_EPOCH \
  fedora:44 bash /repo/packaging/ci/build.sh rpm

# Fresh Fedora: install declared dependencies and run the installed binary.
docker run --rm --platform linux/amd64 \
  -v "$PWD:/repo:ro" -v "$PWD/artifacts:/out:ro" -e VERSION \
  fedora:44 bash /repo/packaging/ci/install.sh rpm
```

Immediately after package installation, before installing any test tools, each
install check requires `ldconfig -p` to resolve `libXcursor.so.1`, `libXext.so.6`,
`libXi.so.6` and `libXrandr.so.2`. This prevents later tooling from masking an
undeclared X11 dependency. In Ubuntu, the test first removes the minimized
image's `/etc/dpkg/dpkg.cfg.d/excludes` so dpkg installs documentation as on a
desktop, including the checked licence files.

Each install command must print `PASS` after `/usr/bin/infopanel --dump-sensors`
exits zero within 120 seconds and logs more than zero plugin sensors. It runs
as a new unprivileged user in that user's home, without `INFOPANEL_DATA_DIR`
or `XDG_DATA_HOME` and with `~/.local/share` absent before launch. The application
must create its XDG plugin directory itself without leaving an `InfoPanel/`
directory in the working directory. The plugin count may vary by container;
few/no hardware sensors are expected. These checks validate initialization and
package paths, not a real graphical session, USB device or running systemd.
Suggested/weak optional dependencies are deliberately not installed.

Resolver and path-isolation unit tests are in `DataDirectoryTests.cs`. On a
machine that can run .NET tests, use
`dotnet test tests/InfoPanel.Core.Tests/InfoPanel.Core.Tests.csproj -p:AllowMissingPrunePackageData=true`.
The data resolver is linked as source into Core (net10) and Extras (net8).
Plugins, plugin state and profile assets all follow `ConfigPersistence.BaseFolder`;
`INFOPANEL_DATA_DIR` also redirects Extras' INI even in a writable installation.
Normal developer runs retain the legacy installation-local Extras INI behavior.
No old data is migrated automatically: users of an existing override must copy
any wanted plugins/state/assets from their previous default data folder.

Check AUR generation/build/install as well:

```bash
# Regenerate the tracked file with makepkg, not a hand edit.
docker run --rm --platform linux/amd64 \
  -v "$PWD:/repo:ro" -v "$PWD/aur:/aur" \
  archlinux:base bash /repo/packaging/ci/srcinfo.sh
git diff -- aur/.SRCINFO

docker run --rm --platform linux/amd64 \
  -v "$PWD:/repo:ro" -v "$PWD/artifacts:/out" \
  -e VERSION -e SOURCE_DATE_EPOCH \
  archlinux:base bash /repo/packaging/ci/build.sh arch
docker run --rm --platform linux/amd64 \
  -v "$PWD:/repo:ro" -v "$PWD/artifacts:/out:ro" -e VERSION \
  archlinux:base bash /repo/packaging/ci/install.sh arch
```

CI diffs checked-in `.SRCINFO` against `makepkg --printsrcinfo` before testing
an unpublished version in a disposable copy. When changing `stage-package.sh`,
update its second `sha256sums` entry in the PKGBUILD (`sha256sum
aur/stage-package.sh`), then regenerate `.SRCINFO`. Preserve the published
tarball checksum. The release publisher computes both source checksums and
regenerates metadata automatically.

Lintian runs its full binary-package checks with errors fatal. Its overrides
are limited to the intentional `/opt` layout, unstripped upstream native files
and specific libraries embedded in Skia/.NET. Warnings about the existing USB
rules, missing AppStream/manual pages and direct systemctl
scriptlets remain visible. `rpmlint` also runs with its exit status enforced;
its filters cover unstripped private binaries, private library RPATHs, the
intentional `/opt/infopanel` layout, explicit host-library requirements needed
with `AutoReqProv: no`, and shared DLL copies in independently loaded plugins.
The missing RPM changelog and env-based helper interpreter are fixed in the
payload/build rather than filtered.
`namcap` runs on the PKGBUILD with both command failures and emitted `E:`
diagnostics fatal (some namcap versions return zero after reporting errors).

Offline packaging tests (`packaging/tests/`) cover version rejection, note
preservation, asset reuse, draft lookup by listing, id-based publishing of a
new draft, checksum updates and
downgrade/recipe-drift guards. Local package
assembly, metadata inspection and lint checks are useful when Docker is
unavailable, but do not replace the clean target-container builds and installs
above. Verify `.SRCINFO` with real `makepkg --printsrcinfo` after recipe changes.
Actions execution, AUR SSH authentication and branch write permissions also
require validation in the actual release environment.
