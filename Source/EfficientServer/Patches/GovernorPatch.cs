using System;
using System.Diagnostics;
using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Adaptive load governor (default off). Watches the real tick interval and moves
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
                if (++_overTicks >= cfg.WindowTicks && _level == 0 && _cooldown == 0)
                    SetLevel(1, cfg);
            }
            else if (_emaMs < cfg.HealthyMs)
            {
                _overTicks = 0;
                if (++_healthyTicks >= cfg.WindowTicks && _level == 1 && _cooldown == 0)
                    SetLevel(0, cfg);
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

            _level = level;
            _overTicks = 0;
            _healthyTicks = 0;
            _cooldown = cfg.CooldownTicks;
            if (level == 1)
            {
                net.EntityDistributionEveryTicks = 2;
                path.GraphUpdateEveryTicks = Math.Min(200, _baseGraphEvery * 2);
                ModApi.Log($"Governor: tick EMA {_emaMs:F1}ms > {cfg.OverBudgetMs}ms - THROTTLED "
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
    }
}
