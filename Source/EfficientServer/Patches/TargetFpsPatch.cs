using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Periodic enforcement of Server.TargetFps. Vanilla resets
    /// Application.targetFrameRate to its default some time after GameStartDone
    /// (measured), so the one-shot apply loses silently. This postfix re-checks
    /// every ~10 s worth of frames and re-applies only on drift; ApplyTargetFps
    /// no-ops (and stays silent) when the value already matches.
    ///
    /// This class is the re-enforcement trigger only. The apply itself lives in
    /// <see cref="GameStartPatch.ApplyTargetFps"/>, next to ApplyJobWorkers,
    /// because both are start-time settings ModApi re-runs on `es reload`; this
    /// file stays the only periodic caller of it.
    /// </summary>
    [HarmonyPatch(typeof(GameManager), "UpdateTick")]
    public static class TargetFpsPatch
    {
        static int _frames;

        static void Postfix()
        {
            if (!ModApi.ShouldRun()) return;
            if (++_frames % 200 != 0) return; // ~10 s at 20 fps, ~3 s at 60
            GameStartPatch.ApplyTargetFps();
        }
    }
}
