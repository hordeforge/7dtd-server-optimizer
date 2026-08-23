using HarmonyLib;
using UnityEngine;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Path admission at <see cref="EntityAlive.FindPath"/> (A2). Stock always
    /// enqueues (only a Y-clamp when xz distSq &gt; 1225); the ASP drain is capped
    /// at ~8 starts/frame. Under blood-moon path spam the wait queue grows while
    /// compute stays bounded - admission cuts enqueue noise without touching the
    /// A* library.
    ///
    /// Defaults are vanilla-neutral (both knobs 0 = unlimited / no distance drop).
    /// Never drops alerted / investigating / attack-target / active-sleeper entities.
    /// Priority admits do not consume the per-frame budget so combat keeps pathing.
    /// Server-internal; no wire change.
    /// </summary>
    [HarmonyPatch(typeof(EntityAlive), "FindPath", new[] {
        typeof(Vector3), typeof(float), typeof(bool), typeof(EAIBase)
    })]
    public static class PathAdmissionPatch
    {
        static int _frameStamp = -1;
        static int _enqueuedThisFrame;

        // Lifetime drop counters for `es status`. Both knobs are silent by design
        // on the hot path (per-request logging would flood at blood-moon rates),
        // so these totals are how an operator tells "cap engaged" from "cap
        // never reached". Two longs, incremented only on the drop branch.
        static long _droppedCapTotal;
        static long _droppedFarTotal;
        public static long DroppedCapTotal { get { return _droppedCapTotal; } }
        public static long DroppedFarTotal { get { return _droppedFarTotal; } }

        // Return false to skip the original FindPath (drop this request).
        static bool Prefix(EntityAlive __instance)
        {
            PathfindingConfig cfg = ModApi.Config != null ? ModApi.Config.Pathfinding : null;
            if (!ModApi.ShouldRun() || cfg == null) return true;
            int maxPerTick = cfg.MaxPathEnqueuesPerTick;
            float dropFarSq = cfg.DropPathWhenFarDistSq;
            if (maxPerTick <= 0 && dropFarSq <= 0f)
                return true; // both off = vanilla

            if (__instance == null || __instance is EntityPlayer)
                return true;

            // Never starve combat / investigation / sleeper wakeup pathing.
            try
            {
                if (__instance.GetAttackTarget() != null) return true;
                if (__instance.HasInvestigatePosition) return true;
                if (__instance.GetAlertTicks() > 0) return true;
                if (__instance.IsSleeper && !__instance.IsSleeperPassive) return true;
            }
            catch
            {
                return true; // API drift -> admit rather than break AI
            }

            if (dropFarSq > 0f && __instance.aiClosestPlayerDistSq >= dropFarSq)
            {
                _droppedFarTotal++;
                return false; // far non-alert: drop
            }

            if (maxPerTick <= 0)
                return true;

            // Main-thread only (EntityAlive.FindPath); frame-local counter.
            int frame = Time.frameCount;
            if (frame != _frameStamp)
            {
                _frameStamp = frame;
                _enqueuedThisFrame = 0;
            }
            if (_enqueuedThisFrame >= maxPerTick)
            {
                _droppedCapTotal++;
                return false;
            }
            _enqueuedThisFrame++;
            return true;
        }
    }
}
