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

## Patch groups

See [`FEATURES.md`](FEATURES.md) for behavior and validation notes. Groups are applied independently from `ModApi` so one missing target should not kill the rest.

| Group | Config block | Intent |
|---|---|---|
| AI LOD | `AiLod` | Distant AI scale / distance bands |
| Task skip | (with AI LOD) | Distant non-alert `updateTasks` throttling |
| Dedicated skips | `SkipOnDedicated` | Presentation paths useless on headless (incl. `ExplosionParticles`, ambient spectrum) |
| Dynamic mesh | `DynamicMesh` | Player-area / time budgets |
| GC pause guard | `Gc` | Skip forced periodic `GC.Collect`; host-aware safety collect; opt-in incremental mode |
| Pathfinding graph throttle | `Pathfinding.GraphUpdateEveryTicks` | Rate-limit `AstarManager.UpdateGraphs` |
| Move rescan threshold | `Pathfinding.MoveRescanThresholdSq` | Widen the grid rescan dead-zone (fewer `InitScan`) |
| Path admission | `Pathfinding.MaxPathEnqueuesPerTick` / `DropPathWhenFarDistSq` | Cap / drop far non-priority path enqueues |
| InitScan node pool | `Pathfinding.PoolInitScanNodes` | UNSAFE: reuse nav node array across scans |
| Fast single-target send | `Network.FastSingleTargetSend` | O(1) recipient lookup in `SendPackage` |
| Replication stride | `Network.EntityDistributionEveryTicks` | Run the replication pass every Nth tick |
| Chunk-send throttle | `WorldTransfer.ChunkPackagesPerObserverPerTick` | Cap chunk packages per observer per tick |
| Target FPS | `Server.TargetFps` | Persistent frame-rate set at game start |
| Animator LOD | `AnimatorLod` | Reduced-rate animation for calm distant zombies |
| Crowd-collision LOD | `CrowdCollisionLod` | Stagger zombie entity-collision queries |
| Governor | `Governor` | Adaptive engagement of the throttle levers under overload (+ opt-in tier 2) |
| TickGuard | `TickGuard` | Last-resort emergency load shedding |
| BenchGod | console `es benchgod` | BENCH ONLY diagnostic (player damage immunity; arming needs `Diagnostics.AllowBenchGod: true`) |
| Game start reapply | (lifecycle) | Re-apply mesh settings after start |

Change **one group at a time**, then re-measure.

## Workflow

