# Speeding up simulation (threading and friends)

**Hub:** [`README.md`](../README.md).  
**Owns:** sim parallelism research notes.  
**Not:** stock loop map ([research loop](../../7dtd-engine-research/docs/loop.md)).


**Scope:** how the **sim** (entities, AI, path, combat, block ticks) can get faster, especially with threads. 
**Context:** stock dedicated is main-loop heavy ([`ARCHITECTURE.md`](ARCHITECTURE.md)). Extreme scale: [`SCALE_1000x10000.md`](SCALE_1000x10000.md). Idea catalog: [`OPTIMIZATION_IDEAS.md`](OPTIMIZATION_IDEAS.md). OSS survey: [`../../7dtd-engine-research/oss-tools/NOTES.md`](../../7dtd-engine-research/oss-tools/NOTES.md). 
**Not** a promise EfficientServer can do all of this via Harmony.

**This doc owns:** less-work ranking, stage model, threading models, **extracting sim off main**, **additional threading policy**, **hot-path catalog**, Amdahl, tiers A-C, EfficientServer boundary.

---

## 1. First principle: less work beats more threads

For this game class, the ranking is almost always:

```text
1. Do not simulate (hibernate / despawn / never spawn)
2. Simulate less often (LOD, tick slice, delay)
3. Simulate cheaper (simpler AI, shared path, cache)
4. Parallelize what remains (workers + safe commit)
5. Faster single-thread of the serial remainder (CCD pin, ICACHE, SoA)
```

Threading only helps **after** (1)-(3). If 90% of zombies are Full AI on the main thread, 32 cores will not save you.

Stock already does pieces of (2)-(4): `aiActiveScale`, `TickEntitiesSlice`, `PathFinderThread`. EfficientServer tightens (2) and skips dead dedicated work.

**io_uring is not on this list.** It is async disk/socket I/O for code that owns the I/O loop. It does not run EAI cheaper. Use it in host/ops tooling if at all; see [`OPTIMIZATION_IDEAS.md`](OPTIMIZATION_IDEAS.md) §3 and [`SCALE_1000x10000.md`](SCALE_1000x10000.md) §9-10 for main-sim levers vs I/O.

**OSS signal:** public mods that tried Parallel EAI / parallel net send / unbounded path workers left that code commented out (IceCoffee). Live levers are “do less.” Details: [`../../7dtd-engine-research/oss-tools/NOTES.md`](../../7dtd-engine-research/oss-tools/NOTES.md).

---

## 2. What stock sim looks like (constraint)

```text
Unity frame (V3.0.1 RE - see ARCHITECTURE + 7dtd-engine-research/docs/loop-gmupdate.md):
 GameManager.Update → gmUpdate (631 IL)
 managers / timer / EntityAsyncManager
 → UpdateTick (150 IL)
 OnUpdateTick → TickEntities prep → slices → LetBlocksFall
 server: NetEntityDistribution, SendChunksToClients
 ConnectionManager.Update (215 IL) // PEER, not called from gmUpdate
 DynamicMeshManager.Update (404 IL) // PEER
 GameManager.LateUpdate → MeshDataManager.LateUpdate

Side:
 PathFinderThread(s) // A* queue (already threaded)
 DynamicMeshThread queues // mesh workers → main drains
 Unity jobs / other pools
```

**Implication:** most **gameplay state mutation** is culturally and often technically **main-thread**. Random `Task.Run` into `EntityAlive` methods is how you get races and desync. **Hijacking only `gmUpdate` does not own net or mesh.**

### 2.1 Already off main (amplify, do not reinvent)

| System | Stock | Notes |
|---|---|---|
| Path **compute** | `PathFinderThread` / A* workers | Requests still originate on main; queue can explode under blood moon |
| Entity **temporal** slice | `TickEntitiesSlice` | Spreads work over frames, not across cores |
| Unity / engine pools | Jobs, mesh coroutines | Budget and avoid fighting the engine |

### 2.2 Still on main (the tax you feel)

| Area | Examples |
|---|---|
| World policy | Block ticker, biome spawn walk (~20 ticks), AIDirector, sleepers |
| Entity AI | `EntityActivityUpdate`, `updateTasks`, EAI, path **requests**, combat apply |
| Net | `ConnectionManager` pump, package build/send for relevant clients |
| Presentation leftovers | Dynamic mesh, deco, music/audio if not skipped |
| I/O hitches | Saves, region load (appear as main stalls) |

---

## 3. Decompose the sim into stages

