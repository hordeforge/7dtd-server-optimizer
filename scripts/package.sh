#!/usr/bin/env bash
# Build the mod and package dist/EfficientServer into a distributable zip.
#
# The zip contains the EfficientServer/ mod folder at its top level, so
# unzipping it inside <server>/Mods installs the mod (Mods/EfficientServer/).
#
# Reproducible on the dotnet backend: entry order is sorted, every mtime is set
# to SOURCE_DATE_EPOCH (falling back to the last commit time), permissions are
# normalized, and owner/group data is stripped (-X). Two dotnet-backend builds
# from the same tree produce byte-identical zips. The mcs fallback backend
# stamps a random MVID and ignores -deterministic, so it cannot reproduce (see
# build.sh); it exists for hosts without an SDK, not for releases.
#
# Version: taken from the newest git tag (vX.Y.Z -> X.Y.Z), or overridden
# with VERSION=x.y.z, with -dirty appended whenever the tree is modified. A
# release-form name must match the Version in Source/EfficientServer/ModInfo.xml.
# Requires a local game install: build.sh compiles
# against the shipped Assembly-CSharp.dll, which this repo does not
# redistribute (see ../AGENTS.md).
set -euo pipefail
export LC_ALL=C TZ=UTC
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

usage() {
  cat <<'EOF'
usage: scripts/package.sh [-h | --help]

Builds the mod and zips dist/EfficientServer into
dist/EfficientServer-<version>.zip, reproducibly. Takes no arguments;
everything is read from the environment.
  -h, --help  show this help and exit

Environment:
  VERSION             version suffix for the zip name (default: newest git tag,
                      with -dirty on a modified tree, else the short commit id).
                      A release-form value (x.y.z) must equal the Version in
                      Source/EfficientServer/ModInfo.xml; the run fails otherwise
  SOURCE_DATE_EPOCH   zip entry mtime epoch (default: last commit time). Held
                      constant across builds, so two builds of one tree are
                      byte-identical
  SEVENDTD_DS_DIR     game install used for the compile (see scripts/build.sh)
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
    echo "ERROR: package.sh takes no arguments, got: $*" >&2
    usage >&2
    exit 2
    ;;
esac

# Stage on disk, never the stock /tmp: it is tmpfs on most Linux hosts, so a
# full mod staging tree would be held in RAM and lost on reboot. mktemp honors
# TMPDIR, and .scratch/ is gitignored.
export TMPDIR="$ROOT/.scratch/tmp"
# shellcheck source=scripts/stage_tmp.sh
source "$ROOT/scripts/stage_tmp.sh"
stage_tmpdir

"$ROOT/scripts/build.sh"

# The dirty mark is a property of the tree, not of where the version came from:
# --dirty is mandatory on the describe below, and the same mark is applied to an
# explicit VERSION= override, which describe would have stamped. Without either,
# a modified working tree describes as the clean tag (or is handed a release
# name verbatim) and ships a different zip under the release name of the
# untouched release.
if [[ -n "$(git -C "$ROOT" status --porcelain 2>/dev/null)" ]]; then
  DIRTY_SUFFIX="-dirty"
else
  DIRTY_SUFFIX=""
fi
VERSION="${VERSION:-$(git -C "$ROOT" describe --tags --always --dirty 2>/dev/null || true)}"
VERSION="${VERSION#v}"
# Strip the mark before the distance test, so a tag-distance form on a dirty
# tree (v1.17.0-3-gabc1234-dirty) is still recognized as a distance and not
# shipped under a version string. The mark is reapplied below.
VERSION="${VERSION%-dirty}"
if [[ -z "$VERSION" || "$VERSION" == *-* ]]; then
  # No tag yet (or an annotated-tag distance like v1.17.0-3-gabc1234): neither
  # names a release, so fall back to a short commit id.
  VERSION="$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown)"
fi
VERSION="$VERSION$DIRTY_SUFFIX"
if [[ ! "$VERSION" =~ ^[0-9A-Za-z._-]+$ ]]; then
  echo "ERROR: unusable version '$VERSION' (set VERSION=x.y.z explicitly)" >&2
  exit 1
fi

# The zip is named for the tag, but the game reads the mod version from
# ModInfo.xml, so a release tagged past the manifest ships a v1.20.0 zip whose
# mod reports 1.19.0 to the server console and to every mod listing. Nothing
# else cross-checks the two: check_version.py ties ModInfo to AssemblyInfo, the
# CHANGELOG and the docs, and has no tag to compare against. Only a release-form
# version is checked (the mark is stripped above, so the check also fires while
# the tree is still dirty); a commit-id name already says what it is.
MODINFO_VERSION="$(sed -n 's/.*<Version value="\([^"]*\)".*/\1/p' \
  "$ROOT/Source/EfficientServer/ModInfo.xml" | head -n 1)"
if [[ -z "$MODINFO_VERSION" ]]; then
  echo "ERROR: no <Version> in Source/EfficientServer/ModInfo.xml" >&2
  exit 1
fi
if [[ "${VERSION%-dirty}" =~ ^[0-9]+(\.[0-9]+)*$ \
   && "${VERSION%-dirty}" != "$MODINFO_VERSION" ]]; then
  echo "ERROR: release version ${VERSION%-dirty} does not match the shipped ModInfo.xml" >&2
  echo "  (ModInfo.xml says $MODINFO_VERSION)." >&2
  echo "  Bump Source/EfficientServer/ModInfo.xml to ${VERSION%-dirty}, or set" >&2
  echo "  VERSION=$MODINFO_VERSION to rebuild the release the manifest describes." >&2
  exit 1
fi

EPOCH="${SOURCE_DATE_EPOCH:-$(git -C "$ROOT" log -1 --pretty=%ct)}"
[[ "$EPOCH" =~ ^[0-9]+$ ]] || { echo "ERROR: bad epoch '$EPOCH'" >&2; exit 1; }

OUT="$ROOT/dist/EfficientServer-$VERSION.zip"
STAGE="$(stage_new es-package)"
ZIP_TMP=""
# Same contract as install.sh: a failed run must not leave the only copy of an
# artifact destroyed (or a partial zip sitting under the release name).
finish() {
  rm -rf "$STAGE"
  if [[ -n "$ZIP_TMP" ]]; then rm -f "$ZIP_TMP"; fi
}
trap finish EXIT
cp -a "$ROOT/dist/EfficientServer" "$STAGE/"

# Normalize all filesystem-dependent metadata before archiving.
find "$STAGE" -type d -exec chmod 755 {} +
find "$STAGE/EfficientServer" -type f -exec chmod 644 {} +
# --no-run-if-empty: a bare `find | xargs touch` with no input leaves touch
# with no operands, which exits nonzero and would abort an otherwise fine run.
find "$STAGE" -print0 | xargs -0 --no-run-if-empty touch -d "@$EPOCH"

# Zip to a sibling temp file and rename, so a failed or killed zip run can
# neither publish a partial archive under the release name nor destroy the
# previous good artifact before its replacement exists. zip does not store the
# output path in the archive, so the bytes are identical to a direct build.
ZIP_TMP="$OUT.tmp.$$"
# Capture the entry list once and feed it to both the count check and zip, so
# the two cannot disagree about what the archive should contain.
ENTRIES="$STAGE/.entries"
(
  cd "$STAGE"
  # File entries only (dirs are implicit on extract), sorted, no extra fields.
  find EfficientServer -type f -print | LC_ALL=C sort | tee "$ENTRIES" | zip -q -X "$ZIP_TMP" -@
)
# pipefail covers a failing find/zip above, but a stage that legitimately
# contains no files still yields a well-formed empty zip, which would publish
# under the release name. The staged file count is the ground truth.
EXPECTED="$(find "$STAGE/EfficientServer" -type f | wc -l)"
ACTUAL="$(wc -l <"$ENTRIES")"
if [[ "$EXPECTED" -ne "$ACTUAL" || "$ACTUAL" -eq 0 ]]; then
  echo "ERROR: archive holds $ACTUAL entries, staged tree has $EXPECTED" >&2
  exit 1
fi
rm -f "$ENTRIES"
mv -f "$ZIP_TMP" "$OUT"
ZIP_TMP=""
# The release attachment is picked by name out of dist/, which keeps every zip
# this tree has ever packaged (`make clean` is the only thing that clears it).
# Name the sibling zips so the wrong one cannot be uploaded by a glance at the
# directory listing instead of at this line.
shopt -s nullglob
SIBLINGS=("$ROOT"/dist/EfficientServer-*.zip)
shopt -u nullglob
if [[ ${#SIBLINGS[@]} -gt 1 ]]; then
  echo "NOTE: dist/ holds ${#SIBLINGS[@]} zips; this run produced $(basename "$OUT")."
  for z in "${SIBLINGS[@]}"; do
    [[ "$z" == "$OUT" ]] || echo "      other: ${z##*/}"
  done
fi
echo "Packaged -> $OUT (entry mtime epoch $EPOCH)"
