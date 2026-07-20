# 7dtd-optimizer implementation plan

The optimizer owns only reviewed, configurable runtime optimizations. Generated
load belongs to `7dtd-loadgen`; profiling and telemetry belong to the standalone
bridge in `7dtd-apm`.

## Phase 0: remove obsolete ownership

- [x] Remove the old in-mod load generator and load tick Harmony patch.
- [x] Remove the duplicate profiler command, hooks/core, reports, and APM export.
- [x] Remove profiler/load configuration and documentation.
- [x] Remove optimizer dependencies and startup behavior used only by those subsystems.
- [x] Verify the optimizer builds with optimization patches only.

## Phase 1: harden retained optimizations

- [x] Report exact matched Harmony targets and fail visibly for required missing targets. (ModApi: per-group matched-method log + "MISSING TARGET ... INACTIVE" on zero matches, 2026-07-18.)
- [x] Record optimizer, game assembly, and configuration versions at startup. (ModApi.LogVersions: mod + Assembly-CSharp + game version + config summary, 2026-07-18.)
- [ ] Add configuration parsing and normalization tests.
- [ ] Add dedicated-only and per-feature enable/disable tests.
- [ ] Validate AI LOD behavior for alert, combat, sleeper, quest, and distant entities.
- [ ] Validate dynamic-mesh budgets during saves, region streaming, and separated players.

## Phase 2: reproducible evidence

- [x] Define small, medium, and high scenarios executed by `7dtd-loadgen`. (Seed-locked tier ladder + heavy canonical standard: `7dtd-apm/plans/profile.{canonical,tiers}.json`; docs LOAD_PROFILE.md, 2026-07-18.)
- [ ] Capture baseline/candidate evidence with `7dtd-apm` and record session IDs.
- [ ] Add regression budgets including simulation/gameplay correctness checks.
- [ ] Document every configuration field, unit, range, runtime behavior, and tradeoff.

## Phase 3: packaging and release

- [ ] Verify clean build, install, upgrade, rollback, and uninstall.
- [ ] Preserve unrelated mods and user configuration during package operations.
- [ ] Reconcile README, architecture, features, and runtime behavior.
- [ ] Publish supported 7DTD/toolchain versions and troubleshooting.

## Verification log

- 2026-07-16: duplicate load/profiling ownership confirmed; removal started.
- 2026-07-16: optimizer-only build passed with 0 warnings and 0 errors.
- 2026-07-18: B12/P1 built (v1.3.0, `AstarGraphThrottlePatch`, config `Pathfinding.GraphUpdateEveryTicks`=4). Sibling APM alloc-attribution bug fixed - `AstarVoxelGrid.InitScan` confirmed #1 allocator. Config renamed from misleading `Pathfinding.Enabled` to single honest knob `GraphUpdateEveryTicks` (1=vanilla).
- 2026-07-18: P1 A/B VALIDATED (32 bots + ~270 zombies, 150 s, `--only all,alloc`): ms_per_tick 54.95 → 39.28 (-28.5%, over-budget → 20 TPS), UpdateGraphs total -35% / p95 -55%, fidelity intact (zombies 277→277). Sessions `session_20260718_074155` (off) / `_074952` (on). RE correction: `UpdateMoveGraph` is called inside `UpdateGraphs` (sole caller, one-move drain), NOT a separate per-tick method; safety rationale corrected in code + docs.
- 2026-07-18: Full-mod A/B (mod off vs all-on, 32+280): ms_per_tick -8.0% at an already-healthy 38 ms baseline (UpdateGraphs -14.9%, NetEntityDistribution -16.8%, TickEntities -6.4%). Mod benefit scales with server stress (P1 alone gave -28% at a breaking 55 ms baseline).
- 2026-07-18: v1.4.0 (mod-audit workflow, 19 verified findings). Correctness: GcIncremental now respects master `ShouldRun()` (#1); `Pathfinding.GraphUpdateEveryTicks` bounded `[1,200]` in `Normalize()` (#2); `ShouldRun()` fails closed on throw (#3); `_tick` uint-cast to avoid signed-wrap mistiming (#8); GcGuard + AstarMoveThreshold transpilers throw on zero-match (fail-visibly, #9); `UpdateGraphs` overload pinned `new[]{typeof(float)}` (#10). Fidelity: `UpdateTasksLodPatch` now calls `CheckDespawn()` before skipping so far entities still despawn (was inert `aiActiveDelay` set - despawn is the first in-method step); `AiLodPatch` cloth toggled level-triggered (self-heals on approach). Lever: **P2 built** (`AstarMoveThresholdPatch`, transpiler on `UpdateGraphPos` raising the `ldc.r4 100` rescan dead-zone to `Pathfinding.MoveRescanThresholdSq`). Smoke test: matched methods=5, no MISSING/exceptions. P2 A/B (100 vs 400) INCONCLUSIVE: confounded by unequal zombie load (294 vs 268 sustained) and a load profile where UpdateGraphs is only ~4% of the tick, so ms_per_tick can't isolate P2. Positive signal: `InitScan` dropped off the top-3 allocators at 400 despite heavier load. Clean re-test (60 bots, 0 zombies, matched 116/111 load): **UpdateGraphs -20.2%** (avg 5.78→5.15, p95 34→32 - P2 hits its target), but **ms_per_tick flat** (+1.9% noise) because at a healthy 20 ms/tick UpdateGraphs is only ~14% of the tick and the saving is swamped by variance. Verdict: P2 real (-20% graph work, fewer InitScan allocs) but tick-time-neutral except under graph-dominated stress / GC-pause sensitivity. Ships at default 100 (vanilla); 400 = mild correct optimization for graph-heavy servers, not a universal default. Follow-ups deferred: #4 DedicatedSkip drift log, #5 Gc fields into Normalize, #6 C# config test project, #7 config-disabled init label, cross-cutting behavioral counters.
- 2026-07-19: v1.4.1 follow-ups DONE + smoke-validated. #4 DedicatedSkipPatch logs "method/type not found (skip disabled)" on drift. #5 Gc numeric fields clamped+logged in Normalize (0-sentinels preserved: `-50 -> 0`, `9.0 -> 0.95` observed). #6 C# Config test harness `Source/EfficientServer.Tests` (Load/Normalize/fuzz, `make test`, passes). #7 init summary appends "(matched but config-disabled)" for inert patches. Remaining: cross-cutting behavioral counters (far-skip/EAI-rate/safety-collect), still deferred (needs an in-body counter + opt-in periodic log).

## Done criteria

Every retained optimization is version-checked, independently configurable,
tested for correctness, measured with sibling loadgen/APM projects, documented,
and safe to disable or roll back.
