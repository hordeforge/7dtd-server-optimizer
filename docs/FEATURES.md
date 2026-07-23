# EfficientServer features

**Owns:** shipped EfficientServer feature groups and validation notes.  
**Not:** host topology ([HOST_TUNING](HOST_TUNING.md)), APM evidence ([../../7dtd-apm/docs/APM.md](../../7dtd-apm/docs/APM.md)), candidate backlog ([OPTIMIZATION_CANDIDATES](OPTIMIZATION_CANDIDATES.md)).  
**Dev process:** [DEVELOPMENT](DEVELOPMENT.md).

## AI level of detail

`AiLodPatch` scales distant AI work using configured full, medium, and far
distance bands. `UpdateTasksLodPatch` can skip distant non-alert task updates.
These are behavioral optimizations and must be validated for combat, sleepers,
quests, and multiplayer separation.

**Mid-band tick-striding (v1.7.0, structural).** `UpdateTasksLodPatch` now has three
bands on `aiClosestPlayerDistSq`: close = full rate; **mid (`MediumAiDistSq` <= d <
`SkipTasksFarDistSq`) = run the heavy `updateTasks` tail every `AiLod.MidTickStride`-th
frame, striped by entity id**; far = skip. The tail includes the 1236-IL
`UpdateMoveHelper` that stock does not throttle via `aiActiveScale`, so at high zombie
counts (the standard 64p+300z load, entity-axis dominated) this spreads the per-tick
entity cost by up to the stride factor. `MidTickStride` default **1 = off** (every
tick); clamped [1, 20]. `CheckDespawn` still runs every tick; alerted / targeting /
investigating / active-sleeper entities are never strided. Fidelity: mid-distance
(20-50 m) zombies react at (20/stride) Hz - imperceptible at that range, but validate
under blood moon / a charging horde before raising it.

**A/B (2026-07-20): inconclusive / low value at the standard close-combat load.**
The entity-axis cost is close-combat-bound (near zombies need full AI, far ones are
already skipped), so the 20-50 m mid band is a thin shell with little to stride - no
measurable win (see [`RESULTS.md`](RESULTS.md)). Safe and default-off; may help
mid-range wandering-horde profiles, but do not expect a win on the standard load.

Two fidelity guards (v1.4.0): `UpdateTasksLodPatch` invokes `EntityAlive.CheckDespawn()`
before skipping, because that check is the first step *inside* `updateTasks`; without
it, far wandering-horde / bloodmoon / lifetime-expired entities would accumulate
instead of despawning. `AiLodPatch` toggles cloth level-triggered (off far, back on
near) rather than one-way, so it self-heals on approach instead of leaving cloth off
permanently after one far excursion (visible only on a player host; cosmetic on a
true dedicated server).

## Dedicated-only skips

`DedicatedSkipPatch` avoids selected presentation work that has no dedicated
server consumer, including configured music, splash, environment-audio, cloth,
and jiggle paths. Every optional target reports patch failure without hiding it.

## Dynamic mesh budgets

`DynamicMeshBudgetPatch` applies player-area filtering, chunk buffering,
per-update region-load time, and active-sync limits. Validate saves, region
streaming, and distant simultaneous players before retaining aggressive values.

## GC pause guard (A7)

`GcGuardPatch` transpiles `GameManager.gmUpdate` to reroute its single forced
`GC.Collect()` (fired every ~120 s via `gcCountdownTimer`) through
`MaybeCollect`. On Unity's Boehm GC that forced full stop-the-world pass is a
self-inflicted late-tick hitch - Boehm already collects on allocation pressure.
With `Gc.SkipForcedCollect` (default true) the forced collect is skipped, but a
heap-ceiling safety collect still fires if the managed heap runs away. The
ceiling is **host-aware**: `Gc.SafetyCollectAboveMB` = 0 (default) means AUTO =
`Gc.SafetyCollectRamFraction` (default 0.5) x host RAM (`SystemInfo.systemMemorySize`),
so it scales with the machine and stays well above the real 5-10 GB working heap
under load (a fixed low ceiling would fire every frame and defeat the guard). Set
`SafetyCollectAboveMB` to a positive MB value for a hard override. Set
`Gc.Enabled` or `Gc.SkipForcedCollect` false to restore vanilla behavior. It
changes no wire bytes and needs no client mod (server-side only), so a vanilla
client connects and nothing desyncs - but note the server runs **EAC-off** (see
"Anti-cheat" below), like any C# mod.

