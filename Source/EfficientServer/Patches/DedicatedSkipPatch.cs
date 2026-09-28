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
            string target = typeName + "." + methodName;
            // Per-target registry key: an absent or broken target is a permanent
            // property of the game build, not of the config, so the entry stays in
            // `es status` for the process. One key per target, so a build that
            // breaks two of them lists both instead of collapsing into one line.
            string key = "skip:" + target;
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
                    Skipped(key, $"skip-patch {target}: type not found (skip disabled)");
                    return;
                }
                MethodInfo m = t.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (m == null)
                {
                    Skipped(key, $"skip-patch {target}: method not found (skip disabled)");
                    return;
                }
                MethodInfo prefix = typeof(DedicatedSkipPatch).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic);
                OptionalHarmony.Patch(m, new HarmonyMethod(prefix));
                EsLog.Emit(LogLevel.Info, $"skip-patch {target}");
            }
            catch (Exception ex)
            {
                Skipped(key, $"skip-patch {target} failed [{ex.GetType().Name}]: {ex.Message}");
            }
        }

        // Register the dead target and warn. The line is emitted on the FIRST
        // report of that key only: ApplyOptional re-runs on every `es reload`, and
        // an operator reloading while chasing a skip would otherwise get the same
        // drift line per reload, drowning the line that says the reload applied
        // everything else. The count still advances on every apply, and `es status`
        // lists the key, so "still broken, and you have reloaded 3 times" is
        // readable without the log repeating itself.
        static void Skipped(string key, string message)
        {
            if (Degrade.Report(key, message))
                EsLog.Emit(LogLevel.Warn, message);
        }

        // Run the original unless the mod is active AND this skip's knob is
        // currently true. The master gate and the section fetch live here so the
        // four prefixes cannot drift on either; both are read per call, which is
        // what lets `es reload` take a skip away without a restart. Null means
        // "run the original".
        static SkipConfig RunSection
        {
            get
            {
                ServerPerfConfig cfg = ModApi.Config;
                return ModApi.ShouldRun(cfg) && cfg != null ? cfg.SkipOnDedicated : null;
            }
        }

        static bool SkipDynamicMusic()
        {
            SkipConfig s = RunSection;
            return s == null || !s.DynamicMusicSystem;
        }

        static bool SkipWaterSplash()
        {
            SkipConfig s = RunSection;
            return s == null || !s.WaterSplashParticles;
        }

        static bool SkipEnvironmentAudio()
        {
            SkipConfig s = RunSection;
            return s == null || !s.EnvironmentAudioUpdates;
        }

        static bool SkipAmbientSpectrum()
        {
            SkipConfig s = RunSection;
            return s == null || !s.AmbientLightSpectrumUpdates;
        }
    }
}
