# Dedicated server bottleneck catalog (V3.0.1)

**Owns:** the consolidated, ranked catalog of tick bottlenecks - super-linear
scaling, inefficient data structures, and serial single-thread stages.
**Method:** IL RE + live APM scaling (`apm scaling`), 42 findings verified against
IL and measured exponents (adversarial audit, 2026-07-19). **Hub:** [`INDEX.md`](INDEX.md).
**Companion docs:** optimizer-facing summary [`PERF_RESEARCH_BRIEF.md`](PERF_RESEARCH_BRIEF.md); scaling laws [`measured-scaling.md`](measured-scaling.md); unsafe levers [`aggressive-optimizations.md`](aggressive-optimizations.md); algorithm cost anatomy [`algorithms.md`](algorithms.md); allocation
[`allocation-reuse.md`](allocation-reuse.md) + [`../../7dtd-optimizer/docs/ALLOCATION_UPSTREAM.md`](ALLOCATION_UPSTREAM.md);
graded levers [`../../7dtd-optimizer/docs/OPTIMIZATION_CANDIDATES.md`](OPTIMIZATION_CANDIDATES.md).

**Confidence:** **CONFIRMED** = IL + measured scaling both check out. **PLAUSIBLE** =
IL/code pattern confirmed but impact attribution weak or already mitigated.

Two scaling regimes must not be conflated: the **player axis** (death-spiral wall
at ~450-500 players, near-zero zombies) holds the near-quadratic walls; the
**entity axis** (many zombies, ~16 players) is algorithmically well-behaved
(near-linear, large constants). The single structural theme across nearly every
high finding: **a missing spatial index, or a serial main-thread stage.**

---

## 1. Worst offenders (set the tick ceiling)

1. **`NetEntityDistribution.OnUpdateEntities`** (IL=322) - CONFIRMED, **O(N^2.26)/call
   by players**. All-pairs interest management with no spatial partition: an
   `enemy x players` view-cone loop (distSq + `Vector3.Angle`), a `players^2`
   distance loop, and a `players x tracked-entities` pass (`updatePlayerList` /
   `updatePlayerEntity`). One of the two near-quadratic sections behind the
   450-500 player cliff.
2. **`ConnectionManager.Update`** (IL=215) - CONFIRMED, **O(N^2.27)/call by
   players**, sub-linear by entities. Serial single-thread pump: `ProcessPackages
   x2 channels x clients` + `FlushClientSendQueues` + a periodic O(N) ClientInfo
   broadcast. The second near-quadratic wall; cannot currently parallelize
   (connection-state safety).
3. **Chunk send pipeline** - `ChunkManager.SendChunksToClients` (IL=216, **O(N^1.64)**)
   + `DetermineChunksToLoad` (IL=448, **O(N^1.77)**), both CONFIRMED. **~56-60% of
   instrumented tick in every loaded scenario.** The heavy `Chunk.write` encode (IL=601)
   runs **synchronously on the sim thread** inside `NetPackageChunk.Setup`; only the
   byte-copy is off-thread.
4. **`AstarManager.UpdateGraphs`** (IL=185) - CONFIRMED, total **O(N^1.43) by
   players** (the only compute section super-linear in *total*). Top managed CPU
   section: **66.6 ms/tick** at 64p+340z, over the 50 ms budget on its own. P1
   throttle shipped (-28.5% ms/tick).
5. **`AstarVoxelGrid.InitScan`** (IL=3 -> `NavGraph.Scan`) - CONFIRMED, **#1
   large-alloc AND #1 steady-churn allocator**. `newarr` of a fixed-length node
   array on every grid move. The single largest feeder of the Boehm megapause.
6. **Boehm `GC_gcollect`** (whole-heap STW) - CONFIRMED, downstream symptom.
   Non-compacting conservative mark freezes the 20 TPS loop; measured **479 ms
   megapause** on a ~5.6 GB live heap growing ~10 MB/s. Only cutting allocation
   helps (cadence tuning is a wash: 15.16 / 14.84 / 14.52 MB/s across forced /
   guard / incremental).

Split: #1-#2 are the player-axis quadratic walls; #3-#5 are the dominant per-tick
CPU + allocation floor at all loads; #6 turns allocation into visible hitches.

---

## 2. Inefficient data structures / serial stages (cross-cutting)

**Linear-scan-where-an-index-belongs** (the structural root of the super-linear walls):

