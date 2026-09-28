using System.Globalization;
using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Adaptive load governor (default on). Watches the real tick interval and
    /// moves the two proven throttle levers between the operator's configured
    /// values and doubled, capped ones:
    ///
    ///   - Network.EntityDistributionEveryTicks: configured value &lt;-&gt; 2x (capped 4;
    ///     from the default 1 that is 1 &lt;-&gt; 2 = 20 &lt;-&gt; 10 Hz, -45% on the replication
    ///     wall, see RESULTS 3g)
    ///   - Pathfinding.GraphUpdateEveryTicks: configured value &lt;-&gt; 2x that value
    ///
    /// Sustained over-budget ticks (interval EMA &gt; OverBudgetMs for WindowTicks)
    /// escalate one step; sustained healthy ticks (EMA &lt; HealthyMs) after a
    /// cooldown step back down. Hysteresis (OverBudgetMs &gt; HealthyMs gap) plus the
    /// cooldown prevent oscillation. Every transition is logged so an operator can
    /// see exactly when and why fidelity was traded for tick rate.
    ///
    /// The governor only moves levers between the OPERATOR'S CONFIGURED BASELINE and
    /// a doubled, capped throttle value - it introduces no new behavior, it
    /// schedules existing, individually-validated ones. It never writes to the
    /// config: the throttled values are derived per read from the configured ones
    /// plus the current tier (see <see cref="GovernorTiers"/>), so an operator's
    /// non-vanilla steady state (e.g. EntityDistributionEveryTicks=3) survives a
    /// governor cycle and a `es reload` unchanged.
    ///
    /// The hysteresis arithmetic itself lives in <see cref="GovernorTiers"/> (pure,
    /// unit-tested); this class is the game-facing adapter: it advances the EMA,
    /// drives the machine, and performs the tier-2 side effect on the rigs.
    /// </summary>
    [HarmonyPatch(typeof(GameManager), "UpdateTick")]
    public static class GovernorPatch
    {
        static readonly TickIntervalEma TickEma = new TickIntervalEma();
        static readonly GovernorTiers Tiers = new GovernorTiers();

        // Live state for `es status` / incident response: which tier is applied
        // RIGHT NOW (config alone cannot tell you this, since config is intent) and
        // the smoothed tick interval driving it.
        public static int Level { get { return Tiers.Level; } }
        public static double EmaMs { get { return TickEma.Value; } }

        // The two levers a throttled tier touches, as the CONSUMING patches see
        // them: the configured value at baseline, doubled and capped above it.
        // Callers pass the value they read from the config; the governor is never
        // the writer of the file's own numbers. A disabled governor reads as
        // baseline even if a tier was somehow left standing, so the effective
        // value can never outlive the lever that produced it.
        public static int EffectiveEntityStride(int configured)
            => GovernorEnabled() ? Tiers.EffectiveEntityStride(configured) : configured;

        public static int EffectiveGraphEvery(int configured)
            => GovernorEnabled() ? Tiers.EffectiveGraphEvery(configured) : configured;

        // Config-reading twins, for `es status`, which reports the configured
        // values in the dump and the values actually in force here.
        public static int EffectiveEntityStride()
        {
            NetworkConfig net = ModApi.Config != null ? ModApi.Config.Network : null;
            return net == null ? 1 : EffectiveEntityStride(net.EntityDistributionEveryTicks);
        }

        public static int EffectiveGraphEvery()
        {
            PathfindingConfig path = ModApi.Config != null ? ModApi.Config.Pathfinding : null;
            return path == null ? 1 : EffectiveGraphEvery(path.GraphUpdateEveryTicks);
        }

        static bool GovernorEnabled()
        {
            GovernorConfig cfg = ModApi.Config != null ? ModApi.Config.Governor : null;
            return cfg != null && cfg.Enabled;
        }

        static void Postfix()
        {
            GovernorConfig cfg = ModApi.Config != null ? ModApi.Config.Governor : null;
            if (!ModApi.ShouldRun() || cfg == null || !cfg.Enabled)
                return;

            double emaMs = TickEma.Advance();
            if (Tiers.Advance(cfg, emaMs))
            {
                if (Tiers.Level == 2)
                {
                    LogEmergencyEnter(cfg, emaMs);
                }
                else if (Tiers.Level == 1)
                {
                    // Tier 2 keeps the tier-1 throttles in force, so a step down
                    // from the emergency leaves them applied; only the rigs go.
                    LogStepDown(cfg, emaMs, "stepped down from emergency to THROTTLED");
                    AnimatorEmergency.Exit();
                }
                else
                    LogRestored(cfg, emaMs);
            }
            else if (Tiers.SweepDue)
            {
                // Still in tier 2: re-sweep so rigs spawned mid-emergency are covered.
                AnimatorEmergency.Enter();
            }
        }

        static void LogRestored(GovernorConfig cfg, double emaMs)
        {
            EsLog.Emit(LogLevel.Info, $"Governor: tick EMA {Ms(emaMs)}ms < "
                + $"{Ms(cfg.HealthyMs)}ms - restored baseline "
                + $"(replication /{EffectiveEntityStride()}, graph updates /{EffectiveGraphEvery()})");
        }

        static void LogStepDown(GovernorConfig cfg, double emaMs, string what)
        {
            EsLog.Emit(LogLevel.Info, $"Governor: tick EMA {Ms(emaMs)}ms < "
                + $"{Ms(cfg.HealthyMs)}ms - {what}");
        }

        // WARNING, not info: tier 2 globally degrades combat fidelity and is
        // opt-in, so firing means the operator both opted in AND the server
        // is past the emergency threshold - exactly what grepping WRN finds.
        // Log floats render invariant, same convention as es status / es
        // animstate: the log is grepped across hosts, and a comma-decimal
        // locale must not reformat these values.
        static void LogEmergencyEnter(GovernorConfig cfg, double emaMs)
        {
            EsLog.Emit(LogLevel.Warn, $"Governor: tick EMA {Ms(emaMs)}ms > "
                + $"{Ms(cfg.EmergencyOverMs)}ms despite throttles "
                + "- ANIMATOR EMERGENCY CullCompletely (combat timing degrades; clients see no visual change)");
            AnimatorEmergency.Enter();
        }

        static string Ms(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

        /// <summary>
        /// Re-base the governor when a NEW world loads (called from the
        /// <c>GameStartDone</c> hook). The tier and its windows are derived from
        /// tick history of the world that just unloaded, and a standing tier-2
        /// emergency is the one piece of governor state that outlives a world: its
        /// saved modes name rigs the new world never had, and its Active flag would
        /// freeze managed animator updates for enemies that were never culled. Both
        /// are dropped instead of carried over, and the new world re-escalates on
        /// its own ticks if it turns out to be over budget.
        /// Main-thread only (the lifecycle hook fires on the same thread as the
        /// UpdateTick postfix).
        /// </summary>
        public static void OnWorldChanged()
        {
            if (Tiers.Level == 0 && !AnimatorEmergency.Active) return;
            Tiers.ResetForNewWorld();
            EsLog.Emit(LogLevel.Info, "new world: governor re-based to baseline "
                + "(tier, windows and any standing animator emergency belonged to the previous world)");
            AnimatorEmergency.ForgetWorld();
        }

        /// <summary>
        /// Re-base the governor after <see cref="ModApi.ReloadConfig"/> swaps the
        /// config object. The levers need no re-applying (they are derived from the
        /// new object on every read); what a reload must settle is the TIER, so a
        /// tier-2 emergency the new config no longer authorizes is released NOW
        /// rather than left to a postfix that may never run again.
        /// Main-thread only (console/telnet/web commands queue through
        /// SdtdConsole's main-thread drain, same thread as the UpdateTick postfix).
        /// </summary>
        public static void OnConfigReloaded()
        {
            GovernorConfig cfg = ModApi.Config != null ? ModApi.Config.Governor : null;
            // The master switch counts too: Enabled=false promises "every patch
            // installed but inert", and the postfix stops running under it.
            bool active = ModApi.Config != null && ModApi.Config.Enabled
                && cfg != null && cfg.Enabled;
            bool releaseRigs = Tiers.ApplyReloadedConfig(cfg, active);
            if (releaseRigs)
            {
                AnimatorEmergency.Exit();
                EsLog.Emit(LogLevel.Info, "config reloaded: animator emergency no longer authorized "
                    + "(governor disabled, or AnimatorEmergency off) - released rigs, "
                    + "levers read at configured values again");
            }
            // A stray BENCH-PROBE emergency (`es animoff`; the probe never raises the
            // tier) must not survive a reload that turns the mod or governor off: no
            // postfix/tier machine remains to step down, so rigs would keep
            // CullCompletely plus skipped managed updates forever, and Enabled=false
            // promises inert/vanilla. Under an active governor the probe stays
            // operator-owned (`es animon` exits it), matching the manual enter/exit
            // contract.
            if (!active && Tiers.Level == 0 && AnimatorEmergency.Active
                && AnimatorEmergency.Exit())
            {
                EsLog.Emit(LogLevel.Info, "config reloaded: governor inactive (disabled or master off) - "
                    + "released animator emergency left armed by the es animoff probe");
            }
            if (!active)
                EsLog.Emit(LogLevel.Info, "config reloaded: governor inactive (disabled or master off) - "
                    + "levers left at reloaded (baseline) values");
        }
    }
}
