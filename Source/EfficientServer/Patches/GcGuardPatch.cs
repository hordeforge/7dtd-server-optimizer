using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Vanilla GameManager.gmUpdate force-calls GC.Collect() every ~120 s
    /// (gcCountdownTimer). On Unity's Boehm GC that forced full stop-the-world
    /// pass is a self-inflicted late-tick hitch - Boehm already collects on
    /// allocation pressure. This transpiler routes that single call through a
    /// guard so the forced periodic STW is skipped, with a heap-ceiling safety
    /// collect so memory still cannot run away. Server-internal only: it changes
    /// no wire bytes, so a vanilla / EAC client connects normally.
    /// </summary>
    [HarmonyPatch(typeof(GameManager), "gmUpdate")]
    public static class GcGuardPatch
    {
        static readonly MethodInfo GcCollect =
            AccessTools.Method(typeof(GC), nameof(GC.Collect), Type.EmptyTypes);
        static readonly MethodInfo Guard =
            AccessTools.Method(typeof(GcGuardPatch), nameof(MaybeCollect));

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int swapped = 0;
            foreach (CodeInstruction ins in instructions)
            {
                if (ins.opcode == OpCodes.Call && ReferenceEquals(ins.operand, GcCollect))
                {
                    swapped++;
                    yield return new CodeInstruction(OpCodes.Call, Guard) { labels = ins.labels };
                }
                else
                {
                    yield return ins;
                }
            }
            ModApi.Log("GcGuardPatch: rerouted " + swapped + " GC.Collect() call(s) in gmUpdate");
            // Matched-but-untransformed is a silent failure: Harmony still reports
            // gmUpdate as patched while the forced STW collect keeps firing. Fail
            // loudly so target/overload drift surfaces instead of pretending to work.
            if (swapped == 0)
                throw new InvalidOperationException(
                    "GcGuardPatch: no parameterless GC.Collect() found in gmUpdate; "
                    + "the forced-collect site moved or changed overload - patch inactive.");
        }

        public static void MaybeCollect()
        {
            GcConfig cfg = ModApi.Config != null ? ModApi.Config.Gc : null;
            // Not our run, or explicitly disabled -> preserve vanilla behavior.
            if (!ModApi.ShouldRun() || cfg == null || !cfg.Enabled || !cfg.SkipForcedCollect)
            {
                GC.Collect();
                return;
            }
            // Skip the forced periodic collect, but keep a safety net: if the
            // managed heap has grown past the ceiling, collect anyway so a
            // long-lived server cannot leak unbounded.
            long ceilingMB = SafetyCeilingMB(cfg);
            if (ceilingMB > 0 && GC.GetTotalMemory(false) > ceilingMB * 1024L * 1024L)
            {
                GC.Collect();
            }
        }

        // Absolute override, else auto = fraction of host RAM. A fixed low ceiling
        // (e.g. 4 GB) would sit below the real 5-10 GB working heap under load and
        // fire every frame, defeating the guard - so auto scales with the host.
        static long SafetyCeilingMB(GcConfig cfg)
        {
            if (cfg.SafetyCollectAboveMB > 0) return cfg.SafetyCollectAboveMB;
            float frac = cfg.SafetyCollectRamFraction;
            if (frac <= 0f || float.IsNaN(frac)) frac = 0.5f;
            if (frac > 0.95f) frac = 0.95f;
            int hostMB = UnityEngine.SystemInfo.systemMemorySize; // host physical RAM in MB
            return hostMB > 0 ? (long)(hostMB * frac) : 0L;
        }
    }
}
