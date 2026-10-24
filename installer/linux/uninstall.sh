#!/bin/sh
# Removes the desktop integration created by install.sh. The package tree itself is left in place -
# delete this folder to remove the rest.
set -eu

BIN="${XDG_BIN_HOME:-$HOME/.local/bin}"
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICONS="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"

rm -f "$BIN/videojanai"
rm -f "$APPS/videojanai.desktop"
rm -f "$ICONS/videojanai.png"
update-desktop-database "$APPS" 2>/dev/null || true

echo "Removed launcher symlink + desktop entry."
echo "Delete this folder to remove VideoJaNai itself; app settings under"
echo "  ~/.config/VideoJaNai remain."
