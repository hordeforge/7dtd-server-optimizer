# AGENTS.md - 7dtd-server-optimizer (EfficientServer)

Harmony optimization mod for **7 Days to Die** dedicated servers (target **V3.1.0**).
Owns only reviewed runtime optim patches. Sibling projects own measurement and load.

Workspace root guide: [`hordeforge/.github` MODDING_BEST_PRACTICES.md](https://github.com/hordeforge/.github/blob/main/MODDING_BEST_PRACTICES.md)

## Scope

| Owns | Does not own |
|---|---|
| Reviewed Harmony patches (AI LOD, task throttle, dedicated skips, mesh budgets) | Profiler / APM (use `7dtd-server-apm`) |
| `config/efficientserver.json` feature groups | Load generation (use `7dtd-loadgen`) |
| Config/version gates + validators under `scripts/` | Terrain / RealEarth |
| Dedicated install/run scripts | CCD/NUMA/affinity (host ops only; see `docs/HOST_TUNING.md`) |
| Graded optim docs under `docs/` | Shipping cracked mods or redistributing game IL |

Default config: `DedicatedOnly: true`. Do not turn this into a client overhaul, measurement suite, or general admin mod.

## Critical rules

1. **One feature group at a time**, then re-measure. Never ship optim changes without APM + loadgen evidence when making performance claims.
2. **Lower CPU alone is not acceptance.** Keep fidelity checks for touched systems (`docs/FEATURES.md`).
3. **Rebuild and re-validate Harmony targets after every game update.** Targets break silently on Steam patches.
4. **Fail soft per group.** One missing target must not kill the whole mod (`PatchAllSafe` pattern).
5. **Do not implement host topology (affinity, NUMA, IRQ) inside the DLL.** Document under `docs/HOST_TUNING.md` only.
6. **Do not redistribute `Assembly-CSharp` or bulk game IL.** Dumps under `../7dtd-engine-research/il/` are regenerable evidence only; narratives live under `../7dtd-engine-research/docs/`.
7. **No AI attribution** in commits, docs, or comments. **No em dashes** in any text this project ships.
8. In-game mod DLL is **net48** against dedicated Managed; stock `0_TFP_Harmony` required; EAC off on test servers.

## Build / install

```bash
make test   # every CI gate: shellcheck + ruff + mypy + script syntax + .NET unit tests + config harness + doc/version consistency
make build
make install DS="/path/to/7 Days to Die Dedicated Server"
make run DS="/path/to/7 Days to Die Dedicated Server"
make uninstall DS="/path/to/7 Days to Die Dedicated Server"
make package   # reproducible dist/EfficientServer-<version>.zip
make clean
# optional Mono mcs path:
make build-mcs
```

Default dedicated root: `~/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server`.

Source: `Source/EfficientServer/`. Packaged mod name: `Mods/EfficientServer/`. Harmony id: `com.7dtd.efficientserver`.

## Evidence loop (required for optim claims)

```text
7dtd-loadgen workload → 7dtd-server-apm baseline → one EfficientServer change
  → same workload → APM compare + budget + gameplay soak
```

## Docs map

| Path | Role |
|---|---|
| `docs/DEVELOPMENT.md` | Optimizer-only workflow |
| `docs/ARCHITECTURE.md` | Dedicated hot-path RE summary |
| `docs/FEATURES.md` | Feature groups and acceptance |
| `docs/OPTIMIZATION_CANDIDATES.md` | Graded candidates (canonical optim backlog) |
| `docs/OPTIMIZATION_IDEAS.md` | Idea map (not commitments) |
| `docs/SIM_PARALLELISM.md` | Threading / extract / hot-path catalog |
| `docs/SCALE_1000x10000.md` | Extreme scale thought experiment |
| `docs/HOST_TUNING.md` | Host ops checklist |
| `../7dtd-engine-research/docs/loop.md` | Full dedicated loop RE map |
| `../7dtd-engine-research/docs/INDEX.md` | Research docs + dump index |
| `TODO.md` | Phased implementation plan |

## Sibling projects

| Project | Role |
|---|---|
| `../7dtd-server-apm` | Host + bridge measurement, compare, budget |
| `../7dtd-loadgen` | LiteNetLib bots and dedicated start helpers |
| `../7dtd-realearth` | RealEarth terrain (unrelated optim product surface) |

Do not silently install, edit, or couple into siblings. Public runner/API only.

## Stock-game research and RE dumps

Anything that studies the **stock** dedicated server belongs in
[`../7dtd-engine-research/`](../7dtd-engine-research/), not here: reverse-engineering
narratives (`docs/`), the Mono.Cecil dump tooling (`tools/`: general dumpers in `src/`,
legacy per-family dumpers in `legacy/`, regen test in `tools/tests/`), wire/protocol
analysis, and engine cost/loop RE. Add new RE there and link back; how:
[`docs/re-methodology.md`](../7dtd-engine-research/docs/re-methodology.md). Build +
usage of the dumpers: [`tools/README.md`](../7dtd-engine-research/tools/README.md).
Regenerate dumps with:

```bash
cd ../7dtd-engine-research/tools && ./build.sh
mono bin/legacy/DumpGmUpdate.exe "$DS/7DaysToDieServer_Data/Managed/Assembly-CSharp.dll" \
  ../il/gmUpdate-VERSION
```

Human synthesis belongs in `../7dtd-engine-research/docs/` or `docs/`, never as optim product narrative inside dump folders. Automated check: `../7dtd-engine-research/tools/tests/test_re_dump_regen.py` (needs dedicated install + mcs/mono).
