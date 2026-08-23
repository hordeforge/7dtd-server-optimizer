# Optimization candidates (from dedicated RE)

**Hub:** [`README.md`](../README.md).  
**Status:** research inventory for EfficientServer, not a commit-to-ship roadmap.

> **2026-07-21 campaign state:** the safe-Harmony space in this inventory is
> RESOLVED - shipped (P1/P2, #1 fast send, GC guard, skips, explosion particles,
> replication stride, governor, TickGuard), refuted with evidence (spatial interest
> grid, serialize-once, mid-band stride, parallel interest scan, chunk-send
> throttle sizing, fps/jitter levers), or measured-and-parked (P4 pool: safe,
> no perf win). The authoritative outcome ledger is [`RESULTS.md`](RESULTS.md)
> §1-3l; entries below are historical grading kept for the reasoning trail.

**Promote only** with APM + loadgen evidence, fidelity checklist, soft-fail Harmony, config flag.

**Owns:** graded A/B/C candidates, hot-path notes, APM probes, experiment order, Harmony target list.

**Does not own:** raw IL dumps (those stay under `7dtd-research/il/` as regenerable evidence).

| Related | Role |
|---|---|
| [`OPTIMIZATION_IDEAS.md`](OPTIMIZATION_IDEAS.md) | Broader idea map, io_uring, threading philosophy, OSS |
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | Stock hot path RE summary |
| [`SIM_PARALLELISM.md`](SIM_PARALLELISM.md) | Threads / extract / conductor |
| [`HOST_TUNING.md`](HOST_TUNING.md) | Ops CCD/NUMA/storage |
| [`../../7dtd-research/docs/loop.md`](../../7dtd-research/docs/loop.md) | Full dedicated loop map |
| [`../../7dtd-research/docs/INDEX.md`](../../7dtd-research/docs/INDEX.md) | IL dump index |

Game version pin: **V3.1.0** dedicated `Assembly-CSharp`. Do not redistribute game IL.

---

## 1. How to read this

| Grade | Meaning |
|---|---|
| **A** | Clear hot path; Harmony/config-shaped; high leverage |
| **B** | Real cost, more fidelity risk or larger surface |
| **C** | Situational / content-dependent / measure first |
| **Ops** | Host, config, schedule, not EfficientServer code |
| **Reject** | Unsafe or wrong layer for this project |

IL counts are **method size**, not runtime rank. Runtime rank needs APM.

---

## 2. Candidate table (prioritized)

### Grade A: primary EfficientServer / near-term research

| # | Target | Evidence | Idea | Risk |
|---:|---|---|---|---|
| A1 | `EntityAlive.updateTasks` + `aiActiveScale` | Already ES; delay only throttles EAI, not nav | Keep LOD; optional stronger far skip | Fidelity combat/sleeper |
| A2 | `EntityAlive.FindPath` → `PathFinderThread.FindPath` | **DONE (ES v1.17.0):** `PathAdmissionPatch` prefix (`Pathfinding.MaxPathEnqueuesPerTick` / `DropPathWhenFarDistSq`, priority admits bypass); live A/Bs found no reliable frame win, so both knobs ship default 0 = vanilla (RESULTS 'Blood-moon path-admission profile') | Always enqueues (Y-clamp if xz dist² &gt; 1225 ≈ 35 m); per-id dict coalesce; worker drains **≤8**/slice then yields; compute = `AstarPath.StartPath`; admission bounds enqueue spikes without touching the A* library | Path stuck / dumb AI |
| A3 | `EntityMoveHelper.UpdateMoveHelper` | **1236 IL**; dig/jump/stuck/attack; every `updateTasks` | Far skip of whole updateTasks; research cheaper mid-tier move | Movement fidelity |
| A4 | `World.EntityActivityUpdate` + `GetClosestPlayer` | O(players) linear scan; builds `aiClosest` | Cache closest player / TTL; spatial hash (Mid) | Stale LOD |
| A5 | `World.AddFallingBlock` / `LetBlocksFall` / `GroupFallingBlocks` (292) | Queue + entity factory; mesh observer | Optional fall→air (ServerTools/IceCoffee) | Collapse gameplay |
| A6 | `SpawnManagerBiomes.SpawnUpdate` (**441 IL**) | Every ~20 ticks × area-master chunks | Scope to player-near chunks only | Spawn density change |
| A7 | Dedicated `GC.Collect` in `gmUpdate` | **DONE (ES 2026-07-18):** transpiler reroutes the single `GC.Collect()` (fired every ~120 s via `gcCountdownTimer`) through `GcGuardPatch.MaybeCollect` | Skip the forced full STW; heap-ceiling safety collect (`Gc.SafetyCollectAboveMB`) | Memory growth (bounded by the safety collect) |

### A7 benchmark (2026-07-18): GC guard vs incremental vs forced

Measured on the same loads (7dtd-loadgen + 7dtd-apm, 150 s captures):