A frame of sim is not one blob. Split it:

| Stage | Read | Write | Parallel? |
|---|---|---|---|
| A. Build active set / tiers | world, players | tier[], active_list | Yes (read-only world) |
| B. AI decide | snapshot positions, blackboards | intents (move, attack, path req) | Yes if pure |
| C. Path compute | nav tiles, goals | path corridors | Yes (stock already) |
| D. Apply motion | intents, collisions | positions | Careful: spatial conflicts |
| E. Combat / damage | pairs, weapons | hp, death, aggro | Often serial or bin-parallel |
| F. Block / world ticks | schedules | voxels, tile entities | Hard; many dependencies |
| G. Spawn / sleeper | budgets, POIs | new entities | Mostly serial policy |
| H. Replicate | committed state | packets | Yes (per connection) |

**Speedup = parallel A/B/C/H + smaller D/E/F**, not “thread the whole `OnUpdateLive`.”

---

## 4. Threading models that work

### 4.1 Fork-join jobs on a snapshot (best default)

```text
t0: freeze snapshot S (SoA copy or double-buffer publish)
t1: workers run stage B over index ranges [i0,i1), [i1,i2), ...
t2: join
t3: main applies intents in deterministic order (entity id)
```

| Need | Why |
|---|---|
| Immutable S for readers | No locks during AI |
| Intents only from workers | No silent world mutation |
| Deterministic apply order | Same combat on replay / less heisenbugs |
| No Unity API in workers | Main-thread sticky objects |

**Speedup:** near-linear on stage B until memory bandwidth or commit stage dominates.

### 4.2 Spatial bins (parallel where agents do not touch)

```text
Partition grid into tiles
For each tile in parallel:
 resolve AI + combat for agents whose bounding box is interior
Serial pass:
 border agents (span two tiles) + cross-tile attacks
```

Good when fights are **localized**. Bad when everyone piles one POI (one hot bin).

### 4.3 Pipeline parallelism (overlap stages)

```text
While frame N commits D/E:
 workers already run B for frame N+1 on S_N
 path workers drain queue
 net encodes frame N-1
```

Hides latency; needs careful double/triple buffering. Does not reduce total sim work.

### 4.4 Existing path workers (amplify, do not replace)

Stock pathfinding is already off-thread. Speedups:

- Cap queue; priority by alert/distance 
- Coalesce same goal 
- Hierarchical / flow field for hordes 
- More workers only if APM shows queue wait **and** idle cores **and** nav is not lock-bound 

Throwing threads at a lock-serialized A* grid can make things **slower**.

### 4.5 Tick frequency split (temporal multithreading-ish)

Not classic threads, but huge:

| System | Rate |
|---|---|
| Player + melee contact | 20-30 Hz |
| Full zombie AI | 10-15 Hz |
| Medium AI | 2-5 Hz |
| Far | 1 Hz |
| Hibernate | 0 |

Implement with **phase groups**: each frame only a stripe of entities is due (`id % N == frame % N`). Stock slice is a cousin of this.

---

## 5. Can we extract most sim from the main loop and multithread it?

### 5.1 Short answer

| Goal | Stock + Harmony (EfficientServer) | Custom dedicated / rewrite |
|---|---|---|
| Extract **most** sim off main | **No** | **Yes** (design target for 1k×10k) |
| Multithread that extracted sim | **No** (not safely at scale) | **Yes**, with snapshot → intents → commit |
| Drain main by **doing less** | **Yes** (primary strategy) | Still mandatory before threads win |

**Extracting most sim and making it multithreaded is the right endgame architecture.** It is **not** an EfficientServer milestone. Mods can **starve** the main loop of work; they cannot **evacuate** most stock sim onto workers safely.

### 5.2 What “extract most sim” means

Today:

```text
Unity main loop
 → gmUpdate
 → world tick (blocks, spawn, AIDirector, sleepers, …)
 → TickEntities (LOD + slice → OnUpdateLive → EAI → path requests)
 → ConnectionManager
 → DynamicMesh / managers
 PathFinderThread(s) ← compute only, already off main
```

Target (rewrite-shaped):

```text
Thin host / main:
 net decode, commit results, net encode, Unity tax you cannot kill

Sim workers (fixed pool):
 A build active set / tiers
 B AI decide → intents only
 C path (stock already partial)
 D/E motion/combat with strict rules
 (F/G often stay serial or highly constrained)
```

That is **fork-join + command buffer**, not `Task.Run` wrapping `EntityAlive.OnUpdateLive`.

