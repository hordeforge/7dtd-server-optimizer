#!/usr/bin/env bash
# Efficient dedicated server launcher: config override, timestamped logs, Mono GC /
# JIT friendly env with A/B-measured defaults, optional CPU affinity.
#
# Usage:
#   scripts/run_server.sh [--ds /path/to/server] [extra server args...]
#
# Environment (all optional; see docs/DEVELOPMENT.md and docs/CONFIG.md):
#   SEVENDTD_DS_DIR / DS   Dedicated install root (default: ~/.local/share/Steam/
#                          steamapps/common/7 Days to Die Dedicated Server)
#   SEVENDTD_CONFIG        Serverconfig XML passed as -configfile. Default:
#                          server/serverconfig.optimized.xml if present, else the
#                          tracked repo-root one
#   SEVENDTD_LOGDIR        Log directory for the timestamped server log
#                          (default: server/logs)
#   MALLOC_ARENA_MAX       glibc memory arena cap (default 2; prevents arena-per-core
#                          fragmentation)
#   GC_FREE_SPACE_DIVISOR  Boehm heap headroom divisor (default 1; ~2x live set).
#                          Legacy spelling FREE_SPACE_DIVISOR still accepted
#   GC_NPROCS              Boehm marking processors (default: nproc)
#   MONO_ENV_OPTIONS       Mono JIT options (default -O=all; set empty to disable)
#   GC_INITIAL_HEAP_SIZE   Optional heap preallocation, passed through when set
#   GC_USE_ENTIRE_HEAP     Set 1 to collect only when the whole heap is full
#   SEVENDTD_GC_INCREMENTAL  Set to enable incremental GC (GC_ENABLE_INCREMENTAL=1)
#   GC_PAUSE_TIME_TARGET   Forwarded ONLY together with SEVENDTD_GC_INCREMENTAL
#   SEVENDTD_CPU_AFFINITY  taskset -c mask for the whole process; silently skipped
#                          when taskset is absent. Leave off by default (measured
#                          loss on naive pinning, see docs/HOST_TUNING.md)

set -euo pipefail

SCRIPTDIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPTDIR/.." && pwd)"