| load | config | gross MB/s | full GC | late ticks | overage |
|---|---|:--:|:--:|:--:|:--:|
| 150 zombies (3.7 MB/s) | forced | 3.78 | 2 | 33 | 1763 |
| | **guard** | 3.68 | **1** | **22** | **1271** (-28%) |
| | incremental | 3.29 | 1 | 27 | 1221 |
| 128 players (15 MB/s, CPU-bound ~7 FPS) | forced | 15.16 | 4 | 192 | 6117 |
| | guard | 14.84 | 4 | 196 | 6622 |
| | incremental | 14.52 | 4 | **176** | 6083 |

**Conclusion.** GC **guard** helps at moderate load (removes the forced collect,
-28% overage) and is a wash at heavy load (churn drives the collects regardless);
free + safe -> **default on**. GC **incremental** is marginal everywhere
(~8% late ticks at 128p; write-barrier overhead ~cancels pause-shortening) ->
**opt-in / default off**. Critically, **gross churn barely moves across configs
(15.16/14.84/14.52)** - GC tuning is downstream of the allocation and cannot
reduce it. At 128 players the server is CPU-saturated on the O(N^2) network
serialization, which also *generates* the 15 MB/s churn. So the real lever is
NETWORK/serialization ([`NETWORK_OPTIMIZATION.md`](NETWORK_OPTIMIZATION.md), esp.
L1 serialize-once, which cuts the alloc at source -> fixes CPU *and* GC), not GC
mode. GC guard/incremental are secondary.

### Grade B: high value, more care

