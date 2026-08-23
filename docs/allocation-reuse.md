# Allocation reuse: trading RAM for zero-alloc (Boehm)

**Owns:** buffer-reuse / preallocation strategy to cut managed churn at source, and
the Boehm "use more RAM, collect less" knobs.
**Context:** Unity Mono/Boehm (`libmonobdwgc-2.0`), non-moving conservative STW GC,
single-threaded 20 TPS. Host has **128 GB RAM** - memory is not the constraint, GC
pause is. **Hub:** [`INDEX.md`](INDEX.md). **Pairs with:** process-level GC knobs
[`runtime-tuning.md`](runtime-tuning.md); concrete optimizer levers
[`../../7dtd-server-optimizer/docs/ALLOCATION_UPSTREAM.md`](ALLOCATION_UPSTREAM.md).

---

## 0. Premise

Gross churn is ~8-15 MB/s under load and is the GC-pause driver; the megapause
diagnostic showed one forced collect of a ~7 GB heap freezes the server ~479 ms.
With abundant RAM the strategy is: **spend memory to never allocate on the hot
path**. Two complementary layers:

- **(A) App-level reuse** - preallocate + reuse buffers so `GC_malloc` fires far
  less. Cuts churn *at source*: fewer collections **and** less mark work per
  collection. Primary lever. Needs a code mod (EAC-off).
- **(B) Boehm heap headroom** - give the collector more free space so it collects
  less often. Cuts collection *frequency* only (churn unchanged). Cheap complement,
  **EAC-safe via env vars**.

(A) is strictly better; (B) is free on top.

---

## 1. What is already pooled vs what still churns

The game is **already pool-heavy** - "add object pooling" is the wrong framing:

- `MemoryPools.poolBinaryWriter` (`MemoryPooledObject<PooledBinaryWriter>`),
  `poolMemoryStream` (`PooledExpandableMemoryStream`), and packages via
  `NetPackageManager.GetPackage<T>` are all pooled objects (`AllocSync` from a pool).

The steady-state churn is **buffer growth and per-operation arrays**, not un-pooled
objects:

| Site | Why it still allocates |
|---|---|
| `AstarVoxelGrid.InitScan` | the nav-graph node array is **re-minted per grid move** even though grid dims are fixed (large-object space) - the clearest realloc-that-should-be-reuse |
| `TerrainSubMesh.Add` | sub-mesh vertex/index buffers (`ArrayDynamicFast` growth) |
| `ItemStack.Clone` | defensive per-op item copies (`newobj` + ItemValue array re-new) |
| `PooledExpandableMemoryStream` | **mostly self-solved** - its `Reset()` is just `SetLength(0)`, which KEEPS the backing `byte[]` capacity, so the pooled buffer is retained across reuse and only reallocates when a write exceeds the retained max (rare after warmup). The residual serialization churn is better attacked by **serialize-once** (cut the *count* of serializations) than by presizing an already-retained buffer. |

**Correction (RE 2026-07-19):** an earlier draft claimed the pooled stream buffer
reallocates on every growth. It does not - `SetLength(0)` retains capacity. The
real reallocating allocator is `InitScan` (fixed-size array, new every move); the
serialization churn is a *count* problem (per-player re-serialization), not a
buffer-retention problem.

Verified clean (do **not** spend effort here): `EntityAlive.updateTasks` and
`World.EntityActivityUpdate` show **zero** LINQ / `new List` / `ToList` in their IL -
the entity hot path already avoids per-tick collection churn.

---

## 2. Reuse techniques (ranked; be generous with 128 GB)

**a. Reuse fixed-size arrays that are re-newed each use - the biggest win.**
The clearest case is `AstarVoxelGrid.InitScan`: the node array is fixed-size per
grid but `new`ed on every grid move. Reuse-in-place (clear + reuse the existing
buffer when dims match) removes the #1 large-alloc. (Note: the pooled *stream*
buffers already retain capacity via `Reset() = SetLength(0)`, so they are NOT the
target - see section 1's correction; presizing them to p99 is a small residual
win at most.)

