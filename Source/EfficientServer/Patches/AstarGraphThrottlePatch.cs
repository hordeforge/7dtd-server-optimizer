using System.Threading;
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
        static int _tick;

        // Return false to skip the original UpdateGraphs on non-Nth ticks.
        static bool Prefix()
        {
            PathfindingConfig cfg = ModApi.Config != null ? ModApi.Config.Pathfinding : null;
            if (!ModApi.ShouldRun() || cfg == null) return true;
            int every = cfg.GraphUpdateEveryTicks;
            if (every <= 1) return true; // 1 = vanilla, run every tick (no throttle)
            // Cast through uint so the signed wrap at ~3.4 years uptime stays a clean
            // monotonic sequence for the modulo instead of going negative.
            uint t = unchecked((uint)Interlocked.Increment(ref _tick));
            return (t % (uint)every) == 0;
        }
    }
}
