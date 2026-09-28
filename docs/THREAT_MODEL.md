# Threat model

Systemic view of what this repository exposes to attack: entry points, trust
boundaries, assets, threats, and the controls that exist versus the ones that are
missing. Individual vulnerability findings belong to sec-review; they land here
as threats with locations. Re-verify every file reference after each game update
(Harmony targets break silently, see AGENTS.md critical rule 3) and after each
change to the scripts named under E5, E9, and E10.

Last reviewed: 2026-09-28, against git 111ed95.
Owner: repository maintainers. Review trigger: every game update, every new
patch group, every change to `scripts/install.sh` / `uninstall.sh` /
`run_server.sh` / `backup_config.py` / `es_cfg_guard.py`, every added GitHub
workflow, and every change to the console arm gates named under B2.

## Risk-ranked summary

| # | Risk | Boundary | Why |
|---|---|---|---|
| R1 | Full-host-authority code runs inside the game server process | Mod to host | By design: a Harmony mod is arbitrary code with the server's privileges. No isolation exists or is possible while it stays a C# mod. Every bug is a server crash or worse, not a sandbox escape |
| R2 | Unverified build artifact installed over the game tree | B4 build to runtime | `install.sh` does `rm -rf` of the destination and copies `dist/EfficientServer/` into `Mods/` with no hash or signature check (`scripts/install.sh:150,152`). Nothing on the install path compares the artifact to a trusted build. Whoever controls `dist/` or the Mods directory controls the server process |
| R3 | Config-file write access silently pre-authorizes every lever, including the diagnostic arms | B1 filesystem to mod | `Config/efficientserver.json` is read with no signature and no watcher. Anyone who can write that file controls policy: `es reload` re-reads it (`Source/EfficientServer/ModApi.cs:143`). The gates that guard `es benchgod on` and `es animoff`/`es rigoff` are themselves config flags (`Diagnostics.AllowBenchGod`, `Diagnostics.AllowFidelityProbes`, `Source/EfficientServer/Config.cs:699,708`), so a config write both sets the limits and lifts the guard. `es` is a consequence, not a prerequisite. Two writer tools in this repo hold that same position (E9, E10) |
| R4 | Console actor can degrade or disable live gameplay with one command | B2 console to mod | The bench-god and fidelity-probe arms are refused unless the operator opted in (`ConsoleCmdEfficientServer.cs:392,204`). Where the opt-in is on, any console-level actor gets global damage immunity for every player (`Patches/BenchGodPatch.cs:32`) or enemy animation and rig disable, unconfirmed and unscoped. Partly mitigated since the last review: revoking the opt-in in the config now stops the damage immunity on the next reload and per-damage-frame re-read (`Patches/BenchGodPatch.cs:32-33`), and a reload without the probe opt-in releases armed animator/rig probes (`ModApi.cs:160-168`). The arm itself is still one console command away, and neither is persisted across restart, so a restart re-applies nothing while a kill mid-bench leaves the arm hot until a reload |
| R5 | Config-file self-denial-of-service paths | B1 filesystem to mod | Clamped maxima are still potent: `TickGuard` despawns living enemies once the tick EMA stays past `ShedAboveMs` for `WindowTicks` (`Patches/TickGuardPatch.cs:134`) and `Governor.AnimatorEmergency` engages itself past `EmergencyOverMs` (`Patches/GovernorPatch.cs:147`); both default off, both config-enableable. A stale measurement cannot survive a world change: both re-base their interval EMA and window counters on every world change (`Patches/TickGuardPatch.cs:43-47`, `Patches/GovernorPatch.cs:172-174`, driven by `Patches/GameStartPatch.cs:38-39`) and drop the average whenever their gate is closed, so an `es reload` that re-enables a lever cannot fire a shed on the new world's spawn load alone. The heap-growing GC megapause probe that used to sit here was removed after tag `v1.19.0` and is unreleased (recorded under `Breaking` in `CHANGELOG.md`) |
| R6 | Inherited telnet exposure | B7 network to console | The shipped serverconfig template enables telnet on port 8082 with an empty password (`serverconfig.optimized.xml:33-35`); safety depends entirely on the game's loopback fallback and failed-login limit, not on this repo |
| R7 | The mod project's NuGet hashes are recorded and now enforced, with a first-lookup residual | B4 build to runtime | `Source/EfficientServer/packages.lock.json` is committed and carries a `contentHash` for both reference-assembly packages, so the graph is reviewable. Both are declared in `Source/EfficientServer/EfficientServer.csproj` with an exact range: the parent `Microsoft.NETFramework.ReferenceAssemblies` had to be declared explicitly, because the SDK adds it as a bare `Version="1.0.3"`, which NuGet normalizes to the open range `[1.0.3, )`, and the SDK skips its implicit reference when a `PackageReference` with that identity already exists. Before that it was the one range in the fetched graph that could float. The hash is now checked: `make build` (`scripts/build.sh`) restores the mod project with `--locked-mode`, and `make test` restores it too (`Makefile`, `unit`), so a lock file that drifts fails a gate instead of being rewritten in place. Restore needs no game install, because the game assemblies are bound as build-time references. The residual is what no graph can close: both restores still resolve a name from nuget.org first, so a feed that answered the very first lookup of a fresh cache could serve a package the lock file then rejects. The packages supply reference assemblies only, so they cannot change the emitted IL. Publisher signatures cannot close the gap: all three packages are author-signed, but the two reference-assembly packages carry a certificate that expired 2023-10-05, so `signatureValidationMode` in `require` mode fails their restore and stays off; the contentHash is what verifies the bytes. `README.md`, `SECURITY.md` and `NuGet.config` updated with the behavior |
| R8 | Repo tooling writes the live config and moves it off-host | B1 tooling to install | `scripts/es_cfg_guard.py` rewrites managed keys of the installed config in place and unlinks stranded temp files beside it; `scripts/backup_config.py` copies the live config to an operator-named destination outside the install tree and can restore over the live file. Both are the R3 write position held by a script, so both are in the blast radius of anything that can run them |

