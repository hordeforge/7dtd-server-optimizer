# 7dtd-optimizer

![CI](https://github.com/hordeforge/7dtd-server-optimizer/actions/workflows/ci.yml/badge.svg)

EfficientServer is a focused Harmony optimization mod for 7 Days to Die
dedicated servers. It owns only reviewed runtime changes: AI level-of-detail,
distant task throttling, dedicated-only work suppression, bounded dynamic mesh
behavior, pathfinding and network throttles, and an adaptive overload governor
with opt-in emergency shedding.

> **Workspace context:** this repo is part of a private `7dtd` workspace.
> Docs link to sibling repos (`7dtd-research`, `7dtd-apm`, `7dtd-loadgen`)
> and workspace files that are not public. Those links resolve only inside
> the workspace; on the public GitHub page they 404.

It intentionally contains no profiler and no load generator. Install the
standalone bridge from sibling `7dtd-apm` for managed instrumentation, and use
sibling `7dtd-loadgen` for repeatable clients.

```bash
make help        # every target, grouped: contributor loop vs game-backed
make build
make install DS="/path/to/7 Days to Die Dedicated Server"
make run DS="/path/to/7 Days to Die Dedicated Server"
```

Contributing: [`CONTRIBUTING.md`](CONTRIBUTING.md) (PR gates are `make test`,
the same command CI runs).

Configuration is in [`config/efficientserver.json`](config/efficientserver.json).
Change one feature group at a time and validate it with the same loadgen
manifest and compatible APM capture. Optimizations can change simulation
fidelity, so lower CPU time alone is not sufficient acceptance evidence.

Source is under `Source/EfficientServer`; packaging and server launch helpers
are under `scripts`. Rebuild and revalidate exact Harmony targets after every
game update.

Packaged builds are attached to GitHub releases (see the Releases page;
`make package` produces `dist/EfficientServer-<tag>.zip`; what changed per
release: [`CHANGELOG.md`](CHANGELOG.md)). Packaging is
reproducible: sorted entries, normalized mtimes/permissions, no owner data;
timestamps honor `SOURCE_DATE_EPOCH` (falling back to the last commit time),
so two builds of the same tree zip byte-identically. `make verify-reproducible`
proves it by rebuilding from scratch at a second path and comparing hashes,
and every package run records its toolchain in
`dist/EfficientServer-*.buildinfo.txt`. Each zip ships its own supply-chain
inventory (`EfficientServer/bom.json`, deterministic CycloneDX 1.5 generated
from the committed lock file; see [`SECURITY.md`](SECURITY.md)). CI runs
`make test` on every PR and on pushes to main.

## Measured impact (v1.17.x)

- **Eliminates the GC megapause:** worst stop-the-world **274 ms -> 0**, full
  collections **3 -> 0** in the aggregate A/B window (vanilla lost 5.5 ticks at once
  to one freeze). Tick-stall total **-28%**; pathfinding graph work **-27%**.
- **At a breaking load** (vanilla tick-starved), the pathfinding throttle alone is
  **-28.5% ms/tick**, pulling the server from failing back to healthy.
- **Adaptive governor** (default on, inert while healthy): under overload it engages
  the measured throttles (each lever doubled from its configured baseline: stride 2
  = -45% on that wall, doubled graph cadence), cushioning a 435-zombie overload at
  128 vs 299 ms/frame and
  self-restoring. **Raises sustained blood-moon capacity from ~147 to ~232 endgame
  zombies at 64 players (+58%).**
- **TickGuard** (opt-in): last-resort shedding of the farthest zombies - a 522-zombie
  overload (3.5x the ceiling) recovered from 167 to 56 ms/frame autonomously.
- **Governor tier 2 (opt-in):** during extreme overload, zombie animators off =
  **~40% of the saturated 64-player frame** recovered (the frame is half main-thread
  job-fence waiting; animation jobs are the dominant fence source). Client-invisible;
  combat timing degrades; nothing despawns.
- **Per-tick compute is flat by design:** the entity tick (close-combat AI, fully
  serial, frame-amortized) and network replication (O(N^2.26), 20 Hz-locked) are the
  measured engine walls; every remaining millisecond of the tick is attributed
  (zero dark matter, RESULTS 3h), and the engine-side masses are named (RESULTS
  3m-3p).

Full ledger with session IDs, per-lever numbers, and honest negative results
(what was tried and refuted): [`docs/RESULTS.md`](docs/RESULTS.md). Per-option
reference: [`docs/CONFIG.md`](docs/CONFIG.md). Deploying:
[`docs/PRODUCTION.md`](docs/PRODUCTION.md).

Docs:

- Local docs hub: [`docs/INDEX.md`](docs/INDEX.md)
- Workspace modding guide: [`../MODDING_BEST_PRACTICES.md`](../MODDING_BEST_PRACTICES.md)
- EfficientServer workflow: [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md)
- Hot path RE: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
- Host CCD/NUMA/affinity: [`docs/HOST_TUNING.md`](docs/HOST_TUNING.md)
- Optimization idea map: [`docs/OPTIMIZATION_IDEAS.md`](docs/OPTIMIZATION_IDEAS.md)
- Optimization candidates (graded): [`docs/OPTIMIZATION_CANDIDATES.md`](docs/OPTIMIZATION_CANDIDATES.md)
- Network/serialization optimization plan: [`docs/NETWORK_OPTIMIZATION.md`](docs/NETWORK_OPTIMIZATION.md)
- Upstream allocation reduction plan (the real GC lever): [`docs/ALLOCATION_UPSTREAM.md`](docs/ALLOCATION_UPSTREAM.md)
- Production deployment + operations runbook: [`docs/PRODUCTION.md`](docs/PRODUCTION.md)
- Config reference (every option: mechanism, gameplay impact, measured gain): [`docs/CONFIG.md`](docs/CONFIG.md)
- Results ledger (every lever, A/B numbers, session IDs, config): [`docs/RESULTS.md`](docs/RESULTS.md)
- Pathfinding / nav-graph optimization plan: [`docs/PATHFINDING_OPTIMIZATION.md`](docs/PATHFINDING_OPTIMIZATION.md)
- Scale thought experiment (1k players / 10k AI): [`docs/SCALE_1000x10000.md`](docs/SCALE_1000x10000.md)
- Sim threading, extract-off-main, hot-path catalog: [`docs/SIM_PARALLELISM.md`](docs/SIM_PARALLELISM.md)
- Feature groups: [`docs/FEATURES.md`](docs/FEATURES.md)
- Bottleneck catalog (ranked, IL+APM verified): [`docs/bottlenecks.md`](docs/bottlenecks.md)
- Algorithms & data structures of every hot subsystem: [`docs/algorithms.md`](docs/algorithms.md)
- Measured scaling laws (live APM): [`docs/measured-scaling.md`](docs/measured-scaling.md)
- Runtime tuning surfaces: [`docs/runtime-tuning.md`](docs/runtime-tuning.md)
- Allocation reuse / zero-alloc levers: [`docs/allocation-reuse.md`](docs/allocation-reuse.md)
- Aggressive / unsafe optimization catalog: [`docs/aggressive-optimizations.md`](docs/aggressive-optimizations.md)
- Perf research brief (RE + APM → optimizer backlog): [`docs/PERF_RESEARCH_BRIEF.md`](docs/PERF_RESEARCH_BRIEF.md)
- V3.1.0 APM / loadgen evidence baseline: [`docs/V310_APM_BASELINE.md`](docs/V310_APM_BASELINE.md)
- OSS tools survey (research): [`../7dtd-research/oss-tools/NOTES.md`](../7dtd-research/oss-tools/NOTES.md)
- Dedicated game loop RE map: [`../7dtd-research/docs/loop.md`](../7dtd-research/docs/loop.md)
- RE dump index: [`../7dtd-research/docs/INDEX.md`](../7dtd-research/docs/INDEX.md)
- Backlog: [`TODO.md`](TODO.md)

## Supported versions and troubleshooting

**Game pin:** 7 Days to Die **V3.1.0 (b14)** dedicated. Built against the live
`Assembly-CSharp.dll` at build time (dedicated `7DaysToDieServer_Data/Managed`
first, client fallback), so a Steam update that changes the assembly is a
supported retarget: rebuild with `make build` and reinstall.

**Toolchain:**
- .NET SDK pinned by [`global.json`](global.json) (8.0.4xx band,
  `rollForward: latestFeature`; CI installs exactly that), target framework
  `net48`; `build.sh` prefers `DOTNET_ROOT` or `~/.cache/dotnet-sdk`
- Fallback backend `SEVENDTD_BUILD_BACKEND=mcs` (Mono `mcs`) when no SDK is present
- Host OS: the build/run/package tooling targets **Linux** hosts (Steam library
  paths, GNU coreutils, `taskset`); the packaged DLL itself is OS-neutral
  managed code loaded by the game's own runtime on any dedicated-server host
- `make test` additionally needs `shellcheck` and Python 3 (both preinstalled on
  GitHub runners; the SDK comes from `global.json`)
- Requires `0_TFP_Harmony` installed in the game's `Mods/` (the Harmony runtime
  the patches load through)
