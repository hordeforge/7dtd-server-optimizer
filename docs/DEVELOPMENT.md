# EfficientServer development

**Owns:** how to change EfficientServer (build, patch groups, evidence loop).  
**Not:** feature behavior detail ([FEATURES](FEATURES.md)), host ops ([HOST_TUNING](HOST_TUNING.md)).

**General 7DTD modding (layers, XPath, Harmony hygiene, packaging, EAC):**  
[`../../MODDING_BEST_PRACTICES.md`](../../MODDING_BEST_PRACTICES.md)

This file is **optimizer-only**: how to change EfficientServer without inventing a second modding guide.

## Scope of this project

| Owns | Does not own |
|---|---|
| Reviewed Harmony optimizers (AI LOD, task skip, dedicated skips, dynamic mesh budgets) | Profiler / APM (use `7dtd-apm`) |
| `config/efficientserver.json` feature groups | Load generation (use `7dtd-loadgen`) |
| Cecil dump helpers under `tools/` | Terrain / RealEarth product work |
| Dedicated-focused install scripts | Balance XML modlets |
| Links to host ops guidance | **CCD/NUMA/affinity** (ops only; see [`HOST_TUNING.md`](HOST_TUNING.md)) |

Default config: `DedicatedOnly: true`. Do not turn EfficientServer into a client overhaul or a measurement suite.

## Patch groups

See [`FEATURES.md`](FEATURES.md) for behavior and validation notes. Groups are applied independently from `ModApi` so one missing target should not kill the rest.

| Group | Config block | Intent |
|---|---|---|
| AI LOD | `AiLod` | Distant AI scale / distance bands |
| Task skip | (with AI LOD) | Distant non-alert `updateTasks` throttling |
| Dedicated skips | `SkipOnDedicated` | Presentation paths useless on headless |
| Dynamic mesh | `DynamicMesh` | Player-area / time budgets |
| GC pause guard | `Gc` | Skip forced periodic `GC.Collect`; host-aware safety collect; opt-in incremental mode |
| Pathfinding graph throttle | `Pathfinding` | Rate-limit `AstarManager.UpdateGraphs` via `GraphUpdateEveryTicks` |
| Game start reapply | (lifecycle) | Re-apply mesh settings after start |

Change **one group at a time**, then re-measure.

## Workflow

```text
1. Baseline: 7dtd-loadgen workload + 7dtd-apm capture
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

Rebuild after **every** Steam update. Re-check Harmony targets against `Assembly-CSharp` (see [`ARCHITECTURE.md`](ARCHITECTURE.md)).

## Reverse engineering helpers

| Path | Role |
|---|---|
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | Dedicated hot path notes (gmUpdate, AI, mesh, networking) |
| [`../../7dtd-research/docs/loop-gmupdate.md`](../../7dtd-research/docs/loop-gmupdate.md) | V3.0.1 gmUpdate phase map |
| [`../../7dtd-research/docs/entity-ai.md`](../../7dtd-research/docs/entity-ai.md) | Entity/AI/path/fall/net deep chain |
| [`../../7dtd-research/tools/`](../../7dtd-research/tools/) | **All RE dumpers** (general `src/` + legacy per-family `legacy/`), build + regen tests |
| [`../../7dtd-research/docs/re-methodology.md`](../../7dtd-research/docs/re-methodology.md) | How to RE: dump, read IL, reconstruct layouts |
| [`../../7dtd-research/docs/INDEX.md`](../../7dtd-research/docs/INDEX.md) | Index of all RE dump sets |
| [`../../7dtd-research/docs/loop.md`](../../7dtd-research/docs/loop.md) | Complete dedicated game/sim loop map + open gaps |
| [`OPTIMIZATION_CANDIDATES.md`](OPTIMIZATION_CANDIDATES.md) | Graded optim candidates (this project) |
| [`OPTIMIZATION_IDEAS.md`](OPTIMIZATION_IDEAS.md) | Optim idea map |
| Sibling `7dtd-apm` | Host + bridge evidence (not in this repo) |
| Sibling `7dtd-loadgen` | Controlled clients |

Narratives under `7dtd-research/docs/`; IL under `7dtd-research/il/` is **generated**. Regenerate after game updates; do not redistribute game IL.

```bash
cd ../7dtd-research/tools && ./build.sh
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
