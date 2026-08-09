# Perf research brief for EfficientServer (V3.1.0)

**Hub:** [`README.md`](../README.md).  
**Owns:** ranked map from stock RE + measured APM to what the optimizer should
do next, what is already closed, and which "big wins" are dead ends.
**Not:** full bottleneck tables ([bottlenecks.md](bottlenecks.md)), A/B ledger
([RESULTS.md](RESULTS.md)), or stock-only RE narratives
([`../../7dtd-research/docs/`](../../7dtd-research/docs/)).
**Evidence date:** 2026-07-28 brief; APM campaign through 2026-07-21; IL re-check
of open hot methods same day (dumps under local `/tmp/perf-re/`, regenerable via
`7dtd-research/tools`). **2026-08-06 research follow-up:** Clone call-site triage
and chunk encode ownership closed in stock RE (see §4.4-4.5).

**Read with:** [RESULTS.md](RESULTS.md) (verdicts), [measured-scaling.md](measured-scaling.md)
(exponents), [bottlenecks.md](bottlenecks.md) (catalog), [engine-limitations.md](../../7dtd-research/docs/engine-limitations.md)
(stock ceilings), [loop.md](../../7dtd-research/docs/loop.md) (frame ownership).

---

## 0. One-page answer

| Question | Answer |
|---|---|
| Is the dedicated tick still a mystery? | **No.** At the blood-moon ceiling, UpdateTick is fully attributed (0.4% residual). |
| What sets the 50 ms wall under load? | **TickEntities ~63%** + **OnUpdateEntities ~30%** (+ SendChunks ~5%). |
| Can more safe Harmony cut the entity wall? | **Mostly no.** Close-combat AI + world-collision physics are fidelity-bound. |
| Can more safe Harmony cut the player O(N^2) wall? | **Partially already.** Fast send + replication stride + governor. Spatial grid is large/risky. |
| What still needs research (not more catalog prose)? | **Animator CullCompletely human soak** (code built), **path admission BM measure** (code built), **chunk blob-cache design** (ownership closed), **optional spatial grid design**. Clone triage **closed** in research. |
| What should the optimizer *not* re-open? | Serialize-once at build layer (stock already does it), mid-band AI stride as a headline win, parallel EAI, parallel SendPackage, fps as TPS lever. |

```text
Stock RE (7dtd-research)     Measured APM (7dtd-apm)     EfficientServer (this repo)
  loop / net / entity IL  -->  exponents + section ms  -->  one lever + same load A/B
```

---

## 1. Two walls, two regimes (do not mix)

| Axis | Dominant sections | Shape | Cliff | Optimizer meaning |
|---|---|---|---|---|
| **Players** | `ConnectionManager.Update`, `NetEntityDistribution.OnUpdateEntities` | ~**O(N^2.27)** / **O(N^2.26)** per-call | ~**450-500** players (near-zero zombies) | Interest + send fan-out; view distance is the safe knob |
| **Entities** | `World.TickEntities*` , move/AI | ~**O(N)** | Volume / BM capacity | LOD, caps, governor, TickGuard; not complexity class |

Canonical measure profile: `7dtd-apm` `canonical-heavy-v2` (64 clients, mixed bots,
forensic preset). Capacity under shipping defaults (2026-07-21): **~232 sustained
endgame zombies at 64 players** with adaptive governor (was ~147 static stride).

**Frame rate is not tick rate.** Full entity sim + replication stay ~20 Hz even if
Unity fps rises. `Server.TargetFps` is not a capacity lever ([RESULTS.md](RESULTS.md) §3k).

---

## 2. Ceiling composition (what research already closed)

At saturated 64p blood-moon-style load ([RESULTS.md](RESULTS.md) §3h):

| Share of UpdateTick | Owner | Lever class |
|---:|---|---|
| ~63% | `TickEntities` (serial main thread) | Starve / LOD / shed; **not** multi-core Harmony |
| ~30% | `OnUpdateEntities` (after stride already halves cost) | Stride + governor; view distance; spatial grid (risky) |
| ~5% | `SendChunksToClients` | View distance / join; encode is heavy but not the ceiling mass |
| <2% | everything else timed | Diminishing |

Per-zombie tick (entity axis, RE + A/B):

