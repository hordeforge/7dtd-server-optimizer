#!/usr/bin/env bash
set -euo pipefail
# Pin locale/timezone so compiler diagnostics and file ordering do not vary
# with the build host's environment.
export LC_ALL=C TZ=UTC
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

usage() {
  cat <<'EOF'
usage: scripts/build.sh [-h | --help]

Compiles Source/EfficientServer into dist/EfficientServer. Takes no arguments;
everything is read from the environment.
  -h, --help  show this help and exit

Environment:
  SEVENDTD_DS_DIR / DS      dedicated install root (falls back to the Steam
                            client install for the game DLLs)
  SEVENDTD_GAME_DIR         Steam client install root
  SEVENDTD_BUILD_BACKEND    auto (default) | dotnet | mcs. 'dotnet' fails loudly
                            when no SDK is present instead of falling back
  DOTNET_ROOT               SDK directory to prepend to PATH when set
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
    echo "ERROR: build.sh takes no arguments, got: $*" >&2
    usage >&2
    exit 2
    ;;
esac

# Prefer local SDK installs (not /tmp)
if [[ -x "${DOTNET_ROOT:-}/dotnet" ]]; then
  export PATH="${DOTNET_ROOT}:$PATH"
elif [[ -x "$HOME/.cache/dotnet-sdk/dotnet" ]]; then
  export DOTNET_ROOT="$HOME/.cache/dotnet-sdk"
  export PATH="$DOTNET_ROOT:$PATH"
fi
SRV="${SEVENDTD_DS_DIR:-${DS:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}}"
# DS is the second accepted spelling (see --help above and install.sh, which
# hands its resolved path to this script); an exported-but-empty one must fail
# instead of silently resolving to the stock default, so the compile never
# happens against a different install than the operator named.
for _var in SEVENDTD_DS_DIR DS; do
  if [[ -n "${!_var+x}" && -z "${!_var}" ]]; then
    echo "ERROR: $_var is set but empty; pass a real install dir or unset it." >&2
    exit 1
  fi
done
unset _var
CLIENT="${SEVENDTD_GAME_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days To Die}"
if [[ -f "$SRV/7DaysToDieServer_Data/Managed/Assembly-CSharp.dll" ]]; then
  MANAGED="$SRV/7DaysToDieServer_Data/Managed"
  HARMONY="$SRV/Mods/0_TFP_Harmony/0Harmony.dll"
elif [[ -f "$CLIENT/7DaysToDie_Data/Managed/Assembly-CSharp.dll" ]]; then
  MANAGED="$CLIENT/7DaysToDie_Data/Managed"
  HARMONY="$CLIENT/Mods/0_TFP_Harmony/0Harmony.dll"
else
  echo "ERROR: Assembly-CSharp.dll not found under dedicated or client install" >&2
  exit 1
fi

OUT="$ROOT/dist/EfficientServer"
SRC="$ROOT/Source/EfficientServer"

# Shared by both backends so dist contents cannot drift between them.
finish() {
  # Fail here, not at the game's load attempt: package.sh zips this directory
  # wholesale, so a mod folder with no DLL would ship as a silent no-op mod.
  if [[ ! -s "$OUT/EfficientServer.dll" ]]; then
    echo "ERROR: $OUT/EfficientServer.dll is missing or empty after the build" >&2
    exit 1
  fi
  cp "$SRC/ModInfo.xml" "$OUT/ModInfo.xml"
  cp "$ROOT/config/efficientserver.json" "$OUT/Config/efficientserver.json"
  # MIT requires the license text to accompany redistribution, and the zip and
  # an installed mod must carry the same file set: copy into dist here so both
  # package.sh (zips dist wholesale) and install.sh inherit it.
  cp "$ROOT/LICENSE" "$OUT/LICENSE.txt"
  echo "OK -> $OUT/EfficientServer.dll"
  ls -la "$OUT"
}

# Output dir, not an incremental cache: wipe so files removed upstream (or a
# leftover .pdb from an older build) cannot leak into the packaged mod.
rm -rf "$OUT"
mkdir -p "$OUT/Config"
# Same reasoning for the dotnet backend's intermediate dir, which is otherwise
# left in the source tree. MSBuild's up-to-date check does not track
# GameManagedDir/HarmonyPath, so a rebuild against a different game install
# (or a Harmony update under the same path) can reuse an assembly resolved
# against the previous one. The compile is seconds; correctness wins.
rm -rf "${SRC:?}/obj" "${SRC:?}/bin"

# Prefer official .NET SDK; SEVENDTD_BUILD_BACKEND=mcs verifies the fallback.
BUILD_BACKEND="${SEVENDTD_BUILD_BACKEND:-auto}"
if [[ "$BUILD_BACKEND" != "mcs" ]] && command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q .; then
  echo "Building with dotnet SDK against: $MANAGED"
  dotnet build "$SRC/EfficientServer.csproj" -c Release \
    -p:GameManagedDir="$MANAGED" -p:HarmonyPath="$HARMONY" \
    -p:EfficientServerOutput="$OUT/" \
    -p:ContinuousIntegrationBuild=true
  finish
  exit 0
fi

if [[ "$BUILD_BACKEND" == "dotnet" ]]; then
  echo "ERROR: dotnet backend requested but no SDK is available" >&2
  exit 1
fi
command -v mcs >/dev/null 2>&1 || { echo "ERROR: mcs fallback compiler not found" >&2; exit 1; }

echo "Building with mcs -nostdlib against: $MANAGED"
# Only game-provided BCL + engine to avoid dual mscorlib with host Mono.
refs=(
  -r:"$MANAGED/mscorlib.dll"
  -r:"$MANAGED/netstandard.dll"
  -r:"$MANAGED/System.dll"
  -r:"$MANAGED/System.Core.dll"
  -r:"$MANAGED/System.Runtime.dll"
  -r:"$MANAGED/Assembly-CSharp.dll"
  -r:"$MANAGED/UnityEngine.CoreModule.dll"
  -r:"$MANAGED/UnityEngine.AnimationModule.dll"
  -r:"$MANAGED/UnityEngine.dll"
  -r:"$HARMONY"
  -r:"$MANAGED/Newtonsoft.Json.dll"
  -r:"$MANAGED/LogLibrary.dll"
  -r:"$MANAGED/AstarPathfindingProject.dll"
)

# Sort explicitly: find order is readdir order, and source order changes the
# emitted metadata layout of the DLL. Prune bin/obj so SDK-generated artifacts
# (e.g. obj/Release/*.AssemblyAttributes.cs left by a prior dotnet build) are
# never compiled into the shipped DLL.
mapfile -d '' sources < <(find "$SRC" -type d \( -name bin -o -name obj \) -prune -o \
  -type f -name '*.cs' -print0 | LC_ALL=C sort -z)
# -pathmap maps the compile root to a fixed token so building from a different
# directory cannot leak a path into the emitted metadata. A no-op while debug
# info is off (no -debug below), kept so the guarantee survives one being added.
# It does NOT make this backend reproducible: mcs 6.12 stamps a random MVID
# and ignores -deterministic, so two mcs builds of one tree differ regardless.
# Only the dotnet backend is byte-reproducible; see verify_reproducible.sh.
mcs -nostdlib -sdk:4.7.2 -target:library -optimize+ -langversion:7.2 \
  -pathmap:"$SRC=/es" \
  -out:"$OUT/EfficientServer.dll" \
  "${refs[@]}" \
  "${sources[@]}"

finish
