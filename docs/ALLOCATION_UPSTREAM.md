# Upstream allocation reduction plan

**Owns:** the plan to cut managed allocation at its source (the real GC lever).
**Not:** GC cadence tuning (that is [`FEATURES.md`](FEATURES.md) GC guard / incremental,
proven secondary) or network wire design ([`NETWORK_OPTIMIZATION.md`](NETWORK_OPTIMIZATION.md)).

---

## 0. Why upstream, not GC

Every GC cadence we have measured leaves **gross allocation churn essentially
invariant**: at 128 players the forced / guard / incremental configs churned
15.16 / 14.84 / 14.52 MB/s (`OPTIMIZATION_CANDIDATES.md` §A7). The two extremes
confirm the same thing from opposite ends:

- **Never collect (GC_disable):** the megapause diagnostic (v1.5.1,
  `Diagnostics.GcMegapauseTest`) disabled Boehm under heavy load, grew the heap
  120s, then timed one forced full collect: **PAUSE_MS = 479** on a **6.91 GB heap
  (~5.6 GB live)** = a **479 ms stop-the-world freeze** (~10 missed 50 ms ticks).
  Note: the live working heap is already ~5.6 GB under heavy load, and it grew at
  ~10 MB/s (matching the measured churn). The pause is dominated by marking the
  live set + sweeping the whole heap (O(heap)), so a bigger deferred heap (16 GB)
  freezes proportionally longer (~1 s+). A never-collect scheme concentrates the
  cost into this one freeze instead of amortizing it; Boehm is non-compacting and
  conservative. (`GC_get_heap_size` reports Boehm's retained capacity, not the live
  set, so it does not shrink post-collect - the pause is the real signal.)
- **Collect every tick:** each forced full collect marks the multi-GB live set;
  at 20 Hz that is deterministically over the 50 ms tick budget.

GC tuning is **downstream of allocation**: it changes *when* you pay, never *how
much*. The only way to cut the GC-pause floor is to allocate less. This plan
attacks the top allocators named by the corrected APM attribution
(`_alloc_block_sites`, ranked by bytes, attributed to the owning game frame).

---

## 1. Allocator inventory (corrected APM, heavy standard load)

| Rank | Site | Subsystem | What allocates | Lever |
|---|---|---|---|---|
| 1 | `AstarVoxelGrid.InitScan` | pathfinding | nav-graph node array, rebuilt per grid move | **A (P4 pooling)** |
| 2 | `TerrainSubMesh.Add` | dynamic mesh | sub-mesh vertex/index buffers | C |
| 3 | `PooledBinaryWriter.Write` | network | per-player entity package serialization | **B (L1 serialize-once)** |
| 4 | `ItemStack.Clone` | inventory | defensive item copies | C |
| 5 | `ChunkBlockChannel.Write` | chunk send | chunk block serialization buffers | C (overlaps B) |

`InitScan` is both the #1 large-allocation spike **and** the #1 steady-churn
source, so Lever A is the single biggest gross-MB/s reduction; Lever B is the
biggest at pure player-scale (the O(players x entities) replication wall).

---

## 2. Lever A - Pathfinding node-array pooling (P4). Attacks #1.

### Source (RE-verified)
`AstarManager.UpdateGraphs` -> (on a queued move) `UpdateMoveGraph` -> `MoveGraph`
-> `AstarVoxelGrid.InitScan` (IL 3, Assembly-CSharp) -> `Pathfinding.NavGraph.Scan()`
-> `AstarVoxelGrid.ScanInternal` -> `LayerGridGraph.ScanInternal` (external
`AstarPathfindingProject.dll`). The grid scan **reallocates the node array** each
time a grid moves. The grid dimensions are fixed per graph, so the array length is
**constant across moves** - it is re-minted purely because the scan always news a
fresh buffer. That is the pooling opportunity.

### Concurrency (the crux, now resolved)
The earlier concern was that path worker threads (`ASPPathFinderThread` ->
`AstarPath.StartPath`) read `graph.nodes` while a scan rebuilds it, so reusing a
buffer could hand a half-cleared array to a live read. RE of
`AstarPathfindingProject.dll` shows the project **already serializes this**:
`AstarPath` has `isScanning` + a `PathProcessor.GraphUpdateLock workItemLock`;
graph scans/updates run through the work-item queue holding that lock, which
**pauses path calculation**. So during a scan no path thread is reading `nodes`.
Reuse within the scan window is therefore safe - the exclusion the design needs
already exists; we only piggy-back on it, we do not add locking.