### 5.3 Why stock sim cannot just be pulled off main

| Constraint | Effect |
|---|---|
| One shared authority world | Chunks, nav, claims, sleepers, integrity, entity lists |
| Main-thread culture | Unity + much game code assume main; worker calls race or crash |
| Fat OOP entities | Not job-batchable without SoA / new representation |
| Determinism / MP | Parallel apply without fixed order → desync / heisenbugs |
| Coupled systems | AIDirector, vehicles, drones, quests, TE, falling blocks, inventory |
| Net reflects commit | Cannot replicate mid-frame partial mutation |

**OSS proof:** IceCoffee `PerformanceTuning` tried Parallel EAI, Parallel SendPackage, concurrent path `Task.Run`, async saves; the whole folder is **commented out**. Parallelizing stock methods in place is a graveyard. You must change **data ownership and stage boundaries**.

### 5.4 Stages that can leave main (design-correct)

Same table as §3, restated as extraction policy:

| Stage | Extract + parallel? | Condition |
|---|---|---|
| A. Active set / tiers | **Yes** | Read-only snapshot |
| B. AI decide | **Yes** | Pure functions → intent buffer only |
| C. Path compute | **Yes** | Already; add admission / hierarchy |
| D. Apply motion | **Partial** | Spatial conflicts; often serial or bin + border |
| E. Combat | **Often serial** | Causality |
| F. Block / TE | **Hard** | Dependency graph |
| G. Spawn / sleeper | **Mostly serial** | Policy / budgets |
| H. Replicate | **After commit** | Per-connection encode can overlap next sim |

**Speedup = parallel A/B/C/H + smaller D/E/F.**

### 5.5 Minimal architecture that works (rewrite)

```text
t0 Publish snapshot S (SoA or double-buffer; no live Entity* in workers)
t1 Workers: stage B over index ranges → intent buffer
t2 Join
t3 Single commit: apply intents in entity-id order
t4 Path workers: drain admitted requests
t5 Serial residuals: combat/block that need world locks
t6 Net: encode committed state
```

Requirements:

- Workers never call Unity or mutate world 
- Intents only from workers 
- Deterministic apply order 
- Hot zombie state as **SoA**, not 10k class graphs 
- Fixed worker pool (never thread-per-entity)

### 5.6 Hybrid ideas (middle ground)

| Hybrid | Feasibility | Notes |
|---|---|---|
| Path admission + heavier LOD only | **Near** | EfficientServer lane |
| Snapshot positions; parallel “should this zed think?”; main runs Full EAI for survivors | **Research** | Limited win; still pays Full EAI on main |
| **Hijack `gmUpdate` / main tick; reorchestrate; reuse stock methods** | **Mid / high risk** | Custom conductor; see §5.6.1 |
| Reimplement far-AI subset off-thread | **Mid/high risk** | Dual AI paths, desync, update rot |
| Separate process sim + thin Unity host | **Far / product** | NAIWAZI-shaped; years; state ownership hell |
| Full custom dedicated | **Far / path A** | Real “most sim multithreaded” |

There is **no** clean “flip a switch, 80% of `gmUpdate` on 16 cores” for this binary.

### 5.6.1 Hijack the main loop, reorchestrate, reuse stock methods

**Idea:** Harmony-prefix (or replace) `GameManager.Update` / `gmUpdate` (and possibly `World.OnUpdateTick` / `TickEntities`), skip the stock body, and run **our** tick schedule that still calls **original** methods (`TickEntity`, path enqueue, package send, block ticker, …) in a better order with budgets, tiers, and maybe some worker prep.

```text
Unity player loop (unchanged)
 → GameManager.Update
 → [Harmony Prefix] EfficientServer.Orchestrator.Tick
 skip original gmUpdate body (or call selected pieces)
 1. net pump / input (stock or thin wrap)
 2. build active set (ours; may use stock entity lists)
 3. optional: parallel pure prep on snapshot
 4. serial: call stock methods for Full-tier only
 5. path admission into stock PathFinderThread
 6. mesh/deco budgets (stock managers, throttled)
 7. commit / late net
```

This is **not** a greenfield dedicated. It is a **custom conductor** over stock instruments.

#### What reusing stock methods actually buys