- Game refs (Assembly-CSharp, UnityEngine.*, 0Harmony, Newtonsoft.Json,
  LogLibrary, AstarPathfindingProject) resolve from the installed game; a
  missing managed DLL fails the build with a clear reference error
- The test project's one NuGet dependency is hash-pinned in the committed
  `packages.lock.json` and restored in locked mode by `make test`

**Troubleshooting (from the mod's own log lines):**
- `MISSING TARGET: <Patch> matched no game method (version drift?) - this
  optimization is INACTIVE` - the patch could not IL-match its target. Almost
  always a game update moved/renamed the method: rebuild against the current
  `Assembly-CSharp.dll`, and if it persists, open an issue with the patch name.
- `InitMod failed:` / `patch <name> failed: <ex>` - an exception during mod init
  or Harmony patching. Check the full stack in the server log; common causes are
  a missing Harmony install or a partial game update.
- Config edits not taking effect - the file is read at startup. Reinstall now
  preserves a user-edited `efficientserver.json` across upgrades (differs from
  the shipped default), so edits survive; a server restart is still required
  after changing it.
- EAC: C# mods (and therefore this one) need EAC disabled on the server.

See [`docs/PRODUCTION.md`](docs/PRODUCTION.md) for the full deploy/operate
runbook and [`docs/CONFIG.md`](docs/CONFIG.md) for every option.