## GC incremental mode (opt-in)

`GcIncremental` P/Invokes the game's own Boehm lib (`monobdwgc-2.0`,
`GC_enable_incremental` + `GC_set_time_limit_ns`) to switch the existing
collector into incremental / generational mode: collection runs in small
increments across frames with an optional per-pause cap
(`Gc.IncrementalPauseTargetMs`) instead of one long stop-the-world pass. This is
a *mode* of the GC already in the process, not a replacement (you cannot swap
the collector on Unity Mono). Off by default (`Gc.Incremental`) because the
write-barrier adds per-allocation overhead whose net value is workload-dependent
- measure with the APM GC window before retaining. Complements the GC guard: the
guard removes the forced periodic collect, incremental mode shortens *every*
collect including the churn-driven ones.

Server frame rate is NOT the tick rate: `UpdateTick` runs per frame, but the full
entity-sim/replication tick is gated at ~20 Hz regardless of fps (measured, RESULTS
3k). `settargetfps <N>` sets the frame rate live but does not persist;
`Server.TargetFps` (v1.14.0) applies it at every game start. Higher fps buys
steadier delivery/lower jitter of the same 20 Hz data - a polish, not a TPS or
capacity change.

## GC megapause diagnostic (opt-in, never ship enabled)

`GcDiagnostics` (`Diagnostics.GcMegapauseTest`, default **false**) proves *why*
deferring GC is not a performance win. It P/Invokes Boehm `GC_disable`, grows the
heap under live load for `GrowSeconds`, then re-enables and times one forced
`GC_gcollect`. Measured (v1.5.1, heavy load): a single collect of a **6.91 GB heap
(~5.6 GB live)** froze the server **479 ms** (~10 missed ticks). It confirms a
never-collect-then-one-big-collect scheme just concentrates the pause; the real
lever is cutting allocation ([`ALLOCATION_UPSTREAM.md`](ALLOCATION_UPSTREAM.md)).
Diagnostic only - it disables the collector, so never enable it on a live server.

## Pathfinding graph throttle (B12 / P1)

`AstarGraphThrottlePatch` is a Harmony prefix on `AstarManager.UpdateGraphs`, the
top managed section under load (66 ms) and, per corrected APM alloc-attribution,
the top allocator too (`AstarVoxelGrid.InitScan`). Vanilla repositions the
player-following voxel nav-graphs every tick (20 Hz); the prefix runs that
maintenance only every `Pathfinding.GraphUpdateEveryTicks` ticks (default 4 →
5 Hz), returning `false` to skip on the others. `UpdateGraphs` both queues and
drains grid moves internally (`UpdateMoveGraph` is called inside it, not a
separate per-tick method), so throttling slows the whole maintenance cadence by
N; nothing is permanently stranded (`moveList` persists), and AI keeps moving
because path *compute* (`FindPath` → `PathFinderThread`) is the separate
every-tick system. Only the walkability window lags, which the load-test fidelity
gate checks. `GraphUpdateEveryTicks` is the single knob:
`1` = vanilla (no throttle), `>1` = throttle to (20/N) Hz. It does **not**
enable/disable pathfinding - path compute and scans always run. Server-internal,
no wire change
(vanilla client connects); code → EAC-off. See
[`PATHFINDING_OPTIMIZATION.md`](PATHFINDING_OPTIMIZATION.md).

