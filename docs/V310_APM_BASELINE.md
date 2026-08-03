# V3.1.0 APM / loadgen evidence (EfficientServer)

**Date:** 2026-08-02  
**Game:** V 3.1.0 (b14) dedicated Navezgane  
**Mods:** `0_TFP_Harmony`, `7dtd-apm-bridge` 2.0.0, `EfficientServer` 1.17.0  
**Workload:** moderate matched pair (not full canonical-heavy-v2)

| Knob | Value |
|---|---|
| clients | 16 |
| seed | 20240802 |
| bot_mix | traverse:30,wander:25,combat:30,bait:15 |
| spawn | zombieBoe,zombieArlene,zombieMoe,zombieBikerFeral @ 4/player every 10s |
| horde | every 45s, 2 waves |
| warmup | 45 s |
| capture | 90 s forensic (`all,alloc`) |
| MaxPlayers | 32 |

## Sessions

| Arm | Session | ES |
|---|---|---|
| ON | `session_20260802_135519_pid84194` | Enabled=true (shipping defaults) |
| OFF | `session_20260802_135942_pid99495` | Enabled=false |

Loadgen: **16/16 pass** both arms (walks ~29800, attacks ~107).

## Headline comparison

| Metric | ES ON | ES OFF | Delta (on-off) |
|---|---:|---:|---:|
| ms_per_tick | 10.91 | 11.71 | **-6.8%** |
| window_updates (90s) | 2375 | 1758 | more ticks completed when ON |
| late_ticks | 52 | 52 | 0 |
| late_tick_share | 2.2% | 3.0% | better share ON (more total ticks) |
| tick_stall_ms | 1817 | 2649 | **-31.4%** |
| alloc MB/s (bridge GC layer) | 2.91 | 18.62 | **-84.4%** |
| STW worst ms | 27.2 | 316.7 | **-91.4%** |
| STW total ms | 112 | 464 | **-76%** |
| full GC collections | 5 | 8 | -3 |
| World.TickEntities p95 | 5.15 | 4.98 | +3.5% (noise) |
| AstarManager.UpdateGraphs total ms | 850 | 955 | **-11%** |
| health grade | C 63.7 | C 62.9 | ~flat |

Budget gates: both arms **FAIL** stock forensic budgets (GC/memory/alloc ceiling). That is expected under forensic + spawn pressure; the A/B is relative, not absolute pass.

## Subsystem share (instrumented managed)

Both arms ~**81% entity_tick**, ~**10% network**, ~**6% io_saves**. Composition is stable; ES does not invent a new wall.

## Interpretation

1. **V3.1.0 join works** after world ready (early join = kick "still initializing", not VersionMismatch).
2. **EfficientServer still pays on 3.1.0** under moderate load: big wins on **alloc + STW megapause risk** and **tick stall**, modest **ms_per_tick**.
3. **P1-class graph throttle** still visible (UpdateGraphs total -11%; avg 1.5 vs 6.3 ms/call).
4. This is **not** a breaking-load / blood-moon capacity re-sweep. Canonical-heavy-v2 (64p) remains the next stress rung if needed.

## How to reproduce

```bash
# dedi (after game fully started)
RE_WORLD_NAME=Navezgane RE_SERVER_MAX_PLAYERS=32 RE_MAX_ZOMBIES=128 \
  bash 7dtd-loadgen/scripts/start_dedicated_prefab.sh

export SEVENDTD_TELNET_PASSWORD=retest DOTNET_ROOT=$HOME/.cache/dotnet-sdk
cd 7dtd-apm
uv run 7dtd-apm scenario run --clients 16 --actions 4000 --seconds 90 --warmup 45 \
  --preset forensic --seed 20240802 \
  --bot-mix 'traverse:30,wander:25,combat:30,bait:15' \
  --spawn-entity 'zombieBoe,zombieArlene,zombieMoe,zombieBikerFeral' \
  --spawn-per-player 4 --spawn-every-ms 10000 \
  --horde-every-ms 45000 --horde-waves 2 --reset-bridge \
  --label 'v310-es-on-moderate-16p'
```

Toggle ES: set `Mods/EfficientServer/Config/efficientserver.json` `Enabled` true/false and restart dedi (same world seed policy as usual).



## Heavy pair (48 clients, forensic 120s, seed 20240803)

Not full canonical 64p (disk/time constrained); still over-budget stress.

| Knob | Value |
|---|---|
| clients | 48 |
| MaxPlayers | 64 |
| warmup / capture | 60 s / 120 s forensic |
| bot_mix | traverse:30,wander:20,combat:25,bait:10,demolition:10,chatty:5 |
| spawn | 5/player every 9s + horde 40s x3 |
| seed | 20240803 |

| Arm | Session | ES |
|---|---|---|
| ON | `session_20260803_001826_pid2015349` | Enabled=true |
| OFF | `session_20260803_003006_pid2055555` | Enabled=false |

Loadgen **48/48** both arms (walks ~128k, attacks ~270-280).

