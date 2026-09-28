# EfficientServer development

**Hub:** [`README.md`](../README.md).  
**Owns:** how to change EfficientServer (build, patch groups, evidence loop).  
**Not:** feature behavior detail ([FEATURES](FEATURES.md)), host ops ([HOST_TUNING](HOST_TUNING.md)).

**General 7DTD modding (layers, XPath, Harmony hygiene, packaging, EAC):**  
[`../../MODDING_BEST_PRACTICES.md`](../../MODDING_BEST_PRACTICES.md)

This file is **optimizer-only**: how to change EfficientServer without inventing a second modding guide.

## Scope of this project

| Owns | Does not own |
|---|---|
| Reviewed Harmony optimizers (AI LOD, task skip, dedicated skips, dynamic mesh budgets) | Profiler / APM (use `7dtd-server-apm`) |
| `config/efficientserver.json` feature groups | Load generation (use `7dtd-loadgen`) |
| Stock-game RE tooling lives in the sibling [`../../7dtd-engine-research/tools/`](../../7dtd-engine-research/tools/) (not here) | Terrain / RealEarth product work |
| Dedicated-focused install scripts | Balance XML modlets |
| Links to host ops guidance | **CCD/NUMA/affinity** (ops only; see [`HOST_TUNING.md`](HOST_TUNING.md)) |

Default config: `DedicatedOnly: true`. Do not turn EfficientServer into a client overhaul or a measurement suite.

## Source layout

Two projects under `Source/`. The mod (`net48`) cannot build without a game install; the test harness (`net8.0`) builds and runs on the bare .NET SDK, which is what `make test` runs.

