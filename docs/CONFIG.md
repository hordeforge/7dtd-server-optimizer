# EfficientServer configuration reference (v1.16.1)

**Owns:** every config option in minute detail - exact mechanism, gameplay impact,
measured performance gain, default + rationale.
**Not:** the A/B evidence trail (session IDs, curves): [RESULTS](RESULTS.md).
File: `Config/efficientserver.json` beside the mod DLL. Every lever is individually
toggleable; a matched-but-disabled patch logs `(matched but config-disabled)` at init.

**Defaults policy:** ON when a lever improves performance with **no gameplay
impact** (provable equivalence or headless-only work). OFF when it changes anything
a player could perceive (staleness, despawns, nav freshness beyond the validated
defaults) or is experimental/unproven. Diagnostics are always OFF.

---

## Top level

### `Enabled` (default `true`)
Master switch. `false` leaves every patch installed but inert (config can be
re-enabled and reloaded without a restart via `ReloadConfig`).

### `DedicatedOnly` (default `true`)
Patches act only when `GameManager.IsDedicatedServer` confirms a dedicated host;
unknown hosts fail closed (patch inert). Protects a client/host from accidentally
running server-only behavior. No reason to change.

---

## AiLod - entity-AI level of detail

Distance-banded AI update scaling for `EntityEnemy`. Bands are SQUARED distances
(100 = 10 m).

### `AiLod.Enabled` (default `true`)
- **Mechanism:** per-entity distance to the nearest player selects a band; the
  band's scale stretches AI update cadence (task re-evaluation, look/turn updates).
- **Gameplay impact:** distant zombies "think" less often. Within `FullAiDistSq`
  nothing changes; alerted/targeting entities are never scaled down.
- **Measured:** part of the original mod package (~-8% at healthy load, RESULTS §3).

### `FullAiDistSq` (100) / `MediumAiDistSq` (400) / `SkipTasksFarDistSq` (2500)
Band edges (squared meters: 10 m / 20 m / 50 m). Widening `FullAiDistSq` restores
vanilla behavior further out (costs CPU); shrinking it saves CPU but lets nearby
zombies react sluggishly - keep >= combat range.

### `FullScale` (1.0) / `MediumScale` (0.2) / `FarScale` (0.05)
Update-rate multipliers per band. `Normalize` enforces Full >= Medium >= Far.

### `SkipTasksUnlessAlerted` (default `true`)
Far-band entities skip the heavy `updateTasks` tail entirely unless alerted -
sleeping/idle far zombies cost near zero. Alert/aggro always restores full AI.

### `MidTickStride` (default `1` = off, clamp [1,20])
- **Mechanism:** mid-band entities run the heavy `updateTasks` tail (path follow +
  EAI + `UpdateMoveHelper`) every Nth frame, striped by entityId so cost spreads.
  `CheckDespawn` still runs every tick; alerted entities never stride.
- **Measured: no win** (RESULTS: the mid band is a thin shell - close entities need
  full AI, far ones are already skipped). Kept for experimentation.
- **Gameplay impact at >1:** mid-range zombies path/react on a delay.

---

## SkipOnDedicated - headless-only work removal

All default `true`: this work produces output only a renderer/speaker could show,
and a dedicated server has neither. **Zero gameplay impact by construction.**

### `DynamicMusicSystem` / `EnvironmentAudioUpdates` (true)
Skip the music conductor and ambient-audio graph updates (audible output only).

### `WaterSplashParticles` (true)
Skip `WaterSplashCubes.Update` (visual splash particles).

### `ClothAndJiggleBoneSimulation` (true)
Skip cloth/jiggle bone simulation (pure visual deformation).

### `AmbientLightSpectrumUpdates` (true, v1.14.3)
Skip the per-frame ambient light-spectrum lerp (~650 IL) whose only outputs are
`RenderSettings` ambient-color writes; the consumer chain (light level -> stealth)
is client-computed. Found by the client-only-code sweep (RESULTS 3n).

