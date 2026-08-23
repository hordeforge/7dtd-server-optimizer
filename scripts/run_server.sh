#!/usr/bin/env bash
# Efficient dedicated server launcher: CPU affinity optional, mono GC friendly env, config override.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRV="${SEVENDTD_DS_DIR:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}"
# Config override, then a local launch copy under server/ (gitignored), then
# the tracked root config so a fresh clone can launch without extra setup.
CFG="${SEVENDTD_CONFIG:-}"
if [[ -z "$CFG" ]]; then
  if [[ -f "$ROOT/server/serverconfig.optimized.xml" ]]; then
    CFG="$ROOT/server/serverconfig.optimized.xml"
  else
    CFG="$ROOT/serverconfig.optimized.xml"
  fi
fi
LOGDIR="${SEVENDTD_LOGDIR:-$ROOT/server/logs}"
mkdir -p "$LOGDIR"

if [[ ! -x "$SRV/7DaysToDieServer.x86_64" ]]; then
  echo "ERROR: dedicated server binary missing at $SRV" >&2
  exit 1
fi

# Ensure EfficientServer is present
if [[ ! -f "$SRV/Mods/EfficientServer/EfficientServer.dll" ]]; then
  echo "Installing EfficientServer mod..."
  "$ROOT/scripts/install.sh"
fi

export LD_LIBRARY_PATH="$SRV:${LD_LIBRARY_PATH:-}"
# Reduce Unity player noise; dedicated headless.
export MALLOC_ARENA_MAX="${MALLOC_ARENA_MAX:-2}"

# Boehm GC RAM-headroom (EAC-safe, read by the GC at process init): trade RAM for
# fewer + shorter GC pauses (GC is ~30% of aggregate CPU). See docs/runtime-tuning.md.
# (env vars verified honored by this build's libmonobdwgc-2.0.so.)
#  - FREE_SPACE_DIVISOR: keep more free heap -> collect LESS often. Heap settles at
#    ~live_set * (1 + 1/divisor). Default 3 (~1.33x live). A/B validated: 1 (~2x live)
#    cut full collections 2->1 and total STW -30%. Lower=fewer collects+more RSS.
#  - GC_NPROCS: processors Boehm uses -> parallel marking (~NPROCS-1 threads). A/B
#    showed NO steady-state benefit (normal collects are small); it only shortens a
#    rare BIG full collect. Cheap insurance; default to core count.
#  - GC_INITIAL_HEAP_SIZE: preallocate near the working set (~6-8G) so early-game
#    growth does not trigger a burst of startup collections. Optional; e.g. 8G.
#  - GC_USE_ENTIRE_HEAP=1: collect only once the whole heap is full (fewer collects).
#    Do NOT let the heap get so big a single FULL mark exceeds ~150 ms (mark scales
#    with heap: ~480 ms measured at a 7 GB full collect); keep INITIAL_HEAP <= ~12 G.
export GC_FREE_SPACE_DIVISOR="${GC_FREE_SPACE_DIVISOR:-1}"
export GC_NPROCS="${GC_NPROCS:-$(nproc 2>/dev/null || echo 4)}"
# Mono JIT: -O=all measured a consistent ~5% section-avg win at blood-moon load
# (single A/B pair, direction-consistent across all timed sections; the -O=all arm
# carried more zombies and still ran faster). EAC-safe env; override or empty to
# disable.
export MONO_ENV_OPTIONS="${MONO_ENV_OPTIONS:--O=all}"
[[ -n "${GC_INITIAL_HEAP_SIZE:-}" ]] && export GC_INITIAL_HEAP_SIZE
[[ -n "${GC_USE_ENTIRE_HEAP:-}" ]] && export GC_USE_ENTIRE_HEAP
# Optional: opt-in incremental GC (marginal in A/B; off by default).
if [[ -n "${SEVENDTD_GC_INCREMENTAL:-}" ]]; then
  export GC_ENABLE_INCREMENTAL=1
  [[ -n "${GC_PAUSE_TIME_TARGET:-}" ]] && export GC_PAUSE_TIME_TARGET
fi

# Optional CPU affinity (default OFF - A/B MEASURED that naive main-thread pinning
# HURTS on this Ryzen 9950X: jitter +122%, because it overrides CPPC preferred-core
# boost + adds cross-CCD latency; the OS scheduler wins. Only set this with a
# CPPC-aware / single-CCD mask, see docs/HOST_TUNING.md). SEVENDTD_CPU_AFFINITY="0-7,16-23"
# -> taskset -c on the whole process. Requires taskset.
WRAP=()
if [[ -n "${SEVENDTD_CPU_AFFINITY:-}" ]] && command -v taskset >/dev/null 2>&1; then
  WRAP=(taskset -c "$SEVENDTD_CPU_AFFINITY")
fi

cd "$SRV"
TS="$(date +%Y%m%d_%H%M%S)"
LOG="$LOGDIR/server_$TS.log"
echo "Starting dedicated server..."
echo "  config=$CFG"
echo "  log=$LOG"

# If config is outside install tree, copy/symlink beside binary (game expects relative path often).
CFG_ARG="$(basename "$CFG")"
if [[ "$(readlink -f "$(dirname "$CFG")")" != "$(readlink -f "$SRV")" ]]; then
  cp -f "$CFG" "$SRV/$CFG_ARG"
fi

echo "  GC: FREE_SPACE_DIVISOR=$GC_FREE_SPACE_DIVISOR NPROCS=$GC_NPROCS${GC_INITIAL_HEAP_SIZE:+ INITIAL_HEAP=$GC_INITIAL_HEAP_SIZE}${GC_USE_ENTIRE_HEAP:+ USE_ENTIRE_HEAP=$GC_USE_ENTIRE_HEAP}${WRAP[*]:+ affinity=$SEVENDTD_CPU_AFFINITY}"
exec "${WRAP[@]}" ./7DaysToDieServer.x86_64 \
  -logfile "$LOG" \
  -quit -batchmode -nographics -dedicated \
  -configfile="$CFG_ARG" \
  "$@"
