# Optimization idea map (research, not roadmap)

**Owns:** idea backlog (not acceptance).  
**Not:** candidates with evidence ([OPTIMIZATION_CANDIDATES](OPTIMIZATION_CANDIDATES.md)).


**Status:** brainstorming grounded in [`ARCHITECTURE.md`](ARCHITECTURE.md) and V3.0.1 dedicated RE.

**Not** a commitment to implement. Ideas must pass: evidence from APM + loadgen, sim fidelity, and the project boundary (reviewed Harmony/config only; no second server).

EfficientServer today: tighter AI LOD, distant task skip, dedicated presentation skips, dynamic mesh budgets. Everything below is **possible direction**, ranked by realism.

**Companion docs (all ideas are split by ownership):**

| Doc | Owns |
|---|---|
| **This file** | Lever catalog, io_uring, philosophy, decision rules, OSS |
| [`OPTIMIZATION_CANDIDATES.md`](OPTIMIZATION_CANDIDATES.md) | **Graded A/B/C candidates**, APM probes, Harmony targets, experiment order |
| [`SIM_PARALLELISM.md`](SIM_PARALLELISM.md) | Extract sim off main, threading policy, hot-path catalog, Amdahl |
| [`SCALE_1000x10000.md`](SCALE_1000x10000.md) | 1k×10k data structures / single-host fantasy |
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | Stock frame RE summary |
| [`../../7dtd-research/docs/loop.md`](../../7dtd-research/docs/loop.md) | Full dedicated loop RE map (evidence) |
| [`../../7dtd-research/oss-tools/NOTES.md`](../../7dtd-research/oss-tools/NOTES.md) | IceCoffee / ServerTools / ecosystem |

---

## 1. Constraints of this binary

| Fact | Implication |
|---|---|
| Unity headless + Mono, not IL2CPP | Managed GC; limited control of engine job system |
| `gmUpdate` orchestrates sim on the main path | “Use all cores for zombies” does not free the main thread unless work is **queued off** and results **merged safely** |
| Stock already has AI LOD + `TickEntitiesSlice` + pathfinder threads | Best first moves are **less work** and **better budgets**, not more threads |
| Shared world: blocks, navgrid, sleeper volumes, claims | Parallel entity AI races without locks or deterministic phases |
| Hundreds of `NetPackage*` types | Rewriting net stack is a multi-year project |
| Saves / regions on disk | Async I/O can help **hitches**, not AI CPU |

---

## 2. Thread-per-zombie (and friends)

### Would it be faster or slower?

**Almost certainly slower or unsafe** if done as “one OS thread per zombie.”

| Cost | Why |
|---|---|
| Thread create/schedule | Thousands of zombies → thousands of stacks and wakeups; cache thrash |
| Locking shared world | Pathing and block queries need voxel/nav data; coarse locks serialize you back to one core |
| Determinism / desync | Different interleavings → different combat outcomes; MP clients assume server authority consistency |
| Unity/Mono affinity | Much game state is main-thread sticky; random worker calls into Unity APIs explode |

Stock already moves **pathfinding** to `PathFinderThread` / A* workers and **slices** entity ticks across frames. That is the right shape: **bounded worker pools + main-thread apply**, not 1:1 threads.

### Ecosystem evidence (open source)

Public mods rarely ship validated parallel AI. The open IceCoffee ServerKit tree has a full `PerformanceTuning/` folder that is **entirely commented out**: concurrent `PathFinderThread` subclass with unbounded `Task.Run` per path, `Parallel.ForEach` over `EAITaskList` and `ConnectionManager.SendPackage`, async player save, off-thread block packages. Live optim-adjacent code there is **less work** (falling block → air, crude zombie cap), not more threads. NAIWAZI markets entity threads / gateway split in closed binaries we did not measure. See [`../../7dtd-research/oss-tools/NOTES.md`](../../7dtd-research/oss-tools/NOTES.md).

