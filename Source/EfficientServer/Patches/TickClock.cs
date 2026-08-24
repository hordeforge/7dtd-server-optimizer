namespace EfficientServer.Patches
{
    /// <summary>
    /// Process-wide counter for per-entity LOD striping inside the TICK path
    /// (<see cref="UpdateTasksLodPatch"/> mid-band striding,
    /// <see cref="CrowdCollisionLodPatch"/> resolve staggering). Advanced by
    /// <see cref="TickClockPatch"/>, whose prefix fires once per GameManager.UpdateTick
    /// INVOCATION - and UpdateTick runs EVERY FRAME while the ~20 Hz full sim tick is
    /// gated inside it (measured: 19.9 -> 59.7 UpdateTick calls/s when Server.TargetFps
    /// rose 20 -> 60, RESULTS 3k). So this counter steps at FRAME granularity: between
    /// two runs of one entity's tick-path work it jumps F = fps/20 steps.
    ///
    /// Coverage consequence: at the vanilla 20 fps (F=1) every entityId owns exactly
    /// one slot per stride window, exact. Above 20 fps the every-residue guarantee
    /// holds only when gcd(F, stride) = 1 (frame jitter usually breaks other
    /// resonances); otherwise fixed id classes can go slotless for sustained spans -
    /// the same hazard a raw Time.frameCount modulo carries. What the dedicated
    /// counter still buys vs Time.frameCount: deterministic zero seed, immunity to
    /// pre-game frames, and game-type-free state the test harness can replay.
    /// </summary>
    internal static class TickClock
    {
        static int _ticks;

        /// <summary>Index of the current logical tick (0 until the first tick runs).</summary>
        public static int Ticks { get { return _ticks; } }

        // Called once per GameManager.UpdateTick (prefix: constant during the whole
        // tick body, including slice-drained entity work that spills onto later
        // frames). Unconditional by design: a clock must not stop when feature
        // configs toggle, and one int increment per tick is noise.
        public static void Advance()
        {
            _ticks = unchecked(_ticks + 1);
        }

        /// <summary>
        /// Does this entity own its Nth-tick slot right now? Striped by entityId so
        /// owners spread evenly across the stride window instead of clumping.
        /// </summary>
        public static bool SlotOwn(int entityId, int everyTicks)
        {
            return OwnsSlot(entityId, _ticks, everyTicks);
        }

        /// <summary>
        /// Pure slot predicate behind <see cref="SlotOwn"/>, taking the tick index
        /// explicitly so tests can replay tick sequences deterministically; also
        /// drives the per-frame animator stripe in <see cref="AnimatorLodPatch"/>
        /// with Time.frameCount as the cursor. Cast through uint so the signed wrap
        /// at ~2.1 billion ticks stays a clean monotonic sequence for the modulo
        /// instead of going negative and freezing whole id classes (same boundary
        /// treatment as <see cref="TickStride"/>).
        /// </summary>
        public static bool OwnsSlot(int entityId, int tickIndex, int everyTicks)
        {
            return unchecked((uint)(entityId + tickIndex)) % (uint)everyTicks == 0;
        }
    }
}
