#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
"$ROOT/scripts/build.sh"

SRV="${SEVENDTD_DS_DIR:-/home/maci/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}"
DEST="$SRV/Mods/EfficientServer"
rm -rf "$DEST"
mkdir -p "$DEST"
cp -a "$ROOT/dist/EfficientServer/." "$DEST/"
echo "Installed -> $DEST"
ls -la "$DEST" "$DEST/Config"
