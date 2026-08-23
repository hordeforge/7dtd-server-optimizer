using System;
using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Post-start setup. NOT a Harmony patch - invoked via the sanctioned
    /// <c>ModEvents.GameStartDone</c> lifecycle hook (registered in ModApi), so
    /// there is no IL patch just to get "run after startup" timing. It still
    /// installs the optional dedicated skips via Harmony (those genuinely need
    /// per-call interception) and switches on incremental GC if configured.
    /// </summary>
    public static class GameStartPatch
    {
        public static void OnGameStartDone(ref ModEvents.SGameStartDoneData _data)
        {
            try
            {
                DynamicMeshBudgetPatch.ApplyBudgets();
                var h = new Harmony(ModApi.HarmonyId + ".optional");
                DedicatedSkipPatch.ApplyOptional(h);
                GcIncremental.Apply(ModApi.Config != null ? ModApi.Config.Gc : null);
                GcDiagnostics.StartMegapauseTest(ModApi.Config != null ? ModApi.Config.Diagnostics : null);
                ApplyTargetFps();
                ApplyJobWorkers();
            }
            catch (Exception ex)
            {
                // Full exception: this wraps the whole start-time chain, so the type
                // and stack are what say WHICH apply step broke.
                ModApi.Error("GameStartDone handler failed: " + ex);
            }
        }

        // Unity job-system worker pool size (0 = vanilla). Runtime-settable; the
        // saturated frame is partly main-thread job-fence waiting (RESULTS 3o), and
        // pool size is the one untested variable there. Logs the current count even
        // when leaving vanilla, so the default is visible in the log.
        public static void ApplyJobWorkers()
        {
            ServerConfig cfg = ModApi.Config != null ? ModApi.Config.Server : null;
            try
            {
                int current = Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount;
                if (cfg == null || cfg.JobWorkerCount <= 0)
                {
                    ModApi.Log($"job workers = {current} (vanilla)");
                    return;
                }
                if (current == cfg.JobWorkerCount) return;
                Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount = cfg.JobWorkerCount;
                ModApi.Log($"job workers {current} -> {cfg.JobWorkerCount}");
            }
            catch (Exception ex)
            {
                ModApi.Warn("job worker set failed [" + ex.GetType().Name + "]: " + ex.Message);
            }
        }

        // Persistent form of `settargetfps` (which does not survive restarts).
        // Frame rate is NOT the tick rate - the full entity tick stays ~20 Hz at
        // any fps; extra frames smooth the per-frame path (pump, slices), lowering
        // delivery jitter. 0 = leave vanilla.
        //
        // Applied at GameStartDone AND re-enforced periodically (TargetFpsPatch):
        // vanilla resets targetFrameRate back to its default some time after
        // GameStartDone (measured: the one-shot set at ~21 s was 20 fps again
        // minutes later), so a single apply silently loses.
        public static void ApplyTargetFps()
        {
            ServerConfig cfg = ModApi.Config != null ? ModApi.Config.Server : null;
            if (cfg == null || cfg.TargetFps <= 0) return;
            if (UnityEngine.Application.targetFrameRate == cfg.TargetFps) return;
            UnityEngine.Application.targetFrameRate = cfg.TargetFps;
            ModApi.Log($"target frame rate -> {cfg.TargetFps} (frame path only; full tick stays ~20 Hz)");
        }
    }
}
