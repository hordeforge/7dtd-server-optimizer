using System;
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
        // Lifetime skip counters for `es status`: how many entity-ticks ran the
        // despawn-only path instead of the heavy tail, split far-skip vs mid-band
        // off-frame. Per-event logging would flood (this fires per entity per
        // tick), so the totals are the engagement signal.
        static long _skippedFarTotal;
        static long _stridedOffTotal;
        public static long SkippedFarTotal { get { return _skippedFarTotal; } }
        public static long StridedOffTotal { get { return _stridedOffTotal; } }

        // The alert probe failing is API drift: without it every entity would be
        // treated as unalerted and strided/skipped regardless of combat state.
        // Fall back to full-rate AI and say so ONCE (this fires per entity per
        // tick, so per-call logging would flood).
        static bool _alertProbeWarned;
        static bool _despawnWarned;

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
                catch (Exception ex)
                {
                    if (!_alertProbeWarned)
                    {
                        _alertProbeWarned = true;
                        ModApi.Warn("AI LOD alert check failed [" + ex.GetType().Name + "]: " + ex.Message
                            + " - every entity now counts as alerted; mid/far throttling is INACTIVE until restart");
                    }
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
            catch (Exception ex)
            {
                // API drift -> let stock run rather than leak; say so once.
                if (!_despawnWarned)
                {
                    _despawnWarned = true;
                    ModApi.Warn("AI LOD CheckDespawn failed [" + ex.GetType().Name + "]: " + ex.Message
                        + " - falling back to full stock updateTasks for all entities until restart");
                }
                return true;
            }
            if (far) _skippedFarTotal++; else _stridedOffTotal++;
            return false;
        }
    }
}
