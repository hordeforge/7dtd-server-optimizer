# Threat model

Systemic view of what this repository exposes to attack: entry points, trust
boundaries, assets, threats, and the controls that exist versus the ones that are
missing. Individual vulnerability findings belong to sec-review; they land here
as threats with locations. Re-verify every file reference after each game update
(Harmony targets break silently, see AGENTS.md critical rule 3).

Last reviewed: 2026-09-28, against git 5913318.
Owner: repository maintainers. Review trigger: every game update, every new
patch group, every change to `scripts/install.sh` / `uninstall.sh` /
`run_server.sh`, and every change to the console arm gates named under B2.

## Risk-ranked summary

| # | Risk | Boundary | Why |
|---|---|---|---|
| R1 | Full-host-authority code runs inside the game server process | Mod to host | By design: a Harmony mod is arbitrary code with the server's privileges. No isolation exists or is possible while it stays a C# mod. Every bug is a server crash or worse, not a sandbox escape |
| R2 | Unverified build artifact installed over the game tree | Build to runtime | `install.sh` does `rm -rf` of the destination and copies `dist/EfficientServer/` into `Mods/` with no hash or signature check (`scripts/install.sh:54,56`). Nothing on the install path compares the artifact to a trusted build. Whoever controls `dist/` or the Mods directory controls the server process |
| R3 | Config-file write access silently pre-authorizes every lever, including the diagnostic arms | Filesystem to mod | `Config/efficientserver.json` is read with no signature and no watcher. Anyone who can write that file controls policy: `es reload` re-reads it (`Source/EfficientServer/ModApi.cs:116`). The gates that guard `es benchgod on` and `es animoff`/`es rigoff` are themselves config flags (`Diagnostics.AllowBenchGod`, `Diagnostics.AllowFidelityProbes`, `Source/EfficientServer/Config.cs:534,541`), so a config write both sets the limits and lifts the guard. `es` is a consequence, not a prerequisite |
| R4 | Console actor can degrade or disable live gameplay with one command | Console to mod | The bench-god and fidelity-probe arms are refused unless the operator opted in (B2, `ConsoleCmdEfficientServer.cs:324,184`). Where the opt-in is on, any console-level actor gets global damage immunity for every player (`Patches/BenchGodPatch.cs:23`) or enemy animation and rig disable, unconfirmed, unscoped, and unpersisted across restart. Disarming is never gated, so a refusal is always reversible |
| R5 | Config-file self-denial-of-service paths | Filesystem to mod | Clamped maxima are still potent: `TickGuard` despawns enemies past a tick EMA (`Patches/TickGuardPatch.cs:97`) and `Governor.AnimatorEmergency` engages itself past `EmergencyOverMs` (`Patches/GovernorPatch.cs:129,132`); both default off, both config-enableable. The heap-growing GC megapause probe that used to sit here was removed after tag `v1.19.0` and is unreleased (`CHANGELOG.md:29`) |
| R6 | Inherited telnet exposure | Network to console | Both shipped serverconfig copies enable telnet on port 8082 with an empty password (`serverconfig.optimized.xml:33-35`); safety depends entirely on the game's loopback fallback and failed-login limit, not on this repo |

Not risks here: the mod opens no sockets, spawns no processes, stores no
credentials, handles no player data beyond what the game already holds, and
never writes outside its mod folder and the log (verified: no socket/process/
write APIs under `Source/EfficientServer/`; the only file reads are
`Source/EfficientServer/Config.cs:373`).

## Entry points

