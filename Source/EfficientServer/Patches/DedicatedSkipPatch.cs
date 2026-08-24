using System;
using System.Reflection;
using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Skip client-oriented systems that still run on dedicated World.OnUpdateTick.
    /// </summary>
    public static class DedicatedSkipPatch
    {
        // One id for the optional skip group; a re-apply (es reload) replaces by method + id instead of stacking.
        static readonly Harmony OptionalHarmony = new Harmony(ModApi.HarmonyId + ".optional");

        // Patched manually from GameStartPatch after types resolve, in case optional types move.
        public static void ApplyOptional()
        {
            if (!ModApi.Config.Enabled) return;
            var skip = ModApi.Config.SkipOnDedicated;
            if (skip == null) return;

            if (skip.DynamicMusicSystem)
                TryPrefix("DynamicMusic.Conductor", "Update");
            if (skip.WaterSplashParticles)
                TryPrefix("WaterSplashCubes", "Update");
            if (skip.EnvironmentAudioUpdates)
            {
                TryPrefix("EnvironmentAudioManager", "Update");
                TryPrefix("EnvironmentAudioManager", "FixedUpdate");
                TryPrefix("EnvironmentAudioManager", "LateUpdate");
            }
            // Per-frame ambient light-spectrum lerp (~650 IL) whose only outputs are
            // RenderSettings.ambient*Color writes; the consumer chain
            // (LightManager.GetLightLevel -> stealth) is client-computed. RE sweep
            // 2026-07-21, RESULTS 3n.
            if (skip.AmbientLightSpectrumUpdates)
                TryPrefix("WorldEnvironment", "AmbientSpectrumFrameUpdate");
        }

        static void TryPrefix(string typeName, string methodName)
        {
            try
            {
                // Harmony's own all-assembly lookup (same Name-or-FullName rule as
                // this file's old hand-rolled scan; verified equivalent against the
                // shipped 0Harmony for every type named below). Returns null when
                // absent instead of throwing.
                Type t = AccessTools.TypeByName(typeName);
                if (t == null)
                {
                    // Soft note (not "MISSING TARGET"): some of these presentation
                    // types are legitimately absent on a headless build, but a
                    // rename would silently disable the skip with zero signal -
                    // hence WARNING, the channel an operator greps after an update.
                    ModApi.Warn($"skip-patch {typeName}.{methodName}: type not found (skip disabled)");
                    return;
                }
                MethodInfo m = t.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (m == null)
                {
                    ModApi.Warn($"skip-patch {typeName}.{methodName}: method not found (skip disabled)");
                    return;
                }
                MethodInfo prefix = typeof(DedicatedSkipPatch).GetMethod(nameof(SkipIfDedicated), BindingFlags.Static | BindingFlags.NonPublic);
                OptionalHarmony.Patch(m, new HarmonyMethod(prefix));
                ModApi.Log($"skip-patch {typeName}.{methodName}");
            }
            catch (Exception ex)
            {
                ModApi.Warn($"skip-patch {typeName}.{methodName} failed [{ex.GetType().Name}]: {ex.Message}");
            }
        }

        static bool SkipIfDedicated()
        {
            return !ModApi.ShouldRun();
        }
    }
}