`AstarMoveThresholdPatch` (v1.4.0, P2) is the complementary lever: a transpiler on
`AstarManager.UpdateGraphPos` that raises the rescan dead-zone (the `SqrMagnitude`
compared against a constant `100` sq grid units before a grid is queued for a
rescan) to `Pathfinding.MoveRescanThresholdSq` (default **100 = vanilla**, clamped
`[100, 10000]`). A larger dead-zone means a grid rebuilds (`AstarVoxelGrid.InitScan`,
the #1 allocator) only after drifting more cells, cutting scan CPU and allocation
from the frequency side. It multiplies with the cadence throttle: P1 lowers how
often maintenance runs, P2 lowers the per-visit rescan probability. Strands nothing
(a below-threshold grid is re-tested next visit; fresh grids bypass the gate via
`IsFullUpdateNeeded`). Fails visibly (MISSING) if the `100` constant drifts.

## Network: single-target fast send (bang-for-buck #1)

`FastSendPatch` is a Harmony prefix on `ConnectionManager.SendPackage`. Vanilla
linear-scans the whole `Clients` list filtering by `entityId` to find one
recipient; `SendToPlayers` calls it once per tracked player and `updatePlayerList`
calls `SendToPlayers` ~7x per entity per tick, so replication fan-out is
O(entities x players x clients). The prefix short-circuits **only the pure
single-target case** (send to exactly one attached entity's client, no other
filter mode) through the existing O(1) `ClientInfoCollection.ForEntityId` map, then
reuses the game's own per-client enqueue (`ClientInfo.SendPackage`). Provably
equivalent to vanilla: `entityId` is unique per client, so vanilla also enqueues to
exactly one client, giving the identical send-queue refcount (one
`RegisterSendQueue` + one `AddToSendQueue`). Every other filter mode (all-but,
in-range, only-attached/not-attached) falls through to vanilla untouched.

Config: `Network.FastSingleTargetSend` (default **false**). Independent toggle,
own config section. Server-internal, no wire change (vanilla client connects);
code -> EAC-off.

**Validated (2026-07-19, pure player-scale, off vs on):** correctness held
(**60/60** and **120/120** clients stayed connected and functional - the send path
is equivalent). Perf **scales with client count** as designed: ms_per_tick
**-1.8% at 60 players -> -4.2% at 128 players**; `ConnectionManager.Update` (which
holds the scan) **-0.2% -> -5.2%**. Marginal at low player counts (the scan is a
handful of cheap int-compares), it grows toward the 450-500 player death-spiral
regime it targets. Correct, safe, and the benefit rises with load.

## Pathfinding node-array pool (P4, UNSAFE, v1.8.0)

`InitScanPoolPatch` is the first **unsafe** lever (opt-in, default off). It attacks
the **#1 large-allocation** site: `LayerGridGraph.ScanInternal` re-mints the nav node
array (`newarr LevelGridNode[width*depth*layerCount]`) on every grid move, even
though grid dims are fixed. A transpiler reroutes that `newarr` through a helper that
**reuses the graph's existing node array** (`Array.Clear` = identical to a fresh
null-filled array; the scan re-populates every cell) when the size matches.
Concurrency is safe: scans hold AstarPath's work-item lock, so no path worker reads
`graph.nodes` mid-scan.

Why unsafe: it transpiles a **compiler-generated iterator `MoveNext` in the external
`AstarPathfindingProject.dll`** - fragile to A* DLL updates. It fails visibly (throws
-> MISSING) if the exact `newarr LevelGridNode` is gone, is gated by
`Pathfinding.PoolInitScanNodes` (default **false**), and **must pass a nav fidelity
check** (zombies still path to players; no pathfinding exceptions) before use. Cuts
the megapause feeder at source (complements the `GC_FREE_SPACE_DIVISOR` env, which
only cuts collection frequency). Code -> EAC-off. See
[`../../7dtd-research/docs/aggressive-optimizations.md`](aggressive-optimizations.md) §3.

## Animator LOD (v1.15.0, default off)

`AnimatorLodPatch` runs calm, distant zombies' animation rigs at a reduced rate
(Animator disabled + manual delta-scaled `Animator.Update` pump every Nth frame).
The raw animator cost is measured at 19.9 ms/frame (28%) at horde scale, but the
correctness exemptions (near players / attacking / stunned) cover almost the whole
horde during clustered sieges, so this pays only for dispersed roamer populations -
see RESULTS 3m-bis for the honest A/B. Server-side animation is client-invisible;
exemptions exist for combat-timing fidelity.

