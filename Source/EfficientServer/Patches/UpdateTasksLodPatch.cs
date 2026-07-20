using HarmonyLib;
using UnityEngine;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Entity-AI level of detail on `EntityAlive.updateTasks` (the heavy per-entity
    /// tail: path follow + EAI + the 1236-IL UpdateMoveHelper, which stock does NOT
    /// throttle via aiActiveScale). Three distance bands:
    ///   close (d &lt; MediumAiDistSq)                : full rate, every tick.
    ///   mid   (MediumAiDistSq &lt;= d &lt; FarDistSq) : run every MidTickStride-th frame,
    ///                                                 striped by entity id (spreads the
    ///                                                 per-tick entity cost).
    ///   far   (d &gt;= SkipTasksFarDistSq)           : skip the tail entirely.
    /// CheckDespawn (updateTasks' first step) still runs every tick in mid/far so far
    /// entities cannot accumulate. Alerted / targeting / investigating / active-sleeper
    /// entities are never strided or skipped. All bands gate on aiClosestPlayerDistSq.
    /// Server-internal, no wire change; code -&gt; EAC-off.
    /// </summary>
    [HarmonyPatch(typeof(EntityAlive), "updateTasks")]
    public static class UpdateTasksLodPatch
    {
        static bool Prefix(EntityAlive __instance)
        {
            if (!ModApi.ShouldRun()) return true;
            var cfg = ModApi.Config.AiLod;
            if (cfg == null || !cfg.Enabled) return true;
            if (__instance == null || __instance is EntityPlayer) return true;

            float d = __instance.aiClosestPlayerDistSq;
            bool far = d >= cfg.SkipTasksFarDistSq;
            bool mid = !far && cfg.MidTickStride > 1 && d >= cfg.MediumAiDistSq;
            if (!far && !mid) return true; // close band (or striding off): full rate

            if (cfg.SkipTasksUnlessAlerted)
            {
                // Keep full AI if hunting / investigating / recently alerted.
                try
                {
                    if (__instance.GetAttackTarget() != null) return true;
                    if (__instance.HasInvestigatePosition) return true;
                    if (__instance.GetAlertTicks() > 0) return true;
                    if (__instance.IsSleeper && !__instance.IsSleeperPassive) return true;
                    // blood-moon / ferals stay active even far out if already chasing
                }
                catch
                {
                    return true;
                }
            }

            if (mid)
            {
                // Run this entity's heavy tail only on its stride frame; otherwise
                // fall through to the despawn-only skip below. Striping by entity id
                // + frame spreads the mid-band cost evenly across `stride` frames.
                if ((__instance.entityId + Time.frameCount) % cfg.MidTickStride == 0)
                    return true; // this entity's turn this frame
            }

            // Mid off-frame or far: run only CheckDespawn (updateTasks' first step, so
            // far wandering-horde / bloodmoon / expired entities still despawn), skip
            // the expensive path follow + EAI + move-helper.
            try { __instance.CheckDespawn(); }
            catch { return true; } // API drift -> let stock run rather than leak
            return false;
        }
    }
}
