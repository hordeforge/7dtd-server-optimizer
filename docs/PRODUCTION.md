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
cd 7dtd-server-optimizer
make build
make install DS="/path/to/7 Days to Die Dedicated Server"     # EfficientServer
cd ../7dtd-server-apm
make bridge-build && make bridge-install                       # APM bridge (24/7-safe)
```

`DS=` may be any dedicated install. When the named dir has no
`7DaysToDieServer_Data/Managed` (a staging target for another host, or a typo),
the build falls back to the Steam client install for the game DLLs and prints
three `WARNING:` lines naming both paths; check them before shipping, since a
typo means the DLL was built against a different game install than intended.

Launch through `7dtd-server-optimizer/scripts/run_server.sh` (or replicate its env in your
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
| <= 16 casual | none | `Server.TargetFps` measured no effect (RESULTS 3k): keep 0 |
| 16-64 | defaults as-is | governor absorbs horde spikes (validated: +58% sustained blood-moon capacity) |
| 64+ heavy hordes | consider `Governor.AnimatorEmergency: true`, then `TickGuard.Enabled: true` | trades a thinner horde for never collapsing to ~3 TPS; despawns are silent + farthest-first, but it IS a gameplay change - announce it to players |
| chasing max capacity | `MaxSpawnedZombies` near the measured ceiling (~230 endgame at 64p on a 9950X-class host) | past it the governor throttles, then TickGuard (if enabled) sheds |

Apply config edits live: `es reload` (telnet/console). `es status` shows active values.

## 3. What to watch (continuous)

- **Log lines that matter** (server log):
  - `MISSING TARGET: ...` at boot = a lever is INACTIVE after a game update. Act.
  - `Governor: ... THROTTLED / restored baseline` = load crossed the band. Frequent
    flapping -> raise `CooldownTicks` or lower the load.
  - `Governor: ... ANIMATOR EMERGENCY / stepped down` = tier 2 (if enabled): all
    zombie animators culled (`CullCompletely`) during extreme overload (~40% frame
    recovery; combat timing degrades, nothing despawns).
  - `TickGuard: ... shed N farthest enemies` = past throttling; expect thinner hordes.
  - `world loaded` = the world-load anchor; one per world load. Every line this
    mod writes ends in `uptimeS=<seconds>`, the same field `es status` prints,
    so any log line and a status capture join on one number, and after a 6-day
    uptime with three world loads this line is what says which world a later
    line belongs to.
  - `SPIKE gmUpdateDuration=...` = frame spikes, logged by the APM bridge
    (rate-limited to 1/5 s).
  - `game log sink unavailable [...]` (console only, never the log file) = the
    game's `Log` static rejected a write, so from that point every mod line goes
    to the server console and NOT to the log file. A log file whose last
    `[EfficientServer]` line is older than expected with nothing new in it is
    this, not a mod that stopped talking.

  Every mod line is ONE line: a reported exception has its breaks rendered as
  ` | ` (`LogLine.Format`), so a stack trace never arrives as several
  untimestamped records, and a line-oriented grep or log shipper sees one event
  per event.
- **Degradations (`es status`, `degraded=` line)**: every fail-open path in the mod
  (AI alert probe, CheckDespawn fallback, LOD cloth toggle, client-list snapshot,
  GC ceiling, target-fps apply, the dedicated-host read, an absent dedicated-skip
  target) announces itself ONCE in the log and then stays silent, because those
  paths fire per tick or per connection request. The `degraded=` line is the
  standing record: `none` is healthy, otherwise `key=count` pairs in occurrence
  order, where `count` is how many times that path hit its fail-open branch.
  Entries persist until restart (they are game-API drift, not config; `es reload`
  cannot repair them). A non-`none` value means some lever is running degraded
  right now.
- **Throttle engagement (`es status`, runtime line)**: `replicationSkipped`,
  `graphUpdatesSkipped` and `collisionOffTicks` are lifetime counts of work the
  silent cadence levers actually took off the tick. A configured cadence with a
  zero count means the patch is inert, not merely idle.
- **Telemetry** (no capture needed, 24/7-safe):
  `Mods/7dtd-server-apm-bridge/telemetry/apm_app_latest.json`, refreshed every 30 s -
  `world.unityDeltaMs` (frame period; idle = frame target), `update.lateTicks`,
  `update.tickStallMsTotal`, `gc.gen2Collections`, `sections[]`.
  Or run `7dtd-server-apm monitor` (headline `tps` is instantaneous; `tps_lifetime` is
  since-reset).
- **Disk:** telemetry dir self-prunes (32 dumps, current-pid maps); capture sessions
  self-prune (`APM_KEEP_SESSIONS`, default 40).

## 4. Capturing on a live server

Default `7dtd-server-apm capture` is production-safe: no jitmap burst (pass `--symbolize`
ONLY on bench servers - it freezes a loaded main thread for tens of seconds), perf
at 99 Hz (~1-2% CPU), dangerous probes opt-in. Raw sessions contain the server log
stream (player names/IPs) - share only `7dtd-server-apm export` bundles, which are
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
  inert, no restart), or `make uninstall DS=...` (keeps the live config under
  `<DS>/EfficientServer-uninstall-backup`).
- Governor stuck throttled: check `es status` (the runtime line shows the current
  tier and tick EMA directly, no log dive needed; `tickEmaMs=n/a` means the
  governor is not sampling, so no tick interval is being measured); the
  transitions themselves are in the log. If the load is real, that is the system
  working. `Governor.Enabled=false` + `es reload` to force vanilla behavior.
- Horde thinner than expected: TickGuard is shedding (log says so per shed with
  counts; `es status` shows the lifetime total).
- Server collapsing but the horde is NOT thinning: grep WARNING for
  "TickGuard ... shed SUPPRESSED" - the trigger fired and was withheld, and the
  line names why (no world, no players online, or the live enemy count is at or
  below `TickGuard.MinEnemiesKept`). Raise the knob the line names.
- Memory growing with no collect in sight: grep WARNING for "gc guard ceiling
  unresolved" - the forced collect is suppressed and host RAM was unreadable, so
  no ceiling replaced it. Set `Gc.SafetyCollectAboveMB` explicitly. It also shows
  as `gcGuardCeiling` on the `es status` `degraded=` line, so a boot-time warning
  is still visible hours later.
- Every lever doing nothing at once: `es status` shows `modActive=false` and the
  `degraded=` line names `dedicatedGate` = the mod could not read whether the
  host is a dedicated server, so the gate that every patch prefix calls fails
  closed and NOTHING is patched. The read is retried per call, so the count on
  that key is how long the server has run unpatched; the cause is in the single
  WARNING line at first failure. Restart after fixing the underlying host
  problem: the degradation, like every other one, does not clear on reload.
- A lever configured but doing nothing: check the `es status` runtime counters for
  that lever (`pathDropped*` for path admission, `tasksSkippedFar` /
  `tasksStridedOff` for AI LOD, `replicationSkipped`, `graphUpdatesSkipped`,
  `collisionOffTicks`). All zero with a non-zero configured value means the patch
  is matched but inert. If a `degraded=` key explains it, that is the cause;
  otherwise re-check for a MISSING TARGET at boot.
- Mystery ~120 s hitches: grep WARNING for "gc guard safety collect fired" - the
  heap ceiling is below the working set and the safety net is collecting; raise
  `Gc.SafetyCollectAboveMB`.

## 7. State, recovery, RPO/RTO

### What state this mod owns

The mod is a patch DLL: it keeps no database, no index, no queue. Everything it
reads is regenerable from this repo, and everything it writes is the log. The
state that is NOT regenerable is what an operator edits on the server host:

| State | Where | Regenerable |
|---|---|---|
| Live config (tuning) | `<DS>/Mods/EfficientServer/Config/efficientserver.json`, or the file `$ES_CONFIG_PATH` names | No. The repo copy is the shipped default; the host copy holds the tuned values |
| Guard backup | `.../Config/efficientserver.json.swap-bak` | No. Only exists mid-bench-run; crash recovery for a killed swap |
| Installed DLL | `<DS>/Mods/EfficientServer/` | Yes: `make build && make install` |
| Server logs | `server/logs/server_<UTC>.log` (default) | No, but expendable: restart writes a new one |
| APM telemetry | `Mods/7dtd-server-apm-bridge/telemetry/` | Yes, within 30 s of the bridge running |
| Server world / player data | `<DS>/GeneratedWorlds`, `serverconfig*.xml` | Not this mod's. The game's own saves, backed up by the host operator |

### RPO and RTO

- **RPO for the live config: 0 across a reinstall or an uninstall.** `install.sh`
  holds the installed `Config/efficientserver.json` in a temp file across the
  `rm -rf` and restores it on success (kept, with its path printed, if the
  install fails); `uninstall.sh` copies the whole `Config/` directory to a
  timestamped backup and prints the restore command.
- **RPO against host loss, disk loss, or a deleted instance: whatever your
  snapshot cadence is, plus one interval, and only if you take one.** Those
  copies live inside the install tree, so the disaster that takes the server
  takes them too. `scripts/backup_config.py` moves the live config off the host
  and proves the copy loads; run it on a schedule (section 7.1). With no
  snapshot the RPO is unbounded: the tuning is worth a re-derivation from
  `docs/CONFIG.md` plus the measured defaults, or the copy, and one of the two
  is a decision only you can make.
- **RTO for a config restore: under a minute** (one `cp` plus `es reload`; no
  restart). **RTO for a full mod reinstall: one `make install`.** Neither path
  needs a rebuild once `dist/` is present.
- **World data RPO/RTO is the host operator's**, not this mod's. Nothing here
  touches it.

### 7.1 Snapshot the live config off-host

```bash
make backup-config ES_CONFIG_BACKUP_DEST=/mnt/backup/es-config
# or: python3 scripts/backup_config.py --dest /mnt/backup/es-config
```

Each run writes a UTC-stamped copy of `Config/efficientserver.json` plus a
manifest recording its sha256, keeps the newest `--keep` (default 14), and
re-reads the copy back before reporting success: a snapshot that would not load
is a failed run, not a backup. A retry inside the same second adds a
suffix-numbered snapshot rather than replacing the earlier one, and retention
counts by creation order, so a rerun never deletes the snapshot it just took.
A run that does not finish leaves the destination as it found it: the copy is
built in a staging directory and renamed into place, so a run killed mid-write
publishes no half snapshot, and a snapshot that fails its own read-back is
removed before the error is raised, so the failure cannot make every later
`--verify` and every later snapshot fail. The destination must be off this
install tree
(the tool refuses it), so a lost disk cannot take the copy with the config.

Verify on a schedule; this is the sample-restore drill, and it exits 1 on a
truncated, corrupted, key-drifted, or missing snapshot:

```bash
python3 scripts/backup_config.py --dest /mnt/backup/es-config --verify
```

Restore one without touching the live config, then put it in place yourself:

```bash
python3 scripts/backup_config.py --dest /mnt/backup/es-config \
    --restore 20260928_101500 --to /tmp/recovered.json
