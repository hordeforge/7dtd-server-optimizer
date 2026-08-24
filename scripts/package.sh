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

# --dirty is mandatory here: without it a modified working tree describes as
# the clean tag and ships a different zip under the release name and SBOM
# serial of the untouched release.
VERSION="${VERSION:-$(git -C "$ROOT" describe --tags --always --dirty 2>/dev/null || true)}"
VERSION="${VERSION#v}"
if [[ -z "$VERSION" || "$VERSION" == *-* && "$VERSION" != *-dirty ]]; then
  # No tag yet (or an annotated-tag distance like v1.17.0-3-gabc1234): fall
  # back to a short commit id.
  VERSION="$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown)"
fi
if [[ ! "$VERSION" =~ ^[0-9A-Za-z._-]+$ ]]; then
  echo "ERROR: unusable version '$VERSION' (set VERSION=x.y.z explicitly)" >&2
  exit 1
fi

EPOCH="${SOURCE_DATE_EPOCH:-$(git -C "$ROOT" log -1 --pretty=%ct)}"
[[ "$EPOCH" =~ ^[0-9]+$ ]] || { echo "ERROR: bad epoch '$EPOCH'" >&2; exit 1; }

OUT="$ROOT/dist/EfficientServer-$VERSION.zip"
STAGE="$(mktemp -d)"
ZIP_TMP=""
# Same contract as install.sh: a failed run must not leave the only copy of an
# artifact destroyed (or a partial zip sitting under the release name).
finish() {
  rm -rf "$STAGE"
  if [[ -n "$ZIP_TMP" ]]; then rm -f "$ZIP_TMP"; fi
}
trap finish EXIT
cp -a "$ROOT/dist/EfficientServer" "$STAGE/"

# Supply-chain inventory inside the zip: a deterministic CycloneDX SBOM built
# from the committed packages.lock.json graph. All inputs are in-tree values,
# so it stays byte-identical across rebuilds (verify_reproducible.sh proves
# this). Written before permission/mtime normalization below.
python3 "$ROOT/scripts/gen_sbom.py" \
  --version "$VERSION" \
  --commit "$(git -C "$ROOT" rev-parse HEAD 2>/dev/null || echo unknown)" \
  --epoch "$EPOCH" \
  --lock "$ROOT/Source/EfficientServer.Tests/packages.lock.json" \
  --out "$STAGE/EfficientServer/bom.json"

# Normalize all filesystem-dependent metadata before archiving.
find "$STAGE" -type d -exec chmod 755 {} +
find "$STAGE/EfficientServer" -type f -exec chmod 644 {} +
find "$STAGE" -print0 | xargs -0 touch -d "@$EPOCH"

# Zip to a sibling temp file and rename, so a failed or killed zip run can
# neither publish a partial archive under the release name nor destroy the
# previous good artifact before its replacement exists. zip does not store the
# output path in the archive, so the bytes are identical to a direct build.
ZIP_TMP="$OUT.tmp.$$"
(
  cd "$STAGE"
  # File entries only (dirs are implicit on extract), sorted, no extra fields.
  find EfficientServer -type f -print | LC_ALL=C sort | zip -q -X "$ZIP_TMP" -@
)
mv -f "$ZIP_TMP" "$OUT"
ZIP_TMP=""
echo "Packaged -> $OUT (entry mtime epoch $EPOCH)"

# Build-environment record next to (not inside) the zip, so a faithful rebuild
# can be attempted and verified with scripts/verify_reproducible.sh. Host-
# specific by design; kept out of the zip to preserve byte-identical artifacts.
if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q .; then
  COMPILER="dotnet SDK $(dotnet --version 2>/dev/null || echo unknown)"
else
  COMPILER="mcs $(mcs --version 2>/dev/null | head -n1 || echo unknown)"
fi
{
  echo "artifact: $(basename "$OUT")"
  echo "zip_sha256: $(sha256sum "$OUT" | cut -d' ' -f1)"
  echo "source_date_epoch: $EPOCH"
  echo "git_commit: $(git -C "$ROOT" rev-parse HEAD 2>/dev/null || echo unknown)"
  echo "git_describe: $(git -C "$ROOT" describe --tags --always --dirty 2>/dev/null || echo unknown)"
  echo "compiler: $COMPILER"
} > "${OUT%.zip}.buildinfo.txt"
