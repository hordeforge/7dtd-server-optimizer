# Pathfinding / nav-graph optimization (`AstarManager.UpdateGraphs`)

**Hub:** [`README.md`](../README.md).  
**Owns:** pathfinding optim notes.  
**Not:** entity AI full map ([research entity-ai](../../7dtd-research/docs/entity-ai.md)).


**Status:** P1 **BUILT + VALIDATED** (EfficientServer v1.3.0, `AstarGraphThrottlePatch`).
Live A/B (2026-07-18, 32 bots + ~270 zombies, N=4, `--only all,alloc`, 150 s each):
**ms_per_tick 54.95 → 39.28 (-28.5%)** - pulled the server from over the 50 ms tick
budget (tick-starved, sub-20 TPS) back to healthy 20 TPS. `UpdateGraphs` total
-35%, avg 14.0 → 3.1 ms (3-of-4 calls short-circuit in the prefix), p95 -55%.
Gross alloc only -3.6% (GC is downstream; `InitScan` still fires on the 1-in-4
real calls). **Fidelity intact:** zombies 277 → 277 stable under throttle (vs
273 → 266 baseline). Sessions `session_20260718_074155` (off) / `_074952` (on).
`AstarManager.UpdateGraphs` was the top
managed section at the heavy standard load (**66.6 ms**, 128 calls @ 64 players +
~340 zombies), and after fixing the APM alloc-attribution bug (see below)
`AstarVoxelGrid.InitScan` is confirmed the **#1 large-allocation site AND #1
steady-churn site** - so pathfinding is both the top CPU section and the top
allocator, making P1 the highest impact-to-effort lever. Graded B12 in
[`OPTIMIZATION_CANDIDATES.md`](OPTIMIZATION_CANDIDATES.md).

> **Grid magnitude (RE-pinned 2026-08-11):** the scan allocates a
> `76 x 76` cell grid at height **320** (`AstarVoxelGrid.cGridXZSize` /
> `cGridHeight`, [`7dtd-research/docs/raycast-pathing.md`](../../7dtd-research/docs/raycast-pathing.md))
> = ~1.85M voxel cells per grid per scan; `cConnectionPoolMax` **16** per node.
> The RE constants are machine-pinned by `7dtd-research`'s
> `test_tuned_constants.py`.

> **APM note (2026-07-18):** the alloc-attribution earlier reported noise
> (`GameTimer.Reset`, `String.Split`) because `_alloc_block_sites` read
> bpftrace's *ascending* ustack map top-down and stopped after 3, grabbing the
> smallest stacks' BCL leaves. Fixed to parse every (stack, bytes) record,
> attribute to the first game frame, and rank by total bytes. The true top
> allocators are `AstarVoxelGrid.InitScan`, `TerrainSubMesh.Add`,
> `PooledBinaryWriter.Write`, `ItemStack.Clone` - which is why P1 (and P4 node
> pooling) target the right thing.

---

## 1. What it is (RE 2026-07-18)

7DTD uses **Aron Granberg's A\* Pathfinding Project** (`Pathfinding.*` namespace,
external managed DLL). `AstarManager` keeps a set of **voxel nav-graphs
(`AstarVoxelGrid`) that follow the players** - a moving window of walkability
around each observer.

`AstarManager.UpdateGraphs(float)` (IL 185), run **every tick** (managed section,
one call/tick):

1. `mergedLocations.Clear()`; then **for each player** `Merge(player.pos.xz, size)`
   - build the set of regions to keep graphed (players + timed `locations` such
     as blood-moon / POI, which decay by `duration`).
2. For each `AstarVoxelGrid` in `graphList` × each merged location →
   `UpdateGraphPos(grid, pos)` (IL 60): if the grid `IsMoving`, compute the delta,
   `SetPos(gridPos)`, and if it moved past a threshold, `Insert` it into `moveList`.
3. **`UpdateMoveGraph()` (called once inside `UpdateGraphs`, IL_018E, its sole
   caller)** drains **one** queued grid-move per call: `RemoveAt(0)` + `MoveGraph`,
   gated on the prior move finishing (`IsMoving`). `MoveGraph` triggers
   `AstarVoxelGrid.InitScan` (IL 3) → `Pathfinding.NavGraph.Scan()` - rebuilds the
   grid's node walkability (per-cell raycasts), allocating the node arrays. This
   is the large-alloc + the cost. Note the drain is **not** a separate per-tick
   method: it lives inside `UpdateGraphs` and processes at most one move per call.

