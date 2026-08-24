using System;

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
                DedicatedSkipPatch.ApplyOptional();
                GcIncremental.Apply();
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

        // Same live-UNDO pair as ApplyTargetFps: a reload to 0 (or a disable)
        // must restore the pre-mod worker count, not leave our value in place.
        static int _prevWorkers;
        static bool _workersApplied;

        // Unity job-system worker pool size (0 = vanilla). Runtime-settable; the
        // saturated frame is partly main-thread job-fence waiting (RESULTS 3o), and
        // pool size is the one untested variable there. Same logging contract as
        // ApplyTargetFpsInner: silent when there is nothing to apply or undo; only
        // real transitions log, so repeated `es reload` stays quiet.
        public static void ApplyJobWorkers()
        {
            ServerConfig cfg = ModApi.Config != null ? ModApi.Config.Server : null;
            int wanted = cfg != null && ModApi.ShouldRun() ? cfg.JobWorkerCount : 0;
            try
            {
                int current = Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount;
                if (wanted <= 0)
                {
                    if (!_workersApplied) return; // never touched it: nothing to undo
                    _workersApplied = false;
                    if (current != _prevWorkers)
                    {
                        Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount = _prevWorkers;
                        ModApi.Log($"job workers {current} -> {_prevWorkers} (config 0 or mod inactive: vanilla restored)");
                    }
                    return;
                }
                if (!_workersApplied)
                {
                    _prevWorkers = current;
                    _workersApplied = true;
                }
                if (current == wanted) return;
                Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount = wanted;
                ModApi.Log($"job workers {current} -> {wanted}");
            }
            catch (Exception ex)
            {
                ModApi.Warn("job worker set failed [" + ex.GetType().Name + "]: " + ex.Message);
            }
        }

        // Apply-once knob state for live UNDO: `es reload` must be able to take
        // TargetFps back to 0 / disable the mod without leaving our override in
        // place, so remember what we set and what was there before it.
        static int _prevFps;
        static int _appliedFps;
        static bool _fpsApplied;

        // Same shape as ApplyJobWorkers' guard: an engine setter failing must not
        // escape. This apply is ALSO re-run every ~200 frames by TargetFpsPatch
        // inside the game's UpdateTick postfix, where an unhandled exception would
        // propagate into the tick loop - hence warn ONCE (per-tick rate forbids
        // per-call logs), matching the AiLodPatch hot-path convention.
        static bool _fpsWarned;

        // Persistent form of `settargetfps` (which does not survive restarts).
        // Frame rate is NOT the tick rate - the full entity tick stays ~20 Hz at
        // any fps; extra frames smooth the per-frame path (pump, slices), lowering
        // delivery jitter. 0 = leave vanilla.
        //
        // Applied at GameStartDone AND re-enforced periodically (TargetFpsPatch):
        // vanilla resets targetFrameRate back to its default some time after
        // GameStartDone (measured: the one-shot set at ~21 s was 20 fps again
        // minutes later), so a single apply silently loses. Gated on ShouldRun like
        // every sibling start-time action (a disabled mod must stay inert), and a
        // reload to 0 (or a disable) restores the pre-mod value instead of silently
        // keeping the override forever.
        public static void ApplyTargetFps()
        {
            try
            {
                ApplyTargetFpsInner();
            }
            catch (Exception ex)
            {
                if (!_fpsWarned)
                {
                    _fpsWarned = true;
                    ModApi.Warn("target fps apply failed [" + ex.GetType().Name + "]: " + ex.Message
                        + " - vanilla frame rate kept");
                }
            }
        }

        static void ApplyTargetFpsInner()
        {
            ServerConfig cfg = ModApi.Config != null ? ModApi.Config.Server : null;
            int wanted = cfg != null && ModApi.ShouldRun() ? cfg.TargetFps : 0;
            if (wanted <= 0)
            {
                if (!_fpsApplied) return;
                _fpsApplied = false;
                // A pre-mod reading of <= 0 means the engine had no explicit cap yet
                // (Unity's uncapped sentinel); restoring that would uncap the loop,
                // so fall back to the documented vanilla 20.
                int restoreTo = _prevFps > 0 ? _prevFps : 20;
                UnityEngine.Application.targetFrameRate = restoreTo;
                ModApi.Log($"target frame rate {_appliedFps} -> {restoreTo} (config 0 or mod inactive: vanilla restored)");
                return;
            }
            if (!_fpsApplied)
            {
                _prevFps = UnityEngine.Application.targetFrameRate;
                _fpsApplied = true;
            }
            if (UnityEngine.Application.targetFrameRate == wanted) return;
            UnityEngine.Application.targetFrameRate = wanted;
            _appliedFps = wanted;
            ModApi.Log($"target frame rate -> {wanted} (frame path only; full tick stays ~20 Hz)");
        }
    }
}
