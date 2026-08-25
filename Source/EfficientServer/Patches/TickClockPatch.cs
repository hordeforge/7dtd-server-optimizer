using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Advances <see cref="TickClock"/> once per GameManager.UpdateTick. A PREFIX, so
    /// the counter is constant for the entire invocation body (entity ticking
    /// included) and steps between consecutive invocations; slice-drained entity work
    /// that spills onto later frames belongs to the counter value it was queued
    /// under. UpdateTick executes EVERY FRAME (the ~20 Hz full sim tick is gated
    /// inside it, RESULTS 3k), so the counter's granularity is frames, not full
    /// ticks - see TickClock for what that means for stride coverage above 20 fps.
    /// Unconditional on purpose: no config gate may stop a clock other stripes read.
    /// Class-annotated and required to match like every sibling group, so a game
    /// update that moves UpdateTick surfaces as MISSING TARGET instead of silently
    /// freezing every striping gate at slot 0; <see cref="TickClock.Alive"/> is the
    /// second line of defense - consumers fail open to vanilla when this prefix has
    /// never run.
    /// </summary>
    [HarmonyPatch(typeof(GameManager), "UpdateTick")]
    internal static class TickClockPatch
    {
        static void Prefix()
        {
            TickClock.Advance();
        }
    }
}
