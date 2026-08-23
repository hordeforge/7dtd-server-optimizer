# Threat model

Systemic view of what this repository exposes to attack: entry points, trust
boundaries, assets, threats, and the controls that exist versus the ones that
are missing. Individual vulnerability findings belong to sec-review; they land
here as threats with locations. Re-verify every file reference after each game
update (Harmony targets break silently, see AGENTS.md critical rule 3).

Last reviewed: 2026-08-23, against git e2b17e8.
Owner: repository maintainers. Review trigger: every game update, every new
patch group, every change to `scripts/install.sh` / `run_server.sh`.

## Risk-ranked summary

| # | Risk | Boundary | Why |
|---|---|---|---|
| R1 | Full-host-authority code runs inside the game server process | Mod to host | By design: a Harmony mod is arbitrary code with the server's privileges. No isolation exists or is possible while it stays a C# mod. Every bug is a server crash or worse, not a sandbox escape |
| R2 | Unverified build artifact installed over the game tree | Build to runtime | `install.sh` wipes and copies `dist/EfficientServer/` into `Mods/` with no hash or signature check (`scripts/install.sh:17`). The zip now carries an SBOM and each build records its SHA-256 (`scripts/package.sh:49,79`), but nothing on the install path compares them. Whoever controls `dist/` or the Mods directory controls the server process |
| R3 | Dangerous operator toggles reachable from any console-level actor, no runtime guard | Console to mod | `es benchgod on` makes ALL players damage-immune server-side; `es animoff` degrades combat; `es rigoff` strips entity rig behaviours. All are one command away for anyone with telnet/console access; the "bench only" restriction is procedural, not enforced |
| R4 | Config-file self-denial-of-service paths | Filesystem to mod | Opt-in diagnostics deliberately freeze the server (`Diagnostics.GcMegapauseTest`); clamps bound the damage but do not prevent it |
| R5 | Inherited telnet exposure | Network to console | Both shipped serverconfig templates enable telnet on port 8082 (`server/serverconfig.optimized.xml:33`, root copy identical); safety depends entirely on the game's loopback-fallback and failed-login limit, not on this repo |

Not risks here: the mod opens no sockets, spawns no processes, stores no
credentials, handles no player data beyond what the game already holds, and
never writes outside its mod folder and the log (verified: no socket/process/
write APIs under `Source/EfficientServer/`; only config reads,
`Source/EfficientServer/Config.cs:303,310`).

## Entry points

| ID | Entry point | Location | Notes |
|---|---|---|---|
| E1 | Mod load into game process (`InitMod`) | `Source/EfficientServer/ModApi.cs:21` | Game loads the DLL at startup and calls `InitMod`; Harmony patches install here per group. Post-start setup registers on the sanctioned `GameStartDone` hook, not a patched game method (`ModApi.cs:79`) |
| E2 | Config JSON file read | `Source/EfficientServer/Config.cs:301` (`Load`), path resolution `Config.cs:487` | Read once at init and again on every `es reload`. Parsed with Newtonsoft.Json (game-bundled); read pinned to UTF-8 (`Config.cs:310`). No file watcher; disk changes apply only via E3 |
| E3 | Operator console command `es` / `efficientserver` | `Source/EfficientServer/ConsoleCmdEfficientServer.cs:20` (`Execute`) | Subcommands: `reload`, `status`, `animoff`/`animon`, `animstate`, `rigoff`/`rigon`, `benchgod on\|off`. Reachable from the server terminal, the telnet remote console, and in-game clients the game's permission system admits to console commands |
| E4 | P/Invoke into bundled Boehm GC library | `Source/EfficientServer/GcIncremental.cs:25`, `Source/EfficientServer/GcDiagnostics.cs:20` | `libmonobdwgc-2.0`; flips collector mode, sets pause limit, disables/enables collection, forces collects |
| E5 | Install/run scripts | `scripts/install.sh`, `scripts/run_server.sh`, `Makefile` (targets `install`, `uninstall`, `run`) | Build, back up user config, wipe and copy artifacts into `<DS>/Mods/EfficientServer/`, export GC/JIT env vars, exec the server binary |
| E6 | CI workflow | `.github/workflows/ci.yml:5` | Runs `make test` on pushes to main and on PRs |

