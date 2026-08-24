using System;
using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Entity-AI level of detail on `EntityAlive.updateTasks` (the heavy per-entity
    /// tail: path follow + EAI + the 1236-IL UpdateMoveHelper, which stock does NOT
    /// throttle via aiActiveScale). Three distance bands:
    ///   close (d &lt; MediumAiDistSq)                : full rate, every tick.
    ///   mid   (MediumAiDistSq &lt;= d &lt; FarDistSq) : run every MidTickStride-th tick,
    ///                                                 striped by entity id (spreads the
    ///                                                 per-tick entity cost; counts TICKS
    ///                                                 via TickClock, not render frames).
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

        // The CheckDespawn fallback failing is API drift: without it far entities
        // would never despawn. Fall back to full-rate AI and say so ONCE (this
        // fires per entity per tick, so per-call logging would flood).
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

            // Keep full AI when hunting / investigating / recently alerted (the
            // shared probe also covers API drift by failing open to "alerted").
            if (cfg.SkipTasksUnlessAlerted && AiAlertGate.IsAlertedOrBusy(__instance))
                return true;

            if (mid)
            {
                // Run this entity's heavy tail only on its stride tick; otherwise
                // fall through to the despawn-only skip below. Striping by entity id
                // + TickClock index spreads the mid-band cost across `stride`
                // windows. The clock steps per UpdateTick invocation (= frames,
                // RESULTS 3k): exact at the vanilla 20 fps, and coverage-complete
                // above 20 fps whenever gcd(fps/20, stride) = 1.
                if (TickClock.SlotOwn(__instance.entityId, cfg.MidTickStride))
                    return true; // this entity's stride tick
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
