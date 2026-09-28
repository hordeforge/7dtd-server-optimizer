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
    /// <see cref="GovernorConfig.AnimatorEmergency"/> stays default-false even
    /// though the CullCompletely exit path was live-cleared (2026-08-09 runs
    /// restored moving rigs with <c>dp &gt; 0</c>, and the tick-bound stress run
    /// measured -15.4% frame with a complete restore, RESULTS 3t): residual dp=0
    /// walkers keep it opt-in (RESULTS 3t, CONFIG.md).
    ///
    /// Internal like the other support modules (<see cref="AiAlertGate"/>,
    /// <see cref="TickClock"/>): runtime state with
    /// in-assembly consumers only (governor tier 2, animator LOD gate, es console),
    /// not a Harmony patch group and not game-discovered, so it stays off the
    /// assembly's public surface.
    /// </summary>
    internal static class AnimatorEmergency
    {
        /// <summary>
        /// A saved culling mode plus the rig it was read from. The rig reference is
        /// what makes the entry checkable: Unity recycles instance IDs after a
        /// destroy, so an id on its own can hand a NEWLY SPAWNED rig a DEAD rig's
        /// saved mode and restore it on Exit.
        /// </summary>
        struct SavedMode
        {
            public readonly Animator Rig;
            public readonly AnimatorCullingMode Prior;

            public SavedMode(Animator rig, AnimatorCullingMode prior)
            {
                Rig = rig;
                Prior = prior;
            }
        }

        // instanceId -> saved mode. Keyed by id because UnityEngine.Object cannot be
        // a reliable dictionary key after destroy (instance IDs stay stable for the
        // GO lifetime); the SavedMode.Rig reference is what proves the entry still
        // describes that id's current owner.
        static readonly Dictionary<int, SavedMode> SavedModes =
            new Dictionary<int, SavedMode>();
        // Reusable sweep scratch: id -> rig seen this pass, ids to drop afterwards.
        static readonly Dictionary<int, Animator> LiveRigs = new Dictionary<int, Animator>();
        static readonly List<int> StaleIds = new List<int>();

        public static bool Active { get; private set; }

        /// <summary>
        /// Every animator rig on a living (non-corpse) enemy. The one enumeration
        /// behind both the Enter sweep and the Exit restore, so the two passes
        /// cannot disagree on what counts as a live rig - corpses stay in
        /// Entities.list and are skipped by this filter on both sides.
        /// </summary>
        static IEnumerable<Animator> LivingEnemyAnimators(World world)
        {
            List<Entity> entities = world.Entities.list;
            for (int i = 0; i < entities.Count; i++)
            {
                if (!(entities[i] is EntityEnemy enemy)) continue;
                // Corpses stay in Entities.list; leave death pose alone.
                if (enemy.IsDead()) continue;
                Animator[] anims = enemy.GetComponentsInChildren<Animator>(true);
                for (int a = 0; a < anims.Length; a++)
                    if (anims[a] != null) yield return anims[a];
            }
        }

        /// <summary>
        /// Enter emergency (or re-sweep while already active so mid-emergency
        /// spawns are covered). Idempotent on already-culled rigs.
        /// </summary>
        public static void Enter()
        {
            World world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) return;
            int swept = 0;
            LiveRigs.Clear();
            foreach (Animator anim in LivingEnemyAnimators(world))
            {
                int id = anim.GetInstanceID();
                // Record BEFORE the culling check so an already-culled rig's
                // existing saved entry is not mistaken for a despawned one.
                LiveRigs[id] = anim;
                // Never touch enabled. Only change cullingMode.
                if (anim.cullingMode == AnimatorCullingMode.CullCompletely)
                    continue;
                // No entry, or an id Unity has handed to a different rig since the
                // last sweep: record what THIS rig actually had.
                SavedMode saved;
                if (!SavedModes.TryGetValue(id, out saved) || !ReferenceEquals(saved.Rig, anim))
                    SavedModes[id] = new SavedMode(anim, anim.cullingMode);
                anim.cullingMode = AnimatorCullingMode.CullCompletely;
                swept++;
            }
            PruneDespawnedSavedModes();
            // Drop the sweep's rig references here rather than holding the last
            // sweep's rigs alive between sweeps.
            LiveRigs.Clear();
            if (!Active || swept > 0)
                EsLog.Emit(LogLevel.Info, $"Governor: animator emergency {(Active ? "sweep" : "ENTER")} - CullCompletely on {swept} rigs (saved={SavedModes.Count})");
            Active = true;
        }

        // Saved entries whose id is not a live rig this pass, or whose rig no
        // longer owns that id (Unity recycles instance IDs), can never be restored
        // without putting a dead rig's mode on a live one: Restore walks exactly
        // the set this sweep enumerated. Drop them each sweep so a long tier-2
        // session does not accumulate one entry per spawn/despawn until Exit.
        static void PruneDespawnedSavedModes()
        {
            if (SavedModes.Count == 0) return;
            StaleIds.Clear();
            foreach (KeyValuePair<int, SavedMode> kv in SavedModes)
            {
                Animator live;
                if (!LiveRigs.TryGetValue(kv.Key, out live) || !ReferenceEquals(live, kv.Value.Rig))
                    StaleIds.Add(kv.Key);
            }
            if (StaleIds.Count == 0) return;
            for (int i = 0; i < StaleIds.Count; i++)
                SavedModes.Remove(StaleIds[i]);
            EsLog.Emit(LogLevel.Info, "Governor: animator emergency pruned " + StaleIds.Count
                + " saved mode(s) for despawned rigs (saved=" + SavedModes.Count + ")");
        }

        /// <summary>
        /// Release the emergency. Returns true when the rigs were actually
        /// released (or there was nothing to release), false when the restore
        /// could not run: with no world there is no rig set to restore, and
        /// dropping the saved modes then would declare the emergency released
        /// while every rig stays CullCompletely, with the state needed to fix it
        /// already thrown away. So a no-world exit keeps the saved modes, stays
        /// Active, and says so; the next Exit (a later `es animon`, a reload, the
        /// next tier-2 cycle) completes the restore.
        /// </summary>
        public static bool Exit()
        {
            if (!Active && SavedModes.Count == 0) return true;
            if (GameManager.Instance == null || GameManager.Instance.World == null)
            {
                EsLog.Emit(LogLevel.Warn, "Governor: animator emergency exit SKIPPED "
                    + "(no world loaded) - " + SavedModes.Count + " saved culling mode(s) retained; "
                    + "run 'es animon' once a world is loaded to release the rigs");
                return false;
            }
            int restored = RestoreAllEnemyAnimators();
            SavedModes.Clear();
            Active = false;
            EsLog.Emit(LogLevel.Info, "Governor: animator emergency EXIT - restored cullingMode on " + restored + " rigs");
            return true;
        }

        /// <summary>
        /// Drop a standing emergency because the world changed, WITHOUT restoring.
        /// The rigs every saved mode belongs to were unloaded with the previous
        /// world, so there is nothing left to put back and the <see cref="SavedModes"/>
        /// table is pure garbage from here on. It holds a reference to every rig it
        /// ever saved, and the only thing that prunes it is the sweep inside
        /// <see cref="Enter"/>, so an emergency still armed when the governor was
        /// then disabled would keep that table (and the dead rigs) for the rest of
        /// the process: <see cref="Exit"/> deliberately keeps both when there is no
        /// world to restore into, and no sweep runs to retire them.
        ///
        /// The flag is the worse half of what goes stale here: <c>AnimatorLodPatch</c>
        /// reads <see cref="Active"/> and skips every living enemy's managed
        /// Update/LateUpdate, so leaving it set means a world that was never put
        /// into the emergency has its enemy animator state frozen with no path back
        /// to baseline. A fresh world's rigs are still at their stock culling mode,
        /// which is exactly what a restore would have left behind.
        /// </summary>
        public static void ForgetWorld()
        {
            bool wasActive = Active;
            int dropped = SavedModes.Count;
            SavedModes.Clear();
            LiveRigs.Clear();
            StaleIds.Clear();
            Active = false;
            if (wasActive || dropped > 0)
                EsLog.Emit(LogLevel.Info, "animator emergency dropped for the new world (was "
                    + (wasActive ? "ENTERED" : "inert") + ", " + dropped
                    + " saved mode(s) belonged to the unloaded world and were not restored)");
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
            foreach (Animator anim in LivingEnemyAnimators(world))
            {
                int id = anim.GetInstanceID();
                SavedMode saved;
                AnimatorCullingMode prior;
                if (SavedModes.TryGetValue(id, out saved) && ReferenceEquals(saved.Rig, anim))
                {
                    prior = saved.Prior;
                }
                else
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
            return restored;
        }
    }
}