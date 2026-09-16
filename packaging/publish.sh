#!/usr/bin/env bash
# Builds a self-contained linux-x64 release tarball of InfoPanel.
set -euo pipefail

cd "$(dirname "$0")/.."

VERSION="$(python3 packaging/version.py "${1:-$(python3 packaging/version.py)}")"
OUT="artifacts/infopanel-${VERSION}-linux-x64"

rm -rf "$OUT"
mkdir -p "$OUT"

# AllowMissingPrunePackageData: distro-packaged SDKs (e.g. Arch dotnet-sdk)
# ship without the prune package data and fail with NETSDK1226 otherwise;
# harmless on official SDKs.
# App builds these plugins through MSBuild tasks, not ProjectReferences, so
# publish's implicit restore never visits them. Restore in separate evaluations
# before publishing; a solution restore would also require the omitted tests/.
for plugin in InfoPanel.Extras InfoPanel.AudioSpectrum InfoPanel.StopWatch; do
    dotnet restore "src/$plugin/$plugin.csproj" -p:AllowMissingPrunePackageData=true
done
dotnet publish src/InfoPanel.App/InfoPanel.App.csproj \
    -m:1 \
    -c Release -r linux-x64 --self-contained \
    -p:PublishSingleFile=false \
    -p:AllowMissingPrunePackageData=true \
    -o "$OUT/infopanel"

# GPLv3 section 4 requires a copy of the license with every distribution;
# LICENSES.md carries the bundled third-party notices (MIT/Apache attribution).
cp LICENSE LICENSES.md "$OUT/"
cp packaging/infopanel-udev.rules "$OUT/"
cp packaging/infopanel.desktop "$OUT/"
cp packaging/infopanel-smart-dump.sh packaging/infopanel-smart.service packaging/infopanel-smart.timer "$OUT/"
cp packaging/infopanel.png "$OUT/infopanel.png"
cp packaging/install.sh "$OUT/"
chmod +x "$OUT/install.sh" "$OUT/infopanel/infopanel"

tar -C artifacts -czf "artifacts/infopanel-${VERSION}-linux-x64.tar.gz" "$(basename "$OUT")"
echo "Built artifacts/infopanel-${VERSION}-linux-x64.tar.gz"