`UpdateGraphPos(AstarVoxelGrid, Vector2)`, `UpdateMoveGraph()` (internal one-move
drain), `FindMoveIndex`, `LocalPosToGridPos`, `SetPos` are the supporting surfaces.

**Measured scaling** (`apm scaling --by players`): `UpdateGraphs` per-call
O(N^0.27) but **total O(N^1.43)** - super-linear in players, because merged
locations (and thus grid `SetPos`/scan work) grow with the player count. It is
**player-driven maintenance**, distinct from the path *compute*
(`EntityAlive.FindPath` → `ASPPathFinderThread.FindPaths`, drained ≤8/slice),
which is zombie-driven.

---

## 2. Why it is expensive

- **Every tick.** A player barely moves in 50 ms, yet the follow-graph is
  re-evaluated (and often re-scanned) at 20 Hz.
- **Per player × per graph.** More observers → more merged locations → more grids
  moving → more `NavGraph.Scan` calls.
- **Scan reallocates.** `NavGraph.Scan()` rebuilds node arrays on each move
  (`InitScan` = top large-alloc), feeding the GC-pause problem.

---

## 3. Levers (prioritized)

### P1 - Rate-limit `UpdateGraphs` (B12). Biggest, lowest risk. **BUILT.**

Run graph maintenance every **N ticks** (e.g. 4-8 → 2.5-5 Hz) instead of every
tick. A follow-graph that lags 200-400 ms is fine: players don't outrun the grid
window in that time, and paths are recomputed continuously against whatever graph
exists. Cuts `UpdateGraphs` cost ~N×.

- **Built:** `Patches/AstarGraphThrottlePatch.cs` - Harmony **prefix** on
  `AstarManager.UpdateGraphs(float)`; returns `false` on non-Nth ticks. Single
  config knob `Pathfinding.GraphUpdateEveryTicks` (default **4**); `1` = vanilla
  (no throttle). Named for what it controls - it does not enable/disable
  pathfinding, only the graph-maintenance rate.
- **Safety (RE-verified, corrected 2026-07-18):** `UpdateGraphs` both queues grid
  moves (`UpdateGraphPos`) **and** drains one via `UpdateMoveGraph` per call (it is
  the sole caller - `UpdateMoveGraph` is *not* a separate per-tick method).
  Throttling to 1/N slows the whole maintenance cadence by N: the follow-graphs
  reposition/rescan at (20/N) Hz and drain headroom drops from 20/s to 20/N per
  second. Nothing is permanently stranded (`moveList` persists and drains on the
  next call), but under heavy simultaneous player movement the graphs can lag more
  than N ticks. AI keeps moving regardless: path *compute* (`EntityAlive.FindPath`
  → `PathFinderThread`) is the genuinely separate every-tick system, running
  against whatever graph exists. This makes the empirical fidelity gate (below)
  load-bearing, not a formality.
- **Impact:** ~66 ms → ~66/N ms of the per-tick budget; also thins the
  `InitScan` large-alloc/churn (fewer moves → fewer scans).
- **Risk:** low-medium. AI on the edge of a just-loaded region may path a few
  ticks late (brief "AI stands still"). Validate on fast-moving players + fresh
  chunk edges.
- **EAC/client:** server-internal, no wire change (client-safe); code → EAC-off.

### P2 - Raise the grid-move / rescan threshold. Fewer scans. **BUILT (v1.4.0).**

`UpdateGraphPos` queues a grid to `moveList` only past a move threshold: it compares
the grid-move `SqrMagnitude` against a constant **100** (squared grid units) and
skips the enqueue below it. Enlarging that dead-zone means a grid re-scans only
after drifting more cells, so fewer `NavGraph.Scan` (`InitScan`) calls = less CPU
**and** less allocation. Orthogonal to P1 and multiplies with it: P1 lowers the
maintenance *cadence*, P2 lowers the per-visit rescan *probability*.

- **Built:** `Patches/AstarMoveThresholdPatch.cs` - transpiler on
  `AstarManager.UpdateGraphPos(AstarVoxelGrid, Vector2)` (params pinned) replaces
  the sole `ldc.r4 100` operand with `Pathfinding.MoveRescanThresholdSq` (default
  **100 = vanilla**; clamped `[100, 10000]` in `Normalize()`). Throws MISSING if the
  `100` constant isn't found (fail-visibly on drift). Conservative first production
  value ~**400** (2x cell radius).
