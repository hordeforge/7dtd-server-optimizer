#!/usr/bin/env bash
# Build the mod and package dist/EfficientServer into a distributable zip.
#
# The zip contains the EfficientServer/ mod folder at its top level, so
# unzipping it inside <server>/Mods installs the mod (Mods/EfficientServer/).
#
# Reproducible by construction: entry order is sorted, every mtime is set to
# SOURCE_DATE_EPOCH (falling back to the last commit time), permissions are
# normalized, and owner/group data is stripped (-X). Two builds from the same
# tree produce byte-identical zips.
#
# Version: taken from the newest git tag (vX.Y.Z -> X.Y.Z), or overridden
# with VERSION=x.y.z. Requires a local game install: build.sh compiles
# against the shipped Assembly-CSharp.dll, which this repo does not
# redistribute (see ../AGENTS.md).
set -euo pipefail
export LC_ALL=C TZ=UTC
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

"$ROOT/scripts/build.sh"

VERSION="${VERSION:-$(git -C "$ROOT" describe --tags --always 2>/dev/null || true)}"
VERSION="${VERSION#v}"
if [[ -z "$VERSION" || "$VERSION" == *-* ]]; then
  # No tag yet (or dirty/untagged describe): fall back to a short commit id.
  VERSION="$(git -C "$ROOT" rev-parse --short HEAD)"
fi

EPOCH="${SOURCE_DATE_EPOCH:-$(git -C "$ROOT" log -1 --pretty=%ct)}"
[[ "$EPOCH" =~ ^[0-9]+$ ]] || { echo "ERROR: bad epoch '$EPOCH'" >&2; exit 1; }

OUT="$ROOT/dist/EfficientServer-$VERSION.zip"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
cp -a "$ROOT/dist/EfficientServer" "$STAGE/"

# Normalize all filesystem-dependent metadata before archiving.
find "$STAGE" -type d -exec chmod 755 {} +
find "$STAGE/EfficientServer" -type f -exec chmod 644 {} +
find "$STAGE" -print0 | xargs -0 touch -d "@$EPOCH"

(
  cd "$STAGE"
  # File entries only (dirs are implicit on extract), sorted, no extra fields.
  find EfficientServer -type f -print | LC_ALL=C sort | zip -q -X "$OUT" -@
)
echo "Packaged -> $OUT (entry mtime epoch $EPOCH)"
