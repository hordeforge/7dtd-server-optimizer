#!/usr/bin/env bash
# Independent rebuild-and-compare check for the reproducibility claims in
# README.md ("two builds of the same tree zip byte-identically").
#
# Three legs, each catches a different nondeterminism class:
#   1. baseline package                          -> reference hash
#   2. second package in the same tree           -> leftover-state / cache drift
#   3. full recompile from a copied tree at another path -> build-path leakage
#                                                           into IL
#
# The epoch is held constant across legs (it is an input by design); everything
# else that must not matter is varied where possible. Needs a game install,
# like make package; not wired into `make test`/CI for that reason.
#
# Scope: this proves reproducibility for the dotnet backend, the one that
# ships. The mcs fallback stamps a random MVID that mcs 6.12 offers no flag to
# suppress, so SEVENDTD_BUILD_BACKEND=mcs cannot pass leg 2; run it without
# that variable set.
set -euo pipefail
export LC_ALL=C TZ=UTC
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

usage() {
  cat <<'EOF'
usage: scripts/verify_reproducible.sh [-h | --help]

Packages twice in the same tree, then recompiles from a copy of the tree at
another path and compares hashes (proves the reproducibility claim in README.md).
Takes no arguments; needs a game install, like make package.
  -h, --help  show this help and exit

Environment:
  SOURCE_DATE_EPOCH  epoch held constant across the three legs (default: last
                     commit time)
  SEVENDTD_DS_DIR    game install used for the compile (see scripts/build.sh)
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
    echo "ERROR: verify_reproducible.sh takes no arguments, got: $*" >&2
    usage >&2
    exit 2
    ;;
esac

# Leg 3 copies the whole tree including .git; the stock /tmp is tmpfs on most
# Linux hosts, so that copy plus a full recompile would run out of RAM instead
# of disk. mktemp honors TMPDIR, and .scratch/ is gitignored (and excluded from
# the tar below, or the copy would recurse into its own destination).
export TMPDIR="$ROOT/.scratch/tmp"
# shellcheck source=scripts/stage_tmp.sh
source "$ROOT/scripts/stage_tmp.sh"
stage_tmpdir

for tool in git zip sha256sum find tar; do
  command -v "$tool" >/dev/null 2>&1 || { echo "ERROR: required tool '$tool' not found" >&2; exit 1; }
done

EPOCH="${SOURCE_DATE_EPOCH:-$(git -C "$ROOT" log -1 --pretty=%ct)}"
[[ "$EPOCH" =~ ^[0-9]+$ ]] || { echo "ERROR: bad epoch '$EPOCH'" >&2; exit 1; }
export SOURCE_DATE_EPOCH="$EPOCH"

# Wipe previous zips, package fresh, print the single resulting zip path.
package_zip() {
  rm -f "$ROOT"/dist/EfficientServer-*.zip
  "$ROOT/scripts/package.sh" >/dev/null || return 1
  set -- "$ROOT"/dist/EfficientServer-*.zip
  [[ $# -eq 1 && -f "$1" ]] || { echo "ERROR: expected exactly one zip in dist/" >&2; return 1; }
  printf '%s\n' "$1"
}

# The count check above needs dist/ to hold exactly the zip this run just built,
# which is why package_zip clears the directory. A pre-existing zip is the
# operator's artifact (a release they are about to publish), not this check's
# scratch: rm -f over it destroyed a good build on the first run, and a killed
# run destroyed it with nothing put back. Park them, and put them back on every
# exit path instead.
PREBUILT="$(stage_new es-repro-prebuilt)"
shopt -s nullglob
for _zip in "$ROOT"/dist/EfficientServer-*.zip; do
  mv -f "$_zip" "$PREBUILT/"
done
shopt -u nullglob
unset _zip
restore_prebuilt() {
  local zip name
  shopt -s nullglob
  for zip in "$PREBUILT"/*.zip; do
    name="$(basename "$zip")"
    if [[ -e "$ROOT/dist/$name" ]]; then
      # This run built the same name (an unchanged tree, VERSION pinned): the
      # fresh archive wins, and the parked one stays where it is rather than
      # being deleted, so the operator can still get to it.
      echo "NOTE: $PREBUILT/$name kept aside; this run built dist/$name too" >&2
    else
      mv -f "$zip" "$ROOT/dist/"
    fi
  done
  shopt -u nullglob
  # Only once nothing is left inside: a kept-aside zip must survive the run.
  rmdir "$PREBUILT" 2>/dev/null || true
}
cleanup() {
  [[ -n "${STAGE:-}" ]] && rm -rf "$STAGE"
  restore_prebuilt
}
trap cleanup EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

echo "== leg 1: baseline package"
Z1="$(package_zip)" || fail "baseline package failed"
H1="$(sha256sum "$Z1")"; echo "  $(basename "$Z1") ${H1%% *}"

echo "== leg 2: repackage same tree (leftover state)"
Z2="$(package_zip)" || fail "second package failed"
H2="$(sha256sum "$Z2")"
[[ "${H1%% *}" == "${H2%% *}" ]] || fail "same-tree rebuild differs:
  $H1
  $H2"
echo "  identical"

echo "== leg 3: full recompile from a copied tree at another path"
STAGE="$(stage_new es-repro)"
# Copy including .git so version resolution sees the same history; exclude
# build outputs, local launch state and .scratch (which holds $STAGE itself)
# so nothing but sources carries over.
tar -C "$ROOT" --exclude=./dist --exclude=./Source/EfficientServer/obj \
  --exclude=./Source/EfficientServer/bin --exclude=./server --exclude=./.scratch \
  -cf - . | tar -C "$STAGE" -xf -
DLL_REF="$ROOT/dist/EfficientServer/EfficientServer.dll"
rm -f "$ROOT"/dist/EfficientServer-*.zip
"$STAGE/scripts/build.sh" >/dev/null || fail "out-of-tree build failed"
HO="$(sha256sum "$STAGE/dist/EfficientServer/EfficientServer.dll")"
HD="$(sha256sum "$DLL_REF")"
[[ "${HO%% *}" == "${HD%% *}" ]] || fail "out-of-tree DLL differs:
  $HO
  $HD"
echo "  DLL identical across paths"

# Repackage once more so dist holds a fresh zip alongside this check's verdict.
"$ROOT/scripts/package.sh" >/dev/null
ZIPFINAL="$(echo "$ROOT"/dist/EfficientServer-*.zip)"
echo "PASS: reproducible ($(basename "$ZIPFINAL"), sha256 ${H1%% *}, epoch $EPOCH)"