**Reading:** the community keeps rediscovering “parallel the hot path,” then abandons it in-tree or sells it closed. That reinforces **admission + LOD first**, threads only with fixed pools, safe commit, and APM proof.

### Variants that *could* help (hard)

| Idea | Shape | Risk |
|---|---|---|
| **Jobified AI batch** | Fixed N workers process AI for entity ranges; write intents; main thread commits | High: requires pure AI functions without Unity main-thread calls |
| **Spatial shards** | World divided into grids; each shard ticks entities that only touch local state | Extreme: cross-shard combat, sound, quests, vehicles |
| **Speculative far-AI** | Far zombies only on workers; near zombies main-thread only | Medium: still need safe read snapshots of world |
| **Better path queue** | Cap path requests, coalesce targets, priority by player distance | **Realistic Harmony/config** adjacent to current LOD work |
| **IceCoffee-style concurrent pathfinder** | Replace stock path thread with concurrent queue + many `GetPathTo` tasks | **Reject as-shipped**: stampede + race risk; keep only the idea of a **bounded** queue |

**Verdict:** Do not pursue thread-per-zombie or Parallel.ForEach EAI/send. Pursue **less AI**, **cheaper AI**, **better path request caps**, and only later **batch workers** if APM shows path/AI queue depth with idle cores.

---

## 3. io_uring (and async I/O)

### What io_uring is good for

Linux completion-based async I/O: many reads/writes with fewer syscalls and less thread-per-op overhead. Helps **storage and sometimes sockets** when *your* code owns the I/O loop.

### What 7DTD actually does

| I/O class | Owner | io_uring realistic? |
|---|---|---|
| Region / save / prefab disk | Game C# + Unity / Mono file APIs | **Not without rewriting their I/O path** |
| LiteNetLib UDP | Managed sockets inside game | Would need replacing ConnectionManager pump (not a small mod) |
| Dynamic mesh / asset reads | Engine | Outside mod surface |
| **Host-side** capture, log shipping, backup | Our tools | **Yes** (APM/ops), irrelevant to tick rate |
| **FUSE / custom userdata proxy** | Hypothetical | Academic; high complexity, EAC/support nightmare |

### When disk *does* matter

ARCHITECTURE lists saves and large worlds as lag drivers. Symptoms: hitch spikes correlated with APM block/VFS latency, not steady AI CPU.

**Practical stack (no io_uring in-process):**

1. Fast local disk for `UserDataFolder` ([`HOST_TUNING.md`](HOST_TUNING.md))
2. Reduce save pressure if knobs exist; avoid pathologically large worlds for the player count
3. Do not thrash region load with insane view distance
4. Optional: external backup tools using io_uring (ops), not the game DLL

**Verdict:** io_uring is a **poor fit inside EfficientServer**. It is a **good fit for host tooling** if we ever optimize APM export/backup. For the game, treat I/O as **placement + less work + measure**, not a new syscall backend.

---

## 4. Idea catalog by lever

Legend: **Near** = fits EfficientServer-style Harmony/config · **Mid** = large mod, high break risk · **Far** = rewrite / engine · **Ops** = host only

### 4.1 Do less sim (highest ROI class)

| Idea | Tier | Notes |
|---|---|---|
| Tighter AI LOD / alert-aware skip | **Near** | Already shipping; tune with APM |
| Cap pathfind requests per frame / per blood moon | **Near** | Coalesce “all zeds path to same player”; inverse of IceCoffee unbounded path workers |
| Skip or slow `SpawnManagerBiomes` walk far from players | **Near/Mid** | Every-20-tick cost scales with chunks |
| Sleeper volume tick budget by player distance | **Near** | Careful: quests/POI clears |
| Deco / music / cloth already skippable on dedicated | **Near** | Partially done |
| Lower imposter / dynamic mesh for non-observers | **Near** | Partially done |
| Entity tick slice: prefer players’ neighborhoods | **Mid** | Change fairness of who gets sim time |
| Disable or rate-limit Twitch/vote leftovers if present | **Near** | ARCHITECTURE notes waste if unused |
| Falling block → air (optional) | **Near** | ServerTools + IceCoffee live; kills collapse physics/entity spikes; fidelity trade; flag + APM only |
| Entity list hygiene (items / Y&lt;0 / junk) | **Ops/Near** | ServerTools cleanup timers; loadgen lever or separate ops mod, not default optim |
| Population hard cap | **Ops/Near** | IceCoffee crude zombie cap; prefer spawn/LOD budgets over full-list scans on every spawn |
| Idle TE early-out (fuel / power no-op) | **Near/Mid** | OCB StopFuel spirit; only if APM shows TE family; content-mod sensitive |

