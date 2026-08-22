# Aggressive / unsafe optimization catalog (deep research)

**Owns:** the optimization space **beyond safe Harmony** - levers that trade
correctness, fidelity, stability, or maintainability for performance, and exactly
what risk each takes. Written after the safe Harmony space was exhausted + proven
([`../../7dtd-optimizer/docs/RESULTS.md`](RESULTS.md) §3b).
**Hub:** [`INDEX.md`](INDEX.md). **Costs to beat:** [`bottlenecks.md`](bottlenecks.md).
**Algorithms:** [`algorithms.md`](algorithms.md).

**Policy:** research only. None of these are shipped. Each names its risk class so a
future build can decide with eyes open; several are documented specifically so they
are **not** attempted blindly.

---

## 0. Risk classes

| Class | Meaning | Detection |
|---|---|---|
| **Desync** | server and client disagree on state (entities vanish/ghost, position wrong) | connection stability + client-visible check; hard to unit-test |
| **Corruption/crash** | bad memory/state -> exception or silent bad data | crash logs; can be intermittent |
| **Race** | off-main-thread access to single-thread-authority state | non-deterministic; heisenbug |
| **Fidelity** | gameplay visibly degrades (AI dumb, laggy far entities) | soak + manual; subjective |
| **EAC** | already off for any C# code mod (property of loading a DLL) | n/a - baseline |
| **Maintenance** | breaks on the next game update (fragile IL targets) | version drift; the `MISSING TARGET` guard |

**The fundamental constraint:** 7DTD is **single-thread authority** - the sim tick
owns world/entity/chunk state, and nothing else may mutate it concurrently. Almost
every big-gain unsafe lever runs into this (off-thread extraction races the tick).
IceCoffee's `Parallel.ForEach` on `EAITaskList` was abandoned for exactly this.

---

## 1. Entity tick (~32 s at 334 zombies) - the biggest cost

Close-combat AI; safe LOD/stride proven not to help (fidelity-bound).

- **Parallelize `TickEntities` across cores.** *Gain: large* (the #1 section is
  single-thread). *Risk: race (high).* Each `TickEntity -> OnUpdateEntity ->
  updateTasks` mutates shared world state (block damage, spawns, path requests,
  net queues). Safe only with a full read/write partition (entities touching
  disjoint chunks) + a serial merge phase - an architectural rewrite, not a patch.
  This is the single biggest theoretical win and the single hardest to do safely.
- **Skip character-controller physics for far entities.** `KinematicCharacterMotor.
  UpdatePhase1` (0.4% main-thread) runs per entity. *Gain: small-med. Risk:
  fidelity* (far zombies clip/float). Gated by distance it is bounded, but movement
  correctness is load-bearing for pathing.
- **Drop AI vision (`EntitySeeCache.CanSee`) for far entities.** *Gain: small
  (0.4%). Risk: fidelity* (far zombies do not notice players). Distance-gated it is
  low-harm but also low-gain.
- **Aggressive far-skip (skip `updateTasks` entirely, no despawn).** *Gain: med.
  Risk: entity accumulation* (the exact bug the v1.4.0 `CheckDespawn` guard fixed) -
  a regression, not an option.

**Verdict:** the only real win is multi-core `TickEntities`, and that needs a
chunk-partitioned rewrite (this is what the `zdtd` clone does from scratch); as a
mod it is a race minefield.

---

## 2. Network replication (~15 s, O(N^2.26)) - inherent, not an index problem

Interest is already distance-gated; the O(N^2) is sending each entity to each nearby
player (proven [`bottlenecks.md`](bottlenecks.md) §5).

- **Network LOD: throttle far entity->player update rate.** *Gain: small* (far pairs
  are few; the cost is close-high-interest pairs). *Risk: fidelity* (far entities
  laggy per player). Fails for the same reason the AI stride failed.
