using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Stride the per-tick entity-replication pass. NetEntityDistribution.OnUpdateEntities
    /// recomputes per-entity per-player interest and enqueues movement/state packages
    /// every tick; at blood-moon load it is ~15 ms/frame, one of the two O(N^2)
    /// player-axis walls. It is a STATE-driven scan (positions and change flags are
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
        static int _tick;

        static bool Prefix()
        {
            NetworkConfig cfg = ModApi.Config != null ? ModApi.Config.Network : null;
            if (!ModApi.ShouldRun() || cfg == null || cfg.EntityDistributionEveryTicks <= 1)
                return true;
            return TickStride.RunThisTick(ref _tick, cfg.EntityDistributionEveryTicks);
        }
    }
}