| Share | Mechanism | Status |
|---:|---|---|
| ~54% | World-collision physics (OverlapCapsule / block AABB / ground) | Irreducible without movement fidelity change |
| ~27% | AI (`updateTasks` / EAI / path follow) | Already LOD'd; mid-stride **no win** on close-combat loads |
| ~4-6% | Neighbor-vs-neighbor collision | Crowd LOD measured **null** |
| rest | small | - |

**Implication for research:** more IL on `EntityMoveHelper` (1236 IL) will not find a
free 30% tick cut. The method is large because combat locomotion is large.

---

## 3. Shipped EfficientServer stack (research consumed)

Do not re-research these as "open optim ideas". Evidence is in RESULTS.

| Lever | RE target | Verdict |
|---|---|---|
| P1 graph throttle | `AstarManager.UpdateGraphs` | **-28.5% ms/tick** at breaking load |
| P2 move dead-zone | `UpdateGraphPos` ldc 100 | Real graph work cut; tick-neutral when healthy |
| Fast single-target send | `ConnectionManager.SendPackage` entityId map | Scales with players (-4.2% @128p) |
| AI LOD + far updateTasks skip | `EntityActivityUpdate` / `updateTasks` | Core entity starve |
| Dedicated presentation skips | music/splash/audio/spectrum/cloth | Headless waste |
| GC guard + Boehm headroom env | forced `GC.Collect` / free-space divisor | Smoothness; not alloc cut |
| P4 InitScan pool | external `LayerGridGraph` iterator | Alloc eliminated; **no steady-state TPS win**; opt-in |
| Replication stride | `NetEntityDistribution.OnUpdateEntities` | **-45%** at stride 2 |
| Adaptive governor | schedules stride + graph | +58% BM capacity vs static |
| TickGuard | farthest-first despawn | Emergency only |
| Animator LOD / emergency | headless animator cost | Exit path **wedged** on `enabled` toggle |

Honest **refutations** (do not rebuild without new evidence): spatial interest as a
*safe small patch*, build-layer serialize-once, mid-band AI stride as headline,
parallel interest scan, chunk-send throttle as primary, fps/jitter, buff throttle,
clustered animator LOD, job-worker pool, crowd-collision LOD, engagement-cap
director (arithmetic kill).

---

## 4. IL re-check of open / structural hot methods (2026-07-28)

Local dumps: regenerate with
`MONO_PATH=tools/bin mono tools/bin/DumpMethod.exe Assembly-CSharp.dll <Type> <Method>`.

### 4.1 Path admission surface (still open for BM spikes)

**`EntityAlive.FindPath` IL=49**

```text
delta = target - position
xzDistSq = dx*dx + dz*dz
if xzDistMax > 1225 (~35 m):
  clamp target.y to position.y +/- 45 if vertical gap large
PathFinderThread.Instance.FindPath(entity, target, speed, canBreak, behavior)
// NO rate limit, NO distance drop, NO queue depth check
```

**`ASPPathFinderThread.FindPath` IL=17 / 22**

```text
entityWaitQueue.Add(entityId)           // HashSetList
finishedPaths[entityId] = new PathInfoSingleTarget(...)  // always new PathInfo
// coalesces by entityId (last request wins) but never refuses enqueue
```

**`AStarPathFinderThread.FindPath` IL=42** (legacy path): same pattern under
`Monitor` + `writerThreadWaitHandle.Set()`.

**Drain:** ASP `FindPaths` is a main-thread coroutine state machine; prior RE:
hard cap **8 path starts per frame**, then yield. Enqueue unbounded, compute capped.

**Optimizer use:** A2 path admission is still a valid *spike* lever (BM path spam),
not a steady-state ms/tick headline. Prefix at `EntityAlive.FindPath` or
`ASPPathFinderThread.FindPath`: cap enqueues/tick, drop far non-alert, keep alerted
/ quest / sleeper. Fidelity risk: stuck AI. Measure under synthetic BM, not light load.

### 4.2 Closest-player primitive (small constant, shared)

**`World.GetClosestPlayer` (xyz overload) IL=63:** linear scan of
`Players.list`; skip dead / not spawned; optional team filter; track min
`GetDistanceSq`. No spatial index.

