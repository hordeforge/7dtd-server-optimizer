# EfficientServer results ledger (minute detail)

Authoritative record of every lever: RE target, mechanism, config knob, IL target,
A/B session IDs + numbers, and verdict. Companion: graded backlog
[`OPTIMIZATION_CANDIDATES.md`](OPTIMIZATION_CANDIDATES.md); bottleneck catalog
[`../../research/docs/bottlenecks.md`](../../research/docs/bottlenecks.md); allocation
[`ALLOCATION_UPSTREAM.md`](ALLOCATION_UPSTREAM.md).

All A/B captures: `7dtd-apm capture --only all,alloc`, 150 s window, matched load,
same world/seed, back-to-back restarts. Sessions under `~/.local/share/7dtd-apm/`.

**Standing caveat:** absolute `ms_per_tick` drifts run-to-run (world state on
restart), so only **within-campaign** (off vs on, same session pair) deltas are
comparable. The mod's benefit is **load-dependent** - largest when the server is
tick-starved, small when it has headroom.

---

## 0. Version history

| Ver | Change |
|---|---|
| 1.2.0 | AI LOD, dedicated skips, dynamic-mesh budgets, GC guard (A7), GC incremental (opt-in) |
| 1.3.0 | P1 pathfinding graph-update throttle (`AstarGraphThrottlePatch`) |
| 1.4.0 | P2 rescan dead-zone (`AstarMoveThresholdPatch`); 8 correctness/fidelity fixes; P3 dropped (unsound) |
| 1.4.1 | Follow-up fixes: DedicatedSkip drift log, Gc Normalize, Config test harness, config-disabled init label |
| 1.5.0/1.5.1 | GC megapause diagnostic (`GcDiagnostics`, opt-in) |
| 1.6.0 | #1 single-target fast send (`FastSendPatch`) |

---

## 1. Shipped levers (A/B validated)

### P1 - pathfinding graph-update throttle (v1.3.0)
- **Target:** `AstarManager.UpdateGraphs(float)` (IL 185). Harmony **prefix**,
  returns `false` on non-Nth ticks.
- **Config:** `Pathfinding.GraphUpdateEveryTicks` (default 4; 1 = vanilla).
- **RE:** UpdateGraphs repositions the player-following voxel nav-graphs every tick;
  it also drains one queued grid move via `UpdateMoveGraph` (its sole caller - not a
  separate per-tick method, corrected 2026-07-18). Throttling slows the whole
  maintenance cadence by N; path *compute* (`FindPath`->`PathFinderThread`) is the
  separate every-tick system, so AI keeps moving against a slightly staler graph.
- **A/B (32 bots + ~270 zombies, breaking load):** sessions `074155` (off) /
  `074952` (on). **ms_per_tick 54.95 -> 39.28 (-28.5%)** - pulled the server from
  over the 50 ms budget (sub-20 TPS, tick-starved) to healthy 20 TPS. UpdateGraphs
  total -35%, avg 14.0 -> 3.1 ms (3-of-4 calls short-circuit), p95 -55%. Gross alloc
  -3.6%. **Fidelity: zombies 277 -> 277 stable** (vs 273 -> 266 baseline).
- **Verdict:** the standout. Turns a failing stressed server into a working one.
  Benefit is largest exactly when the server is drowning; small with headroom.

### P2 - rescan dead-zone (v1.4.0)
- **Target:** `AstarManager.UpdateGraphPos(AstarVoxelGrid, Vector2)`. Transpiler
  replaces the sole `ldc.r4 100` (the `SqrMagnitude` rescan gate) with config.
- **Config:** `Pathfinding.MoveRescanThresholdSq` (default 100 = vanilla; clamp
  [100, 10000]). Throws MISSING if the `100` constant drifts.
- **A/B (60 bots, 0 zombies, matched 116/111, dead-zone 100 vs 400):** sessions
  `085903` (100) / `090329` (400). **UpdateGraphs total -20.2%** (avg 5.78 -> 5.15,
  p95 34 -> 32) - hits its target. **ms_per_tick flat (+1.9%, noise)** because at a
  healthy ~20 ms/tick UpdateGraphs is only ~14% of the tick. A prior mixed-load run
  (`084350`/`085228`) was inconclusive (unequal zombie load).
- **Verdict:** real (-20% graph work) but tick-time-neutral except under
  graph-dominated stress. Ships vanilla-default (100); 400 is a mild safe tune.