- **Impact (measured 2026-07-18, 60 bots / 0 zombies, dead-zone 100 vs 400):**
  `AstarManager.UpdateGraphs` **total -20.2%** (avg 5.78 → 5.15 ms, p95 34 → 32),
  confirming fewer rescans. But `ms_per_tick` was flat (+1.9%, noise): at a healthy
  ~20 ms/tick, `UpdateGraphs` is only ~14% of the tick, so a 20% cut is ~3% of the
  total and is swamped by run variance. P2 converts to tick-time only when graph
  maintenance is a large share (a stressed/graph-dominated server) or when GC-pause
  frequency matters (`InitScan` is the #1 allocator). Ships at default 100 (vanilla);
  400 is a mild, safe optimization for graph-heavy servers, not a universal win.
- **Strands nothing:** a below-threshold grid just isn't queued this visit and is
  re-tested next visit (`GridMovePendingPos` only written on enqueue); the
  `IsFullUpdateNeeded` branch snaps fresh grids immediately, bypassing the gate.
- **Risk:** low-medium (graph edge slightly staler on the leading side of motion -
  the same failure class the P1 gate exercises).

### P3 - ~~Budget scans per tick (`moveList` drain cap).~~ DROPPED - unsound.

**Disproved (2026-07-18):** `UpdateMoveGraph` already drains exactly **one** move per
call and `UpdateGraphs` is its sole once-per-tick caller, so vanilla already caps at
1 scan/tick - a downward "cap at K≥1" is a strict no-op. A catch-up (upward) drain is
also unsound: `MoveGraph` submits an async work item and sets `IsMoving`, so a loop
honoring the single-in-flight gate fires exactly once. The only implementable form is
*adaptive de-throttling* (dynamically lower P1's `every` under measured `moveList`
backlog) - and no backlog has been measured to justify it. Original (wrong) idea:

- **Surface:** Harmony around the `moveList` drain.
- **Impact:** flattens the tick p99 (no single tick pays for all scans).
- **Risk:** low (a deferred grid scans one tick later).

### P4 - Pool `NavGraph.Scan` node buffers. Cuts the large-alloc.

`InitScan` reallocates node arrays each move. Reuse a per-grid node buffer (the
grid dimensions are fixed) instead of allocating a fresh one when it moves.
Attacks the top large-alloc directly → fewer GC pauses.

- **Surface:** harder - `Scan()` lives in the external `Pathfinding` DLL; Harmony
  the grid's node-allocation path or the `AstarVoxelGrid` scan wrapper.
- **Impact:** removes a top spike-alloc site; complements the GC work.
- **Risk:** medium (buffer lifetime / correctness across moves).

### P5 - Merge clustering. Fewer regions when players group.

`Merge(pos, size)` already coalesces nearby locations; enlarging the merge radius
means clustered players share one graph region instead of one each. Helps most on
grouped populations (bases, traders).

- **Surface:** Harmony/config the merge size.
- **Risk:** low-medium (a grid covering more area may be coarser).

### P6 - Async scan (deferred / risky).

The A\* project supports threaded graph updates, but the path workers read the
graph; a scan racing a read needs the asset's work-item queue, not a naive thread.
High risk, research-only.

---

## 4. Sequencing

1. **P1 rate-limit** - one prefix, biggest win, ship behind a config + fidelity
   gate first.
2. **P2 move-threshold** + **P3 scan budget** - cheap, bound CPU + alloc spikes.
3. **P4 node pooling** - the alloc-cut for the GC-pause angle; do after P1-P3
   prove the harness.
4. **P5 / P6** situational.

Every code lever is server-side-only and wire-compatible (nav graphs are internal
AI infra), so a vanilla client connects; but code → EAC-off. The only EAC-safe
lever is workload reduction: fewer observers or entities via serverconfig
(`ServerMaxAllowedViewDistance`, `MaxSpawnedZombies`) reduce path demand, not the
per-tick maintenance.

## 5. Validation

Load: the canonical heavy standard (`plans/profile.canonical.json`, 64 players +
~300 zombies) - where `UpdateGraphs` was the top section. Measure with
`7dtd-apm capture --reset-bridge`: `AstarManager.UpdateGraphs` section ms, tick
p99, gross alloc MB/s + `InitScan` large-alloc frequency; `apm compare` before/
after. **Fidelity gate:** zombies must still path to players across fresh chunk
edges and fast player movement - a scripted "AI reaches target within T" check +
manual blood-moon spot check. No lever ships that leaves AI visibly stuck.