### 4.2 Cheaper algorithms (same features, less CPU)

| Idea | Tier | Notes |
|---|---|---|
| Coarser far-AI: wander noise instead of full EAI graph | **Mid** | Behavior change |
| Hierarchical pathing (region waypoints then local A*) | **Far** | Touches nav system deeply |
| Shared path for zombie groups with same goal | **Mid** | Classic crowd optimization |
| Cache “closest player” less often for far entities | **Near** | Invalidation on player move |
| Physics: fewer SyncTransforms / ragdoll on dedicated | **Mid** | Fidelity + desync risk |

### 4.3 Parallelism (only where data races are boring)

| Idea | Tier | Notes |
|---|---|---|
| Pathfinder pool sizing / affinity | **Ops/Near** | Stock already threaded; tune, don’t 1:1 |
| Mesh gen already coroutine/budget oriented | **Near** | Budget first |
| Parallel **read-only** scans (e.g. build tick lists) | **Mid** | Must not mutate world |
| Thread-per-zombie | **Reject** | See §2 |
| Parallel.ForEach EAI / SendPackage (IceCoffee fossils) | **Reject** | Commented out upstream for good reasons; Unity + connection safety |
| Unbounded Task.Run path workers | **Reject** | IceCoffee ASPPathFinderThread shape; use admission instead |
| Full world sharding | **Far** | Second game |
| Out-of-process gateway (NAIWAZI-style) | **Far** | Product; not EfficientServer |

### 4.4 Networking

| Idea | Tier | Notes |
|---|---|---|
| Snapshot rate LOD by distance | **Mid** | Bandwidth + CPU on serialize |
| Interest management (don’t send far entity detail) | **Mid** | Large surface, desync risk |
| Compress / batch position packages | **Mid** | Protocol expectations |
| Replace LiteNetLib with custom io_uring UDP | **Far** | Reject for this project |
| Parallel client fan-out on send | **Reject** | IceCoffee sketch; prefer fewer packages |
| Disable unused transports (SteamNetworking) | **Ops** | Already in loadgen configs |
| High-ping kick / laggy client policy | **Ops** | ServerTools, lag-shield, CSMM; shrink bad clients; loadgen needs immunity |
| Bulk mod transfer off game net (HTTP CDN) | **Ops** | MVirus pattern; isolate join-stress scenarios from AI A/B |

### 4.5 Memory / GC (Mono)

| Idea | Tier | Notes |
|---|---|---|
| Find per-frame allocations in hot AI/net with bridge + profiler | **Near** | Evidence-driven |
| Object pools for temporary lists in patched paths | **Mid** | Only if we own the alloc site |
| Avoid Harmony that allocates every tick | **Near** | Patch hygiene |
| Process-wide GC latency mode | **Ops/Far** | Limited levers on Mono embed |
| Manual `GC.Collect` as optim | **Reject** | IceCoffee console cmd; diagnostic hitch, not a strategy |

### 4.6 I/O and host

