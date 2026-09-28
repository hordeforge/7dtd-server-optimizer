using System;

namespace EfficientServer
{
    /// <summary>
    /// Switches the Boehm collector already in the process (Unity Mono
    /// monobdwgc) into incremental / generational mode: instead of one long
    /// stop-the-world pass, collection runs in small increments across frames
    /// (page-protection dirty tracking), with an optional per-pause time limit.
    /// This is a MODE of the existing GC, not a replacement - you cannot swap
    /// the collector on Unity Mono. P/Invokes via <see cref="BoehmNative"/> into
    /// the game's own bundled lib, server-internal, changes no wire bytes.
    /// Opt-in (`Gc.Incremental`) because the write-barrier adds per-allocation
    /// overhead whose net value depends on the workload - measure with the APM
    /// GC window before retaining.
    /// </summary>
    internal static class GcIncremental
    {
        static bool _applied;

        public static void Apply()
        {
            // Respect the master switch like every sibling GameStartDone action:
            // the Boehm mode flip is a one-shot P/Invoke that cannot be undone, so
            // it must not fire when the mod is disabled or off a dedicated server.
            GcConfig cfg = ModApi.Config != null ? ModApi.Config.Gc : null;
            if (_applied || cfg == null || !cfg.Incremental || !ModApi.ShouldRun()) return;
            try
            {
                BoehmNative.GC_enable_incremental();
                // Marked only once the mode flip actually landed: a missing entry
                // point must stay retryable, so a later `es reload` (or the
                // GameStartDone hook re-firing) can apply it once the host library
                // is reachable. Booking the flag before the call made a failed
                // enable indistinguishable from an applied one for the rest of
                // the process.
                _applied = true;
                // Separate try: the mode flip already happened, so a missing
                // time-limit entry point must not report the whole apply as
                // failed and re-run the flip.
                try
                {
                    if (cfg.IncrementalPauseTargetMs > 0)
                        BoehmNative.GC_set_time_limit_ns((long)cfg.IncrementalPauseTargetMs * 1_000_000L);
                }
                catch (Exception ex)
                {
                    EsLog.Emit(LogLevel.Warn, "GC incremental pause target not applied ["
                        + ex.GetType().Name + " via " + BoehmNative.Lib + "]: " + ex.Message
                        + " - incremental mode stays on without a per-pause time limit");
                }
                EsLog.Emit(LogLevel.Info, "GC incremental mode enabled"
                    + (cfg.IncrementalPauseTargetMs > 0
                        ? " (pauseTargetMs=" + cfg.IncrementalPauseTargetMs + ")"
                        : ""));
            }
            catch (Exception ex)
            {
                // Symbol absent on some builds -> stay on the default STW collector.
                // Name the type + library: a missing module (host OS bundles the
                // Boehm lib under another name) is a different problem from a
                // missing entry point, and the log must say which one fired. An
                // opt-in lever that silently did not apply is WARNING material.
                // _applied stays false, so an `es reload` retries instead of
                // reporting success for a mode flip that never happened.
                EsLog.Emit(LogLevel.Warn, "GC incremental enable failed [" + ex.GetType().Name
                    + " via " + BoehmNative.Lib + "]: " + ex.Message);
            }
        }
    }
}
