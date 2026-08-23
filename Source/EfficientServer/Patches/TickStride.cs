using System.Threading;

namespace EfficientServer.Patches
{
    /// <summary>
    /// The one copy of the per-tick stride gate shared by the two cadence levers
    /// (<see cref="AstarGraphThrottlePatch"/>, <see cref="EntityDistributionStridePatch"/>):
    /// advances a caller-owned counter and reports whether this call owns the Nth
    /// slot. Cast through uint so the signed wrap at ~3.4 years uptime stays a
    /// clean monotonic sequence for the modulo instead of going negative and
    /// flipping which slots run.
    /// </summary>
    internal static class TickStride
    {
        public static bool RunThisTick(ref int tick, int every) =>
            unchecked((uint)Interlocked.Increment(ref tick)) % (uint)every == 0;
    }
}