**b. `ArrayPool<T>.Shared` for transient large arrays.**
Confirmed available in the game BCL (394 refs in mscorlib). Rent/return node
arrays, mesh buffers, big scratch instead of `new T[]`. Boehm is non-moving, so no
pinning concerns and rented buffers never get relocated.

**c. Clear-and-reuse collections.**
Presize `List`/`Dictionary` `Capacity` once and `Clear()` + refill each tick
instead of `new` each tick. The engine already does this in places
(`AstarManager.mergedLocations.Clear()`); extend the pattern to any per-tick
collection a mod adds.

**d. `Span<T>` / `stackalloc` for small transient scratch.**
Serialization headers, temp math, small fixed buffers -> zero heap. net48 supports
`Span<T>` via `System.Memory`. Best for sub-KB scratch that would otherwise be a
short-lived `byte[]`.

**e. Thread-local scratch buffers.**
One reusable buffer per writer thread (`NCS_Writer`) and per path worker
(`ASPPathFinderThread`), reused across calls - avoids cross-thread pool contention
and repeated allocation on the off-main threads.

---

## 3. Boehm specifics (why reuse is clean here + the RAM knobs)

- **Non-moving / non-compacting:** pooled buffers stay put; no pinning, no
  compaction cost, no fragmentation churn from a moving collector. Long-lived pools
  are structurally free.
- **Large-object space:** arrays > ~4 KB (node arrays, mesh buffers, big packets)
  take Boehm's large-object path. Reusing them avoids both the expensive large
  allocation **and** shrinks the conservative-scan surface each collect must walk.
- **RAM-for-frequency knobs (all exported; EAC-safe via env vars):**
  - `GC_FREE_SPACE_DIVISOR` (`GC_set_free_space_divisor`): lower = keep more free
    heap = collect less often. Default ~3; `1`-`2` keeps ~1.5-2x heap free, roughly
    halving collection frequency. **Caveat (megapause):** a bigger retained heap
    means each collect marks more, so the per-collect pause grows - moderate values
    only, do not chase "never collect".
  - `GC_expand_hp` / preallocate the heap at startup so early-game growth does not
    trigger a burst of growth-collections.
  - `GC_set_full_freq`: full collect every N partial collects.
  These are set via environment variables (no code) -> **EAC-safe**. App-level reuse
  (section 2) is a code mod -> EAC-off. See [`runtime-tuning.md`](runtime-tuning.md).
- **Reuse beats the knobs:** reuse lowers *churn* (fewer `GC_malloc` -> fewer AND
  cheaper collects); the knobs only lower *frequency* (same churn, bigger collects).
  Use reuse as the lever, the knobs as a cheap complement.

---

## 4. Application to the measured top allocators

| Allocator | Technique | Where |
|---|---|---|
| `PooledExpandableMemoryStream` byte[] (network serialize) | **a** presize + retain | ALLOCATION_UPSTREAM Lever B / this doc |
| `AstarVoxelGrid.InitScan` node array | **a/b** per-grid reusable buffer / ArrayPool | ALLOCATION_UPSTREAM Lever A (P4) |
| `TerrainSubMesh.Add` | **b** pooled mesh buffers | ALLOCATION_UPSTREAM Lever C |
| `ItemStack.Clone` | **c** pool / elide (correctness-sensitive) | ALLOCATION_UPSTREAM Lever C |

---

## 5. EAC + measurement

- App-level reuse = C# code mod -> **EAC-off**. Boehm env knobs -> **EAC-safe**.
- Measure with the corrected APM attribution (`top_alloc_sites`/`top_churn_sites`
  ranked by bytes) + `gross MB/s`: **reuse should drop gross MB/s directly** (fewer
  `GC_malloc`). The env knobs drop collection *count*, not `gross MB/s`. Confirm the
  gain lands in `ms_per_tick` only where GC was actually the cost - the no-GC
  diagnostic window is the ceiling on achievable tick-time gain (the P2 lesson: a
  real allocation cut can still be tick-time-neutral on a server with headroom;
  then the win is pause-smoothness/RAM, not TPS - state that honestly).
