# Production deployment runbook

**Hub:** [`README.md`](../README.md).  
**Owns:** deploying EfficientServer + the APM bridge to a real dedicated server and
operating them continuously. **Not:** per-option detail ([CONFIG](CONFIG.md)),
evidence ([RESULTS](RESULTS.md)).

## 0. Prerequisites and the one hard trade

- 7 Days to Die dedicated V3.1.0 (b14). After ANY game update, rebuild and check the
  init log for `MISSING TARGET` lines before going live (section 5).
- **EAC must be off** (any C# mod requires it). Clients must also launch EAC-off.
- Host with RAM headroom: the GC configuration trades RAM for fewer collections
  (plan ~2x the live heap; 16 GB+ comfortable for 64 players).

## 1. Install

```bash
cd 7dtd-optimizer
make build
make install DS="/path/to/7 Days to Die Dedicated Server"     # EfficientServer
cd ../7dtd-apm
make bridge-build && make bridge-install                       # APM bridge (24/7-safe)
```

Launch through `7dtd-optimizer/scripts/run_server.sh` (or replicate its env in your
service unit):

```bash
GC_FREE_SPACE_DIVISOR=1     # collections 3->0 in the A/B window; use 2 if RSS matters
GC_NPROCS=$(nproc)          # parallel GC marking
MONO_ENV_OPTIONS=-O=all     # ~5% section-avg win, EAC-safe
# do NOT set CPU affinity (measured loss - fights CPPC preferred cores)
```

## 2. Recommended config by population

`Mods/EfficientServer/Config/efficientserver.json`. The shipping defaults are the
recommended base for every size: all zero-gameplay-impact levers on, governor on
(inert while healthy), everything perceivable off.

| pop | changes from defaults | why |
|---|---|---|
| <= 16 casual | optional `Server.TargetFps: 40-60` | delivery-jitter polish; costs CPU + idle wakeups |
| 16-64 | defaults as-is | governor absorbs horde spikes (validated: +58% sustained blood-moon capacity) |
| 64+ heavy hordes | consider `Governor.AnimatorEmergency: true`, then `TickGuard.Enabled: true` | trades a thinner horde for never collapsing to ~3 TPS; despawns are silent + farthest-first, but it IS a gameplay change - announce it to players |
| chasing max capacity | `MaxSpawnedZombies` near the measured ceiling (~230 endgame at 64p on a 9950X-class host) | past it the governor throttles, then TickGuard (if enabled) sheds |

Apply config edits live: `es reload` (telnet/console). `es status` shows active values.

## 3. What to watch (continuous)

- **Log lines that matter** (server log):
  - `MISSING TARGET: ...` at boot = a lever is INACTIVE after a game update. Act.
  - `Governor: ... THROTTLED / restored vanilla` = load crossed the band. Frequent
    flapping -> raise `CooldownTicks` or lower the load.
  - `Governor: ... ANIMATOR EMERGENCY / stepped down` = tier 2 (if enabled): all
    zombie animators off during extreme overload (~40% frame recovery; combat
    timing degrades, nothing despawns).
  - `TickGuard: ... shed N farthest enemies` = past throttling; expect thinner hordes.
  - `SPIKE gmUpdateDuration=...` = frame spikes (rate-limited to 1/5 s).
- **Telemetry** (no capture needed, 24/7-safe):
  `Mods/7dtd-apm-bridge/telemetry/apm_app_latest.json`, refreshed every 30 s -
  `world.unityDeltaMs` (frame period; idle = frame target), `update.lateTicks`,
  `update.tickStallMsTotal`, `gc.gen2Collections`, `sections[]`.
  Or run `7dtd-apm monitor` (headline `tps` is instantaneous; `tps_lifetime` is
  since-reset).
- **Disk:** telemetry dir self-prunes (32 dumps, current-pid maps); capture sessions
  self-prune (`APM_KEEP_SESSIONS`, default 40).

## 4. Capturing on a live server

Default `7dtd-apm capture` is production-safe: no jitmap burst (pass `--symbolize`
ONLY on bench servers - it freezes a loaded main thread for tens of seconds), perf
at 99 Hz (~1-2% CPU), dangerous probes opt-in. Raw sessions contain the server log
stream (player names/IPs) - share only `7dtd-apm export` bundles, which are
scrubbed (cmdline/exe redacted, home path replaced).

## 5. After a game update

1. `make build` - fix compile errors first (API drift).
2. Boot once on a copy/staging save; grep the log for `MISSING TARGET` and
   `patch ... failed`. Every lever fails VISIBLY (a moved IL target logs MISSING and
   deactivates that lever only - the rest keep working).
3. The two external-DLL transpilers (`InitScanPoolPatch` on AstarPathfindingProject,
   `ChunkSendThrottlePatch` batch constant) are the most drift-prone; both throw ->
   MISSING rather than corrupt.

## 6. Emergencies

- Server melting, need vanilla NOW: set `"Enabled": false` + `es reload` (all levers
  inert, no restart), or remove `Mods/EfficientServer`.
- Governor stuck throttled: check `es status`, then the log for the engaging EMA;
  if the load is real, that is the system working. `Governor.Enabled=false` +
  `es reload` to force vanilla behavior.
- Horde thinner than expected: TickGuard is shedding (log says so, with counts).
  Disable it or raise `ShedAboveMs`.