### `ExplosionParticles` (true, v1.10.0)
- **Mechanism:** prefix on `GameManager.ExplosionClient` skips
  `Object.Instantiate(WorldStaticData.prefabExplosions[i])` - the visual explosion
  prefab - while executing every gameplay side effect exactly as vanilla: the
  physics push (`ApplyExplosionForce`, gated on the prefab existing, like vanilla),
  the block destruction (`ChangeBlocks`), and the quest event
  (`QuestEventManager.DetectedExplosion`). Returns null = vanilla's no-prefab path.
- **Measured:** explode 10.57 -> 9.42 ms/explosion (-11%) at the blood-moon
  standard (~220 explosions). The bulk of explosion cost is the block-destruction
  application, which is gameplay and untouched (RESULTS §3f).
- **Gameplay impact:** none (server-side particle was never visible to anyone).

---

## DynamicMesh - dynamic (destruction) mesh maintenance

### `Enabled` (true) + `OnlyPlayerAreas` (true) + `PlayerAreaChunkBuffer` (2)
Bound dynamic-mesh regeneration to chunks near players (buffer = extra chunk ring).
- **Gameplay impact:** none in player-visible areas; distant destroyed-terrain
  meshes regenerate when a player approaches instead of immediately.

### `MaxRegionLoadMsPerFrame` (2) / `MaxActiveSyncs` (2)
Per-frame time budget for region loads and the concurrent mesh-sync cap - they
convert bursts into bounded per-tick work.

---

## Gc - managed-heap collection management

The BIG GC lever is a **launch env var**, not mod config (see "Launch environment"
below). These knobs manage the forced-collect path.

### `Gc.Enabled` (true) + `SkipForcedCollect` (true)
- **Mechanism:** vanilla `gmUpdate` forces `GC.Collect()` every ~120 s - a full
  stop-the-world mark of the entire heap (measured up to 479 ms at 6.9 GB) on a
  fixed timer regardless of need. The transpiler reroutes that call through a guard
  that skips it while the heap is below the safety ceiling.
- **Measured:** -28% late-tick overage at moderate churn (A7, RESULTS §1).
- **Gameplay impact:** none. Boehm still collects on allocation pressure; the guard
  only removes the fixed-timer full collect.

### `SafetyCollectAboveMB` (0 = AUTO) / `SafetyCollectRamFraction` (0.5)
The guard's safety net: force a collect anyway once the managed heap exceeds the
ceiling. AUTO derives it from host RAM (fraction x system memory) so a fixed number
can never sit below the real working set (which would fire every frame).

### `Incremental` (false) / `IncrementalPauseTargetMs` (0)
Opt-in Boehm incremental mode (collection in bounded slices). **Measured: marginal**
(churn is invariant; slice count rises as pause length falls). Off by default.

---

## Pathfinding - A* nav-graph maintenance

### `GraphUpdateEveryTicks` (default `4`, clamp [1,200])
- **Mechanism:** `AstarManager.UpdateGraphs` (per-player follow-graph maintenance:
  merge, repositioning scan-queue drain) runs every Nth tick instead of every tick.
  It does NOT gate path *computation* - only graph maintenance cadence.
- **Measured:** the headline lever. At a breaking load: **-28.5% ms_per_tick,
  failing -> healthy**. At the blood-moon ceiling it holds `UpdateGraphs` at ~4 ms
  avg (RESULTS §1, §3h).
- **Gameplay impact at 4 (5 Hz):** nav graphs reposition up to 200 ms later after
  fast player movement - not measurable in zombie behavior in any A/B. Raising far
  above 4 risks visibly stale walkability for sprinting players.

