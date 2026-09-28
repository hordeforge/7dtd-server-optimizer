# Changelog

Notable changes to the EfficientServer mod and its packaging, for server
admins who deploy it. Newest first; each released section matches a GitHub
release tag. Detail behind every entry: [docs/FEATURES.md](docs/FEATURES.md)
(behavior, fidelity checks) and [docs/CONFIG.md](docs/CONFIG.md) (options,
defaults, measured gains).

## Version numbering

A release carries one version number, named twice:

- The **GitHub release tag** (`vX.Y.Z`) versions this repository's releases.
  Downloadable zips are named after it minus the leading `v`
  (`EfficientServer-<version>.zip`; `scripts/package.sh` strips the prefix,
  and a modified tree keeps an explicit `-dirty` suffix instead).
- The **mod version** (`ModInfo.xml` / assembly version, currently `1.19.0`)
  tracks the feature history of the mod itself and is what the server log
  reports at startup (`versions: mod=...`). `scripts/check_version.py` (run by
  `make test`/CI) keeps it identical across source, dist copy, and
  AssemblyInfo, rejects doc claims of versions that never shipped, and requires
  the newest dated section below to be it. `.github/workflows/release.yml`
  fails a pushed tag that does not equal the mod version, so the two cannot
  drift.

The `v0.1.0`, `v1.17.0` and `v1.17.1` tags predate that rule and all carry mod
version 1.17.0; every release after them takes its number from the tag.

## What a version number promises

This is the de facto policy, read off the history below rather than a scheme
chosen up front:

- **patch**: fixes only. The mod version lineage has one (1.4.1, follow-up
  fixes); the tag scheme began at 0.1.0, so no tagged patch release exists yet.
- **minor**: features, fixes, and config-surface changes, including removals
  and renames. 1.13.0 renamed config keys and 1.19.0 removed
  `scripts/gen_sbom.py`; the GC megapause probe's removal is the current
  instance. A minor is therefore not semver-clean against the config file, and
  an operator upgrading across minors is expected to read the notes.
- The config load is what keeps that survivable: an unknown key is ignored
  rather than rejected, the rest of the file still applies, and an ignored key
  is now named at load, one WARNING per key with its section path. A key that
  disappears leaves the lever at its built-in default, never a failed boot.
- So the contract an upgrade relies on is: read the release's `Breaking` and
  `Removed` sections (each names the exact keys or symbols and what to do),
  then check the startup log for `config unknown key` to catch anything the
  notes missed. Anything that removes or renames a config key, a console
  command, or a documented default lands under one of those two headings;
  nothing breaking goes under `Changed` alone.

## [Unreleased]

### Fixed
- `scripts/bench_parse.py --selftest` no longer fails on a loaded host. The fuzz
  gate asserted its 4000 rounds finished inside a fixed 30s wall-clock budget,
  which grades the machine rather than the parser: the same run took 49.3s here
  and passed everywhere else. The check is now a per-round ceiling, so it fires
  on a round that stopped returning (the failure a fixed-iteration fuzz can
  actually see) and not on a slow disk. The total stays on the PASS line as a
  measurement.
- `ruff format --check scripts`, the formatter half of `make lint`, is green
  again: three harness files were committed in a layout the pinned ruff
  (0.16.4) does not produce, so the gate failed on a clean checkout.
- `make package` names a missing `zip`, `git`, `find`, `sort`, `touch` or `sed`
  before it spends a compile. `zip` runs at the end of a pipeline, so its
  absence used to surface as a bare "command not found" after the whole build.
- The start-time and `es reload` apply chains no longer cascade. Both ran every
  lever inside one `try`, so a single throwing step (mesh budgets, governor
  re-base, dedicated skips) skipped every step behind it and the operator's only
  record was a single "handler failed" line with no list of what actually
  applied. Each lever now gets its own boundary through a shared `ApplyChain`
  runner; the remaining levers apply, and the failures are collected and named
  as `step[Type]: message` in one ERROR line. `es reload` still refuses to print
  its success echo over a partial apply, so the console contract is unchanged.
- A tick-guard shed batch that failed part-way no longer disappears from the
  record. Entity removal is irreversible, so a throw mid-batch used to remove
  the ids before it, skip the rest, and escape before the lifetime counter and
  the shed line were written, under-reporting what left the world. The batch now
  counts what was actually removed, continues past failures, and prints the
  failed ids on the shed line.

