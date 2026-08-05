# Dedicated server algorithms & data structures (V3.0.1)

**Owns:** the algorithm + data-structure used by each hot subsystem - what runs, in
what complexity, on what structure. **Hub:** [`INDEX.md`](../../7dtd-research/docs/INDEX.md). Deep dives:
loop [`loop.md`](../../7dtd-research/docs/loop.md); AI/path [`entity-ai.md`](../../7dtd-research/docs/entity-ai.md); net
[`network.md`](../../7dtd-research/docs/network.md); world [`world-chunks.md`](../../7dtd-research/docs/world-chunks.md); scaling
[`measured-scaling.md`](measured-scaling.md); bottleneck ranking
[`bottlenecks.md`](bottlenecks.md); allocation [`allocation-reuse.md`](allocation-reuse.md).

Runtime: Unity Mono, Boehm GC, **single-threaded 20 TPS main loop** (50 ms budget).

---

## 1. Frame / tick loop

- **`GameManager.gmUpdate`** - the per-frame driver: phased sequence (input, world
  tick, managers, net, GC countdown). Runs every Unity frame; the sim tick is gated
  to 20 Hz by `GameTimer`.
- **`World.TickEntities` / `TickEntitiesSlice` / `Flush`** - the entity tick. Uses a
  **temporal slice**: not every entity every tick; an EMA-driven slice spreads
  per-entity work across frames (a *time* spread, not a *core* spread - it stays
  single-threaded). `tickEntityList` is **cleared and rebuilt from `Entities.list`
  every tick** (full O(N) copy).
- **Forced GC:** `gmUpdate` calls `GC.Collect()` every ~120 s (`gcCountdownTimer`) -
  a self-inflicted full STW; EfficientServer's guard reroutes it.

## 2. Pathfinding (Aron Granberg A* Project, external DLL)

Two distinct systems, often conflated:

- **Graph maintenance - `AstarManager.UpdateGraphs(float)`, every tick.**
  Keeps player-following voxel nav-graphs (`AstarVoxelGrid` : `LayerGridGraph`). Per
  tick: `mergedLocations.Clear()`, then `Merge(player.xz, 76)` per player (coalesce
  observer regions); for each grid x merged location, `UpdateGraphPos` computes the
  grid-move `SqrMagnitude` and, if past a **dead-zone constant 100** (sq units),
  `Insert`s the grid into `moveList`; then `UpdateMoveGraph` drains **one** queued
  move per call (`RemoveAt(0)` + `MoveGraph`, gated on the prior move finishing).
  **Scaling: total O(N^1.43) in players.** Data structures: `graphList` (linear),
  `moveList` (linear FIFO-ish, drained 1/call).
