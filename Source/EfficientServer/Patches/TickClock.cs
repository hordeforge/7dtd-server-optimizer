namespace EfficientServer.Patches
{
    /// <summary>
    /// Process-wide game-tick counter for per-entity LOD striping inside the TICK
    /// path (<see cref="UpdateTasksLodPatch"/> mid-band striding,
    /// <see cref="CrowdCollisionLodPatch"/> resolve staggering). Those methods run
    /// from the ~20 Hz authority tick, which is independent of the render frame
    /// rate, so their stripes must count TICKS: with Server.TargetFps above 20 the
    /// frame counter jumps F = fps/20 steps between consecutive ticks, and a modulo
    /// sampled only at tick times then misses whole residue classes whenever F and
    /// the stride share a factor - a fixed subset of entities silently never owns a
    /// slot (frozen AI tail / no neighbor-collision resolution). A tick-sourced
    /// counter visits every residue exactly once per stride window at any frame
    /// rate. Game-type-free so the test harness can drive it deterministically;
    /// <see cref="TickClockPatch"/> is the one Harmony hookup that advances it.
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
        /// explicitly so tests can replay tick sequences deterministically. Cast
        /// through uint so the signed wrap at ~2.1 billion ticks stays a clean
        /// monotonic sequence for the modulo instead of going negative and freezing
        /// whole id classes (same boundary treatment as <see cref="TickStride"/>).
        /// </summary>
        public static bool OwnsSlot(int entityId, int tickIndex, int everyTicks)
        {
            return unchecked((uint)(entityId + tickIndex)) % (uint)everyTicks == 0;
        }
    }
}