| ID | Entry point | Location | Notes |
|---|---|---|---|
| E1 | Mod load into game process (`InitMod`) | `Source/EfficientServer/ModApi.cs:38` | Game loads the DLL at startup and calls `InitMod`; Harmony patches install here per group. Post-start setup registers on the sanctioned `GameStartDone` hook, not a patched game method (`ModApi.cs:103`) |
| E2 | Config JSON file read | `Source/EfficientServer/Config.cs:364` (`Load`), path resolution `Config.cs:503` | Read once at init and again on every `es reload` (`ModApi.ReloadConfig`, `ModApi.cs:116`). Parsed with Newtonsoft.Json (game-bundled); read pinned to UTF-8 (`Config.cs:373`). No file watcher, no signature, no ownership check; disk changes apply only via E3 |
| E3 | Operator console command `es` / `efficientserver` | `Source/EfficientServer/ConsoleCmdEfficientServer.cs:20` (`Execute`) | Subcommands: `reload`, `status`, `animoff`/`animon`, `animstate`, `rigoff`/`rigon`, `benchgod on\|off` (`ConsoleCmdEfficientServer.cs:32-73`). Reachable from the server terminal, the telnet remote console, and in-game clients the game's permission system admits to console commands |
| E4 | P/Invoke into bundled Boehm GC library | `Source/EfficientServer/BoehmNative.cs` (declarations), `Source/EfficientServer/GcIncremental.cs:30` (call site) | `monobdwgc-2.0`; flips collector mode and sets the pause limit. The megapause probe P/Invokes were removed after `v1.19.0` (`CHANGELOG.md:29`); what remains is the mode flip and the optional pause target |
| E5 | Install/run/uninstall scripts | `scripts/install.sh`, `scripts/uninstall.sh`, `scripts/run_server.sh`, `Makefile:142,147,149` | Build, back up user config, wipe and copy artifacts into `<DS>/Mods/EfficientServer/`, export GC/JIT env vars, exec the server binary. All three reject an exported-but-empty `SEVENDTD_DS_DIR` rather than defaulting (`install.sh:10`, and the same guard in `uninstall.sh` and `run_server.sh`) so the `rm -rf` cannot land on a different install. `uninstall.sh` copies `Config/` out to a timestamped backup before the wipe (`uninstall.sh:54,69`), and `run_server.sh` keeps the `<DS>/serverconfig*.xml` it replaces |
| E6 | CI workflow | `.github/workflows/ci.yml:5` | Runs `make test` on pushes to main and on PRs |
| E7 | Repo Python tooling | `scripts/check_config_doc.py`, `scripts/check_version.py`, `scripts/es_cfg_guard.py`, `scripts/coverage_badge.py`, `scripts/repo_root.py` | Stdlib-only, run by `make test` and CI over repo content and `config/efficientserver.json`. They read the working tree and exit nonzero; none writes outside the repo. `repo_root.py --selftest` is the only one with a self-test |
| E8 | Offline measurement and validation scripts (not in `make test`) | `scripts/validate_anim_path_admission.py`, `scripts/validate_bloodmoon_path.py`, `scripts/measure_es_onoff.py` | Need a live dedicated server; syntax-gated by `compileall` only (`Makefile:114`). They connect to a server the operator names, so the target host is operator-supplied input, not a network listener this repo opens |

## Trust boundaries

| ID | Boundary | Crossing data |
|---|---|---|
| B1 | Host filesystem to mod process (E2) | `Config/efficientserver.json` next to the assembly. Written by the operator; writable by anything that can write that directory. Trusted more than a network input would be, less than compiled-in constants. It is also the carrier for the B2 arm opt-ins, so it is not merely a tuning file (R3) |
| B2 | Console-equivalent actor to mod commands (E3) | Whoever passes the game's telnet password (or connects from loopback with no password set) or holds console permission in game. This mod adds no second gate beyond the two arm opt-ins below; everything else is console-access only |
| B3 | Mod to game host process (E1, E3, E4) | No boundary in a memory-safety sense: the mod shares the process, and patches rewrite game method behavior (prefix/transpiler/finalizer) |
| B4 | Build and publish to installed server (E5) | `dist/EfficientServer/` copied verbatim into the game's `Mods/` tree; zips additionally published via `dist/*.zip` |
| B5 | Host environment to server process (E5) | Env vars consumed at process init: `MALLOC_ARENA_MAX`, `GC_FREE_SPACE_DIVISOR`, `GC_NPROCS`, `MONO_ENV_OPTIONS`, optional heap/affinity vars (`scripts/run_server.sh:88-105`); plus `LD_LIBRARY_PATH` prepended with the server dir (`run_server.sh:86`) and the GC-incremental pair gated on `SEVENDTD_GC_INCREMENTAL` (`run_server.sh:109-110`) |
| B6 | CI runner to repository (E6) | GitHub Actions with `permissions: contents: read` (`.github/workflows/ci.yml:10`), token not persisted into the runner workspace (`ci.yml:29`) |

