using System;
using System.Diagnostics;
using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Adaptive load governor (default on). Watches the real tick interval and moves
    /// the two proven throttle levers between their vanilla and throttled settings:
    ///
    ///   - Network.EntityDistributionEveryTicks: 1 (20 Hz) <-> 2 (10 Hz, -45% on the
    ///     replication wall, see RESULTS 3g)
    ///   - Pathfinding.GraphUpdateEveryTicks: configured value <-> 2x that value
    ///
    /// Sustained over-budget ticks (interval EMA > OverBudgetMs for WindowTicks)
    /// escalate one step; sustained healthy ticks (EMA < HealthyMs) after a cooldown
    /// step back down. Hysteresis (OverBudgetMs > HealthyMs gap) plus the cooldown
    /// prevent oscillation. Every transition is logged so an operator can see exactly
    /// when and why fidelity was traded for tick rate.
    ///
    /// The governor only ever moves between VANILLA and the measured throttled
    /// settings of levers that ship in this mod - it introduces no new behavior, it
    /// schedules existing, individually-validated ones.
    /// </summary>
    [HarmonyPatch(typeof(GameManager), "UpdateTick")]
    public static class GovernorPatch
    {
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static double _lastTickMs;
        static double _emaMs = 50.0;
        static int _overTicks;
        static int _healthyTicks;
        static int _cooldown;
        static int _level; // 0 = vanilla, 1 = throttled
        static int _baseGraphEvery = -1;

        static void Postfix()
        {
            GovernorConfig cfg = ModApi.Config != null ? ModApi.Config.Governor : null;
            if (!ModApi.ShouldRun() || cfg == null || !cfg.Enabled)
                return;

            double now = Clock.Elapsed.TotalMilliseconds;
            if (_lastTickMs > 0)
            {
                double interval = now - _lastTickMs;
                // EMA over ~32 ticks: cheap, smooths spawn spikes without hiding trends.
                _emaMs += (interval - _emaMs) / 32.0;
            }
            _lastTickMs = now;
            if (_cooldown > 0) _cooldown--;

            if (_emaMs > cfg.OverBudgetMs)
            {
                _healthyTicks = 0;
                _overTicks++;
                if (_cooldown == 0 && _overTicks >= cfg.WindowTicks)
                {
                    if (_level == 0)
                        SetLevel(1, cfg);
                    // Tier 2 (opt-in): throttling did not fix it and the EMA is past
                    // the emergency threshold - shut down zombie animators (~40% of
                    // the saturated 64p frame, RESULTS 3o + fence check).
                    else if (_level == 1 && cfg.AnimatorEmergency && _emaMs > cfg.EmergencyOverMs)
                        SetLevel(2, cfg);
                }
                // Periodic sweep while in tier 2 so mid-emergency spawns are covered.
                if (_level == 2 && _overTicks % 100 == 0)
                    AnimatorEmergency.Enter();
            }
            else if (_emaMs < cfg.HealthyMs)
            {
                _overTicks = 0;
                if (++_healthyTicks >= cfg.WindowTicks && _level > 0 && _cooldown == 0)
                    SetLevel(_level - 1, cfg); // step down one tier at a time
            }
            else
            {
                _overTicks = 0;
                _healthyTicks = 0;
            }
        }

        static void SetLevel(int level, GovernorConfig cfg)
        {
            PathfindingConfig path = ModApi.Config.Pathfinding;
            NetworkConfig net = ModApi.Config.Network;
            if (_baseGraphEvery < 0)
                _baseGraphEvery = path.GraphUpdateEveryTicks;

            int previous = _level;
            _level = level;
            _overTicks = 0;
            _healthyTicks = 0;
            _cooldown = cfg.CooldownTicks;
            if (level == 2)
            {
                // Tier 2 keeps the tier-1 throttles active (early return below),
                // so a mid-tier-2 reload must re-apply those tier-1 values too.
                ModApi.Log($"Governor: tick EMA {_emaMs:F1}ms > {cfg.EmergencyOverMs}ms despite throttles "
                    + "- ANIMATOR EMERGENCY CullCompletely (combat timing degrades; clients see no visual change)");
                AnimatorEmergency.Enter();
                return;
            }
            if (previous == 2)
                AnimatorEmergency.Exit();
            if (level == 1)
            {
                net.EntityDistributionEveryTicks = 2;
                path.GraphUpdateEveryTicks = Math.Min(200, _baseGraphEvery * 2);
                ModApi.Log(previous == 2
                    ? $"Governor: tick EMA {_emaMs:F1}ms < {cfg.HealthyMs}ms - stepped down from emergency to THROTTLED"
                    : $"Governor: tick EMA {_emaMs:F1}ms > {cfg.OverBudgetMs}ms - THROTTLED "
                      + $"(replication 10 Hz, graph updates /{path.GraphUpdateEveryTicks})");
            }
            else
            {
                net.EntityDistributionEveryTicks = 1;
                path.GraphUpdateEveryTicks = _baseGraphEvery;
                ModApi.Log($"Governor: tick EMA {_emaMs:F1}ms < {cfg.HealthyMs}ms - restored vanilla "
                    + $"(replication 20 Hz, graph updates /{_baseGraphEvery})");
            }
        }

        /// <summary>
        /// Re-base the governor after <see cref="ModApi.ReloadConfig"/> swaps the
        /// config object. The governor mutates Pathfinding.GraphUpdateEveryTicks and
        /// Network.EntityDistributionEveryTicks IN PLACE as its throttle channel, so
        /// a reload would otherwise desync it: the cached _baseGraphEvery still held
        /// the previous object's value and the next step-down would clobber the
        /// operator's reloaded GraphUpdateEveryTicks with it, while a reload mid-tier
        /// silently dropped the applied throttles (fresh object carries operator
        /// values) until the next transition. Main-thread only (console/telnet/web
        /// commands queue through SdtdConsole's main-thread drain, same thread as the
        /// UpdateTick postfix), so plain field writes suffice.
        /// </summary>
        public static void OnConfigReloaded()
        {
            _baseGraphEvery = -1;
            if (_level <= 0)
                return; // vanilla tier: the fresh object is already correct

            GovernorConfig cfg = ModApi.Config != null ? ModApi.Config.Governor : null;
            if (cfg == null || !cfg.Enabled)
            {
                // Governor removed/disabled mid-tier: stand the levers down on the
                // new object; exit an active tier-2 emergency so rigs cannot stay
                // CullCompletely with no governor left to recover them.
                if (_level >= 2)
                    AnimatorEmergency.Exit();
                _level = 0;
                ModApi.Log("config reloaded: governor disabled - levers left at reloaded (vanilla) values");
                return;
            }

            // Active tier (1 or 2): re-apply the tier-1 throttle values onto the new
            // object so throttling stays coherent across the swap. Tier 2 keeps those
            // same lever values (SetLevel(2) never touches them). Tick-health windows
            // and cooldown describe recent tick history, not config state - keep them.
            PathfindingConfig path = ModApi.Config.Pathfinding;
            NetworkConfig net = ModApi.Config.Network;
            _baseGraphEvery = path.GraphUpdateEveryTicks;
            net.EntityDistributionEveryTicks = 2;
            path.GraphUpdateEveryTicks = Math.Min(200, _baseGraphEvery * 2);
            ModApi.Log($"config reloaded: governor tier {_level} re-applied to new config "
                + $"(replication 10 Hz, graph updates /{path.GraphUpdateEveryTicks})");
        }
    }
}
