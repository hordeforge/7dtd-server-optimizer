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
  SEVENDTD_INSTALL_BACKUP_DIR
                       where a FAILED install's preserved Config/ lives
                       (default: <DS>/EfficientServer-install-backup). Outside
                       Mods/ so the game's mod scan never sees it
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
# Same failure mode uninstall.sh guards: an install dir that is empty, "/", or
# a run of whitespace resolves the target below to "/Mods/EfficientServer", and
# the rm -rf fires on a path at the filesystem root rather than on a dedicated
# install the operator named. Unlike uninstall.sh this script cannot require
# 7DaysToDieServer_Data/ to be present, because staging an install for another
# host is a supported use (the Harmony check below only warns), so the guard
# stops at the paths that are never a valid target.
SRV="${SRV#"${SRV%%[![:space:]]*}"}"
SRV="${SRV%"${SRV##*[![:space:]]}"}"
if [[ -z "$SRV" || "$SRV" == "/" ]]; then
  echo "ERROR: install dir is empty or the filesystem root: pass DS=\"/path/to/7 Days to Die Dedicated Server\"." >&2
  exit 1
fi
# Refuse to wipe a tree that is not a dedicated install. The rm -rf below is
# unconditional once SRV is non-empty, so a mistyped DS=/home/user (or any
# unrelated directory that happens to hold a Mods/EfficientServer) would have
# that folder deleted with nothing else in the output to explain it.
# uninstall.sh applies a stricter form of this check; install.sh cannot,
# because staging an install for another host is a supported use. So this
# accepts the two cases that are not a mistake - a real dedicated install
# (7DaysToDieServer_Data/ or the server binary is present) and a fresh or
# already-staged destination (the mod dir exists, or SRV is empty or absent,
# which is what staging to a new host looks like) - and refuses everything
# else: a populated directory that is neither.
if [[ ! -d "$SRV/7DaysToDieServer_Data" && ! -x "$SRV/7DaysToDieServer.x86_64" \
      && ! -d "$SRV/Mods/EfficientServer" ]]; then
  if [[ -d "$SRV" ]] && [[ -n "$(ls -A "$SRV" 2>/dev/null)" ]]; then
    echo "ERROR: '$SRV' is not a 7 Days to Die dedicated install and is not empty." >&2
    echo "  (no 7DaysToDieServer_Data/, no 7DaysToDieServer.x86_64, no existing" >&2
    echo "  Mods/EfficientServer/, and the directory has other content.) Installing" >&2
    echo "  here would delete that Mods/EfficientServer folder." >&2
    echo "  Pass DS=\"/path/to/7 Days to Die Dedicated Server\"." >&2
    exit 1
  fi
fi

# Back up on disk, never the stock /tmp: it is tmpfs on most Linux hosts, and
# after a failed install that copy is the only place the operator's tuning
# still exists - it must survive a reboot. mktemp honors TMPDIR.
export TMPDIR="$ROOT/.scratch/tmp"
mkdir -p "$TMPDIR"
# The installed mod tree is destroyed and rewritten below, and build.sh inside
# this call destroys dist/ and the intermediate obj/bin. A second install or
# uninstall of the same tree running at the same time would interleave with
# both. Held until this script exits; build.sh takes its own, separate lock
# underneath this one.
# shellcheck source=scripts/repo_lock.sh
source "$ROOT/scripts/repo_lock.sh"
repo_lock install
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
# JSON: the guard backup (efficientserver.json.swap-bak<pid>) and its quarantined
# .stale files are the only crash-recovery snapshot of a config a killed bench
# run left half-swapped, and nothing regenerates them. Same rule uninstall.sh
# applies, and the RPO claim in docs/PRODUCTION.md depends on it.
#
# The copy is taken to a STABLE path, not a mktemp name, because a failed
# install must be recoverable by simply running the script again. A temp name
# is only recoverable if the operator transcribes it out of the log, and the
# rm -rf below has already destroyed the installed config by the time the
# failure is reported: a retry found no Config/ to preserve, installed the
# shipped default, and the operator's tuning was left in a directory nothing
# ever reads again. The preserved dir exists only between a failure and the
# retry that consumes it, so it needs no retention window.
PRESERVED="${SEVENDTD_INSTALL_BACKUP_DIR:-$SRV/EfficientServer-install-backup}"
BACKUP=""
INSTALL_OK=0
# Success consumes the backup copy; a FAILED install must keep it, at a path
# the next run looks in.
finish() {
  if [[ -z "$BACKUP" ]]; then
    return
  fi
  if [[ "$INSTALL_OK" == 1 ]]; then
    rm -rf "$BACKUP"
    # A Config/ a still-failed earlier run preserved, now superseded by a
    # successful install of a newer one, must not sit in the install tree
    # waiting to be adopted by a later retry.
    [[ "$BACKUP" == "$PRESERVED" ]] || rm -rf "$PRESERVED"
  elif [[ "$BACKUP" != "$PRESERVED" ]]; then
    rm -rf "$PRESERVED"
    mv "$BACKUP" "$PRESERVED"
    echo "WARNING: install failed; your previous EfficientServer Config/ was preserved at $PRESERVED" >&2
    echo "  Rerun this script to install over it, or copy the files back by hand." >&2
  else
    echo "WARNING: install failed; your previous EfficientServer Config/ is still preserved at $PRESERVED" >&2
  fi
}
trap finish EXIT
# nullglob covers the globs below: an empty (or absent) Config/ must expand to
# no operands, never to the literal pattern.
shopt -s nullglob
# A preserved dir is present only when an earlier install failed mid-way, so
# its copy is the operator's config from BEFORE that failure, and the live tree
# is either that same config, the untouched shipped default, or gone (the
# failed run's rm -rf landed and the copy-out never got to finish). Adopt the
# preserved copy in all three cases, because in each of them it is the only
# record of the operator's tuning. It is superseded only by a live config that
# differs from BOTH the shipped default and the preserved copy, which is an
# edit made after that failure; there the live tree wins and the stale copy is
# dropped by finish() on success.
if [[ -d "$PRESERVED" ]]; then
  # The preserved copy exists to survive a failed install, and the default
  # location is inside the install tree, so a lost disk takes it with the
  # config. Say so while the operator is reading the output, and point at the
  # same tool uninstall.sh points at.
  case "$(readlink -f "$PRESERVED")/" in
    "$(readlink -f "$SRV")"/*)
      echo "WARNING: preserved-config dir is inside the install tree $SRV; a lost disk takes" >&2
      echo "WARNING: this copy with the config. scripts/backup_config.py moves it off-host." >&2
      ;;
  esac
  if [[ -z "$(ls -A "$PRESERVED" 2>/dev/null)" ]]; then
    # An empty one is debris, not a recovery point: there is nothing in it to
    # restore, and leaving it would have the next run re-warn about it.
    rmdir "$PRESERVED"
  else
    LIVE="$DEST/Config/efficientserver.json"
    SUPERSEDED=0
    if [[ -f "$LIVE" && -f "$PRESERVED/efficientserver.json" ]] \
       && ! cmp -s "$LIVE" "$PRESERVED/efficientserver.json" \
       && ! cmp -s "$LIVE" "$ROOT/config/efficientserver.json"; then
      SUPERSEDED=1
    fi
    if [[ "$SUPERSEDED" == 1 ]]; then
      # An operator edit made after that failure. The live tree is the newer
      # state, so the preserved copy is only removed on success, and the
      # operator is told which one the install is about to keep.
      BACKUP=""
      echo "NOTE: keeping the installed config; the Config/ an earlier failed" >&2
      echo "NOTE: install preserved at $PRESERVED is older and is removed on success." >&2
      echo "NOTE: move it aside first to install that copy instead." >&2
    else
      # Either there is no live config to speak of (the failed run's rm -rf
      # landed and the copy-out never finished), or the live one is that same
      # config, the shipped default, or already the preserved copy. In every
      # one of those the preserved copy is the only record of the operator's
      # tuning, so a rerun must not install the shipped default over it.
      BACKUP="$PRESERVED"
      echo "Adopting the Config/ preserved by an earlier failed install: $PRESERVED"
    fi
  fi
fi
if [[ -z "$BACKUP" && -d "$DEST/Config" ]]; then
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
