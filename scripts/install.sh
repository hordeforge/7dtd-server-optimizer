#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
"$ROOT/scripts/build.sh"

SRV="${SEVENDTD_DS_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}"
DEST="$SRV/Mods/EfficientServer"
# Preserve a user-edited config across upgrade/reinstall: back up the installed
# one before wiping, and keep it if it differs from the newly shipped default.
BACKUP=""
INSTALL_OK=0
# Success consumes the backup copy; a FAILED install must keep it. The rm -rf
# below has already destroyed the installed config by then, so the temp copy
# is the only place the operator's tuning still exists.
finish() {
  if [[ -n "$BACKUP" ]]; then
    if [[ "$INSTALL_OK" == 1 ]]; then
      rm -f "$BACKUP"
    else
      echo "WARNING: install failed; your previous EfficientServer config was preserved at $BACKUP" >&2
    fi
  fi
}
trap finish EXIT
if [[ -f "$DEST/Config/efficientserver.json" ]]; then
  BACKUP="$(mktemp)"
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
INSTALL_OK=1
echo "Installed -> $DEST"
ls -la "$DEST" "$DEST/Config"
