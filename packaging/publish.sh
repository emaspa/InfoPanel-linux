#!/usr/bin/env bash
# Builds a self-contained linux-x64 release tarball of InfoPanel.
set -euo pipefail

cd "$(dirname "$0")/.."

VERSION="${1:-0.0.1}"
OUT="artifacts/infopanel-${VERSION}-linux-x64"

rm -rf "$OUT"
mkdir -p "$OUT"

dotnet publish src/InfoPanel.App/InfoPanel.App.csproj \
    -c Release -r linux-x64 --self-contained \
    -p:PublishSingleFile=false \
    -o "$OUT/infopanel"

cp packaging/infopanel-udev.rules "$OUT/"
cp packaging/infopanel.desktop "$OUT/"
cp packaging/infopanel-smart-dump.sh packaging/infopanel-smart.service packaging/infopanel-smart.timer "$OUT/"
cp src/InfoPanel.App/Assets/logo.png "$OUT/infopanel.png"
cp packaging/install.sh "$OUT/"
chmod +x "$OUT/install.sh" "$OUT/infopanel/infopanel"

tar -C artifacts -czf "artifacts/infopanel-${VERSION}-linux-x64.tar.gz" "$(basename "$OUT")"
echo "Built artifacts/infopanel-${VERSION}-linux-x64.tar.gz"
