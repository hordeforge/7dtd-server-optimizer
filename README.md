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

## Measured impact (v1.16.x)

- **Eliminates the GC megapause:** worst stop-the-world **274 ms -> 0**, full
  collections **3 -> 0** in the aggregate A/B window (vanilla lost 5.5 ticks at once
  to one freeze). Tick-stall total **-28%**; pathfinding graph work **-27%**.
- **At a breaking load** (vanilla tick-starved), the pathfinding throttle alone is
  **-28.5% ms/tick**, pulling the server from failing back to healthy.
- **Adaptive governor** (default on, inert while healthy): under overload it engages
  the measured throttles (replication stride 2 = -45% on that wall, doubled graph
  cadence), cushioning a 435-zombie overload at 128 vs 299 ms/frame and
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
- OSS tools survey (research): [`../research/oss-tools/NOTES.md`](../research/oss-tools/NOTES.md)
- Dedicated game loop RE map: [`../research/docs/loop.md`](../research/docs/loop.md)
- RE dump index: [`../research/docs/INDEX.md`](../research/docs/INDEX.md)
- Backlog: [`TODO.md`](TODO.md)
