using System;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Pure tier machine behind <see cref="GovernorPatch"/>: the hysteresis, window
    /// and cooldown arithmetic plus the lever math, with no game types, so it runs
    /// under the plain .NET unit harness (same reason <see cref="TickIntervalEma"/>
    /// and <see cref="TickClock"/> live in their own files).
    ///
    /// The machine never writes to <see cref="ServerPerfConfig"/>. Config is the
    /// operator's declared intent and stays exactly what they configured; the live
    /// throttle is DERIVED from that intent plus the current tier
    /// (<see cref="EffectiveEntityStride"/> / <see cref="EffectiveGraphEvery"/>), so
    /// "configured" and "applied" cannot drift and a config swap needs no re-basing
    /// of anything: the next read already reflects the new file. Mutating the
    /// config object as the throttle channel was the alternative, and it is what
    /// forced the governor to cache operator baselines and re-apply them on every
    /// reload, with a real chance of restoring stale values.
    ///
    /// Main-thread confined: the only caller is the GameManager.UpdateTick postfix
    /// in <see cref="GovernorPatch"/> (and the main-thread console reload drain).
    /// </summary>
    internal sealed class GovernorTiers
    {
        // Tier-2 rigs are re-swept every this many over-budget ticks (~5 s at the
        // vanilla 20 TPS) so zombies that spawn mid-emergency are covered too.
        const int SweepPeriodTicks = 100;

        int _overTicks;
        int _healthyTicks;
        int _cooldown;

        /// <summary>0 = configured baseline, 1 = throttled, 2 = animator emergency.</summary>
        public int Level { get; private set; }

        /// <summary>
        /// True on the tick the machine asks for a tier-2 rig re-sweep (a periodic
        /// re-entry while the emergency holds, not a transition). False otherwise.
        /// </summary>
        public bool SweepDue { get; private set; }

        /// <summary>
        /// Record one tick's smoothed interval; returns true when the tier changed
        /// this tick. Transition bookkeeping (window and cooldown reset) happens
        /// here so a caller cannot apply a tier without it.
        /// </summary>
        public bool Advance(GovernorConfig cfg, double emaMs)
        {
            SweepDue = false;
            if (_cooldown > 0) _cooldown--;

            int previous = Level;
            if (emaMs > cfg.OverBudgetMs)
            {
                _healthyTicks = 0;
                _overTicks++;
                if (_cooldown == 0 && _overTicks >= cfg.WindowTicks)
                {
                    if (Level == 0) Level = 1;
                    else if (Level == 1 && cfg.AnimatorEmergency && emaMs > cfg.EmergencyOverMs) Level = 2;
                }
            }
            else if (emaMs < cfg.HealthyMs)
            {
                _overTicks = 0;
                if (++_healthyTicks >= cfg.WindowTicks && Level > 0 && _cooldown == 0)
                    Level--; // step down one tier at a time
            }
            else
            {
                // Hysteresis band: neither window advances, so a server hovering
                // between the thresholds holds its tier.
                _overTicks = 0;
                _healthyTicks = 0;
            }

            if (Level != previous)
            {
                _overTicks = 0;
                _healthyTicks = 0;
                _cooldown = cfg.CooldownTicks;
                return true;
            }
            // A transition just reset the window, so the entry tick never also
            // sweeps; later tier-2 ticks do, on a fixed period.
            if (Level == 2 && _overTicks > 0 && _overTicks % SweepPeriodTicks == 0)
                SweepDue = true;
            return false;
        }

        /// <summary>
        /// Replication stride actually in force: the configured value at baseline,
        /// a doubled-and-capped value while throttled. Doubling is relative to the
        /// operator's own value, so a throttle never runs a lever FASTER than they
        /// configured, and the ceiling is the same one Normalize enforces.
        /// </summary>
        public int EffectiveEntityStride(int configured) =>
            Level > 0 ? ThrottleLever(configured, ServerPerfConfig.EntityStrideMax) : configured;

        /// <summary>Nav-graph update cadence actually in force; see the stride twin.</summary>
        public int EffectiveGraphEvery(int configured) =>
            Level > 0 ? ThrottleLever(configured, ServerPerfConfig.GraphUpdateMax) : configured;

        /// <summary>The one baseline -&gt; doubled lever mapping, capped at the config ceiling.</summary>
        /// <remarks>
        /// The doubling is computed in <see cref="long"/>, not int: a plain
        /// <c>configured * 2</c> wraps negative past 2^30, and a negative
        /// double then loses to <c>Math.Max</c>, so the lever silently reads as
        /// the operator's own value instead of the doubled one the contract
        /// promises. Every caller passes a Normalize-clamped value (stride 4,
        /// cadence 200) so the wrap is unreachable today, but this is a public
        /// pure mapping with its own test, and the long form costs nothing.
        /// </remarks>
        public static int ThrottleLever(int configured, int maxValue)
            => (int)Math.Min((long)maxValue, Math.Max((long)configured, (long)configured * 2));

        /// <summary>
        /// Re-base after <see cref="ModApi.ReloadConfig"/> swapped the config
        /// object. Nothing needs re-applying (the levers are derived per read), but
        /// the tier itself may no longer be authorized: returns true when the caller
        /// must release a standing tier-2 emergency, because the governor is now
        /// inactive, or because the operator turned AnimatorEmergency off mid
        /// emergency (tier 1 keeps the throttles).
        /// </summary>
        public bool ApplyReloadedConfig(GovernorConfig cfg, bool governorActive)
        {
            if (!governorActive)
            {
                // Disabled (or master off) with no postfix left to step down: rigs
                // would stay CullCompletely forever and the levers must read as
                // configured. Windows describe pre-reload tick history, so drop them.
                bool releaseRigs = Level >= 2;
                Level = 0;
                _overTicks = 0;
                _healthyTicks = 0;
                _cooldown = 0;
                return releaseRigs;
            }
            if (Level >= 2 && !cfg.AnimatorEmergency)
            {
                Level = 1;
                return true;
            }
            return false;
        }
    }
}