## Trust boundaries

| ID | Boundary | Crossing data |
|---|---|---|
| B1 | Host filesystem to mod process (E2) | `Config/efficientserver.json` next to the assembly. Written by the operator; writable by anything that can write that directory. Trusted more than a network input would be, less than compiled-in constants |
| B2 | Console-equivalent actor to mod commands (E3) | Whoever passes the game's telnet password (or connects from loopback with no password set) or holds console permission in game. This mod adds no second gate |
| B3 | Mod to game host process (E1, E3, E4) | No boundary in a memory-safety sense: the mod shares the process, and patches rewrite game method behavior (prefix/transpiler/finalizer) |
| B4 | Build and publish to installed server (E5) | `dist/EfficientServer/` copied verbatim into the game's `Mods/` tree; zips additionally published via `dist/*.zip` with buildinfo sidecars |
| B5 | Host environment to server process (E5) | Env vars consumed at process init: `GC_FREE_SPACE_DIVISOR`, `GC_NPROCS`, `MONO_ENV_OPTIONS`, optional heap/affinity vars (`scripts/run_server.sh:48-60`); plus `LD_LIBRARY_PATH` prepended with the server dir (`run_server.sh:30`) |
| B6 | CI runner to repository (E6) | GitHub Actions with `permissions: contents: read` (`.github/workflows/ci.yml:10`), token not persisted into the runner workspace (`ci.yml:29`) |

## Assets

| ID | Asset | Concrete blast radius |
|---|---|---|
| A1 | Server availability and tick latency | The mod's own levers (GC mode, entity shedding, animator culling) can freeze or degrade the tick; a bad patch crashes the process for all players |
| A2 | Game world and save integrity | `TickGuardPatch` despawns enemies (`Patches/TickGuardPatch.cs:88`); `AnimatorEmergency` degrades combat timing; both alter live world state |
| A3 | Fair-play integrity | Server runs EAC-off by necessity of loading a C# mod (`docs/FEATURES.md:396`); `benchgod` is aimbot-grade damage immunity for every player while active (`Patches/BenchGodPatch.cs:19`) |
| A4 | Host RAM and CPU | Megapause diagnostic grows the heap toward a 24 GiB cap under load (`GcDiagnostics.cs:27`); GC env vars trade RAM for pause length |
| A5 | Trust in the packaged DLL | Anything installed from `dist/` executes with full server authority; tampered artifacts are indistinguishable from releases unless a deployer manually compares the recorded SHA-256 |
| A6 | Game-owned data in-process | Save games, `serveradmin.xml`, session tokens held by the game are all reachable from mod code because there is no isolation. Not read or written by current mod code, but within blast radius of any code-execution event |

## Threats per boundary

### B1: config file to mod

- Tampering (T): extreme values reshape gameplay or load. Mitigated: every knob
  passes `Normalize` range clamps with logged corrections
  (`Source/EfficientServer/Config.cs:396`), unknown keys are named and ignored
  (`Config.cs:314`), malformed JSON falls back to defaults (`Config.cs:334`),
  NaN/Infinity take clamped fallbacks (`Config.cs:467`). Residual: clamped
  maxima are still potent (see R4), and the config can enable levers that
  automatically degrade gameplay under load (`Governor.AnimatorEmergency`,
  default false, engages itself past `EmergencyOverMs`,
  `Patches/GovernorPatch.cs:65`; `TickGuard` despawns entities, default false).
- Denial of service: `Diagnostics.GcMegapauseTest: true` intentionally disables
  collection, grows the heap under load, then forces a multi-second STW collect
  (`GcDiagnostics.cs:57`). Caps: warmup 1 h, grow 2 h, heap 24 GiB
  (`Config.cs:463`, `GcDiagnostics.cs:27`). Documented "never enable on a live
  server" (`Config.cs:255`); nothing enforces it. The diagnostic arms only at
  game start; `es reload` deliberately never re-runs it (`ModApi.cs:117`), so
  mid-run config edits need a restart to arm it.