- **Grid scan - `AstarVoxelGrid.InitScan` -> `NavGraph.Scan` ->
  `LayerGridGraph.ScanInternal` (iterator).** Rebuilds a grid's walkability when it
  moves: allocates `LevelGridNode[width x depth x layerCount]` (**large-object
  alloc, #1 large-alloc + #1 churn**), then per cell casts collision rays to set
  walkability and `CalculateConnections`. `LevelGridNode` is a **class**, so each
  scan news the array **and** N node objects. Grid dims are fixed per graph
  (`lastScannedWidth`/`Depth` tracked), so both are re-newed needlessly.
- **Path compute - `EntityAlive.FindPath` -> `PathFinderThread.FindPath`.** No
  admission gate (only a horizontal-distance Y-clamp); always `entityWaitQueue.Add`
  + `finishedPaths[id] = new PathInfoSingleTarget`. A **per-id dictionary coalesces**
  repeat requests. The "worker" `ASPPathFinderThread.<FindPaths>.MoveNext` is a
  **main-thread coroutine draining a hardcoded 8 path-starts/frame** (`AstarPath.
  StartPath`) - throughput is fixed regardless of backlog, so under a swarm each
  entity's path refreshes ~ceil(N/8) ticks late. Scan vs path-read exclusion is via
  `AstarPath.isScanning` + `PathProcessor.GraphUpdateLock workItemLock`.

## 3. Networking

- **Interest management - `NetEntityDistribution.OnUpdateEntities`, every tick.**
  Rebuilds enemy/player lists from `trackedEntitySet`, then **all-pairs, no spatial
  index**: an `enemy x players` loop (squared distance + `Vector3.Angle` view-cone
  vs `priorityViewAngleLimit`), a `players^2` distance loop, and a `players x
  tracked-entities` loop (`updatePlayerList` / `updatePlayerEntity`). Interest add/
  remove is a `HashSet<EntityPlayer>.Contains` per pair. **Scaling: O(N^2.26)/call in
  players** - a co-cause of the 450-500 player death-spiral. The missing structure is
  a spatial bucket (chunk-cell grid).
- **Replication packages** (`updatePlayerList`, per tracked pair): position state
  machine keyed on encoded-position deltas - `Teleport` if delta outside +-256,
  full `PosAndRot` if outside +-128 or age > 100 ticks, else `RelPosAndRot`;
  velocity if motion^2 > 0.04; plus player-independent `EntityAliveFlags`,
  `PlayerStats`, `PlayerTwitchStats`, `PlayerEquipment`, `EntitySpeeds`. These
  player-independent packages are **re-serialized per receiving player** (identical
  bytes N times) - the serialize-once (L1) target.
- **Serialization** - `PooledBinaryWriter` over `PooledExpandableMemoryStream`. Both
  are **pooled objects** (`MemoryPools`); the stream `Reset()` = `SetLength(0)`
  **retains** its backing `byte[]` (no steady realloc). Chunks: `ChunkBlockChannel.
  Write` runs a **64-layer RLE** over 1024-byte block runs.
- **Send fan-out - `ConnectionManager.SendPackage`.** Multi-mode filter (attached-to
  / all-but / in-range / not-attached). Vanilla **linear-scans the whole `Clients`
  list** by `entityId`; a `ClientInfoCollection.entityIdMap` (`ForEntityId`, O(1))
  exists but is unused on this path. `SendToPlayers` calls it per tracked player.
  Refcount: `NetPackage.RegisterSendQueue` = `Interlocked.Increment(inSendQueuesCount)`
  per enqueue; the package returns to its pool when all queues drain it.
  (EfficientServer `FastSendPatch` short-circuits the single-target case via the map.)
- **Connection pump - `ConnectionManager.Update`.** Serial per-tick loop over
  `Clients` calling `ProcessPackages` x2 channels + `FlushClientSendQueues`, plus a
  periodic O(N) ClientInfo broadcast. **O(N^2.27)/call in players.** Writer threads
  (`NCS_Writer`, `taskSerialize`) are per-connection long-lived tasks.

## 4. Entity AI

- **AI LOD** - `World.EntityActivityUpdate` sets `aiActiveScale` (1.0 / 0.3 / 0.1) by
  distance band; per full tick it rebuilds `aiClosest` per player and calls
  `GetClosestPlayer` (linear `Players.list` scan) per EntityAlive - **O(entities x
  players)**, no spatial index.
- **`EntityAlive.updateTasks`** - the AI gate; the LOD scale throttles only the EAI
  decision, then GetPath/SetPath/UpdateNavigation/`UpdateMoveHelper`/onUpdateLook run
  every invocation. `CheckDespawn()` is the **first** step (distance/lifetime).
- **`EAITaskList.OnUpdateTasks`** - a **serial priority list** walked twice per pulse
  with a shared `executingTasks`/`isBestTask` interlock and a fixed 0.05 s countdown
  (assumes 20 Hz). Not parallelizable (the interlock); IceCoffee's `Parallel.ForEach`
  attempt was abandoned.
- **`EntityMoveHelper.UpdateMoveHelper`** (1236 IL) - the largest per-entity method:
  locomotion + stuck-detect + jump/dig/attack + trig angle-lerp; runs unconditionally
  after the LOD gate. Linear-heavy, entity axis O(N^1.1).

## 5. World / chunks / mesh

- **Chunk selection - `ChunkManager.DetermineChunksToLoad`** (O(N^1.77)/call).
  **Clears + rebuilds** `chunksAround` per boundary crossing, then under `lockObject`
  clears + rebuilds `m_AllChunkPositions` / `m_ViewingChunkPositions` from scratch
  (no incremental diff) while chunk worker threads contend the lock.
- **Chunk send - `SendChunksToClients` -> `NetPackageChunk.Setup` -> `Chunk.write`**
  (601 IL, O(N^1.64)/call). The full encode (64-layer blocks, 5x channel, heightmap/
  biome, per-entity/TileEntity) runs **synchronously on the sim thread**; only the
  byte-copy is off-thread. **~56-60% of instrumented tick** in every loaded scenario.
- **Dynamic mesh - `DynamicMeshManager`/`DynamicMeshServer.Update`** + `TerrainSubMesh.
  Add` (per texture-id linear `ArrayDynamicFast` scan + growth alloc, #2 large-alloc).
- **Light - `LightProcessor.RefreshSunlightAtLocalPos`**: a full 256-iteration Y-column
  walk (y=255->0, never breaks early) per relit column on block edits.
- **Save - `WorldState.SaveLoad`** (884 IL): 59 scalar `ReadWrite` + 5 volume blobs
  under a single `Monitor` span - blocks the tick for the save (~8 ms p95).

## 6. Spatial queries (the missing index)

There is **no spatial acceleration structure** for entity/player proximity. Both
primitives are linear scans:
- `World.GetClosestPlayer` - linear `Players.list` scan (IsDead/Spawned/team +
  `GetDistanceSq`); shared by EntityActivityUpdate, CheckDespawn, vulture AI, AIDirector.
- `Chunk.GetEntitiesInBounds` - chunk-bucketed but per-slab linear `List<Entity>` scan
  with `Bounds.Intersects` into one shared reused non-reentrant buffer.
A single uniform grid keyed on chunk cell would serve all of these + network interest.

## 7. Garbage collection (Boehm `libmonobdwgc-2.0`)

- **Non-moving, non-compacting, conservative mark-sweep, stop-the-world.** Marks the
  entire reachable object graph on one thread with the tick frozen; sweep walks the
  whole heap. Cost scales with the **live-set size** (mark) + heap size (sweep).
- **Not generational by default** (opt-in incremental mode splits it into
  `collect_a_little` slices via a page-protection write barrier).
- **Measured:** live working heap ~5.6 GB under heavy load, growing ~10 MB/s gross;
  one forced full collect of a 6.91 GB heap = **479 ms STW** (megapause). Gross churn
  is invariant across GC configs -> **allocation is the only lever**.
- Retained buffers stay put (no pin, no compaction); large arrays (>4 KB: nav nodes,
  mesh, big packets) hit a separate large-object path. RAM-headroom knobs:
  `GC_set_free_space_divisor` (collect less often), `GC_expand_hp` (preallocate).
- **Measured CPU (perf, all-threads):** GC + allocation are ~30% of aggregate CPU -
  `libmonobdwgc` 20%, `GC_dirty_inner` 5.4% (the write-barrier / dirty-page tracking
  for conservative marking), `RuntimeHelpers.InitializeArray` 5.5% (array zeroing on
  every allocation). See [bottlenecks.md](bottlenecks.md) §5b.

## 8. Recurring data-structure patterns

| Pattern | Where | Fix |
|---|---|---|
| Linear scan where an index belongs | `SendPackage` Clients, `GetClosestPlayer`, `NetEntityDistribution` all-pairs, `GetEntitiesInBounds` | map / spatial grid |
| Per-tick clear-and-rebuild a full collection | `tickEntityList`, `chunksAround`, `m_*ChunkPositions` | incremental diff |
| Re-new a fixed-size buffer each use | `InitScan` node array (+ nodes) | reuse-in-place / pool |
| Serial priority interlock | `EAITaskList`, `ConnectionManager.Update` | reduce who runs, not parallelize |
| Pooled object with retained buffer (already good) | `PooledExpandableMemoryStream` | leave it; attack serialize *count* |
