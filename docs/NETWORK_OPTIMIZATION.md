# Network / serialization optimization plan

**Hub:** [`README.md`](../README.md).  
**Status:** research + implementation plan for the player-scale wall. Two cheap
levers from this space already SHIPPED - `Network.FastSingleTargetSend` (O(1)
single-target recipient lookup, default on) and
`Network.EntityDistributionEveryTicks` (whole-pass replication stride; also moved
dynamically by the governor) - see [FEATURES.md](FEATURES.md) / [CONFIG.md](CONFIG.md).
The structural levers below (L1 serialize-once - note stock RE later showed the
build layer already serializes once, see PERF_RESEARCH_BRIEF refutations; L2
spatial grid, L3 interest LOD bands, L4 round-robin budget, L5 per-player caps)
are NOT yet built. Promote each
lever only with APM before/after + a desync/fidelity check.

**Owns:** the detailed attack plan for `NetEntityDistribution` / `ConnectionManager`
serialization cost. Graded summary lives in
[`OPTIMIZATION_CANDIDATES.md`](OPTIMIZATION_CANDIDATES.md) (B3, A4); measured
exponents in [`measured-scaling.md`](measured-scaling.md)
and the loop RE in [`../../7dtd-engine-research/docs/network.md`](../../7dtd-engine-research/docs/network.md).

---

## 1. The measured problem

Player scaling is **super-linear in the network/connection layer** (`apm scaling
--by players`, 15 → 498 players):

| Section | per-call | total | IL |
|---|:--:|:--:|--|
| `ConnectionManager.Update` | **O(N^2.27)** | O(N^1.3) | IL≈215 |
| `NetEntityDistribution.OnUpdateEntities` | **O(N^2.26)** | O(N^1.3) | IL=322 |
| `NetEntityDistributionEntry.updatePlayerList` | n/a | n/a | IL=509 |

At **128 players** (measured, 150 s capture): gross alloc churn **15.16 MB/s**
(vs 3.7 at moderate load), **4 full STW collections**, **192 late ticks /
6117 ms overage**, server at ~6.5 cores / ~7 FPS, and `gc_pauses` is still the
#1 lag cause. So the network layer burns **both** CPU (serialization compute) and
GC (per-package allocation) - and both scale super-linearly.

### Why it is O(players × entities)

Replication runs after `TickEntities` in `UpdateTick`: `OnUpdateEntities`
iterates entities and, per entity, `updatePlayerList` evaluates **every player's
interest**. That is the all-pairs eval - ~128 × 131 ≈ **16.7 k pair-evaluations
per tick** at 128 players, each doing: interest `distSq`, delta-encode vs that
player's last-known state, a package-type state machine, and a **serialize**.

Per-player package choice (B3 thresholds, from IL):
- interest refresh when last pos `distSq > 16`
- `Teleport` if encoded Δ ∉ ±256; full `PosAndRot` if Δ ∉ ±128 **or** age > 100
  ticks; else `RelPosAndRot`
- move sent if encoded Δ ≥ 2; velocity if motion² > 0.04
- package types: `RelPosAndRot`, `PosAndRot`, `Teleport`, `Rotation`,
  `Velocity`, `AliveFlags`, `PlayerStats`, `Equipment`

