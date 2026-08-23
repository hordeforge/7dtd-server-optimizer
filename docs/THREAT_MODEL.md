# Threat model

Systemic view of what this repository exposes to attack: entry points, trust
boundaries, assets, threats, and the controls that exist versus the ones that
are missing. Individual vulnerability findings belong to sec-review; they land
here as threats with locations. Re-verify every file reference after each game
update (Harmony targets break silently, see AGENTS.md critical rule 3).

Last reviewed: 2026-08-23, against git 74269ad.
Owner: repository maintainers. Review trigger: every game update, every new
patch group, every change to `scripts/install.sh` / `run_server.sh`.

## Risk-ranked summary

| # | Risk | Boundary | Why |
|---|---|---|---|
| R1 | Full-host-authority code runs inside the game server process | Mod to host | By design: a Harmony mod is arbitrary code with the server's privileges. No isolation exists or is possible while it stays a C# mod. Every bug is a server crash or worse, not a sandbox escape |
| R2 | Unsigned build artifact installed over the game tree | Build to runtime | `install.sh` wipes and copies `dist/EfficientServer/` into `Mods/` with no hash or signature check. Whoever controls `dist/` or the Mods directory controls the server process |
| R3 | Dangerous operator toggles reachable from any console-level actor, no runtime guard | Console to mod | `es benchgod on` makes ALL players damage-immune server-side; `es animoff` degrades combat. Both are one command away for anyone with telnet/console access; the "bench only" restriction is procedural, not enforced |
| R4 | Config-file self-denial-of-service paths | Filesystem to mod | Opt-in diagnostics deliberately freeze the server (`Diagnostics.GcMegapauseTest`); clamps bound the damage but do not prevent it |
| R5 | Inherited telnet exposure | Network to console | The shipped serverconfig template enables telnet on port 8082; safety depends entirely on the game's loopback-fallback and failed-login limit, not on this repo |

Not risks here: the mod opens no sockets, stores no credentials, handles no
player data beyond what the game already holds, and never writes outside its
mod folder and the log.

## Entry points

| ID | Entry point | Location | Notes |
|---|---|---|---|
| E1 | Mod load into game process (`InitMod`) | `Source/EfficientServer/ModApi.cs:18` | Game loads the DLL at startup and calls `InitMod`; Harmony patches are installed into game methods here |
| E2 | Config JSON file read | `Source/EfficientServer/Config.cs:298` (`Load`), path resolution `Config.cs:458` | Read once at init and again on every `es reload`. Parsed with Newtonsoft.Json (game-bundled). No file watcher; disk changes apply only via E3 |
| E3 | Operator console command `es` / `efficientserver` | `Source/EfficientServer/ConsoleCmdEfficientServer.cs:19` | Subcommands: `reload`, `status`, `animoff`/`animon`, `animstate`, `rigoff`/`rigon`, `benchgod on\|off`. Reachable from the server terminal, the telnet remote console, and in-game clients the game's permission system admits to console commands |
| E4 | P/Invoke into bundled Boehm GC library | `Source/EfficientServer/GcIncremental.cs:25`, `Source/EfficientServer/GcDiagnostics.cs:20` | `libmonobdwgc-2.0`; flips collector mode, disables/enables collection, forces collects |
| E5 | Install/run scripts | `scripts/install.sh`, `scripts/run_server.sh`, `Makefile` (targets `install`, `uninstall`, `run`) | Copy artifacts into `<DS>/Mods/EfficientServer/`, export GC/JIT env vars, exec the server binary |
| E6 | CI workflow | `.github/workflows/ci.yml` | Runs `make test` on push/PR |

## Trust boundaries

| ID | Boundary | Crossing data |
|---|---|---|
| B1 | Host filesystem to mod process (E2) | `Config/efficientserver.json` next to the assembly. Written by the operator; writable by anything that can write that directory. Trusted more than a network input would be, less than compiled-in constants |
| B2 | Console-equivalent actor to mod commands (E3) | Whoever passes the game's telnet password (or connects from loopback with no password set) or holds console permission in game. This mod adds no second gate |
| B3 | Mod to game host process (E1, E3, E4) | No boundary in a memory-safety sense: the mod shares the process, and patches rewrite game method behavior (prefix/transpiler/finalizer) |
| B4 | Build and publish to installed server (E5) | `dist/EfficientServer/` copied verbatim into the game's `Mods/` tree |
| B5 | Host environment to server process (E5) | Env vars consumed at process init: `GC_FREE_SPACE_DIVISOR`, `GC_NPROCS`, `MONO_ENV_OPTIONS`, optional affinity wrapper; plus `LD_LIBRARY_PATH` prepended with the server dir |
| B6 | CI runner to repository (E6) | GitHub Actions with `permissions: contents: read` (`.github/workflows/ci.yml:4`) |

## Assets