## Assets

| ID | Asset | Concrete blast radius |
|---|---|---|
| A1 | Server availability and tick latency | The mod's own levers (GC mode, entity shedding, animator culling) can freeze or degrade the tick; a bad patch crashes the process for all players |
| A2 | Game world and save integrity | `TickGuardPatch` despawns enemies (`Patches/TickGuardPatch.cs:97`); `AnimatorEmergency` degrades combat timing (`Patches/GovernorPatch.cs:132`); both alter live world state |
| A3 | Fair-play integrity | Server runs EAC-off by necessity of loading a C# mod (`Source/EfficientServer/ModInfo.xml:9`); `benchgod` is aimbot-grade damage immunity for every player while active (`Patches/BenchGodPatch.cs:23`) |
| A4 | Host RAM and CPU | GC env vars trade RAM for pause length. The heap-growing megapause diagnostic that could pin memory is gone (`CHANGELOG.md:29`) |
| A5 | Trust in the packaged DLL | Anything installed from `dist/` executes with full server authority; tampered artifacts are indistinguishable from releases unless a deployer manually compares the recorded SHA-256 |
| A6 | Game-owned data in-process | Save games, `serveradmin.xml`, session tokens held by the game are all reachable from mod code because there is no isolation. Not read or written by current mod code, but within blast radius of any code-execution event |

## Threats per boundary

### B1: config file to mod

- Tampering: extreme values reshape gameplay or load. Mitigated: every knob
  passes `Normalize` range clamps with logged corrections
  (`Source/EfficientServer/Config.cs:415`, clamp helper `Config.cs:495`), a
  misspelled key binds to nothing and keeps the built-in default (fail-soft per
  group, `Config.cs:377`, with template typos caught pre-packaging by
  `scripts/check_config_doc.py`), malformed JSON falls back to defaults
  (`Config.cs:384`). A JSON `null` for a whole section is backfilled from
  defaults by reflection so no null hole reaches a patch
  (`Config.cs:404`). Residual: clamped maxima are still potent (R5), and the
  file is unsigned, so clamps bound the damage, not the write (R3).
- Denial of service: config levers can degrade gameplay under load
  (`Governor.AnimatorEmergency` engages itself past `EmergencyOverMs`;
  `TickGuard` despawns entities). Both emit a WARNING when they engage
  (`GovernorPatch.cs:129`, `TickGuardPatch.cs:104`), so the event is
  reconstructable after the fact.
- Repudiation: corrections and parse failures are logged as WARN with values
  (`EsLog.Emit`, `EsLog.cs:26`). Reload apply failures are surfaced rather than
  swallowed, so no success echo covers a partial apply
  (`Source/EfficientServer/ModApi.cs:125` onward, per-group handling).

### B2: console actor to mod commands

- Elevation of privilege / abuse: the two arms that change gameplay for every
  player are refused unless the operator opted in through the config:
  `es benchgod on` requires `Diagnostics.AllowBenchGod`
  (`ConsoleCmdEfficientServer.cs:324`, gate `Config.cs:534`) and `es animoff` /
  `es rigoff` require `Diagnostics.AllowFidelityProbes`
  (`ConsoleCmdEfficientServer.cs:184`, gate `Config.cs:543`). Both gates fail
  closed on a null config or null section. Disarming is never gated. Residual
  risk R4: with the opt-in on, the arming is still one console command from
  anyone who reaches the console, with no confirmation, no scope, and no
  persistence across restart.
- Denial of service (minor): `es animstate` emits one console/log-sink line per
  living enemy; at horde scale this floods telnet output. Bounded by entity
  count, self-limited to manual invocation, and kept off the persisted log
  because it is read-only output (`ConsoleCmdEfficientServer.cs:100`).