| Path | Role |
|---|---|
| `EfficientServer/ModApi.cs` | `IModApi` entry point and composition root: the only place the required patch groups are enumerated (`RequiredGroups`), and the only place the config is reloaded. The world-load re-base lives in `GameStartPatch.StartSteps`, and the imperative skip group installs its own prefixes under a `.optional` Harmony id |
| `EfficientServer/ApplyChain.cs` | The step runner both apply chains share: one boundary per lever, so a throwing step cannot skip the steps behind it, plus the collected `name[Type]: message` report the callers log (and `ModApi.ReloadConfig` rethrows on) |
| `EfficientServer/Config.cs` | Config model: one sealed section class per config block, `ServerPerfConfig` with `Load` / `Normalize` / `FeatureActive`, and the pure `ShouldRunFor` / `BenchGodArmAllowed` policy the tests exercise |
| `EfficientServer/EsLog.cs`, `LogLine.cs` | The single logging surface; the `LogLevel` enum lives here so low-level modules log without depending on `ModApi`. `LogLine` owns the record shape `Emit` renders: one physical line per record (a reported exception's breaks become ` | `) plus the `uptimeS=` stamp shared with the `es status` runtime line |
| `EfficientServer/ConsoleCmdEfficientServer.cs` | The `es` console command; reads the live config, never owns state |
| `EfficientServer/GcIncremental.cs`, `BoehmNative.cs` | One-shot Boehm mode flip and the only P/Invoke surface into it |
| `EfficientServer/Patches/` | Harmony patches plus the shared types they call. The `*Patch` suffix marks a class that participates in Harmony patching, but not all of them are processed the same way: `[HarmonyPatch]`-annotated classes go through Harmony's class processor from `ModApi.RequiredGroups` and MUST match a game method, while `GameStartPatch` (the `ModEvents.GameStartDone` handler) runs `ApplyChain`, where `DedicatedSkipPatch.ApplyOptional` installs that group's prefixes imperatively from that same hook (a missing target there logs its own status, it does not fail the group). `DynamicMeshBudgetPatch` rides the same start-time chain but patches no code: it writes four stock `DynamicMeshSettings` statics and restores them on disable, so it has no target to drift. Non-suffixed files here (`TickClock`, `TickIntervalEma`, `ShedOrder`, `AiAlertGate`, `AnimatorEmergency`, `GovernorTiers`, `RigVisualProbe`, `DedicatedHostGate`) are support types, never patched at all. A support type that holds process state for an in-assembly consumer belongs here rather than in the command that drives it: `AnimatorEmergency` and `RigVisualProbe` are the two fidelity probes `es animoff`/`rigoff` arm, and both keep their tracking and restore paths out of `ConsoleCmdEfficientServer.cs`, so the command file owns only dispatch, the arm gate, and the echo |
| `EfficientServer.Tests/` | Assert harness (no test framework, so it builds offline) over a hand-picked, game-type-free subset of the mod, listed as `<Compile Include>` paths in its csproj |

**Clocks are injected, never read:** the mod's two real-time reads, `TickIntervalEma`'s tick interval (which drives governor tiers and tick-guard sheds) and `LogLine`'s `uptimeS` stamp, each bind a `Func<double>` time source and default to the process stopwatch. A harness or a replay passes a virtual clock and drives the shipping entry points, so a run reproduces from a recorded tick schedule and its log stream replays byte-for-byte; a new clock read in a game-type-free module has to arrive the same way, or nothing can replay it.

**Getting new code under test:** the harness compiles real production files by path rather than referencing the mod assembly, because the mod needs the game DLLs. A new module is testable only if it references no game type, and it gets zero coverage until its path is added to that csproj. `EsLog` is the one production type the harness re-declares as a stub, because the real one calls the game's `global::Log`; the stub runs messages through the real `LogLine`, and a check greps the shipped `EsLog.cs` to pin that it does the same. The harness also fails on a support module that is in neither list: every non-`*Patch.cs` file under `Source/EfficientServer/` must be a `<Compile Include>` path or a `GameCoupled` entry in `Program.cs` carrying the reason it cannot be compiled here, so the gap above is caught at `make unit` instead of at review.

## Patch groups

See [`FEATURES.md`](FEATURES.md) for behavior and validation notes. Groups are applied independently from `ModApi` so one missing target should not kill the rest.

| Group | Config block | Intent |
|---|---|---|
| Tick clock | (none, always on) | `TickClock` counter every stripe reads; unconditional, no config gate may stop a clock other groups consume |
| AI LOD | `AiLod` | Distant AI scale / distance bands |
| Task skip | (with AI LOD) | Distant non-alert `updateTasks` throttling |
| Dedicated skips | `SkipOnDedicated` | Presentation paths useless on headless (music, splash, environment audio, ambient spectrum) |
| Explosion particles | `SkipOnDedicated.ExplosionParticles` | Skip the explosion prefab `Instantiate`, keep physics push, block damage and the quest event (own patch group on `GameManager.ExplosionClient`) |
| Dynamic mesh | `DynamicMesh` | Player-area / time budgets |
| GC pause guard | `Gc` | Skip forced periodic `GC.Collect`; host-aware safety collect; opt-in incremental mode |
| Pathfinding graph throttle | `Pathfinding.GraphUpdateEveryTicks` | Rate-limit `AstarManager.UpdateGraphs` |
| Move rescan threshold | `Pathfinding.MoveRescanThresholdSq` | Widen the grid rescan dead-zone (fewer `InitScan`) |
| Path admission | `Pathfinding.MaxPathEnqueuesPerTick` / `DropPathWhenFarDistSq` | Cap / drop far non-priority path enqueues |
| InitScan node pool | `Pathfinding.PoolInitScanNodes` | UNSAFE: reuse nav node array across scans |
| Fast single-target send | `Network.FastSingleTargetSend` | O(1) recipient lookup in `SendPackage` |
| Client-list snapshot | `Network.ClientListSnapshot` | Scan a private copy of the client list, closing the stock receive-thread enumeration race |
| Replication stride | `Network.EntityDistributionEveryTicks` | Run the replication pass every Nth tick |
| Chunk-send throttle | `WorldTransfer.ChunkPackagesPerObserverPerTick` | Cap chunk packages per observer per tick |
| Target FPS | `Server.TargetFps` | Persistent frame-rate set at game start |
| Animator LOD | `AnimatorLod` | Reduced-rate animation for calm distant zombies |
| Crowd-collision LOD | `CrowdCollisionLod` | Stagger zombie entity-collision queries |
| Governor | `Governor` | Adaptive engagement of the throttle levers under overload (+ opt-in tier 2) |
| TickGuard | `TickGuard` | Last-resort emergency load shedding |
| BenchGod | console `es benchgod` | BENCH ONLY diagnostic (player damage immunity; arming needs `Diagnostics.AllowBenchGod: true`) |
| Game start reapply | (lifecycle) | Re-apply mesh settings after start |

The authoritative list is the `RequiredGroups` table in `Source/EfficientServer/ModApi.cs`: one row per patch group, pairing the type the class processor patches with the `ServerPerfConfig.Key*` constant its init log reports against, so the apply list and the feature vocabulary cannot drift. `Game start reapply` is not in it (that is the `ModEvents.GameStartDone` lifecycle hook, not a Harmony group).

Change **one group at a time**, then re-measure.

## Workflow

```text
0. make test - shellcheck + ruff lint and format gates, mypy type gate, config harness
   (normalize/clamps/invariants/fuzz), config-doc
   coverage gate, version-consistency gate (ModInfo == Assembly == docs);
   also runs in CI on every PR and on pushes to main (.github/workflows/ci.yml)
   Narrower loop while editing: make lint (scripts), make unit (the C# harness),
   make check-scripts (doc/version gates). 'make test' is those three in order.
   One harness check at a time: make unit FILTER='<glob>' (names from
   make unit-list).
1. Baseline: 7dtd-loadgen workload + 7dtd-server-apm capture
2. Edit one feature group (config and/or patch code)
3. Rebuild and install against current dedicated Managed
4. Same workload + APM compare / budget
5. Gameplay soak (combat, sleepers, quests, multi-player separation)
```

```bash
make build
make install DS="/path/to/7 Days to Die Dedicated Server"
# or: ./scripts/install.sh
```

## Environment variables (scripts)

All optional; scripts fall back to defaults. The Makefile routes its documented
`DS=...` argument through `SEVENDTD_DS_DIR`, so both spellings stay in sync.

| Variable | Read by | Default | Meaning |
|---|---|---|---|
| `SEVENDTD_DS_DIR` / make `DS=` | build.sh, install.sh, uninstall.sh, run_server.sh, make `uninstall`, harness scripts (`measure_es_onoff.py`, `validate_*`) | `~/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server` | Dedicated install: game DLL refs, mod install target, launch dir. Harnesses also accept `SEVENDTD_SERVER_DIR` (the 7dtd-loadgen sibling's spelling) |
| `SEVENDTD_UNINSTALL_BACKUP_DIR` | uninstall.sh | `<DS>/EfficientServer-uninstall-backup` | Where `make uninstall` copies the live `Config/` before deleting the mod. Outside `Mods/` so the game's mod scan never sees it; point it at another disk for host-loss cover (the default sits on the same disk, which uninstall.sh warns about) |
| `SEVENDTD_INSTALL_BACKUP_DIR` | install.sh | `<DS>/EfficientServer-install-backup` | Where a FAILED `make install` leaves the preserved `Config/`, and where the next `make install` looks for it. A rerun after a failed install adopts this copy instead of installing the shipped default over the operator's tuning; a successful install removes it. Same off-host caveat as the uninstall dir |
| `ES_CONFIG_BACKUP_DEST` | make `backup-config`, `backup_config.py --dest` | none (required) | Off-host snapshot destination for the live `Config/` and the live `serverconfig*.xml`; `backup_config.py` rejects any path inside the server install tree |
| `SEVENDTD_UNINSTALL_PURGE` | uninstall.sh | unset (config is preserved) | `1` deletes the installed config with the mod, no copy kept |
| `SEVENDTD_GAME_DIR` | build.sh | client install path | Client fallback for game DLL refs |
| `SEVENDTD_BUILD_BACKEND` | build.sh (`make build-mcs`) | auto (dotnet if SDK present) | `mcs` forces the Mono fallback compiler; `dotnet` forces the SDK path and fails hard without one |
| `SEVENDTD_CONFIG` | run_server.sh | local `server/serverconfig.optimized.xml`, else tracked root `serverconfig.optimized.xml` | Dedicated server config XML |
| `SEVENDTD_LOGDIR` | run_server.sh | `server/logs` | Server log directory |
| `VALIDATE_OUT` | harness scripts (`measure_es_onoff.py`, `validate_*`) | `server/logs` | Directory for harness JSON reports |
| `SEVENDTD_TELNET_PASSWORD` | harness scripts (telnet via 7dtd-loadgen) | `retest` | Telnet password of the bench dedicated server (local test servers only) |
| `RE_DEDICATED_USERDATA` | measure_es_onoff.py | `~/.cache/7dtd-loadgen` | 7dtd-loadgen dedicated userdata dir; also where the Unity-log lookup globs `server_prefab_*.txt`. Owned by 7dtd-loadgen (`start_dedicated_prefab.sh`) |
| `SEVENDTD_CPU_AFFINITY` | run_server.sh | unset (no pinning) | `taskset -c` mask for the whole process; silently skipped when `taskset` is absent; see HOST_TUNING.md (measured loss on naive pinning) |
| `SEVENDTD_GC_INCREMENTAL` | run_server.sh | unset | Opt-in incremental GC (sets `GC_ENABLE_INCREMENTAL=1`) |
| `DOTNET_ROOT` | build.sh, Makefile | Makefile picks the first of `~/.cache/dotnet-sdk`, `~/.dotnet` containing a `dotnet` binary; direct build.sh runs fall back to `~/.cache/dotnet-sdk` only (and export it) | Local SDK location prepended to PATH |
| `TMPDIR` | make `test`/`coverage`, install.sh, package.sh, verify_reproducible.sh | `.scratch/tmp` in this repo | Overridden away from the stock `/tmp`, which is tmpfs on most Linux hosts: staging trees, config backups and test temp files would be held in RAM and lost on reboot. Honored by `mktemp`, Python's `tempfile` and .NET's `Path.GetTempPath`, so one setting covers all three. Staging trees from package.sh and verify_reproducible.sh are named `<prefix>.<pid>.…` by `scripts/stage_tmp.sh` and swept when their owning pid is gone, because a run killed by a tool timeout never reaches its `EXIT` trap; `make` also reaps the .NET SDK's own empty `MSBuildTemp*`/`NuGetScratch*` leftovers there, which no version of the SDK removes. install.sh's failure backup is deliberately exempt from both |
| `SOURCE_DATE_EPOCH` | package.sh | last commit time | Zip mtime epoch for reproducible packaging |
| `VERSION` | package.sh | `git describe --tags --always --dirty` | Override for the zip version suffix (`EfficientServer-<VERSION>.zip`); a modified tree keeps an explicit `-dirty` suffix instead of the clean release name, whether the version came from `describe` or from this override. A tag-distance name (`1.19.0-5-gabc1234`) names no release and falls back to the short commit id, dirty mark kept; that name is exempt from the ModInfo check below, including when the hash is all digits. A release-form name must equal the `Version` in `Source/EfficientServer/ModInfo.xml`, since the zip is named for the tag while the game reads the version from the manifest; the run fails on a mismatch |
| `GC_FREE_SPACE_DIVISOR`, `GC_NPROCS`, `MONO_ENV_OPTIONS`, `MALLOC_ARENA_MAX` | run_server.sh | see `scripts/run_server.sh --help` | Boehm GC / Mono JIT tuning with A/B-measured defaults |
| `GC_INITIAL_HEAP_SIZE`, `GC_USE_ENTIRE_HEAP` | run_server.sh | unset | Optional GC headroom knobs (see script comments) |
| `GC_PAUSE_TIME_TARGET` | run_server.sh | unset | Forwarded ONLY together with `SEVENDTD_GC_INCREMENTAL`; ignored otherwise |

Rebuild after **every** Steam update. Re-check Harmony targets against `Assembly-CSharp` (see [`ARCHITECTURE.md`](ARCHITECTURE.md)).

### Releases

The GitHub release tag numbers the repo release (first cut: `v0.1.0`). The
mod's own version (`ModInfo.xml`, pinned by `check_version.py` in `make test`)
is what the server log reports, and a `vX.Y.Z` tag must equal it
(`.github/workflows/release.yml` fails the tag otherwise; the pre-1.18 tags are
the exceptions). The mapping and per-release changes are recorded in
[`CHANGELOG.md`](../CHANGELOG.md). Releasing is one commit that does all of:
rename `## [Unreleased]` to `## [<new mod version>] - <today>`, open a fresh
`## [Unreleased]` above it, bump `ModInfo.xml` plus `AssemblyInfo.cs` to the
same version, add that version's row to the `docs/RESULTS.md` version
history, and update the supported-version sentence in [`SECURITY.md`](../SECURITY.md).
`check_version.py` (in `make test`) fails when the newest release
section is not the version the mod reports, when sections are not newest-first,
when a section is undated or repeated, when a section carries two headings for
one impact level (or an unknown one), when the `docs/RESULTS.md`
version-history table has no row for the shipped version, and when
`SECURITY.md` does not name it, so notes, history, security promise and
manifest cannot be tagged out of sync.

Write entries for the operator, not the maintainer: what changed for a running
server, and, under `### Breaking`, what a configured key or console command now
does instead. A removed config key belongs under `### Breaking` with its
migration step, not only under `### Removed`: config load fails soft, so a
deleted key still parses and the lever is simply gone with no error to notice
it by. One heading per impact level per section: a second `### Fixed` in the same
release splits those entries in half, and a reader scanning the first list never
sees the rest.

`## [Unreleased]` is staging: an operator runs nothing described there yet, so a
config-facing change is announced only once its notes sit under a dated
section carrying the version the server log then reports (`versions: mod=...`).

```bash
make test        # CI gate; also runs on every PR / main push via .github/workflows/ci.yml
make package     # builds dist/EfficientServer and zips it (needs a game install)
gh release create v0.1.0 dist/EfficientServer-0.1.0.zip --title "EfficientServer 0.1.0" --notes "..."
```

Attach the zip's `sha256sum` to the release body. The zip is reproducible
(`make verify-reproducible`), so a consumer can rebuild it and compare; without
a published digest there is nothing to compare against. That check needs `dist/`
to hold exactly the zip it just built, so it parks any zip already there and
puts it back on every exit path: a release zip waiting to be published survives
the check, including a run of it that is killed.

A published version is immutable: never re-upload a zip over an existing tag
or move that tag. Ship the fix forward instead, as a new mod version with its
own dated changelog section, and say in the new section which release it
replaces. Rolling back on a server means installing the older zip (or the
older mod version) and restoring the config the failed install preserved. A
mod version older than the installed config still starts: an unrecognized key
is reported once at load (`config unknown key '...' ignored ...`) and the rest
of the file still applies.

NuGet dependencies are hash-pinned by the committed `packages.lock.json` of
both package graphs, `Source/EfficientServer.Tests/` and
`Source/EfficientServer/`; `make test` and `make build` restore in
locked mode, so bumping a `PackageReference` requires regenerating that file
with `dotnet restore Source/EfficientServer.Tests` or
`dotnet restore Source/EfficientServer` (plain, not locked) and
committing it together with the version change. Restore sources are pinned in
`NuGet.config` (nuget.org only, inherited machine and user feeds cleared);
add a source there, in the same change that needs it.

`make package` must run on a machine with the game installed: `build.sh`
compiles against the shipped `Assembly-CSharp.dll`, which the repo does not
redistribute (AGENTS.md rule 6). GitHub Actions therefore runs the test gate
but not the package build. The zip is reproducible (sorted entries,
SOURCE_DATE_EPOCH-normalized mtimes, stripped owner data); verify with
`make verify-reproducible`, which packages twice, recompiles from scratch at
a copied tree path, and compares hashes (the manual equivalent:
two `make package` runs plus `sha256sum dist/EfficientServer-*.zip`).

Each zip carries `EfficientServer/LICENSE.txt`, so redistributed copies
are self-contained under the MIT license terms.

### Validation tooling (scripts/)

Offline gates run by `make test` and CI. Live-server harnesses need a running dedicated server plus the `7dtd-loadgen` sibling, so `make test` only syntax-checks them (`compileall`); the shell build/install scripts are `make` targets, not gates.

| Script | Role |
|---|---|
| `repo_root.py` | Shared repository-root lookup (marker walk, not `parent.parent`) used by the gates below; selftest pins the walk |
| `cli_common.py` | Shared argument dispatch (`-h`/`--help`, `--selftest`, unknown-argument exit 2) for the gates below and every other script in this directory, plus `preflight_usage` for the three live harnesses, which answer `--help` before importing the loadgen sibling. Not an entry point |
| `check_config_doc.py` | Regression gate (in `make test`): every `ServerPerfConfig` field must be documented in CONFIG.md; selftest pins its parsing/comparison logic |
| `check_version.py` | Regression gate (in `make test`): ModInfo (source, plus dist when it has been packaged) == AssemblyVersion, no doc claims a future minor, the CHANGELOG release list is dated/newest-first, carries at most one heading per impact level per section and matches the shipped mod version, RESULTS.md's version-history table has a row for it, and SECURITY.md's supported-versions section names it; selftest pins version extraction/normalization and both changelog structure rules |
| `es_cfg_guard.py` | Config swap/restore primitive: snapshot the installed `efficientserver.json` before a harness mutates it, and restore it on every exit path (a SIGKILLed run's interrupted restore is finished, or its backup quarantined, by the NEXT run); selftest pins the guard protocol, fixture by fixture and then over seeded-random hostile configs (operator-edited bytes, leftover backups, random step order) against the protocol invariants |
| `bench_parse.py` | The parsers for text this repo does not write: the `[7dtd-server-apm]` health line out of the Unity server log (incremental tailing reader + windowed-rate reconstruction) and the `es animstate` console dump. They sit here, not in the harnesses that use them, because the harness import needs the `7dtd-loadgen` sibling and no gate can reach a parser behind it. Selftest pins the known-good line shapes, then fuzzes both: the log reader is judged differentially against a full-file rescan after every append (the incremental offset, carry buffer and remembered value must never disagree with the file), and both must stay fail-soft, so a malformed field is skipped whole rather than raised or half-applied |
| `apm_tail.py` | Standalone copy of the APM reader and window math, split out of `measure_es_onoff.py` (which cannot be imported without a live server, so the tail cache would have had no test) and since superseded by `bench_parse.py`. Nothing imports it; `make check-scripts` still runs its selftest. Not an entry point |
| `backup_config.py` | Operator backup of the host-only config (production host, not `make test`): stamped off-host snapshot of the live `Config/` plus the live `serverconfig*.xml`, `--verify` sample-restore drill (`--max-age-hours` also fails a job that stopped running), `--restore` with `--item` to pick one file or `all`; refuses a destination inside the install tree; selftest pins the snapshot/verify/restore protocol |
| `coverage_badge.py` | Renders the Cobertura report from `make coverage` into a badge SVG (CI publishes it to the `badges` branch, which the README badge references); selftest pins the percentage and colour bands |
| `selftest_support.py` | PASS/FAIL collector the selftests above share, so the result line and exit code are one spelling. Not an entry point |
| `harness_common.py` | Shared plumbing for the three live harnesses below: loadgen import path, env-driven paths, readiness probe, report writer. Not an entry point |
| `validate_anim_path_admission.py` | Live A/B: animator-emergency + path-admission against real bots/zombies (telnet + loadgen); see RESULTS |
| `validate_bloodmoon_path.py` | Live blood-moon path-admission A/B: real director-spawned horde, baseline vs path knobs on; writes a JSON report |
| `measure_es_onoff.py` | Live whole-mod ES on/off APM compare; `ES_ARM=on|off` = matched-arm mode (fresh server per arm) |
| `verify_reproducible.sh` (`make verify-reproducible`) | Rebuild-and-compare proof of the packaging reproducibility claim: same-tree repackage, full recompile, out-of-tree path variation; parks and restores the zips already in `dist/`; needs a game install |
| `build.sh` / `install.sh` / `uninstall.sh` / `package.sh` / `run_server.sh` | `make build` / `install` / `uninstall` / `package` / `run`; the only scripts that need a game install (`verify_reproducible.sh` is listed on its own row above) |
| `repo_lock.sh` | Sourced, not executed. `repo_lock <name>` takes an `flock` lock under `.scratch/locks/`, held until the calling script exits. `build.sh` takes `build` (over `dist/EfficientServer` and the intermediate `obj`/`bin`), `install.sh` and `uninstall.sh` take `install` (over the installed `Mods/EfficientServer/`). All of those paths are `rm -rf`ed and rewritten, so a second run in another shell, or a scheduled `make package` landing on a `make install`, would otherwise delete a tree the first run is still writing. Separate lock names, always taken build-inside-install, so the two cannot deadlock. Bounded wait: a holder past `REPO_LOCK_TIMEOUT` is reported, not waited on forever. A host without `flock(1)` is warned, not silently left unlocked |

Known infra note: >12 loadgen bots can trigger a stock LiteNetLib join flake
(`Collection was modified` in `CreateEvent`) that drops clients; use small
cohorts for measurement runs (see RESULTS). **Root cause closed 2026-08-10
(7dtd-engine-research):** a managed race - `LiteNetLibAuthWrapperServer.
ConnectionRequestCheck` enumerates `ConnectionManager.Clients.List` on the
socket-receive thread (`UnsyncedEvents=true` from `NetworkCommonLiteNetLib.
InitConfig`) while the main thread mutates it. Fix direction: run the
duplicate-IP scan on the main thread or copy the IP set under lock. Full
evidence: `7dtd-engine-research/docs/network/network.md` §4.0; a second churn bug
(`NetPackageMinEventFire.write` NRE on null itemValue) is in
`7dtd-engine-research/docs/network/protocol-packages.md` §6.23.

## Reverse engineering helpers

| Path | Role |
|---|---|
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | Dedicated hot path notes (gmUpdate, AI, mesh, networking) |
| [`../../7dtd-engine-research/docs/loop/loop-gmupdate.md`](../../7dtd-engine-research/docs/loop/loop-gmupdate.md) | V3.0.1 gmUpdate phase map |
| [`../../7dtd-engine-research/docs/entities/entity-ai.md`](../../7dtd-engine-research/docs/entities/entity-ai.md) | Entity/AI/path/fall/net deep chain |
| [`../../7dtd-engine-research/tools/`](../../7dtd-engine-research/tools/) | **All RE dumpers** (general `src/` + legacy per-family `legacy/`), build + regen tests |
| [`../../7dtd-engine-research/docs/meta/re-methodology.md`](../../7dtd-engine-research/docs/meta/re-methodology.md) | How to RE: dump, read IL, reconstruct layouts |
| [`../../7dtd-engine-research/docs/INDEX.md`](../../7dtd-engine-research/docs/INDEX.md) | Index of all RE dump sets |
| [`../../7dtd-engine-research/docs/loop/loop.md`](../../7dtd-engine-research/docs/loop/loop.md) | Complete dedicated game/sim loop map + open gaps |
| [`OPTIMIZATION_CANDIDATES.md`](OPTIMIZATION_CANDIDATES.md) | Graded optim candidates (this project) |
| [`OPTIMIZATION_IDEAS.md`](OPTIMIZATION_IDEAS.md) | Optim idea map |
| Sibling `7dtd-server-apm` | Host + bridge evidence (not in this repo) |
| Sibling `7dtd-loadgen` | Controlled clients |

Narratives under `7dtd-engine-research/docs/`; IL under `7dtd-engine-research/il/` is **generated**. Regenerate after game updates; do not redistribute game IL.

```bash
cd ../7dtd-engine-research/tools && ./build.sh
mono bin/legacy/DumpGmUpdate.exe "$DS/7DaysToDieServer_Data/Managed/Assembly-CSharp.dll" ../il/gmUpdate-VERSION
```

## Research ideas (not commitments)

Broader levers (threading, I/O, net LOD, rejects): [`OPTIMIZATION_IDEAS.md`](OPTIMIZATION_IDEAS.md).
Promote nothing without APM + loadgen evidence and a FEATURES fidelity checklist.

## Host topology (not this DLL)

CPU affinity, CCD placement, NUMA bind, core isolation, IRQ steering, and
governors are **host ops**. Do not implement them inside EfficientServer.
Checklist and A/B procedure: [`HOST_TUNING.md`](HOST_TUNING.md). Prove wins with
the same APM + loadgen loop as for Harmony changes.

## Acceptance

Lower CPU alone is not enough. Keep a change only if:

1. APM comparison is valid (same workload shape, collectors, duration rules), and  
2. Fidelity checks for the touched systems still pass (see FEATURES.md).

Harmony id: `com.7dtd.efficientserver` (and optional sub-ids for late patches).
## Related docs

| Doc | Role |
|---|---|
| [FEATURES.md](FEATURES.md) | Feature groups |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Hot path |
| [HOST_TUNING.md](HOST_TUNING.md) | Topology ops |
| [MODDING_BEST_PRACTICES.md](../../MODDING_BEST_PRACTICES.md) | Workspace modding layers |

## Changelog

- **2026-07-19:** Ownership/related docs polish.