# Resolve dedicated server directory
SRV="${SEVENDTD_DS_DIR:-${DS:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}}"
# A bare --ds with no path must fail here, not fall through and pass "--ds"
# to the server binary as a stray launch argument.
if [[ "${1:-}" == "--ds" ]]; then
  if [[ $# -lt 2 ]]; then
    echo "ERROR: --ds needs a path argument: scripts/run_server.sh [--ds /path/to/server] [extra server args...]" >&2
    exit 1
  fi
  SRV="$2"
  shift 2
fi

BIN="$SRV/7DaysToDieServer.x86_64"
if [[ ! -x "$BIN" ]]; then
  echo "ERROR: dedicated server binary not executable: $BIN" >&2
  exit 1
fi

# Config override, then a local launch copy under server/ (gitignored), then the
# tracked root config so a fresh clone can launch without extra setup.
CFG="${SEVENDTD_CONFIG:-}"
if [[ -z "$CFG" ]]; then
  if [[ -f "$ROOT/server/serverconfig.optimized.xml" ]]; then
    CFG="$ROOT/server/serverconfig.optimized.xml"
  else
    CFG="$ROOT/serverconfig.optimized.xml"
  fi
fi
if [[ ! -f "$CFG" ]]; then
  echo "ERROR: serverconfig XML not found: $CFG (set SEVENDTD_CONFIG)" >&2
  exit 1
fi

LOGDIR="${SEVENDTD_LOGDIR:-$ROOT/server/logs}"
mkdir -p "$LOGDIR"

# :+ keeps a trailing empty element out of the list: an empty entry makes the
# loader treat the CURRENT DIRECTORY as a library source for the process
# lifetime, not just at launch.
export LD_LIBRARY_PATH="$SRV${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
# Reduce Unity player noise; dedicated headless.
export MALLOC_ARENA_MAX="${MALLOC_ARENA_MAX:-2}"

# Boehm GC RAM-headroom (EAC-safe, read by the GC at process init): trade RAM for
# fewer + shorter GC pauses (GC is ~30% of aggregate CPU). See docs/runtime-tuning.md.
# (env vars verified honored by this build's libmonobdwgc-2.0.so.)
#  - FREE_SPACE_DIVISOR: keep more free heap -> collect LESS often. Heap settles at
#    ~live_set * (1 + 1/divisor). Default 1 (~2x live). A/B validated: full
#    collections 3 -> 0 in the aggregate window, worst STW 274 -> 0 ms.
#  - GC_NPROCS: processors Boehm uses -> parallel marking (~NPROCS-1 threads).
#    Marginal but free; default to core count.
export GC_FREE_SPACE_DIVISOR="${GC_FREE_SPACE_DIVISOR:-${FREE_SPACE_DIVISOR:-1}}"
export GC_NPROCS="${GC_NPROCS:-$(nproc 2>/dev/null || echo 4)}"
# Mono JIT: -O=all measured a consistent ~5% section-avg win at blood-moon load
# (single A/B pair, direction-consistent across all timed sections). EAC-safe env;
# override or set empty to disable.
export MONO_ENV_OPTIONS="${MONO_ENV_OPTIONS--O=all}"
[[ -n "${GC_INITIAL_HEAP_SIZE:-}" ]] && export GC_INITIAL_HEAP_SIZE
[[ -n "${GC_USE_ENTIRE_HEAP:-}" ]] && export GC_USE_ENTIRE_HEAP
# Optional: opt-in incremental GC (off by default); the pause target is forwarded
# ONLY together with it.
if [[ -n "${SEVENDTD_GC_INCREMENTAL:-}" ]]; then
  export GC_ENABLE_INCREMENTAL=1
  [[ -n "${GC_PAUSE_TIME_TARGET:-}" ]] && export GC_PAUSE_TIME_TARGET
fi

# Optional CPU affinity wrapper (default OFF - A/B MEASURED that naive main-thread
# pinning HURTS on this Ryzen 9950X: jitter +122%; it defeats CPPC preferred-core
# boost + adds cross-CCD latency, so the OS scheduler wins). Only use a CPPC-aware
# or single-CCD mask; silently skipped when taskset is absent (docs/HOST_TUNING.md).
WRAP=()
if [[ -n "${SEVENDTD_CPU_AFFINITY:-}" ]] && command -v taskset >/dev/null 2>&1; then
  WRAP=(taskset -c "$SEVENDTD_CPU_AFFINITY")
fi

cd "$SRV"
# UTC stamp: local time repeats an hour at every DST fall-back, so restarts
# inside the repeated hour (same wall second) would reuse and clobber the
# previous server log exactly when you need the crash evidence.
TS="$(date -u +%Y%m%d_%H%M%S)"
LOG="$LOGDIR/server_$TS.log"

# If config is outside the install tree, copy it beside the binary (the game often
# resolves -configfile relative to its cwd).
CFG_ARG="$(basename "$CFG")"
if [[ "$(readlink -f "$(dirname "$CFG")")" != "$(readlink -f "$SRV")" ]]; then
  cp -f "$CFG" "$SRV/$CFG_ARG"
fi

echo "Starting 7 Days to Die Dedicated Server..."
echo "  Binary:  $BIN"
echo "  Config:  $CFG_ARG"
echo "  Log:     $LOG"
echo "  GC:      FREE_SPACE_DIVISOR=$GC_FREE_SPACE_DIVISOR NPROCS=$GC_NPROCS${GC_INITIAL_HEAP_SIZE:+ INITIAL_HEAP=$GC_INITIAL_HEAP_SIZE}${GC_USE_ENTIRE_HEAP:+ USE_ENTIRE_HEAP=$GC_USE_ENTIRE_HEAP}${SEVENDTD_GC_INCREMENTAL:+ INCREMENTAL=1}"
echo "  JIT:     MONO_ENV_OPTIONS=$MONO_ENV_OPTIONS"
echo "  Arenas:  MALLOC_ARENA_MAX=$MALLOC_ARENA_MAX"
[[ ${#WRAP[@]} -gt 0 ]] && echo "  Affinity: taskset -c $SEVENDTD_CPU_AFFINITY"

CMD=("${WRAP[@]+"${WRAP[@]}"}")
CMD+=("$BIN" -logfile "$LOG" -dedicated -configfile="$CFG_ARG")
CMD+=("$@")
exec "${CMD[@]}"