**`World.EntityActivityUpdate` IL=229:**

1. Clear every player's `aiClosest` list.
2. For each `EntityAlive`: `GetClosestPlayer(pos, -1, false)` -> store
   `aiClosestPlayer` / `aiClosestPlayerDistSq` (or +inf if none).
3. Cloth / camera distances for local player (dedi: local often null): cloth bands
   **625** / aiming **3025** distSq.
4. Per-player sort budget: `FastClamp(60/playerCount, 4, 20)`.
5. Stock `aiActiveScale` bands (distSq): **full < 64**, **mid < 225 -> 0.3**,
   else **0.1**; jiggle **36**.

**Optimizer use:** A4 spatial hash is **low tick %** (perf leaf ~0.3%) but is the
shared primitive for LOD. Only worth building as part of a larger spatial subsystem
(with interest), not as a standalone patch.

### 4.3 Replication (wall is send interest, not mystery)

**`NetEntityDistribution.OnUpdateEntities` IL=322**

- Clear `playerList` / `enemyList`.
- Partition `trackedEntitySet` into enemies vs players.
- When `GameManager.enableNetworkdPrioritization`: per-enemy vs all players,
  Y-flattened distSq, priority bands (constants include **16384**, **625**, **324**,
  angle **25**). This is extra O(enemies x players) for priority, then...
- Per tracked entry: `updatePlayerList(playerList)`.

**`NetEntityDistributionEntry.updatePlayerList` IL=509**

- Interest refresh gate: entity moved `distSq > 16` (or first update) ->
  `updatePlayerEntities`.
- Physics master broadcast on counter.
- **Exactly 7 `SendToPlayers` call sites** in the body for package families:
  Teleport / PosAndRot / RelPosAndRot (x2 paths) / Rotation / Velocity / AliveFlags
  (+ physics package path earlier).
- Encoded motion thresholds: move if |d| >= **2**; teleport if outside **+/-256**;
  full PosAndRot if outside **+/-128** or age > **100** ticks; else RelPosAndRot.

**RE correction already in RESULTS:** package *build* is once + `SendToPlayers`;
per-connection re-encode lives in writer threads (`taskSerialize`), off sim tick.
Build-layer "serialize-once" is **not** an open win.

**Optimizer use:** keep stride/governor as the production lever. Spatial interest
grid only if product goal is **>>128 players** and desync budget is accepted.

### 4.4 Chunk encode on sim thread (structural CPU, not ceiling mass)

**`NetPackageChunk.Setup` IL=31**

```text
chunk field = _chunk
bOverwriteExisting = flag
serializedData = MemoryPools.poolMS.AllocSync(true)
writer = poolBinaryWriter.AllocSync(false)
writer.SetBaseStream(serializedData)
chunk.write(writer)          // FULL encode on caller thread (sim)
dispose writer
```

So every chunk package pays **`Chunk.write` on the sim thread** at Setup time; only
later byte copy/send is elsewhere. Catalog still lists chunk pipeline as large under
join/spread loads; at BM ceiling it is ~5% of UpdateTick after entities+replication.

**Ownership (stock RE, 2026-08-06 Xref, closed):**

| Symbol | Callers |
|---|---|
| `ChunkManager.SendChunksToClients` | **sole** = `GameManager.UpdateTick` |
| `NetPackageChunk.Setup` | **4**: two from `SendChunksToClients` (first-load + reload), plus disc/flat `RebuildTerrain` overwrite paths |
| `NetPackageChunk.write` | **0** direct; body goes through virtual package serialize on the connection writer |

Narrative: [world-chunks.md](../../7dtd-research/docs/world-chunks.md),
[engine-limitations.md](../../7dtd-research/docs/engine-limitations.md).

**Optimizer use:** blob cache per (chunkKey, version) shared across observers is the
safe-ish research design; off-thread encode races world mutation. P6 send-batch
throttle already measured mis-targeted for join lag (gen/load dominates).
**Ownership is no longer an open research question**; remaining work is design +
fidelity of a cache, not "who calls Setup".

### 4.5 ItemStack.Clone (alloc rank, not tick rank) - triage closed

**`ItemStack.Clone` IL=15:** always `new ItemStack(itemValue.Clone() or None, count)`.
Array clones allocate `newarr` then per-element Clone.

