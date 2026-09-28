#!/usr/bin/env bash
# Remove the installed EfficientServer mod, keeping the operator's config first.
#
# The installed Mods/EfficientServer/Config/ directory is the only copy of the
# live tuning (it is edited on the server host, not in this repo), so a bare
# rm -rf over the mod folder is unrecoverable once the host is gone. install.sh
# already preserves it across a reinstall; this does the same on removal and
# prints the command that puts it back.
#
# Environment:
#   SEVENDTD_DS_DIR / DS        Dedicated install root (default: ~/.local/share/
#                               Steam/steamapps/common/7 Days to Die Dedicated Server)
#   SEVENDTD_UNINSTALL_BACKUP_DIR
#                               Where the preserved config lands (default:
#                               <DS>/EfficientServer-uninstall-backup). Outside
#                               Mods/ so the game's mod scan never sees it.
#   SEVENDTD_UNINSTALL_PURGE    Set to 1 for a deliberate full wipe: skip the
#                               copy and delete the config too.
set -euo pipefail
export LC_ALL=C TZ=UTC

SRV="${SEVENDTD_DS_DIR:-${DS:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}}"
# Exported-but-empty must fail, not fall through to the stock default in the
# expression above: that would delete the mod from a DIFFERENT install than the
# operator named. Same guard in install.sh and run_server.sh.
for _var in SEVENDTD_DS_DIR DS; do
  if [[ -n "${!_var+x}" && -z "${!_var}" ]]; then
    echo "ERROR: $_var is set but empty; pass a real install dir or unset it." >&2
    exit 1
  fi
done
unset _var
# Same failure mode install.sh guards: an empty variable would expand to
# "/Mods/EfficientServer" and rm -rf a path at the filesystem root.
if [[ -z "$SRV" || "$SRV" == "/" ]]; then
  echo "ERROR: uninstall got an empty or root install dir; pass DS=\"/path/to/7 Days to Die Dedicated Server\"." >&2
  exit 1
fi
# Refuse to delete from a path that is not a dedicated install: a typo in DS=
# would otherwise remove a same-named folder out of an unrelated tree.
if [[ ! -d "$SRV/7DaysToDieServer_Data" && ! -x "$SRV/7DaysToDieServer.x86_64" ]]; then
  echo "ERROR: '$SRV' does not look like a 7 Days to Die dedicated install" >&2
  echo "  (no 7DaysToDieServer_Data/ and no 7DaysToDieServer.x86_64)." >&2
  echo "  Pass DS=\"/path/to/7 Days to Die Dedicated Server\"." >&2
  exit 1
fi

DEST="$SRV/Mods/EfficientServer"
if [[ ! -d "$DEST" ]]; then
  echo "Nothing installed at $DEST"
  exit 0
fi

BACKUP_DIR="${SEVENDTD_UNINSTALL_BACKUP_DIR:-$SRV/EfficientServer-uninstall-backup}"
# Every Config file is kept, not just the JSON: the bench harnesses' guard
# backup (efficientserver.json.swap-bak) is the only crash-recovery snapshot of
# a config a killed run left half-swapped, and the mod folder is about to go.
PRESERVED=()
if [[ "${SEVENDTD_UNINSTALL_PURGE:-0}" == "1" ]]; then
  echo "SEVENDTD_UNINSTALL_PURGE=1: deleting the installed config too, no copy kept."
else
  # UTC stamp: a repeated local hour (DST fall-back) would otherwise overwrite
  # the previous backup with the one taken minutes earlier. Second resolution
  # alone still collides when two uninstalls land in the same second, and then
  # the second cp -a overwrites the first backup - the exact evidence loss this
  # copy exists to prevent. Suffix until the target dir is free.
  STAMP="$(date -u +%Y%m%d_%H%M%S)"
  TARGET="$BACKUP_DIR/$STAMP"
  n=1
  while [[ -e "$TARGET" ]]; do
    TARGET="$BACKUP_DIR/${STAMP}_$n"
    n=$((n + 1))
  done
  mkdir -p "$TARGET"
  shopt -s nullglob
  for f in "$DEST"/Config/*; do
    cp -a "$f" "$TARGET/"
    PRESERVED+=("$f")
  done
  shopt -u nullglob
  if [[ ${#PRESERVED[@]} -eq 0 ]]; then
    rmdir "$TARGET"
    echo "No Config files to preserve under $DEST/Config."
  else
    echo "Preserved ${#PRESERVED[@]} config file(s) -> $TARGET"
  fi
fi

rm -rf "$DEST"
echo "Removed -> $DEST"

if [[ ${#PRESERVED[@]} -gt 0 ]]; then
  cat <<EOF

Restore after reinstalling:
  make install DS="$SRV"
EOF
  # Only name the copy line for files that were actually preserved: the Config
  # directory can hold the guard's .swap-bak with no efficientserver.json beside
  # it, and a hint that names a file which was never copied sends the operator to
  # a "No such file" instead of their tuning.
  for f in "${PRESERVED[@]}"; do
    echo "  cp -a \"$TARGET/$(basename "$f")\" \"$DEST/Config/$(basename "$f")\""
  done
  cat <<EOF
  es reload            # or restart the server
EOF
fi
