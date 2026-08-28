# Runtime tuning surfaces (V 3.0.1 dedicated)

**Owns:** process-level knobs (Boehm GC symbols/env, `GC.Collect` gate, `settargetfps`, ModEvents lifecycle).  
**Scaling context:** [`measured-scaling.md`](measured-scaling.md). **Hub:** [`INDEX.md`](INDEX.md).

Reverse-engineered runtime knobs found while building the GC / lifecycle work.
These are process-level surfaces (GC, frame rate, lifecycle hooks), distinct from
the game-logic hot paths in the loop docs.

## 1. Boehm GC (`libmonobdwgc-2.0.so`)

Unity Mono uses the Boehm-Demers-Weiser collector - conservative, non-generational,
stop-the-world. You cannot swap it (recompiling the Unity runtime is not possible
/ EAC), but it exposes a **bounded-pause incremental mode** and tuning.

**Forced collect (the A7 target).** `GameManager.gmUpdate` counts down
`gcCountdownTimer`; at 0 it calls `System.GC.Collect()` and resets to **120 s** -
a forced full STW every 2 minutes. EfficientServer's `GcGuardPatch` transpiles
that single call.

**Exported symbols (P/Invoke - runtime, but needs a code mod → EAC-off):**

| Symbol | Effect |
|---|---|
| `GC_enable_incremental` | switch to incremental/generational mode (increments across frames) |
| `GC_set_time_limit_ns(ns)` / `GC_set_time_limit(ms)` | per-collection pause budget |
| `GC_set_free_space_divisor(n)` | space/throughput dial: higher = collect more aggressively (less heap growth, more frequent shorter pauses) |
| `GC_set_full_freq(n)` | do a full mark every N partials → space out long full-STW pauses |
| `GC_set_max_heap_size` / `GC_set_dont_expand` | heap-growth caps |
| `GC_start_incremental_collection` / `GC_collect_a_little` | proactively step a collection |
| `GC_parallel` / `GC_init_parallel` | parallel marking (mark with multiple threads → shorter mark pause) |

**Environment variables (EAC-SAFE - no mod, read by Boehm at process init):**
classic Boehm dials include `GC_ENABLE_INCREMENTAL`, `GC_PAUSE_TIME_TARGET` (ms),
`GC_MARKERS`, `GC_FREE_SPACE_DIVISOR`, `GC_FULL_FREQUENCY`. Setting them in the
server launch environment is the only EAC-compatible route to this lever, but
this build honors only a subset (verified string-table list below); e.g.
`GC_ENABLE_INCREMENTAL=1 ./7DaysToDieServer...`

