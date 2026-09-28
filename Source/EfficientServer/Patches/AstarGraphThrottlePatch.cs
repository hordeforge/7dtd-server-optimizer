using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// AstarManager.UpdateGraphs(float) runs every tick (20 Hz) to reposition the
    /// player-following voxel nav-graphs - the top managed section under load. A
    /// player barely moves in 50 ms, so re-evaluating the follow window every tick
    /// is wasted work. This prefix runs it only every Nth tick; on skipped ticks
    /// the graphs hold their position.
    ///
    /// What UpdateGraphs does internally (verified in IL): queue grid moves
    /// (UpdateGraphPos), then drain ONE queued move per call via UpdateMoveGraph
    /// (single RemoveAt(0) + MoveGraph, gated on the prior move finishing), which
    /// triggers the InitScan rescan. Throttling therefore slows the whole
    /// maintenance cadence by N: the follow-graphs reposition/rescan at (20/N) Hz
    /// and the per-call drain headroom drops from 20/s to 20/N per second. Nothing
    /// is permanently stranded - moveList is a persistent field, drained on the
    /// next call - but under pathological mass player-movement the graphs can lag
    /// more than N ticks. AI keeps moving regardless: path COMPUTE is a separate
    /// every-tick system (EntityAlive.FindPath -> PathFinderThread) that runs
    /// against whatever graph currently exists; only the walkability window lags.
    /// The fidelity gate (AI reaches targets across fresh chunk edges / fast
    /// movement) is validated by load test, not assumed.
    ///
    /// Server-internal: nav graphs are AI infra, no wire bytes change, so a
    /// vanilla / EAC client connects normally.
    /// </summary>
    // Pin the (float) overload so a future signature change fails loudly (MISSING
    // TARGET) instead of ambiguously binding or silently disabling the throttle.
    [HarmonyPatch(typeof(AstarManager), "UpdateGraphs", new[] { typeof(float) })]
    public static class AstarGraphThrottlePatch
    {
        // Lifetime skip count for `es status`: this is the top managed section
        // under load, so "is the cadence actually costing us the graphs' refresh"
        // is the first question when a server feels stale, and the throttle is
        // silent per skipped call by design. A governor-driven doubling of the
        // cadence lands here too.
        static long _skippedTotal;
        public static long SkippedTotal { get { return _skippedTotal; } }

        // Return false to skip the original UpdateGraphs on non-Nth ticks.
        static bool Prefix()
        {
            ServerPerfConfig config = ModApi.Config;
            PathfindingConfig cfg = config != null ? config.Pathfinding : null;
            if (!ModApi.ShouldRun(config) || cfg == null) return true;
            // The cadence in force: the configured one, or the governor's doubled
            // one while it is throttling (the governor decides in the UpdateTick
            // POSTFIX, so a tier set at the end of tick N-1 is what runs here).
            int every = GovernorPatch.EffectiveGraphEvery(cfg.GraphUpdateEveryTicks);
            if (every <= 1) return true; // 1 = vanilla, run every tick (no throttle)
            // UpdateGraphs runs once per UpdateTick invocation, so the shared
            // TickClock index is a valid cadence cursor; id 0 holds the Nth
            // run crossing. Fail open to vanilla until the clock driver fires.
            if (!TickClock.Alive || TickClock.OwnsSlot(0, TickClock.Ticks, every))
                return true;
            _skippedTotal++;
            return false;
        }
    }
}
