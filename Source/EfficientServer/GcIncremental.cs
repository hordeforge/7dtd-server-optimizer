using System;
using System.Runtime.InteropServices;

namespace EfficientServer
{
    /// <summary>
    /// Switches the Boehm collector already in the process (Unity Mono
    /// monobdwgc) into incremental / generational mode: instead of one long
    /// stop-the-world pass, collection runs in small increments across frames
    /// (page-protection dirty tracking), with an optional per-pause time limit.
    /// This is a MODE of the existing GC, not a replacement - you cannot swap
    /// the collector on Unity Mono. P/Invoke into the game's own bundled lib,
    /// server-internal, changes no wire bytes. Opt-in (`Gc.Incremental`) because
    /// the write-barrier adds per-allocation overhead whose net value depends on
    /// the workload - measure with the APM GC window before retaining.
    /// </summary>
    internal static class GcIncremental
    {
        // Same library the game loads for Boehm; matches the bridge's P/Invoke.
        // This is the LINUX bundled name (libmonobdwgc-2.0.so); other host OSes
        // ship the same collector under a different module name, so the calls
        // below fail soft with DllNotFoundException there by design.
        const string Lib = "monobdwgc-2.0";

        [DllImport(Lib)] static extern void GC_enable_incremental();
        [DllImport(Lib)] static extern void GC_set_time_limit_ns(long ns);

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
                GC_enable_incremental();
                if (cfg.IncrementalPauseTargetMs > 0)
                    GC_set_time_limit_ns((long)cfg.IncrementalPauseTargetMs * 1_000_000L);
                ModApi.Log("GC incremental mode enabled"
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
                ModApi.Warn("GC incremental enable failed [" + ex.GetType().Name
                    + " via " + Lib + "]: " + ex.Message);
            }
        }
    }
}