| Reuse | Benefit | Catch |
|---|---|---|
| `EntityAlive` AI / combat / inventory methods | No reimplement of game rules | Still main-thread sticky; still OOP cost |
| Stock pathfinder enqueue/API | Keep nav correctness | Must not reenter unsafely from workers |
| NetPackage factories / `ConnectionManager` | Stay wire-compatible | Send still serial-ish; interest is stock |
| World block / TE APIs | Saves and multiplayer authority stay valid | Hard to parallelize |

You keep **correctness and protocol** longer than a rewrite. You do **not** magically get SoA or free multi-core AI.

#### Spectrum of hijack depth

| Depth | What you own | Skip stock body? | Realistic gain |
|---|---|---|---|
| **A. Surgical** (today) | Prefixes on leaf methods (LOD, mesh, skip deco) | No | Proven EfficientServer shape |
| **B. Conductor on main** | Order + budgets: which entities/systems run this frame | Partial replace of tick entity list / spawn walk | Medium: global **admission** beats scattered leaf patches |
| **C. Conductor + worker prep** | Snapshot → parallel tier/filter → main calls stock Full AI | Replace large part of `TickEntities` path | Research; win only if Full set shrinks a lot |
| **D. Full gmUpdate replace** | Reimplement orchestration of every subsystem call | Yes, entire body | High break risk every TFP patch; must not drop silent work (quests, vehicles, drones, …) |

**B** is the interesting step up from current EfficientServer. **C** is the honest form of “extract a bit + reuse methods.” **D** is a maintenance trap unless you treat it as a product and re-RE every game update.

#### Why “call stock from workers” usually fails

Stock methods typically:

- touch Unity (`Transform`, physics, coroutines) 
- touch shared managers without locks 
- assume single-threaded reentrancy (static/frame caches) 
- allocate and mutate world immediately (not intent-based)

Safe reuse pattern:

```text
Workers (optional): pure C# on copied floats/ids only
Main: original.InstanceMethod(...) for anything that mutates the world
```

If the stock method mutates the world, it stays on main. Hijack does not remove that law.

#### What a conductor *can* do efficiently (reuse-heavy)

1. **Global AI budget** - hard max Full-tier EAI calls per tick; rest hibernate or stripe (stock `updateTasks` only for admitted set). 
2. **Unified path admission** - all path requests funnel through one gate before stock path API. 
3. **Phase groups** - entity id % N == frame % N; still stock `TickEntity`. 
4. **Skip whole subsystems** on dedicated when config says so (already partly leaf-based). 
5. **Reorder** cheap prep before expensive AI so early-outs hit first (if you own the list walk). 
6. **Optional pure prep jobs** - closest-player distance, tier bits, “in any player bubble?” on SoA copy; main only runs stock AI when tier says so.

Real win: **reuse stock for correctness of Full AI; do not call stock for most zombies at all.**

#### Failure modes of hijacking `gmUpdate`

| Failure | Why |
|---|---|
| **Silent subsystem skip** | Dense method; miss vehicles/drones/quests/one dedicated branch → subtle MP bugs |
| **Double update** | Prefix runs custom + falls through to original |
| **Update rot** | Every V3.x build reshuffles `gmUpdate` IL; full replace is permanent RE tax |
| **Desync** | Different order of damage/block apply vs stock expectations |
| **Harmony war** | Other mods also patch Update; order/priority hell |
| **False multithreading** | Tasks that call stock methods “for speed” → races (IceCoffee graveyard) |
| **Worse frame time** | Orchestrator overhead + incomplete skip of stock work |

#### Relation to EfficientServer scope

| Approach | In scope? |
|---|---|
| Leaf Harmony (LOD, mesh, skips) | **Yes** (current) |
| Conductor **B**: own entity tick list / budgets, still main-thread stock calls | **Maybe later**, evidence-gated, large fidelity matrix |
| Conductor **C**: pure worker prep + main stock Full AI | **Research only** until snapshot purity proven |
| Full **D** replace of `gmUpdate` | **Out** unless mission becomes “custom host for stock rules” |
| Worker threads calling stock `EntityAlive` methods | **Reject** |

#### Verdict

```text
Hijack main loop + reorchestrate + reuse stock methods
 = valid name for a "custom conductor" hybrid
 = NOT free multi-core sim
 = win = NOT calling stock for most entities + budgets/order
 = full body replace is high maintenance; grow from leaf patches
 toward owning TickEntities admission before owning all of gmUpdate
```

**Recommended evolution if we ever go beyond leaf patches:**

