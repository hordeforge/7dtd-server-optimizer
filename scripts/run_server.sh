#!/usr/bin/env bash
# Start the Linux 7 Days to Die dedicated server with recommended environment
# knobs for EfficientServer performance.
#
# Usage:
#   scripts/run_server.sh [--ds /path/to/server] [extra server args...]
#
# Environment:
#   SEVENDTD_DS_DIR / DS   Directory holding 7DaysToDieServer.x86_64
#                          Default: ~/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server
#   MALLOC_ARENA_MAX       glibc memory arena cap (default 2; prevents arena-per-core fragmentation)
#   FREE_SPACE_DIVISOR     Boehm GC target free heap divisor (default 1; ~2x live set)

set -euo pipefail

SCRIPTDIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPTDIR/.." && pwd)"

# Resolve dedicated server directory
SRV="${SEVENDTD_DS_DIR:-${DS:-$HOME/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server}}"
if [[ $# -ge 2 && "$1" == "--ds" ]]; then
  SRV="$2"
  shift 2
fi

BIN="$SRV/7DaysToDieServer.x86_64"
if [[ ! -x "$BIN" ]]; then
  echo "ERROR: dedicated server binary not executable: $BIN" >&2
  exit 1
fi

# Environment knobs
export LD_LIBRARY_PATH="$SRV:${LD_LIBRARY_PATH:-}"
export MALLOC_ARENA_MAX="${MALLOC_ARENA_MAX:-2}"

# Boehm GC RAM-headroom (EAC-safe, read by the GC at process init): trade RAM for
# fewer + shorter GC pauses (GC is ~30% of aggregate CPU). See 7dtd-server-optimizer/docs/runtime-tuning.md.
# (env vars verified honored by this build's libmonobdwgc-2.0.so.)
#  - FREE_SPACE_DIVISOR: keep more free heap -> collect LESS often. Heap settles at
#    ~live_set * (1 + 1/divisor). Default 3 (~1.33x live). A/B validated: 1 (~2x live)
#    halves GC cycles with zero stability cost on a 16GB+ host.
#  - MAXIMUM_HEAP_SIZE: soft ceiling; GC tries harder to collect as heap nears it.
#    Default: unset (unbounded). Unset is safer on dedicated servers (GC uses
#    FREE_SPACE_DIVISOR without triggering panic collections).
export GC_FREE_SPACE_DIVISOR="${FREE_SPACE_DIVISOR:-1}"

cd "$SRV"
echo "Starting 7 Days to Die Dedicated Server..."
echo "  Binary:  $BIN"
echo "  Config:  $SRV/serverconfig.xml"
echo "  GC Div:  GC_FREE_SPACE_DIVISOR=$GC_FREE_SPACE_DIVISOR"
echo "  Arenas:  MALLOC_ARENA_MAX=$MALLOC_ARENA_MAX"

exec "$BIN" -logfile "$SRV/7DaysToDieServer_Data/output_log.txt" -configfile="$SRV/serverconfig.xml" -dedicated "$@"
