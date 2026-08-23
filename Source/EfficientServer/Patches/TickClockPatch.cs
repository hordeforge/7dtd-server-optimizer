using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Advances <see cref="TickClock"/> once per GameManager.UpdateTick. A PREFIX, so
    /// the counter is constant for the entire tick body (entity ticking included)
    /// and steps between consecutive ticks; slice-drained entity work that spills
    /// onto later frames still belongs to the logical tick it was queued from.
    /// Unconditional on purpose: no config gate may stop a clock other stripes read.
    /// Class-annotated and required to match like every sibling group, so a game
    /// update that moves UpdateTick surfaces as MISSING TARGET instead of silently
    /// freezing every tick-sourced stripe at slot 0.
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