| Idea | Tier | Notes |
|---|---|---|
| UserData on local NVMe, affinity, IRQ, CCD pin | **Ops** | [`HOST_TUNING.md`](HOST_TUNING.md) |
| io_uring inside game | **Reject** | §3 |
| Async player/world save | **Mid** | IceCoffee async PDF save was commented; only with main-thread snapshot then worker write |
| Save / backup blackout in APM protocol | **Ops** | BackupMod + ServerTools AutoSave; never compare mid-save to quiet |
| Reduce region thrash (view distance, mesh) | **Near** | Config + current mesh patches |
| Map-render / web panel load off during baselines | **Ops** | Allocs map tiles + panel pollers steal CPU/IO |

### 4.7 Content and TE (baseline risk, not default optim)

| Idea | Tier | Notes |
|---|---|---|
| Heavy power / TE grids (OCB Electricity class) | **Ops** | Can burn &gt;20 ms/tick by author’s own Stopwatch; document incompat for A/B packs |
| Idle early-out on stock TE paths | **Near/Mid** | Same as §4.1; measure first |
| Content NPC packs (SphereII SCore class) | **Ops** | Increases AI load; use as stress content, not optim reference |

### 4.8 Structural (out of EfficientServer mission)

| Idea | Tier | Notes |
|---|---|---|
| Dedicated-stripped fork of Assembly-CSharp | **Far** | Legal/update hell; SphereII Dedi stub is not a real strip |
| Custom dedicated server reimplementation | **Far** | ARCHITECTURE: years; only path for “extract most sim” |
| Extract most sim off main + multithread (stock Harmony) | **Reject** | Starve main, do not evacuate stock methods; see [`SIM_PARALLELISM.md`](SIM_PARALLELISM.md) §5 |
| Extract most sim + SoA intents (custom dedicated) | **Far** | Correct 1k×10k architecture; not this modlet |
| **Hijack `gmUpdate`; reorchestrate; reuse stock methods** | **Mid** | Custom conductor: budgets/order on main; workers pure prep only; full body replace = high RE tax. [`SIM_PARALLELISM.md`](SIM_PARALLELISM.md) §5.6.1 |
| Conductor owns TickEntities admission only | **Near/Mid** | Preferable step before full gmUpdate replace |
| Hybrid: parallel far-tier filter only | **Mid** | Research; main still runs Full EAI for survivors |
| Stock mutators called from worker threads | **Reject** | Reuse stays on main commit path |
| IL2CPP / different Unity | **Far** | Not our ship |
| Multiple server processes with seamless transfer | **Far** | NAIWAZI Gateway product shape; not a modlet |
| Admin mega-suite in the optim DLL | **Reject** | ServerTools / IceCoffee surface is ops/AC/economy |

### 4.9 Hot paths (summary; see OPTIMIZATION_CANDIDATES)

| Rank | Path | Preferred lever | Threads? |
|---|---|---|---|
| 1 | Entity AI + **FindPath** + **MoveHelper (1236 IL)** | LOD, path admission, far updateTasks skip | ASP coroutine; no EAI threads |
| 2 | `GetClosestPlayer` / EntityActivityUpdate | Cache TTL | No |
| 3 | Biome `SpawnUpdate` (441 IL) | Player-near scope | No |
| 4 | Mesh / deco (330) / splash (185) | Budgets + dedi skips | No |
| 5 | Falling blocks (GroupFalling 292) | Optional air | No |
| 6 | Net `updatePlayerList` (509 IL) | Rate LOD (Mid) | Parallel send reject |
| 7 | Vehicle/Drone managers (297/305) | Idle early-out | No |
| 8 | Saves / `GC.Collect` / admin | Ops + optional GC guard | io_uring host only |

Full grades: [`OPTIMIZATION_CANDIDATES.md`](OPTIMIZATION_CANDIDATES.md).

---

## 5. Candidate EfficientServer directions (if evidence demands)

Only promote after APM shows the bottleneck under a fixed loadgen scenario.

**Authoritative graded inventory:** [`OPTIMIZATION_CANDIDATES.md`](OPTIMIZATION_CANDIDATES.md).
Loop RE evidence: [`../../7dtd-research/docs/loop.md`](../../7dtd-research/docs/loop.md).

