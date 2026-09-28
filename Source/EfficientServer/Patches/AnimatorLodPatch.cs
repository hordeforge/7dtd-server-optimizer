using HarmonyLib;
using UnityEngine;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Animator LOD (the 20 ms lever). Measured: engine-side animator evaluation for
    /// zombies is ~19.9 ms/frame (28% of the loaded frame) at ~379 endgame zombies -
    /// rigs nobody renders, evaluated every frame because gameplay reads
    /// animator state (root motion drives authoritative movement, attack cadence
    /// reads the state tag, stuns read stun clips). A permanent skip therefore
    /// breaks combat; this LOD instead runs calm, distant zombies' animators at a
    /// REDUCED rate: the Animator component is disabled (stopping the engine's
    /// per-frame evaluation) and manually pumped via Animator.Update(stride * dt)
    /// every Nth frame, so root motion arrives in aggregate and state reads lag by
    /// at most the stride. Exempt (always full rate): zombies near any player,
    /// attacking, stunned, or dead (death animation).
    ///
    /// Managed AvatarZombieController.Update/LateUpdate are also skipped on
    /// off-frames (they only interpret the animator state that has not advanced).
    /// </summary>
    public static class AnimatorLodPatch
    {
        [HarmonyPatch(typeof(AvatarZombieController), "Update")]
        public static class UpdatePatch
        {
            static bool Prefix(AvatarZombieController __instance)
            {
                return Gate(__instance, pump: true);
            }
        }

        [HarmonyPatch(typeof(AvatarZombieController), "LateUpdate")]
        public static class LateUpdatePatch
        {
            static bool Prefix(AvatarZombieController __instance)
            {
                return Gate(__instance, pump: false);
            }
        }

        static bool Gate(AvatarZombieController controller, bool pump)
        {
            EntityAlive entity = controller.entity;
            Animator anim = controller.anim;
            if (entity == null || anim == null)
                return true;

            // Governor tier-2 / es animoff: CullCompletely owns the rig. Do not
            // re-enable, re-pump, or fight cullingMode. Skip managed Update work.
            if (AnimatorEmergency.Active)
            {
                if (entity.IsDead())
                    return true;
                return false;
            }

            ServerPerfConfig config = ModApi.Config;

            AnimatorLodConfig cfg = config != null ? config.AnimatorLod : null;
            if (!ModApi.ShouldRun(config) || cfg == null || !cfg.Enabled)
            {
                // LOD off (config reload / mod disable / host change): release any rig
                // this patch left strided-disabled, else its animator never evaluates
                // again and server-side root motion stays frozen forever.
                if (!anim.enabled)
                {
                    anim.enabled = true;
                    anim.Update(0f);
                }
                return true;
            }

            bool exempt =
                entity.aiClosestPlayerDistSq < cfg.FullRateDistSq
                || entity.IsDead()
                || controller.attackPlayingTime > 0f
                || entity.bodyDamage.CurrentStun != EnumEntityStunType.None;
            if (exempt)
            {
                if (!anim.enabled)
                {
                    // Revive properly: re-enable AND pump once so the state machine
                    // resumes from a fresh evaluation (a bare enabled=true can leave
                    // the rig stale - observed with the bench probe).
                    anim.enabled = true;
                    anim.Update(0f);
                }
                return true;
            }

            // Strided mode: engine evaluation off, manual pump on this entity's slot
            // frame (slots striped by entityId so the per-frame pump load is spread).
            // NOTE: calm-far LOD still uses enabled=false; emergency uses CullCompletely only.
            if (anim.enabled)
                anim.enabled = false;
            // Same wrap-safe striped-slot predicate every other cadence consumer
            // uses, on TickClock (which steps per UpdateTick invocation = per frame,
            // so the cursor granularity animator evaluation wants is unchanged).
            // The engine's Time.frameCount also counted frames outside UpdateTick
            // and reset nothing the mod could seed, so a replay could not reproduce
            // the stripe phase; TickClock starts at zero and advances from the driver
            // patch, so the same entity id takes the same slot on the same frame of a
            // replayed run. Fail open to a pump every frame if that driver ever goes
            // missing, the same degrade-don't-corrupt rule the other consumers use.
            bool slotFrame = !TickClock.Alive
                || TickClock.OwnsSlot(entity.entityId, TickClock.Ticks, cfg.FarStride);
            if (!slotFrame)
                return false; // no pump, no managed interpretation this frame
            if (pump)
                anim.Update(Time.deltaTime * cfg.FarStride);
            return true;
        }
    }
}
