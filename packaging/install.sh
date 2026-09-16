#!/usr/bin/env bash
# Installs InfoPanel for the current user (~/.local) + udev rules (needs sudo).
set -euo pipefail

cd "$(dirname "$0")"

APP_DIR="$HOME/.local/opt/infopanel"
BIN_DIR="$HOME/.local/bin"
DESKTOP_DIR="$HOME/.local/share/applications"

echo "Installing InfoPanel to $APP_DIR"
mkdir -p "$APP_DIR" "$BIN_DIR" "$DESKTOP_DIR"
cp -r infopanel/. "$APP_DIR/"
ln -sf "$APP_DIR/infopanel" "$BIN_DIR/infopanel"

sed "s|^Exec=.*|Exec=$APP_DIR/infopanel|" infopanel.desktop > "$DESKTOP_DIR/infopanel.desktop"

ICON_DIR="$HOME/.local/share/icons/hicolor/256x256/apps"
mkdir -p "$ICON_DIR"
cp infopanel.png "$ICON_DIR/infopanel.png" 2>/dev/null || true
gtk-update-icon-cache -f "$HOME/.local/share/icons/hicolor" 2>/dev/null || true

echo "Installing udev rules (sudo required, grants USB panel access)"
sudo cp infopanel-udev.rules /etc/udev/rules.d/99-infopanel.rules
sudo udevadm control --reload-rules
sudo udevadm trigger

# SMART drive health sensors: a root systemd timer dumps smartctl JSON to
# /run/infopanel/smart.json, which the bundled Drive Health plugin reads.
if command -v smartctl >/dev/null 2>&1; then
    echo "Installing SMART sensor timer (sudo required)"
    sudo install -D -m 0755 infopanel-smart-dump.sh /usr/local/lib/infopanel/infopanel-smart-dump.sh
    sudo cp infopanel-smart.service infopanel-smart.timer /etc/systemd/system/
    sudo systemctl daemon-reload
    sudo systemctl enable --now infopanel-smart.timer
else
    echo "smartmontools not found - skipping SMART sensors (install it and re-run for drive health sensors)"
fi

echo
echo "Done. Replug your panels once (or reboot) so the udev rules apply, then run: infopanel"