```text
1. Keep leaf LOD/mesh (today)
2. Own admission at TickEntities / updateTasks (list filter + path gate)
3. Optional SoA snapshot + parallel tier tags only
4. Only if (2)+(3) solid: Prefix UpdateTick or TickEntities path
 (closer to sim than full gmUpdate; see ARCHITECTURE)
5. Full gmUpdate replace still misses ConnectionManager + DynamicMeshManager
 peer Updates; patch those separately if you need full frame control
6. Never call stock world mutators from workers
7. Preserve or consciously replace entity slice/EMA behavior (stock spreads
 ticks across Unity frames between game ticks)
```

### 5.7 Amdahl if you extract 50% of the frame

Suppose after LOD:

- 50% of frame = AI you hope to parallelize 
- 25% = block/TE/spawn/commit (serial) 
- 15% = net 
- 10% = Unity/mesh tax 

Perfect 8-way on the 50%:

```text
speedup ≈ 1 / (0.50 + 0.50/8) ≈ 1.6×
```

If only 20% of the frame is parallelizable, ~1.2×. 
**Cutting AI work in half** (hibernate/LOD) often beats an 8-core AI job you cannot safely build on stock types. Threads amplify a **small** hot set; they do not replace architecture.

### 5.8 EfficientServer policy on extraction

```text
Do: shrink main-loop work (LOD, skip, caps, budgets, scoped walks)
Do: admit/prioritize work that already has workers (path)
Do: if growing past leaf patches: own TickEntities admission (conductor B)
 before full gmUpdate replace (conductor D)
Don't: Parallel.ForEach EAI / OnUpdateLive / SendPackage
Don't: unbounded Task.Run path workers
Don't: call stock world-mutating methods from workers “to reuse code”
Don't: claim “sim extracted” until SoA + intents + commit exist
Don't: full gmUpdate body skip without a subsystem checklist + per-update RE
```

```text
Main loop budget
 = work you still choose to run
 + commit of anything workers produced
 + net + Unity tax

Goal: shrink first term aggressively
 only then grow workers for a pure remainder
```

---

## 6. Additional threading: decision policy

### 6.1 When more threads are on the table

```text
APM: main busy, path workers idle, path queue deep
 → path admission first
 → then maybe worker count / affinity (Ops/Near)

APM: main busy in entity AI, workers idle
 → less AI (LOD/skip/caps), NOT more EAI threads

APM: main hitchy + disk latency
 → storage / save schedule / less region thrash
 → NOT io_uring-in-mod, NOT more sim threads

APM: main busy in mesh/deco
 → budgets / skip
 → not a new thread pool in EfficientServer
```

**Rule:** additional threading only when APM shows **idle cores + a thread-safe queue that is the bottleneck** (almost always **path**). Everything else on main sim is **cut work first**.

Host CCD/NUMA/affinity ([`HOST_TUNING.md`](HOST_TUNING.md)) protects **existing** main + path threads (jitter). That is not “more threading.”

### 6.2 Threading idea scorecard

| Idea | Tier | Verdict |
|---|---|---|
| Path admission / priority / coalesce | **Near** | Best threading-adjacent win |
| More path workers / affinity | **Ops/Near** | Only if queue wait + idle cores + not lock-bound |
| Parallel read-only active-set build | **Mid** | Needs pure snapshot; rare in stock types |
| Jobified AI intents + main commit | **Far** (rewrite B/C) | Correct shape; not Harmony v1 |
| Spatial bin combat | **Far** | Hot-bin collapse under megafights |
| Pipeline overlap (sim N / net N-1) | **Far** | Needs buffering you own |
| Parallel.ForEach EAI | **Reject** | Unity/world safety; OSS abandoned |
| Parallel SendPackage | **Reject** | Connection safety; prefer fewer packages |
| Unbounded Task.Run per path | **Reject** | Stampede under BM |
| Thread-per-zombie | **Reject** | Overhead + races |
| General “sim thread pool” over stock methods | **Reject** | Extraction without data model |
| io_uring for AI tick | **Reject** | Wrong layer ([`OPTIMIZATION_IDEAS.md`](OPTIMIZATION_IDEAS.md) §3) |

### 6.3 Practical map

```text
 ┌─────────────────────────────┐
 │ Prove with APM + loadgen │
 └──────────────┬──────────────┘
 ┌───────────────────────┼───────────────────────┐
 │ │ │
 AI / entity CPU Path queue depth Disk hitch
 │ │ │
 LESS WORK ADMISSION HOST + less thrash
 LOD / skip / caps then maybe no io_uring-in-mod
 (no EAI threads) path workers no more sim threads
 │ │
 └───────────┬───────────┘
 │
 Mesh / deco / spawn walk
 │
 Budgets / scope (main-thread cuts)
```

