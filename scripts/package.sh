#!/usr/bin/env bash
# Build the mod and package dist/EfficientServer into a distributable zip.
#
# The zip contains the EfficientServer/ mod folder at its top level, so
# unzipping it inside <server>/Mods installs the mod (Mods/EfficientServer/).
#
# Version: taken from the newest git tag (vX.Y.Z -> X.Y.Z), or overridden
# with VERSION=x.y.z. Requires a local game install: build.sh compiles
# against the shipped Assembly-CSharp.dll, which this repo does not
# redistribute (see ../AGENTS.md).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

"$ROOT/scripts/build.sh"

VERSION="${VERSION:-$(git -C "$ROOT" describe --tags --always 2>/dev/null || true)}"
VERSION="${VERSION#v}"
if [[ -z "$VERSION" || "$VERSION" == *-* ]]; then
  # No tag yet (or dirty/untagged describe): fall back to a short commit id.
  VERSION="$(git -C "$ROOT" rev-parse --short HEAD)"
fi

OUT="$ROOT/dist/EfficientServer-$VERSION.zip"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
cp -a "$ROOT/dist/EfficientServer" "$STAGE/"
( cd "$STAGE" && zip -qr "$OUT" EfficientServer )
echo "Packaged -> $OUT"
