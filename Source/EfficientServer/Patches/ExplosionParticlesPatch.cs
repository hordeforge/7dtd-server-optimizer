using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Skip the explosion VISUAL on a headless dedicated server. This prefix keeps
    /// every gameplay side effect - the physics push (ApplyExplosionForce), the block
    /// changes (ChangeBlocks), and the quest event (QuestEventManager.DetectedExplosion)
    /// - and skips only Object.Instantiate of the explosion particle prefab
    /// (WorldStaticData.prefabExplosions), returning null exactly like the vanilla
    /// no-prefab path (the caller null-checks the returned GameObject).
    ///
    /// Measured A/B at blood-moon load (64 players, ~550 endgame zombies, ~220
    /// explosions): the Instantiate is ~1.1 ms of the ~9 ms ExplosionClient cost
    /// (~10% of GameManager.explode). The bulk is ChangeBlocks - applying and
    /// broadcasting the block destruction - which is gameplay and preserved here.
    /// A small, pure-waste win; not a structural one.
    /// </summary>
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.ExplosionClient))]
    public static class ExplosionParticlesPatch
    {
        static bool Prefix(
            GameManager __instance, Vector3 _center, int _index, int _blastPower,
            float _blastRadius, float _blockDamage, int _entityId,
            List<BlockChangeInfo> _explosionChanges, ref GameObject __result)
        {
            SkipConfig cfg = ModApi.Config != null ? ModApi.Config.SkipOnDedicated : null;
            if (!ModApi.ShouldRun() || cfg == null || !cfg.ExplosionParticles)
                return true; // vanilla
            if (__instance.World == null)
                return true; // vanilla early-return path

            // Vanilla order: physics push first (gated exactly like vanilla: only when
            // a prefab exists for _index, since the push accompanies the visual), then
            // block changes, then the quest event.
            Transform[] prefabs = WorldStaticData.prefabExplosions;
            if (_index > 0 && prefabs != null && _index < prefabs.Length && prefabs[_index] != null)
                ApplyExplosionForce.Explode(_center, _blastPower, _blastRadius);
            if (_explosionChanges != null && _explosionChanges.Count > 0)
                __instance.ChangeBlocks(null, _explosionChanges);
            QuestEventManager.Current.DetectedExplosion(_center, _entityId, _blockDamage);
            __result = null; // same as vanilla when no prefab exists for _index
            return false;
        }
    }
}
