# 7 Days to Die Dedicated Server - reverse engineering notes

**Hub:** [`README.md`](../README.md).  
**Owns:** EfficientServer-oriented map of the dedicated hot path (optim context).  
**Not:** full generic RE narratives ([research loop](../../7dtd-engine-research/docs/loop.md)), feature behavior ([FEATURES](FEATURES.md)), host topology ([HOST_TUNING](HOST_TUNING.md)).

**Target:** Steam dedicated server **V 3.1.0 (b14)**  
**Engine:** Unity **2022.3.62f2** (Mono, not IL2CPP)  
**Main game assembly:** `7DaysToDieServer_Data/Managed/Assembly-CSharp.dll` (~11 MB, ~4400 types)  
**Networking stack:** LiteNetLib (+ optional SteamNetworking, disabled by default on dedicated)  
**Mod hooks:** Official `Mods/0_TFP_Harmony` (HarmonyX)

This document maps the **real hot path** of the official dedicated process. It is research for performance work, not a redistribution of game source.

**Complete loop map (peers, all subsystem families, open gaps):**
[`../../7dtd-engine-research/docs/loop.md`](../../7dtd-engine-research/docs/loop.md)  
**Stock ceilings (sim, net, AI, GC):**
[`../../7dtd-engine-research/docs/engine-limitations.md`](../../7dtd-engine-research/docs/engine-limitations.md)  
**Dump index:** [`../../7dtd-engine-research/docs/INDEX.md`](../../7dtd-engine-research/docs/INDEX.md)

## Why a full reimplementation is the wrong first move

A wire-compatible replacement server would need to reimplement:

- full world / chunk voxel simulation and save format
- all entity classes, AI packages, combat, inventory, quests
- hundreds of `NetPackage*` types and encryption handshake
- sleeper volumes, AIDirector, pathfinding graphs, block physics

That is years of work for a team. Client lag on a dedicated host is almost always **server simulation budget**, not missing protocol features.

**Practical path:** keep the official binary, reverse its frame loop, and cut work that does not matter on headless multiplayer.

## Concurrency model (audit-pinned V3.1.0)

Every EfficientServer patch surface runs on the Unity main thread; the mod takes
no locks anywhere. The confinement rules below are the invariant new patches
must preserve:

- **Main-thread confined:** `GameManager.UpdateTick` / `gmUpdate` / `LateUpdate`
  postfixes (Governor, TickGuard, TargetFps), `World.EntityActivityUpdate`,
  `EntityAlive.updateTasks`, every `EntityAlive.FindPath` caller (all EAI/UAI
  task leaves), `NetEntityDistribution.OnUpdateEntities` (from LateUpdate),
  `ChunkManager.SendChunksToClients`, `Entity.ccEntityCollision`,
  `AvatarZombieController.Update/LateUpdate`, and `AstarManager.UpdateGraphs`
  (driven by the `Start` coroutine). Path COMPUTE is also main-thread here:
  `ASPPathFinderThread.FindPaths` is a Unity coroutine (`StartCoroutine`), not
  an OS thread.
- **Console commands** (`es ...`) execute on the main thread too: telnet/stdin/web
  go through `SdtdConsole.ExecuteAsync` (Monitor-locked queue drained one command
  per frame by `SdtdConsole.Update`), and the game-connection path
  (`ServerConsoleCommand` -> `ExecuteSync`) runs where packages are processed,
  i.e. the main-thread `ConnectionManager.Update` pump. So config reload and
  governor transitions can interleave only at main-thread frame boundaries.
- **The one mod-owned background thread:** `GcDiagnostics`' opt-in megapause
  probe (default off). It touches nothing shared except P/Invoke into Boehm and
  `Log.Out`; it is one-shot per process (`_started`, set on the sequential
  main-thread GameStartDone).