- **`ConnectionManager.SendPackage` (IL=100) linear `Clients.List` scan by `entityId`** -
  a single-target send scans the whole client list instead of using the existing
  `ClientInfoCollection.entityIdMap` / `ForEntityId` O(1) lookup. `SendToPlayers`
  calls it once per tracked player; `updatePlayerList` calls `SendToPlayers` **7x
  per entity per tick**. Turns fan-out into **O(entities x players x clients)**.
  **Highest-leverage pure data-structure fix.** CONFIRMED.
- **`World.GetClosestPlayer` (IL=63) linear `Players.list` scan** - no spatial
  index; called per-entity from `EntityActivityUpdate` and `CheckDespawn`, making
  LOD/despawn **O(entities x players)**. The shared primitive the AI-LOD cost rides
  on. CONFIRMED.
- **`NetEntityDistribution.OnUpdateEntities` all-pairs loops** - `players^2`,
  `enemy x players`, `players x tracked-entities`, none bucketed. The #1 offender is
  fundamentally a missing-spatial-index problem.
- **`Chunk.GetEntitiesInBounds` (IL=85) per-slab linear `List<Entity>` scan** into a
  single shared reused (non-reentrant) buffer - chunk-bucketed but no sub-chunk
  index; called per-entity by target-find / turrets / traps / push / spawn. CONFIRMED.
- **Per-tick full-copy rebuilds**: `World.TickEntities` (IL=117) clears + rebuilds
  `tickEntityList` every tick; `DetermineChunksToLoad` clears + rebuilds
  `chunksAround` / `m_AllChunkPositions` / `m_ViewingChunkPositions` from scratch
  instead of an incremental diff.
- Minor: `AstarManager.FindClosestGraph` (IL=67) / `FindMoveIndex` (IL=22) linear
  `graphList` / `moveList` scans (lists stay in the tens; real cost is downstream
  InitScan).

**Serial single-thread stages** (parallelization rejected by design - shared state):

- `ConnectionManager.Update` - connection-state races (parallel `SendPackage` rejected).
- `TickEntities` / `TickEntitiesSlice` / `Flush` (IL=117/37) - shared authority world
  state; stock spreads temporally via the EMA slice, not across cores. CONFIRMED.
- `EAITaskList.OnUpdateTasks` (IL=137) - shared `executingTasks` / `isBestTask`
  priority interlock; IceCoffee's `Parallel.ForEach` attempt was abandoned. CONFIRMED.
- `ASPPathFinderThread.<FindPaths>.MoveNext` (IL=87) - the "worker" is a **main-thread
  Unity coroutine draining a hardcoded 8 path-starts/frame**. Throughput fixed
  regardless of backlog. CONFIRMED.
- `Chunk.write` encode inside `NetPackageChunk.Setup` - sim thread (only the byte-copy
  is off-thread).
- `DetermineChunksToLoad` Phase 2 holds `lockObject` (contended by chunk worker/copy
  threads) through the whole from-scratch union rebuild.
- `WorldState.SaveLoad` (IL=884) - 59 `ReadWrite` fields + 5 volume blobs under one
  `Monitor` span, blocking the tick per save (p95 ~8 ms). CONFIRMED.

---

## 3. Full catalog by subsystem

Ranked within each by severity, then scaling badness (super-linear worst).

### Network (replication + send + connection pump + serialization)