### RE update (2026-07-19): array vs nodes
The allocation is **two parts**, both in the external `AstarPathfindingProject.dll`
`LayerGridGraph.ScanInternal` iterator (`newarr Pathfinding.LevelGridNode`):
1. the **node array** itself (`LevelGridNode[N]`) - a large-object-space alloc, the
   **#1 *large*-alloc** site;
2. the **N node *objects*** - `LevelGridNode` is a **class**, so each cell is a
   separate `new` - the #1 *churn* (N in the thousands per grid).
Pooling the array (option a/b below) kills the large-alloc spike; killing the churn
requires **reusing the node objects** (reinit existing instances instead of `new`),
a deeper change. Sequence: array reuse first (bounded, kills the large spike that
most feeds the megapause), node-object reuse only if churn still dominates after.

### Fix (two options, prefer the in-place reuse)
- **(a) Reuse-in-place (preferred, minimal):** transpile/prefix the node
  allocation in `LayerGridGraph.ScanInternal` so that when the grid already has a
  `nodes` buffer of the required length, it is **cleared and reused** instead of
  `newarr`. One-buffer-per-grid, lifetime = graph lifetime.
- **(b) Per-grid pool:** a small pool keyed by grid dimensions; `Rent` at scan
  start, `Return` when the grid is destroyed. More machinery; only needed if (a)'s
  length-invariance assumption ever breaks (it should not - fixed grid dims).

### Surface, effort, risk
- **Surface:** Harmony on `LayerGridGraph.ScanInternal` (external DLL; Harmony
  patches external assemblies fine) or the `AstarVoxelGrid` node-alloc helper it
  calls. The method is an **iterator state machine** (`<ScanInternal>d__NN`), so a
  transpiler must target the MoveNext body; a cleaner route is a prefix that
  pre-populates the reusable buffer and a postfix/field-swap. Pin the target and
  fail visibly (MISSING) on drift, per the mod's invariant.
- **Effort:** L. External DLL + iterator rewrite + a fidelity gate.
- **Risk:** medium. Buffer lifetime across grid destroy/resize; a stale reused
  buffer would corrupt walkability. Guard: only reuse when the grid identity and
  length match; drop to vanilla `newarr` otherwise.
- **Impact:** removes the #1 large-alloc **and** #1 churn site - the largest single
  gross-MB/s cut. **Multiplies with P1/P2:** P1 cuts scan *cadence*, P2 cuts
  per-visit *probability*, P4 cuts the alloc *per scan*. All three compound.
- **EAC:** code mod -> EAC-off. No wire/save change.

---

## 3. Lever B - Network serialize-once (L1). Attacks #3.

### Source (RE, `network.md` §4b)
`NetEntityDistributionEntry.updatePlayerEntity` builds and
`ConnectionManager.SendPackage`s a set of **player-independent** packages
(`EntityAliveFlags`, `PlayerStats`, `PlayerTwitchStats`, `PlayerEquipment`,
`EntitySpeeds` - all `Setup(EntityAlive)`, identical content for every viewer)
**once per receiving player**. At N players each viewing each other, the same
bytes are re-serialized ~N times -> `PooledBinaryWriter.Write` churn scaling
O(players x entities).

### RE CORRECTION (2026-07-20): the package build is already serialize-once
`updatePlayerList` (IL 509) already **builds each package once and broadcasts via
`SendToPlayers`**, and the player-independent ones (`PlayerStats`, `PlayerEquipment`,
`EntityAliveFlags`, `PlayerTwitchStats`) are **change-gated** (`bPlayerStatsChanged`
etc., sent only on change). So the "hoist out of a per-player loop" fix is a
NON-ISSUE - the game already does it. My earlier RE (network.md §4b) was wrong.

