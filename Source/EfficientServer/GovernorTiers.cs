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
        public static int ThrottleLever(int baseValue, int maxValue)
            => Math.Min(maxValue, Math.Max(baseValue, baseValue * 2));
    }
}