### 5.1 Near-term (Grade A from RE)

1. **Pathfind admission** on `EntityAlive.FindPath` / `PathFinderThread.FindPath` - stock **always enqueues** (Y-clamp only when xz dist² &gt; 1225). Per-entityId dict coalesces. Worker coroutine drains **≤8 paths/slice** then yields; each path runs **`AstarPath.StartPath`** (A* package). Combat EAI can call FindPath **3× per Update**.
2. **Closest-player cache TTL** - `GetClosestPlayer` linear over players; `EntityActivityUpdate` primary consumer.
3. **Spawn walk scoping** - `SpawnManagerBiomes.SpawnUpdate` **441 IL**, ~every 20 ticks × area-master chunks.
4. **Optional falling-block → air** - `AddFallingBlock` → `GroupFallingBlocks` (292) / `LetBlocksFall`.
5. **Guard dedicated `GC.Collect`** in `gmUpdate`.
6. **Keep / tune LOD + far updateTasks skip** - scale only throttles EAI; **`EntityMoveHelper.UpdateMoveHelper` (1236 IL)** does dig/jump/stuck/attack assist on every updateTasks. Far skip avoids it. Vulture `updateTasks` is **1344 IL** if relevant.

### 5.2 Next (Grade B, evidence-gated)

7. **Dedicated deco / splash skip** - `DecoManager.UpdateTick` (330), `WaterSplashCubes.Update` (185); same class as music skip.
8. **Sleeper volume LOD** - `SleeperVolume.Tick` / touch paths.
9. **Vehicle/drone manager idle early-out** - 297 / 305 IL every gmUpdate if instances exist.
10. **Net interest rate LOD** - `updatePlayerList` (**509 IL**): Teleport if enc Δ∉±256; full PosAndRot if ∉±128 or age&gt;100; else RelPos; vel if motion²&gt;0.04; interest refresh if distSq&gt;16. High desync risk.
11. **Dynamic mesh budget retune** - peer Update 404 + DynamicMeshServer 452.
12. **Block ticker budget** - `tickScheduled` 151 / `tickRandom` 97.

### 5.3 Measurement protocol

- Black out auto-save / BackupMod windows when comparing APM runs.
- Clean dedicated (no admin mega-mods) unless that *is* the scenario.
- Isolate MVirus-scale join transfer from pure AI tick tests.
- Name content packs (power/NPC) in loadgen manifest.
- Probe stacks listed in [`OPTIMIZATION_CANDIDATES.md`](OPTIMIZATION_CANDIDATES.md) §4 (moveHelper, FindPath, SpawnUpdate, deco, net entry, …).

Each code candidate needs: config flag, soft fail on missing targets, FEATURES.md fidelity checklist, budget gate.

---

## 6. Decision rules

```text
Is the bottleneck “too many zombies thinking”?
 → Less AI / cheaper AI / path caps (not more threads)
 → Optional: falling-block storms / entity bloat as separate levers

Is the bottleneck “main thread busy but workers idle”?
 → Only then consider batch jobs with safe commit (hard)
 → Do not ship Parallel.ForEach EAI/send or unbounded Task.Run path

Is the bottleneck hitchy with disk latency?
 → Host storage + less region thrash (not io_uring in-mod)
 → Check save/backup schedule contaminated the window

Is the bottleneck network serialize/send?
 → Interest management / fewer packages (high risk)
 → Not parallel fan-out; not a gateway rewrite in this DLL

Is the bottleneck GC?
 → Find allocators; don’t random-pool the world; no manual Collect loops

Is the bottleneck a content mod TE/NPC pack?
 → Fix or remove that pack for the A/B; EfficientServer is not a power-grid rewrite
```

---

## 7. Explicit rejects (for this project)