## Ambient light-spectrum skip (v1.14.3, default on)

`SkipOnDedicated.AmbientLightSpectrumUpdates` skips the per-frame ambient-spectrum
lerp whose only outputs are RenderSettings colors nothing headless reads (RESULTS 3n).

## TickGuard emergency load-shedding (v1.13.0, default off)

`TickGuardPatch` (config `TickGuard.*`) sheds the farthest-from-any-player enemies
in batches (silent despawn: no loot/XP/corpse) when the tick stays past the point
throttling can fix, never below `MinEnemiesKept`. Validated live: with the governor,
drove a 522-zombie overload (3.5x the capacity ceiling) back from 167 to 56 ms/frame
autonomously. Default off because it removes entities - a real gameplay trade
(thinner horde at 20 TPS instead of a full horde at 3 TPS). See
[`RESULTS.md`](RESULTS.md) §3j and [`CONFIG.md`](CONFIG.md).

## Adaptive load governor (v1.12.0, default ON since v1.13.0; tier 2 in v1.16.0)

`GovernorPatch` (config `Governor.*`) watches the tick-interval EMA and moves the
proven throttle levers between vanilla and throttled (replication stride 2 + doubled
graph cadence) with hysteresis and a cooldown, logging every transition. Validated
live: engages under a 435-zombie overload (cushioning 299 -> 128 ms/frame), restores
vanilla within seconds of the load clearing. It schedules existing levers only.
Note the recovery threshold must sit ABOVE 50 ms - the healthy loop idles at exactly
50 ms and never below (enforced in config normalize).

**Tier 2 (v1.16.0, `Governor.AnimatorEmergency`, default off):** when throttling has
not recovered the tick and the EMA exceeds `EmergencyOverMs` (80), disable ALL
zombie animators - measured **~40% of the saturated 64-player frame** (147 -> 85 ms,
the fence check: the animator burden at 64p is mostly main-thread JOB-FENCE waiting,
which triples per zombie vs 24p). Combat timing degrades (timer-only attack cadence,
no stagger); nothing despawns, clients see no visual change. Steps down one tier at
a time. Live-validated: full autonomous chain THROTTLED -> ANIMATOR EMERGENCY ->
step-down + EXIT. See [`RESULTS.md`](RESULTS.md) §3i, §3o.

**Stays default-off - exit path has a known wedge** (human eval, RESULTS 3s): a
re-enabled animator evaluates but never emits root motion again
(`deltaPosition=0`), leaving zombies at crawl speed. Culling-mode rework designed,
tracked in TODO; until then treat tier 2 as a bench lever.

## Entity-replication stride (v1.11.0, default off)

`EntityDistributionStridePatch` (config `Network.EntityDistributionEveryTicks`,
1 = vanilla) runs the per-tick replication pass every Nth tick. Stride 2 = 10 Hz
entity replication (+50 ms staleness; clients interpolate), measured **-45%** on
`NetEntityDistribution.OnUpdateEntities` - one of the two O(N^2) player-axis walls -
at the blood-moon standard, with stable connections and entity counts. Safe by
construction (state-driven scan: skips delay, never lose), but ships default off
until a human confirms fast movers do not visibly rubber-band at stride 2. See
[`RESULTS.md`](RESULTS.md) §3g.

## Explosion particles skip (v1.10.0)

`ExplosionParticlesPatch` (config `SkipOnDedicated.ExplosionParticles`, default on)
prefixes `GameManager.ExplosionClient` to skip `Object.Instantiate` of the visual
explosion prefab on the headless server while preserving every gameplay side effect:
the physics push (`ApplyExplosionForce`, vanilla-gated on the prefab existing), the
block changes (`ChangeBlocks`), and the quest event (`DetectedExplosion`). Returns
null exactly like the vanilla no-prefab path.