### Breaking
- The opt-in GC megapause diagnostic is gone: `Diagnostics.GcMegapauseTest`,
  `Diagnostics.WarmupSeconds` and `Diagnostics.GrowSeconds` no longer exist,
  and `GcDiagnostics` (the background thread that disabled the collector, grew
  the heap and timed one forced `GC_gcollect`) is deleted with them. The probe
  did ship (mod 1.5.0/1.5.1, carried in the `[0.1.0]` artifact, which packaged
  mod 1.17.0), so a config edited against that install really does carry those
  three keys. The load still succeeds: an unrecognized key is ignored rather
  than rejected, and the probe is never armed, so the only effect on such a
  config is that the unknown-key warning added in this same release now names
  each removed key on load (`config unknown key
  'Diagnostics.GcMegapauseTest' ignored`). Delete them from the installed
  `efficientserver.json` to silence it. Nothing else consumed the removal: the
  production GC path is `Gc.Incremental` plus the `Gc` safety ceiling, both
  untouched. The measurement the probe produced (479 ms forced collect on a
  6.91 GB heap) stays in `docs/RESULTS.md`; the shipped lever it informed is
  the allocation work it argued for.
- `es animoff` (every enemy animator culled, timer-only attack cadence) and
  `es rigoff` (unguarded rig visual components disabled) now REFUSE until the
  new `Diagnostics.AllowFidelityProbes` knob is true in the installed config
  (`es reload` applies it). Both commands shipped back in mod 1.14 and worked
  with no config, so a validation run scripted against 1.19.0 stops at the
  refusal line until the knob is set. Each refusal is audited and echoed, and
  `es status` shows the switch as `probeAllow=`. The restore commands
  (`es animon`, `es rigon`) and `es animstate` stay ungated, so an armed probe
  can always be walked back, and `es reload` releases one when the reloaded
  config takes the switch back to false. This matches the `es benchgod on` gate
  1.18.0 added; the default (false) is what refuses a fresh install.

### Added
- `backup_config.py` now snapshots the live `serverconfig*.xml` in the install
  root alongside the mod config, and proves both parse before reporting success.
  Those files hold the ports, password, whitelist and world settings; they are
  written into the install tree by `run_server.sh` (including the
  `<name>.pre-optimized` copy it keeps) and have never existed anywhere else,
  so until now a lost disk took them with no copy at all. A run against a real
  dedicated install that finds no `serverconfig*.xml` fails rather than quietly
  publishing narrower coverage; a mod-only staging tree warns and continues.
  `--restore` takes `--item NAME` to restore one file or `all` to write the
  whole set into a directory. `--verify` takes `--max-age-hours N`, so a backup
  job that stopped running fails instead of reporting on a directory of last
  week's healthy snapshots.
- `ES_CONFIG_PATH` overrides where the config file is read from, so tuning can
  live outside the game install (config management, a read-only install, one
  file driving several installs). It outranks the two beside-assembly
  locations, and a config file that exists but loses the precedence chain is now
  named in a WARNING instead of being ignored in silence. An `ES_CONFIG_PATH`
  set to an empty or whitespace value is an ERROR, not a silent fallthrough to
  a config nobody named. `es status` opens with the file the live values came
  from (`config=<path> enabledFile=<bool>`). The operator tooling
  (`backup_config.py`, the bench config guard) still acts on the installed
  `Config/efficientserver.json`; see `docs/CONFIG.md`.
- The game-type-free harness (config load, normalize, config-path discovery)
  now also runs on a Windows CI runner, so the README's claim that the shipped
  DLL is OS-neutral managed code is exercised on the host OS a dedicated
  server usually runs, not asserted from a Linux-only run. No shipped behavior
  changed.
- A JSON key that binds to no knob is named at load again, one WARNING per key
  with its section path (`config unknown key 'Pathfinding.GraphUpdateEveryTick'
  ignored ...`), restoring what 1.19.0 dropped so a typo stops being a silent
  default. The load itself is unchanged (fail-soft per group, the rest of the
  file still applies). The dotted path points at the section the typo is in;
  name lookup is ordinal-ignore-case, matching the binder, so a recased key
  binds and is not reported. This is the behavior docs/CONFIG.md already
  promised.