- Repudiation: state-changing commands (`animprobe`, `rigprobe`, `benchgod`
  toggles, and refusals) persist through `Output` to console AND server log
  (`ConsoleCmdEfficientServer.cs:100`); command execution is additionally
  governed by the game setting `HideCommandExecutionLog`, kept at 0 = everything
  logged in both shipped templates (`serverconfig.optimized.xml:49`). A bare
  `es benchgod` peek is read-only and intentionally unlogged.

### B3: mod to host process

- Elevation of privilege: inherent and accepted; the mod IS privileged code.
  Controls reduce likelihood, not impact: per-group fail-soft patching so one
  bad target does not kill the rest (`PatchAllSafe`, `ModApi.cs:211`), visible
  MISSING TARGET detection on version drift (`ModApi.cs:94`), fail-closed
  dedicated gating (`ShouldRunFor`, `Config.cs:516`; the runtime probe fails
  closed, `ModApi.cs:263`) so server-only behavior (including BenchGod) cannot
  activate on an unknown/client host.
- Single point of failure: `ModApi.ShouldRun()` gates every behavioral patch,
  including damage immunity (`Patches/BenchGodPatch.cs:23`). If it ever returned
  true where it must not, several high-impact threats activate at once. Treat
  any future edit to it, and any edit to the two arm gates, as
  security-critical.
- Tampering with game logic: transpiler patches rewrite game IL at runtime
  (e.g. `LayerGridGraph.ScanInternal` node allocation,
  `Patches/InitScanPoolPatch.cs:31`). Fail-visibly-by-design on IL drift
  (`InitScanPoolPatch.cs:67`); re-verify after every game update.
- One-shot native flip: the GC mode flip cannot be undone, so it is guarded
  against repeat and mixed application (`GcIncremental.cs:27`) and the
  pause-target call has its own try so a missing entry point does not undo or
  re-run the flip (`GcIncremental.cs:41`).

### B4: build/publish to installed server

- Tampering: no signature, checksum comparison, or provenance check anywhere on
  the install path. `install.sh` backs up a differing user config via a trap
  that survives failure, then does `rm -rf` of the destination and copies from
  `dist/` (`scripts/install.sh:40,54,56`). An exported-but-empty
  `SEVENDTD_DS_DIR` is rejected before the build and before the wipe
  (`install.sh:10`), which is what keeps the `rm -rf` pointed at the install the
  operator named. `package.sh` produces a reproducible, byte-identical zip
  (`scripts/package.sh:42`); `scripts/verify_reproducible.sh` proves
  rebuildability. Named gap remains: install verifies none of this, and nothing
  signs anything.
- Spoofing (supply chain): CI actions are commit-pinned with a stated reason
  (`.github/workflows/ci.yml:24,33`), NuGet restore is locked-mode
  (`Makefile:119`) against an in-repo source list (`NuGet.config`: nuget.org
  only, inherited feeds cleared), push trigger scoped to main
  (`.github/workflows/ci.yml:6`). Dependency surface is small: Newtonsoft.Json
  comes from the game's own Managed folder for the mod; test deps are lock-pinned
  (`Source/EfficientServer.Tests/packages.lock.json`).
- Stale install: `run_server.sh` never builds or installs. It execs whatever
  `Mods/EfficientServer` the server tree already holds (`scripts/install.sh`
  is the only writer), so a launch can run a DLL that no longer matches the
  repository source. Re-run `make install` after every change.

### B5: host environment to server process

- Tampering (local): env vars materially change GC and JIT behavior
  (`scripts/run_server.sh:88-110`); `LD_LIBRARY_PATH` is prepended with the
  server dir (`run_server.sh:86`) so libraries there shadow system ones.
  `SEVENDTD_GC_INCREMENTAL` is the only var that turns a lever on by itself
  (`run_server.sh:109`). Local-operator trust domain; acceptable, listed so the
  surface is named.

### B6: CI runner to repository

- Low. Read-only token (`ci.yml:10`) that is not persisted into the runner
  workspace (`ci.yml:29`; the gate runs no git commands), 15-minute timeout
  (`ci.yml:20`), concurrency cancellation (`ci.yml:12`), NuGet cache keyed on
  the locked dependency graph (`ci.yml:36`). No secrets are used or needed.