| Symbol (IL / exponent) | Kind · sev | Scales with | Mechanism | Lever | Verdict |
|---|---|---|---|---|---|
| `NetEntityDistribution.OnUpdateEntities` (322; O(N^2.26)/call players) | superlinear · high | players x entities; players^2 | all-pairs distSq + `Vector3.Angle` view-cone, no spatial partition | spatial interest grid (exp ~2.26 -> ~1); network LOD stopgap | CONFIRMED |
| `ConnectionManager.Update` (215; O(N^2.27)/call players) | serial · high | players | serial Clients loop: `ProcessPackages x2` + flush + periodic O(N) broadcast | round-robin connection budget; dict-ize the broadcast scan | CONFIRMED |
| `SendToPlayers` -> `ConnectionManager.SendPackage` (42->100) | data-structure · high | entities x players x clients | single-target send linear-scans whole `Clients` list by entityId | use existing `entityIdMap`/`ForEntityId` O(1); or enqueue direct | CONFIRMED |
| `PooledBinaryWriter.Write` (per-conn `taskSerialize`; ~15 MB/s@128p) | allocation · high | players x entities | same `NetPackage` re-serialized into each connection; player-independent packages produce identical bytes N times | serialize-once: encode once/tick, memcpy per connection; keep RelPosAndRot per-player | CONFIRMED |
| `updatePlayerEntity` pair loop (222) | linear-heavy · medium | players x entities | `playerList x trackedEntitySet` each tick: distSq + `HashSet.Contains`, early return in steady state | same grid; skip distSq when neither moved past cached cell | CONFIRMED |
| `ChunkBlockChannel.Write` (120; via NetPackageChunk) | allocation · medium | chunks x players | 64-layer RLE through the pooled writer; per-chunk-per-client on stream | cache serialized chunk bytes per chunk-version, reuse across observers | CONFIRMED (share over-attributed; #4-5 churn not dominant) |

### Chunk / mesh streaming

| Symbol (IL / exponent) | Kind · sev | Scales with | Mechanism | Lever | Verdict |
|---|---|---|---|---|---|
| `SendChunksToClients`->`NetPackageChunk.Setup`->`Chunk.write` (216/31/601; O(N^1.64)) | serial · high | players x per-observer chunk churn | full 601-IL `Chunk.write` (64-layer, 5x channel, heightmap/biome/TE) runs synchronously on sim thread | move encode off-sim into NCS writer; cache/reuse serialized blobs | CONFIRMED |
| `DetermineChunksToLoad` (448; O(N^1.77)) | lock-contention · medium | players x view-dist^2 | clears+rebuilds chunk sets from scratch under `lockObject` while chunk workers contend | incremental diff; shard/shrink the lock | CONFIRMED |
| `TerrainSubMesh.Add` (61; #2 large/#3 churn) | allocation · medium | chunks meshed x texture-ids | linear `others` scan + dynamic-array growth alloc; terrain/collision mesh still built on dedicated | preallocate `ArrayDynamicFast`; confirm server needs submesh vs only collision | CONFIRMED |
| `WorldState.SaveLoad` (884) | serial · low | live-heap size (periodic) | 59 `ReadWrite` + 5 blobs under one `Monitor`; blocks tick per save (~8 ms p95) | snapshot fields under lock, release before stream I/O | CONFIRMED |
| `LightProcessor.RefreshSunlightAtLocalPos` (107) | linear-heavy · low | block edits x columns | full 256-iteration Y-column walk per relit column (never breaks early) | start walk at chunk max terrain height, not y=255 | PLAUSIBLE (impact is the chunk-resend it triggers) |

### Pathfinding / nav

| Symbol (IL / exponent) | Kind · sev | Scales with | Mechanism | Lever | Verdict |
|---|---|---|---|---|---|
| `AstarManager.UpdateGraphs` (185; total O(N^1.43) players) | superlinear · high | players x nav-graphs | per-player `Merge`, `mergedLocations x graphList` scan, grid moves -> scans | P1 throttle (shipped, -28.5%); P5 merge clustering | CONFIRMED |
| `AstarVoxelGrid.InitScan` (3 -> Scan; #1 large & churn) | allocation · high | players (grid-move freq) | `newarr` node array every scan though grid dims fixed; per-cell raycasts | P4/Lever A reuse-in-place node buffer (scan exclusion via AstarPath work-item lock) | CONFIRMED |
| `ASPPathFinderThread FindPaths.MoveNext` (87; drain 8/frame) | serial · medium | fixed 8/frame vs queue depth | main-thread coroutine draining 8 `GetPathTo`/frame; throughput fixed | admission + priority/coalesce on enqueue; do NOT add unbounded workers | CONFIRMED |
| `EntityAlive.FindPath`->`PathFinderThread.FindPath` (49/17) | data-structure · low | requesting entities | no admission gate (only Y-clamp); always enqueue + `new PathInfoSingleTarget`; per-id dict coalesces | admission (cap/priority/drop-far) at enqueue | CONFIRMED (low tick-budget; cost is alloc + refresh latency) |
| `ASPPathNavigate.CreatePath`->`new ASPPathFinder` (38) | allocation · low | path builds/tick | `newobj ASPPathFinder` per build before `Calculate` | reuse per-navigator `ASPPathFinder` | CONFIRMED (small Gen0, dwarfed by InitScan) |
| `UpdateGraphPos`/`FindClosestGraph`/`FindMoveIndex` (60/67/22) | data-structure · low | players x graphs | linear `graphList`/`moveList` scans; rescan gated on distSq dead-zone 100 | P2 dead-zone (shipped, [100,10000]) | PLAUSIBLE (per-call O(N^0.27); real driver is downstream InitScan) |

### Entity AI / tick

| Symbol (IL / exponent) | Kind · sev | Scales with | Mechanism | Lever | Verdict |
|---|---|---|---|---|---|
| `EntityMoveHelper.UpdateMoveHelper` (1236; entity axis O(N^1.1)) | linear-heavy · high | ticked entities/frame | largest per-entity AI method (locomotion + stuck + 4x jump + dig + 2x attack + trig + 9x rand); runs unconditionally after the LOD gate - stock scale never throttles it | far full-skip of `updateTasks` (EfficientServer); measure under blood moon | CONFIRMED (large-constant *linear*, not the wall) |
| `TickEntities`/`Slice`/`Flush` (117/37; O(N^1.13)/call) | serial · medium | entities | per-entity chain mutates shared world state -> single-thread; ~0.08 ms/entity -> ~80 ms/tick @1000 | tick-striding (id%N==frame%N far tiers), not threads | CONFIRMED (extrapolated; at ~100 the wall is GC) |
| `EntityAlive.updateTasks` (125) | linear-heavy · medium | entities/frame | LOD gate throttles only the EAI decision; GetPath+SetPath+UpdateNav+UpdateMoveHelper+onUpdateLook run every invocation | far-skip the whole tail (UpdateTasksLodPatch) | CONFIRMED |
| `EAITaskList.OnUpdateTasks` (137) | serial · medium | entities x tasks, gated by aiActiveScale | serial priority list walked 2x/pulse; shared interlock + fixed 0.05 countdown (assumes 20 Hz) | reduce WHO runs EAI (tighter scale), not parallelize | CONFIRMED |

### World-query / spatial

| Symbol (IL / exponent) | Kind · sev | Scales with | Mechanism | Lever | Verdict |
|---|---|---|---|---|---|
| `World.EntityActivityUpdate` (229; O(N^1.07) entities) | data-structure · medium | entities x players | per-player `aiClosest` rebuild + per-entity `GetClosestPlayer` linear scan + per-player sort; E x P, from scratch each tick | closest-player cache w/ TTL + spatial hash by chunk cell | CONFIRMED |
| `Chunk.GetEntitiesInBounds` (85; entity axis O(N^1.1)) | data-structure · medium | entities x density | chunk-bucketed but per-slab linear `List<Entity>` scan into one shared non-reentrant buffer | coarser target-scan cadence / reuse target across ticks | CONFIRMED |
| `World.TickEntities` (117) | data-structure · low | entities (full copy/tick) | clears+re-adds every entity into `tickEntityList` each tick | maintain `tickEntityList` incrementally on add/remove | CONFIRMED (benign ref copy) |
| `World.GetClosestPlayer` (63,+57/7) | data-structure · low | players x callers | linear `Players.list` scan, no acceleration; shared primitive under many callers | shared spatial hash keyed on chunk cell (feeds A4 + network grid) | CONFIRMED/PLAUSIBLE (bounded player count) |
| `SpawnManagerBiomes.SpawnUpdate` (441) | linear-heavy · low | spawn-area chunks x players | per area-master chunk fetches players, builds exclusion boxes; scout path allocs per spawn | scope area-master walk to player-near chunks | PLAUSIBLE (early-exit; negligible in APM) |

### GC / allocation (cross-cutting, downstream)

| Symbol | Kind · sev | Scales with | Mechanism | Lever | Verdict |
|---|---|---|---|---|---|
| Boehm `GC_gcollect` (whole-heap STW) | serial · medium | live-heap size | non-compacting conservative mark, one thread, tick frozen; ~5.6 GB +~10 MB/s -> 479 ms megapause | cut allocation upstream (cadence is a wash; forced collect already mitigated by GcGuardPatch) | CONFIRMED |
| `ItemStack.Clone` (15; #2 churn) | allocation · medium | inventory/loot/container ops | `newobj ItemStack` + ItemValue clone (re-news backing array) = >=2 objects | Lever C: elide only provably-unnecessary defensive clones | CONFIRMED |

Dominant allocators feeding the megapause, ranked (corrected `GC_malloc` uprobe):
**#1 `AstarVoxelGrid.InitScan`**, **#2 `ItemStack.Clone`**, **#3 `TerrainSubMesh.Add`**,
**#4 `PooledBinaryWriter.Write` -> `ChunkBlockChannel.Write`**. Allocation is the true
steady floor; every allocator is one lever against the same 479 ms STW pause.

**Note (2026-07-20):** the 479 ms figure is a *forced* full collect on a GC-disabled
6.9 GB heap (the `GcMegapauseTest` probe). At runtime the launch env
`GC_FREE_SPACE_DIVISOR=1` gives enough headroom that *natural* full collections drop to
**0 in a 150 s window** (vanilla did 3 + a 274 ms freeze at the same load) - see the
aggregate A/B in [`../../7dtd-optimizer/docs/RESULTS.md`](RESULTS.md)
§3. So the env lever eliminates the *natural* megapause; cutting the allocators at
source (P4 etc.) further shrinks the heap the *forced* worst case would have to mark.

---

## 4. Honest PLAUSIBLE caveats

- `UpdateGraphPos` / `FindClosestGraph`: mechanism misattributed - real cost is
  downstream `InitScan`; the lever (P2) already shipped.
- `ChunkBlockChannel.Write` gc-alloc share was over-stated; it is #4-5 churn, not
  dominant.
- `LightProcessor.RefreshSunlightAtLocalPos`: code real, impact belongs to the
  chunk-resend it triggers.
- `SpawnManagerBiomes.SpawnUpdate`: early-exit loop, negligible in measurement.
- `GetClosestPlayer`: real pattern, but player count is bounded so the constant is
  small (not dominant or super-linear).

---

## 4b. Campaign-final measured state (2026-07-21)

The optimization campaign against this catalog concluded with the tick FULLY
attributed (parent-minus-children residual 0.4% at the blood-moon ceiling - see
`7dtd-optimizer/docs/RESULTS.md` §3h): **TickEntities 63%** (serial main-thread,
frame-amortized, close-combat-bound - no worker pool exists to widen),
**OnUpdateEntities 30%** (20 Hz-locked replication; stride lever = -45/-61/-70% at
2/3/4, governor-managed), **chunk send 5%**, all else < 2%. PhysX ~0%. Frame rate
is not the tick rate (loop.md §3). Sustained blood-moon capacity at 64 players:
~147 static / **~232 with the adaptive governor**. Remaining costs are engine
walls, ops config, or the custom-server long game.

## 5. The one structural conclusion

Nearly every high-severity finding is **a missing spatial index or a serial
main-thread stage**. Two moves collapse most of the board:

1. **Spatial bucketing on the player/entity axes** - a shared uniform grid keyed on
   chunk cell, reused by AI `GetClosestPlayer` (A4) and `SendPackage`'s `entityId`
   map (shipped).

> **CORRECTION (deep RE 2026-07-20):** a spatial grid does **not** collapse
> `NetEntityDistribution.OnUpdateEntities`. The interest is **already distance-gated**
> (`updatePlayerEntity`: distSq vs `trackingDistanceThreshold` -> add/remove from
> `trackedPlayers`), and that evaluation is cheap (0.5% main-thread). The O(N^2.26)
> is **inherent replication** - `updatePlayerList` -> `SendToPlayers` sends each
> entity to each genuinely-interested (nearby) player - which only blows up when
> players **cluster** (all within threshold; the loadgen worst case). A grid cannot
> cull that (everyone is genuinely nearby), and a conservative cull cannot safely
> skip far *tracked* players (they must be re-evaluated for removal, else stale
> interest = desync). **Network LOD also fails** for the same reason the AI stride
> failed: throttling *far* entity->player updates saves little (far entities have
> few interested players); the cost is *close* entities with *many* interested
> players, which are fidelity-bound (can't send late without visible lag). The enemy
> x players `Vector3.Angle` prioritization loop is already distSq-gated (Angle only
> for <128 m), so it is not the cost either. **The only real lever for the O(N^2)
> replication wall is reducing view distance** (vanilla `ServerMaxAllowedViewDistance`
> = fewer interested players per entity), a config knob with a gameplay tradeoff, not
> a mod. Conclusion: there is no safe EfficientServer lever for this wall.
2. **Off-thread / lazy encode + buffer reuse** - chunk serialize-once + off-sim
   `Chunk.write`, `InitScan` node pooling, serialize-once replication. Attacks both
   the serial CPU stages (#2, #3) and the allocation floor driving the GC megapause
   (#5, #6).

---

## 5b. CPU-sampled hot paths (perf, aggregate all-threads, 2026-07-20)

Auto-discovered from the symbolized perf folded stacks (`cpu_hot_paths` in
`summary.json`), NOT from curated bridge hooks - so it surfaces methods no one
instrumented. **Caveat:** perf samples **all threads across all cores**, so these
are aggregate CPU %, not main-thread-per-tick (the single 20 TPS sim thread is a
small slice: `GameManager.Update` reads ~0.6%). Use §1-3 (bridge sections) for the
tick bottleneck; use this for total-CPU hot spots. Standard heavy load, 32 bots +
~290 zombies.

**Top game-code by self CPU (leaf attributed to first game frame):**

| % | Frame | Note |
|--:|---|---|
| 2.2 | `ThreadManager.myThreadInvoke` | thread-pool dispatch (workers/writers) |
| 1.9 | `NetConnectionSimple.taskSerialize` | per-connection writer-thread serialization |
| 1.3 | `StreamUtils.StreamCopy` | serialization byte copy |
| 0.6 | `GameManager.Update` | the main 20 TPS tick (small % of aggregate CPU) |
| 0.6 | `NetConnectionSimple.StreamToBuffer` | serialization |
| 0.4 | `ChunkBlockLayer.GetAt` | chunk block access |
| 0.4 | `PooledBinaryWriter.Write` | packet encode |
| 0.4 | `NetConnectionStatistics.RegisterSentPackage` | per-send bookkeeping |
| 0.3 | `Lighting3DArray.GetLight` / `get_Item` | light lookups |
| 0.2 | `NetEntityDistributionEntry.SendToPlayers` | fan-out |

**Broad inclusive (native kept, % of all samples):** `[jit]` 43.6 (JIT'd managed),
`[UnityPlayer.so]` 22.8, `[libc]` 22.0, `[libmonobdwgc]` 20.1 (**GC**),
`RuntimeHelpers.InitializeArray` **5.5** (array zeroing = allocation as CPU),
`GC_dirty_inner` **5.4** (GC write-barrier / dirty tracking), `__pthread_cond_wait`
4.8 (threads **parked**, idle not work), `taskSerialize` 4.2.

**Takeaways:** (1) the dominant *game-code* CPU across all threads is **network
serialization on the writer threads** (`taskSerialize` + `StreamCopy` + `StreamToBuffer`
+ `WriteToStream` + `PooledBinaryWriter.Write` + `SendToPlayers` ~= 4%+ combined) -
confirms per-connection re-serialization is a real (off-main) cost. (2) **GC + array
allocation together are ~30% of aggregate CPU** (`monobdwgc` + `GC_dirty_inner` +
`InitializeArray`) - the allocation floor showing as CPU, reinforcing the upstream
lever. (3) The main tick is single-thread-bound, invisible in aggregate CPU - see
the main-thread view below.

### Main-thread (sim thread, tid==pid) hot paths - FOR THE 20 TPS TICK

`cpu_hot_paths.main_thread` (built 2026-07-20): `stacks.main.folded` = perf samples
filtered to the sim thread, ranked by first-game-frame self. This is the hot game
code that **actually gates `ms_per_tick`** (the all-thread views above bury it).

| % of main-thread samples | Frame | Note |
|--:|---|---|
| 4.6 | `GameManager.Update` | the tick dispatcher |
| 1.5 | `NetEntityDistributionEntry.SendToPlayers` | replication fan-out (main-thread part) |
| 1.3 | `EntityHuman.OnUpdateLive` | per-entity update |
| 1.0 | `EntityAlive.Update` | per-entity update |
| 0.6 | `EntitySeeCache.CanSee` | **AI vision line-of-sight (not bridge-hooked)** |
| 0.5 | `EAITaskList.OnUpdateTasks` | EAI dispatch |
| 0.5 | `ChunkBlockLayer.GetAt` | chunk block access |
| 0.4 | `KinematicCharacterMotor.UpdatePhase1` / `CC.Move` | **character-controller physics (not hooked)** |
| 0.4 | `AstarVoxelGrid.CalcBlockingFlags` / `CalculateConnections` | **the nav-scan raycast internals** |
| 0.3 | `World.GetClosestPlayer` | the linear player scan (§2) |

**New hot paths this surfaced** (none in the curated bridge sections): AI vision
(`EntitySeeCache.CanSee`), character physics (`KinematicCharacterMotor`), the
pathfinding-scan cell work (`CalcBlockingFlags`/`CalculateConnections` - the CPU half
of the `InitScan` cost), and `GetClosestPlayer`. Confirms the tick is spread across
replication + per-entity AI (update/vision/EAI) + character physics + nav scan, with
no single dominant frame (`GameManager.Update` self is only 4.6% - the cost is fanned
across many per-entity callees, consistent with the linear entity-axis).

## 6. Biggest impact per line of code (bang-for-buck)

Ranked by perf-win / code-size. "Shipped" = already in EfficientServer.

**Tier 1 - tiny code, big or proven impact (do these):**

1. **`SendPackage` entityId-map lookup** - *the standout unshipped win.* Replace the
   linear `Clients.List` scan in `ConnectionManager.SendPackage` with the **already-
   existing** `ClientInfoCollection.entityIdMap` / `ForEntityId` O(1) lookup. A few
   lines (Harmony), no new data structure, and it removes an O(clients) inner scan
   from the hottest send path (`SendToPlayers` calls it 7x per entity per tick).
   Turns replication fan-out from O(entities x players x clients) to O(entities x
   players). Highest impact-to-effort of anything not yet done.
2. **P1 `UpdateGraphs` throttle** - 1 Harmony prefix, **-28.5% ms/tick** at breaking
   load. SHIPPED.
3. **AI `updateTasks` far-skip** - 1 prefix, drops the 1236-IL `UpdateMoveHelper`
   for far dormant entities. SHIPPED (`UpdateTasksLodPatch`).
4. **GC forced-collect guard** - 1 transpiler, removes the self-inflicted 120 s STW.
   SHIPPED (`GcGuardPatch`).

**Tier 2 - small code, good impact:**

5. **~~`PooledExpandableMemoryStream` presize + retain~~ - DOWNGRADED.** RE
   correction: `Reset()` is `SetLength(0)`, which already retains the buffer, so the
   pooled stream does not realloc in steady state. The serialization churn is a
   *count* problem (per-player re-serialization -> **serialize-once**, the deeper
   L1 lever), not a buffer-retention problem. See [`allocation-reuse.md`](allocation-reuse.md).
6. **Path admission cap + `ASPPathFinder` reuse** - a small gate at
   `EntityAlive.FindPath` enqueue (cap/priority/drop-far) + reuse the per-navigator
   `ASPPathFinder` instead of `newobj` per build. Bounds path-request spikes + alloc.
7. **P2 move dead-zone** - 1 transpiler operand; cuts `InitScan` frequency. SHIPPED
   (marginal except under graph-dominated load).

**Tier 3 - moderate code, big impact:**

8. **`InitScan` node-buffer reuse (P4)** - kills the **#1 allocator**; concurrency
   already safe (AstarPath work-item lock). External-DLL iterator rewrite = the code
   cost. See [`ALLOCATION_UPSTREAM.md`](ALLOCATION_UPSTREAM.md) Lever A.
9. **Off-sim `Chunk.write` encode / cached chunk blobs** - the chunk pipeline is
   **56-60% of tick**; moving the 601-IL encode off the sim thread (or caching
   serialized blobs per chunk-version across observers) is the single biggest CPU
   reclaim, but threading it safely is real work.

**Tier 4 - biggest impact, most code:**

10. **Shared spatial interest grid** - a uniform grid keyed on chunk cell, reused by
    `NetEntityDistribution` interest (collapses the **O(N^2.26)** wall), AI
    `GetClosestPlayer` (A4), and the `SendPackage` map (#1). Collapses both
    quadratic walls toward linear - the highest *absolute* ceiling raise, but a new
    subsystem to build and validate.

**The one-line answer:** the best impact-per-line not yet shipped is **#1, the
`SendPackage` entityId-map lookup** - a few lines against infrastructure that
already exists, on the hottest path in the game. After that, **#5 buffer
presize-and-retain** for allocation, then **#8/#9** for the structural CPU floor.