**Call-site triage (stock RE Xref, 2026-08-06, closed):** **162** sites total.

| Bucket | Sites | Dedi relevance |
|---|---:|---|
| Client UI (`XUi*` / `XUiC_*` / `XUiM_*`) | **56** | **None** for headless STW |
| TE Workstation / Collector / Forge / TEFeatureStorage / traps | ~28 | **High** (server inventory) |
| TransactionalInventory / Inventory / InventoryOperation / Bag | ~14 | **High** |
| NetPackagePlayerInventory / ItemDrop Setup | 4 | Medium (wire copy) |
| Loot / trader / quest / rewards / game events | rest | Medium, event-driven |

Full table: [items.md](../../7dtd-research/docs/items.md) § ItemStack.Clone call-site triage.

**Optimizer use:**

1. Do **not** Harmony XUi Clone for dedicated tick or STW.
2. Any elision must be per-site with non-escaping proof (TE/inventory/net Setup);
   global Prefix is wrong.
3. Prefer APM alloc samples that attribute to TE/inventory under loot/combat soak,
   not UI-heavy client captures.
4. Triage itself is **done**; remaining work is optional micro-patches + soak, not
   more catalog RE.

### 4.6 Animator headless waste (largest recent RE win, exit unsolved)

Healthy server zombies: `cullingMode = CullUpdateTransforms` (not AlwaysAnimate).
Toggling `Animator.enabled` kills root-motion forever (`deltaPosition=0`) after restore.

**Open research/build:** enter emergency via **`cullingMode = CullCompletely`**,
restore prior mode on exit; never touch `enabled`. Needs:

1. Perf re-validation (does it reproduce ~147 -> ~85 ms class win?).
2. Spawn-hook so new zombies also enter the mode.
3. Human + `es animstate` dp check on restore.

Tracked in repo `TODO.md` (animator revival wedge).

---

## 5. Ranked "research -> optimizer" backlog

Priority = (expected capacity or smoothness gain) x (evidence readiness) /
(fidelity + maintenance risk). One lever at a time; APM + same world/seed/bots.

| Rank | Work | Type | Why now | Fidelity gate |
|---:|---|---|---|---|
| **1** | **Animator `CullCompletely` emergency** | **Built v1.17.0** (default-off) | Enter/exit + es animoff use CullCompletely | Human `es animstate` dp + heavy A/B still required |
| **2** | **Path admission (A2)** under synthetic BM | **Built; measured 2026-08-07 default-off** | BM-ish 24p: cap=32+drop@50m **worsened** late ticks / UpdateTick; loadgen still 24/24 | Keep off; only revisit with path-queue telemetry + true BM director |
| **3** | **Ops pack as first-class** | Docs + launch | ViewDistance, MaxSpawnedZombies, `GC_FREE_SPACE_DIVISOR`, `MONO_ENV_OPTIONS=-O=all` already validated | Publish recommended serverconfig matrix |
| **4** | **Chunk blob cache design** | Design + optional patch | Ownership closed (Setup on sim from SendChunks/RebuildTerrain); multi-observer join still pays N× encode | Byte-identical packages; invalidation on block edit / TE / density |
| **5** | **Spatial interest + closest-player grid** | Large project | Only structural fix for 450-500p cliff | Client never missing in-range entities; removal correctness |
| **6** | **ItemStack.Clone micro-patches (optional)** | Tiny patches only | **Triage closed** (162 sites; skip XUi; TE/inventory/net mass) | Inventory/loot soak; no dupe/desync; no global Prefix |
| **7** | **Far character-controller stride** | Research only | 54% of per-zombie is world collision | Far zombies must not float/clip into play |

**Explicit non-goals (parked):**

- Parallel `TickEntities` / `EAITaskList` via Harmony  
- Build-layer serialize-once  
- Mid-band AI stride as default win  
- Raising fps to raise TPS  
- More presentation skips without main-thread samples proving cost  

---

## 6. How to use this brief in the evidence loop