## Abuse cases (scenarios, not demonstrations)

1. Config write escalates to gameplay control: an actor with write access to
   `Mods/EfficientServer/Config/efficientserver.json` (a shared host, a backup
   restore, a careless deploy) sets `Diagnostics.AllowBenchGod=true` and the
   desired clamps, and any later `es reload` (`ModApi.cs:116`) makes the policy
   live. No console access is needed for the policy itself, only for the arm.
   Config write access is therefore the higher-privilege position (R3).
2. Bench mode left hot: an operator enables the opt-in for a bench session and
   runs `es benchgod on`, then forgets it. Every player becomes immune to zombie
   damage until restart, because the flag is static and unpersisted
   (`ConsoleCmdEfficientServer.cs:332`; checked at `Patches/BenchGodPatch.cs:23`).
   The refusal path and the toggle are both audited to the log. What remains
   open is the missing scope and confirmation on a live server, not the gate.
3. Telnet inheritance: both shipped templates enable telnet with an empty
   password, relying on the game's loopback-only fallback
   (`serverconfig.optimized.xml:33-35`). An operator who sets a weak password or
   forwards port 8082 inherits full console access, and with it every `es`
   subcommand, including any arm the config has pre-authorized (R4 + R6). This
   repo's contribution to the fix is documentation accuracy, not code.
4. Reload-window drift: an operator edits the config between init and a later
   `es reload`; because the file has no watcher and `reload` re-reads from disk,
   whatever sits in the file at that moment becomes live policy, including
   levers that were off at boot (`ModApi.ReloadConfig`, `ModApi.cs:116`; late
   enable of skips/GC incremental is supported behavior). Anyone with write
   access to the config directory controls policy without console access,
   subject to the same clamps and the arm gates.
5. Artifact swap before install: an actor who can write `dist/EfficientServer/`
   between `make build` and `make install` supplies a DLL that runs with full
   server authority, and nothing on the install path notices (R2). Recovery is
   manual hash comparison only.

## Mitigations inventory (what exists)

