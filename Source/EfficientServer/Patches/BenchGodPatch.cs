using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// BENCH DIAGNOSTIC (`es benchgod on|off`, default off, not config-persisted):
    /// makes all players immune to damage server-side. Purpose: synthetic bench
    /// bots are level-1 and die to endgame zombies in seconds, which collapses the
    /// horde's target anchors and turns "active siege" loads into spawn-equilibrium
    /// plateaus (RESULTS 3q). With bench-god on, bots survive, zombies stay
    /// actively attacking, and the standing horde reflects the intended load.
    /// Never enable on a real server - it is aimbot-grade cheating for players.
    /// </summary>
    [HarmonyPatch(typeof(EntityPlayer), "DamageEntity")]
    public static class BenchGodPatch
    {
        public static bool BenchGod; // toggled by the console command only

        static bool Prefix(ref int __result)
        {
            // Same gate as every other patch: DedicatedOnly must hold even for the
            // bench flag, so a client host cannot toggle itself damage-immune.
            if (!ModApi.ShouldRun() || !BenchGod) return true;
            __result = 0;
            return false;
        }
    }
}