Not risks here: the mod opens no sockets, spawns no processes, stores no
credentials, handles no player data beyond what the game already holds, and
never writes outside its mod folder and the log (verified: no socket/process/
write APIs under `Source/EfficientServer/`; the only file read is
`Source/EfficientServer/Config.cs:393`).

## Entry points

| ID | Entry point | Location | Notes |
|---|---|---|---|
| E1 | Mod load into game process (`InitMod`) | `Source/EfficientServer/ModApi.cs:49` | Game loads the DLL at startup and calls `InitMod`; Harmony patches install here per group. Post-start setup registers on the sanctioned `GameStartDone` hook, not a patched game method (`ModApi.cs:122-125`) |
| E2 | Config JSON file read | `Source/EfficientServer/Config.cs:384` (`Load`), path resolution `Config.cs:664` | Read once at init (`ModApi.cs:72`) and again on every `es reload` (`ModApi.ReloadConfig`, `ModApi.cs:140-143`). Parsed with Newtonsoft.Json (game-bundled); read pinned to UTF-8 with BOM tolerated (`Config.cs:394`). No file watcher, no signature, no ownership check; disk changes apply only via E3 |
| E3 | Operator console command `es` / `efficientserver` | `Source/EfficientServer/ConsoleCmdEfficientServer.cs:26` (`Execute`) | Subcommands: `reload`, `status`, `animoff`/`animon`, `animstate`, `rigoff`/`rigon`, `benchgod on\|off` (`ConsoleCmdEfficientServer.cs:32-89`). Reachable from the server terminal, the telnet remote console, and in-game clients the game's permission system admits to console commands |
| E4 | P/Invoke into bundled Boehm GC library | `Source/EfficientServer/BoehmNative.cs` (declarations), `Source/EfficientServer/GcIncremental.cs:21` (call site) | `monobdwgc-2.0`; flips collector mode and sets the pause limit. The megapause probe P/Invokes were removed after `v1.19.0` (the `Breaking` entry in `CHANGELOG.md`); what remains is the mode flip and the optional pause target |
| E5 | Install/run/uninstall scripts | `scripts/install.sh`, `scripts/uninstall.sh`, `scripts/run_server.sh`, `Makefile:254,259,276` | Build, back up user config, wipe and copy artifacts into `<DS>/Mods/EfficientServer/`, export GC/JIT env vars, exec the server binary. All three reject an exported-but-empty `SEVENDTD_DS_DIR` or `DS` rather than defaulting (`install.sh:46,52-57`, and the same guard in `uninstall.sh` and `run_server.sh`) so the `rm -rf` cannot land on a different install, and `install.sh` additionally trims the value and rejects one that resolves to empty or `/` (`install.sh:66-71`) so a stray space or slash cannot aim the wipe at the filesystem root. `uninstall.sh` copies `Config/` out to a timestamped backup before the wipe (`uninstall.sh:129-131`) and warns when that backup sits inside the install tree (`uninstall.sh:121-125`); `run_server.sh` keeps the `<DS>/serverconfig*.xml` it replaces. `install.sh` preserves `Config/` to a fixed path rather than a `mktemp` name (`SEVENDTD_INSTALL_BACKUP_DIR`, default `<DS>/EfficientServer-install-backup`) and warns when that path sits inside the install tree, so a failed install is recoverable by simply rerunning the script: the next run adopts the preserved copy instead of installing the shipped default over the operator's tuning, and a successful install removes it. `install.sh` and `uninstall.sh` take the same `flock` lock (`scripts/repo_lock.sh`) over the installed tree, and `build.sh` takes a second one over `dist/` and the intermediate `obj`/`bin`, because all of those paths are `rm -rf`ed and rewritten by a run that does not know another one is in flight |
| E6 | CI and release workflows | `.github/workflows/ci.yml`, `.github/workflows/release.yml` | `ci.yml` runs `make test` on pushes to main and on PRs. `release.yml` runs on a `v*` tag, is `permissions: contents: read`, and only compares the tag to `Source/EfficientServer/ModInfo.xml` and runs `scripts/check_version.py`; it deliberately does not build the archive |
| E7 | Repo Python tooling | `scripts/check_config_doc.py`, `scripts/check_version.py`, `scripts/coverage_badge.py`, `scripts/repo_root.py`, `scripts/cli_common.py`, `scripts/selftest_support.py` | Stdlib-only, run by `make test` and CI over repo content and `config/efficientserver.json` (`Makefile:217-231`). They read the working tree and exit nonzero; none writes outside the repo. Every one ships a `--selftest` that drives its own logic |
| E8 | Offline measurement and validation scripts (not in `make test`) | `scripts/validate_anim_path_admission.py`, `scripts/validate_bloodmoon_path.py`, `scripts/measure_es_onoff.py` | Need a live dedicated server, so the harnesses themselves are syntax-gated by `compileall` (`Makefile:221`); the parsers they read server output with are fuzzed in `make test` (E12). They connect to a server the operator names, so the target host is operator-supplied input, not a network listener this repo opens. Two of them import E10 to swap config keys for the duration of a run, and all three read server output through E12, which is in `make test` |
| E9 | Off-host config backup, verify, restore | `scripts/backup_config.py` (`snapshot:327`, `verify:491`, `restore:593`), `Makefile:270` (`make backup-config`) | Reads the live installed mod config AND the live `serverconfig*.xml` in the install root, and writes timestamped snapshots plus a per-file sha256 manifest to a destination the operator names. Refuses a destination inside the server install tree (`_resolve_outside_install`, `backup_config.py:308`); prunes to `DEFAULT_KEEP` snapshots, and never deletes a destination directory whose name is not one of its own stamps (an undated one is reported by `verify` instead, and is left for the operator to account for); `verify` re-reads every snapshot the way a restore would (JSON parse, XML parse, per-file sha256, serverconfig recorded-but-absent) and exits nonzero on the first that would not load. `restore` copies a verified snapshot to `--to` and refuses to clobber an existing file without `--force` (`backup_config.py:644-646`). A real dedicated install with no `serverconfig*.xml` fails the run rather than publishing narrower coverage. Self-test is wired into `make test` (`Makefile:231`) |
| E10 | Live-config backup/restore guard (library + CLI) | `scripts/es_cfg_guard.py` (`ConfigSwap:330`, `_sweep_abandoned_temps:431`, `_write_atomic:187`, `TEMP_ATTEMPTS:82`) | Rewrites managed keys of `Mods/EfficientServer/Config/efficientserver.json` in place, keeps a `.swap-bak<pid>` snapshot and a `.stale` quarantine, decodes as `utf-8-sig` (`CFG_ENCODING`, `es_cfg_guard.py:66`), and writes through temp-file + rename. The temp name is fully predictable, so it is created `O_CREAT|O_EXCL` (`es_cfg_guard.py:197-205`, mode 0600 with the live file's own mode carried over at `es_cfg_guard.py:228`) and a name already on disk is skipped rather than followed: a pre-planted symlink at the temp path cannot redirect the write, and a squatter holding every name in the attempt range makes the write fail loudly rather than pick a name someone else owns. Recovers an interrupted swap only when the live file diverges solely in the managed keys, so a restore cannot revert unrelated operator edits. The backup is named for its owning run's pid, so two harnesses on one install never share a snapshot: one run's `begin()` cannot replay another's snapshot over the live config, and one run's `restore()` cannot land on the other's in-flight config. `recover()` resolves only backups no live pid owns (a dead pid's, or an unparseable name from a pre-pid-scoping build) and leaves a running run's backup, and any `.stale` evidence, alone. Imported by the E8 harnesses. Self-test, including a fuzz of the restore protocol over hostile config bytes, is wired into `make test` (`Makefile:227`) |
| E11 | Scratch staging-directory lifecycle | `scripts/stage_tmp.sh` (`stage_sweep:33`, `stage_new:45`), sourced by `scripts/package.sh:73` and `scripts/verify_reproducible.sh:59` | `mktemp -d` under `$TMPDIR` with the creating pid in the name; the sweep `rm -rf`s only same-prefix directories under `$TMPDIR` whose owning pid is not running. A recycled pid makes a dead stage survive a sweep. Not executed, only sourced |
| E12 | Server log and console text parsed by the bench harnesses | `scripts/bench_parse.py` (`read_apm:208`, `parse_animstate:352`), used by `scripts/measure_es_onoff.py` and `scripts/validate_anim_path_admission.py` | The Unity server log and the `es animstate` console dump are written by the game and the APM bridge, not by this repo, and the log is append-only and unbounded, so both are hostile input: any byte, any line length, any numeric spelling can reach the parsers. A field this repo misreads becomes an A/B verdict (`ON_faster` from print rounding, a `dp=0` crawl call), and a field it cannot read must not raise, or the comparison is lost. Every numeric field is width-bounded in the pattern (a bare `[0-9.]+` accepts `.` and `1.2.3`, which `float()` rejects, and unbounded `\\d+` accepts counts past the Python 3.11 `int()` conversion limit), reads are capped per poll and the partial-line carry is capped, so a huge or newline-free log cannot grow the harness without bound. Fuzzed in `make test` (`Makefile:228`) |

## Trust boundaries

| ID | Boundary | Crossing data |
|---|---|---|
| B1 | Host filesystem to mod process (E2) | `Config/efficientserver.json` next to the assembly. Written by the operator; writable by anything that can write that directory. Trusted more than a network input would be, less than compiled-in constants. It is also the carrier for the B2 arm opt-ins, so it is not merely a tuning file (R3) |
| B2 | Console-equivalent actor to mod commands (E3) | Whoever passes the game's telnet password (or connects from loopback with no password set) or holds console permission in game. This mod adds no second gate beyond the two arm opt-ins below; everything else is console-access only |
| B3 | Mod to game host process (E1, E3, E4) | No boundary in a memory-safety sense: the mod shares the process, and patches rewrite game method behavior (prefix/transpiler/finalizer) |
| B4 | Build and publish to installed server (E5) | `dist/EfficientServer/` copied verbatim into the game's `Mods/` tree; zips additionally published via `dist/*.zip`; the fetched build dependency graph enters here (R7) |
| B5 | Host environment to server process (E5) | Env vars consumed at process init: `MALLOC_ARENA_MAX` (`run_server.sh:123`), `GC_FREE_SPACE_DIVISOR`, `GC_NPROCS`, `MONO_ENV_OPTIONS` (`run_server.sh:138`), optional heap/affinity vars; plus `LD_LIBRARY_PATH` prepended with the server dir (`run_server.sh:121`) and the GC-incremental pair gated on `SEVENDTD_GC_INCREMENTAL` (`run_server.sh:143`) |
| B6 | CI runner to repository (E6) | GitHub Actions with `permissions: contents: read` in both workflows, token not persisted into the runner workspace (`ci.yml:32`, `release.yml`) |
| B7 | Network to game console | Telnet (port 8082 in the shipped template), the game UDP port, and the web dashboard: all game-owned, configured through `serverconfig*.xml`. This repo ships one template and inherits every exposure it sets |
| B8 | Operator tooling to the live config and to the off-host destination (E9, E10, E11) | Scripts that write `Mods/EfficientServer/Config/efficientserver.json` in place, and that copy it to a destination outside the install tree, and that `rm -rf` scratch trees under `$TMPDIR` |
| B9 | Server log and console text to the bench harness (E8, E12) | The Unity server log the loadgen dedicated server writes and the `es animstate` console dump it prints, read by the offline harnesses; the log path is operator-selected (`RE_DEDICATED_USERDATA`) and its contents are game-written |

## Assets

| ID | Asset | Concrete blast radius |
|---|---|---|
| A1 | Server availability and tick latency | The mod's own levers (GC mode, entity shedding, animator culling) can freeze or degrade the tick; a bad patch crashes the process for all players |
| A2 | Game world and save integrity | `TickGuardPatch` despawns enemies (`Patches/TickGuardPatch.cs:104`); `AnimatorEmergency` degrades combat timing (`Patches/GovernorPatch.cs:132`); both alter live world state |
| A3 | Fair-play integrity | Server runs EAC-off by necessity of loading a C# mod (`Source/EfficientServer/ModInfo.xml:9`); `benchgod` is aimbot-grade damage immunity for every player while active (`Patches/BenchGodPatch.cs:32`) |
| A4 | Host RAM and CPU | GC env vars trade RAM for pause length. The heap-growing megapause diagnostic that could pin memory is gone (the `Breaking` entry in `CHANGELOG.md`) |
| A5 | Trust in the packaged DLL | Anything installed from `dist/` executes with full server authority; tampered artifacts are indistinguishable from releases unless a deployer manually compares the recorded SHA-256 |
| A6 | Game-owned data in-process | Save games, `serveradmin.xml`, session tokens held by the game are all reachable from mod code because there is no isolation. Not read or written by current mod code, but within blast radius of any code-execution event |
| A7 | Operator tuning, as a data asset | The installed config is the only copy of the operator's tuning: it is edited on the server host and nothing regenerates it. It is what A3 and A5 are steered by, so its loss is an availability and integrity event, not a convenience one. Named explicitly because E9 and E10 exist to preserve it |

## Threats per boundary

### B1: config file to mod

- Tampering: extreme values reshape gameplay or load. Mitigated: every knob
  passes `Normalize` range clamps with logged corrections
  (`Source/EfficientServer/Config.cs:566`, clamp helper `Config.cs:644`),
  a misspelled key binds to nothing and keeps the built-in default (fail-soft per
  group, `Config.cs:399-402`, with template typos caught pre-packaging by
  `scripts/check_config_doc.py`, and each unknown key named in a WARN so a typo
  is not silent, `Config.cs:402`), NaN/Infinity take a clamped `FiniteRange`
  fallback, and malformed JSON falls back to defaults with an ERROR
  (`Config.cs:427`). A JSON `null` document is treated as a load failure, not a
  silent full reset (`Config.cs:411`), and a JSON `null` for a whole section is
  backfilled from defaults by reflection so no null hole reaches a patch
  (`Config.cs:416`). Residual: clamped maxima are still potent (R5), and the
  file is unsigned, so clamps bound the damage, not the write (R3).
- Repudiation of a rejected config: a file that fails to parse must not quietly
  become built-in defaults mid-session, which would make every knob look
  operator-chosen when no operator chose it. Mitigated:
  `ServerPerfConfig.LastLoadFailed` (`Config.cs:382`) is checked by the reload
  path, which keeps the previous config and logs ERROR with the path
  (`ModApi.cs:144-152`) and by the console, which prints a refusal rather than a
  success echo (`ConsoleCmdEfficientServer.cs:52`). The initial load at startup
  has no previous config to keep, so a bad file at boot still means defaults;
  that outcome is logged at ERROR (`Config.cs:429`).
- Denial of service: config levers can degrade gameplay under load
  (`AnimatorEmergency` engages itself past `EmergencyOverMs`;
  `TickGuard` despawns living enemies). Both emit a WARNING when they engage
  (`GovernorPatch.cs:144`, `TickGuardPatch.cs:142`), and a shed that was
  suppressed instead of performed logs its reason rather than staying silent
  (`TickGuardPatch.cs:151-153`), so the event is reconstructable after the
  fact. Partial mitigation added since the last review: the interval average
  and window counters behind both levers are re-based on every world change
  (`TickGuardPatch.cs:43-47`, `GovernorPatch.cs:172-174`) and dropped while
  the gate is closed (`TickGuardPatch.cs:71-74`, `GovernorPatch.cs:93-95`), so
  a config reload that re-enables a lever cannot complete a shed window on the
  new world's spawn load. Residual: an operator who sets both levers at their
  clamped maxima still has an armed despawn path while the tick is over budget.
- Repudiation: corrections and parse failures are logged by severity through
  `EsLog.Emit` (`EsLog.cs:34-47`); ERROR is reserved for the outcomes that leave
  the server on knobs nobody chose. Reload apply failures are surfaced rather
  than swallowed, so no success echo covers a partial apply
  (`ModApi.cs:200`).

### B2: console actor to mod commands

- Elevation of privilege / abuse: the two arms that change gameplay for every
  player are refused unless the operator opted in through the config:
  `es benchgod on` requires `Diagnostics.AllowBenchGod`
  (`ConsoleCmdEfficientServer.cs:392`, gate `Config.cs:699`) and `es animoff` /
  `es rigoff` require `Diagnostics.AllowFidelityProbes`
  (`ConsoleCmdEfficientServer.cs:204`, gate `Config.cs:708`). Both gates fail
  closed on a null config or null section. Disarming is never gated. Residual
  risk R4: with the opt-in on, arming is still one console command, with no
  confirmation and no scope. What the last pass left open has since narrowed:
  the damage-immunity prefix re-reads the allow-switch every time it is
  consulted, so the immunity stops as soon as the config revokes it even
  without a reload (`Patches/BenchGodPatch.cs:32-33`), and a reload without the
  probe opt-in releases armed animator and rig probes and logs what it released
  (`ModApi.cs:151-157`, `ConsoleCmdEfficientServer.cs:359`).
- Denial of service (minor): `es animstate` emits one console/log-sink line per
  living enemy; at horde scale this floods telnet output. Bounded by entity
  count, self-limited to manual invocation, and written straight to the console
  rather than the persisted log because it is read-only output
  (`ConsoleCmdEfficientServer.cs:226,238`).
- Repudiation: state-changing commands (`animprobe`, `rigprobe`, `benchgod`
  toggles, refusals, and the reload outcome) persist through the `Output` choke
  point to console AND server log (`ConsoleCmdEfficientServer.cs:114-120`);
  command execution is additionally governed by the game setting
  `HideCommandExecutionLog`, kept at 0 = everything logged in the shipped
  template (`serverconfig.optimized.xml:49`). A bare `es benchgod` peek is
  read-only and intentionally unlogged.

### B3: mod to host process

- Elevation of privilege: inherent and accepted; the mod IS privileged code.
  Controls reduce likelihood, not impact: per-group fail-soft patching so one
  bad target does not kill the rest (`PatchAllSafe`, `ModApi.cs:267`), visible
  MISSING TARGET detection on version drift (`ModApi.cs:113`), fail-closed
  dedicated gating (`ShouldRunFor`, `Config.cs:681`; the runtime probe fails
  closed, `ModApi.cs:333`) so server-only behavior (including BenchGod) cannot
  activate on an unknown/client host.
- Single point of failure: `ModApi.ShouldRun()` gates every behavioral patch,
  including damage immunity (`Patches/BenchGodPatch.cs:32`). If it ever returned
  true where it must not, several high-impact threats activate at once. Treat
  any future edit to it, and any edit to the two arm gates, as
  security-critical.
- Tampering with game logic: transpiler patches rewrite game IL at runtime
  (e.g. `LayerGridGraph.ScanInternal` node allocation,
  `Patches/InitScanPoolPatch.cs:43`). Fail-visibly-by-design on IL drift
  (`InitScanPoolPatch.cs:66`); re-verify after every game update.
- One-shot native flip: the GC mode flip cannot be undone, so it is guarded
  against repeat and mixed application (`GcIncremental.cs:27`) and the
  pause-target call has its own try, marked applied only once the flip actually
  landed, so a missing entry point stays retryable rather than leaving the
  collector in a half-configured state (`GcIncremental.cs:31-40`).

### B4: build/publish to installed server

- Tampering: no signature, checksum comparison, or provenance check anywhere on
  the install path. `install.sh` backs up the installed `Config/` (a differing
  user config plus the guard backup files) via a trap that survives failure
  (`install.sh:126-149`), then does `rm -rf` of the destination and copies from
  `dist/` (`install.sh:150,152`). An exported-but-empty `SEVENDTD_DS_DIR` or
  `DS` is rejected before the build and before the wipe (`install.sh:52-57`),
  and an install dir that trims to empty or to `/` is rejected too
  (`install.sh:66-71`), which is what keeps the wipe off a filesystem-root
  target when the value is a stray space or slash rather than a real path.
  The same three rewrites are serialized by an `flock` lock
  (`scripts/repo_lock.sh`): `install.sh` and `uninstall.sh` over the installed
  tree, `build.sh` over `dist/EfficientServer` and the intermediate
  `obj`/`bin`, so a second run cannot delete a tree the first is still writing.
  `package.sh` produces a reproducible, byte-identical zip
  (`scripts/package.sh:7-15,161`); `scripts/verify_reproducible.sh` proves
  rebuildability. Named gap remains: install verifies none of this, and nothing
  signs anything.
- Dependency substitution (R7): restore sources are pinned in-repo
  (`NuGet.config`: nuget.org only, inherited feeds cleared) and the test
  project's graph is exact-pinned and hash-pinned in a committed lock file,
  restored `--locked-mode` (`Makefile:205-206`, `Source/EfficientServer.Tests/packages.lock.json`).
  The mod project adds two fetched packages,
  `Microsoft.NETFramework.ReferenceAssemblies` and its `.net48` leaf, each at
  exact range `[1.0.3]`
  (`Source/EfficientServer/EfficientServer.csproj`), each recorded with a
  `contentHash` in the committed `Source/EfficientServer/packages.lock.json`
  and restored `--locked-mode` by both `make build` (`scripts/build.sh`) and
  `make test`. The packages provide reference metadata
  only (`PrivateAssets="all"`), so they cannot alter the emitted IL.
- Spoofing (supply chain): CI actions are commit-pinned with a stated reason
  (`.github/workflows/ci.yml:27,36`, `.github/workflows/release.yml`), the
  release job runs on a `v*` tag with a read-only token and a 5-minute timeout,
  and deliberately does not build the archive because a hosted runner has no
  dedicated-server assemblies. Push trigger scoped to main
  (`.github/workflows/ci.yml:5-7`). Test dependency surface is
  Newtonsoft.Json only, exact-pinned (`[13.0.4]`) and hash-pinned; the
  `dotnet-coverage` local tool is version-pinned (`.config/dotnet-tools.json`)
  but not hash-pinned, so `make coverage` fetches an unhashed tool graph.
  The lint toolchain is a third unhashed fetch surface: the optimizer job
  installs ruff and mypy from PyPI at the exact versions in the Makefile
  (`RUFF_VERSION`, `MYPY_VERSION`, read back by
  `.github/workflows/ci.yml`), but `uv tool install` resolves their transitive
  dependencies at run time and no requirements file with hashes is committed,
  and Dependabot has no ecosystem entry for it. They gate the shipped source
  only, and `make test` refuses any other version than the pinned one, so a
  substituted build fails the gate instead of passing it. Dependabot watches
  the NuGet and action surfaces (`.github/dependabot.yml`).
- Stale install: `run_server.sh` never builds or installs. It execs whatever
  `Mods/EfficientServer` the server tree already holds (`scripts/install.sh`
  is the only writer), so a launch can run a DLL that no longer matches the
  repository source. Re-run `make install` after every change.

### B5: host environment to server process

- Tampering (local): env vars materially change GC and JIT behavior
  (`scripts/run_server.sh:123-143`); `LD_LIBRARY_PATH` is prepended with the
  server dir (`run_server.sh:121`) so libraries there shadow system ones.
  `SEVENDTD_GC_INCREMENTAL` is the only var that turns a lever on by itself
  (`run_server.sh:143`). Local-operator trust domain; acceptable, listed so the
  surface is named.

### B6: CI runner to repository

- Low. Read-only token that is not persisted into the runner workspace
  (`ci.yml:32`; the gate runs no git commands), 15-minute timeout
  (`ci.yml:23`), concurrency cancellation (`ci.yml:12-14`), NuGet cache keyed on
  the locked test dependency graph (`ci.yml:36`). The release workflow is
  read-only and shells out to two files it just checked out, so a tag cannot
  publish an archive whose version disagrees with `ModInfo.xml`. No secrets are
  used or needed in either workflow.

### B7: network to game console

- Spoofing and elevation of privilege: telnet with an empty password relies on
  the game's loopback-only fallback plus `TelnetFailedLoginLimit=10` and a
  10-second block (`serverconfig.optimized.xml:33-37`). An operator who sets a
  weak password or forwards 8082 hands an unauthenticated remote actor the
  console, and with it every `es` subcommand including any arm the config has
  pre-authorized (R4 + R6). This repo's contribution is the template default and
  the documentation, not the code.

### B8: operator tooling to the live config and off-host destination

- Tampering (destructive write): E10 rewrites live config keys in place. Two
  properties keep a killed or unrelated run from destroying tuning: writes go
  through temp-file + rename so no truncated JSON is left for the game's
  reader, and `restore()` writes only the managed keys from its snapshot back,
  so a later run cannot revert unrelated operator edits
  (`scripts/es_cfg_guard.py:1-30`). Exception, stated in the same docstring: if
  the live file is missing, unreadable, or not a JSON object, the full snapshot
  is restored instead, which does overwrite everything.
- Tampering (redirected write): the temp name is derived from the live file's
  name, this process's pid, and a counter, and it lands in the live install
  directory, so anything else on the host can create that name first. Without a
  create-exclusive open, `os.replace` onto a pre-planted symlink would move the
  written config onto whatever the link points at, with this process's
  privileges. Mitigated: the temp is created `O_CREAT|O_EXCL` and a name that
  already exists is skipped rather than followed, and exhausting
  `TEMP_ATTEMPTS` names raises instead of choosing one someone else owns
  (`scripts/es_cfg_guard.py:104-114`). Residual: the temp still inherits the
  live file's owner and mode, so a write into a directory this process can
  write is still reachable by that directory's other members; that is the
  same trust position as R3, not a new one. The temp is also created 0600 and
  then chmod-ed to the live file's mode (`es_cfg_guard.py:122-125`), so a
  tightened operator mode is not silently widened by the swap.
- Tampering (destructive delete): `ConfigSwap._sweep_abandoned_temps`
  (`es_cfg_guard.py:233`) unlinks `<config>.tmp<pid>` files beside the live
  config when the owning pid is gone, and `stage_tmp.sh:33` `rm -rf`s dead
  staging trees under `$TMPDIR`. Both are pattern-scoped to files this tooling
  itself creates, both refuse to touch a pid that is still alive, and both treat
  a recycled pid as alive (leaving garbage rather than deleting a live write).
  `$TMPDIR` is operator-set; a hostile value makes the sweep operate in a
  directory of the setter's choosing, which is already the setter's own
  privilege.
- Tampering (restore): `backup_config.py --restore` copies a verified snapshot
  over a target path and refuses to clobber an existing file without `--force`
  (`backup_config.py:593-649`, refusal at `backup_config.py:644-646`); the snapshot is re-verified in the same call, so
  a restore never copies a snapshot that would not load.
- Information disclosure: the destination is operator-named and may be a synced
  folder or another host. The config holds tuning, not secrets, so a leaked copy
  is a gameplay-integrity event rather than a credential event; the tool refuses
  a destination inside the install tree so the copy is not lost with the disk it
  protects (`backup_config.py:308-324`).
- Unbounded growth: snapshots are pruned to `DEFAULT_KEEP = 14`
  (`backup_config.py:76`), and `make backup-config` requires
  `ES_CONFIG_BACKUP_DEST` to be set rather than defaulting to a path inside the
  install (`Makefile:270-275`).

## Abuse cases (scenarios, not demonstrations)

1. Config write escalates to gameplay control: an actor with write access to
   `Mods/EfficientServer/Config/efficientserver.json` (a shared host, a backup
   restore, a careless deploy) sets `Diagnostics.AllowBenchGod=true` and the
   desired clamps, and any later `es reload` (`ModApi.cs:143`) makes the policy
   live. No console access is needed for the policy itself, only for the arm.
   Config write access is therefore the higher-privilege position (R3), and a
   script holding it (E9, E10) is in the same class.
2. Bench mode left hot: an operator enables the opt-in for a bench session and
   runs `es benchgod on`, then the process is killed before `es benchgod off`.
   Every player is immune to zombie damage, because the flag is process state,
   not config state (`ConsoleCmdEfficientServer.cs:361`; consulted at
   `Patches/BenchGodPatch.cs:17,32`). The two documented escapes are both config
   moves: revoking `Diagnostics.AllowBenchGod` stops the immunity on the next
   damage event even without a reload, and a reload clears the latch outright
   (`ModApi.cs:161`). The refusal path and the toggle are both audited to the
   log. What remains open is the missing scope and confirmation at arming time.
3. Telnet inheritance: the shipped template enables telnet with an empty
   password, relying on the game's loopback-only fallback
   (`serverconfig.optimized.xml:33-35`). An operator who sets a weak password or
   forwards port 8082 inherits full console access, and with it every `es`
   subcommand, including any arm the config has pre-authorized (R4 + R6). This
   repo's contribution to the fix is documentation accuracy, not code.
4. Reload-window drift: an operator edits the config between init and a later
   `es reload`; because the file has no watcher and `reload` re-reads from disk,
   whatever sits in the file at that moment becomes live policy, including
   levers that were off at boot (`ModApi.ReloadConfig`, `ModApi.cs:140-143`; late
   enable of skips/GC incremental is supported behavior). Anyone with write
   access to the config directory controls policy without console access,
   subject to the same clamps and the arm gates. A file that fails to parse is
   rejected instead: the previous config stays live and the refusal is logged
   (`ModApi.cs:144-152`), so the only way to land a bad file is a file that
   parses.
5. Artifact swap before install: an actor who can write `dist/EfficientServer/`
   between `make build` and `make install` supplies a DLL that runs with full
   server authority, and nothing on the install path notices (R2). Recovery is
   manual hash comparison only.
6. Snapshot tampered with before restore: the backup destination is a plain
   directory, so an actor who can write it can replace a snapshot or its
   manifest. `verify` re-reads every snapshot and re-checks the recorded sha256
   before `restore` copies one (`backup_config.py:491`, `593-649`), so a
   tampered snapshot fails verification and is not restored. What verification
   does not provide is authenticity: a tampered pair that is internally
   consistent (recomputed manifest) is indistinguishable from a real one, and
   the manifest is unsigned. The restored file is still bounded by the same
   clamp and gate rules once the server loads it.

## Mitigations inventory (what exists)

| Control | Covers | Location |
|---|---|---|
| Config-opt-in gate on arming global damage immunity | B2 elevation of privilege, R4 | `Source/EfficientServer/ConsoleCmdEfficientServer.cs:392`, `Config.cs:699` (`BenchGodArmAllowed`) |
| Allow-switch re-read on every damage event, so revoking it stops the immunity without a reload | B2 residual R4 after a killed bench run | `Patches/BenchGodPatch.cs:32-33` |
| Reload releases an armed damage-immunity latch and armed animator/rig probes the new config no longer allows | B2 residual R4, B1 repudiation | `ModApi.cs:151-157`, `ConsoleCmdEfficientServer.cs:359` (`ReleaseArmedProbes`) |
| Config-opt-in gate on arming fidelity probes (`animoff`, `rigoff`) | B2 elevation of privilege, R4 | `ConsoleCmdEfficientServer.cs:204`, `Config.cs:708` (`FidelityProbeArmAllowed`) |
| Disarm paths never gated, refusals audited | B2 availability, repudiation | `ConsoleCmdEfficientServer.cs:392-403` |
| Range-clamp normalization of every numeric knob, with logged corrections, incl. non-finite rejection | B1 tampering extremes, config self-DoS upper bounds | `Source/EfficientServer/Config.cs:566`, clamps `Config.cs:644` |
| Reflection backfill of JSON-null sections from defaults; JSON `null` document treated as a load failure | B1 null-hole reaching a patch; B1 silent full reset | `Config.cs:411,416` |
| Parse-failure fallback to defaults, logged at ERROR | B1 malformed input | `Config.cs:427-429` |
| A rejected reload keeps the previous config instead of reverting tuning to defaults | B1 repudiation of a bad edit | `ModApi.cs:144-152`, `ConsoleCmdEfficientServer.cs:52`, `Config.cs:382` (`LastLoadFailed`) |
| Config read pinned to UTF-8 (BOM tolerated) | B1 encoding-dependent misparse across hosts | `Config.cs:394` |
| Shipped template typos caught pre-packaging | B1 silent misconfiguration | `scripts/check_config_doc.py` (run by `Makefile:223`) |
| Structure-aware value fuzz + byte-level file fuzz (invalid UTF-8, NUL, truncation, runaway nesting) and a write-then-read round trip of the loaded config | B1 parser robustness regressions, reload drift | `Source/EfficientServer.Tests/Fuzz.cs:43,143`, run by `make unit` |
| Per-group fail-soft Harmony application | B3 partial breakage on version drift | `ModApi.cs:267` (`PatchAllSafe`) |
| Visible MISSING TARGET init summary | B3 silent target drift | `ModApi.cs:113` |
| Fail-closed `DedicatedOnly` gate (pure, unit-tested) | B3 activation on wrong host type | `Config.cs:681`, runtime probe `ModApi.cs:333` |
| One-shot guard, applied-only-on-success, and separate try on the irreversible native flip | B3 repeated/mixed GC modes | `Source/EfficientServer/GcIncremental.cs:27,31-40` |
| Reload apply failures surfaced and rethrown, success echo suppressed | B1/B2 false "reloaded OK" over partial apply | `ModApi.cs:194-203` |
| State-changing console commands and refusals echoed to console and log | B2 repudiation | `ConsoleCmdEfficientServer.cs:114-120` |
| Severity-split logging channels (INFO/WARN/ERROR), ERROR reserved for outcomes that leave the server on unchosen knobs | triage of config corrections vs failures | `EsLog.cs:18,26` |
| Emergency levers and every shed, shed suppression, or refusal log as WARNING | B1/B3 unnoticed combat degradation or entity sheds | `Patches/GovernorPatch.cs:144`, `Patches/TickGuardPatch.cs:142,153` |
| Tick and governor measurements re-based on every world change and dropped while their gate is closed, so a reload cannot fire a shed or an emergency engage on a stale average | B1 reload-induced entity shed on the new world (R5) | `Patches/TickGuardPatch.cs:43-47,71-74`, `Patches/GovernorPatch.cs:93-95,172-174`, `Patches/GameStartPatch.cs:38-39` |
| Install dir guard: exported-but-empty `SEVENDTD_DS_DIR` or `DS` rejected before build and wipe | B4 `rm -rf` on an unintended install | `scripts/install.sh:52-57`, same guard in `uninstall.sh` and `run_server.sh` |
| Failed install preserves the operator's config via EXIT trap | B4 silent config loss (A7) | `scripts/install.sh:126-149` |
| Install dir that trims to empty or `/` rejected before the wipe | B4 `rm -rf` on a filesystem-root target | `scripts/install.sh:66-71` (same guard in `uninstall.sh`) |
| Uninstall preserves `Config/` and warns when the copy lands inside the install tree | A7 loss, B4 | `scripts/uninstall.sh:121-131` |
| Build, install and uninstall of the same tree serialize on an `flock` lock, so a second run cannot `rm -rf` a tree the first is still writing | B4 corrupted artifact or half-installed mod | `scripts/repo_lock.sh`; taken in `build.sh`, `install.sh`, `uninstall.sh` |
| Config backup tool refuses a destination inside the install tree; `make backup-config` requires an explicit dest | A7, B8 | `scripts/backup_config.py:308-324`, `Makefile:270-275` |
| Backup `verify` re-reads every snapshot the way a restore would; `restore` re-verifies its own snapshot and refuses to clobber without `--force` | A7 corruption, B8 destructive restore | `scripts/backup_config.py:491,593-649` |
| Snapshot retention bound | B8 unbounded growth | `scripts/backup_config.py:76,442` |
| Live-config swap protocol: temp-file + rename writes, managed-keys-only restore, stale-backup quarantine, divergent-file rule | B8 destructive write, B1 partial revert | `scripts/es_cfg_guard.py:1-30,139` |
| Stranded-temp sweep scoped to this tooling's own names, live pids and recycled pids left alone | B8 destructive delete | `scripts/es_cfg_guard.py:233,240-245`, `scripts/stage_tmp.sh:33` |
| Atomic config write creates its temp `O_CREAT\|O_EXCL` and gives up rather than reuse a taken name; live file's mode carried onto the temp | B8 pre-planted symlink or squatter redirecting the live-config write | `scripts/es_cfg_guard.py:104-114,122-125` |
| Bench log parser: every numeric field width-bounded in the pattern, so a line it cannot read is skipped whole rather than raised or half-applied | B9 malformed field becomes a wrong A/B verdict or kills the run | `scripts/bench_parse.py:70-84` (field widths), fuzzed at `Makefile:228` |
| Incremental log read judged against a full-file rescan after every append; offset, carry buffer and remembered value may never disagree with the file | B9 lost line, invented line, unbounded carry | `scripts/bench_parse.py:633` (`_rescan`), `:655` (`_check_apm`); carry capped at `:86`, one poll reads at most `:93` |
| Commit-pinned CI and release actions, unpersisted read-only token, main-scoped push, tag/manifest version agreement | B6 supply chain | `.github/workflows/ci.yml:27,36`, `.github/workflows/release.yml` |
| Locked-mode restore from an in-repo source list for the test graph, SDK pin | B4/B6 dependency drift | `NuGet.config`, `Makefile:205-206`, `global.json` |
| Reproducible package build (sorted entries, epoch mtimes, rebuilt from scratch) | B4 artifact diffing | `scripts/package.sh:7-15,161` |
| Reproducibility proof target | B4 rebuild-equals-release claim | `Makefile:126`, `scripts/verify_reproducible.sh` |
| Command-execution logging kept on (game setting) | B2 repudiation | `serverconfig.optimized.xml:49` (template default) |

## Claimed-but-unverified and named gaps

Checked the docs against the code; results:

- **Corrected on this pass.** The previous revision of this model, `SECURITY.md`
  and the `NuGet.config` comment all stated that no
  `Source/EfficientServer/packages.lock.json` is committed and that the mod
  project's fetched package is version-pinned only. That stopped being true:
  the lock file is in the tree (`111ed95`) and carries a `contentHash` for both
  reference-assembly packages, which are now declared in the csproj. The docs
  now say what the file contains, and a later pass added the enforcement half:
  `make build` and `make test` now restore the mod project `--locked-mode`, so
  the recorded hash is checked rather than a drifting lock file being rewritten
  in place. R7 records what is left.
- **Corrected on this pass.** The previous revision dated the model at `d72362a`
  and predated the last four commits, which moved the install wipe
  (`install.sh:150,152`), rewrote the atomic config write, and re-based the
  tick and governor measurements. Line references for the install path, E10,
  and B8 were re-read against `111ed95` and the new controls are recorded.
- **Corrected on this pass.** The previous revision of this model described
  E7 as tooling that "reads the working tree and exits nonzero; none writes
  outside the repo". `scripts/es_cfg_guard.py` rewrites the live installed
  config in place and unlinks files beside it, and `scripts/backup_config.py`
  writes off-host and can restore over the live path. Both are now named entry
  points (E9, E10) with boundary B8 and the risks they carry (R8).
- **Corrected on this pass.** `SECURITY.md` said that with the bench opt-in on,
  nothing in code further scopes or confirms the arm on a live server. That is
  still true of arming, but the arm is no longer purely open-ended: the
  allow-switch is re-read per damage event and a reload releases armed probes
  (`Patches/BenchGodPatch.cs:32-33`, `ModApi.cs:160-168`). The bullet now states
  both halves.
- EAC statements match reality: `ModInfo.xml` sets `SkipWithAntiCheat=true`
  (`Source/EfficientServer/ModInfo.xml:9`) and the docs correctly explain the
  mod therefore does not load under enforcing EAC. No remaining claim of
  authentication, rate limiting, sandboxing, or input validation that the code
  lacks.
- **Corrected on this pass.** The previous revision of this model and
  `SECURITY.md` both claimed that nothing in code refuses the bench-only toggles
  on a live server, and listed the missing guard as a top-3 risk. The code
  gates them: `ServerPerfConfig.BenchGodArmAllowed` and
  `FidelityProbeArmAllowed` (`Config.cs:699,708`) are enforced at the arm sites
  (`ConsoleCmdEfficientServer.cs:392,204`) and the refusal goes to the audited
  output path. R4 and the `SECURITY.md` bullet were rewritten to the residual
  risk (no scope, no confirmation, opt-in pre-authorizes) rather than the
  already-fixed whole.
- Claims carried forward as claims (plausible, not proven by this model):
  "provably equivalent" single-target send short-circuit
  (`Config.cs` network knobs, code `Patches/FastSendPatch.cs:44`); "changes no
  wire bytes" GC incremental (`GcIncremental.cs:12`); "EAC-safe" env vars
  (`scripts/run_server.sh`). Each is falsifiable by sec-review against the named
  code.
- Gaps (ranked): R2 install path verifies nothing (candidate fix: compare a
  recorded artifact hash in `install.sh` before copying); R3 the config file is unsigned and pre-authorizes
  the R4 arms; R4 no scope or confirmation at arming time; no signing or release
  provenance process documented anywhere; the backup manifest is unsigned, so a
  self-consistent tampered snapshot verifies (abuse case 6).

## Response readiness (notes only)

- Audit trail available for investigation: everything logs through
  `[EfficientServer]`-prefixed lines into the game log (`EsLog.Emit`,
  `EsLog.cs:21,34-47`) across three severity channels, including config
  corrections, parse failures, rejected reloads, patch failures, MISSING TARGET
  summaries, engaged emergency levers, entity sheds, released probes, and (via
  the console `Output` choke point) every state-changing console command and
  every arm refusal; executed commands generally also appear via the game's own
  execution log while `HideCommandExecutionLog=0`. No separate security event
  stream exists; o11y-review owns log structure. The config backup tool's
  evidence is its own manifest: timestamp, size, sha256 per snapshot, plus the
  verification result when `--verify` is run.
- Vulnerability-reported-to-fix-shipped path: `SECURITY.md` names the reporting
  channel (GitHub issues, public until a private channel exists), the
  authoritative version source (`Source/EfficientServer/ModInfo.xml`, currently
  1.19.0), and the supported-version policy (current release only, no
  backports). A `v*` tag is rejected at CI time unless it matches that manifest
  and the changelog (`.github/workflows/release.yml`). No private disclosure
  contact is published yet; that remains an organizational gap, noted rather
  than invented.