| Idea | Why reject here |
|---|---|
| Thread per zombie / per entity | Overhead + races + desync |
| Parallel.ForEach EAI or SendPackage | IceCoffee fossils; thread-safety + tiny client N |
| Unbounded concurrent path Task.Run workers | Stampede; races; admission is the near lever |
| Extract most stock sim onto workers via Harmony | Main-thread world APIs; needs SoA + intents (rewrite) |
| General “sim thread pool” over stock methods | Same as above; [`SIM_PARALLELISM.md`](SIM_PARALLELISM.md) §5-6 |
| Full `gmUpdate` replace without subsystem checklist | Silent skips; update rot every TFP patch |
| Worker threads calling stock `EntityAlive` / world APIs | “Reuse” that races; IceCoffee-class failure |
| Manual GC.Collect as optim | Hitches; panels may expose heap, APM owns diagnosis |
| Admin / AC / economy / web panel in optim DLL | ServerTools / IceCoffee / CSMM mission, not ours |
| NAIWAZI-style gateway process | Product; closed; out of EfficientServer scope |
| io_uring inside EfficientServer | Game does not expose I/O loop; wrong layer |
| Full server rewrite | Out of EfficientServer mission (research only in SCALE) |
| Auto-generated Harmony from profiles | Already removed from APM for safety |
| Topology pinning inside the DLL | Ops only ([`HOST_TUNING.md`](HOST_TUNING.md)) |

---

## Related

- Graded candidates (authoritative): [`OPTIMIZATION_CANDIDATES.md`](OPTIMIZATION_CANDIDATES.md)
- Extreme scale data structures (1k players / 10k zombies): [`SCALE_1000x10000.md`](SCALE_1000x10000.md)
- Extract sim / threading policy / hot-path catalog: [`SIM_PARALLELISM.md`](SIM_PARALLELISM.md) (§5-7)
- Stock frame RE: [`ARCHITECTURE.md`](ARCHITECTURE.md)
- Dedicated loop RE map: [`../../7dtd-research/docs/loop.md`](../../7dtd-research/docs/loop.md)
- NAIWAZI ServerKit reconstruction (gateway split, free AC/Bot RE): [`../../7dtd-research/naiwazi/NOTES.md`](../../7dtd-research/naiwazi/NOTES.md)
- ServerTools (dmustanger) admin suite, optim-relevant bits: [`../../7dtd-research/7dtd-ServerTools/NOTES.md`](../../7dtd-research/7dtd-ServerTools/NOTES.md)
- Open-source tools survey (IceCoffee, SphereII, CSMM, MVirus, OCB, Allocs, …): [`../../7dtd-research/oss-tools/NOTES.md`](../../7dtd-research/oss-tools/NOTES.md)

## Changelog

- **2026-07-16:** Optim candidates canonical file is `OPTIMIZATION_CANDIDATES.md` in this project (not under 7dtd-research/il).
- **2026-07-16:** Deeper RE: path drain ≤8/slice, combat FindPath×3, MoveHelper dig/jump, Vulture 1344; link SYNTHESIS.
- **2026-07-16:** Merged opt-scan RE: path always-enqueue, MoveHelper 1236, SpawnUpdate 441, net entry 509, deco/splash, candidate order §5.
- **2026-07-16:** Hijack-gmUpdate conductor hybrid (§4.8, rejects); details in SIM §5.6.1.
- **2026-07-16:** Doc ownership map; §4.8 extract-sim rows; §4.9 hot-path summary; rejects for Harmony sim extraction / sim thread pool; related links to SIM §5-7.
- **2026-07-16:** Merged OSS survey lessons into §2 (ecosystem evidence), lever catalog (§4.1-4.8), candidates + measurement protocol (§5), decision rules (§6), rejects (§7).
- **2026-07-16:** Linked OSS tools survey notes under `7dtd-research/oss-tools/NOTES.md`.
- **2026-07-16:** Initial idea map: threading, io_uring, lever catalog, near-term candidates, rejects.