- **Off-thread serialize-once (shared buffer across writer threads).** The per-
  connection `taskSerialize` re-encodes the same broadcast package N times. Encode
  once, memcpy per connection. *Gain: cuts off-main CPU + the #4 allocator; not
  ms_per_tick. Risk: race* (a shared buffer read by N writer threads while the pool
  may recycle the source) + pool double-free. Needs the package's byte layout frozen
  at broadcast time and a refcounted shared buffer - deep, thread-safety-critical.
- **Reduce interest range inside the mod (bypass view distance).** *Gain: real
  (fewer interested players = less replication). Risk: fidelity/desync* if the
  server's interest disagrees with what the client expects to receive. This is what
  vanilla `ServerMaxAllowedViewDistance` does safely; doing it per-entity in a mod
  risks the client waiting for entities it never gets.

**Verdict:** no unsafe mod lever beats the vanilla view-distance knob here without
fidelity loss. Off-thread serialize-once is the only real CPU/alloc win and it is a
thread-safety rewrite for an off-main cost.

---

## 3. Pathfinding (`InitScan` = #1 allocator) - external DLL

- **P4 node-array reuse (in-place).** Reuse the fixed-size `LevelGridNode[]` instead
  of `newarr` each scan. *Gain: kills the #1 large-alloc (megapause feeder). Risk:
  corruption* - the alloc is inside `AstarPathfindingProject.dll`'s
  `<ScanInternal>d__21` **iterator state machine**; a transpiler on compiler-generated
  MoveNext IL is fragile (state gotos, `<>4__this`), and a stale reused buffer =
  wrong walkability = AI walks through walls. Concurrency is OK (scans hold
  `AstarPath.workItemLock`), but the IL surgery is the risk.
- **P4b node-object reuse.** `LevelGridNode` is a class, so each scan news N node
  objects too. Reusing them (reinit vs new) kills the churn. *Risk: corruption
  (higher)* - node lifetime across moves, references held by path workers.
- **Async graph scan.** The A* project supports threaded updates. *Risk: race* - path
  workers read the graph while a scan writes; needs the asset's work-item queue, not
  a naive thread.

**Verdict:** P4 array-reuse is the most defensible unsafe lever (bounded, concurrency
proven), but the external-iterator transpiler is fragile and breaks on any A* DLL
update. Worth a careful gated attempt if the megapause becomes the gating problem.

**BUILT (v1.8.0, `InitScanPoolPatch`, default off).** The transpiler loads clean:
smoke test showed `rerouted 1 LevelGridNode newarr` -> patched
`<ScanInternal>d__21:MoveNext()`, matched methods=7, no MISSING/exception. Gated by
`Pathfinding.PoolInitScanNodes`. `ReuseOrAlloc(count, graph)` reuses `graph.nodes`
via `Array.Clear` when `Length == count`, else falls back to `newarr`. The
external-DLL types (`LayerGridGraph`, `LevelGridNode`, `.nodes`) are all public, so
no reflection is needed. **A/B (§3c):** eliminates the `InitScan` alloc cleanly, no
benchable steady-state win (array large but infrequent). **25-min fidelity soak (§3d):**
zero pathfinding exceptions at a 10 GB heap, no leak, `alive` stable - the unsafe lever
is **safe under sustained load**, cleared for opt-in use. Still no *proven* perf win
(no matched pool-off soak at 10 GB); final gate is a visual blood-moon watch. Full
detail in [`../../7dtd-optimizer/docs/RESULTS.md`](RESULTS.md)
§3c-3d.

---

## 4. Chunk pipeline (56-60% of tick) - the biggest CPU share

`SendChunksToClients -> NetPackageChunk.Setup -> Chunk.write` (601 IL) runs
**synchronously on the sim thread**.

- **Off-sim `Chunk.write` encode.** Move the encode to the NCS writer thread.
  *Gain: large (reclaims most of 56-60% off the tick). Risk: race* - `Chunk.write`
  reads block/heightmap/TileEntity state the tick may mutate. Safe only if the chunk
  is snapshot/immutable at send time (copy-on-send), which itself costs.
