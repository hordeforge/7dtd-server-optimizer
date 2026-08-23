using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Adaptive load governor (default on). Watches the real tick interval and moves
    /// the two proven throttle levers between their configured baselines and doubled
    /// throttled settings:
    ///
    ///   - Network.EntityDistributionEveryTicks: configured value <-> 2x (capped 4;
    ///     from the default 1 that is 1 <-> 2 = 20 <-> 10 Hz, -45% on the replication
    ///     wall, see RESULTS 3g)
    ///   - Pathfinding.GraphUpdateEveryTicks: configured value <-> 2x that value
    ///
    /// Sustained over-budget ticks (interval EMA > OverBudgetMs for WindowTicks)
    /// escalate one step; sustained healthy ticks (EMA < HealthyMs) after a cooldown
    /// step back down. Hysteresis (OverBudgetMs > HealthyMs gap) plus the cooldown
    /// prevent oscillation. Every transition is logged so an operator can see exactly
    /// when and why fidelity was traded for tick rate.
    ///
    /// The governor only moves levers between the OPERATOR'S CONFIGURED BASELINE and
    /// a doubled, capped throttle value (<see cref="GovernorTiers"/>) - it introduces
    /// no new behavior, it schedules existing, individually-validated ones. Baselines
    /// are captured at first transition and restored on full recovery, so an
    /// operator's non-vanilla steady state (e.g. EntityDistributionEveryTicks=3)
    /// survives a governor cycle unchanged.
    /// </summary>
    [HarmonyPatch(typeof(GameManager), "UpdateTick")]
    public static class GovernorPatch
    {
        static readonly TickIntervalEma TickEma = new TickIntervalEma();
        static int _overTicks;
        static int _healthyTicks;
        static int _cooldown;
        static int _level; // 0 = baseline, 1 = throttled
        static int _baseGraphEvery = -1;
        static int _baseEntityStride = -1;

        // Period between the tier-2 re-sweeps that cover mid-emergency spawns
        // (~5 s at the vanilla 20 TPS).
        const int SweepPeriodTicks = 100;

        // Live state for `es status` / incident response: which tier is applied
        // RIGHT NOW (config alone cannot tell you this) and the smoothed tick
        // interval driving it.
        public static int Level { get { return _level; } }
        public static double EmaMs { get { return TickEma.Value; } }

        static void Postfix()
        {
            GovernorConfig cfg = ModApi.Config != null ? ModApi.Config.Governor : null;
            if (!ModApi.ShouldRun() || cfg == null || !cfg.Enabled)
                return;

            double emaMs = TickEma.Advance();
            if (_cooldown > 0) _cooldown--;

            if (emaMs > cfg.OverBudgetMs)
            {
                _healthyTicks = 0;
                _overTicks++;
                if (_cooldown == 0 && _overTicks >= cfg.WindowTicks)
                {
                    if (_level == 0)
                        SetLevel(1, cfg, emaMs);
                    // Tier 2 (opt-in): throttling did not fix it and the EMA is past
                    // the emergency threshold - shut down zombie animators (~40% of
                    // the saturated 64p frame, RESULTS 3o + fence check).
                    else if (_level == 1 && cfg.AnimatorEmergency && emaMs > cfg.EmergencyOverMs)
                        SetLevel(2, cfg, emaMs);
                }
                // Periodic sweep while in tier 2 so mid-emergency spawns are covered.
                // _overTicks > 0 skips the entry tick: SetLevel(2) just swept the
                // world and reset the counter, and 0 % period == 0 would re-run it
                // immediately in the same tick.
                if (_level == 2 && _overTicks > 0 && _overTicks % SweepPeriodTicks == 0)
                    AnimatorEmergency.Enter();
            }
            else if (emaMs < cfg.HealthyMs)
            {
                _overTicks = 0;
                if (++_healthyTicks >= cfg.WindowTicks && _level > 0 && _cooldown == 0)
                    SetLevel(_level - 1, cfg, emaMs); // step down one tier at a time
            }
            else
            {
                _overTicks = 0;
                _healthyTicks = 0;
            }
        }

        static void SetLevel(int level, GovernorConfig cfg, double emaMs)
        {
            PathfindingConfig path = ModApi.Config.Pathfinding;
            NetworkConfig net = ModApi.Config.Network;
            // Baselines are captured once per config generation (reset by reload):
            // the values the operator actually configured, which recovery restores.
            if (_baseGraphEvery < 0)
                _baseGraphEvery = path.GraphUpdateEveryTicks;
            if (_baseEntityStride < 0)
                _baseEntityStride = net.EntityDistributionEveryTicks;

            int previous = _level;
            _level = level;
            _overTicks = 0;
            _healthyTicks = 0;
            _cooldown = cfg.CooldownTicks;
            if (level == 2)
            {
                // Tier 2 keeps the tier-1 throttles active (early return below),
                // so a mid-tier-2 reload must re-apply those tier-1 values too.
                // WARNING, not info: tier 2 globally degrades combat fidelity and is
                // opt-in, so firing means the operator both opted in AND the server
                // is past the emergency threshold - exactly what grepping WRN finds.
                ModApi.Warn($"Governor: tick EMA {emaMs:F1}ms > {cfg.EmergencyOverMs}ms despite throttles "
                    + "- ANIMATOR EMERGENCY CullCompletely (combat timing degrades; clients see no visual change)");
                AnimatorEmergency.Enter();
                return;
            }
            if (previous == 2)
                AnimatorEmergency.Exit();
            if (level == 1)
            {
                ApplyThrottledLevers(path, net);
                ModApi.Log(previous == 2
                    ? $"Governor: tick EMA {emaMs:F1}ms < {cfg.HealthyMs}ms - stepped down from emergency to THROTTLED"
                    : $"Governor: tick EMA {emaMs:F1}ms > {cfg.OverBudgetMs}ms - THROTTLED "
                      + $"(replication /{net.EntityDistributionEveryTicks}, graph updates /{path.GraphUpdateEveryTicks})");
            }
            else
            {
                net.EntityDistributionEveryTicks = _baseEntityStride;
                path.GraphUpdateEveryTicks = _baseGraphEvery;
                ModApi.Log($"Governor: tick EMA {emaMs:F1}ms < {cfg.HealthyMs}ms - restored baseline "
                    + $"(replication /{_baseEntityStride}, graph updates /{_baseGraphEvery})");
            }
        }

        // The one place that maps baseline -> doubled lever values. Shared by the
        // escalate path and the mid-tier config reload so they cannot drift apart.
        static void ApplyThrottledLevers(PathfindingConfig path, NetworkConfig net)
        {
            net.EntityDistributionEveryTicks =
                GovernorTiers.ThrottleLever(_baseEntityStride, GovernorTiers.EntityStrideMax);
            path.GraphUpdateEveryTicks =
                GovernorTiers.ThrottleLever(_baseGraphEvery, GovernorTiers.GraphUpdateMax);
        }

        /// <summary>
        /// Re-base the governor after <see cref="ModApi.ReloadConfig"/> swaps the
        /// config object. The governor mutates Pathfinding.GraphUpdateEveryTicks and
        /// Network.EntityDistributionEveryTicks IN PLACE as its throttle channel, so
        /// a reload would otherwise desync it: the cached baselines still held the
        /// previous object's values and the next step-down would clobber the
        /// operator's reloaded values with them, while a reload mid-tier
        /// silently dropped the applied throttles (fresh object carries operator
        /// values) until the next transition. Main-thread only (console/telnet/web
        /// commands queue through SdtdConsole's main-thread drain, same thread as the
        /// UpdateTick postfix), so plain field writes suffice.
        /// </summary>
        public static void OnConfigReloaded()
        {
            _baseGraphEvery = -1;
            _baseEntityStride = -1;
            if (_level <= 0)
                return; // baseline tier: the fresh object is already correct

            GovernorConfig cfg = ModApi.Config != null ? ModApi.Config.Governor : null;
            // The master switch counts too: Enabled=false promises "every patch
            // installed but inert", and the postfix stops running under it, so a
            // tier-2 emergency left standing would freeze every enemy rig at
            // CullCompletely with no code path left to restore it.
            bool governorInactive = ModApi.Config == null || !ModApi.Config.Enabled
                || cfg == null || !cfg.Enabled;
            if (governorInactive)
            {
                // Governor removed/disabled (or mod disabled) mid-tier: stand the
                // levers down on the new object; exit an active tier-2 emergency
                // so rigs cannot stay CullCompletely with no governor left to
                // recover them.
                if (_level >= 2)
                    AnimatorEmergency.Exit();
                _level = 0;
                ModApi.Log("config reloaded: governor inactive (disabled or master off) - "
                    + "levers left at reloaded (baseline) values");
                return;
            }

            // Tier 2 is opt-in, so flipping AnimatorEmergency off mid-emergency must
            // release the rigs NOW (reload applies live). Without this the postfix's
            // periodic tier-2 sweep would keep re-entering CullCompletely despite the
            // flag being false; step down to tier 1, which keeps the throttle levers.
            if (_level >= 2 && !cfg.AnimatorEmergency)
            {
                AnimatorEmergency.Exit();
                _level = 1;
                ModApi.Log("config reloaded: AnimatorEmergency off - stepped down from emergency to THROTTLED");
            }

            // Active tier (1 or 2): re-capture the baselines from the new object and
            // re-apply the tier-1 throttle values so throttling stays coherent across
            // the swap. Tier 2 keeps those same lever values (SetLevel(2) never
            // touches them). Tick-health windows and cooldown describe recent tick
            // history, not config state - keep them.
            PathfindingConfig path = ModApi.Config.Pathfinding;
            NetworkConfig net = ModApi.Config.Network;
            _baseGraphEvery = path.GraphUpdateEveryTicks;
            _baseEntityStride = net.EntityDistributionEveryTicks;
            ApplyThrottledLevers(path, net);
            ModApi.Log($"config reloaded: governor tier {_level} re-applied to new config "
                + $"(replication /{net.EntityDistributionEveryTicks}, graph updates /{path.GraphUpdateEveryTicks})");
        }
    }
}
