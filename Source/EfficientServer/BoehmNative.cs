using System;
using System.Runtime.InteropServices;

namespace EfficientServer
{
    /// <summary>
    /// The one P/Invoke surface into the Boehm collector already in the process
    /// (Unity Mono monobdwgc). Shared by <see cref="GcIncremental"/> (mode flip)
    /// and <see cref="GcDiagnostics"/> (megapause probe) so the library name and
    /// entry points live in exactly one place.
    /// </summary>
    internal static class BoehmNative
    {
        // Same library the game loads for Boehm; matches the bridge's P/Invoke.
        // This is the LINUX bundled name (libmonobdwgc-2.0.so); other host OSes
        // ship the same collector under a different module name, so callers fail
        // soft with DllNotFoundException there by design.
        internal const string Lib = "monobdwgc-2.0";

        [DllImport(Lib)] internal static extern void GC_disable();
        [DllImport(Lib)] internal static extern void GC_enable();
        [DllImport(Lib)] internal static extern void GC_gcollect();
        [DllImport(Lib)] internal static extern UIntPtr GC_get_heap_size();
        [DllImport(Lib)] internal static extern void GC_enable_incremental();
        [DllImport(Lib)] internal static extern void GC_set_time_limit_ns(long ns);
    }
}