| ID | Asset | Concrete blast radius |
|---|---|---|
| A1 | Server availability and tick latency | The mod's own levers (GC mode, entity shedding, animator culling) can freeze or degrade the tick; a bad patch crashes the process for all players |
| A2 | Game world and save integrity | `TickGuardPatch` despawns enemies; `AnimatorEmergency` degrades combat behavior; both alter live world state |
| A3 | Fair-play integrity | Server runs EAC-off by necessity of loading a C# mod (`docs/FEATURES.md:392`); `benchgod` is aimbot-grade damage immunity for every player while active (`Source/EfficientServer/Patches/BenchGodPatch.cs:19`) |
| A4 | Host RAM and CPU | Megapause diagnostic grows the heap toward a 24 GiB cap under load (`Source/EfficientServer/GcDiagnostics.cs:27`); GC env vars trade RAM for pause length |
| A5 | Trust in the packaged DLL | Anything installed from `dist/` executes with full server authority; tampered artifacts are indistinguishable from releases |
| A6 | Game-owned data in-process | Save games, `serveradmin.xml`, session tokens held by the game are all reachable from mod code because there is no isolation. Not read or written by current mod code, but within blast radius of any code-execution event |

## Threats per boundary

### B1: config file to mod

- Tampering (T): extreme values reshape gameplay or load. Mitigated: every knob
  passes `Normalize` range clamps with logged corrections
  (`Source/EfficientServer/Config.cs:371`), unknown keys are named and ignored
  (`Source/EfficientServer/Config.cs:309`), malformed JSON falls back to
  defaults (`Source/EfficientServer/Config.cs:329`). Residual: clamped maxima
  are still potent (see R4).
- Denial of service: `Diagnostics.GcMegapauseTest: true` intentionally disables
  collection, grows the heap under load, then forces a multi-second STW collect
  (`Source/EfficientServer/GcDiagnostics.cs:57`). Caps: warmup 1 h, grow 2 h,
  heap 24 GiB (`Source/EfficientServer/Config.cs:436`,
  `GcDiagnostics.cs:27`). Documented "never enable on a live server"
  (`Source/EfficientServer/Config.cs:250`); nothing enforces it.
- Repudiation: corrections and parse failures are logged with values
  (`ModApi.Log`), giving an after-the-fact trail in the server log.

### B2: console actor to mod commands

- Elevation of privilege / abuse: any console-level actor gets global,
  unpersisted damage immunity for all players via `es benchgod on`
  (`Source/EfficientServer/ConsoleCmdEfficientServer.cs:151`,
  `Patches/BenchGodPatch.cs:19`); combat degradation via `es animoff`
  (`ConsoleCmdEfficientServer.cs:57`). The only gate is reaching the console
  at all; the mod adds no confirmation, scoping, or prod-mode refusal.
- Denial of service (minor): `es animstate` emits one console/log line per
  living enemy (`ConsoleCmdEfficientServer.cs:91`); at horde scale this floods
  telnet output and the log. Bounded by entity count, self-limited to manual
  invocation.
- Repudiation: command execution logging is governed by the game setting
  `HideCommandExecutionLog`; the shipped template keeps it at 0 = everything
  logged (`server/serverconfig.optimized.xml:49`). Good default; depends on
  operators keeping it.

### B3: mod to host process

- Elevation of privilege: inherent and accepted; the mod IS privileged code.
  Controls reduce likelihood, not impact: per-group fail-soft patching so one
  bad target does not kill the rest (`ModApi.cs:147`), visible MISSING TARGET
  detection on version drift (`ModApi.cs:67`), fail-closed dedicated gating
  (`Config.cs:471`, `ModApi.cs:175`) so server-only behavior (including
  BenchGod) cannot activate on an unknown/client host.
- Single point of failure: `ModApi.ShouldRun()` gates every behavioral patch,
  including damage immunity (`BenchGodPatch.cs:23`). If it ever returned true
  where it must not, several high-impact threats activate at once. It fails
  closed on exception (`ModApi.cs:184`); treat any future edit to it as
  security-critical.
- Tampering with game logic: transpiler patches rewrite game IL at runtime
  (e.g. `LayerGridGraph.ScanInternal` node allocation,
  `Patches/InitScanPoolPatch.cs:31`). Fail-visibly-by-design on IL drift;
  re-verify after every game update.

### B4: build/publish to installed server

- Tampering: no signature, checksum, or provenance check anywhere on the
  install path. `install.sh` does `rm -rf` of the destination then copies from
  `dist/` (`scripts/install.sh:17`); `package.sh` produces a reproducible but
  unsigned zip (`scripts/package.sh:9`). `run_server.sh` silently builds and
  installs if the mod is missing (`scripts/run_server.sh:25`). Named gap:
  record and verify a hash at install time (candidate for a later pass; not
  fixed here).
- Spoofing (supply chain): CI actions are commit-pinned with a stated reason
  (`.github/workflows/ci.yml:18`), NuGet restore is locked-mode
  (`Makefile:28`). Dependency surface is small: Newtonsoft.Json comes from the
  game's own Managed folder for the mod; test deps are lock-pinned
  (`Source/EfficientServer.Tests/packages.lock.json`).

