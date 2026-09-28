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
  the newest dated section above to be it. `.github/workflows/release.yml`
  fails a pushed tag that does not equal the mod version, so the two cannot
  drift.

The `v0.1.0`, `v1.17.0` and `v1.17.1` tags predate that rule and all carry mod
version 1.17.0; every release after them takes its number from the tag.

## [Unreleased]

### Breaking
- The opt-in GC megapause diagnostic is gone: `Diagnostics.GcMegapauseTest`,
  `Diagnostics.WarmupSeconds` and `Diagnostics.GrowSeconds` no longer exist,
  and `GcDiagnostics` (the background thread that disabled the collector, grew
  the heap and timed one forced `GC_gcollect`) is deleted with them. A config
  still carrying those keys keeps them on disk, but nothing reads them: the
  probe is simply never armed, so delete them. Nothing else consumed the
  removal: the production GC path is `Gc.Incremental` plus the `Gc` safety
  ceiling, both untouched. The measurement the probe produced (479 ms forced
  collect on a 6.91 GB heap) stays in `docs/RESULTS.md`; the shipped lever it
  informed is the allocation work it argued for.

### Added
- A JSON key that binds to no knob is now named at load
  (`config unknown key 'Pathfinding.GraphUpdateEveryTick' ignored ...` on the
  WARNING channel) instead of silently leaving the lever at its default. The
  dotted path points at the section the typo is in; case variants of real keys
  still bind and are not reported. This is the behavior docs/CONFIG.md already
  promised.
- `Diagnostics.AllowFidelityProbes` (default false) gates the console arms of
  the fidelity probes: `es animoff` (every enemy animator culled, timer-only
  attack cadence) and `es rigoff` (unguarded rig visual components disabled)
  now refuse with an audited log line until the operator opts in, matching the
  existing `es benchgod on` gate. The restore commands (`es animon`,
  `es rigon`) and `es animstate` stay ungated. `es status` shows the switch as
  `probeAllow=`.

### Fixed
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
- Unknown config keys are named again at load, one WARNING per key with its
  section path (`config unknown key 'Pathfinding.GraphUpdateEveryTick' ignored`).
  The load is unchanged (fail-soft per group, the rest of the file still
  applies), so this only makes a typo visible instead of silent. Name lookup is
  ordinal-ignore-case, matching the binder: a recased key binds and is not
  reported.
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
