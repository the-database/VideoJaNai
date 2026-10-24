#!/bin/sh
# Packages an assembled linux-x64 install tree into the portable tarball: drops the install/uninstall
# scripts and the icon at the tree root, then tars the tree (zstd) with its versioned top-level
# directory (VideoJaNai-v<ver>/, which the updater's full update looks for).
#
# Usage: package-tarball.sh <tree-dir> <version> [out-dir]
set -eu

TREE="$1"
VER="$2"
OUT="${3:-.}"
HERE="$(cd "$(dirname "$0")" && pwd)"

[ -d "$TREE" ] || { echo "tree dir not found: $TREE" >&2; exit 1; }

cp -f "$HERE/install.sh" "$HERE/uninstall.sh" "$TREE/"
cp -f "$HERE/../../logo.png" "$TREE/videojanai.png"
chmod +x "$TREE/install.sh" "$TREE/uninstall.sh" "$TREE/VideoJaNai" "$TREE/VideoJaNaiUpdater" "$TREE/7zz" \
         "$TREE/animejanai/inference/aji_encode" 2>/dev/null || true

mkdir -p "$OUT"
name="VideoJaNai-v${VER}-linux-x64.tar.zst"
tar --zstd -C "$(dirname "$TREE")" -cf "$OUT/$name" "$(basename "$TREE")"
echo "$OUT/$name"
