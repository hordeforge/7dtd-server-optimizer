# EfficientServer docs index

Hub for this repository's docs. The [README](../README.md) covers install,
toolchain, and the measured-impact summary.

## Docs

| Doc | Role |
|---|---|
| [DEVELOPMENT.md](DEVELOPMENT.md) | Optimizer-only workflow and releases |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Dedicated hot-path summary |
| [FEATURES.md](FEATURES.md) | Feature groups and acceptance |
| [CONFIG.md](CONFIG.md) | Every option: mechanism, gameplay impact, measured gain |
| [RESULTS.md](RESULTS.md) | A/B ledger, session IDs, negative results |
| [OPTIMIZATION_CANDIDATES.md](OPTIMIZATION_CANDIDATES.md) | Graded optim candidates (canonical backlog) |
| [OPTIMIZATION_IDEAS.md](OPTIMIZATION_IDEAS.md) | Idea map (not commitments) |
| [bottlenecks.md](bottlenecks.md) | Ranked bottleneck catalog |
| [algorithms.md](algorithms.md) | Hot-subsystem algorithms and data structures |
| [measured-scaling.md](measured-scaling.md) | Live APM scaling laws |
| [runtime-tuning.md](runtime-tuning.md) | Runtime tuning surfaces |
| [allocation-reuse.md](allocation-reuse.md) | Allocation reuse / zero-alloc levers |
| [aggressive-optimizations.md](aggressive-optimizations.md) | Aggressive / unsafe optimization catalog |
| [NETWORK_OPTIMIZATION.md](NETWORK_OPTIMIZATION.md) | Network / serialization optimization plan |
| [PATHFINDING_OPTIMIZATION.md](PATHFINDING_OPTIMIZATION.md) | Pathfinding / nav-graph optimization plan |
| [ALLOCATION_UPSTREAM.md](ALLOCATION_UPSTREAM.md) | Upstream allocation reduction plan |
| [SIM_PARALLELISM.md](SIM_PARALLELISM.md) | Threading / extract / hot-path catalog |
| [SCALE_1000x10000.md](SCALE_1000x10000.md) | Extreme scale thought experiment |
| [HOST_TUNING.md](HOST_TUNING.md) | Host ops checklist (no DLL changes) |
| [PERF_RESEARCH_BRIEF.md](PERF_RESEARCH_BRIEF.md) | RE + APM research to optimizer backlog |
| [V310_APM_BASELINE.md](V310_APM_BASELINE.md) | V3.1.0 APM / loadgen evidence baseline |
| [PRODUCTION.md](PRODUCTION.md) | Deployment and operations runbook |
| [plans/animator-cull-and-path-admission.md](plans/animator-cull-and-path-admission.md) | Build plan + live gates: animator CullCompletely emergency, path admission |
| [evidence/](evidence/) | Stored A/B compare logs referenced by [V310_APM_BASELINE](V310_APM_BASELINE.md) |
| [THREAT_MODEL.md](THREAT_MODEL.md) | Attack surface, trust boundaries, threats, mitigations |

## Evidence sources (private workspace)

This repository is part of the private `7dtd` workspace. Many docs link to
sibling repositories and workspace files that are not public; those links
resolve only inside the workspace:

| Source | Role |
|---|---|
| `7dtd-engine-research` | Stock-game reverse engineering (`docs/`) and regenerable IL dumps (`il/`, git-ignored) |
| `7dtd-server-apm` | Profiler / APM bridge, capture, compare |
| `7dtd-loadgen` | LiteNetLib bots and dedicated start helpers |
| `MODDING_BEST_PRACTICES.md` | Workspace-root modding guide |

On the public GitHub page those links 404; inside the workspace they resolve
relative to this repo.
