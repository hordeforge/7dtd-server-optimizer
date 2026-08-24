using System.Collections.Generic;
using UnityEngine;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Emergency animator cost cut, driven by the governor's second escalation
    /// tier (or `es animoff` bench probe). Measured basis (RESULTS 3o + fence
    /// check): at 64 players + ~400 endgame zombies the animator path is ~60 ms
    /// of a ~147 ms saturated frame (~40%). Disabling evaluation recovered
    /// ~147 -> ~85 ms.
    ///
    /// Mechanism (v1.17.0+): set <see cref="Animator.cullingMode"/> to
    /// <see cref="AnimatorCullingMode.CullCompletely"/> while leaving
    /// <c>enabled = true</c>. A headless dedicated server has no visible
    /// renderers, so CullCompletely stops evaluation without tearing down the
    /// root-motion binding. The previous approach (<c>enabled = false</c>) left
    /// <c>deltaPosition = 0</c> forever after restore (RESULTS 3s) - refuted
    /// revival: bare enable, Rebind, re-push WalkType/IsAlive.
    ///
    /// While active, combat fidelity still degrades in known ways (attack
    /// cadence falls back to wall-clock timer, stuns clear next tick, movement
    /// uses the supplementary displacement path). Clients still animate locally.
    /// <see cref="GovernorConfig.AnimatorEmergency"/> stays default-false until
    /// a live human cycle clears the exit path with <c>es animstate</c> dp &gt; 0.
    /// </summary>
    public static class AnimatorEmergency
    {
        // instanceId -> prior culling mode. UnityEngine.Object cannot be a reliable
        // dictionary key after destroy; instance IDs stay stable for the GO lifetime.
        static readonly Dictionary<int, AnimatorCullingMode> SavedModes =
            new Dictionary<int, AnimatorCullingMode>();
        // Reusable sweep scratch: ids seen this pass, ids to drop afterwards.
        static readonly HashSet<int> SweepIds = new HashSet<int>();
        static readonly List<int> StaleIds = new List<int>();

        public static bool Active { get; private set; }

        /// <summary>
        /// Enter emergency (or re-sweep while already active so mid-emergency
        /// spawns are covered). Idempotent on already-culled rigs.
        /// </summary>
        public static void Enter()
        {
            World world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) return;
            int swept = 0;
            SweepIds.Clear();
            List<Entity> entities = world.Entities.list;
            for (int i = 0; i < entities.Count; i++)
            {
                if (!(entities[i] is EntityEnemy enemy)) continue;
                if (enemy.IsDead()) continue;
                Animator[] anims = enemy.GetComponentsInChildren<Animator>(true);
                for (int a = 0; a < anims.Length; a++)
                {
                    Animator anim = anims[a];
                    if (anim == null) continue;
                    int id = anim.GetInstanceID();
                    // Record BEFORE the culling check so an already-culled rig's
                    // existing saved entry is not mistaken for a despawned one.
                    SweepIds.Add(id);
                    // Never touch enabled. Only change cullingMode.
                    if (anim.cullingMode == AnimatorCullingMode.CullCompletely)
                        continue;
                    if (!SavedModes.ContainsKey(id))
                        SavedModes[id] = anim.cullingMode;
                    anim.cullingMode = AnimatorCullingMode.CullCompletely;
                    swept++;
                }
            }
            PruneDespawnedSavedModes();
            if (!Active || swept > 0)
                EsLog.Log($"Governor: animator emergency {(Active ? "sweep" : "ENTER")} - CullCompletely on {swept} rigs (saved={SavedModes.Count})");
            Active = true;
        }

        // Saved entries whose animator no longer enumerates as a living enemy rig
        // (corpse despawned mid-emergency) can never be restored: Restore walks
        // exactly the set this sweep enumerated. Drop them each sweep so a long
        // tier-2 session does not accumulate one entry per spawn/despawn until Exit.
        static void PruneDespawnedSavedModes()
        {
            if (SavedModes.Count == 0) return;
            StaleIds.Clear();
            foreach (KeyValuePair<int, AnimatorCullingMode> kv in SavedModes)
                if (!SweepIds.Contains(kv.Key)) StaleIds.Add(kv.Key);
            if (StaleIds.Count == 0) return;
            for (int i = 0; i < StaleIds.Count; i++)
                SavedModes.Remove(StaleIds[i]);
            EsLog.Log("Governor: animator emergency pruned " + StaleIds.Count
                + " saved mode(s) for despawned rigs (saved=" + SavedModes.Count + ")");
        }

        public static void Exit()
        {
            if (!Active && SavedModes.Count == 0) return;
            int restored = RestoreAllEnemyAnimators();
            SavedModes.Clear();
            Active = false;
            EsLog.Log($"Governor: animator emergency EXIT - restored cullingMode on {restored} rigs");
        }

        /// <summary>
        /// Restore every live enemy animator's saved (or healthy default)
        /// culling mode. Does not toggle enabled, does not Rebind.
        /// </summary>
        static int RestoreAllEnemyAnimators()
        {
            World world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) return 0;
            int restored = 0;
            List<Entity> entities = world.Entities.list;
            for (int i = 0; i < entities.Count; i++)
            {
                if (!(entities[i] is EntityEnemy enemy)) continue;
                // Corpses stay in Entities.list; leave death pose alone.
                if (enemy.IsDead()) continue;
                Animator[] anims = enemy.GetComponentsInChildren<Animator>(true);
                for (int a = 0; a < anims.Length; a++)
                {
                    Animator anim = anims[a];
                    if (anim == null) continue;
                    int id = anim.GetInstanceID();
                    AnimatorCullingMode prior;
                    if (!SavedModes.TryGetValue(id, out prior))
                    {
                        // Probe may have entered without a dict entry if the rig
                        // was already CullCompletely, or spawn arrived mid-exit.
                        // Healthy server zombies use CullUpdateTransforms (RE).
                        if (anim.cullingMode != AnimatorCullingMode.CullCompletely)
                            continue;
                        prior = AnimatorCullingMode.CullUpdateTransforms;
                    }
                    if (anim.cullingMode == prior) continue;
                    anim.cullingMode = prior;
                    // Ensure enabled stayed true (never force-disable in this path).
                    if (!anim.enabled)
                        anim.enabled = true;
                    anim.Update(0f);
                    restored++;
                }
            }
            return restored;
        }
    }
}