Key observation: **absolute/full packages (`PosAndRot`, `Teleport`, `Rotation`,
flags, stats) are player-independent** - the same entity state. Only
`RelPosAndRot` (delta vs a specific player's last-sent) is per-player. Yet each
is serialized once **per interested player** today.

---

## 2. Levers (prioritized)

Each lever notes: mechanism, expected impact, implementation surface, wire /
client compatibility, risk, and how to validate with APM.

### L1: "Serialize once, send many" (TOP PICK: hits CPU *and* GC)

**Mechanism.** For player-independent packages, serialize the entity's state
**once per tick** into a shared byte buffer and send those identical bytes to all
K interested players, instead of re-serializing per player. Turns per-tick
serialization from O(players × entities) to O(entities) for those packages; the
send stays O(pairs) but send is cheap vs serialize.

**Impact.** Cuts the dominant serialization CPU **and** the 15 MB/s allocation
churn (fewer `PooledBinaryWriter` / package allocations → fewer STW collections).
The only lever that also shrinks the GC pressure the GC guard/incremental work is
fighting - synergistic.

**Surface.** Harmony around the per-player send in `updatePlayerList` /
`OnUpdateEntities`: build the shared package once per (entity, package-type),
cache in a per-tick buffer keyed by entity+type, reuse across players. Keep
`RelPosAndRot` per-player (it is a per-player delta).

**Wire / client.** Bytes are byte-identical to what the client already receives -
**no protocol change, vanilla-client-safe.**

**Risk.** Low-medium. Must be certain a cached package is truly player-independent
(no per-player fields snuck in); cache lifetime is exactly one tick.

**Validate.** APM: gross alloc MB/s down, `fullCollections` down, `ConnectionManager` /
`NetEntityDistribution` per-call ms down, at 64/128 players. Fidelity: positions
identical (same bytes).

**Effort:** medium. **Priority:** 1.

### L2: Spatial interest culling (kills the exponent)

**Mechanism.** Replace the all-pairs interest eval with a spatial grid / hash:
bucket entities and players into cells; an entity evaluates only players within
its interest radius (a handful of cells). O(players × entities) →
O(players × local_density), near-linear at real densities.

**Impact.** Attacks the **exponent**, not just the constant - the actual fix for
the ~450-500 player collapse. Also feeds A4 (`GetClosestPlayer` spatial hash,
same structure).

**Surface.** Harmony transpile/replace of the interest loop in `OnUpdateEntities`;
maintain a grid updated as entities/players move. Reuse the game's chunk grid as
the bucket where possible.

**Wire / client.** Fewer entities sent to distant players = same behavior as the
existing interest management, just cheaper. Client-compatible.

**Risk.** Medium (desync/pop-in if the interest radius or grid update is wrong;
must match or exceed the stock interest distance so nothing visible is dropped).

**Validate.** APM scaling `--by players` re-run: the `NetEntityDistribution`
exponent should drop from ~2.26 toward ~1. Fidelity: entity visibility radius
unchanged at the client.

**Effort:** high. **Priority:** 2 (the real ceiling-raiser).

### L3: Network LOD / rate-limit under load (B3, config, immediate)

**Mechanism.** A network mirror of the shipped AI LOD. Under load: raise the far
thresholds (interest `distSq`, full `PosAndRot` Δ, `Teleport` Δ) so distant/slow
entities emit fewer/smaller packages, and send distant entity↔player pairs every
Nth tick instead of every tick (e.g. far tier at 5 Hz vs 20 Hz).

**Impact.** Directly cuts the per-tick pair count and package volume. Tunable and
reversible - the pragmatic first cut while L1/L2 are built.

**Surface.** Config-driven (distance bands + per-band Hz, like `AiLod`), applied
in `updatePlayerList`. No structural rewrite.

**Wire / client.** Fewer *valid* packets, no protocol change - client sees
less-frequent updates for far entities (mild rubber-banding), tolerated by stock
interpolation. Client-compatible.

**Risk.** Medium-high (visible desync / rubber-banding if too aggressive; this is
why B3 is graded high-risk and last). Needs a fidelity check across combat,
vehicles, and fast movement.

**Validate.** APM tick/overage down; a scripted fidelity pass (position error vs
stock at set distances) within budget.

**Effort:** low-medium. **Priority:** 3.

### L4: `ConnectionManager` round-robin budget

**Mechanism.** `ConnectionManager.Update` services all connections every tick.
Service a budget of K connections/tick round-robin; each connection is serviced
every ⌈N/K⌉ ticks (K=32 at 128 → every 4 ticks / 5 Hz).

**Impact.** Bounds per-tick connection work; spreads the O(N) send loop across
ticks so no single tick pays for all 128.

**Surface.** Harmony around `ConnectionManager.Update`; a rotating cursor over the
connection list.

**Wire / client.** Slightly higher per-connection latency; no protocol change.

**Risk.** Medium (latency; must not starve a connection under churn - cap the
max skip).

**Validate.** APM tick p99 down; per-connection RTT (loadgen cohort ping stats)
within budget.

**Effort:** medium. **Priority:** 4.

### L5: Cap updates-per-player-per-tick (bounded work)

**Mechanism.** Per player per tick, send at most the N most-relevant entity
updates (nearest / most-changed); defer the rest to later ticks. Bounds work
regardless of entity count.

**Impact.** Hard ceiling on per-tick serialization; complements L3.

**Surface.** Config N + a per-player priority sort in `updatePlayerList`.

**Risk.** Low-medium (stale positions for deferred low-priority entities).

**Effort:** low-medium. **Priority:** 5.

### L6: Off-thread interest compute (deferred / risky)

**Mechanism.** The interest eval (read-only `distSq` over positions) could compute
per-player interest sets on worker threads; serialize+send stays main-thread.

**Risk.** High - shared entity state, races; the *send* must stay main-thread
(parallel `SendPackage` is in the Reject list for connection safety). Only the
read-only compute is a candidate, and only behind a job barrier.

**Effort:** high. **Priority:** last / research-only.

---

## 3. Sequencing

1. **L1 serialize-once** - biggest immediate win, cuts CPU + GC, no wire change,
   low risk. Build + APM before/after at 64 and 128 players.
2. **L3 network LOD** (config) in parallel as the tunable safety valve, behind a
   fidelity gate.
3. **L2 spatial culling** - the exponent fix for the real 500+ ceiling; larger
   effort, do after L1 proves the harness.
4. **L4 / L5** as bounded-work backstops.
5. **L6** only if L1-L4 leave a main-thread wall.

Every lever is server-side-only and wire-compatible (identical or fewer valid
packets, no protocol change), so a vanilla client connects and nothing desyncs -
though the server runs EAC-off like any C# mod (see FEATURES "Anti-cheat").

## 4. Client-mod and EAC compatibility

| Lever | Needs client mod | Works with EAC enforcing |
|---|:--:|:--:|
| L1 serialize-once | no (byte-identical) | no - C# code |
| L2 spatial culling | no | no - C# code |
| L3 network LOD (code) | no | no - C# code |
| L4 connection round-robin | no | no - C# code |
| L5 cap-per-tick | no | no - C# code |
| L6 off-thread compute | no | no - C# code |
| Config `ServerMaxAllowedViewDistance` ↓ | no | **yes - XML only** |
| Config `MaxSpawnedZombies` ↓ | no | **yes - XML only** |

- **Client mod:** none of L1-L6 need one. Every lever sends identical or fewer
  *valid* packets with no protocol change, so a vanilla client connects
  unmodified. (L3 shows as rubber-banding on far entities - a fidelity effect,
  not a compatibility one.)
- **EAC:** none of the code levers run under enforcing EAC. Any C# mod
  (Harmony *or* IModApi *or* P/Invoke) forces the server EAC-off; EAC cares that
  the server loads code, not how it hooks. The only EAC-compatible network levers
  are the **vanilla config** ones - lower `ServerMaxAllowedViewDistance` and
  `MaxSpawnedZombies` - which cut the *workload* (fewer entities' worth of O(N²)
  work) without code. Strictly less effective than L1/L2 per unit, but they keep
  EAC on. **Trade-off: EAC-on = turn the workload down (config only); attack the
  per-unit cost = code = EAC-off.**

## 5. Validation harness

Use the sibling projects, not in-mod tooling:
- **Load:** `7dtd-loadgen` at 64 and 128 wander bots (network churn; no zombies -
  players are the driver). See the 128-player GC benchmark for the pattern.
- **Measure:** `7dtd-server-apm capture --reset-bridge` → gross alloc MB/s,
  `fullCollections`, `ConnectionManager`/`NetEntityDistribution` per-call ms,
  late-tick overage; `apm scaling --by players` for the exponent.
- **Fidelity gate:** scripted position-error check vs stock at fixed distances +
  the loadgen ping/RTT stats. No lever ships if it moves fidelity beyond budget.
