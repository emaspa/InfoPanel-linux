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

echo "Installing udev rules (sudo required, grants USB panel access to plugdev)"
sudo cp infopanel-udev.rules /etc/udev/rules.d/99-infopanel.rules
sudo udevadm control --reload-rules
sudo udevadm trigger

echo
echo "Done. Make sure your user is in the plugdev group, then run: infopanel"