- **Cross-thread reads are reference/int atomic only:** patches snapshot
  `ModApi.Config` per call; `ReloadConfig` swaps the whole object rather than
  mutating fields, so readers see one consistent snapshot (no torn state).
  State the governor derives from that object is re-based explicitly via
  `GovernorPatch.OnConfigReloaded()` inside `ReloadConfig`.
- **Rule for new patches:** static mutable fields are only safe if the patched
  method is proven main-thread (trace callers in the game IL first); anything
  reached from A* workers, DynamicMesh threads, LiteNet reader/writer threads,
  or Unity job workers needs its own synchronization. `AstarGraphThrottlePatch`
  uses `Interlocked` defensively even though its caller is main-thread today.

## Process model

```
7DaysToDieServer.x86_64
 └─ UnityPlayer.so (player loop, physics, threads)
 └─ Mono
 └─ Assembly-CSharp
 GameManager.Update → gmUpdate() # orchestration (631 IL)
 └─ UpdateTick() # sim core (150 IL)
 ConnectionManager.Update # LiteNetLib I/O (215 IL) - peer MonoBehaviour
 DynamicMeshManager.Update # mesh pipeline (404 IL) - peer MonoBehaviour
 GameManager.LateUpdate # ThreadManager + MeshDataManager
 PathFinderThread / other workers # off main
```

Launch flags used by stock `startserver.sh`:

```
-quit -batchmode -nographics -dedicated -configfile=serverconfig.xml
```

`get_IsDedicatedServer()` gates some client-only work, but **not everything**. Several systems still run on dedicated.

**Critical RE fact (V3.1.0; same as 3.0.1):** `ConnectionManager.Update` and `DynamicMeshManager.Update` are **not** called from `gmUpdate`. They are separate `MonoBehaviour` updates on the same Unity frame. Hijacking only `gmUpdate` does not own net or mesh.

Deep dump + phase map: [`../../7dtd-engine-research/docs/loop-gmupdate.md`](../../7dtd-engine-research/docs/loop-gmupdate.md) (regenerate with `../7dtd-engine-research/tools/` (legacy/DumpGmUpdate or src/DumpMethod)).

---

## Frame orchestration

### Unity entry

| Method | IL | Role |
|---|---:|---|
| `GameManager.Update` | 3 | Only calls `gmUpdate()` |
| `GameManager.gmUpdate` | **631** | Managers, timer, calls `UpdateTick`, save/GC side paths |
| `GameManager.UpdateTick` | **150** | World tick, entities, fall, server entity/chunk distribute |
| `GameManager.FixedUpdate` | 5 | `fixedUpdateCount++` only |
| `GameManager.LateUpdate` | 18 | `ThreadManager.LateUpdate`, platform, AIDirector debug, `MeshDataManager.LateUpdate` |
| `ConnectionManager.Update` | 215 | Protocol + `ProcessPackages` + flush + pings |
| `DynamicMeshManager.Update` | 404 | Region/mesh queues, coroutines, `DynamicMeshServer.Update` if server |

```text
Unity frame
 ├─ GameManager.Update → gmUpdate
 │ └─ UpdateTick → World / entities / fall / net distribute
 ├─ ConnectionManager.Update (packages) ← parallel peer, not child of gmUpdate
 ├─ DynamicMeshManager.Update (mesh) ← parallel peer
 └─ GameManager.LateUpdate
```

### `gmUpdate` phases (V3.1.0 ordered; gmUpdate IL still 631)

`IsDedicatedServer` checked **6** times. Exception handler around a destroy-`GameObject` queue (`Monitor.Enter/Exit`).