cp -a /tmp/recovered.json "$DS/Mods/EfficientServer/Config/efficientserver.json"
es reload
```

### Restore the live config

```bash
ls "$DS"/EfficientServer-uninstall-backup/          # UTC-stamped copies
make install DS="/path/to/7 Days to Die Dedicated Server"
cp -a "$DS"/EfficientServer-uninstall-backup/<stamp>/efficientserver.json \
  "$DS/Mods/EfficientServer/Config/efficientserver.json"
es reload
```

`SEVENDTD_UNINSTALL_BACKUP_DIR` moves the copies elsewhere (another disk, a
synced directory); the default sits inside the install tree, which uninstall.sh
now says out loud when it happens. `SEVENDTD_UNINSTALL_PURGE=1` skips the copy
and deletes the config with the mod, which is a deliberate act with no recovery
path.

Mid-bench-run crash (`efficientserver.json.swap-bak` present, live config holds
the harness's toggled values): the next harness run finishes the interrupted
restore automatically (`scripts/es_cfg_guard.py`). To inspect or replay it by
hand, copy the `.swap-bak` over the live file. A `.swap-bak.stale` file means
the live config moved on beyond the swap, so the guard left it alone; read it
before deciding.

### Backups that do not exist here

There is no scheduled backup of the install tree, no off-host copy of the DLL,
and no restore drill for the mod install itself. The config is the only
non-regenerable state, and `backup_config.py` covers it as far as a script
can: it cannot run on a schedule, so scheduling it is yours, and a snapshot
nobody verifies is still a hypothesis. Do not treat a green `make install` as
proof the config survives: nothing in this repo verifies that copy, it only
makes it.