```text
1. Pick ONE row from section 5 (or a config-only ops change).
2. Baseline: loadgen + apm on canonical-heavy-v2 (or BM synthetic).
3. Candidate: same world seed, bot count, duration, collectors, serverconfig.
4. Compare: ms_per_tick, section totals, gross alloc, worst STW, alive/bots stable.
5. Fidelity: combat/sleeper/path/visibility checklist for that lever.
6. Record session IDs in RESULTS.md; promote config default only if zero gameplay hit.
```

Stock RE facts for patch targets live in:

| Topic | Research doc |
|---|---|
| Frame ownership (gmUpdate vs ConnectionManager vs DynamicMesh) | [loop.md](../../7dtd-research/docs/loop.md) |
| AI LOD bands / updateTasks | [entity-ai.md](../../7dtd-research/docs/entity-ai.md) |
| Net interest / package thresholds | [network.md](../../7dtd-research/docs/network.md), [protocol-packages.md](../../7dtd-research/docs/protocol-packages.md) |
| Chunk stream / SendChunks ownership | [world-chunks.md](../../7dtd-research/docs/world-chunks.md) |
| ItemStack.Clone call-site triage | [items.md](../../7dtd-research/docs/items.md) § Clone triage |
| Hard ceilings | [engine-limitations.md](../../7dtd-research/docs/engine-limitations.md) |

---

## 7. Gaps research can still close (narrow)

These are **not** "continue annotating all catalogued types". They are perf-specific.

| # | Gap | Status |
|---|---|---|
| 1 | `ASPPathFinderThread` FindPaths drain: re-pin literal **8** + priority order | Still open (admission design polish) |
| 2 | `Chunk.write` dirty/version stamp for blob-cache invalidation | Still open (design input for rank 4) |
| 3 | `ItemStack.Clone` call-site triage (Xref + buckets) | **Closed 2026-08-06** in research items.md |
| 4 | Animator spawn path (`cullingMode` / `applyRootMotion` on create) | Still open (supports CullCompletely + spawn hook) |
| 5 | Interest removal package when player leaves entity set | Still open (spatial grid) |
| 6 | Chunk encode ownership (who calls Setup / SendChunks) | **Closed 2026-08-06** in research world-chunks |

Everything else in the managed corpus is either already in RESULTS/bottlenecks or is
non-IL residual (Unity order, native LiteNet, Boehm internals).

---

## 8. Summary for product decisions

| Goal | Do this |
|---|---|
| Better BM at 64p | Keep governor + stride; finish animator CullCompletely; path admission if path backlog shows in APM |
| Better 128-500p | View distance + FastSend (done); only then spatial interest project |
| Fewer hitches | Alloc upstream at **TE/inventory** Clone sites (not XUi); P4 opt-in long soak; GC headroom env |
| Join less laggy | Chunk gen/load on sim, not P6 send cap; blob cache design (ownership known) |
| Rewrite-scale wins | `zdtd` multi-core sim / different net model - out of EfficientServer scope |

**Bottom line:** stock + APM research already named the walls and exhausted safe
Harmony headroom. Further research should be **lever-shaped** (section 5-7), not
coverage-shaped. The optimizer's next product work is **animator emergency exit**
and **path admission under BM**, with ops config as the free capacity dial.

---

## Changelog

- **2026-08-07:** Path admission BM-ish A/B (24p combat/bait, sessions `161109`/
  `161552`): enabling MaxPathEnqueuesPerTick=32 + DropPathWhenFarDistSq=2500
  **worsened** lag; keep default-off. See V310_APM_BASELINE.md.
- **2026-08-06:** Consumed research Clone triage (162 sites, skip XUi) and chunk
  encode ownership (SendChunks sole UpdateTick; Setup from SendChunks + RebuildTerrain).
  Rank 6 demoted to optional micro-patches; rank 4 ownership closed; §7 gaps table updated.
  **Live remeasure** moderate 16p forensic ES ON vs OFF (sessions
  `155401` / `160357`): UpdateTick avg -17.5%, late_ticks -68%, STW worst -91.5%;
  see V310_APM_BASELINE.md remeasure section.
- **2026-07-28:** Initial brief. IL re-check FindPath / GetClosestPlayer /
  EntityActivityUpdate / OnUpdateEntities / updatePlayerList / NetPackageChunk.Setup /
  ItemStack.Clone. Ranked backlog aligned with RESULTS campaign end-state.
