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

## Changelog

- **2026-08-02:** First V3.1.0 matched ES on/off moderate forensic pair.