### #1 - single-target fast send (v1.6.0)
- **Target:** `ConnectionManager.SendPackage(NetPackage, bool, int, int, int,
  Nullable<Vector3>, int, bool)`. Harmony **prefix**.
- **Config:** `Network.FastSingleTargetSend` (default false).
- **RE:** vanilla linear-scans the whole `Clients` list filtering by `entityId`;
  `SendToPlayers` calls it once per tracked player, `updatePlayerList` calls
  `SendToPlayers` ~7x/entity/tick -> fan-out O(entities x players x clients). The
  prefix short-circuits **only** the pure single-target case via the existing
  O(1) `ClientInfoCollection.ForEntityId` map, then reuses `ClientInfo.SendPackage`
  (the game's own enqueue). Provably equivalent: `entityId` is unique, so vanilla
  also enqueues to exactly one client (identical send-queue refcount: one
  `RegisterSendQueue` + one `AddToSendQueue`). Every other filter mode -> vanilla.
- **A/B (pure player-scale, 0 zombies):**
  - 60 players (`013125`/`013542`): ms_per_tick -1.8%, NetEntityDistribution -2.5%,
    ConnectionManager.Update -0.2%. **60/60 clients stable.**
  - 128 players (`014109`/`014539`): **ms_per_tick -4.2%**, ConnectionManager.Update
    **-5.2%**, NetEntityDistribution -3.6%. **120/120 clients stable.**
- **Verdict:** correct + **scales with client count** (-1.8% -> -4.2% from 60 -> 128
  players), targeting the 450-500 player death-spiral wall. Marginal at low counts
  (the scan is a few cheap int-compares); grows with the regime it targets.

### Mid-band entity tick-stride (v1.7.0) - INCONCLUSIVE / low value
- **Target:** `EntityAlive.updateTasks` prefix. Entities in the mid band
  (`MediumAiDistSq` <= d < `SkipTasksFarDistSq`) run the heavy tail every
  `AiLod.MidTickStride`-th frame, striped by entity id. `CheckDespawn` every tick;
  alerted never strided.
- **Config:** `AiLod.MidTickStride` (default 1 = off, clamp [1,20]).
- **A/B (32 bots + ~290 zombies, stride 1 vs 4):** sessions `004441` (off) /
  `005308` (on). **No benefit:** ms_per_tick +7.5%, TickEntities +4.9% - but
  stride_on had ~6% more zombies (298 vs 281), so the deltas track the load
  imbalance, not the stride. **Fidelity OK** (entity-tick magnitude normal
  throughout - no despawn bug; `post=0` was a run-end observer-gating artifact).
- **Why low value (mechanism):** the entity-axis cost is **close-combat-bound** -
  the dominant cost is zombies within ~20 m attacking players (must run full AI),
  the far band is already skipped, and the 20-50 m mid band is a thin shell with
  few entities. Little middle ground to stride. Ships **default-off** (harmless);
  may help profiles with many mid-range wandering-horde entities, but not the
  standard close-combat load. Honest negative result - striding is not the entity
  lever; the entity cost is largely irreducible without breaking combat fidelity.

### AI LOD + dedicated skips + dynamic-mesh budgets + GC guard (v1.2.0)
- **AiLodPatch** (`World.EntityActivityUpdate` postfix): tighter far/medium/full
  `aiActiveScale` bands; cloth toggled level-triggered (self-heals, v1.4.0).
- **UpdateTasksLodPatch** (`EntityAlive.updateTasks` prefix): far-skip the whole
  tail; calls `CheckDespawn()` first so far entities still despawn (v1.4.0 fix).
- **DedicatedSkipPatch:** music/splash/environment-audio skips on dedicated.
- **DynamicMeshBudgetPatch:** player-area filter + load/sync budgets.
- **GcGuardPatch** (A7, `gmUpdate` transpiler): skip the forced 120 s `GC.Collect`,
  host-aware safety ceiling. **A/B (three-way):** at 150 zombies, guard -28% overage
  (2->1 full GC); at 128 players, wash (churn drives collects regardless).
- **Aggregate full-mod A/B (mod OFF vs all-ON, 32 bots + ~280 zombies, healthy
  38 ms baseline):** sessions `082030` (off) / `082834` (on). **ms_per_tick -8.0%**
  (UpdateGraphs -14.9%, NetEntityDistribution -16.8%, TickEntities -6.4%). Gross
  alloc +3.8% (noise). Fidelity intact. *Measured before #1 and the v1.4-1.6 work.*

---

## 2. GC / allocation findings

- **Cadence is a wash:** gross churn 15.16 / 14.84 / 14.52 MB/s across forced /
  guard / incremental at 128p. GC tuning is downstream of allocation.
- **Megapause diagnostic (v1.5.1, `Diagnostics.GcMegapauseTest`):** disabled Boehm
  under load, grew the heap 120 s, timed one forced collect. **PAUSE_MS = 479** on a
  **6.91 GB heap (~5.6 GB live)**, growing ~10 MB/s. Confirms a never-collect scheme
  concentrates the cost into one ~0.5 s freeze (scales with heap toward ~1 s+ at
  16 GB). `GC_get_heap_size` reports Boehm capacity (retained), so `freed` reads 0 -
  the pause is the real signal.
- **Corrected APM allocator ranking:** #1 `AstarVoxelGrid.InitScan`, #2
  `ItemStack.Clone`, #3 `TerrainSubMesh.Add`, #4 `PooledBinaryWriter.Write` ->
  `ChunkBlockChannel.Write`. Allocation is the true steady floor; **untouched by any
  shipped lever** (they cut CPU sections, not churn).
- **Heavy re-profile (2026-07-20, 48 bots + 334 zombies, 57 ms/tick over budget):**
  section wall is **`World.TickEntities` ~32 s** (entity sim, close-combat AI -
  irreducible per the stride test), then `NetEntityDistribution.OnUpdateEntities`
  15 s (O(N^2) interest). Churn allocators (after an **APM fix**, below):
  `ItemStack.Clone`, `InitScan`, `ChunkBlockLayer.OnLoad`, `ASPPathNavigate.CreatePath`
  (`new ASPPathFinder`/build - tiny, gated, skipped). No new *safe* Harmony target.
- **APM fix (found via this profile):** alloc attribution was skipping `System.` /
  `Unity.Profiling` but not `UnityEngine.`, so an engine struct leaf
  (`Quaternion.FromToRotation`, a 16-byte value type - impossible as a >4 KB alloc)
  was attributed instead of the game caller. Now skips `UnityEngine.` / `Unity.` too
  (consistent with the CPU filter); re-mining then revealed the real game allocators
  (`ChunkBlockLayer.OnLoad`, `ASPPathNavigate.CreatePath`). Regression-tested.
- **Buffer-retain correction:** `PooledExpandableMemoryStream.Reset()` = `SetLength(0)`
  keeps the buffer, so pooled stream buffers do NOT realloc in steady state - #5
  (presize+retain) downgraded. The reallocating allocator is `InitScan` (array +
  N `LevelGridNode` objects per move).

---

## 3. Current aggregate + honest state

- **At a breaking (tick-starved) load:** P1 alone = **-28.5% ms/tick**, failing ->
  healthy. This is the headline improvement.
- **At an already-healthy load:** full mod ~**-8%**; individual levers 2-5%.
- **At high player count (128p):** #1 adds **-4.2%** and scales toward 500p.
- **NOT summable** into one figure (levers overlap, regime-specific, run drift).
- **Definitive aggregate A/B (2026-07-20, v1.8.0):** true vanilla (mod `Enabled=false`
  + Boehm-default GC) vs everything-on (full safe mod + `GC_FREE_SPACE_DIVISOR=1`;
  both toggles verified live in `/proc/<pid>/environ`), matched ~320 zombies + 32 bots,
  150 s, sessions `053013` / `053846`. **The mod's win at a heavy-but-not-breaking load
  is smoothness, not raw throughput:**
  - **worst STW 274 ms -> 0 ms**, **full collections 3 -> 0**: vanilla ate a 274 ms
    megapause-class freeze (5.5 ticks lost at once) in the window; everything-on had
    zero full collections (the `GC_FREE_SPACE_DIVISOR=1` headroom + skip-forced-collect
    guard). This is the standout number.
  - **tick-stall total -27.9%** (9990 -> 7205 ms), late-tick share -13.9%.
  - **UpdateGraphs -26.6%** (3132 -> 2299 ms): the P1 pathfinding throttle delivering.
  - **Per-tick compute flat** (tick interval -1.4%, ms_per_tick +1.0%, TickEntities
    +2.2%, OnUpdateEntities +3.7%): at this load neither side fully tick-starves, and
    the entity/network walls are irreducible. The **-28.5% ms/tick headline is a
    *breaking-load* result** (vanilla tick-starved, mod pulls it back to healthy) - a
    regime heavier than this bench pushed into.
- **Biggest gains still on the table (unshipped):** the allocation floor / 479 ms
  megapause (P4 `InitScan` pooling, L1 serialize-once) and the two O(N^2.26/2.27)
  player-axis quadratic walls / death-spiral (spatial interest grid). See §5.

---

## 3b. Safe Harmony ceiling reached (proven 2026-07-20) + next levers

Deep bottom-up RE proved the two big remaining costs have **no safe Harmony lever**:
- **Entity tick (~32 s at 334 zombies):** close-combat AI. The mid-band stride
  A/B showed no win (thin shell; close entities need full AI, far are already
  skipped). Fidelity-bound.
- **Network replication (`OnUpdateEntities` ~15 s, O(N^2.26)):** interest is already
  distance-gated and cheap; the O(N^2) is **inherent replication** (each entity ->
  each nearby player). A spatial grid cannot cull genuinely-nearby players, a
  conservative cull breaks removals (desync), and network LOD hits the same
  close-high-interest wall as the AI stride. See
  [`../../research/docs/bottlenecks.md`](../../research/docs/bottlenecks.md) §5.

So the next-best levers are **not Harmony patches** - they are process/ops and config:

| Lever | Type | Why | Status |
|---|---|---|---|
| Boehm RAM-headroom (`GC_FREE_SPACE_DIVISOR=1` env) | process/env, EAC-safe | GC is ~30% of aggregate CPU; 128 GB host - trade RAM for **fewer collections** | **VALIDATED, see below** |
| ~~CPU affinity (naive main-thread pin)~~ | ops (host) | | **MEASURED LOSS** - pinning the sim thread to a fixed core HURT (jitter +122%): it overrides Ryzen CPPC preferred-core boost + adds cross-CCD latency. OS scheduler wins. See HOST_TUNING. Knob stays opt-in, default off. |
| `settargetfps` | vanilla console | caps the render/update rate (dedicated tick is separate) | documented |
| `ServerMaxAllowedViewDistance` (default 12) | vanilla config, gameplay tradeoff | fewer interested players per entity -> less replication (the only lever for the O(N^2) wall) | config |
| `MaxSpawnedZombies` / spawn caps | vanilla config, gameplay tradeoff | fewer entities -> less entity-tick | config |
| P4 `InitScan` node reuse | Harmony (UNSAFE) | #1 large-alloc, external-DLL iterator - fragile | **BUILT v1.8.0, default off - see §3c** |

**Boehm RAM-headroom A/B (2026-07-20, `GC_FREE_SPACE_DIVISOR=1` vs default, 40 bots
+ ~350 zombies):** sessions `015900` (default) / `020645` (headroom). Env verified in
`/proc/<pid>/environ`. **Boehm honors it:** full collections **2 -> 1**, total STW
**102.8 -> 72.5 ms (-30%)**, incremental slices -12% - and it did this with *higher*
gross churn (10.35 vs 8.7 MB/s), so the collection-frequency cut is the clean signal.
ms_per_tick read 58.6 -> 49.9 (-14.7%) but is **confounded** (default sustained ~370
zombies vs headroom ~326). Tradeoff: the *worst single* STW grew 52 -> 72 ms (bigger
heap = bigger mark; still ~1.4 ticks). **Verdict: a genuine EAC-safe, zero-code,
zero-fidelity-risk lever** - trades RAM (128 GB available) for fewer GC collections.
**Ship as a launch env var** (`GC_FREE_SPACE_DIVISOR=1` or `2`), NOT a mod P/Invoke
(env is EAC-safe and set before GC init; the mod path would force EAC-off). Prefer
`2` if RSS matters; `1` for max headroom. Not a `ms_per_tick` headline (GC is
downstream of allocation) but a real STW-smoothness win. See
[`../../research/docs/runtime-tuning.md`](../../research/docs/runtime-tuning.md).

## 3c. P4 InitScan node-array pool (first UNSAFE lever, A/B 2026-07-20)

`InitScanPoolPatch` (v1.8.0, `Pathfinding.PoolInitScanNodes`, **default off**)
transpiles the external `AstarPathfindingProject.dll` iterator
`LayerGridGraph.<ScanInternal>d__21:MoveNext()` to reuse the graph's fixed-size
`LevelGridNode[]` (`Array.Clear` + reuse when `Length==count`) instead of
`newarr LevelGridNode[]` every grid move. It attacks the **#1 large-allocation**
site (`AstarVoxelGrid.InitScan`, the megapause feeder).

**A/B (p4_off vs p4_on, 32 bots + ~314 zombies, 150 s, sessions `032225` / `033047`,
matched counts off 313->305 / on 314->309):**

| metric | off | on | delta |
|---|---|---|---|
| ms_per_tick | 48.6 | 46.7 | -3.9% (noise) |
| gross alloc MB/s | 8.07 | 8.33 | +3.2% (noise) |
| full collections | 3 | 3 | 0 |
| TickEntities totalMs | 39332 | 38260 | -2.7% (noise) |
| top large-alloc #1 | **`AstarVoxelGrid.InitScan`** | *(gone: `Entity.OnDestroy`, `StabilityInitializer`, `BlockEntityData`)* | **eliminated** |
| top churn | incl. `InitScan` | *(InitScan gone)* | **eliminated** |

**Mechanically works, cleanly:** server log `rerouted 1 LevelGridNode newarr ->
patched LayerGridGraph+<ScanInternal>d__21:MoveNext()`, `matched methods=7`, no
MISSING, **no pathfinding exception**. The `InitScan` alloc left both the large-alloc
and churn top lists (causal - it was #1 off, absent on). **Fidelity signals clean:**
zombies stable (309 alive), `TickEntities` cost comparable (still path-following; a
corrupted graph would freeze entities and collapse that cost - it didn't).

**But no measurable steady-state win:** gross alloc flat, collections unchanged. The
node array is **large but infrequent** (only on grid moves), so removing it doesn't
dent per-tick churn (dominated by `ItemStack.Clone` etc.) or the collection count at
a 150 s heap far below the 6.9 GB megapause threshold. Its real value is
**long-session heap-growth / megapause-tail** reduction, which a short bench cannot
capture.

**Verdict: keep BUILT, default off.** It is the cleanest unsafe lever (bounded,
concurrency-proven, fail-visible, no fidelity break in-bench) but pays off only on
multi-hour / blood-moon sessions where the large-object heap actually approaches the
megapause. **Final AI-fidelity sign-off needs a visual blood-moon soak** (bench signals
are necessary, not sufficient - subtle wall-clipping would not throw an exception).
Same honesty as the other unsafe levers: mechanically sound, marginal in benchable
steady-state.

## 3d. P4 fidelity soak (pool ON, 25 min sustained, 2026-07-20)

The A/B (§3c) proved P4 eliminates the alloc cleanly but gave no fidelity confidence
over time (a reused buffer could corrupt nav slowly, or leak). Soak: **pool ON only,
8 x 150 s captures over ~25 min, 32 persistent bots + ~300 zombies, heap 9.6 -> 10.3
GB** (already in the megapause regime). Per-sample: bot count (load-valid guard),
`alive` (AI-freeze guard), RSS, worst STW, and a cumulative grep for **any**
pathfinding exception (`NullReference|IndexOutOfRange|Argument` near
`Astar|LayerGridGraph|LevelGridNode|ScanInternal|Path|Grid`).

| t (min) | bots | alive | RSS MB | worst STW ms | nav exc |
|---|---|---|---|---|---|
| 3.2 | 32/32 | 352->349 | 9624 | 51.8 | 0 |
| 6.3 | 32/32 | 349->345 | 9891 | 55.2 | 0 |
| 9.5 | 32/32 | 342->326 | 10133 | 57.8 | 0 |
| 12.7 | 32/32 | 316->256 | 10156 | 60.2 | 0 |
| 15.8 | 32/32 | 304->229 | 10144 | 66.7 | 0 |
| 19.0 | 32/32 | 306->279 | 10539 | 65.3 | 0 |
| 22.2 | 32/32 | 364->319 | 10555 | 75.2 | 0 |
| 25.3 | 32/32 | 314->298 | 10295 | 78.3 | 0 |

**Fidelity: CLEAN.** Zero pathfinding exceptions across 25 min of sustained heavy
pathing at a 10 GB heap. `alive` stayed stable (zombies pathing, no freeze / mass
death), bots held 32/32 the whole run. **No leak:** RSS grew +672 MB then plateaued
(~10.1-10.5 GB); the reused node buffer does not accumulate. The unsafe lever is
**safe under load** - cleared for opt-in use.

**Two honest limits:**
1. **Still no proven perf win.** This is pool-ON only; there is no matched pool-OFF
   soak at a 10 GB heap, so the STW climb (51.8 -> 78.3 ms, monotonic with heap
   growth = the megapause-tail relationship: bigger heap = bigger mark) **cannot be
   attributed to or against P4**. Consistent with §3c: P4 buys fidelity-safe alloc
   elimination, not measurable speed.
2. **STW 78 ms worst != the 479 ms megapause.** That 479 ms was a *forced* full
   collect on a GC-disabled 6.9 GB heap (the `GcMegapauseTest` probe). *Natural*
   collections stay partial/incremental even at 10 GB (78 ms worst here). The 479 ms
   is a worst-case forced event, not steady state.

The one gate the soak cannot close is **visual wall-clipping** (headless: "pathing" is
inferred from stable `alive` + normal `ms_per_tick` + zero exceptions, not seen). A
human-client watch during a real blood moon is the final sign-off; every automated
signal is green.

**Bench-driver bug found + fixed mid-soak:** the first attempt collapsed at sample 2
(`alive` 282 -> 5). Root cause was **not P4** - `modab.start_bots` hard-codes
`LOADGEN_TIMEOUT=600000` (10 min), so all 32 bots hit `timeout_alive` and disconnected
~11 min in; with no players, spawned zombies despawned. Fixed with a soak-local
`start_bots_long()` (38 min lifetime) + a per-sample `bots/32` guard that flags a
collapse inline instead of misreading it as corruption.

## 3e. Chunk-send throttle (P6, EXPERIMENTAL, 2026-07-20)

`ChunkSendThrottlePatch` (v1.9.0, `WorldTransfer.ChunkPackagesPerObserverPerTick`,
default 3 = vanilla) transpiles the batch-cap constant in
`ChunkManager.SendChunksToClients` to spread a mass-join chunk transfer across more
ticks. Built + smoke-verified (`rerouted 1 chunk batch-cap constant -> patched
ChunkManager:SendChunksToClients()`, matched methods=8, no exception), tests cover the
clamp + deadlock guard. **Kept as an opt-in experimental knob; NOT validated.**

Motivation was the observed "other players lag when someone joins/transfers." RE
(bottlenecks §3) flagged the chunk pipeline as ~56-60% of *instrumented sections* - but
that is section-relative, not tick-relative; in absolute terms it is ~1-2% of the tick
wall at reachable loads. Three A/Bs tried to size the prize:

| test | method | result |
|---|---|---|
| clustered join | 40 bots join at spawn (share chunks) | `SendChunks` +559 ms (~1.2% tick); bots survive; too small to show a throttle win |
| simultaneous spread | 40 bots teleported to distinct unexplored regions | multi-minute **load/gen freeze**, synthetic bots time out and disconnect (0 players); the freeze is region **load/gen**, not send |
| staggered spread | teleport one region every 4 s | trivial - `SendChunks` +13 ms (noise), stall even dropped; top riser was `UpdateGraphs` |

**Findings:** (1) `SendChunksToClients` (the P6 target) is modest and downstream in
every *survivable* test. (2) The catastrophic join stall is **synchronous region
load/generation on the sim thread** (`RegionFileManager` + chunk gen), which P6 does
not throttle. (3) **Measurement wall:** the regime that actually hurts (a wave landing
in distinct unexplored regions at once) stalls the server hard enough to kill the
synthetic bots - real clients ride it out as lag, bots disconnect - so it cannot be
cleanly A/B'd with the current harness.

**Verdict: P6 is mechanically sound, low-risk, default-inert, but unproven and likely
mis-targeted.** Kept as an experiment. The real join-lag lever is the sim-thread chunk
load/gen (a big, *risky* structural target: players need those chunks to not fall
through the world, and moving gen off-thread races the sim). Next honest step if
pursued: a load/gen profile with a bot harness that survives the freeze (raised
keepalive tolerance), to size the real cost before touching the gen path.

## 3f. Explosion cost anatomy + particles skip (v1.10.0, 2026-07-20)

At the blood-moon standard load (64 players + ~550 endgame zombies, GS250; see
`7dtd-loadgen/BLOODMOON.md`), `GameManager.explode` was a top-3 per-frame cost
(~10 ms/explosion, ~220 explosions per window: exploding cops + demolishers).
Sub-split via four new bridge sections (`Explosion:AttackBlocks`,
`Explosion:AttackEntites`, `GameManager:ExplosionClient`, `GameManager:ChangeBlocks`):

| stage | avg/explosion | nature |
|---|---|---|
| `Explosion.AttackBlocks` (damage sphere) | 1.3 ms | gameplay |
| `Explosion.AttackEntites` (OverlapSphere + LOS + DamageEntity) | 0.2 ms | gameplay |
| `GameManager.ExplosionClient` | 9.0 ms | mixed, see below |
| - of which block-destruction application (`ChangeBlocks`: SetBlock + chunk dirty + stability + broadcast to 64 clients) | ~7.9 ms | **gameplay, the true bulk** |
| - of which `Object.Instantiate` of the visual explosion prefab | ~1.1 ms | **pure waste on a headless server** |

**A/B (vanilla vs `SkipOnDedicated.ExplosionParticles`, matched ~550z arms):**
explode 10.57 -> 9.42 ms/explosion (-11%), ExplosionClient 9.00 -> 7.87 ms. The
patch (`ExplosionParticlesPatch`, default on) keeps every gameplay side effect
(physics push, block changes, quest event; all vanilla-gated) and skips only the
prefab Instantiate.

**Honest verdict:** a real but small win (~1.1 ms/explosion, ~10%). The initial
hypothesis (Instantiate = 85%) was WRONG - the bulk is `ChangeBlocks`, i.e. applying
and broadcasting the block destruction, which is gameplay and irreducible by a skip.
The remaining explosion-cost levers are ops knobs with gameplay tradeoffs
(`BlockDamageAIBM` server config scales AI blood-moon block damage down, directly
shrinking the ChangeBlocks batches) - not code.

## 3g. Entity-replication stride (v1.11.0, A/B 2026-07-20)

`EntityDistributionStridePatch` (config `Network.EntityDistributionEveryTicks`,
default 1 = vanilla, clamp [1,4]) prefixes `NetEntityDistribution.OnUpdateEntities`
to run the replication pass every Nth tick. Safety basis (IL): the pass is a
**state-driven scan** - interest is recomputed from current positions each visit and
change flags persist on entries until sent - so a skipped tick *delays* replication
by 50 ms, it cannot *lose* state. Clients interpolate motion; 10 Hz entity
replication is the console-game norm.

**A/B at the blood-moon standard (64 players + ~580 endgame zombies, stride 1 vs 2):**

| metric | stride 1 | stride 2 | delta |
|---|---|---|---|
| OnUpdateEntities avg | 7.69 ms | 4.25 ms | **-45%** |
| OnUpdateEntities total/window | 38.5 s | 21.2 s | **-45%** |
| GameManager.UpdateTick avg | 17.2 ms | 14.1 ms | -18% |
| frame time | 230 ms | 175 ms | -24% (mild zombie-count confound, 594 vs 572) |
| players / alive | 64, stable | 64, stable | connections + entity counts clean |

**The largest single lever measured since P1** - it halves one of the two O(N^2)
player-axis walls with a one-line skip. The bridge times the method wrapper, so
"calls" stay ~equal in both arms; the halved *avg* is the skip signature (half the
visits do full work, half return immediately).

**Ships default OFF (stride 1).** The remaining gate is human-eye fidelity: bots
cannot see rubber-banding, and +50 ms staleness on fast movers (feral sprinters at
blood moon) is exactly the case a human should confirm before production. Stride 3-4
(6.7/5 Hz) exists for headroom experiments, not production.

## 4. Config reference (every knob, all independently toggleable)

```
Enabled, DedicatedOnly
AiLod.{Enabled, FullAiDistSq, MediumAiDistSq, FullScale, MediumScale, FarScale,
       SkipTasksFarDistSq, SkipTasksUnlessAlerted}
SkipOnDedicated.{DynamicMusic, WaterSplash, EnvironmentAudio, ClothAndJiggle,
    ExplosionParticles (true=skip visual spawn; gameplay preserved, see §3f)}
DynamicMesh.{Enabled, OnlyPlayerAreas, PlayerAreaChunkBuffer, MaxRegionLoadMsPerFrame, MaxActiveSyncs}
Gc.{Enabled, SkipForcedCollect, SafetyCollectAboveMB (0=auto), SafetyCollectRamFraction,
    Incremental, IncrementalPauseTargetMs}
Pathfinding.{GraphUpdateEveryTicks (1=vanilla), MoveRescanThresholdSq (100=vanilla),
    PoolInitScanNodes (false=vanilla; UNSAFE, external-DLL transpiler, see §3c)}
Network.{FastSingleTargetSend,
    EntityDistributionEveryTicks (1=vanilla; 2=10 Hz replication, -45% on the wall,
    needs human-eye pass, see §3g)}
WorldTransfer.{ChunkPackagesPerObserverPerTick (3=vanilla; EXPERIMENTAL, see §3e)}
Diagnostics.{GcMegapauseTest, WarmupSeconds, GrowSeconds}   # never enable on a live server
```
Init log tags each matched patch `(matched but config-disabled)` when its toggle is
off; numeric fields are clamped + logged by `Normalize()`.

---

## 5. Next: biggest-gain unshipped levers (in progress)

**Two "big" levers turned out already-done by the game (RE 2026-07-20):**
- **#5 buffer presize/retain** - `PooledExpandableMemoryStream.Reset()` = `SetLength(0)`
  already retains the buffer. Non-issue.
- **L1 serialize-once (build layer)** - `updatePlayerList` already builds each package
  once + broadcasts via `SendToPlayers` + change-gates the player-independent ones.
  The only residual re-serialization is per-connection in the **writer threads**
  (`taskSerialize`), which is off the main tick, feeds only the #4 allocator, and
  needs a risky shared-buffer rewrite. Deprioritized.

**Entity-axis is largely irreducible (RE 2026-07-20):** the standard-load CPU is
dominated by **close-combat AI** (near zombies must run full-rate `updateTasks` /
`UpdateMoveHelper` for combat), the far band is already skipped, and mid-band
striding showed no win (thin shell). So the entity axis has no safe Harmony lever
left - the cost is combat-fidelity-bound.

**Remaining genuine biggest-gain levers (all large/risky projects):**
1. **Spatial interest grid** - the highest ceiling: collapses the O(N^2.26/2.27)
   player-axis walls (450-500p death-spiral) toward linear. New subsystem; rewires
   `NetEntityDistribution` all-pairs interest + `GetClosestPlayer`; desync risk.
2. **P4 `InitScan` array reuse** - kills the #1 large-alloc (megapause feeder), but the
   alloc is inside the external `AstarPathfindingProject.dll` `<ScanInternal>d__21`
   **iterator state machine** - a transpiler there is fragile; `LevelGridNode` is a
   class so the per-node churn needs object reuse too.
3. **Off-sim `Chunk.write` encode** - the chunk pipeline is 56-60% of tick, but moving
   the 601-IL encode off the sim thread races world state.

**Honest status:** the safe implementable win (#1) is shipped + validated. The rest
are project-scale with real corruption/desync risk; the responsible path is one
careful phased build with a full fidelity gate, not rushed patches. The game is also
more optimized than early RE assumed (#5, L1-build already done).

**Main-thread profiling confirms no single big safe target left (2026-07-20).** The
APM's `cpu_hot_paths.main_thread` view (perf `--tid` on the sim thread) ranks the hot
game code *for the tick itself*: `GameManager.Update` self 4.6%, then `SendToPlayers`
1.5%, per-entity `OnUpdateLive`/`EntityAlive.Update` ~2.3%, `EntitySeeCache.CanSee`
(AI vision) 0.6%, `EAITaskList` 0.5%, `KinematicCharacterMotor` (physics) 0.4%,
`AstarVoxelGrid.CalcBlockingFlags`/`CalculateConnections` (nav scan) 0.4%,
`GetClosestPlayer` 0.3%. **No single dominant frame** - the tick is fanned across
replication + per-entity AI (update/vision/EAI) + character physics + nav scan. So
the remaining safe Harmony levers are small (each < 1% of tick); the real gains are
the O(N^2) network wall (spatial grid, risky) and the allocation floor (~30% of
aggregate CPU is GC + array-init). The APM now auto-discovers hot paths
(`research/docs/bottlenecks.md` §5b), so future targets are data-driven, not guessed.
