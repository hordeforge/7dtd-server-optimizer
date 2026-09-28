using System.Diagnostics;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Exponential moving average of the tick interval, used by BOTH the governor
    /// (<see cref="GovernorPatch"/>) and the tick guard (<see cref="TickGuardPatch"/>)
    /// so throttle and shed decisions key off the same measurement. Each holder owns
    /// its OWN instance rather than sharing one object: both run as UpdateTick
    /// postfixes on the main thread, so a shared instance would be advanced twice per
    /// tick (the second call would read a ~0 ms gap and drag the average down), and
    /// either gate can be disabled alone. Instances seeded identically and stepped
    /// once per tick measure the same gaps, so their values are equivalent - so every
    /// holder must also call <see cref="Reseed"/> at the same points, or they drift.
    /// Alpha 1/32 (~32-tick memory): cheap, smooths spawn spikes without hiding
    /// trends. Seeds at the vanilla 50 ms idle interval so the first ticks after boot
    /// read as healthy instead of as a spike from an arbitrary seed.
    ///
    /// The production path reads a Stopwatch started at construction; the
    /// <see cref="Advance(double)"/> overload takes the tick timestamp explicitly so
    /// tests can replay identical tick sequences deterministically instead of
    /// inheriting host scheduler jitter into governor/tick-guard transitions.
    /// </summary>
    internal sealed class TickIntervalEma
    {
        // The vanilla idle interval: an unloaded 20 TPS loop never measures below it,
        // so this is the floor the smoother relaxes to between spikes.
        const double SeedMs = 50.0;

        readonly Stopwatch _clock = Stopwatch.StartNew();
        double _lastTickMs;
        double _ms = SeedMs;

        /// <summary>Record one tick; returns the smoothed interval in ms.</summary>
        public double Advance()
        {
            return Advance(_clock.Elapsed.TotalMilliseconds);
        }

        // Explicit-timestamp variant: the pure transition function behind Advance().
        // nowMs must be monotonic across calls; the first positive call only records
        // the baseline (no gap is averaged yet).
        public double Advance(double nowMs)
        {
            if (_lastTickMs > 0)
                _ms += (nowMs - _lastTickMs - _ms) / 32.0;
            _lastTickMs = nowMs;
            return _ms;
        }

        /// <summary>Smoothed interval in ms as of the last <see cref="Advance"/>.</summary>
        public double Value { get { return _ms; } }

        /// <summary>
        /// Drop the sample history and start over from the seed, so the next
        /// <see cref="Advance"/> records a baseline instead of averaging one gap.
        ///
        /// Call this on a world change. The gap between the last UpdateTick of the
        /// outgoing world and the first of the incoming one is the whole world load,
        /// not a tick: feeding it to the recurrence adds gap/32 to the average in a
        /// single step (a 30 s load lands the EMA near 1 s, ~20x the idle interval),
        /// and the alpha-1/32 memory then needs tens of ticks to fall back. Both
        /// consumers make irreversible, gameplay-affecting decisions off that value
        /// (governor throttle tiers, emergency rig culling; tick-guard entity
        /// removal), and their escalation windows are counted in ticks, so a stale
        /// spike can carry a previous world's overload across into a freshly loaded
        /// one and fire before the new world has had a chance to be slow.
        ///
        /// Deliberately does NOT read the clock: the point is to forget the
        /// pre-world-change sample, so recording "now" as a baseline would average
        /// exactly the load stall this discards. The next tick's timestamp becomes
        /// the baseline and the one after that the first real measurement.
        /// Main-thread confined, like every other holder of this state.
        /// </summary>
        public void Reseed()
        {
            _ms = SeedMs;
            _lastTickMs = 0;
        }
    }
}