Measured A/B at the blood-moon standard (64p + ~550 endgame zombies): explode
10.57 -> 9.42 ms/explosion (-11%). Honest scope: the bulk of explosion cost is the
block-destruction application (gameplay, preserved); this removes only the
pure-waste visual spawn. See [`RESULTS.md`](RESULTS.md) §3f.

## Chunk-send throttle (P6, EXPERIMENTAL / UNVALIDATED, v1.9.0)

`ChunkSendThrottlePatch` transpiles the unique batch-cap constant (`ldc.i4.3`) in
`ChunkManager.SendChunksToClients` so `WorldTransfer.ChunkPackagesPerObserverPerTick`
controls how many chunks each observer encodes+sends per tick (each is a synchronous
`NetPackageChunk.Setup` / `Chunk.write` on the sim thread). **Default 3 = vanilla**
(byte-identical, inert); lowering to 1-2 spreads a mass-join transfer across more
ticks. Fail-visible (throws -> MISSING if the constant moves); floor of 1 is a
deadlock guard (0 would stall the send loop). Code -> EAC-off.

**Status: EXPERIMENTAL, kept as an opt-in knob but NOT validated.** Three A/Bs
(2026-07-20) could not demonstrate a win:
- **Clustered join:** `SendChunksToClients` +559 ms (~1.2% of tick) - real but small.
- **Simultaneous spread** (bots teleported to distinct unexplored regions): a
  multi-minute **region load/generation freeze** that killed the synthetic bots - and
  that freeze is chunk **load/gen**, NOT the send path this patch throttles.
- **Staggered spread** (one region at a time): trivial, nothing spiked.

Conclusion: the send encode is modest and downstream; the real join-lag driver is the
**synchronous region load/gen on the sim thread**, which P6 does not touch. It is kept
because it is low-risk and default-inert, not because it is proven. See
[`RESULTS.md`](RESULTS.md) §3e.

## Lifecycle

Post-start setup (dynamic-mesh reapply, optional dedicated skips, GC incremental
enable) runs via the sanctioned `ModEvents.GameStartDone` hook - **not** a
Harmony patch on `StartGame`, since no IL match is needed just for "run after
startup" timing. `ModApi` loads configuration, applies each Harmony patch group
independently (logging the exact matched game methods and failing visibly if a
required target matches nothing), records mod / Assembly-CSharp / game versions
at startup, and registers the lifecycle handler.

## Anti-cheat (EAC)

EfficientServer is a **C# code mod**, so - like every code mod on 7DTD - it
cannot run while EAC is actively enforcing. The `ModInfo` sets
`SkipWithAntiCheat=true`, meaning the game **skips loading it when EAC is on**
(EAC stays enforcing, mod absent); set it false and the mod loads but the server
runs **EAC-off**. This is a property of loading a DLL, not of the hooking
mechanism - IModApi hooks and Harmony patches are identical to EAC. What the mod
*does* guarantee is that it is **server-side only, needs no client mod, and
changes no wire/save bytes**, so a vanilla client connects and nothing desyncs;
the server simply runs without EAC, as any code mod does. Only XML-only mods run
under EAC.

Instrumentation is supplied by `7dtd-apm`; workloads are supplied by
`7dtd-loadgen`. EfficientServer deliberately has no console profiler or load
generation commands. Development workflow: [`DEVELOPMENT.md`](DEVELOPMENT.md).
General modding rules: [`../../MODDING_BEST_PRACTICES.md`](../../MODDING_BEST_PRACTICES.md).
## Related docs

| Doc | Role |
|---|---|
| [DEVELOPMENT.md](DEVELOPMENT.md) | How to change EfficientServer |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Hot path map |
| [HOST_TUNING.md](HOST_TUNING.md) | Host topology (not Harmony) |
| [OPTIMIZATION_CANDIDATES.md](OPTIMIZATION_CANDIDATES.md) | Evidence backlog |
| [loop.md](../../7dtd-research/docs/loop.md) | Generic frame map |
| [APM.md](../../7dtd-apm/docs/APM.md) | Evidence |

## Changelog

- **2026-07-19:** Ownership/related docs polish.
