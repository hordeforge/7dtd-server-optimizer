using System.Threading;
using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Counts invocations of the dynamic-mesh frame updates. The two methods exist
    /// on every build this mod targets, but V3.4.0 removed every server-side call
    /// site and starts the subsystem only from the console: the methods resolve,
    /// Harmony patches them, and they simply never run. The mesh-budget lever then
    /// changes statics that nothing reads, which without a counter is
    /// indistinguishable from a working lever. <see cref="Updates"/> is that
    /// detection signal, reported by the budget apply line and by `es status`.
    /// Main-thread confined like the methods it counts, so a plain interlocked
    /// increment is enough for the console thread's read.
    /// </summary>
    public static class DynamicMeshProbePatch
    {
        static long _updates;

        public static long Updates { get { return Interlocked.Read(ref _updates); } }

        public static bool HasRun { get { return Updates > 0; } }

        static void Count()
        {
            Interlocked.Increment(ref _updates);
        }

        [HarmonyPatch(typeof(DynamicMeshManager), "Update")]
        internal static class ManagerUpdatePatch
        {
            static void Postfix()
            {
                Count();
            }
        }

        [HarmonyPatch(typeof(DynamicMeshServer), "Update")]
        internal static class ServerUpdatePatch
        {
            static void Postfix()
            {
                Count();
            }
        }
    }
}
