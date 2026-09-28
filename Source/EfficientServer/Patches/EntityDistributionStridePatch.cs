using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Stride the per-tick entity-replication pass. NetEntityDistribution.OnUpdateEntities
    /// recomputes per-entity per-player interest and enqueues movement/state packages
    /// every tick; at the blood-moon standard it measures 7.69 ms avg/frame, one
    /// of the two O(N^2) player-axis walls. It is a STATE-driven scan (positions
    /// and change flags are
    /// read from current state; dirty flags persist on the entry until sent), so
    /// skipping a call only delays replication by the stride - nothing is lost.
    /// Clients interpolate entity motion, so a 2-tick stride (10 Hz replication,
    /// +50 ms staleness) is the console-game norm; it halves this wall's cost.
    ///
    /// Risk is fidelity, not correctness: fast-moving entities rubber-band harder at
    /// higher strides. Default 1 = vanilla (every tick). Needs a human-eye pass at
    /// stride 2 before production use.
    /// </summary>
    [HarmonyPatch(typeof(NetEntityDistribution), "OnUpdateEntities")]
    public static class EntityDistributionStridePatch
    {
        // Lifetime skip count for `es status`. The stride is silent by design (one
        // skipped replication pass per stride window, forever, would flood), so
        // this total is how an operator tells "the lever is engaged" from "the
        // stride is configured but the tick slot never crossed". A governor-driven
        // doubling shows up here too, which is what makes the throttle visible
        // without reading the governor's tier transitions.
        static long _skippedTotal;
        public static long SkippedTotal { get { return _skippedTotal; } }

        static bool Prefix()
        {
            ServerPerfConfig config = ModApi.Config;
            NetworkConfig cfg = config != null ? config.Network : null;
            if (!ModApi.ShouldRun(config) || cfg == null)
                return true;
            // The stride in force: the configured cadence, or the governor's
            // doubled one while it is throttling. The governor decides in the
            // UpdateTick POSTFIX, so a tier set at the end of tick N-1 is what
            // this tick runs at, exactly as when it rewrote the config in place.
            int stride = GovernorPatch.EffectiveEntityStride(cfg.EntityDistributionEveryTicks);
            if (stride <= 1) return true;
            // OnUpdateEntities runs once per UpdateTick invocation; id 0 keeps
            // the Nth-run crossing, fail open to vanilla before the clock livers.
            if (!TickClock.Alive || TickClock.OwnsSlot(0, TickClock.Ticks, stride))
                return true;
            _skippedTotal++;
            return false;
        }
    }
}
