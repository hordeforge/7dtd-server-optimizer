using HarmonyLib;
using KinematicCharacterController;
using UnityEngine;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Crowd-collision LOD. RE + measurement (RESULTS 3q + the MoveEntityHeaded
    /// anatomy): entity movement/collision integration is 54% of the per-zombie
    /// tick, and its per-neighbor share - up to 3 depenetration rounds of
    /// Physics.OverlapCapsule + an O(n^2) interop-heavy collider sort + extra
    /// capsule-cast hits - exists only in dense packs. Vanilla ALREADY staggers the
    /// depenetration RESPONSE (GetCollisionOverlapScale: 10% normally, 50% on each
    /// entity's every-16th tick) but pays the full query cost every tick.
    ///
    /// This lever staggers the QUERIES at the broadphase: on a zombie's off-ticks,
    /// bit 15 (the alive-entity physics layer) is stripped from its kinematic
    /// motor's CollidableLayers for the duration of ccEntityCollision, so overlap
    /// queries, the sort, ComputePenetration, sweeps, and the ground probe simply
    /// never see neighbor capsules. Block/world collision is untouched (other
    /// layers), so stuck-detection and EAIBreakBlock behave normally. The soft-push
    /// separation force (Entity.OnPushEntity, a different mechanism) keeps running
    /// every tick, so packs still spread out.
    ///
    /// Fidelity trade: zombie-vs-zombie (and zombie-vs-player-capsule, server-side)
    /// depenetration happens every Nth tick instead of every tick - bounded extra
    /// interpenetration in packs (clients render the server pile as-is today;
    /// vanilla packs already clip at 10% resolve strength). Zombies only
    /// (EntityEnemy); players use CharacterControllerUnity and vehicles override
    /// entityCollision - both naturally excluded. Finalizer guarantees mask
    /// restore (a leaked strip would permanently ghost the zombie).
    /// </summary>
    [HarmonyPatch(typeof(Entity), "ccEntityCollision")]
    public static class CrowdCollisionLodPatch
    {
        const int AliveEntityLayerBit = 1 << 15;

        static void Prefix(Entity __instance, out int __state)
        {
            __state = -1;
            CrowdCollisionLodConfig cfg = ModApi.Config != null ? ModApi.Config.CrowdCollisionLod : null;
            if (!ModApi.ShouldRun() || cfg == null || !cfg.Enabled)
                return;
            if (!(__instance is EntityEnemy))
                return;
            if ((Time.frameCount + __instance.entityId) % cfg.ResolveEveryNTicks == 0)
                return; // this zombie's resolve tick: full collision
            var cck = __instance.m_characterController as CharacterControllerKinematic;
            KinematicCharacterMotor motor = cck != null ? cck.motor : null;
            if (motor == null)
                return;
            int mask = motor.CollidableLayers;
            if ((mask & AliveEntityLayerBit) == 0)
                return; // already stripped (shouldn't happen; avoid double-save)
            // Save BEFORE stripping: the Finalizer runs even if this prefix or the
            // setter throws, and restoring from __state is the only way back - so
            // the saved mask must already be in place when the mutation happens.
            __state = mask;
            motor.CollidableLayers = mask & ~AliveEntityLayerBit;
        }

        static void Finalizer(Entity __instance, int __state)
        {
            if (__state < 0)
                return;
            var cck = __instance.m_characterController as CharacterControllerKinematic;
            KinematicCharacterMotor motor = cck != null ? cck.motor : null;
            if (motor != null)
                motor.CollidableLayers = __state;
        }
    }
}