| Metric | ES ON | ES OFF | Delta |
|---|---:|---:|---:|
| ms_per_tick | **24.2** | **37.0** | **-34.7%** |
| window_updates | 3130 | 1778 | more ticks completed ON |
| late_ticks | 654 | 782 | -16% |
| late_tick_share | **20.9%** | **44.0%** | **-52% relative** |
| tick_stall_ms | 22419 | 28942 | **-22.5%** |
| STW worst ms | **42.8** | **349.9** | **-87.8%** |
| STW total ms | 256 | 617 | **-58%** |
| UpdateGraphs total ms | 740 | 978 | **-24%** |
| UpdateGraphs avg ms | 0.71 | 6.70 | throttle visible |
| health grade | C 57.4 | D 50.3 | better ON |
| gross alloc budget | 30.8 MB/s FAIL | 40.1 MB/s FAIL | both over 15 |

Subsystem share ON: entity_tick 61%, network 21%, falling 7%, explosions 5%.  
OFF: entity_tick 53%, network 30% (more time in net under deeper stall).

**Interpretation:** Under real over-budget load, ES on 3.1.0 recovers **~1/3 of ms_per_tick**, roughly **halves late-tick share**, and nearly **eliminates megapause-class STW** (350 ms -> 43 ms). Matches V3.0.1 campaign story (smoothness + stress recovery).

## Animator CullCompletely stress (2026-08-03)

Harness: `scripts/validate_anim_path_admission.py`  
Report: `server/logs/validate_anim_path_20260803_082721.json`

| Knob | Value |
|---|---|
| bots | 24 |
| endgame spawn target | 350 (reached ~273 alive) |
| ES | Enabled=true |

| Check | Result |
|---|---|
| Join | PASS 24/24 |
| CullCompletely enter | PASS (mixed cull modes in animstate; CullCompletely present) |
| Root-motion after restore | **PASS** 209/271 moving with **dp>0** |
| Frame win (over budget) | **PASS** baseline **85.4 ms** -> off **75.8 ms** (delta **-9.7 ms**, -11%) |
| Path admission fidelity | PASS (alive held ~271-272) |
| Overall | **PASS** |

Honest notes: path admission did not improve frame at this load (+5.6 ms noise). Animator emergency still default-off until human combat soak; stress frame win is real but smaller than the old 147->85 class under different load.

## Changelog

- **2026-08-03:** Heavy 48p ES on/off + animator stress gate on V3.1.0.
- **2026-08-02:** Moderate 16p pair (section above).



## Canonical-heavy-v2 (64 clients, forensic 150s, seed 20240717)

Full profile from `7dtd-apm/plans/profile.canonical.json`.

| Knob | Value |
|---|---|
| clients | **64** |
| MaxPlayers | 64 |
| warmup / capture | 90 s / 150 s forensic |
| bot_mix | traverse:30,wander:20,combat:25,bait:10,demolition:10,chatty:5 |
| spawn | 6/player every 8s + horde 40s x4 + max_dynamite 80 |
| seed | 20240717 |

| Arm | Session | ES |
|---|---|---|
| ON | `session_20260803_004634_pid2107665` | Enabled=true |
| OFF | `session_20260803_005248_pid2125881` | Enabled=false |

Loadgen **64/64** both arms (walks ~216k).

| Metric | ES ON | ES OFF | Notes |
|---|---:|---:|---|
| ms_per_tick | **57.2** | **42.8** | ON worse (+34%) |
| window_updates | 1136 | 2100 | ON completed fewer ticks in window |
| late_ticks | 731 | 1804 | ON fewer absolute late |
| late_tick_share | **64.4%** | **85.9%** | ON better share |
| tick_stall_ms | 125020 | 74056 | ON worse absolute stall sum |
| STW worst ms | **110** | **373** | ON **-70%** |
| STW total ms | 884 | 1156 | ON **-24%** |
| alloc MB/s (layer) | 0.75 | 3.32 | ON lower |
| TickEntities p95 | 10.7 | 7.5 | ON higher |
| UpdateGraphs total | 780 | 753 | ~flat |
| health grade | D 44.0 | D 45.7 | ~flat fail |
| gross alloc budget | 36.5 FAIL | 42.5 FAIL | both over |

Subsystem share ON: entity 40%, network 28%, **explosions 17%**, io 9%.  
OFF: entity 43%, network 34%, explosions 10%.

### Honest interpretation (do not overclaim)

At full 64p + demolition chaos, **ES is not a free win on ms_per_tick**.

1. **Clear ON wins:** STW worst/total, late-tick *share*, alloc rate.
2. **Clear ON losses / mixed:** ms_per_tick, tick_stall_ms sum, TickEntities p95, fewer window_updates.
3. Workloads are **not perfectly composition-matched** (explosion share 17% vs 10%). Chaos + dynamite dominate; ES levers (graph throttle, GC guard, net stride) do not cancel explosion/disk walls.
4. **Moderate 16p and heavy 48p** remain the clean wins. Canonical 64p shows the **stress ceiling and residual walls** (entity x player replication, explosions, IO), not a reason to ship more unmeasured Harmony.

## Changelog

- **2026-08-03 (later):** Full canonical-heavy-v2 64p ES on/off (mixed; STW win, ms_per_tick not).

## Changelog

- **2026-08-03 (later):** Full canonical-heavy-v2 64p ES on/off (mixed).
- **2026-08-03:** Heavy 48p ES on/off + animator stress gate.
- **2026-08-02:** First V3.1.0 matched ES on/off moderate forensic pair.
