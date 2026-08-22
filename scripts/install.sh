#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
"$ROOT/scripts/build.sh"

SRV="${SEVENDTD_DS_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}"
DEST="$SRV/Mods/EfficientServer"
# Preserve a user-edited config across upgrade/reinstall: back up the installed
# one before wiping, and keep it if it differs from the newly shipped default.
BACKUP=""
if [[ -f "$DEST/Config/efficientserver.json" ]]; then
  BACKUP="$(mktemp)"
  # Clean up on any exit path: an interrupted reinstall must not leak the copy.
  trap 'rm -f "$BACKUP"' EXIT
  cp "$DEST/Config/efficientserver.json" "$BACKUP"
fi
rm -rf "$DEST"
mkdir -p "$DEST"
cp -a "$ROOT/dist/EfficientServer/." "$DEST/"
if [[ -n "$BACKUP" ]] && ! cmp -s "$BACKUP" "$DEST/Config/efficientserver.json"; then
  cp "$BACKUP" "$DEST/Config/efficientserver.json"
  echo "Preserved existing user config (differs from shipped default)."
else
  echo "Using shipped default config."
fi
echo "Installed -> $DEST"
ls -la "$DEST" "$DEST/Config"
