using System;
using System.Reflection;
using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Skip client-oriented systems that still run on dedicated World.OnUpdateTick.
    /// Each skip live-gates on its own knob (read per call, like every other
    /// lever), so `es reload` can take a skip away without a restart: the prefix
    /// stays installed but runs the original while its knob is false. The
    /// install-time check below only decides whether a prefix exists at all;
    /// without the per-call knob read, flipping a skip off would keep skipping
    /// until the process restarted.
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
                TryPrefix("DynamicMusic.Conductor", "Update", nameof(SkipDynamicMusic));
            if (skip.WaterSplashParticles)
                TryPrefix("WaterSplashCubes", "Update", nameof(SkipWaterSplash));
            if (skip.EnvironmentAudioUpdates)
            {
                TryPrefix("EnvironmentAudioManager", "Update", nameof(SkipEnvironmentAudio));
                TryPrefix("EnvironmentAudioManager", "FixedUpdate", nameof(SkipEnvironmentAudio));
                TryPrefix("EnvironmentAudioManager", "LateUpdate", nameof(SkipEnvironmentAudio));
            }
            // Per-frame ambient light-spectrum lerp (~650 IL) whose only outputs are
            // RenderSettings.ambient*Color writes; the consumer chain
            // (LightManager.GetLightLevel -> stealth) is client-computed. RE sweep
            // 2026-07-21, RESULTS 3n.
            if (skip.AmbientLightSpectrumUpdates)
                TryPrefix("WorldEnvironment", "AmbientSpectrumFrameUpdate", nameof(SkipAmbientSpectrum));
        }

        static void TryPrefix(string typeName, string methodName, string prefixName)
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
                    EsLog.Warn($"skip-patch {typeName}.{methodName}: type not found (skip disabled)");
                    return;
                }
                MethodInfo m = t.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (m == null)
                {
                    EsLog.Warn($"skip-patch {typeName}.{methodName}: method not found (skip disabled)");
                    return;
                }
                MethodInfo prefix = typeof(DedicatedSkipPatch).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic);
                OptionalHarmony.Patch(m, new HarmonyMethod(prefix));
                EsLog.Log($"skip-patch {typeName}.{methodName}");
            }
            catch (Exception ex)
            {
                EsLog.Warn($"skip-patch {typeName}.{methodName} failed [{ex.GetType().Name}]: {ex.Message}");
            }
        }

        // Run the original unless the mod is active AND this skip's knob is
        // currently true. Master gate first: an inactive mod must never skip.
        static bool Gate(bool knobActive)
        {
            return !(ModApi.ShouldRun() && knobActive);
        }

        static bool SkipDynamicMusic()
        {
            return Gate(ModApi.Config != null && ModApi.Config.SkipOnDedicated != null
                && ModApi.Config.SkipOnDedicated.DynamicMusicSystem);
        }

        static bool SkipWaterSplash()
        {
            return Gate(ModApi.Config != null && ModApi.Config.SkipOnDedicated != null
                && ModApi.Config.SkipOnDedicated.WaterSplashParticles);
        }

        static bool SkipEnvironmentAudio()
        {
            return Gate(ModApi.Config != null && ModApi.Config.SkipOnDedicated != null
                && ModApi.Config.SkipOnDedicated.EnvironmentAudioUpdates);
        }

        static bool SkipAmbientSpectrum()
        {
            return Gate(ModApi.Config != null && ModApi.Config.SkipOnDedicated != null
                && ModApi.Config.SkipOnDedicated.AmbientLightSpectrumUpdates);
        }
    }
}