| Phase | Work | Dedicated notes |
|---|---|---|
| **A Prologue** | `frameCount`/`time`, pause, resolution, **`ModEvents.SUnityUpdate`**, global actions, `Physics.SyncTransforms` if paused, `LoadManager`, `PlatformManager`, invite/lock, FPS, liquid time | Resolution/UI noise mostly harmless if short |
| **B Manager chain** | Quest, Trigger, **Twitch vote/manager**, GameEvent, **PowerManager**, Party, **Vehicle/Drone**, Dismemberment, **TurretTracker**, RaycastPath, Token, Trajectory, Faction, NavObject, BlockedPlayerList, PrefabEdit, TriggerEffect, SpeedTree, **`ThreadManager.UpdateMainThreadTasks`** | Null-instance skips; Twitch/edit/speedtree often waste if present |
| **C Client UI/EAC/cursor** | AntiCheat messages, cursor, FPS cap | **Skipped** when dedicated (`brtrue` over block) |
| **D Destroy queue** | Locked list destroy | Always if queued |
| **E Game started?** | Else `GameTimer.Reset` + **ret** | No sim |
| **F Pre-sim** | **`EntityAsyncManager.Update`** (async spawn completes), `GameTimer.updateTimer`, block particles, TOD, audio, water, signs, evaporation; chunk determine/load; optional idle `ClearCaches` | Zero-player idle branches; timer uses player count on dedicated |
| **G Sim** | **`UpdateTick()`** | See below |
| **H Post** | Ground align; **`CopyChunksToUnity`** budget loop | **CopyChunks skipped** on dedicated |
| **I Save/GC/net extras** | Provider update, save world/name maps, optional **persistent player positions package**, dedicated **`GC.Collect`** under dt gate, client unload assets | GC.Collect is an explicit hitch risk |
| **J Epilogue** | StabilityViewer?, **`ModEvents.SGameUpdate`**, `GameObjectPool.FrameUpdate` | Late mod hook after sim |

### `UpdateTick` (sim core)

Entity work is **sliced across Unity frames** between game ticks:

```text
if game timer not ready for full tick && players > 0:
 World.TickEntitiesSlice() // continue prior list
 return
else:
 TickEntitiesFlush() // finish remainder
 OnUpdateTick(dt, activeChunks)
 [server] GameStateManager.OnUpdateTick // may abort
 TickEntities(dt) // rebuild list + activity; often only sets slice budget
 LetBlocksFall()
 [not dedi] SetEntitiesVisibleNearToLocalPlayer
 [server] NetEntityDistribution.OnUpdateEntities
 [server] ChunkManager.SendChunksToClients
 [server] optional SaveRandomChunks / SaveDecorations / EventPrefabs
```

---

## World tick (`World.OnUpdateTick`, 189 IL)

**Always** (before server gate):

1. Chunk add/remove callbacks, world event time
2. `WaterSplashCubes.Update`
3. `DecoManager.UpdateTick`
4. `MultiBlockManager.MainThreadUpdate`
5. `DynamicMusic.Conductor.Update` (if not editor)
6. POI uncull bookkeeping

If `!ConnectionManager.IsServer` → **return**.

**Server:**

7. `WorldBlockTicker.Tick` - scheduled blocks
8. Every **20** game ticks: walk **area-master** chunks in the active set → biome spawn data → `SpawnManagerAbstract.Update`
9. Optional prefs-driven second spawn manager update
10. `AIDirector.Tick`
11. `TickSleeperVolumes`

**Lag lever:** deco, music, splash, wide area-master spawn walk, sleepers, block ticker. Scale with loaded chunks and player spread.

---

## Entity pipeline

```text
TickEntities(dt) // on full game tick
 clear tickEntityList
 copy Entities.list except primary local player
 TickEntity(localPlayer) if any
 EntityActivityUpdate() // aiActiveScale + cloth/jiggle
 compute tickEntitySliceCount from EMA(frame gaps) + list size
 often RETURN without ticking the list

TickEntitiesSlice / Flush // this and following Unity frames
 for slice of tickEntityList:
 TickEntity(e, partialTicks)
 OnUpdatePosition / chunk membership
 OnUpdateEntity → live/AI → updateTasks (uses aiActiveScale)
```

Slice math (IL): maintains `tickEntityFrameCountAverage` (EMA 0.8/0.2). Spreads entities beyond a small base (~25 accounting) over estimated frames; `TickEntitiesFlush` forces the remainder. **Do not assume every entity runs every Unity frame.**