### `MoveRescanThresholdSq` (default `100` = vanilla, clamp [100,10000])
- **Mechanism:** a follow-graph queues a rescan only after the observer drifts more
  than sqrt(threshold) grid units (vanilla 10). Larger = fewer `InitScan` rebuilds
  (the #1 allocator) at the cost of a staler walkability window on fast motion.
- **Measured:** multiplies with the cadence lever; at 400 (2x drift) rescans drop
  ~4x under wandering load.
- **Gameplay impact:** above ~400, zombies pathing to a sprinting/driving player
  can briefly walk stale terrain data.

### `PoolInitScanNodes` (default `false`, UNSAFE, v1.8.0)
- **Mechanism:** transpiles the `<ScanInternal>` iterator inside the EXTERNAL
  `AstarPathfindingProject.dll` to reuse the graph's fixed-size `LevelGridNode[]`
  (`Array.Clear` + reuse when the size matches) instead of `newarr` on every grid
  move - eliminating the #1 large allocation.
- **Measured:** allocation eliminated (top-alloc list, RESULTS §3c) but **no
  benchable steady-state win**; 25-min soak at a 10 GB heap: zero pathfinding
  exceptions, no leak (§3d).
- **Why off:** external-DLL IL surgery (fragile across A* updates) with no measured
  perf payoff; the megapause it targeted is already eliminated by the GC env. Final
  gate: a human visual pass for wall-clipping.

---

## Network

### `FastSingleTargetSend` (default `true`, v1.13.0)
- **Mechanism:** prefix on `ConnectionManager.SendPackage`: the pure single-target
  case resolves the recipient via the O(1) `ClientInfoCollection.ForEntityId` map
  instead of vanilla's linear scan over all clients. Every other filter mode falls
  through to vanilla untouched.
- **Measured:** -4.2% at 128 players, scaling with player count (RESULTS §2).
- **Gameplay impact: none, provably** - entityId is unique, so vanilla also
  enqueues to exactly the one client the map returns. Default ON per policy.

### `EntityDistributionEveryTicks` (default `1` = vanilla, clamp [1,4])
- **Mechanism:** prefix on `NetEntityDistribution.OnUpdateEntities` runs the whole
  replication pass every Nth tick. The pass is a state-driven scan (interest
  recomputed from live positions; change flags persist until sent), so a skipped
  tick DELAYS replication by 50 ms - it cannot lose state.
- **Measured curve** (matched ~562-zombie arms): stride 1/2/3/4 = 7.69/4.25/2.97/
  2.34 ms avg = -0/-45/-61/-70%. Diminishing past 2 (RESULTS §3g).
- **Gameplay impact at 2 (10 Hz):** entity positions arrive up to 50 ms staler;
  clients interpolate, and 10 Hz is a console-game norm - but fast movers (feral
  sprinters) may rubber-band. **Needs a human-eye pass before enabling as the
  static base.** The governor uses stride 2 dynamically under overload instead.

---

## WorldTransfer

### `ChunkPackagesPerObserverPerTick` (default `3` = vanilla, clamp [1,32], EXPERIMENTAL)
- **Mechanism:** transpiles the unique batch constant in
  `ChunkManager.SendChunksToClients` so config controls how many chunks each
  observer encodes+sends per tick (each is a synchronous `Chunk.write` on the sim
  thread). Floor of 1 is a deadlock guard (0 would stall the send loop forever).
- **Measured:** could NOT be validated - the send path is ~1-2% of tick in every
  survivable test; the real join stall is region load/gen, which this does not
  touch (RESULTS §3e).
- **Gameplay impact below 3:** slower world transfer to joining players.
  Kept as an experimental knob only.

---

## AnimatorLod - reduced-rate animation for calm, distant zombies (v1.15.0)

### `AnimatorLod.Enabled` (default `false`)
- **Mechanism:** every zombie runs a full Unity Animator on the headless server
  (`AlwaysAnimate`; measured 19.9 ms/frame = 28% of the loaded frame at ~380
  zombies with 24 players). This LOD disables the Animator component for calm,
  distant zombies (stopping the engine's per-frame evaluation) and manually pumps
  `Animator.Update(FarStride x dt)` on the entity's slot frame - root motion
  arrives in aggregate, state reads lag by at most the stride. Always full rate:
  within `FullRateDistSq` of a player, attacking, stunned, or dead.
- **Measured:** NO win at the clustered blood-moon standard - the correctness
  exemptions (near players / mid-attack) cover almost the entire horde during a
  siege (RESULTS 3m-bis). The prize is real only for DISPERSED populations
  (wandering hordes, far roamers) where coverage approaches 100%.
- **Gameplay impact:** server-side animation is invisible to clients (anim params
  are never netsynced; clients animate zombies locally); the exemptions protect
  combat timing (root-motion movement, attack cadence, stuns). Far zombies react
  with up to `FarStride` frames of animation lag.
- **When to enable:** servers whose load is dominated by spread-out roamers, not
  clustered sieges.

### `FullRateDistSq` (400 = 20 m) / `FarStride` (4 = 5 Hz at 20 fps)
Band and stride; clamps [100, 1e6] and [1, 10].

## Server - loop settings (v1.14.0)

### `Server.TargetFps` (default `0` = leave vanilla, clamp [0,120])
- **Mechanism:** sets `Application.targetFrameRate` at game start (persistent form
  of the non-persistent `settargetfps` console command). The frame loop runs
  `UpdateTick` every frame for housekeeping, work slices, and the network pump; the
  FULL tick (entity sim + replication) is gated internally at ~20 Hz **regardless of
  frame rate** - measured: `TickEntities`/`OnUpdateEntities` stay at 19.9 calls/s at
  fps 20 and fps 60 alike (RESULTS 3k).
- **Effect: none measured.** The delivery-jitter hypothesis was refuted
  (inter-send gap distributions identical at fps 20 vs 60 under matched load - the
  send path paces on the 20 Hz tick, not frames). Does NOT raise TPS or capacity.
  A human "somewhat smoother" impression at 40 predates the measurement and is
  unconfirmed - likely placebo.
- **Cost:** modest per-frame loop overhead + idle wakeups. **Recommendation: 0.**
- **Interactions:** the governor's EMA measures FRAME intervals, so its idle floor
  equals the frame target - calibrate `HealthyMs`/`OverBudgetMs` to your fps (see
  below). Tick-counted knobs (`GraphUpdateEveryTicks`, replication stride) count
  FULL ticks and are fps-independent.

### `Server.JobWorkerCount` (default `0` = vanilla, clamp [0,64], v1.16.1)
Unity job-worker pool size (vanilla default 31 on a 32-thread host). Runtime-settable
via `es reload`. **Measured: no resolvable effect on the saturated frame** (sweep
4-24 workers = noise) - the main thread's fence waits are serial, not pool-bound.
Ships 0; experimenters only (RESULTS 3p).

## Governor - adaptive load management (v1.12.0)

### `Governor.Enabled` (default `true`, v1.13.0)
- **Mechanism:** a `GameManager.UpdateTick` postfix tracks the tick-interval EMA
  (~32-tick horizon). Sustained EMA > `OverBudgetMs` for `WindowTicks` switches to
  the THROTTLED state: replication stride 2 + pathfinding cadence x2. Sustained
  EMA < `HealthyMs` switches back to vanilla. Transitions respect `CooldownTicks`
  and are logged with the EMA that caused them.
- **Measured:** engages autonomously past the capacity ceiling; cushioned a ~435
  zombie overload at 128 ms/frame vs 299 unthrottled; restored vanilla within
  seconds of the load clearing (RESULTS §3i).
- **Gameplay impact:** ZERO while healthy (it does nothing). Under overload it
  applies the stride-2 staleness described above - in a regime where the
  alternative is a 3 TPS server for everyone. Default ON per policy.

### `OverBudgetMs` (57) / `HealthyMs` (52)
The hysteresis band, in FRAME-INTERVAL milliseconds (the EMA is measured on
UpdateTick, which runs per frame). The loop idles at exactly the frame target
(50 ms at fps 20, 25 at 40, 16.7 at 60) and never below, so `HealthyMs` must sit
ABOVE your idle frame time or recovery never triggers (proved live at fps 20 with a
45 ms threshold). Defaults assume the vanilla fps 20; a fps-40 tune would be e.g.
OverBudget 30 / Healthy 27.

### `WindowTicks` (100) / `CooldownTicks` (400)
~5 s of sustained signal to transition; ~20 s minimum between transitions.

### `AnimatorEmergency` (false) / `EmergencyOverMs` (80, floor OverBudget+5)
Tier 2 (opt-in, gameplay-affecting): when tier-1 throttling has not recovered the
tick and the EMA is past `EmergencyOverMs`, disable ALL zombie animators - measured
**~40% of the saturated 64-player frame** (fence check, RESULTS 3o). Combat timing
degrades (timer-only attack cadence, no stagger, supplementary movement path) but
nothing despawns and clients see no visual difference (zombie animation is
client-local). Steps back down one tier at a time.

**KNOWN DEFECT (human eval, RESULTS 3s): the exit path cannot fully restore.**
After any `Animator.enabled` off->on cycle the rig evaluates but emits zero root
motion (`deltaPosition=0`), and server zombies are root-motion-driven - restored
zombies crawl at supplementary-path speed until they die. Keep this `false` until
the culling-mode rework lands (TODO). Feel WHILE active passed human eval.

---

## TickGuard - emergency load shedding (v1.13.0)

### `TickGuard.Enabled` (default `false`)
- **Mechanism:** same EMA tracking; when EMA stays above `ShedAboveMs` (a level
  the governor's throttles could not fix) for `WindowTicks`, it despawns the
  `ShedBatch` enemies FARTHEST from any player via the game's silent despawn
  (`RemoveEntity(Despawned)` - the vanilla distance-despawn path: no loot, no XP,
  no corpse), repeating each `CooldownTicks` until recovery, never below
  `MinEnemiesKept` living enemies.
- **Gameplay impact: REAL - it removes zombies.** The farthest-first order makes
  the cut least visible (players in combat keep their attackers), but a horde that
  should have 400 zombies will thin. That is the explicit trade: a thinner horde
  at 20 TPS instead of a full horde at ~3 TPS. Default OFF because the mod does
  not silently change gameplay.
- **When to enable:** servers that routinely exceed the measured capacity ceiling
  (~147 endgame zombies at 64 players, RESULTS §3h) and prefer degradation over
  collapse.

### `ShedAboveMs` (70, floor 60) / `WindowTicks` (60) / `ShedBatch` (15, max 100) / `CooldownTicks` (100) / `MinEnemiesKept` (60)
Last-resort threshold well above the governor band; ~3 s of sustained overload per
shed; bounded batch and a keep-floor so a bad config cannot wipe the horde.

---

## Diagnostics - never enable on a live server

### `GcMegapauseTest` (false) + `WarmupSeconds` (60) + `GrowSeconds` (240)
Disables Boehm, grows the heap under load, then times one forced full collect to
measure the worst-case freeze (measured 479 ms at 6.9 GB). A destructive probe for
research only.

---

## Launch environment (scripts/run_server.sh, all EAC-safe)

| Env | Default | Effect |
|---|---|---|
| `GC_FREE_SPACE_DIVISOR` | `1` | Boehm heap headroom: heap ~= live x (1 + 1/divisor). At 1: full collections 3 -> 0 in the aggregate A/B window, worst STW 274 -> 0 ms (RESULTS §3). Costs RAM (~2x live). Use 2 if RSS matters. |
| `GC_NPROCS` | `nproc` | Parallel GC marking threads. Marginal but free. |
| `MONO_ENV_OPTIONS` | `-O=all` | Mono JIT full optimization: ~5% section-avg win, direction-consistent across all timed sections (single A/B pair). |
| `GC_INITIAL_HEAP_SIZE` | unset | Optional heap preallocation (e.g. `8G`) to avoid startup collection bursts. |
| (`settargetfps` console cmd) | 20 | Tick rate = frame rate (see `Server.TargetFps` above for the persistent mod knob). |
| `SEVENDTD_CPU_AFFINITY` | unset | **Leave off.** Naive pinning measured a LOSS (+122% jitter): it defeats Ryzen CPPC preferred-core boost (HOST_TUNING). |


---

## Console command (`es`, v1.13.1+)

`es status` prints every active lever value; `es reload` re-reads
`efficientserver.json` and applies it LIVE (all patches read the config object per
call - no restart needed). Diagnostics (BENCH ONLY, gameplay breaks while active):
`es animoff` / `es animon [bare]` toggle all enemy Animators (used to measure the
19.9 ms animator slice; skips corpses; `bare` = enable+pump without Rebind/param
re-push; NOTE restore is imperfect - RESULTS 3s root-motion wedge, restart to
fully recover); `es animstate` prints a per-zombie animator truth table
(enabled/speed/rootMotion/culling/params/velocity/deltaPosition/state) for
debugging revival and movement issues; `es rigoff` / `es rigon` toggle the unguarded rig visual
components (eyelid/gaze/feather/held-light-raycast; measured: no resolvable cost
at saturation variance); `es benchgod on|off` makes players damage-immune so
synthetic bench bots survive endgame hordes and the load stays an active siege
(RESULTS 3q spawn-equilibrium problem) - never on a real server.