### B5: host environment to server process

- Tampering (local): env vars materially change GC and JIT behavior
  (`scripts/run_server.sh:48` onward); `LD_LIBRARY_PATH` is prepended with the
  server dir (`run_server.sh:30`) so libraries there shadow system ones.
  Local-operator trust domain; acceptable, listed so the surface is named.

### B6: CI runner to repository

- Low. Read-only token (`ci.yml:4`), 15-minute timeout (`ci.yml:14`),
  concurrency cancellation (`ci.yml:8`). No secrets are used or needed.

## Abuse cases (scenarios, not demonstrations)

1. Bench mode left hot: an operator runs `es benchgod on` during a bench
   session and forgets it on the production box. Every player becomes immune
   to zombie damage until restart. Enabling path:
   `ConsoleCmdEfficientServer.cs:151` sets the static flag checked by
   `BenchGodPatch.Prefix`. The flag is not persisted and prints a "(bench
   only!)" warning, but nothing refuses the toggle on a live server. Fix
   belongs to sec-review (e.g. refuse unless a diagnostics allow-flag is set).
2. Telnet inheritance: the shipped template enables telnet with an empty
   password, relying on the game's loopback-only fallback
   (`server/serverconfig.optimized.xml:33`). An operator who sets a weak
   password or forwards port 8082 inherits full console access, and with it
   every `es` toggle above. This repo's contribution to the fix is
   documentation accuracy, not code.
3. Diagnostic on production: `Diagnostics.GcMegapauseTest: true` committed to
   a live config freezes the server for the duration of the grow phase and
   the final collect. Path: `Config.Load` accepts it, `GameStartPatch`
   arms `GcDiagnostics.StartMegapauseTest`. Clamps bound it (R4) but do not
   prevent it.

## Mitigations inventory (what exists)

| Control | Covers | Location |
|---|---|---|
| Range-clamp normalization of every numeric knob | B1 tampering extremes, config self-DoS upper bounds | `Source/EfficientServer/Config.cs:371` |
| Unknown-key warning (typo guard) | B1 silent misconfiguration | `Source/EfficientServer/Config.cs:342` |
| Parse-failure fallback to defaults | B1 malformed input | `Source/EfficientServer/Config.cs:329` |
| Per-group fail-soft Harmony application | B3 partial breakage on version drift | `ModApi.cs:147` (`PatchAllSafe`) |
| Visible MISSING TARGET init summary | B3 silent target drift | `ModApi.cs:67` |
| Fail-closed `DedicatedOnly` gate | B3 activation on wrong host type | `Config.cs:471`, `ModApi.cs:175` |
| One-shot guards on irreversible native flips | B3 repeated/mixed GC modes | `GcIncremental.cs:35`, `GcDiagnostics.cs:45` |
| Defensive `GC_enable()` on probe failure | B3 permanently-disabled collector | `GcDiagnostics.cs:104` |
| Commit-pinned CI actions, read-only token | B6 supply chain | `.github/workflows/ci.yml:4,18` |
| Locked-mode restore, SDK pin | B4/B6 dependency drift | `Makefile:28`, `global.json` |
| Reproducible package build (sorted, epoch mtimes) | B4 artifact diffing | `scripts/package.sh:9` |
| Command-execution logging kept on | B2 repudiation | `server/serverconfig.optimized.xml:49` (template default) |

## Claimed-but-unverified and named gaps

Checked the docs against the code; results:

- No false mitigation claim found. Nothing in README or docs asserts
  authentication, rate limiting, sandboxing, or input validation that the code
  lacks. EAC statements match reality: `ModInfo.xml` sets
  `SkipWithAntiCheat=true` and the docs correctly explain the mod therefore
  does not load under enforcing EAC (`docs/FEATURES.md:392`).
- Claims carried forward as claims (plausible, not proven by this model):
  "provably equivalent" single-target send short-circuit
  (`Config.cs:126`, code `Patches/FastSendPatch.cs:20`); "changes no wire
  bytes" GC incremental (`GcIncremental.cs:13`); "EAC-safe" env vars
  (`run_server.sh:34`). Each is falsifiable by sec-review against the named
  code.
- Gaps (ranked): R2 unsigned install path; R3 no runtime guard on bench-only
  toggles; no hash verification or release signing process documented anywhere.

## Response readiness (notes only)

- Audit trail available for investigation: everything logs through
  `[EfficientServer]`-prefixed lines into the game log (`ModApi.cs:193`),
  including config corrections, patch failures, MISSING TARGET summaries, and
  (via the game, template default) executed console commands. No separate
  security event stream exists; o11y-review owns log structure.
- Vulnerability-reported-to-fix-shipped path: none existed before this pass;
  `SECURITY.md` now names the reporting channel and supported version. No
  private disclosure contact is published yet; that remains an organizational
  gap, noted rather than invented.