---

## 7. Hot-path catalog (optimize these)

Grounded in [`ARCHITECTURE.md`](ARCHITECTURE.md) lag list and EfficientServer patches. 
**Threading column:** whether extra threads help *this* path.

### 7.1 Entity AI + path (usually #1)

| Hot path | Stock | Optim (prefer) | Threads? | EfficientServer |
|---|---|---|---|---|
| `EntityActivityUpdate` | Bands → scale 1.0 / 0.3 / 0.1 | Tighter bands, lower far scale | No | **Shipping** `AiLodPatch` |
| `EntityAlive.updateTasks` | Delay then EAI + path follow | Skip far non-alert | No | **Shipping** `UpdateTasksLodPatch` |
| Path **requests** | Enqueue via `EntityAlive.FindPath` → `PathFinderThread.FindPath` (per-entityId dict coalesce) | Cap / priority / drop far | Protect queue | **Candidate** admission |
| Path **compute** | V3.0.1: **ASPPathFinderThread** + **coroutine**; MoveNext drains **≤8** paths then yields; AStar OS thread exists but not installed by Init | Hierarchy / shared goals (Mid/Far); enqueue admission vs fixed drain | Off entity tick | Research / Ops |
| Path **apply** | Every `updateTasks`: GetPath + nav + move/look even when EAI delayed | Far skip whole updateTasks (stronger); or leave stock | Main | ES far skip |
| **MoveHelper** | `EntityMoveHelper.UpdateMoveHelper` **1236 IL** every updateTasks | Far skip; measure under BM | Main | Research |
| **SpawnUpdate** | `SpawnManagerBiomes.SpawnUpdate` **441 IL** / 20 ticks / area-master | Player-near scope | Main | Candidate |
| **Net interest emit** | `NetEntityDistributionEntry.updatePlayerList` **509 IL** | Rate LOD (Mid) | Main | Research |
| **Deco / splash** | 330 / 185 IL always on world tick | Dedicated skip flags | Main | Candidate |
| Closest-player for LOD | Per-entity cost | Cache TTL for far tiers | No | **Candidate** |
| Blood moon density | Max zombies + director | Config + Full-tier budget | No | Config + APM |

Highest ROI: **fewer requests + cheaper AI**, not a second path engine.

### 7.2 World / chunk volume

| Hot path | Optim | Threads? |
|---|---|---|
| Player spread × view/sim distance | Server knobs, scenario design | No |
| Loaded chunk union | Cluster tests; product issue at 1k | No |

### 7.3 Spawn and sleepers

| Hot path | Optim | Threads? | Tier |
|---|---|---|---|
| `SpawnManagerBiomes` (~every 20 ticks) | Scope to player-near chunks | No | **Near/Mid** candidate |
| `TickSleeperVolumes` | Distance/alert gate | No | **Near** candidate |

### 7.4 Dynamic mesh / dedicated leftovers

| Hot path | Optim | Threads? | EfficientServer |
|---|---|---|---|
| `DynamicMeshManager` | Per-frame budgets, player areas | Budget, not new pool | **Shipping** `DynamicMeshBudgetPatch` |
| Music / splash / env audio / cloth | Skip on dedicated | No | **Shipping** optional `DedicatedSkipPatch` |

### 7.5 Blocks, falling, TE

| Hot path | Optim | Threads? | Tier |
|---|---|---|---|
| Falling-block storms | Optional block → air | No | **Near** research (fidelity) |
| Power / workstation TE | Idle early-out if APM shows TE | No | **Near/Mid**; content-sensitive |
| `WorldBlockTicker` | Measure first | No | Usually after AI |

### 7.6 Networking

| Hot path | Optim | Threads? | Tier |
|---|---|---|---|
| High-rate entity pos packages | Interest / rate LOD; measure per connection | Prefer fewer packages | **Mid** research |
| `ConnectionManager` pump | Same | Parallel send **Reject** | - |
| Join / mod bulk transfer | HTTP offload (MVirus class) | Ops / separate channel | Scenario isolation |

### 7.7 I/O and ops noise

| Hot path | Optim | Threads? |
|---|---|---|
| World/player save | Schedule, NVMe, APM blackout | Async only with snapshot (**Mid**) |
| Backup zip | Ops schedule | Host tools may use io_uring |
| Map render / web pollers | Off during baselines | No |
| Twitch/vote leftovers | Disable if unused | No |
| Admin mega-mods on measure box | Clean dedicated | No |