| # | Target | Evidence | Idea | Risk |
|---:|---|---|---|---|
| B1 | `EAIApproachAndAttackTarget.Update` (**846 IL**) | Dominant combat EAI; **FindPath up to 3×** per Update | Fewer EAI pulses (scale); admission catches path spam | Combat feel |
| B2 | `EAITaskList.OnUpdateTasks` | Serial task list; IceCoffee parallel failed | Leave serial; reduce **who** runs EAI | - |
| B3 | `NetEntityDistribution` + `updatePlayerList` (**509**) | Interest refresh distSq&gt;**16**; move if enc Δ≥**2**; **Teleport** if Δ∉±**256**; full **PosAndRot** if Δ∉±**128** or age&gt;**100** ticks; else **RelPosAndRot**; vel if motion²&gt;**0.04** | Raise far thresholds / lower full updates under load | Desync |
| B4 | `ChunkManager.DetermineChunksToLoad` (**448**) / `SendChunksToClients` (216) | Per UpdateTick server | View distance ops; rare Harmony | Streaming bugs |
| B5 | `DynamicMeshManager.Update` (404) + `DynamicMeshServer.Update` (**452**) | Peer MB; already ES budgets | Tighter budgets; OnlyPlayerAreas | Pop-in |
| B6 | `DecoManager.UpdateTick` (330) | Always before server gate; locks + coroutine | Dedicated skip / rate limit deco | World cosmetics |
| B7 | `WaterSplashCubes.Update` (185) | Always in `OnUpdateTick` | Dedicated skip (like music) | VFX |
| B8 | `SleeperVolume.Tick` (137) / touch paths | All volumes each tick path | Distance gate / budget | POI sleepers |
| B9 | `WorldBlockTicker.tickScheduled` (151) / `tickRandom` (97) | Server world tick | Budget execute rate | Farming/liquid |
| B10 | `VehicleManager.Update` (297) / `DroneManager.Update` (305) | gmUpdate every frame if instance | Idle early-out; less waypoint work | QoL |
| B11 | `EntityVulture.updateTasks` (**1344 IL**) | Flying special case | Species-specific far skip | Flying AI |
| B12 **DONE+VALIDATED (P1)** | `AstarManager.UpdateGraphs` (185) | Graph maintenance; **66 ms top section** AND top allocator (`InitScan`, corrected APM) at heavy load, total O(N^1.43) in players ([`PATHFINDING_OPTIMIZATION.md`](PATHFINDING_OPTIMIZATION.md)) | **P1+P2 shipped (v1.4.0):** `AstarGraphThrottlePatch` (cadence, `GraphUpdateEveryTicks`=4; A/B **ms_per_tick -28.5%**) + `AstarMoveThresholdPatch` (rescan dead-zone `MoveRescanThresholdSq`, default 100=vanilla). P3 **dropped** (unsound: `UpdateMoveGraph` already 1/call). P4 `InitScan` pooling **built (v1.8.0, `InitScanPoolPatch`, opt-in default off)**: the earlier concurrency doubt is resolved (scans hold AstarPath's work-item lock); alloc eliminated but no benchable tick win (RESULTS §3c-3d) | Nav holes / AI stuck |
| B13 | `ThreadManager.UpdateMainThreadTasks` (64) | Drains main queue every gmUpdate | Don’t flood from mods | - |
| B14 | AIDirector always-on components | BM, wandering, chunk scouts, airdrop always installed | Measure BM/scout cost; don’t assume removable without CreateComponents change | Spawn fidelity |

### Grade C: situational

| # | Target | Notes |
|---:|---|---|
| C1 | `PowerManager.Update` (106) | Content grids (OCB can explode this) |
| C2 | `QuestEventManager.Update` (127) | Quest-heavy servers |
| C3 | `GameEventManager.HandleSpawnUpdates` (148) | Event content |
| C4 | `FactionManager.Update` (43) | Usually small |
| C5 | `MultiBlockManager` stability helpers | Falling-block related |
| C6 | `Entity.OnUpdateEntity` → `GetEntitiesInBounds` | Push neighbors; density |
| C7 | Turrets / traps `GetEntitiesInBounds` | TE density |
| C8 | `ConnectionManager.SendPackage` | Prefer fewer packages, not parallel send |
| C9 | `ProcessPackages` (116) | Inbound; not first |
| C10 | Mesh generators / DistantChunk* | Client/gen; not dedi AI loop |
| C11 | Dual entity Unity `Entity.Update` | C until dedi GO activity measured |
| C12 | Origin / Sky / lights / console | Ops or rare |

### Ops / config (not Harmony first)

| Item | Notes |
|---|---|
| `MaxSpawnedZombies` / blood moon counts | Stock knobs |
| Server view / sim distance | Chunk union |
| Save / backup windows | APM blackout |
| Zero-player idle | gmUpdate special-cases somewhat |
| Disable Twitch / unused managers | Avoid constructing instances |
| Host CCD/pin | [`HOST_TUNING.md`](HOST_TUNING.md) |
| **GameTimer 20 ticks/sec** | Stock design; frame budget context |

### Reject (reconfirmed)

| Idea | Why |
|---|---|
| Parallel.ForEach EAITaskList | Shared lists; OSS abandoned |
| Parallel SendPackage | Connection safety |
| Worker `TickEntity` / `OnUpdateLive` | Main-thread world |
| io_uring for AI | Wrong layer |
| Full gmUpdate replace without peer Updates | Misses net/mesh MBs |
| Thread-per-zombie | Overhead + races |
| Unbounded Task.Run path workers | Stampede (IceCoffee fossil) |

---

## 3. Hot path detail notes (RE-derived)

### 3.1 Path enqueue (`EntityAlive.FindPath`)

```text
delta = target - position
xzDistSq = dx*dx + dz*dz
if xzDistSq > 1225 (~35 m):
 clamp target.y within ±45 of entity.y if vertical gap large
PathFinderThread.Instance.FindPath(...) // always
```

No rate limit. One Prefix admits all EAI/UAI path requests.

**Callers:** EAIApproachAndAttack (up to 3×/Update), ApproachDistraction/Spot, DestroyArea, RunAway, Territorial, Wander; UAI flee/move/wander.

### 3.2 Path worker (ASP + A*)

```text
FindPaths coroutine: for i in 0..7: dequeue → GetPathTo → maybe remove
 GetPathTo → CreatePath → ASPPathFinder.Calculate (333 IL)
 → Pathfinding.ABPath/XPath/MultiTarget/Random/Flee
 → AstarPath.StartPath
yield; repeat
```

Enqueue unbounded; compute capped at **8/slice**. Admission complements the drain.

### 3.3 Move helper

`EntityMoveHelper.UpdateMoveHelper` (**1236 IL**): dig, jump, stuck, attack assist, random. Runs whenever `updateTasks` runs (even if EAI delayed). Far full-skip of updateTasks is the practical lever.

### 3.4 AI LOD vs updateTasks

```text
aiActiveDelay -= aiActiveScale
if elapsed → EAIManager.Update() or UAI // decisions only
// ALWAYS on updateTasks:
GetPath → SetPath → pathFollow → MoveHelper → LookHelper
```

Stock scale **does not** throttle MoveHelper/path follow.

LOD bands (dist²): full &lt;64; mid &lt;225 → 0.3; else 0.1; jiggle &lt;36; cloth ~625/3025.

### 3.5 Closest player

`GetClosestPlayer`: linear `Players.list` scan. Primary consumer: `EntityActivityUpdate`.

### 3.6 Falling blocks

`AddFallingBlock` → queue → `GroupFallingBlocks` / `LetBlocksFall` → `EntityFallingBlock*`.

### 3.7 Spawn

`SpawnManagerBiomes.SpawnUpdate` (441 IL) every ~20 game ticks × area-master chunks. AIDirector always has BM + wandering + chunk scouts + airdrop components.

### 3.8 Net interest packages

See Grade B3 thresholds. Package types: RelPosAndRot, PosAndRot, Teleport, Rotation, Velocity, AliveFlags, PlayerStats, TwitchStats, Equipment.

### 3.9 Deco / splash / managers

Deco 330, splash 185 always on world tick path. Vehicle/Drone ~300 IL each frame if instances exist.

### 3.10 Alloc pressure

Hot entity methods mostly **0 newobj**. Path enqueue allocates `PathInfo` (newobj=1). Admission reduces that.

### 3.11 Game timer

`ticksPerSecond = 20` stock. Aligns with common 50 ms dedicated frame discussions.

---

## 4. Suggested APM stack probes

```text
GameManager.gmUpdate
GameManager.UpdateTick
World.TickEntities / TickEntity / EntityActivityUpdate
EntityAlive.updateTasks / OnUpdateLive
EntityMoveHelper.UpdateMoveHelper
EAIManager.Update / EAIApproachAndAttackTarget.Update
PathFinderThread.FindPath / ASPPathNavigate.pathFollow / ASPPathFinder.Calculate
World.LetBlocksFall / AddFallingBlock
SpawnManagerBiomes.SpawnUpdate
NetEntityDistribution.OnUpdateEntities
NetEntityDistributionEntry.updatePlayerList
ChunkManager.DetermineChunksToLoad / SendChunksToClients
DynamicMeshManager.Update / DynamicMeshServer.Update
DecoManager.UpdateTick
WaterSplashCubes.Update
VehicleManager.Update / DroneManager.Update
ConnectionManager.Update
AIDirectorBloodMoonComponent.Tick
GC.Collect (if hit)
```

Correlate with: Full-tier entity count, path queue depth, players online, resident chunks.

---

## 4b. APM measured evidence (2026-07-16 live campaign)

Seven window-scoped experiments on the V3.0.1 dedicated (RWG 4k, 128-slot),
bot cohorts via `7dtd-loadgen`, 120 s deep captures via
`7dtd-apm scenario run` with bridge stats reset at window start. Attribution =
per-subsystem sum of instrumented managed section time (deep sections scaled
by sample rate 16). Caveat: sections nest, so `frame_core`
(`GameManager.UpdateTick`) is inclusive of the entity/AI buckets; chunk/net
buckets are largely disjoint from it.

| Experiment | Workload | Dominant instrumented cost |
|---|---|---|
| exp0 idle | 0 players | 0.4 s total in 60 s: idle server is negligible |
| exp1 baseline | 100 wanderers + zombie pressure | chunk pipeline ~60% (NetPackageChunk Setup/write + SendChunksToClients + DetermineChunksToLoad); entity_tick 7% |
| exp2 zombie scale | 25 clients, continuous spawns | same chunk dominance; entity_tick doubles to 12% |
| exp3 demolition | 60 dynamite bots | chunk resend + saves ~65%: terrain damage = chunk invalidation storms |
| exp4 vehicles | 20 clients + spawned vehicles | entity_tick 18%; chunk pipeline still ~49% |
| exp5 turrets/drones | 20 clients + turrets | entity_tick 19.5%; same shape |
| exp6 horde | 50 clients + heavy spawns | 59.5 ms/tick instrumented (over 50 ms budget); chunk ~56%, entity_tick 12% |
| exp8 scale-70 | 30 grounded bots + 69 telnet-spawned pursuing zombies (99 entities) | entity_tick 20% and rising; **pathfinding drilldown 3127 ms** (5x combat-bait's 653 ms at ~40 zombies -> super-linear); per-entity tick cost flat ~0.08 ms/entity/tick; 50 late ticks at only 13.6 ms compute; frame spike = a 384 ms Mono GC pause |
| exp7 combat-bait | 30 clustered bait bots (`--rally`), ~90 zombies in active pursuit | chunk noise cut to ~12%; **entity_tick (slice-level) 58%**; AI-decide 0.9 s + pathfinding 0.65 s + movement 0.34 s of 27.4 s window; path queue healthy (112 enq / 80 drained / 32 computed) |

**Scale extrapolation to 1000 (measured, not modeled):** per-entity tick cost
is linear at ~0.08 ms/entity/tick across the ladder (32 -> 99 entities), so
1000 AI entities = ~80 ms/tick of entity-tick machinery ALONE, over the 50 ms
(20 TPS) budget before AI decisions, pathfinding, net, or chunks. Pathfinding
scales super-linearly with *pursuing* count (653 ms at ~40 -> 3127 ms at 69).
Confirms the doctrine: 1000 needs (1) tick-stride skipping at the
TickEntity/OnUpdateEntity level for far tiers (not just updateTasks, which is
the cheap part), and (2) path admission (A2). Also: on this hardware the lag
was never CPU-bound - gmUpdate averaged 1.7-13.6 ms while ticks ran 50+ ms,
and the actual frame spikes were Mono stop-the-world GC pauses (357-384 ms),
i.e. "laggy without CPU" = GC. Measured allocation pressure at ~70 zombies /
30 players = **3.8 MB/s of managed heap growth** (**CORRECTED in §4c: this is
*net* heap delta and undercounts ~3x; use ~12.5 MB/s *gross* churn for any
decision**), triggering ~3 full Boehm
collections per 90 s window (each a STW hitch). Boehm is non-generational, so
the lever is raw allocation rate: guard the dedicated GC.Collect (A7) and cut
per-tick allocations (LINQ in hot loops, per-tick new List/string.Format in
entity/AI paths) - these rise to top priority alongside entity-tick striding.
The APM allocation-site probe (mono_alloc, jitmap-annotated) named the two
largest managed allocation sites under load: **EntityItem.tickDistraction**
and **AstarManager pathfinding** (A* iterator). These are the concrete cut
targets: pool/avoid the per-tick allocations in item-tick and the pathfinder.
Broader allocator sampling (forensic) also surfaced **Chunk.load** and
**AstarVoxelGrid.InitScan** as large-allocation sites - chunk load and A* graph
scan allocate heavily, reinforcing B4 (chunk streaming) and A2 (path) as the
allocation-reduction targets, not only the CPU targets.

**Measured conclusions:**

1. **Chunk streaming/serialization is the top measured managed cost in every
   loaded scenario** (NetPackageChunk.Setup x100k+/window, SendChunksToClients,
   DetermineChunksToLoad). Promote **B4 to first experiment position** for
   many-player workloads. Wandering bots are worst-case chunk churn; clustered
   real players will be milder, but demolition shows the same signature purely
   from block damage.
2. **entity_tick scales with entity count** (5-20%) and is the second lever,
   through `TickEntity`/`OnUpdateEntity` volume rather than AI decisions.
3. **Combat evidence (exp7, clustered bait cohort under active pursuit):**
   per-entity tick machinery (`TickEntitiesSlice` chain, `OnUpdateLive`)
   dominates at ~58% additive / ~0.12 ms per entity-tick, while AI decisions,
   pathfinding, and MoveHelper together stay under ~2 s of a 27 s window and
   the path queue never backlogs (112 enqueued vs 80 drained in 120 s at ~90
   pursuing zombies). Supports **UpdateTasksLodPatch-style whole-chain skips
   (A1/A3) over path admission (A2)** at these scales; A2 needs a blood-moon
   200+ feral test before promotion.
4. `AstarManager.UpdateGraphs` spikes to p95 8.9 ms per call under load
   (B12 evidence): graph maintenance, not path volume.
5. Explicit saves visible as periodic io_saves activity
   (`World.SaveWorldState` p95 8 ms, SaveRandomChunks steady): A7/Ops
   unchanged.

Raw sessions: `~/.local/share/7dtd-apm/session_20260716_14*-15*` (labels in
`workload.json`); per-subsystem numbers in each session's
`csharp_bridge.json` `attribution` block.

## 4c. Corrections from the 2026-07-17 measurement-fidelity pass

Three §4b figures were measurement artifacts; the corrected picture sharpens
the levers (does not overturn them).

1. **Allocation rate was under-measured ~3x.** §4b's "3.8 MB/s managed heap
   growth" is *net* heap delta (`GC.GetTotalMemory` between windows), which
   reads near-zero at steady state because allocation and collection cancel.
   True *gross* allocation is the GC-pause driver. `GC.GetTotalAllocatedBytes`
   is absent in Unity 2022 Mono, so gross is now measured via the `mono_alloc`
   probe summing `GC_malloc` arg0: **~12.5 MB/s gross churn** with a stable
   heap (net ~0) and ~2.5M allocations per 30 s window. The old net metric
   would have cleared GC entirely. This is the corrected magnitude of the
   "laggy without CPU = GC" mechanism. (The bridge now also reads gross directly
   from Boehm's native `GC_get_total_bytes`, so gross is in every capture, not
   just forensic runs.) Load-scaling: an idle server (0 players) allocates only
   **~0.2 MB/s**; 15-20 bots + zombies drive it to ~11 MB/s (~50x) regardless of
   activity type (wander/kite/demolition all land ~11) - so the churn is
   per-client/per-entity serialization + pathfinding, not a base-system cost.

2. **The steady churn floor is per-entity/pathfinding + tile-entity IO, NOT
   serialization reflection.** A 1/4096 sampled all-sizes allocation profile
   attributes the floor partly to **`TileEntity.InstantiateFromRead` /
   `TileEntityFeatureData.InstantiateModule`** (tile-entity chunk load/save),
   with the top *large*-alloc sites (R25) being **`EntityItem.tickDistraction`**
   and **`AstarManager` pathfinding** (`get_Current` iterator).
   **CORRECTION (2026-07-18):** an earlier draft here claimed
   `PooledBinaryWriter.FinalizeSizeMarker` calls `Type.GetMethod` per serialize -
   that was a sampling misattribution. Direct IL inspection of `FinalizeSizeMarker`
   (DumpMethodByName, il=91) shows **no per-call reflection**: it is an enum
   switch on `EMarkerSize` (Int8/16/32) + stream position writes; the only
   reflection-ish call (`EnumUtils.ToStringCached`) is in an error-throw path
   only. So "cache the reflected MethodInfo in PooledBinaryWriter" is **not a
   valid cut** - there is nothing to cache. Real cut targets remain: pool the
   `TileEntity` read buffers; reduce `tickDistraction` / pathfinding-iterator
   allocation. `AstarVoxelGrid.InitScan` remains the top *large-allocation* (spike)
   site; it is large+infrequent
   (heap-growth hitches) rather than the steady floor.

3. **Chunk *bandwidth* is a join-time burst, not a steady wall.** §4b's chunk
   "MB/s" came from the bridge `mapTransfers` counter, which is a since-reset
   lifetime average dominated by the initial world download at client join. An
   independent kernel UDP probe (`udp_sendmsg` byte sum, always
   capture-windowed) shows **steady-state chunk send is ~1.8 MB/s** for 20
   moving bots, versus the ~60 MB/s the bridge average reported. Chunk
   *serialization CPU + allocation* is still the top managed cost (conclusion
   #1 stands); chunk *bandwidth* is not a steady bottleneck. Also: 7DTD
   loadgen bots now roam the map freely (continuous server-position
   reconciliation + real ~6 m/s run speed; validated single bot ~1800 m, cohort
   ~3700 m spread). Even so, kernel chunk bandwidth stays low (~0.2-1.8 MB/s):
   chunks compress well, so the chunk COST the server pays is CPU + allocation
   (serialization ~10 MB/s gross alloc), not network bytes. This reconciles §4b
   ("chunk pipeline dominates" = serialization CPU) with the low measured
   bandwidth - the wire was never the bottleneck; the serialization allocation
   is. (An earlier note here that server validation clamped bot roaming was
   wrong: the blocker was the loadgen's one-shot Y-adoption + superhuman step
   speed, since fixed.)

Raw sessions: `~/.local/share/7dtd-apm/session_20260716_19*` (capture with
`--only alloc,app,runtime` for gross allocation + churn attribution).

## 4d. Player-scaling wall (2026-07-17, ramp to 1000 clients)

Ramped LiteNetLib bot *players* (no zombie spawn, to isolate per-player cost;
`ServerMaxPlayerCount=1100`, seed fixed). Result: **the server does not reach
1000 players - it saturates and death-spirals at ~450-500.**

Measured `gmUpdate` compute vs connected players (healthy region, linear
~0.0085 ms/player; tick interval stays well under the 50 ms budget):

| players |  86 | 153 | 255 | 309 | 413 |
|---------|----:|----:|----:|----:|----:|
| gmUpdate ms | 0.7 | 1.2 | 1.9 | 2.5 | 3.5 |
| tick avg ms | 8.8 | 9.9 | 11.1 | 12.3 | 14.0 |

Then a cliff (not a slope): by ~498 players gmUpdate = **1376 ms** and tick
interval = **2928 ms (0.34 TPS)**; by 634 clients tick = 3397 ms (0.29 TPS),
telnet unresponsive, and ~1250 further bots time out joining. It is a phase
transition / feedback spiral: once per-tick serialization to N connections
misses the budget under a burst, the packet + join backlog grows, the
connection layer falls further behind, and gross allocation from the join/log
storm (164 MB/s at the wall vs ~11 MB/s at 50 players) triggers GC pauses that
deepen the spiral.

**The player-scale bottleneck is the network/connection layer, NOT entity AI.**
Section attribution at ~500 players (per 15-tick window): `NetConnectionSimple.taskSerialize`
**4554 ms**, `GameManager.UpdateTick` 1181 ms, `ConnectionManager.Update` 988 ms,
`NetEntityDistribution.OnUpdateEntities` 914 ms, `ChunkManager.SendChunksToClients`
244 ms - while `World.TickEntities` is only 18 ms. So the entity-tick striding
(A1/A3) that helps the 1000-*zombie* case (§4b) does **not** address the
1000-*player* case. Distinct levers: move package serialization off the main
thread, spatially cull `NetEntityDistribution` (per-player entity lists appear
to scale ~O(players x entities)), batch/rate-limit `ConnectionManager` work, and
cut the per-join auth/log allocation churn (`String.Format`, telnet/Unity log
writes were top churn sites at scale). Practical ceiling on this build/hardware:
**~450 players** before collapse; 1000 needs the connection-layer rework first.

**Scaling exponents (log-log least-squares over 5 captures, 15 → 498 players,
`apm scaling`).** This quantifies the wall - the two dominant sections are
*near-quadratic per call*:

| Section | per-call | total | note |
|---------|:--------:|:-----:|------|
| `ConnectionManager.Update` | **O(N^2.27)** | O(N^1.3) | per-connection broadcast |
| `NetEntityDistribution.OnUpdateEntities` | **O(N^2.26)** | O(N^1.3) | confirms ~O(players×entities) |
| `ChunkManager.DetermineChunksToLoad` | O(N^1.77) | O(N^0.82) | |
| `GameManager.UpdateTick` | O(N^1.67) | O(N^0.7) | frame core |
| `ChunkManager.SendChunksToClients` | O(N^1.64) | O(N^0.68) | |
| `AstarManager.UpdateGraphs` | O(N^0.27) | **O(N^1.43)** | only compute section super-linear in *total* |
| `World.TickEntities` / `Slice` / `Flush` | ~O(N^0.67) | *negative* | entity AI is **sub-linear** - not the wall |

So the O(N^2) per-call connection + entity-distribution updates are the
mechanism behind the ~450-player cliff. Entity AI is sub-linear **in this fit
only because these captures spawned no zombies** (`--no-spawn`, to isolate
per-player cost): the 498-player session has ~610 entities that are mostly the
bot players themselves, so there is almost no AI load and the deep AI sections
(`EntityAlive.updateTasks`, `EAIManager.Update`, `FindPath`) barely register.

**This is a two-dimensional problem, not "AI doesn't matter":**
- **Player scale** (this section, §4d): the wall is the network/connection layer
  (O(N^2) per call). Levers: off-thread serialization, `NetEntityDistribution`
  spatial culling, `ConnectionManager` batching.
- **Entity / zombie scale** (§4b): the wall is **entity AI + pathfinding**
  (`EntityAlive.updateTasks`, `EAIManager.Update`, `EntityAlive.FindPath`,
  `PathNavigate`) - the A1/A3 tick-striding / path-admission candidates target
  exactly this. Even in the player-only runs, `AstarManager.UpdateGraphs`
  (pathfinding maintenance) is already **total super-linear O(N^1.43)**.

**Entity-scale fit (2026-07-18, players held at 16, zombies ramped 114 → 452
via telnet spawn, `apm scaling --by entities`):** entity AI is a **linear**
volume cost, not super-linear. `World.TickEntities` / `Slice` / `Flush` =
**O(N^1.13) per call, O(N^0.96) total**; `World.EntityActivityUpdate` and
`NetEntityDistribution.OnUpdateEntities` ~O(N^1.07). The scaling detector found
**no super-linear section by entities**. Sanity check on the two-axis split:
`ConnectionManager.Update` is **sub-linear (0.09) in entities** (it is
player-driven, per §4d), and `AstarManager.UpdateGraphs` is sub-linear in
entities too. So the two walls are genuinely distinct:

- **Players → network, super-linear** (O(N^2.27) per call - an algorithmic wall).
- **Zombies → entity AI, linear** (O(N^1.1) - a large but well-behaved volume
  cost). The A1/A3 tick-striding / path-admission levers cut the *slope* of this
  linear cost (do less per entity, tick fewer per frame); there is no bad
  complexity class to fix on the entity axis.

Sessions: `session_20260717_{224604,225502,231311}_pid2415896` (114/306/452
alive; the 1000-tier capture snapshotted 0 entities and was dropped);
`apm scaling` output in `~/.local/share/7dtd-apm/zladder_scaling.json`.

Method note: `NetConnectionSimple.taskSerialize` is **no longer instrumented**
(2026-07-17) - it is a long-lived per-connection writer-thread task, so
prefix/postfix wall-clock reported its whole lifetime (600 s+), swamping the
section table; the bridge now also drops any single sample > 30 s defensively.
Its network cost is captured via `ConnectionManager.Update` + the map-transfer
byte counters instead.

Raw sessions: the exponent fit used
`session_20260717_{022851,015855,072731,081439,030120}_*` (15/20/41/100/498
players); `apm scaling` output in `~/.local/share/7dtd-apm/ladder_scaling.json`.
Forensic ~500-player capture: `session_20260717_0301*`.

## 5. Experiment order (if evidence agrees)

```text
0. DONE 2026-07-16: attribution campaign (see 4b): chunk pipeline dominates
1. Chunk streaming budget/scope (B4): first, per measured evidence
2. DONE 2026-07-16 (exp7 combat-bait): entity tick chain 58%; whole-chain AI LOD (A1/A3) over path admission
3. Path admission (A2): after combat evidence
4. Closest-player cache TTL (A4)
5. Falling-block optional (A5): demolition evidence says cost is chunk resend
6. Spawn walk scope (A6)
7. Deco/splash dedicated skip (B6/B7): measured tiny on dedicated; low prio
8. Vehicle/drone idle skip (B10)
9. Guard dedicated GC.Collect (A7)
10. Net package rate LOD (B3) - high risk, last
```

---

## 6. Giant methods NOT first optim targets

| Method | IL | Why deprioritize |
|---|---:|---|
| DistantChunk* / MeshGenerator* | 1k-4k | Client/gen mesh |
| DynamicMeshConsoleCmd | 3604 | Console |
| Block.Init / EntityClass.Init | 2k | Startup |
| EntityPlayerLocal.* | large | Client |
| EntityVehicle.PhysicsFixedUpdate | 1509 | Only if many vehicles |

---

## 7. Harmony soft-fail target list (experiments)

```text
EntityAlive.FindPath(Vector3, float, bool, EAIBase)
PathFinderThread.FindPath (virt; Instance is ASPPathFinderThread)
World.AddFallingBlock(Vector3i, bool)
World.EntityActivityUpdate
SpawnManagerBiomes.SpawnUpdate
DecoManager.UpdateTick
WaterSplashCubes.Update
VehicleManager.Update
DroneManager.Update
GameManager.gmUpdate // GC.Collect site only, not full replace
NetEntityDistributionEntry.updatePlayerList // research only
EntityMoveHelper.UpdateMoveHelper // research; huge
```

Each: feature flag, dedicated-only, soft-fail log, FEATURES fidelity notes.

---

## 8. Loop surfaces (cross-check)

Every Grade A/B row anchors a method on the dedicated loop map ([`../../7dtd-research/docs/loop.md`](../../7dtd-research/docs/loop.md)).

| Surface | Method anchors | Grade |
|---|---|---|
| Dual entity Unity Update | `Entity.Update`, `EntityAlive.Update` | C until measured |
| Origin shift | `Origin.FixedUpdate` | Ops / rare |
| AIDirector family | CreateComponents always-on list | B/C with spawn |
| Save serializers | `WorldState.SaveLoad`, region files | Ops |
| Sky/Environment/Lights | if components present | C |
| Console | `SdtdConsole.Update` | Ops |

---

## 9. RE evidence locations (raw dumps only)

| Evidence | Path |
|---|---|
| Frame / gmUpdate | `7dtd-research/il/loop-complete-v3.1.0/` |
| Entity→AI→path | `7dtd-research/il/deep-v3.1.0/` |
| EAI/MoveHelper constants | `7dtd-research/il/deeper-v3.1.0/` |
| Large-method scan | `7dtd-research/docs/inventories/opt-scan.md` + `7dtd-research/il/opt-scan-v3.1.0/*_il.txt` |
| Timer/AIDirector/net bands | `7dtd-research/il/gaps-v3.1.0/` |
| MB inventory | `7dtd-research/il/frame-entries-v3.1.0/` |
| Loop notes | `7dtd-research/docs/inventories/loop-complete.md` + `7dtd-research/il/loop-complete-v3.1.0/` |

Regenerate dumps with the RE tooling in `../../7dtd-research/tools/` (see [`../../7dtd-research/tools/README.md`](../../7dtd-research/tools/README.md)). Optim **narrative** lives only under `7dtd-optimizer/docs/`.

---

## 10. Bottleneck-audit additions (2026-07-19)

Full ranked catalog + bang-for-buck ordering:
[`bottlenecks.md`](bottlenecks.md) (42 verified
findings). New/actionable levers not already graded above, by impact-per-line:

| Lever | Grade | Code | Impact |
|---|---|---|---|
| **`ConnectionManager.SendPackage` entityId-map lookup (`FastSendPatch`, v1.6.0)** | **A - SHIPPED + VALIDATED** | done (prefix, `Network.FastSingleTargetSend`) | removes the O(clients) linear `Clients` scan for pure single-target sends via the existing `ForEntityId` map. A/B: correct (60/60, 120/120 stable), ms_per_tick **-1.8%@60p -> -4.2%@128p**, `ConnectionManager.Update` -5.2%@128p; scales toward the 450-500p death-spiral |
| ~~`PooledExpandableMemoryStream` presize + retain~~ | **downgraded** | n/a | RE correction: `Reset()`=`SetLength(0)` already retains the buffer; not a realloc problem. Serialization churn is a *count* problem -> serialize-once ([`allocation-reuse.md`](allocation-reuse.md)) |
| Path admission cap + `ASPPathFinder` reuse | cap/drop **shipped v1.17.0 (`PathAdmissionPatch`), no win at any tested load - knobs stay 0/0**; `ASPPathFinder` per-build alloc reuse still open | small | bounds path-request spikes + per-build alloc at `EntityAlive.FindPath` enqueue |
| Off-sim `Chunk.write` encode / cached chunk blobs | B | moderate (threading) | chunk pipeline is 56-60% of tick; biggest single CPU reclaim |
| Shared spatial interest grid (chunk-cell uniform grid) | B/C | large (new subsystem) | collapses the O(N^2.26)/O(N^2.27) player walls + E x P products toward linear; reused by `NetEntityDistribution` interest, `GetClosestPlayer` (A4), and the `SendPackage` map |

Structural conclusion of the audit: nearly every high-severity bottleneck is **a
missing spatial index or a serial main-thread stage**. Spatial bucketing + off-thread
/ lazy encode + buffer reuse collapse most of the board.

---

## Related docs

| Doc | Role |
|---|---|
| [DEVELOPMENT.md](DEVELOPMENT.md) | How to ship a candidate |
| [FEATURES.md](FEATURES.md) | Shipped feature groups |
| [bottlenecks.md](bottlenecks.md) | Ranked bottleneck catalog this grades |
| [OPTIMIZATION_IDEAS.md](OPTIMIZATION_IDEAS.md) | Unranked idea backlog |
| [RESULTS.md](RESULTS.md) | A/B evidence ledger |

## Changelog

- **2026-08-08:** Stale `il/*-v3.0.1/` dump paths updated to current `*-v3.1.0/` dirs.
- **2026-07-19:** bottleneck audit (42 verified) consolidated into `bottlenecks.md`; §10 additions (SendPackage entityId-map = top new bang-for-buck, buffer presize+retain, off-sim chunk encode, spatial interest grid). GC megapause measured (479 ms @ 6.9 GB); allocation-reuse research documented.
- **2026-07-17:** scale ladder (exp8, 99 entities) added; per-entity tick cost measured linear (~0.08 ms), 1000-AI extrapolation and GC-pause-as-lag conclusion recorded.
- **2026-07-16 (later):** 4b measured-evidence campaign added; experiment order re-ranked (chunk streaming first).
- **2026-07-16:** Moved from `7dtd-research/il/opt-scan-v3.0.1/` into optimizer project; merged deeper/gaps optim findings; IL folders keep dumps only.