- `Diagnostics.AllowFidelityProbes` (default false) gates the console arms of
  the fidelity probes: `es animoff` (every enemy animator culled, timer-only
  attack cadence) and `es rigoff` (unguarded rig visual components disabled)
  now refuse with an audited log line until the operator opts in, matching the
  existing `es benchgod on` gate. The restore commands (`es animon`,
  `es rigon`) and `es animstate` stay ungated. `es status` shows the switch as
  `probeAllow=`.
- Every line the mod writes is now one record, one physical line, stamped with
  the mod's uptime: `EsLog.Emit` renders through `LogLine.Format`, which renders
  an embedded break (a concatenated exception's stack trace) as ` | ` and
  appends `uptimeS=<seconds>`, the same field `es status` prints. A stack trace
  used to reach the server log as several untimestamped continuation lines, and
  a governor or `TickGuard` line could not be placed against the uptime the
  server is at now. The world-load anchor line keeps its `world loaded` text;
  its inline `at uptime Ns` is the new trailing field, not a separate one.

### Fixed
- A failure to read whether the host is a dedicated server failed closed and
  silent. That read gates EVERY patch prefix, so a host where it kept throwing
  left the whole mod unpatched, and `es status` showed it only as
  `modActive=false`, indistinguishable from a disabled config. It now announces
  once on the WARNING channel and registers as `dedicatedGate` on the
  `degraded=` line, so the count reads as how long the server has run unpatched.
- A mod line that failed to reach the game log said nothing about it. The
  fallback wrote the line to stdout and dropped the reason, so a server whose
  `Log` static rejected writes had an aging `[EfficientServer]` tail in the log
  file and no clue why. The first failure now names the exception and states
  that later lines reach the console only; the per-line stdout fallback is
  unchanged.
- `make install` now refuses an install dir that is neither a 7 Days to Die
  dedicated install (no `7DaysToDieServer_Data/`, no server binary), nor an
  already-staged destination, nor empty or absent. A mistyped `DS=` pointing at
  an unrelated populated directory reached an `rm -rf` of
  `Mods/EfficientServer` with nothing in the output to explain it.
  `uninstall.sh` already applied a stricter form of this check. Staging an
  install for another host, and reinstalling over an existing mod folder, are
  unaffected.
- `scripts/backup_config.py --restore` takes a directory NAME and refuses
  anything else. A traversing stamp (`../outside`, an absolute path) resolved
  outside the snapshot root, and the per-snapshot verification keyed on the
  directory's own name never matched it, so the copy came from a file no
  verification step had looked at.
- The config guard's abandoned-temp sweep could be crashed from outside the
  protocol: it parsed a pid out of a filename sitting in the LIVE install
  directory, and `str.isdigit()` accepts names `int()` then rejects (`²`) as
  well as digit runs too large for `os.kill` (OverflowError). Neither is an
  `OSError`, so either one aborted the run with the config already snapshotted
  and not yet restored. A name that is not a plain ASCII pid within the
  kernel's range is now left untouched.
- The bench harnesses (`measure_es_onoff.py`, `validate_anim_path_admission.py`,
  `validate_bloodmoon_path.py`) now refuse an exported-but-empty
  `SEVENDTD_DS_DIR`, `SEVENDTD_SERVER_DIR` or `VALIDATE_OUT` instead of
  falling through to the stock install. The shell scripts have rejected this
  for the same reason: the harnesses rewrite the live installed config in
  place, so a mistyped empty value swapped knobs in an install the operator did
  not name.
- A coverage report whose `line-rate` is `NaN` or an out-of-range exponent
  (`1e999999999`) exited `coverage_badge.py` with a traceback; only
  `InvalidOperation` was handled. Both now report the named FAIL line and exit
  1, leaving any existing badge untouched.
- A `scripts/backup_config.py` run that failed its own read-back left the
  snapshot it had just written under the backup destination, where it made
  every later `--verify` and every later snapshot fail, with no rerun able to
  clear it. The copy is now built in a staging directory and renamed into
  place, so a run killed mid-write publishes no half snapshot, a snapshot
  that fails its read-back is removed before the error is raised, and the next
  run sweeps a staging directory whose owning pid is gone.
- `scripts/verify_reproducible.sh` cleared `dist/` to make its "exactly one
  zip" check work, so running it destroyed a release zip the operator was
  about to publish, and a killed run destroyed it with nothing put back. The
  zips already there are parked before the first leg and restored on every exit
  path; one whose name the run rebuilds is kept aside at a printed path rather
  than deleted.
- The dedicated-server host type could be resolved twice with different answers
  and the loser could win. Every patch prefix gates on `ShouldRun`, and the
  LiteNetLib receive thread reaches that gate (the client-list snapshot's
  duplicate-IP scan runs there) as does the main thread, but the cached answer
  was a resolved-flag/value pair any thread finding the flag clear would fill
  in. In the boot window, where the flag is still clear, a thread that read
  `GameManager.IsDedicatedServer` before the game published it could publish
  "client host" over the main thread's "dedicated", and the mod would then stay
  inactive for the life of the process with nothing in the log. The answer now
  lives in `DedicatedHostGate`: one volatile int for the three states, published
  under a lock so the first answer stands, with the host-type read taken outside
  the lock and a read that throws still leaving the answer unresolved (fail
  closed, retried on a later call). No shipped lever or default changed.
- The config structure fuzz treated the static `ServerPerfConfig.LastLoadFailed`
  load outcome as a knob, so it mutated a leaf the serializer never writes and
  the suite failed on `leaf 'LastLoadFailed' present in serialized defaults`.
  The reflected schema now walks instance properties only, which is what the
  deserialized-defaults seed actually contains.
- `TickGuard` shed batches were not reproducible: co-located enemies share a
  distance exactly, and the batch was cut by `World.Entities.list` order, so the
  same horde at the same distances could shed different zombies on two runs.
  Distance is now a total order (farthest first, lowest entity id inside a tie),
  and the selection moved to a game-type-free seam (`ShedOrder.Select`) the unit
  harness drives directly. The batch scratch also holds ids instead of entity
  references, so a horde is no longer pinned between sheds.
- `scripts/backup_config.py` ordered snapshots by directory name, so the
  same-second `_N` counter sorted bytewise (`..._101500_10` before
  `..._101500_9`) and `--keep` could prune the newest snapshot as the oldest
  once a second held ten or more copies. Ordering is now the parsed stamp plus
  the counter, and the same-second suffix is zero-padded to the same width.
  The selftest runs on a fixed clock instead of wall time, which had made
  `make test` fail on roughly one run in three.
- A console-armed bench probe outlived the config that allowed it. `es
  benchgod on` sets a process latch, so a bench harness killed before its own
  `es benchgod off` (or an operator setting `Diagnostics.AllowBenchGod` back to
  false) left every player damage-immune on a live server, and an armed
  `es animoff` / `es rigoff` kept enemy combat timing and rig visuals degraded
  after `Diagnostics.AllowFidelityProbes` was taken back. The damage-immunity
  prefix now re-checks the live allow-switch on every hit, and `es reload`
  clears the benchgod latch and releases an armed animator/rig probe when the
  reloaded config no longer allows it. Repeats are no-ops, so a second reload
  changes nothing.
- `scripts/install.sh` ignored the `DS=` spelling it documents: only
  `SEVENDTD_DS_DIR` resolved the target, so `DS=/path scripts/install.sh` fell
  through to the stock Steam path and installed (and `rm -rf`'d the mod folder
  of) a *different* server than the operator named. The Makefile exports
  `SEVENDTD_DS_DIR` from `DS=`, so `make install DS=...` was unaffected and the
  script's own `--help`, plus the restore hint `make uninstall` prints, both
  advertise the spelling. `SEVENDTD_DS_DIR` wins, then `DS`, then the stock
  path, and either variable set-but-empty now fails instead of falling through
  to the default.
- `make install` destroyed everything in the installed `Config/` except
  `efficientserver.json`: the bench guard's `efficientserver.json.swap-bak` and
  its quarantined `.stale` files are the only crash-recovery snapshot of a
  config a killed run left half-swapped, and the upgrade wiped them (the
  uninstall path already kept them, and docs/PRODUCTION.md claimed the same
  RPO for a reinstall). The install now copies the whole `Config/` out before
  the wipe and restores every file from it, keeping the shipped default only
  when the installed JSON is byte-identical to it. Installed file modes are
  normalized to 755/644, so `make install` and the release zip produce the same
  tree.
- The bench harnesses read the installed `efficientserver.json` as strict
  UTF-8, so a leading BOM (which the game's own reader has always tolerated)
  made every one of them fail: `recover()` quarantined a perfectly good backup
  as "unreadable" and `restore()` fell into its unreadable-live branch and
  overwrote the config from the snapshot instead of replaying the managed keys
  only. All three readers now share one `CFG_ENCODING` (utf-8-sig) constant, so
  a BOM'd config takes the same paths a BOM-less one does and a later operator
  edit still survives a bench run.
- `es status` reported `tickEmaMs` as a live number even with the governor
  disabled, where no tick was ever sampled and the EMA just holds its 50 ms
  seed. A server at 3 TPS read as a healthy idle tick. The field now prints
  `n/a` whenever the governor is off or the mod is inactive, so a number in
  that line always means something is measuring it.
- The GC safety guard suppressed its forced collect when the ceiling could not
  be resolved (host RAM unreadable) without saying so, leaving a long-lived
  server free to grow unbounded with no log. The unresolvable case now warns
  once; the recurring "heap above ceiling" warning is unchanged.
- A tick-guard shed that fired but was withheld (no world, no players, or
  living enemies at or below `TickGuard.MinEnemiesKept`) was silent, so an
  operator could not tell "never triggered" from "triggered and did nothing".
  Each suppression logs on the same WARNING channel with its reason, the tick
  EMA, and the lifetime shed count.
- `Gc.Incremental` was marked applied before the mode flip ran, so a host whose
  Boehm entry point was missing reported a mode flip that never happened and
  never retried it. The one-shot guard is now set only after `GC_enable_incremental`
  lands, a failed pause-target P/Invoke warns on its own instead of failing the
  whole apply, and `es reload` retries a flip that did not take.
- Governor tier 2 saved enemy animator culling modes by Unity instance ID only.
  Unity recycles those IDs after a destroy, so a newly spawned rig could inherit
  a dead rig's saved mode and have it restored on exit. Each entry now carries
  the rig it was read from and is dropped at the next sweep when the live rig no
  longer owns the ID.
- `make uninstall` prints a restore line only for the config files it actually
  preserved. It keeps every file under `Mods/EfficientServer/Config/`, so with
  only the guard's `.swap-bak` present the old hint named an
  `efficientserver.json` that was never copied.
- `scripts/uninstall.sh` took no arguments and never checked argv, so a
  mistyped flag (`uninstall.sh --purge`) was silently ignored and the mod
  folder was deleted anyway. It now takes the same `-h`/`--help` and
  unknown-argument exit 2 the other scripts here do, both before any path
  guard runs.
- `scripts/coverage_badge.py` died on a traceback when the Cobertura report was
  missing, malformed, or carried a non-numeric `line-rate`, or when the badge
  could not be written. Each is a bad argument from CI's side: it now prints
  one stderr line naming the path and exits 2, the same code the argument-count
  error already used.
- `--help` on `measure_es_onoff.py`, `validate_bloodmoon_path.py` and
  `validate_anim_path_admission.py` died on an import traceback unless the
  `7dtd-loadgen` sibling tree happened to be cloned next to the repo. Each now
  answers `--help` and rejects unknown arguments before that import, and a run
  without the sibling names the directory it looked in instead of raising.
- `check_config_doc.py` reported an unreadable gate input as a Python
  traceback; it now prints the FAIL line every other gate uses.

### Changed
- The governor throttle-ceiling constants (`EntityStrideMax`, `GraphUpdateMax`)
  are actually used now: `Normalize` and `ApplyThrottledLevers` read them
  instead of repeating `4` and `200` as literals.
- The four dedicated-skip prefixes share one gated `SkipOnDedicated` fetch
  instead of each repeating the same null-guard chain.
- Restore sources are now pinned in-repo (`NuGet.config`, nuget.org only with
  inherited machine and user feeds cleared) instead of coming from whatever
  feed the host machine happens to configure.
- The .NET SDK pin is exact within its band: `global.json` moves from
  `rollForward: latestFeature` to `latestPatch`, so a new 8.0.5xx SDK can no
  longer be picked up silently. Bump `version` there to move bands.
- Governor no longer writes the throttled replication and nav-graph cadences
  into the loaded config. The levers are derived per read from the configured
  values plus the current tier, so `es status` shows the configured numbers and
  the values in force separately, and a reload can no longer restore a stale
  cached baseline. The tier arithmetic moved to `Patches/GovernorTiers.cs`
  (game-type-free, unit-tested) next to `TickClock` and `TickIntervalEma`.
- `ModApi.Config` and `ModApi.Active` are published through `volatile` storage
  (`ConfigPublication.Current` and the `_active` field), so the receive-thread
  surfaces that read them per call see a whole config generation, never a
  half-swapped one.
- The bench config guard sweeps the atomic-write temp files a killed run
  strands beside the live config (`<file>.tmp<pid>`) at the start of every
  run. Those runs are routinely SIGKILLed by tool timeouts, and nothing ever
  removed them, so a long-lived server install accumulated one per killed
  run. Temps owned by a still-running pid are left alone.
- `Server.TargetFps > 0` now caps `Governor.OverBudgetMs` at 1.2x the target
  frame interval (60 at fps 20, 30 at 40, 20 at 60). The band is compared
  against the measured frame interval, so the fps-20 default of 57 could never
  be exceeded above ~18 fps: raising the frame target left the governor
  permanently healthy, i.e. silently off. A band tuned below the cap is kept
  as written; a wider one is pulled down and named in the `config corrected`
  log. Defaults (`TargetFps` 0) are unchanged.

## [1.19.0] - 2026-09-20

Artifact: `EfficientServer-1.19.0.zip`, containing mod version 1.19.0.

### Changed
- Governor throttle ceilings are `Config` constants now
  (`EntityStrideMax`, `GraphUpdateMax`) instead of the separate
  `GovernorTiers` helper; the semantics (throttled lever never below its
  configured baseline, capped at the Normalize ceilings) are unchanged.
- The cadence stride gate is gone as a shared type: the stride patches read
  slot ownership through `TickClock` like the other levers. Behavior is the
  same every-Nth-tick schedule.
- The logging surface collapsed to one `EsLog.Emit(LogLevel, msg)` API with
  an internal `LogLevel` enum; `EsLog.Log` / `Warn` / `Error` wrappers are
  removed. Output channels are unchanged.
- Config load no longer scans for unknown keys at runtime: a misspelled knob
  silently keeps its default (fail-soft), exactly as before, and template
  typos are caught pre-packaging by `scripts/check_config_doc.py`. The
  runtime JSON walk (and its locale-sensitive comparison risk) is removed.

### Removed
- Release packaging no longer ships a CycloneDX SBOM inside the zip or a
  `.buildinfo.txt` sidecar beside it. `scripts/gen_sbom.py` is deleted and
  its selftest dropped from `make test`. Artifact verification rests on the
  reproducible package plus `make verify-reproducible`.

## [1.18.0] - 2026-09-11

Artifact: `EfficientServer-1.18.0.zip`, containing mod version 1.18.0.

### Fixed
- `make test` was red on `main`: the config harness failed to compile
  (`Program.cs` CS8602) because a `d2.Network != null` comparison narrows the
  reference to maybe-null for the rest of the method, and a later
  `ClientListSnapshot` assertion dereferenced it bare. Null-guarded like its
  neighbours.
- Stock join-churn race: the connection-request duplicate-IP scan ran on the
  LiteNetLib receive thread and enumerated the live `Clients` list the main
  thread mutates during joins/disconnects, throwing "Collection was modified"
  under churn and cascading into `RemoteConnectionClose` bursts that dropped
  connected clients (the same stock bug that capped live validation cohorts at
  ~12 bots). The new `Network.ClientListSnapshot` lever (default on) enumerates
  a private point-in-time snapshot instead; rate limiting, rejects, and Accept
  are untouched, decision semantics hold up to one copy instant, and a raced
  copy fails open to an empty scan instead of crashing. Set it false to
  reproduce the vanilla behavior in a controlled A/B.
- The apply-once knobs (`Server.TargetFps`, `Server.JobWorkerCount`,
  `DynamicMesh.*` budgets) now undo their effect when a reload disables them:
  reloading to `TargetFps: 0`, `JobWorkerCount: 0`, or
  `DynamicMesh.Enabled: false` - or setting `Enabled: false` for the emergency
  "all levers inert" procedure - restores the pre-mod values instead of
  silently keeping the applied override until restart. These knobs also gate on
  ShouldRun now like every sibling GameStartDone action, so a mod disabled at
  startup no longer applies them at all.
- Turning `Governor.AnimatorEmergency` off mid-tier-2 via `es reload` now steps
  down to tier 1 and restores the rigs immediately (the flag is opt-in).
  Previously the reloaded flag was ignored while tier 2 stayed engaged and the
  periodic sweep kept re-entering CullCompletely until tick recovery.
- `es reload` now fully honors the "re-enable without a restart" contract for
  the two apply-once groups it missed: the imperative dedicated skips
  (dynamic music, water splash, environment audio, ambient light spectrum)
  and opt-in GC incremental mode. Previously both were installed at game start
  only if the config was already enabled, so a disabled->enabled reload left
  them inactive until restart while every other group activated live. The
  re-apply is idempotent (Harmony replaces an identical patch method); the
  GC megapause diagnostic stays start-time-only by design.
- Opt-in GC megapause diagnostic: `Diagnostics.WarmupSeconds` is now clamped to
  [0, 3600] and `Diagnostics.GrowSeconds` to [1, 7200], and the sleep duration
  is computed in long math. Previously an unclamped warmup above ~2.1M seconds
  wrapped the milliseconds product negative and killed the probe with a
  misleading log.

### Added
- Bench-god runtime guard: `es benchgod on` (global player damage immunity)
  now refuses to arm unless the new `Diagnostics.AllowBenchGod: true` opt-in is
  present in the installed config (`es reload` applies it). Reaching
  telnet/console alone no longer suffices to make every player immortal on a
  live server; the refusal is echoed and logged, `es status` shows the switch
  as `benchgodAllow=`, and `es benchgod off` always works. The shipped config
  template omits the Diagnostics group on purpose, so fresh installs refuse;
  the animator/path validation harness writes the flag swap-guarded for its
  bench runs and restores it afterwards.
- Supply-chain inventory: `make package` now embeds a deterministic CycloneDX
  1.5 SBOM at `EfficientServer/bom.json` in every release zip, generated from
  the committed `packages.lock.json` graph (component versions plus NuGet
  content hashes; game-provided libraries are marked not-bundled). All inputs
  are in-tree values, so it stays byte-identical across rebuilds and inside
  the `make verify-reproducible` guarantee. A selftest gate
  (`scripts/gen_sbom.py --selftest`) runs in `make test`. See the new
  "Supply chain" section in `SECURITY.md`.
- License text now ships in the artifact: every build copies the repo's MIT
  `LICENSE` to `EfficientServer/LICENSE.txt`, so release zips and installed
  mod directories carry it instead of pointing at the repository.
- `make install` preflights its runtime dependency: when the target server has
  no `Mods/0_TFP_Harmony/0Harmony.dll`, install.sh warns by name instead of
  completing silently into a state where the game skips the mod with only
  generic log errors. The check targets `$SRV` itself, so installs staged from
  a client-side compile are covered too; it warns rather than fails, keeping
  cross-host staging possible.

### Changed
- Internal refactor: feature-gating keys shared between config parsing and
  ModApi startup notes; no config schema or behavior change.
- Tooling/packaging: reproducible zip packaging, SDK pinned via
  `global.json`, hardened shell scripts; the ES on/off measurement helper now
  tail-reads APM logs instead of rescanning whole files. Nothing here changes
  the shipped DLL surface.
- `make verify-reproducible` automates the rebuild-and-compare check of the
  packaging reproducibility claim (same-tree repackage, full recompile,
  out-of-tree path variation), and `make package` now writes a buildinfo file
  (toolchain, commit, epoch, zip sha256) next to the zip so release artifacts
  record the environment that produced them.
- Removed the vendored `scripts/dotnet-install.sh` bootstrap.
- Dependency audit: dropped the unused `MemoryPack` game-DLL reference (no
  source usage, compile-verified against both build backends); the test
  project's `Newtonsoft.Json` dependency is now hash-pinned in a committed
  `packages.lock.json` that `make test` restores in locked mode.
- CI: the workflow now triggers on PRs and direct pushes to `main` instead of
  every branch push, so a pushed PR branch no longer starts a duplicate run,
  and checkout no longer persists the GitHub token into the runner workspace
  (the test gate performs no git operations). The coverage-badge job now
  authenticates through an environment-reading credential helper instead of a
  `https://x-access-token:$TOKEN@...` URL, which put the token in argv and
  persisted it into the badge clone's `.git/config` on disk.
- Temp files no longer land in `/tmp`, which is tmpfs on most Linux hosts: the
  package staging tree, `verify-reproducible`'s full tree copy, install.sh's
  pre-upgrade config backup and every gate's test temp directory are RAM-backed
  and lost on reboot there. All of them now route through `TMPDIR`, pointed at
  the gitignored `.scratch/tmp` on disk. install.sh's backup is the copy that
  matters: after a failed install it is the only place the operator's tuning
  still exists.
- The harnesses reap stray loadgen and dedicated-server processes with a
  `/proc` walk instead of `pkill -f`, dropping an external-process dependency
  and the bracketed-glob trick that kept the pattern from matching its own
  command line. The current process is now excluded by pid.
- Scripts resolve the repository root by walking up for marker files
  (`scripts/repo_root.py`, selftested in `make test`) instead of counting
  `parent.parent` levels, so moving one into a subdirectory fails loudly
  rather than silently reading the parent tree.
- Dropped `*.lock` from `.gitignore`: nothing in this repo produces such a
  file, and the pattern would have silently excluded a future lockfile from
  version control.

## [1.17.1] - 2026-08-23

Artifact: `EfficientServer-1.17.1.zip`, containing mod version 1.17.0, the same
DLL as v1.17.0. The tag moved without a mod bump, which is the drift the
release tag gate now rejects. (The `v1.17.0` tag, the same day, packaged the
same mod version and is described by `[0.1.0]` below.)

### Changed
- Mod metadata: author `7dtd` to `HordeForge`, and the empty `Website` value to
  the repository URL. Both are what the game's mod list shows.
- Repository and doc paths retargeted to the hordeforge layout, and
  `scripts/run_server.sh` gained a `--ds` flag and a documented environment
  block in place of the inline defaults.

## [0.1.0] - 2026-08-22

First packaged release. Artifact: `EfficientServer-0.1.0.zip`, containing mod
version 1.17.0, built against 7 Days to Die dedicated **V3.1.0 (b14)**.

Requirements: dedicated server, EAC disabled, stock `0_TFP_Harmony` present in
`Mods/`. Patches fail soft per group: a game update that moves one target
disables only that optimization and logs
`MISSING TARGET: <Patch> matched no game method`.

Upgrade behavior: installing over an existing deployment preserves a user-edited
`efficientserver.json`; unknown config keys are ignored and missing keys fall
back to built-in defaults, so configs from earlier development snapshots keep
loading.

### Added
- AI LOD: distance-banded scaling of AI work plus mid-band tick striding for
  distant non-alert task updates (stride default 1 = off).
- Dedicated-only skips for presentation paths useless on headless servers.
- Dynamic mesh budgets (player-area and time budgets).
- GC pause guard: skips forced periodic `GC.Collect` (host-aware safety
  collect remains); opt-in incremental GC mode; opt-in megapause diagnostic.
- Pathfinding graph throttle: rate-limits `AstarManager.UpdateGraphs`
  (`Pathfinding.GraphUpdateEveryTicks`).
- Path admission (v1.17.0): optional cap on non-priority path enqueues per
  tick and far-drop knob; both default off (vanilla).
- Network single-target fast send (default on, provably equivalent to the
  vanilla scan): O(1) recipient lookup for the pure single-target send case;
  no wire change.
- Ambient light-spectrum skip (default on).
- Adaptive load governor (default on; inert while healthy): engages measured
  throttles under overload; opt-in tier 2 animator emergency (v1.16.0/v1.17.0,
  default off).
- TickGuard emergency far-zombie shedding (opt-in, default off).
- Animator LOD (opt-in, default off).
- Entity-replication stride (opt-in, default off).
- Explosion particles skip.
- Chunk-send throttle (EXPERIMENTAL, unvalidated; evaluate before production
  use).
- `es` console command family for runtime inspection/toggles.