### 7.8 Evidence order (what to do next)

```text
1. APM deep capture under fixed loadgen (BM + optional spread)
2. If AI-dominated → tighter LOD + path admission
3. If spawn/chunk walk → spawn scoping
4. If mesh high → budget retune only
5. If path workers idle + main AI burns → cut AI (do not add EAI threads)
6. If path queue deep + workers saturated → admission first, then worker count/affinity
```

Metrics that decide the branch:

- Entities in Full / Medium / Far / Hibernate 
- Path queue depth and wait 
- Main time: entity vs path wait vs mesh vs spawn vs net 
- Packages **per connection** and aggregate outbound 
- Resident chunk count 
- TE/power stacks if content packs present 
- Save windows excluded from A/B 

---

## 8. What is hard to speed up with threads

| Work | Why serial pressure stays |
|---|---|
| Voxel edits / structural integrity | Graph dependencies, save consistency |
| Inventory / trading | Strict causality |
| Many Unity/engine calls | Main-thread affinity |
| Global singleton managers | Hidden shared mutables |
| GC storms | Parallel alloc makes it worse |

If APM says the hot stack is `OnUpdateLive` → deep managed graph with allocs, **first** cut work and allocs; threads second.

---

## 9. Data layouts that make threads actually win

Threads without layout changes often stall on cache:

| Layout | Effect |
|---|---|
| **SoA** hot fields | Workers scan `pos_x[]` linearly |
| **Dense active index** | Skip hibernators entirely |
| **Spatial hash** | Build jobs by cell ranges |
| **No virtual calls in hot AI** | Direct function per tier |
| **Pre-sized intent buffers** | Per-worker slab, no concurrent List growth |

OOP `List<Entity>` + virtual `Update` on main thread is the slow baseline.

Without SoA + dense active index, “extract most sim” never becomes real multi-core AI (see §5).

---

## 10. Concrete “sim faster” levers (ordered)

### Tier A: works in stock-shaped servers (Harmony / config)

| Lever | Mechanism |
|---|---|
| Stricter AI LOD | Fewer `updateTasks` / EAI runs |
| Path admission | Fewer A* jobs |
| Skip dedicated-only presentation | Free main-thread ms |
| Mesh budgets | Free main-thread ms (not pure AI, same frame) |
| Spawn walk scoping | Less per-20-tick chunk iteration |
| Sleeper distance gate | Less volume work |
| Avoid alloc in patched hot paths | Less GC hitch |

This is EfficientServer’s lane. Hot paths: §7. Shipping vs candidates: §7 tables.

### Tier B: deep mod / partial rewrite

| Lever | Mechanism |
|---|---|
| Intent-based AI jobs | Stage B on workers (§5.5) |
| Shared group paths / flow fields | Less path CPU |
| Closest-player cache + spatial query | Less O(n) targeting |
| Stripe tick scheduling | Temporal LOD |
| Reduce Unity touches in AI | More code legal on workers |
| Far-AI reimplementation off-thread | Dual path risk (§5.6) |

### Tier C: greenfield dedicated

| Lever | Mechanism |
|---|---|
| Full SoA + phased commit | Real multi-core sim (extract §5) |
| Spatial bin combat | Parallel D/E |
| Custom nav hierarchy | Path at 10k agents |
| Hibernation store | 10k cap with tiny CPU |
| Own region streaming / async disk | I/O layer you control (still not “AI via io_uring”) |

---

## 11. How much speedup can threading give?

Rough model:

```text
Frame sim time ≈ Serial + ParallelWork / (cores × efficiency)
```

| If serial share is… | 8 cores @ 70% eff. | Ceiling |
|---|---|---|
| 80% (typical Unity game loop) | ~1.2× | Amdahl wall |
| 40% | ~2.5× | Useful |
| 15% (data-oriented redesign) | ~4-5× | Good |

So: **threading without reducing serial `gmUpdate` body often yields 10-30%**, not 8×. 
**LOD + budgets** can yield 2-10× on blood-moon-like loads by deleting work. 
Combine: delete work until the remainder is parallelizable, then jobify that remainder. 
See also worked example in §5.7 when “50% of frame extracted.”

---

## 12. Myths

