# Changelog

Notable changes to the EfficientServer mod and its packaging, for server
admins who deploy it. Newest first; each released section matches a GitHub
release tag. Detail behind every entry: [docs/FEATURES.md](docs/FEATURES.md)
(behavior, fidelity checks) and [docs/CONFIG.md](docs/CONFIG.md) (options,
defaults, measured gains).

## Version numbering

Two independent version numbers apply to every release, by design:

- The **GitHub release tag** (`vX.Y.Z`) versions this repository's releases.
  Downloadable zips are named after it (`EfficientServer-<tag>.zip`).
- The **mod version** (`ModInfo.xml` / assembly version, currently `1.17.0`)
  tracks the feature history of the mod itself and is what the server log
  reports at startup (`versions: mod=...`). It is independent of the release
  tag; `scripts/check_version.py` (run by `make test`/CI) keeps it identical
  across source, dist copy, and AssemblyInfo, and rejects doc claims of
  versions that never shipped.

So `EfficientServer-0.1.0.zip` logging `mod=1.17.0` is correct, not drift.

## [Unreleased]

### Fixed
- `es reload` now fully honors the "re-enable without a restart" contract for
  the two apply-once groups it missed: the imperative dedicated skips
  (dynamic music, water splash, environment audio, ambient light spectrum)
  and opt-in GC incremental mode. Previously both were installed at game start
  only if the config was already enabled, so a disabled->enabled reload left
  them inactive until restart while every other group activated live. The
  re-apply is idempotent (Harmony replaces an identical patch method); the
  GC megapause diagnostic stays start-time-only by design.
- Opt-in GC megapause diagnostic: `Gc.WarmupSeconds` is now clamped to
  [0, 3600] and `GrowSeconds` to [1, 7200], and the sleep duration is computed
  in long math. Previously an unclamped warmup above ~2.1M seconds wrapped the
  milliseconds product negative and killed the probe with a misleading log.

### Added
- Supply-chain inventory: `make package` now embeds a deterministic CycloneDX
  1.5 SBOM at `EfficientServer/bom.json` in every release zip, generated from
  the committed `packages.lock.json` graph (component versions plus NuGet
  content hashes; game-provided libraries are marked not-bundled). All inputs
  are in-tree values, so it stays byte-identical across rebuilds and inside
  the `make verify-reproducible` guarantee. A selftest gate
  (`scripts/gen_sbom.py --selftest`) runs in `make test`. See the new
  "Supply chain" section in `SECURITY.md`.

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
  (the test gate performs no git operations).

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
- Network single-target fast send (default off): O(1) recipient lookup for the
  pure single-target send case; no wire change.
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