| Control | Covers | Location |
|---|---|---|
| Config-opt-in gate on arming global damage immunity | B2 elevation of privilege, R4 | `Source/EfficientServer/ConsoleCmdEfficientServer.cs:324`, `Config.cs:534` (`BenchGodArmAllowed`) |
| Config-opt-in gate on arming fidelity probes (`animoff`, `rigoff`) | B2 elevation of privilege, R4 | `ConsoleCmdEfficientServer.cs:184`, `Config.cs:543` (`FidelityProbeArmAllowed`) |
| Disarm paths never gated, refusals audited | B2 availability, repudiation | `ConsoleCmdEfficientServer.cs:329,184` |
| Range-clamp normalization of every numeric knob, with logged corrections | B1 tampering extremes, config self-DoS upper bounds | `Source/EfficientServer/Config.cs:415`, clamp `Config.cs:495` |
| Reflection backfill of JSON-null sections from defaults | B1 null-hole reaching a patch | `Config.cs:404` |
| Parse-failure fallback to defaults | B1 malformed input | `Config.cs:384` |
| Config read pinned to UTF-8 | B1 encoding-dependent misparse across hosts | `Config.cs:373` |
| Shipped template typos caught pre-packaging | B1 silent misconfiguration | `scripts/check_config_doc.py` (run by `Makefile:152`) |
| Structure-aware value fuzz + byte-level file fuzz (invalid UTF-8, NUL, truncation, runaway nesting) and a write-then-read round trip of the loaded config | B1 parser robustness regressions, reload drift | `Source/EfficientServer.Tests/Fuzz.cs:36,143`, run by `Makefile:144` |
| Per-group fail-soft Harmony application | B3 partial breakage on version drift | `ModApi.cs:211` (`PatchAllSafe`) |
| Visible MISSING TARGET init summary | B3 silent target drift | `ModApi.cs:94` |
| Fail-closed `DedicatedOnly` gate (pure, unit-tested) | B3 activation on wrong host type | `Config.cs:516`, runtime probe `ModApi.cs:263` |
| One-shot guard and separate try on the irreversible native flip | B3 repeated/mixed GC modes | `Source/EfficientServer/GcIncremental.cs:27,41` |
| Reload apply failures surfaced, success echo suppressed | B1/B2 false "reloaded OK" over partial apply | `ModApi.cs:116` onward |
| State-changing console commands and refusals echoed to log | B2 repudiation | `ConsoleCmdEfficientServer.cs:100` |
| Severity-split logging channels (INFO/WARN/ERROR) | triage of config corrections vs failures | `EsLog.cs:18,26` |
| Emergency levers log as WARNING when engaged | B1/B3 unnoticed combat degradation or entity sheds | `Patches/GovernorPatch.cs:129`, `Patches/TickGuardPatch.cs:104` |
| Install dir guard: exported-but-empty `SEVENDTD_DS_DIR` rejected before build and wipe | B4 `rm -rf` on an unintended install | `scripts/install.sh:10`, same guard in `uninstall.sh` and `run_server.sh` |
| Failed install preserves the operator's config via EXIT trap | B4 silent config loss | `scripts/install.sh:40` |
| Commit-pinned CI actions, unpersisted read-only token, main-scoped push | B6 supply chain | `.github/workflows/ci.yml:6,10,24,29` |
| Locked-mode restore from an in-repo source list, SDK pin | B4/B6 dependency drift | `NuGet.config`, `Makefile:119`, `global.json` |
| Reproducible package build (sorted entries, epoch mtimes, rebuilt from scratch) | B4 artifact diffing | `scripts/package.sh:42` |
| Reproducibility proof target | B4 rebuild-equals-release claim | `Makefile:68`, `scripts/verify_reproducible.sh` |
| Command-execution logging kept on (game setting) | B2 repudiation | `serverconfig.optimized.xml:49` (template default) |

## Claimed-but-unverified and named gaps

Checked the docs against the code; results:

- No remaining false mitigation claim in README or docs: nothing asserts
  authentication, rate limiting, sandboxing, or input validation that the code
  lacks. EAC statements match reality: `ModInfo.xml` sets
  `SkipWithAntiCheat=true` (`Source/EfficientServer/ModInfo.xml:9`) and the
  docs correctly explain the mod therefore does not load under enforcing EAC.
- **Corrected on this pass.** The previous revision of this model and
  `SECURITY.md` both claimed that nothing in code refuses the bench-only toggles
  on a live server, and listed the missing guard as a top-3 risk. The code
  gates them: `ServerPerfConfig.BenchGodArmAllowed` and
  `FidelityProbeArmAllowed` (`Config.cs:534,541`) are enforced at the arm sites
  (`ConsoleCmdEfficientServer.cs:324,184`) and the refusal goes to the audited
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
  recorded artifact hash in `install.sh` before copying); R3 the config file is
  unsigned and pre-authorizes the R4 arms; R4 no scope, confirmation, or
  production-mode refusal on an opted-in arm; no signing or release provenance
  process documented anywhere.

## Response readiness (notes only)

- Audit trail available for investigation: everything logs through
  `[EfficientServer]`-prefixed lines into the game log (`EsLog.Emit`,
  `EsLog.cs:26`) across three severity channels, including config corrections,
  parse failures, patch failures, MISSING TARGET summaries, engaged emergency
  levers, entity sheds, and (via the console `Output` choke point) every
  state-changing console command and every arm refusal; executed commands
  generally also appear via the game's own execution log while
  `HideCommandExecutionLog=0`. No separate security event stream exists;
  o11y-review owns log structure.
- Vulnerability-reported-to-fix-shipped path: `SECURITY.md` names the reporting
  channel (GitHub issues, public until a private channel exists), the
  authoritative version source (`Source/EfficientServer/ModInfo.xml`, currently
  1.19.0), and the supported-version policy (current release only, no
  backports). No private disclosure contact is published yet; that remains an
  organizational gap, noted rather than invented.