| Myth | Reality |
|---|---|
| Thread per zombie | Slower; races |
| Extract most sim with Harmony | Starve main, do not evacuate stock methods (§5) |
| Hijack gmUpdate = free multi-core | Conductor can budget/reuse stock on **main**; workers only pure prep (§5.6.1) |
| Call stock EntityAlive from worker threads | Reuse is for main-thread commit; methods are not job-safe |
| More pathfinder threads always help | Not if grid lock or main thread is the wait |
| `async`/`Task` everywhere in Harmony | Easy to break determinism and Unity rules |
| Parallel.ForEach on EAI/SendPackage | Abandoned open-source; unsafe |
| io_uring speeds sim | Sim is CPU/state, not disk syscalls |
| NUMA/CCD replaces sim design | Only protects the serial core ([`HOST_TUNING.md`](HOST_TUNING.md)) |
| Admin panel / gateway = more sim capacity | Ops/net product; does not multiply `gmUpdate` |
| Manual GC.Collect optimizes tick | Hitch generator |

---

## 13. Practical recipe (if designing sim speed)

```text
1. Profile: is hot path AI, path wait, spawn walk, mesh, net, TE, or GC? (§7.8)
2. If AI count: LOD + hibernate + path cap (Tier A)
3. If path queue: coalesce, priority, hierarchy (Tier A/B); then workers
4. If main serial but workers idle: do NOT Parallel.ForEach stock AI;
 either cut work or (rewrite) extract pure AI to jobs + intent commit (Tier B/C)
5. If still bound at product scale: SoA + active set + spatial jobs (Tier C / §5)
6. Only then: more cores / CCD pin for the remaining serial commit
```

APM questions that decide the branch:

- Entities in Full tier per tick 
- Path queue depth / wait time 
- `gmUpdate` vs worker CPU 
- GC pause frequency 
- Chunk resident count (sim adjacency, not only net) 
- Packages per connection 
- TE/power if content packs present 

---

## 14. Relation to EfficientServer

| In scope today | Out of scope |
|---|---|
| LOD, task skip, dedicated skips, mesh budgets | Full jobified AI pipeline / extract most sim |
| Soft-fail Harmony patches | Replacing entity representation with SoA |
| Config knobs + evidence loop | Thread-per-entity; Parallel EAI/send |
| Path admission / spawn scope / cache candidates | Custom dedicated, gateway process, io_uring-in-DLL |

Next Harmony candidates that *move toward* a world where threads could matter **without** claiming multi-core AI yet: see graded list in [`OPTIMIZATION_CANDIDATES.md`](OPTIMIZATION_CANDIDATES.md) and summary in [`OPTIMIZATION_IDEAS.md`](OPTIMIZATION_IDEAS.md) §5.

---

## 15. Doc map (ideas live where)

| Topic | Primary doc |
|---|---|
| Stock frame / lag drivers RE | [`ARCHITECTURE.md`](ARCHITECTURE.md) |
| Idea catalog, rejects, io_uring | [`OPTIMIZATION_IDEAS.md`](OPTIMIZATION_IDEAS.md) |
| Graded candidates / APM probes | [`OPTIMIZATION_CANDIDATES.md`](OPTIMIZATION_CANDIDATES.md) |
| This file: threads, extract, hot paths | **Here** |
| 1k×10k data structures / single-host fantasy | [`SCALE_1000x10000.md`](SCALE_1000x10000.md) |
| Host pin / storage | [`HOST_TUNING.md`](HOST_TUNING.md) |
| OSS ecosystem evidence | [`../../7dtd-engine-research/oss-tools/NOTES.md`](../../7dtd-engine-research/oss-tools/NOTES.md) |
| Shipping feature groups | [`FEATURES.md`](FEATURES.md) |

---

## Changelog

- **2026-07-16:** Hot-path table: ASP coroutine pathfinder; updateTasks always-on nav vs EAI delay.
- **2026-07-16:** Stock frame sketch: peer ConnectionManager/DynamicMeshManager Updates; pointer to gmUpdate STRUCTURE dump.
- **2026-07-16:** §5.6.1 hijack gmUpdate / reorchestrate / reuse stock methods (conductor spectrum A-D).
- **2026-07-16:** §5 extract-most-sim; §6 additional threading policy; §7 hot-path catalog; renumber; myths/recipe/EfficientServer boundary expanded; doc map.
- **2026-07-16:** Note io_uring is off the sim ranking; OSS parallel-AI abandonment pointer to scale/OSS notes.
- **2026-07-16:** Initial sim parallelism note: stages, job models, Amdahl, tiers A-C, myths, recipe.
