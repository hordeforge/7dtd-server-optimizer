using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace EfficientServer
{
    /// <summary>
    /// DIAGNOSTIC ONLY (opt-in, default off). Measures the "megapause": disable the
    /// Boehm collector, let the heap grow under live load, then time a single forced
    /// full GC_gcollect. Shows how a never-collect-then-one-big-collect scheme trades
    /// many small pauses for one catastrophic multi-second stop-the-world freeze.
    /// Runs on a background thread; GC_gcollect is a global STW so the wall-clock
    /// around it is the freeze the main tick loop actually eats. Never ship enabled.
    /// </summary>
    internal static class GcDiagnostics
    {
        const string Lib = "monobdwgc-2.0";

        [DllImport(Lib)] static extern void GC_disable();
        [DllImport(Lib)] static extern void GC_enable();
        [DllImport(Lib)] static extern void GC_gcollect();
        [DllImport(Lib)] static extern UIntPtr GC_get_heap_size();

        // Hard safety cap: abort the grow + collect if the heap passes this, so a
        // misconfigured run cannot exhaust host RAM.
        const ulong SafetyCapBytes = 24UL * 1024 * 1024 * 1024;

        static ulong HeapBytes()
        {
            try { return (ulong)GC_get_heap_size(); }
            catch { return 0UL; }
        }

        static string Gb(ulong bytes) => (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2") + " GB";

        static bool _started;

        public static void StartMegapauseTest(DiagnosticsConfig cfg)
        {
            if (cfg == null || !cfg.GcMegapauseTest) return;
            // One shot per process: GameStartDone fires again if the world restarts
            // in-process, and overlapping megapause threads would interleave
            // GC_disable/GC_enable refcounts against the single collector.
            if (_started)
            {
                ModApi.Log("GC MEGAPAUSE diagnostic already armed this process; not re-arming");
                return;
            }
            _started = true;
            var t = new Thread(() => RunMegapause(cfg)) { IsBackground = true, Name = "es-gc-megapause" };
            t.Start();
            ModApi.Log("GC MEGAPAUSE diagnostic armed (DIAGNOSTIC ONLY): warmup="
                + cfg.WarmupSeconds + "s grow=" + cfg.GrowSeconds + "s");
        }

        static void RunMegapause(DiagnosticsConfig cfg)
        {
            // Tracks the GC_disable acquire so every release path matches it:
            // Boehm's enable/disable is a process-wide counter, and an enable
            // without a matching disable (a failure before the disable below)
            // skews that count for every other user of the collector.
            bool gcDisabled = false;
            try
            {
                // long math: seconds -> ms must not wrap int before Sleep sees it.
                Thread.Sleep((int)Math.Min(int.MaxValue, Math.Max(0, cfg.WarmupSeconds) * 1000L));
                ulong heap0 = HeapBytes();
                GC_disable();
                gcDisabled = true;
                ModApi.Log("GC MEGAPAUSE: collector DISABLED at heap=" + Gb(heap0) + "; growing under load...");

                int grow = Math.Max(1, cfg.GrowSeconds);
                var sw = Stopwatch.StartNew();
                bool aborted = false;
                while (sw.Elapsed.TotalSeconds < grow)
                {
                    Thread.Sleep(10000);
                    ulong h = HeapBytes();
                    ModApi.Log("GC MEGAPAUSE: +" + (int)sw.Elapsed.TotalSeconds + "s heap=" + Gb(h));
                    if (h >= SafetyCapBytes)
                    {
                        ModApi.Warn("GC MEGAPAUSE: SAFETY CAP hit (" + Gb(h) + "), collecting early");
                        aborted = true;
                        break;
                    }
                }

                ulong heapBefore = HeapBytes();
                // Re-enable BEFORE collecting: Boehm no-ops GC_gcollect while the
                // disable count is > 0, so collecting first measures a 0 ms no-op.
                GC_enable();
                gcDisabled = false;
                var pause = Stopwatch.StartNew();
                GC_gcollect();               // single forced full STW collect of the grown heap
                pause.Stop();
                ulong heapAfter = HeapBytes();

                double freedGb = (heapBefore > heapAfter)
                    ? (heapBefore - heapAfter) / (1024.0 * 1024.0 * 1024.0) : 0.0;
                ModApi.Log("GC MEGAPAUSE RESULT: "
                    + "grow=" + (int)sw.Elapsed.TotalSeconds + "s"
                    + (aborted ? " (safety-capped)" : "")
                    + " heap " + Gb(heap0) + " -> " + Gb(heapBefore) + " -> " + Gb(heapAfter)
                    + " freed=" + freedGb.ToString("F2") + " GB"
                    + " ** PAUSE_MS=" + pause.Elapsed.TotalMilliseconds.ToString("F0") + " **");
            }
            catch (Exception ex)
            {
                // Re-enable defensively so a failed probe never leaves GC disabled,
                // but only if THIS run disabled it (an unmatched enable skews the
                // process-wide counter; see gcDisabled above).
                if (gcDisabled)
                {
                    gcDisabled = false;
                    try { GC_enable(); } catch { }
                }
                // Name the type + library: DllNotFoundException (host OS ships the
                // Boehm lib under another name) reads differently from a missing
                // entry point, and the operator needs to know which one fired.
                ModApi.Warn("GC MEGAPAUSE failed [" + ex.GetType().Name
                    + " via " + Lib + "]: " + ex.Message);
            }
        }
    }
}