```text
0. make test - shellcheck + ruff lint gates, mypy type gate, config harness
   (normalize/clamps/invariants/fuzz), config-doc
   coverage gate, version-consistency gate (ModInfo == Assembly == docs);
   also runs in CI on every PR and on pushes to main (.github/workflows/ci.yml)
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
| `SEVENDTD_DS_DIR` / make `DS=` | build.sh, install.sh, run_server.sh, make `uninstall`, harness scripts (`measure_es_onoff.py`, `validate_*`) | `~/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server` | Dedicated install: game DLL refs, mod install target, launch dir. Harnesses also accept `SEVENDTD_SERVER_DIR` (the 7dtd-loadgen sibling's spelling) |
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
| `SOURCE_DATE_EPOCH` | package.sh | last commit time | Zip mtime epoch for reproducible packaging |
| `VERSION` | package.sh | `git describe --tags --always --dirty` | Override for the zip version suffix (`EfficientServer-<VERSION>.zip`); a modified tree keeps an explicit `-dirty` suffix instead of the clean release name |
| `GC_FREE_SPACE_DIVISOR`, `GC_NPROCS`, `MONO_ENV_OPTIONS`, `MALLOC_ARENA_MAX` | run_server.sh | see script header | Boehm GC / Mono JIT tuning with A/B-measured defaults |
| `GC_INITIAL_HEAP_SIZE`, `GC_USE_ENTIRE_HEAP` | run_server.sh | unset | Optional GC headroom knobs (see script comments) |
| `GC_PAUSE_TIME_TARGET` | run_server.sh | unset | Forwarded ONLY together with `SEVENDTD_GC_INCREMENTAL`; ignored otherwise |

Rebuild after **every** Steam update. Re-check Harmony targets against `Assembly-CSharp` (see [`ARCHITECTURE.md`](ARCHITECTURE.md)).

### Releases

The GitHub release tag numbers the repo release (first cut: `v0.1.0`). The
mod's own version (`ModInfo.xml`, pinned by `check_version.py` in `make test`)
tracks the target game baseline and is independent of the release tag. The
mapping and per-release changes are recorded in
[`CHANGELOG.md`](../CHANGELOG.md): move `[Unreleased]` items under the new
version before tagging (`check_version.py` fails if the shipped mod version has
no changelog entry).

```bash
make test        # CI gate; also runs on every PR / main push via .github/workflows/ci.yml
make package     # builds dist/EfficientServer and zips it (needs a game install)
gh release create v0.1.0 dist/EfficientServer-0.1.0.zip --title "EfficientServer 0.1.0" --notes "..."
```

NuGet dependencies are hash-pinned by the committed
`Source/EfficientServer.Tests/packages.lock.json`; `make test` restores in
locked mode, so bumping a `PackageReference` requires regenerating that file
with `dotnet restore Source/EfficientServer.Tests` (plain, not locked) and
committing it together with the version change.

`make package` must run on a machine with the game installed: `build.sh`
compiles against the shipped `Assembly-CSharp.dll`, which the repo does not
redistribute (AGENTS.md rule 6). GitHub Actions therefore runs the test gate
but not the package build. The zip is reproducible (sorted entries,
SOURCE_DATE_EPOCH-normalized mtimes, stripped owner data); verify with
`make verify-reproducible`, which packages twice, recompiles from scratch at
a copied tree path, and compares hashes (the manual equivalent:
two `make package` runs plus `sha256sum dist/EfficientServer-*.zip`). Each
package run also writes `dist/EfficientServer-<version>.buildinfo.txt`
(toolchain versions, commit, epoch, zip hash) next to the zip so any release
artifact records the environment that produced it; the buildinfo lives
outside the zip to keep artifacts byte-identical.

Each zip also embeds `EfficientServer/bom.json`, a deterministic CycloneDX 1.5
SBOM generated from the committed `packages.lock.json` by `gen_sbom.py`
(component versions plus NuGet content hashes; game-provided libraries are
marked not-bundled). It is part of the reproducibility guarantee above. The
zip (and any installed mod directory, since both flow from `build.sh`'s dist
output) also carries `EfficientServer/LICENSE.txt`, so redistributed copies
are self-contained under the MIT license terms. The
supply-chain posture this documents: [`SECURITY.md`](../SECURITY.md),
"Supply chain".

### Validation tooling (scripts/)

| Script | Role |
|---|---|
| `check_config_doc.py` | Regression gate (in `make test`): every `ServerPerfConfig` field must be documented in CONFIG.md; selftest pins its parsing/comparison logic |
| `check_version.py` | Regression gate (in `make test`): ModInfo (source+dist) == AssemblyVersion, no doc claims a future minor; selftest pins version extraction/normalization |
| `gen_sbom.py` | Release SBOM generator (called by `package.sh`; selftest in `make test`): deterministic CycloneDX 1.5 inventory from packages.lock.json |
| `verify_reproducible.sh` (`make verify-reproducible`) | Rebuild-and-compare proof of the packaging reproducibility claim: same-tree repackage, full recompile, out-of-tree path variation; needs a game install |
| `validate_anim_path_admission.py` | Live A/B: animator-emergency + path-admission against real bots/zombies (telnet + loadgen); see RESULTS |
| `validate_bloodmoon_path.py` | Live blood-moon path-admission A/B: real director-spawned horde, baseline vs path knobs on; writes a JSON report |
| `measure_es_onoff.py` | Live whole-mod ES on/off APM compare; `ES_ARM=on|off` = matched-arm mode (fresh server per arm) |

Known infra note: >12 loadgen bots can trigger a stock LiteNetLib join flake
(`Collection was modified` in `CreateEvent`) that drops clients; use small
cohorts for measurement runs (see RESULTS). **Root cause closed 2026-08-10
(7dtd-engine-research):** a managed race - `LiteNetLibAuthWrapperServer.
ConnectionRequestCheck` enumerates `ConnectionManager.Clients.List` on the
socket-receive thread (`UnsyncedEvents=true` from `NetworkCommonLiteNetLib.
InitConfig`) while the main thread mutates it. Fix direction: run the
duplicate-IP scan on the main thread or copy the IP set under lock. Full
evidence: `7dtd-engine-research/docs/network.md` §4.0; a second churn bug
(`NetPackageMinEventFire.write` NRE on null itemValue) is in
`7dtd-engine-research/docs/protocol-packages.md` §6.23.

## Reverse engineering helpers

| Path | Role |
|---|---|
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | Dedicated hot path notes (gmUpdate, AI, mesh, networking) |
| [`../../7dtd-engine-research/docs/loop-gmupdate.md`](../../7dtd-engine-research/docs/loop-gmupdate.md) | V3.0.1 gmUpdate phase map |
| [`../../7dtd-engine-research/docs/entity-ai.md`](../../7dtd-engine-research/docs/entity-ai.md) | Entity/AI/path/fall/net deep chain |
| [`../../7dtd-engine-research/tools/`](../../7dtd-engine-research/tools/) | **All RE dumpers** (general `src/` + legacy per-family `legacy/`), build + regen tests |
| [`../../7dtd-engine-research/docs/re-methodology.md`](../../7dtd-engine-research/docs/re-methodology.md) | How to RE: dump, read IL, reconstruct layouts |
| [`../../7dtd-engine-research/docs/INDEX.md`](../../7dtd-engine-research/docs/INDEX.md) | Index of all RE dump sets |
| [`../../7dtd-engine-research/docs/loop.md`](../../7dtd-engine-research/docs/loop.md) | Complete dedicated game/sim loop map + open gaps |
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
