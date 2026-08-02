using HarmonyLib;
using UnityEngine;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Animator LOD (the 20 ms lever). Measured: engine-side animator evaluation for
    /// zombies is ~19.9 ms/frame (28% of the loaded frame) at ~379 endgame zombies -
    /// rigs nobody renders, evaluated at AlwaysAnimate because gameplay reads
    /// animator state (root motion drives authoritative movement, attack cadence
    /// reads the state tag, stuns read stun clips). A permanent skip therefore
    /// breaks combat; this LOD instead runs calm, distant zombies' animators at a
    /// REDUCED rate: the Animator component is disabled (stopping the engine's
    /// per-frame evaluation) and manually pumped via Animator.Update(stride * dt)
    /// every Nth frame, so root motion arrives in aggregate and state reads lag by
    /// at most the stride. Exempt (always full rate): zombies near any player,
    /// attacking, stunned, ragdolling, or dead (death animation).
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

            AnimatorLodConfig cfg = ModApi.Config != null ? ModApi.Config.AnimatorLod : null;
            if (!ModApi.ShouldRun() || cfg == null || !cfg.Enabled)
                return true;

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
            bool slotFrame = (Time.frameCount + entity.entityId) % cfg.FarStride == 0;
            if (!slotFrame)
                return false; // no pump, no managed interpretation this frame
            if (pump)
                anim.Update(Time.deltaTime * cfg.FarStride);
            return true;
        }
    }
}
