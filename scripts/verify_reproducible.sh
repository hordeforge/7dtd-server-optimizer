#!/usr/bin/env bash
# Independent rebuild-and-compare check for the reproducibility claims in
# README.md ("two builds of the same tree zip byte-identically").
#
# Three legs, each catches a different nondeterminism class:
#   1. plain second package in the same tree     -> leftover-state / cache drift
#   2. full recompile (obj/bin wiped)            -> incremental-build drift
#   3. build from a copied tree at another path  -> build-path leakage into IL
#
# The epoch is held constant across legs (it is an input by design); everything
# else that must not matter is varied where possible. Needs a game install,
# like make package; not wired into `make test`/CI for that reason.
set -euo pipefail
export LC_ALL=C TZ=UTC
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

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
STAGE="$(mktemp -d "${TMPDIR:-/tmp}/es-repro.XXXXXX")"
trap 'rm -rf "$STAGE"' EXIT
# Copy including .git so version resolution sees the same history; exclude
# build outputs and local launch state so nothing but sources carries over.
tar -C "$ROOT" --exclude=./dist --exclude=./Source/EfficientServer/obj \
  --exclude=./Source/EfficientServer/bin --exclude=./server -cf - . | tar -C "$STAGE" -xf -
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
