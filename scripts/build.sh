#!/usr/bin/env bash
set -euo pipefail
# Pin locale/timezone so compiler diagnostics and file ordering do not vary
# with the build host's environment.
export LC_ALL=C TZ=UTC
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# Prefer local SDK installs (not /tmp)
if [[ -x "${DOTNET_ROOT:-}/dotnet" ]]; then
  export PATH="${DOTNET_ROOT}:$PATH"
elif [[ -x "$HOME/.cache/dotnet-sdk/dotnet" ]]; then
  export DOTNET_ROOT="$HOME/.cache/dotnet-sdk"
  export PATH="$DOTNET_ROOT:$PATH"
fi
SRV="${SEVENDTD_DS_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}"
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
# Output dir, not an incremental cache: wipe so files removed upstream (or a
# leftover .pdb from an older build) cannot leak into the packaged mod.
rm -rf "$OUT"
mkdir -p "$OUT/Config"

# Prefer official .NET SDK; SEVENDTD_BUILD_BACKEND=mcs verifies the fallback.
BUILD_BACKEND="${SEVENDTD_BUILD_BACKEND:-auto}"
if [[ "$BUILD_BACKEND" != "mcs" ]] && command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q .; then
  echo "Building with dotnet SDK against: $MANAGED"
  dotnet build "$SRC/EfficientServer.csproj" -c Release \
    -p:GameManagedDir="$MANAGED" -p:HarmonyPath="$HARMONY" \
    -p:EfficientServerOutput="$OUT/"
  cp "$SRC/ModInfo.xml" "$OUT/ModInfo.xml"
  cp "$ROOT/config/efficientserver.json" "$OUT/Config/efficientserver.json"
  echo "OK -> $OUT/EfficientServer.dll"
  ls -la "$OUT"
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
  -r:"$MANAGED/MemoryPack.dll"
  -r:"$MANAGED/AstarPathfindingProject.dll"
)

# Sort explicitly: find order is readdir order, and source order changes the
# emitted metadata layout of the DLL.
mapfile -d '' sources < <(find "$SRC" -type f -name '*.cs' -print0 | LC_ALL=C sort -z)
mcs -nostdlib -sdk:4.7.2 -target:library -optimize+ -langversion:7.2 \
  -out:"$OUT/EfficientServer.dll" \
  "${refs[@]}" \
  "${sources[@]}"

cp "$SRC/ModInfo.xml" "$OUT/ModInfo.xml"
cp "$ROOT/config/efficientserver.json" "$OUT/Config/efficientserver.json"
echo "OK -> $OUT/EfficientServer.dll"
ls -la "$OUT"
