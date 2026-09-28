using System.Runtime.InteropServices;

namespace EfficientServer
{
    /// <summary>
    /// The one P/Invoke surface into the Boehm collector already in the process
    /// (Unity Mono monobdwgc). Used by <see cref="GcIncremental"/> (mode flip)
    /// so the library name and entry points live in exactly one place.
    /// </summary>
    internal static class BoehmNative
    {
        // Same collector the game already loads; matches the bridge's P/Invoke.
        // The bare base name, so the host applies its own module resolution
        // (Linux loads libmonobdwgc-2.0.so, Windows the .dll of the same name).
        // A host that resolves neither fails soft in GcIncremental by design.
        internal const string Lib = "monobdwgc-2.0";

        [DllImport(Lib)] internal static extern void GC_enable_incremental();
        [DllImport(Lib)] internal static extern void GC_set_time_limit_ns(long ns);
    }
}
