using System;

namespace EfficientServer
{
    /// <summary>
    /// Pure tier math for the governor's two throttle levers
    /// (<see cref="PathfindingConfig.GraphUpdateEveryTicks"/>,
    /// <see cref="NetworkConfig.EntityDistributionEveryTicks"/>). Escalation DOUBLES
    /// each lever from its configured baseline; de-escalation restores that exact
    /// baseline. A throttle must never run a lever FASTER than the operator set it,
    /// so the doubled value is floored at the baseline and capped at the same ceiling
    /// <see cref="ServerPerfConfig.Normalize"/> enforces. Game-type-free and the single
    /// definition of the mapping, so the transition, step-down, and config-reload
    /// paths in GovernorPatch cannot drift apart - and the test harness can pin it.
    /// </summary>
    internal static class GovernorTiers
    {
        // Lever ceilings. Shared with ServerPerfConfig.Normalize, which clamps the
        // operator's configured baseline to the same range, so the doubled throttle
        // value can never sit outside what a hand-written config could express.
        public const int EntityStrideMax = 4;
        public const int GraphUpdateMax = 200;

        public static int ThrottleLever(int baseValue, int maxValue)
            => Math.Min(maxValue, Math.Max(baseValue, baseValue * 2));
    }
}