- Repudiation: corrections and parse failures are logged as WARN with values
  (`ModApi.Warn`, `ModApi.cs:231`), giving an after-the-fact trail. Reload apply
  failures are surfaced and rethrown so no success echo covers a partial apply
  (`ModApi.cs:129`).

### B2: console actor to mod commands

- Elevation of privilege / abuse: any console-level actor gets global,
  unpersisted damage immunity for all players via `es benchgod on`
  (`ConsoleCmdEfficientServer.cs:191`, `Patches/BenchGodPatch.cs:19`); combat
  degradation via `es animoff` (CullCompletely emergency,
  `ConsoleCmdEfficientServer.cs:96`); visual-only rig behaviour disable via
  `es rigoff` (`ConsoleCmdEfficientServer.cs:148`). The only gate is reaching
  the console at all; the mod adds no confirmation, scoping, or prod-mode
  refusal.
- Denial of service (minor): `es animstate` emits one console/log-sink line per
  living enemy (`ConsoleCmdEfficientServer.cs:120`); at horde scale this floods
  telnet output. Bounded by entity count, self-limited to manual invocation;
  stays console-only, not written to the log (`ConsoleCmdEfficientServer.cs:200`).
- Repudiation: state-changing commands (`animprobe`, `rigprobe`, `benchgod`
  toggles) persist through `ConsoleCommandUtil.Output` to console AND server log
  (`ConsoleCommandUtil.cs:23`); command execution is additionally governed by
  the game setting `HideCommandExecutionLog`, kept at 0 = everything logged in
  both shipped templates (`server/serverconfig.optimized.xml:49`). A bare
  `es benchgod` peek is read-only and intentionally unlogged
  (`ConsoleCmdEfficientServer.cs:195`).

### B3: mod to host process

- Elevation of privilege: inherent and accepted; the mod IS privileged code.
  Controls reduce likelihood, not impact: per-group fail-soft patching so one
  bad target does not kill the rest (`PatchAllSafe`, `ModApi.cs:174`), visible
  MISSING TARGET detection on version drift (`ModApi.cs:70`), fail-closed
  dedicated gating (`ShouldRunFor`, `Config.cs:500`; exception path fails
  closed, `ModApi.cs:213`) so server-only behavior (including BenchGod) cannot
  activate on an unknown/client host.
- Single point of failure: `ModApi.ShouldRun()` gates every behavioral patch,
  including damage immunity (`BenchGodPatch.cs:23`). If it ever returned true
  where it must not, several high-impact threats activate at once. It fails
  closed on exception (`ModApi.cs:213`); treat any future edit to it as
  security-critical.
- Tampering with game logic: transpiler patches rewrite game IL at runtime
  (e.g. `LayerGridGraph.ScanInternal` node allocation,
  `Patches/InitScanPoolPatch.cs:31`). Fail-visibly-by-design on IL drift
  (`InitScanPoolPatch.cs:67`); re-verify after every game update.

### B4: build/publish to installed server

- Tampering: no signature, checksum comparison, or provenance check anywhere on
  the install path. `install.sh` backs up a differing user config, then does
  `rm -rf` of the destination and copies from `dist/` (`scripts/install.sh:17`);
  `package.sh` produces a reproducible, byte-identical zip
  (`scripts/package.sh:16`) and now records artifact SHA-256, commit, epoch, and
  compiler in a `.buildinfo.txt` sidecar (`scripts/package.sh:79`) plus a
  CycloneDX SBOM inside the zip (`scripts/package.sh:49`);
  `scripts/verify_reproducible.sh` proves rebuildability. Named gap remains:
  install itself verifies none of this, and nothing signs anything. An operator
  who wants integrity must diff hashes by hand.