### Dual entity paths (authority vs Unity MB)

| Path | Driver | Content |
|---|---|---|
| **A. Authority** | `World.TickEntity` from UpdateTick slices | OnUpdateEntity → OnUpdateLive → **updateTasks (AI/path)** |
| **B. Unity Update** | `Entity`/`EntityAlive` MonoBehaviour Update if GO active | Transform, network stats, model fade; **not** primary AI |

AI/path requests are on path A. Path B may still cost main-thread time on dedicated if entity GameObjects stay enabled (no IsDedicated early-out in Entity.Update IL).

### Built-in AI LOD (`EntityActivityUpdate`, 229 IL)

Stock distance bands from IL constants (squared metres):

| Condition | `aiActiveScale` |
|-----------|-----------------|
| closest dist² **&lt; 64** (~8 m) | **1.0** full AI |
| else dist² **&lt; 225** (~15 m) | **0.3** or **0.1** (branch) |
| farther | lower scale via same stores |

Also toggles cloth/jiggle at larger radii (IL uses constants including **625** / **3025** among others).

`EntityAlive.updateTasks` subtracts `aiActiveScale` from delay and only runs full EAI when delay hits zero.

**EfficientServer** tightens bands / far skip further (config).

### Pathfinding (V3.1.0 production)

`AstarManager.Init` installs **`ASPPathFinderThread`** as `PathFinderThread.Instance` and `StartWorkerThreads()` → **`StartCoroutine(FindPaths)`** (not the OS-thread `AStarPathFinderThread`, which still exists in the binary).

| Step | Where |
|---|---|
| Request | EAI/UAI → `EntityAlive.FindPath` → `PathFinderThread.FindPath` |
| Queue | `entityWaitQueue` + `finishedPaths[entityId] = PathInfo` (per-id replace coalesces) |
| Compute | ASP coroutine **≤8/yield** → `ASPPathNavigate.GetPathTo` → `ASPPathFinder.Calculate` → **`AstarPath.StartPath`** (Pathfinding.* AB/X/Multi/Flee/Random paths) |
| Apply | `updateTasks` → `GetPath` → `PathNavigate.SetPath` / `UpdateNavigation` / pathFollow |

Blood moons spike queue depth (unbounded enqueue vs fixed drain of 8). **Admission** belongs on enqueue.
**GameTimer.ticksPerSecond = 20** (stock). AIDirector always installs BM + wandering + chunk scouts + airdrop + player/marker mgmt.

Separate from path *compute* above, `AstarManager.UpdateGraphs` runs the player-following nav-graph maintenance every tick and is the top managed section under load; EfficientServer rate-limits it via `AstarGraphThrottlePatch`. Plan and levers: [`PATHFINDING_OPTIMIZATION.md`](PATHFINDING_OPTIMIZATION.md).

### `updateTasks` vs stock AI LOD (important)

```text
aiActiveDelay -= aiActiveScale
if delay elapsed → EAIManager.Update() or UAIBase.Update() // decisions + FindPath
// ALWAYS when updateTasks runs:
GetPath → SetPath → UpdateNavigation → MoveHelper → LookHelper
```

Stock scale **throttles decision AI**, not path-follow/move helpers. EfficientServer far **skip of whole `updateTasks`** is stronger (also stops nav follow that frame).

Deep chain dump: [`../../7dtd-engine-research/docs/entity-ai.md`](../../7dtd-engine-research/docs/entity-ai.md).
Optim candidates (graded): [`OPTIMIZATION_CANDIDATES.md`](OPTIMIZATION_CANDIDATES.md).
RE dump index: [`../../7dtd-engine-research/docs/INDEX.md`](../../7dtd-engine-research/docs/INDEX.md).

### Async entity create

`EntityAsyncManager.Update` (called from `gmUpdate`) drains a queue of completed create handles on the main thread. Small IL; shows stock already uses async **creation**, not async AI.

---

## Networking (`NetPackage*`)

