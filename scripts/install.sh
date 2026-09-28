#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
usage() {
  cat <<'EOF'
usage: scripts/install.sh [-h | --help]

Builds the mod and copies dist/EfficientServer into <DS>/Mods/EfficientServer.
A user-edited installed config is preserved across the reinstall. Takes no
arguments; everything is read from the environment.
  -h, --help  show this help and exit

Environment:
  SEVENDTD_DS_DIR / DS  dedicated install root; the mod is installed into
                       $SEVENDTD_DS_DIR/Mods/EfficientServer
EOF
}

case "${1:-}" in
  -h | --help)
    usage
    exit 0
    ;;
  "")
    ;;
  *)
    echo "ERROR: install.sh takes no arguments, got: $*" >&2
    usage >&2
    exit 2
    ;;
esac

# Resolve and validate the install dir BEFORE the build: an empty override
# must not spend a compile, and must not reach the rm -rf below.
SRV="${SEVENDTD_DS_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}"
# Exported-but-empty must fail, not fall through to the stock default in the
# expression above: the rm -rf below would then wipe a DIFFERENT install's mod
# folder than the operator named. Same guard in uninstall.sh and run_server.sh.
if [[ -n "${SEVENDTD_DS_DIR+x}" && -z "$SEVENDTD_DS_DIR" ]]; then
  echo "ERROR: SEVENDTD_DS_DIR is set but empty; pass a real install dir or unset it." >&2
  exit 1
fi

# Back up on disk, never the stock /tmp: it is tmpfs on most Linux hosts, and
# after a failed install that copy is the only place the operator's tuning
# still exists - it must survive a reboot. mktemp honors TMPDIR.
export TMPDIR="$ROOT/.scratch/tmp"
mkdir -p "$TMPDIR"
"$ROOT/scripts/build.sh"

DEST="$SRV/Mods/EfficientServer"
# Runtime-dependency preflight: the mod loads only through the stock
# 0_TFP_Harmony loader (README "Requirements"), which must live in the TARGET
# server's Mods/. build.sh may have compiled against a client install's
# Harmony while $SRV is the install destination, so check $SRV itself.
# Warning, not failure: staging an install for another host stays possible,
# and the game skips the mod with only generic log errors when this is missing.
if [[ ! -f "$SRV/Mods/0_TFP_Harmony/0Harmony.dll" ]]; then
  echo "WARNING: $SRV has no Mods/0_TFP_Harmony/0Harmony.dll." >&2
  echo "WARNING: EfficientServer requires the stock 0_TFP_Harmony mod at load time; without it the server will not load this mod." >&2
fi
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
