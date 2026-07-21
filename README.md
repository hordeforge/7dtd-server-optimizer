# 7dtd-optimizer

EfficientServer is a focused Harmony optimization mod for 7 Days to Die
dedicated servers. It owns only reviewed runtime changes: AI level-of-detail,
distant task throttling, dedicated-only work suppression, and bounded dynamic
mesh behavior.

It intentionally contains no profiler and no load generator. Install the
standalone bridge from sibling `7dtd-apm` for managed instrumentation, and use
sibling `7dtd-loadgen` for repeatable clients.

```bash
make build
make install DS="/path/to/7 Days to Die Dedicated Server"
make run DS="/path/to/7 Days to Die Dedicated Server"
```

Configuration is in [`config/efficientserver.json`](config/efficientserver.json).
Change one feature group at a time and validate it with the same loadgen
manifest and compatible APM capture. Optimizations can change simulation
fidelity, so lower CPU time alone is not sufficient acceptance evidence.

Source is under `Source/EfficientServer`; packaging and server launch helpers
are under `scripts`. Rebuild and revalidate exact Harmony targets after every
game update.

## Measured impact

Aggregate A/B, true vanilla vs everything-on (full safe mod +
`GC_FREE_SPACE_DIVISOR=1` launch env), matched heavy load ~320 zombies + 32 bots
(2026-07-20, v1.8.0):

- **Eliminates the GC megapause:** worst stop-the-world **274 ms -> 0**, full
  collections **3 -> 0** in the window (vanilla lost 5.5 ticks at once to one freeze).
- **Tick-stall total -28%**, pathfinding graph work (`UpdateGraphs`) **-27%**.
- **Per-tick compute flat:** the entity tick (close-combat AI) and network
  replication (O(N^2.26)) are irreducible walls; the mod's win at surviving load is
  smoothness, not raw throughput.
- **At a breaking load** (vanilla tick-starved), the pathfinding throttle alone is
  **-28.5% ms/tick**, pulling the server from failing back to healthy.

Full ledger with session IDs and per-lever numbers: [`docs/RESULTS.md`](docs/RESULTS.md).

Docs:

- Workspace modding guide: [`../MODDING_BEST_PRACTICES.md`](../MODDING_BEST_PRACTICES.md)
- EfficientServer workflow: [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md)
- Hot path RE: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
- Host CCD/NUMA/affinity: [`docs/HOST_TUNING.md`](docs/HOST_TUNING.md)
- Optimization idea map: [`docs/OPTIMIZATION_IDEAS.md`](docs/OPTIMIZATION_IDEAS.md)
- Optimization candidates (graded): [`docs/OPTIMIZATION_CANDIDATES.md`](docs/OPTIMIZATION_CANDIDATES.md)
- Network/serialization optimization plan: [`docs/NETWORK_OPTIMIZATION.md`](docs/NETWORK_OPTIMIZATION.md)
- Upstream allocation reduction plan (the real GC lever): [`docs/ALLOCATION_UPSTREAM.md`](docs/ALLOCATION_UPSTREAM.md)
- Config reference (every option: mechanism, gameplay impact, measured gain): [`docs/CONFIG.md`](docs/CONFIG.md)
- Results ledger (every lever, A/B numbers, session IDs, config): [`docs/RESULTS.md`](docs/RESULTS.md)
- Pathfinding / nav-graph optimization plan: [`docs/PATHFINDING_OPTIMIZATION.md`](docs/PATHFINDING_OPTIMIZATION.md)
- Scale thought experiment (1k players / 10k AI): [`docs/SCALE_1000x10000.md`](docs/SCALE_1000x10000.md)
- Sim threading, extract-off-main, hot-path catalog: [`docs/SIM_PARALLELISM.md`](docs/SIM_PARALLELISM.md)
- Feature groups: [`docs/FEATURES.md`](docs/FEATURES.md)
- OSS tools survey (research): [`../research/oss-tools/NOTES.md`](../research/oss-tools/NOTES.md)
- Dedicated game loop RE map: [`../research/docs/loop.md`](../research/docs/loop.md)
- RE dump index: [`../research/docs/INDEX.md`](../research/docs/INDEX.md)
- Backlog: [`TODO.md`](TODO.md)