- Spoofing (supply chain): CI actions are commit-pinned with a stated reason
  (`.github/workflows/ci.yml:24,33`), NuGet restore is locked-mode
  (`Makefile:70`), push trigger scoped to main (`.github/workflows/ci.yml:6`).
  Dependency surface is small: Newtonsoft.Json comes from the game's own
  Managed folder for the mod; test deps are lock-pinned
  (`Source/EfficientServer.Tests/packages.lock.json`).
- Silent install: `run_server.sh` builds and installs automatically if the DLL
  is missing from the server tree (`scripts/run_server.sh:25`), so a launch can
  ship code that was never explicitly reviewed as a release.

### B5: host environment to server process

- Tampering (local): env vars materially change GC and JIT behavior
  (`scripts/run_server.sh:48` onward); `LD_LIBRARY_PATH` is prepended with the
  server dir (`run_server.sh:30`) so libraries there shadow system ones.
  Local-operator trust domain; acceptable, listed so the surface is named.

### B6: CI runner to repository

- Low. Read-only token (`ci.yml:10`) that is not persisted into the runner
  workspace (`ci.yml:29`; the gate runs no git commands), 15-minute timeout
  (`ci.yml:20`), concurrency cancellation (`ci.yml:12`), NuGet cache keyed on
  the locked dependency graph (`ci.yml:36`). No secrets are used or needed.

## Abuse cases (scenarios, not demonstrations)

1. Bench mode left hot: an operator runs `es benchgod on` during a bench
   session and forgets it on the production box. Every player becomes immune
   to zombie damage until restart. Enabling path:
   `ConsoleCmdEfficientServer.cs:191` sets the static flag checked by
   `BenchGodPatch.Prefix`. The flag is not persisted, prints "(bench only!)",
   and is audited to the log, but nothing refuses the toggle on a live server.
   Fix belongs to sec-review (e.g. refuse unless a diagnostics allow-flag is
   set).
2. Telnet inheritance: both shipped templates enable telnet with an empty
   password, relying on the game's loopback-only fallback
   (`server/serverconfig.optimized.xml:33`; root copy identical). An operator
   who sets a weak password or forwards port 8082 inherits full console access,
   and with it every `es` toggle above. This repo's contribution to the fix is
   documentation accuracy, not code.
3. Diagnostic on production: `Diagnostics.GcMegapauseTest: true` committed to a
   live config freezes the server for the duration of the grow phase and the
   final collect. Path: `Config.Load` accepts it (`Config.cs:301`),
   `GameStartPatch.OnGameStartDone` arms it (`Patches/GameStartPatch.cs:23`),
   one-shot per process (`GcDiagnostics.cs:45`). Clamps bound it (R4) but do
   not prevent it; a restart with the flag still set re-arms it.
4. Reload-window drift: an operator edits the config between init and a later
   `es reload`; because the file has no watcher and `reload` re-reads from disk,
   whatever sits in the file at that moment becomes live policy, including
   levers that were off at boot (`ModApi.ReloadConfig`, `ModApi.cs:92`; late
   enable of skips/GC incremental is supported behavior, `ModApi.cs:119`).
   Anyone with write access to the config directory therefore controls policy
   without needing console access, subject to the same clamps.

## Mitigations inventory (what exists)

