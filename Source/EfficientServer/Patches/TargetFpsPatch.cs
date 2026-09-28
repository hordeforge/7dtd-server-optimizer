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
        // Frames between re-checks (~10 s at the vanilla 20 fps, ~3 s at 60).
        const uint FramesPerRecheck = 200;

        // uint so the cadence survives the signed wrap: this counter is the only
        // driver of the periodic re-apply, and a 60 fps server crosses 2^31
        // frames in ~10 hours, after which a signed `_frames % 200` runs
        // negative and the 200-frame spacing is scrambled for a window. Same
        // wrap-safe cursor convention as TickClock.OwnsSlot.
        static uint _frames;

        static void Postfix()
        {
            if (!ModApi.ShouldRun()) return;
            if (++_frames % FramesPerRecheck != 0) return; // ~10 s at 20 fps, ~3 s at 60
            GameStartPatch.ApplyTargetFps();
        }
    }
}
