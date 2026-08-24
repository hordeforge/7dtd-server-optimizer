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
            _applied = true;
            try
            {
                BoehmNative.GC_enable_incremental();
                if (cfg.IncrementalPauseTargetMs > 0)
                    BoehmNative.GC_set_time_limit_ns((long)cfg.IncrementalPauseTargetMs * 1_000_000L);
                EsLog.Log("GC incremental mode enabled"
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
                EsLog.Warn("GC incremental enable failed [" + ex.GetType().Name
                    + " via " + BoehmNative.Lib + "]: " + ex.Message);
            }
        }
    }
}
