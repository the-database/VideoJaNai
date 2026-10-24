#!/bin/sh
# VideoJaNai (Linux portable) installer.
#
# Run from inside the extracted package directory: `./install.sh`. It does NOT move the package - it
# registers the in-place tree with your desktop:
#   - a launcher symlink in ~/.local/bin
#   - a .desktop menu entry (~/.local/share/applications)
# then installs the TensorRT components for the detected GPU (and the RIFE models) into the tree.
# The tree stays writable, so the in-app updater and component manager keep working.
# Uninstall with ./uninstall.sh (the tree itself is removed by deleting it).
set -eu

HERE="$(cd "$(dirname "$0")" && pwd)"
APP="$HERE/VideoJaNai"
BIN="${XDG_BIN_HOME:-$HOME/.local/bin}"
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICONS="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"

if [ ! -x "$APP" ]; then
    echo "error: VideoJaNai not found at $APP (run this from the extracted package)" >&2
    exit 1
fi

mkdir -p "$BIN" "$APPS" "$ICONS"
ln -sf "$APP" "$BIN/videojanai"

icon_line="Icon=videojanai"
if [ -f "$HERE/videojanai.png" ]; then
    cp -f "$HERE/videojanai.png" "$ICONS/videojanai.png"
else
    icon_line=""
fi

cat > "$APPS/videojanai.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=VideoJaNai
Comment=Upscale videos with ONNX models on NVIDIA TensorRT
Exec=$APP
TryExec=$APP
Path=$HERE
$icon_line
Terminal=false
Categories=AudioVideo;Video;
StartupNotify=true
EOF
update-desktop-database "$APPS" 2>/dev/null || true

echo "Installed:"
echo "  launcher : $BIN/videojanai  ->  $APP"
echo "  menu     : $APPS/videojanai.desktop"
echo
echo "Installing the components for this GPU (TensorRT runtime + kernels, RIFE models)..."
"$HERE/VideoJaNaiUpdater" --auto || echo "Component install failed; VideoJaNai offers it again on first launch."
echo
case ":$PATH:" in
    *":$BIN:"*) ;;
    *) echo "NOTE: $BIN is not on your PATH; add it to use 'videojanai' from a shell." ;;
esac
echo "Launch 'VideoJaNai' from your application menu, or: $APP"