| Control | Covers | Location |
|---|---|---|
| Range-clamp normalization of every numeric knob, NaN fallbacks | B1 tampering extremes, config self-DoS upper bounds | `Source/EfficientServer/Config.cs:396,467` |
| Unknown-key warning (typo guard), ordinal case fold matching Newtonsoft binding | B1 silent misconfiguration, locale-dependent false alarms | `Config.cs:349,370` |
| Parse-failure fallback to defaults | B1 malformed input | `Config.cs:334` |
| Config read pinned to UTF-8 | B1 encoding-dependent misparse across hosts | `Config.cs:310` |
| Structure-aware + garbage fuzzing of config parsing in CI | B1 parser robustness regressions | `Source/EfficientServer.Tests/Fuzz.cs`, run by `Makefile:71` |
| Per-group fail-soft Harmony application | B3 partial breakage on version drift | `ModApi.cs:174` (`PatchAllSafe`) |
| Visible MISSING TARGET init summary | B3 silent target drift | `ModApi.cs:70` |
| Fail-closed `DedicatedOnly` gate | B3 activation on wrong host type | `Config.cs:500`, `ModApi.cs:213` |
| One-shot guards on irreversible native flips | B3 repeated/mixed GC modes | `GcIncremental.cs:35`, `GcDiagnostics.cs:45` |
| Defensive `GC_enable()` on probe failure | B3 permanently-disabled collector | `GcDiagnostics.cs:104` |
| Reload apply failures surfaced, success echo suppressed | B1/B2 false "reloaded OK" over partial apply | `ModApi.cs:129` |
| State-changing console commands echoed to log | B2 repudiation | `ConsoleCommandUtil.cs:23` |
| Severity-split logging channels (INFO/WARN/ERROR) | triage of config corrections vs failures | `ModApi.cs:222-241` |
| Emergency levers log as WARNING when engaged | B1/B3 unnoticed combat degradation or entity sheds | `Patches/GovernorPatch.cs:108`, `Patches/TickGuardPatch.cs:94` |
| Commit-pinned CI actions, unpersisted read-only token, main-scoped push | B6 supply chain | `.github/workflows/ci.yml:6,10,24,29` |
| Locked-mode restore, SDK pin | B4/B6 dependency drift | `Makefile:70`, `global.json` |
| Reproducible package build (sorted entries, epoch mtimes, rebuilt from scratch) | B4 artifact diffing | `scripts/package.sh:16,63` |
| SBOM in every release zip; buildinfo SHA-256/commit/compiler sidecar | B4 artifact inventory and manual verification inputs | `scripts/package.sh:49,79` |
| Reproducibility proof target | B4 rebuild-equals-release claim | `Makefile:45`, `scripts/verify_reproducible.sh` |
| Command-execution logging kept on (game setting) | B2 repudiation | `server/serverconfig.optimized.xml:49` (template default) |

## Claimed-but-unverified and named gaps

Checked the docs against the code; results:

- No false mitigation claim found. Nothing in README or docs asserts
  authentication, rate limiting, sandboxing, or input validation that the code
  lacks. EAC statements match reality: `ModInfo.xml` sets
  `SkipWithAntiCheat=true` (`Source/EfficientServer/ModInfo.xml:11`) and the
  docs correctly explain the mod therefore does not load under enforcing EAC
  (`docs/FEATURES.md:400`).
- Claims carried forward as claims (plausible, not proven by this model):
  "provably equivalent" single-target send short-circuit
  (`Config.cs:130`, code `Patches/FastSendPatch.cs:44`); "changes no wire
  bytes" GC incremental (`GcIncremental.cs:13`); "EAC-safe" env vars
  (`run_server.sh:34`). Each is falsifiable by sec-review against the named
  code.
- Gaps (ranked): R2 install path verifies nothing even though hashes are now
  recorded (candidate fix: compare buildinfo SHA-256 in `install.sh` before
  copying); R3 no runtime guard on bench-only toggles; no signing or release
  provenance process documented anywhere.

## Response readiness (notes only)

- Audit trail available for investigation: everything logs through
  `[EfficientServer]`-prefixed lines into the game log (`ModApi.Emit`,
  `ModApi.cs:246`) across three severity channels, including config
  corrections, patch failures, MISSING TARGET summaries, engaged emergency
  levers, entity sheds, and (via `ConsoleCommandUtil.Output`) every
  state-changing console command; executed commands generally also appear via
  the game's own execution log while `HideCommandExecutionLog=0`. No separate
  security event stream exists; o11y-review owns log structure.
- Vulnerability-reported-to-fix-shipped path: `SECURITY.md` names the reporting
  channel (GitHub issues, public until a private channel exists) and the
  supported-version policy. No private disclosure contact is published yet;
  that remains an organizational gap, noted rather than invented.
