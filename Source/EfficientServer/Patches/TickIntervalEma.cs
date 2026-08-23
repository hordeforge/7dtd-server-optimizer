using System.Diagnostics;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Exponential moving average of the tick interval, shared by the governor
    /// (<see cref="GovernorPatch"/>) and the tick guard (<see cref="TickGuardPatch"/>)
    /// so both throttle and shed decisions read the SAME smoothed signal. Alpha
    /// 1/32 (~32-tick memory): cheap, smooths spawn spikes without hiding trends.
    /// Seeds at the vanilla 50 ms idle interval so the first ticks after boot read
    /// as healthy instead of as a spike from an arbitrary seed. Main-thread only
    /// (both callers run in GameManager.UpdateTick postfixes).
    /// </summary>
    sealed class TickIntervalEma
    {
        readonly Stopwatch _clock = Stopwatch.StartNew();
        double _lastTickMs;
        double _ms = 50.0;

        /// <summary>Record one tick; returns the smoothed interval in ms.</summary>
        public double Advance()
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            if (_lastTickMs > 0)
                _ms += (now - _lastTickMs - _ms) / 32.0;
            _lastTickMs = now;
            return _ms;
        }

        /// <summary>Smoothed interval in ms as of the last <see cref="Advance"/>.</summary>
        public double Value { get { return _ms; } }
    }
}
