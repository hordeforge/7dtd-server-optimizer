#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C TZ=UTC
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
usage() {
  cat <<'EOF'
usage: scripts/install.sh [-h | --help]

Builds the mod and copies dist/EfficientServer into <DS>/Mods/EfficientServer.
Every file under the installed Config/ is preserved across the reinstall: a
user-edited efficientserver.json (when it differs from the shipped default),
plus the bench harness' guard backup files. Takes no arguments; everything is
read from the environment.
  -h, --help  show this help and exit

Environment:
  SEVENDTD_DS_DIR / DS  dedicated install root (SEVENDTD_DS_DIR wins, then DS,
                       then the stock Steam path); the mod is installed into
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
# DS is an accepted spelling, not just the SEVENDTD_DS_DIR the Makefile exports:
# uninstall.sh printed `make install DS="$SRV"` in its restore hint, and
# --help above documents both, so `DS=/path scripts/install.sh` must resolve to
# that path. It used to fall through to the stock default and wipe a DIFFERENT
# install's mod folder than the operator named. SEVENDTD_DS_DIR wins so an
# explicit env override beats the Makefile's DS=. Same resolution order in
# uninstall.sh and run_server.sh.
SRV="${SEVENDTD_DS_DIR:-${DS:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}}"
# An exported-but-empty value must fail, not fall through to the stock default
# in the expression above: the rm -rf below would then wipe a DIFFERENT
# install's mod folder than the operator named. Both spellings are checked,
# because either one being empty is what a mistyped `DS=` leaves behind. Same
# guard in uninstall.sh and run_server.sh.
for _var in SEVENDTD_DS_DIR DS; do
  if [[ -n "${!_var+x}" && -z "${!_var}" ]]; then
    echo "ERROR: $_var is set but empty; pass a real install dir or unset it." >&2
    exit 1
  fi
done
unset _var

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
# Preserve the whole installed Config/ across upgrade/reinstall, not just the
# JSON: the guard backup (efficientserver.json.swap-bak) and its quarantined
# .stale files are the only crash-recovery snapshot of a config a killed bench
# run left half-swapped, and nothing regenerates them. Same rule uninstall.sh
# applies, and the RPO claim in docs/PRODUCTION.md depends on it.
BACKUP=""
INSTALL_OK=0
# Success consumes the backup copy; a FAILED install must keep it. The rm -rf
# below has already destroyed the installed config by then, so the temp copy
# is the only place the operator's tuning still exists.
finish() {
  if [[ -n "$BACKUP" ]]; then
    if [[ "$INSTALL_OK" == 1 ]]; then
      rm -rf "$BACKUP"
    else
      echo "WARNING: install failed; your previous EfficientServer Config/ was preserved at $BACKUP" >&2
    fi
  fi
}
trap finish EXIT
# nullglob covers both globs below: an empty (or absent) Config/ must expand to
# no operands, never to the literal pattern.
shopt -s nullglob
if [[ -d "$DEST/Config" ]]; then
  BACKUP="$(mktemp -d)"
  for f in "$DEST"/Config/*; do
    cp -a "$f" "$BACKUP/"
  done
fi
rm -rf "$DEST"
mkdir -p "$DEST"
cp -a "$ROOT/dist/EfficientServer/." "$DEST/"
# Match the installed tree to the shipped zip: package.sh normalizes modes
# before archiving, so an install must not leave build-host umask modes behind.
find "$DEST" -type d -exec chmod 755 {} +
find "$DEST" -type f -exec chmod 644 {} +
CONFIG_RESTORED=0
if [[ -n "$BACKUP" ]]; then
  for f in "$BACKUP"/*; do
    name="$(basename "$f")"
    if [[ "$name" == "efficientserver.json" ]]; then
      # Byte-identical to the newly shipped default: nothing the operator did.
      if cmp -s "$f" "$DEST/Config/$name"; then
        continue
      fi
      CONFIG_RESTORED=1
    fi
    cp -a "$f" "$DEST/Config/$name"
    [[ "$name" == "efficientserver.json" ]] ||
      echo "Preserved $name from the previous install."
  done
fi
if [[ "$CONFIG_RESTORED" == 1 ]]; then
  echo "Preserved existing user config (differs from shipped default)."
else
  echo "Using shipped default config."
fi
shopt -u nullglob
INSTALL_OK=1
echo "Installed -> $DEST"
ls -la "$DEST" "$DEST/Config"