Hundreds of packages. High frequency:

- `NetPackageEntityPosAndRot` / `RelPosAndRot` / `EntityVelocity`
- `NetPackageChunk` / map packages
- `NetPackageSetBlock`, damage, inventory, player stats
- Dynamic mesh packages

**Per-frame pump:** `ConnectionManager.Update` (~215 IL): `ProtocolManager.Update`, per-client `ProcessPackages`, `FlushClientSendQueues`, periodic pings / `NetPackageClientInfo`.

**From `UpdateTick` (server, after entities):** `NetEntityDistribution.OnUpdateEntities`, `ChunkManager.SendChunksToClients`.

**From `gmUpdate` (server, gated):** occasional `NetPackagePersistentPlayerPositions`.

---

## Dynamic mesh

`DynamicMeshManager.Update` (~404 IL) still runs on dedicated as its **own** behaviour:

- concurrent region/item queues
- coroutines for mesh generation / region load
- `DynamicMeshServer.Update` when `IsServer`
- observer tracking; drains `DynamicMeshThread` ready collections

`MeshDataManager.LateUpdate` runs from `GameManager.LateUpdate`.

Settings knobs (`DynamicMeshSettings`): `MaxRegionLoadMsPerFrame`, `MaxRegionMeshData` / `MaxDyMeshData`, `OnlyPlayerAreas`, `PlayerAreaChunkBuffer`, `MaxViewDistance`.

EfficientServer lowers per-frame mesh budget on dedicated and prefers player-area work.

---

## Known lag drivers (ordered)

1. **Entity AI + pathfinding** under high zombie counts / blood moon (slice + `aiActiveScale` already help)
2. **Active chunk volume** (view distance, player spread)
3. **Spawn system** area-master walk every 20 ticks
4. **Dynamic mesh / deco / splash / music** background work
5. **Disk saves** / random chunk save / optional **`GC.Collect`** on dedicated
6. **Mods** that hook terrain or per-chunk generation
7. **Main-thread fan-in** - `gmUpdate` + `ConnectionManager` + `DynamicMeshManager` share one core’s frame; extra cores help secondary threads only

Host **CCD / NUMA / affinity** reduces jitter; it does not parallelize these Updates. Measure-first host checklist: [`HOST_TUNING.md`](HOST_TUNING.md).

### Conductor / hijack targets (from structure)

| Patch surface | Owns | Misses |
|---|---|---|
| Leaf (`updateTasks`, mesh settings, deco) | Local | Global admission |
| `UpdateTick` | Sim + fall + entity/chunk distribute | Manager spam, ConnectionManager, DynamicMesh |
| `gmUpdate` | Managers + timer + UpdateTick + save/GC | **Still misses** ConnectionManager + DynamicMesh peer Updates |
| Full frame | Need all three Update paths + Unity order | High maintenance |

Prefer growing admission at **TickEntities / Slice / TickEntity** over replacing all of `gmUpdate`. Details: [`SIM_PARALLELISM.md`](SIM_PARALLELISM.md) §5.6.1.

**Research (ideas):** [`SIM_PARALLELISM.md`](SIM_PARALLELISM.md), [`OPTIMIZATION_IDEAS.md`](OPTIMIZATION_IDEAS.md), scale fantasy [`SCALE_1000x10000.md`](SCALE_1000x10000.md).

---

## Type index / dumps

Stock-game RE tooling lives in the sibling [`../../7dtd-engine-research/tools/`](../../7dtd-engine-research/tools/)
(general dumpers in `src/`, legacy per-family dumpers in `legacy/`), not in this repo.

V3.1.0 dumps (regenerated after the 3.1.0 game update; historical V3.0.1 names noted inline):