The real residual: `NetConnectionSimple.taskSerialize` (the per-connection **writer
thread**, double-buffered `writerListFilling`/`Processing`) serializes each queued
package **independently per connection**, so one broadcast package is serialized N
times into N byte streams. A true serialize-once would encode the package to bytes
once and memcpy per connection. BUT this runs **off the main sim thread** (so it does
NOT cost `ms_per_tick`), feeds only the **#4** allocator (`PooledBinaryWriter.Write`),
and needs a thread-safe shared buffer across N writer threads. **Poor risk/reward -
deprioritized.** The genuinely worthwhile network lever remains the send-path scan
(shipped, `FastSendPatch`) and the O(N^2) interest spatial grid, not serialize-once.

### Surface, effort, risk
- **Surface:** Harmony on `NetEntityDistributionEntry.updatePlayerEntity`.
- **Pool lifecycle (the crux):** packages are pooled via
  `NetPackageManager.GetPackage<T>`. Serialize-once means one pooled package is
  sent to many connections; it must be returned to the pool exactly once, after
  all sends complete - not per-send (that double-frees). Verify the send path
  copies bytes into each connection's buffer (so the package can be freed after
  the broadcast) before changing ownership.
- **Effort:** M-L. **Risk:** medium (pool double-free, desync if a per-viewer
  field leaks into the shared package). **Impact:** cuts serialization CPU **and**
  alloc at player-scale; the confirmed dominant lever above ~64 players.
- **EAC:** server-side only, wire-compatible (same packages, fewer serializations)
  -> vanilla client connects; code -> EAC-off.

---

## 4. Lever C - Situational allocators (lower priority)

- **`TerrainSubMesh.Add`:** dynamic-mesh sub-mesh buffers. On a dedicated server
  mesh matters only for collision; the existing `DynamicMesh` budget already caps
  it. Pool the sub-mesh lists if it survives after A/B. Do only if it stays top-3
  once A lands.
- **`ItemStack.Clone`:** defensive copies in inventory/loot paths. Correctness-
  sensitive (aliasing bugs if a clone is elided wrongly); audit for provably-
  unnecessary clones only. Low priority, high review cost.
- **`ChunkBlockChannel.Write`:** chunk serialization on send - overlaps Lever B's
  pooled-writer work; fold into B rather than a separate lever.

---

## 5. Cross-cutting: pooling utility + measurement

- **Pooling primitive:** prefer `System.Buffers.ArrayPool<T>.Shared` (confirmed
  available in the game BCL); otherwise a tiny fixed free-list. One helper, reused
  by A and B, so lifetime rules live in one place. The reuse-technique catalog
  (presize-and-retain, ArrayPool, Clear-and-reuse, Span/stackalloc, thread-local
  scratch) and the Boehm "trade RAM for fewer collections" knobs are in
  [`allocation-reuse.md`](allocation-reuse.md).
  Key finding there: the game already pools the writer/stream *objects*, so the
  remaining churn is the expandable buffer **reallocating** on growth - the fix is
  presize + retain at max capacity (free with 128 GB RAM), not new pooling.
- **Measurement (mandatory per lever):** corrected APM alloc attribution
  (`top_alloc_sites` / `top_churn_sites`, ranked by bytes) + `gross MB/s` +
  `ms_per_tick`, before/after, matched load. The **no-GC diagnostic window**
  (`GC_disable` for ~90 s under load, measure `ms_per_tick`) gives the **GC-free
  tick floor**: it is the ceiling on how much any allocation cut can improve
  tick-time. If the GC-free floor is already near the observed tick time, the
  bottleneck is not GC/alloc and the win is RAM/pause-smoothness, not TPS - state
  that honestly rather than overselling an alloc cut as a TPS win (the P2 lesson).

---

## 6. Sequencing

1. **B (L1 serialize-once)** first: biggest at player-scale, target fully RE'd, no
   external DLL, and it builds the pooled-broadcast harness Lever C reuses.
2. **A (P4 node pooling)** second: biggest single allocator, concurrency now
   proven safe, but external-DLL iterator rewrite - do it after B proves the
   pooling + measurement harness.
3. **C** situational, only if it survives A/B in the top allocators.

Each ships behind a config flag, default vanilla, fidelity-gated (B: no desync
across a heavy mixed load; A: AI still paths across fresh chunk edges + fast
movement, no nav corruption), and validated with the corrected APM attribution.