**Honored GC env vars (verified in this build's `libmonobdwgc-2.0.so` string table):**
`GC_FREE_SPACE_DIVISOR`, `GC_INITIAL_HEAP_SIZE`, `GC_MAXIMUM_HEAP_SIZE`,
`GC_USE_ENTIRE_HEAP`, `GC_FULL_FREQUENCY`, `GC_NPROCS`, `GC_ENABLE_INCREMENTAL` /
`GC_DISABLE_INCREMENTAL`, `GC_PAUSE_TIME_TARGET`, `GC_FORCE_UNMAP_ON_GCOLLECT`,
`GC_DONT_GC`. **NOT honored:** `GC_MARKERS` / `GC_PAUSE_TIME_TARGET` alone (use
`GC_NPROCS` for parallel marking).

**`GC_FREE_SPACE_DIVISOR` value vs memory** (live working set ~6 GB on this world):

| divisor | free kept | heap settles ~ | collect frequency | single full-collect |
|--:|--:|--:|---|---|
| 3 (default) | heap/3 (~33%) | ~1.33x live (~8 GB) | baseline | smaller |
| 2 | ~50% | ~1.5x live (~9 GB) | ~0.7x | - |
| **1** (validated) | ~100% | ~2x live (~12 GB) | **~0.5x** (2->1 collects) | bigger |

Rule: heap ~= `live * (1 + 1/divisor)`; lower divisor -> fewer collects, larger
heap, **larger single full collect**. On a 123 GB host divisor `1` is free. Going
further (rarer collects) is done with **`GC_INITIAL_HEAP_SIZE`** (preallocate near
the working set, e.g. `8G`, to skip the startup collection burst) + optional
**`GC_USE_ENTIRE_HEAP=1`** (collect only when the whole heap is full). **Ceiling:** a
single FULL mark scales with heap (~480 ms measured at a 7 GB full collect), so do
**not** preallocate a huge heap (a 32 GB full collect ~= 1-2 s freeze); keep the heap
<= ~12 GB. **`GC_NPROCS`** (= core count) enables parallel marking (~NPROCS-1 threads). Do not
combine a low divisor with a huge preallocated heap - that recreates the megapause.

**4-way A/B (2026-07-20, 32 cores, ~320-360 zombies): only the divisor is a repeatable
win.** default / div1 / div1+NPROCS=32 / div1+8G-heap+USE_ENTIRE_HEAP:
- **`FREE_SPACE_DIVISOR=1` cut full collections 2 -> 1, total STW ~-40%** (again).
- **`GC_NPROCS=32` parallel marking: no measured steady-state benefit** - worst STW
  read 65.6 vs 53.9 ms (noise: 1 big collect vs 2 small). The STW sample is tiny
  (1-2 events/150 s), so pause *duration* cannot be isolated. Parallel marking only
  helps a **big full collect** (the megapause case), which did not occur here; set it
  as cheap insurance, not a proven win.
- **`GC_INITIAL_HEAP_SIZE=8G` + `GC_USE_ENTIRE_HEAP=1`: marginal** - lowest total STW
  (52.7 ms) but middling ms_per_tick. Optional.
ms_per_tick (46-57) was confounded by unequal zombie counts + tick variance, so the
clean signal is **collection frequency**, not tick time. Sessions
`session_20260720_022153` / `_022928` / `_023719` / `_024456`.

**Measured (2026-07-20, `GC_FREE_SPACE_DIVISOR=1` env A/B, heavy load):** Boehm
honors it - full collections **2 -> 1**, total STW **-30%** (102.8 -> 72.5 ms),
incremental slices -12%, even at higher churn. Tradeoff: the worst *single* STW
grew 52 -> 72 ms (bigger heap = bigger mark). **EAC-safe, zero-code, zero-fidelity
win** on a high-RAM host - trade RAM for fewer collections. Recommend
`GC_FREE_SPACE_DIVISOR=1` (max headroom) or `2` (if RSS matters) at launch. It is
downstream of allocation (not a ms_per_tick headline) but a real STW-smoothness
lever. Sessions `session_20260720_015900` (default) / `_020645` (headroom).

**Strongest evidence (2026-07-20, aggregate vanilla-vs-everything A/B, v1.8.0, matched
~320 zombies + 32 bots, both toggles verified in `/proc/<pid>/environ`):** with the
divisor unset (Boehm default 3) vanilla did **3 full collections and ate a single
274 ms stop-the-world freeze** in the 150 s window; with `GC_FREE_SPACE_DIVISOR=1` +
the skip-forced-collect guard, everything-on did **0 full collections, worst STW 0 ms**.
This is the divisor win at a heavier (10 GB-class) heap: the megapause is not merely
shorter, it did not fire at all in the window. Sessions `session_20260720_053013`
(vanilla) / `_053846` (everything). Full detail:
[`../../7dtd-server-optimizer/docs/RESULTS.md`](RESULTS.md) §3.

**Measured (2026-07-18, see `7dtd-server-optimizer` A7 benchmark):** the GC guard helps
at moderate churn (removes the forced collect, -28% late-tick overage) but is a
wash at heavy churn (Boehm auto-collects on pressure regardless). Incremental mode
is marginal (~8% late ticks at 128 players; write-barrier overhead ~cancels the
pause-shortening). GC tuning is downstream of allocation - it cannot cut the
churn; only cutting the allocation can. Per the corrected ranking
([`measured-scaling.md`](measured-scaling.md) §4b) the top steady-churn sources
at heavy load are pathfinding (`AstarVoxelGrid.InitScan`, nav-graph rebuild) then
network serialization (`PooledBinaryWriter.Write`, `NETWORK_OPTIMIZATION.md`).

## 2. Server frame rate

`settargetfps <N>` - vanilla console command (`ConsoleCmdSetTargetFps`), the
**same command for client and server**, sets `Application.targetFrameRate`. On a
dedicated server issue it over telnet. **Not persistent** (reset on restart).
There is no serverconfig.xml FPS property; the FPS options in the assembly
(`FpsLimitInGame`, `FrameRateLimiter`) are the client options menu. This is a
vanilla knob, not a mod concern.

## 3. Non-Harmony lifecycle hooks (`ModEvents`)

Sanctioned hooks that avoid a Harmony patch just for timing: `ModEvents.GameAwake`,
`GameStartDone`, `GameShutdown`, `PlayerSpawnedInWorld`, etc. Register via
`ModEvents.<Event>.RegisterHandler(handler)`. In V3.0.1 the handler is a **typed
`ref` delegate**: `ModEventHandlerDelegate<TData>(ref TData)` - e.g.
`GameStartDone` takes `ref ModEvents.SGameStartDoneData` (the older parameterless
`void()` form is pre-V1.0). Prefer these + public setters + P/Invoke over Harmony;
Harmony only for per-call behavior with no hook (see the IModApi-vs-Harmony note
in `7dtd-server-optimizer/docs/FEATURES.md`).

## 4. Correction: `PooledBinaryWriter.FinalizeSizeMarker`

An earlier optimizer draft claimed this method does per-serialize reflection
(`Type.GetMethod`). Direct IL inspection (DumpMethodByName, il=91) shows **no
per-call reflection**: it is an enum switch on `EMarkerSize` (Int8/16/32) + stream
position writes; the only reflection-ish call (`EnumUtils.ToStringCached`) is in an
error-throw path only. Not a valid allocation-cut target.

## See also
- [`measured-scaling.md`](measured-scaling.md) - runtime scaling laws, GC-pause vs CPU regimes
- [`network.md`](../../7dtd-engine-research/docs/network.md) - replication send path (the churn source)
- `7dtd-server-optimizer/docs/FEATURES.md` - EfficientServer GC guard / incremental, EAC
- `7dtd-server-optimizer/docs/NETWORK_OPTIMIZATION.md` - the network levers