- Frame narrative: [`../../7dtd-engine-research/docs/loop-gmupdate.md`](../../7dtd-engine-research/docs/loop-gmupdate.md); dumps: [`../../7dtd-engine-research/il/loop-complete-v3.1.0/`](../../7dtd-engine-research/il/loop-complete-v3.1.0/) (historical `gmUpdate-v3.0.1` name)
- Entity/AI/path/net/fall: [`../../7dtd-engine-research/docs/entity-ai.md`](../../7dtd-engine-research/docs/entity-ai.md); dumps: [`../../7dtd-engine-research/il/deep-v3.1.0/`](../../7dtd-engine-research/il/deep-v3.1.0/)
- Optim scan dumps: [`../../7dtd-engine-research/il/opt-scan-v3.1.0/`](../../7dtd-engine-research/il/opt-scan-v3.1.0/) (raw only)
- Deeper multi-subsystem dumps: [`../../7dtd-engine-research/il/deeper-v3.1.0/`](../../7dtd-engine-research/il/deeper-v3.1.0/)

Do not commit game IL or `Assembly-CSharp.dll`. Regenerate after every game update ([`DEVELOPMENT.md`](DEVELOPMENT.md)).

```bash
cd ../7dtd-engine-research/tools && ./build.sh
mono bin/legacy/DumpGmUpdate.exe "$DS/7DaysToDieServer_Data/Managed/Assembly-CSharp.dll" ../il/gmUpdate-VERSION
```

## Legal / distribution

- Own a legitimate game copy.
- Do not redistribute decompiled game IL or `Assembly-CSharp.dll`.
- Ship only your mod binaries, configs, and original notes.
- Harmony mods load through the official mod pipeline; EAC policy is the publisher’s.

## Open gaps

See [`../../7dtd-engine-research/docs/loop.md`](../../7dtd-engine-research/docs/loop.md) §14. Several former gaps closed in [`../../7dtd-engine-research/docs/closed-gaps.md`](../../7dtd-engine-research/docs/closed-gaps.md) (timer 20 Hz, AIDirector CreateComponents, net package thresholds, AstarPath). Still open: Unity script order, entity Behaviour.enabled on dedi, region serializers, native LiteNet/EAC.

## Changelog

- **2026-08-23:** Concurrency model section added (main-thread confinement audit: patch surfaces, console drain, the one background thread, reload re-basing rule).
- **2026-08-23:** Stale in-repo `tools/` dump-helper references repointed to `../7dtd-engine-research/tools/`.
- **2026-08-08:** Stale `il/*-v3.0.1/` dump links repointed to current `*-v3.1.0/` dirs (loop-complete, deep, deeper, opt-scan).
- **2026-07-16:** Optim candidates doc under `docs/OPTIMIZATION_CANDIDATES.md` (not 7dtd-engine-research/il).
- **2026-07-16:** Gap-close: ticks/sec 20, path→AstarPath, AIDirector component list, net bands.
- **2026-07-16:** loop complete map link; dual entity paths; open gaps pointer.
- **2026-07-16:** Link deeper synthesis (path drain ≤8/slice, MoveHelper, EAI rank); RESEARCH_INDEX.
- **2026-07-16:** Deep entity/AI/path: updateTasks always-on nav; ASPPathFinderThread+coroutine; EAITaskList; link entity-ai.
- **2026-07-16:** Deep `gmUpdate` / `UpdateTick` / peer Update RE from V3.0.1 Cecil dump; multi-behaviour frame model; entity slice EMA; dedicated GC.Collect; conductor targets.
- **2026-07-19:** Ownership/related docs polish.

## Related docs
| Doc | Role |
|---|---|
| [FEATURES.md](FEATURES.md) | Shipped patches |
| [HOST_TUNING.md](HOST_TUNING.md) | Host ops |
| [loop.md](../../7dtd-engine-research/docs/loop.md) | Full loop narrative |
| [engine-limitations.md](../../7dtd-engine-research/docs/engine-limitations.md) | Stock ceilings (sim, net, AI, GC) |
| [measured-scaling.md](measured-scaling.md) | Live scale |
| [OPTIMIZATION_CANDIDATES.md](OPTIMIZATION_CANDIDATES.md) | Candidates |
