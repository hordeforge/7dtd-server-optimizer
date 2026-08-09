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
- [x] Add configuration parsing and normalization tests. (Self-contained `EfficientServer.Tests` harness: defaults, clamps, NaN/Inf fallback, hysteresis, round-trips, 500-case fuzz; `dotnet run --project Source/EfficientServer.Tests`.)
- [x] Add dedicated-only and per-feature enable/disable tests. (Dedicated-only gate: pure `ServerPerfConfig.ShouldRunFor` + `ModApi.ShouldRun` rewired, fail-closed on unknown host. Per-feature: pure `ServerPerfConfig.FeatureActive(featureKey, benchGod)` extracted from `ModApi.ConfigNote`; harness covers defaults, on/off knobs per feature, Gc Enabled+SkipForcedCollect conjunction, and unknown keys. All in `EfficientServer.Tests`.)
- [ ] Validate AI LOD behavior for alert, combat, sleeper, quest, and distant entities. (2026-08-09 static audit vs stock RE: `UpdateTasksLodPatch` never strides/skips attacking (GetAttackTarget), investigating (HasInvestigatePosition), alerted (GetAlertTicks>0), or active-sleeper entities - the combat/alert/sleeper set is structurally covered; `AiLodPatch`'s `aiActiveScale` only gates EAI cadence (locomotion always runs per RE) and the missing stock top-N quota is documented as accepted in FEATURES.md with a bounded residual. Remaining: live blood-moon charge validation.)
- [ ] Validate dynamic-mesh budgets during saves, region streaming, and separated players. (2026-08-09 static audit vs V3.1.0 IL + dynamic-mesh.md: all four patch fields verified as real stock statics (no API drift); MaxActiveSyncs 2-vs-10 and OnlyPlayerAreas semantics documented in FEATURES.md with the region-streaming / separated-players risks scoped. No A/B evidence exists - the live multi-player streaming residual remains.)

## Phase 2: reproducible evidence

- [x] Define small, medium, and high scenarios executed by `7dtd-loadgen`. (Seed-locked tier ladder + heavy canonical standard: `7dtd-apm/plans/profile.{canonical,tiers}.json`; docs LOAD_PROFILE.md, 2026-07-18.)
- [x] Capture baseline/candidate evidence with `7dtd-apm` and record session IDs. (V3.1.0: moderate 16p 135519/135942; heavy 48p 001826/003006; canonical 64p 004634/005248 mixed; see docs/V310_APM_BASELINE.md)
- [ ] Add regression budgets including simulation/gameplay correctness checks. (2026-08-09: config-level correctness invariants added to the self-contained harness - the AiLod band ordering (FullAiDistSq <= MediumAiDistSq <= SkipTasksFarDistSq) and scale monotonicity (Full >= Medium >= Far) are now regression-checked, including fully-inverted inputs and valid round-trips. Live simulation/gameplay budgets (loadgen + APM thresholds) remain.)
- [x] Document every configuration field, unit, range, runtime behavior, and tradeoff. (CONFIG.md covers all 51 config fields - added the previously missing CrowdCollisionLod section incl. `ResolveEveryNTicks` clamp [1,16]; automated cross-check source-vs-doc now passes.)

## Phase 3: packaging and release

- [x] Verify clean build, install, upgrade, rollback, and uninstall. (2026-08-09: clean build via scripts/build.sh, install to the live dedicated server (DLL hash-match verified, ModInfo 1.17.0, Config shipped), uninstall, reinstall, and a live in-game upgrade smoke test all pass: booted the V3.1.0 b14 dedicated server with EfficientServer installed - all 18 patch groups IL-matched real game methods, 0 MISSING TARGET, default-off features correctly config-disabled; loadgen self-test-join PASSED (joined entity=102, logins=1, challengesOk=1).)
- [x] Preserve unrelated mods and user configuration during package operations. (install.sh only touches `Mods/EfficientServer`; now backs up an existing `efficientserver.json` and keeps it on reinstall when it differs from the shipped default. Verified: fresh install uses default, user edit survives reinstall, restored-default uses shipped.)
- [x] Reconcile README, architecture, features, and runtime behavior. (2026-08-09: FEATURES.md Path-admission + Animator-emergency sections were appended after the Changelog and claimed v1.18 (a release that does not exist). Moved before the changelog and corrected to v1.17.0; same fix in PERF_RESEARCH_BRIEF ("Built v1.18") and CONFIG ("v1.18+ emergency"). No remaining future-version claims.)
- [x] Publish supported 7DTD/toolchain versions and troubleshooting. (README: game pin V3.1.0 b14 + rebuild-on-update retarget note, toolchain (net48, dotnet SDK w/ mcs fallback, 0_TFP_Harmony, game-managed refs), troubleshooting from mod log lines (MISSING TARGET version-drift, InitMod/patch failures, config restart, EAC).)

## Research evidence consumed (2026-08-06)

Stock RE closed two brief gaps without new EfficientServer code:

| Gap | Result | Research |
|---|---|---|
| ItemStack.Clone triage | 162 sites; ~56 XUi (ignore for dedi); mass TE+inventory+net Setup | `7dtd-research/docs/items.md` |
| Chunk encode ownership | SendChunks sole caller UpdateTick; Setup from SendChunks + RebuildTerrain | `7dtd-research/docs/world-chunks.md` |

**Measured 2026-08-06 (moderate 16p forensic A/B, shipping defaults):**

| Arm | Session |
|---|---|
| ES ON | `session_20260806_155401_pid3593984` |
| ES OFF | `session_20260806_160357_pid3639944` |

Headline ON vs OFF: UpdateTick avg **-17.5%**, late_ticks **-68%**, tick_stall
**-56%**, STW worst **-91.5%** (364→31 ms), UpdateGraphs avg 7.45→1.46 ms.
Both arms still fail absolute forensic budgets. Details: `docs/V310_APM_BASELINE.md`
§ Remeasure 2026-08-06.

**Path admission BM-ish A/B (2026-08-07):** sessions `161109` (path OFF) /
`161552` (cap=32, dropFarSq=2500). Loadgen 24/24 both. Lag **worse** ON
(late_ticks +16%, UpdateTick avg +34%). **Keep default-off.** Details:
`docs/V310_APM_BASELINE.md` § Path admission BM-ish A/B.

**Still measure/product:** Animator CullCompletely **human combat soak** before default-on;
optional Clone micro-patches only with soak; true BM capacity sweep if needed.

See `docs/PERF_RESEARCH_BRIEF.md` §4.4-4.5, §5 ranks 1/2/4/6.

## Open: animator revival wedge (tier-2 exit) - CODE BUILT, MEASURE OPEN

**2026-07-28:** `AnimatorEmergency` rewritten to `cullingMode = CullCompletely` (no `enabled` toggle). `es animoff`/`animon` use the same path.

**2026-08-03 stress:** 24 bots + ~273 endgame, frame 85->76 ms on CullCompletely; 209/271 movers with dp>0 after restore. Stress frame win PASS. Still human combat soak before defaulting Governor.AnimatorEmergency.

**2026-08-02 live gate:** `validate_anim_path_admission.py` PASS overall. CullCompletely 97/97 enter+exit; root-motion **34/96** moving with dp>0 after restore. Frame win SKIP (light load). Path admission fidelity PASS. **Stress done 2026-08-03** (over-budget PASS). Remaining before default-on: **human combat soak**. Path knobs stay default-off.

### Prior notes

Human eval 2026-07-23 (live client, benchgod bots): tier-2 combat feel WHILE
active is fine, but every restore path after `Animator.enabled=false` leaves
zombies producing `deltaPosition=0` (verified via `es animstate`: state machine
advances, `applyRootMotion=true`, `AvatarRootMotion` forwarder enabled, delta
still zero) - they crawl at supplementary-path speed until death. Tried and
refuted: bare enable+pump, Rebind+pump, Rebind+`SetAlive`/`SetWalkType`
re-push (params survive fine; delta stays dead). Corpse-restore statue bug
found and fixed en route (`IsDead()` skip). NEXT LEVER (designed, unbuilt):
stop toggling `enabled`; switch `cullingMode` to `CullCompletely` on
enter and restore the saved mode on exit - headless server culls everything, so
evaluation should stop for the same win while keeping the root-motion binding
alive. Needs: implementation in AnimatorEmergency + animoff/animon, perf
re-validation (does CullCompletely reproduce the 147->85 ms win?), and a fresh
human cycle with `es animstate` dp readings. Until this lands,
`Governor.AnimatorEmergency` stays default-false; `es animoff` remains
bench-only with a known-degraded exit.

Follow-up validation (2026-07-23, headless): `es animstate` confirms zombies
spawned AFTER `es animoff` come up `en=True` - the probe only disables
existing rigs, so the human report "post-off spawns moved fine" is explained
(they ran full animators). Consequence for a permanent-off mode: it must also
catch spawns (sweep or spawn-hook, as AnimatorEmergency.Enter already does)
AND ship the supplementary-path speed compensation, else every zombie crawls.
Bot-based displacement A/B (on vs off vs restored speed, no human) is written
(`anim_validate.py`, session scratchpad) but needs the standard loadgen
bring-up: reusing the optimizer save as bot bait left zombie AI dormant (0.0 m
displacement even with animators on), so the measurement was void.

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

## Residual (post V3.1.0 campaign, 2026-08-03)

Honest open items only. Do not re-open refuted levers without new APM evidence.

| Residual | Status | Notes |
|---|---|---|
| `Governor.AnimatorEmergency` default-on | **blocked on human soak** | Stress gate PASS (85->76 ms, root-motion restore). Light + stress automated gates green. |
| Path admission defaults | **keep 0/0 vanilla** | Fidelity PASS; no reliable frame win under stress noise. |
| Canonical 64p ms_per_tick win | **not claimed** | ES ON worse ms/tick at 64p chaos; STW/late-share still better. Residual walls: entity tick, explosions, IO. |
| Safe Harmony space | **exhausted** | Entity wall ≈ world-collision + close AI; player wall O(N²); serialize-once already stock. |
| Config unit / dedicated-only tests | open | Phase 1 remaining checkboxes above. |
| Packaging upgrade/rollback verify | open | Phase 3. |
| Full blood-moon capacity re-sweep on 3.1 | optional | Prior BM capacity was V3.0.1 campaign; 3.1 has moderate/heavy/canon only. |

Evidence hub: [docs/V310_APM_BASELINE.md](docs/V310_APM_BASELINE.md) · [docs/RESULTS.md](docs/RESULTS.md) · [docs/PERF_RESEARCH_BRIEF.md](docs/PERF_RESEARCH_BRIEF.md).
