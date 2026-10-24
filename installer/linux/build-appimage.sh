#!/bin/bash
# Builds the VideoJaNai AppImage from an assembled linux-x64 core tree plus the base TensorRT runtime
# pack (an AppImage can't install component packs into its read-only squashfs; AppRun fetches the
# GPU-matched builder kernels and RIFE into $XDG_DATA_HOME/VideoJaNai instead).
#
# Usage: build-appimage.sh <tree-dir> <version> [out-dir]
#
# The app is a self-contained .NET/Avalonia publish, so there is no dependency walk to do: the host
# provides glibc, the X11/Wayland + GL stack, fontconfig and the NVIDIA driver. Runs headless via
# APPIMAGE_EXTRACT_AND_RUN (no FUSE needed on CI runners).
set -euo pipefail

TREE="$1"
VER="$2"
OUT="${3:-.}"
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mktemp -d)"
APPDIR="$WORK/AppDir"
export APPIMAGE_EXTRACT_AND_RUN=1
export ARCH=x86_64

[ -d "$TREE" ] || { echo "tree not found: $TREE" >&2; exit 1; }

AT="$(command -v appimagetool || true)"
if [ -z "$AT" ]; then
    AT="$WORK/appimagetool-x86_64.AppImage"
    curl -fsSL "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage" -o "$AT"
    chmod +x "$AT"
fi

mkdir -p "$APPDIR"
cp -a "$TREE/." "$APPDIR/"
# The AppImage updates by replacing the file; no in-place updater.
rm -f "$APPDIR/VideoJaNaiUpdater"
install -m755 "$HERE/AppRun" "$APPDIR/AppRun"
chmod +x "$APPDIR/VideoJaNai" "$APPDIR/7zz" "$APPDIR/animejanai/inference/aji_encode" \
         "$APPDIR/animejanai/inference/trtexec"

cp "$HERE/../../logo.png" "$APPDIR/videojanai.png"
cat > "$APPDIR/videojanai.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=VideoJaNai
Comment=Upscale videos with ONNX models on NVIDIA TensorRT
Exec=AppRun
Icon=videojanai
Terminal=false
Categories=AudioVideo;Video;
EOF

mkdir -p "$OUT"
outfile="$OUT/VideoJaNai-x86_64-v${VER}.AppImage"
"$AT" "$APPDIR" "$outfile"
echo "$outfile"
rm -rf "$WORK"