- **Cache serialized chunk blobs per chunk-version.** Serialize once, reuse across
  observers + until a block change. *Gain: large (chunk sent to N players once).
  Risk: corruption* - cache invalidation on every block/TileEntity/light change;
  a missed invalidation = players see stale terrain (desync).

**Verdict:** the highest-CPU target, but both forms need a chunk snapshot/version
discipline the stock code does not provide - substantial and race/invalidation-prone.

---

## 5. GC / allocation floor (~30% of aggregate CPU)

- **Boehm incremental mode.** Tested: marginal (~8% late ticks at 128p; write-barrier
  cancels the pause-shortening). *Risk: low* (it is a supported mode) - already an
  opt-in mod knob, just not worth much.
- **Huge heap / never-collect windows.** The megapause experiment: `GC_disable` to a
  large heap, one big collect. *Gain: negative* (479 ms freeze at 7 GB; scales with
  heap). *Risk: stability* (multi-second STW = client timeouts). Diagnostic only.
- **RAM-headroom (`GC_FREE_SPACE_DIVISOR`).** EAC-safe env; fewer collections for
  more RAM. **This is the one being A/B-tested - it is *safe*, listed here only for
  contrast** (it does not corrupt or desync; worst case is more RSS).
- **Unsafe pooling of game objects** (ItemStack, packages). *Risk: corruption/double-
  free* - the pooled object's lifetime is owned by game code; reusing it early
  aliases live state.

**Verdict:** allocation is best cut at the source (P4, §3), not by GC tuning (churn
is invariant across GC configs). The safe RAM knob is the only free win here.

---

## 6. Cross-cutting unsafe techniques

- **Transpilers on hot inline loops** (interest all-pairs, iterator MoveNext).
  *Risk: corruption + maintenance* - fragile IL matching; a mis-emitted branch is a
  silent wrong result. Mitigation: pin targets, fail-visibly (`MISSING TARGET`), gate
  behind config, fidelity-gate every ship.
- **Reflection / pointer writes to private game state.** *Risk: corruption +
  maintenance* - field offsets/names change per update; writing them races the tick.
- **Off-main-thread extraction** (the recurring theme). *Risk: race* - the
  single-thread-authority model means any concurrent mutation is UB. Only read-only
  off-thread work (pure encode of an immutable snapshot) is safe, and stock rarely
  provides the snapshot.
- **Native / `unsafe` code, memory hacks.** *Risk: crash* - segfaults take the server
  down; no managed exception net.
- **Disabling whole subsystems** (deco, water, stability, light propagation).
  *Risk: gameplay/save* - some feed save state or block physics; a blanket skip can
  corrupt the world or break falling-block/farming mechanics.

---

## 7. The honest hierarchy

For a **stock server via Harmony**, ranked by (gain / risk):
1. **Config knobs** (view distance, spawn caps, `settargetfps`) - safe, gameplay
   tradeoff, no code. The real remaining lever.
2. **RAM-headroom GC env** - safe, EAC-safe, RAM tradeoff (testing).
3. **P4 `InitScan` array-reuse** - the most defensible *unsafe* lever (bounded,
   concurrency-proven; external-iterator-fragile). Gated + fidelity-tested.
4. **Off-sim chunk encode / blob cache** - biggest CPU, needs snapshot discipline.
5. **Multi-core `TickEntities`** / off-thread serialize-once - biggest theoretical
   wins, full race rewrites. **These belong in a from-scratch server** (`zdtd`),
   where the data model is designed for concurrency, not bolted on via Harmony.

The through-line: **every remaining big win requires either a gameplay tradeoff
(config) or breaking the single-thread-authority model (race), and the latter is an
architecture change, not a patch.** That is the honest ceiling of modding the stock
server.
