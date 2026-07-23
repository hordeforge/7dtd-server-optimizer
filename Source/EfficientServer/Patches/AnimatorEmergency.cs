using System.Collections.Generic;
using UnityEngine;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Emergency animator shutdown, driven by the governor's second escalation
    /// tier. Measured basis (RESULTS 3o + fence check): at 64 players + ~400
    /// endgame zombies the animator path (worker-job compute + the main-thread
    /// FENCES on those jobs) is ~60 ms of a ~147 ms saturated frame (~40%) -
    /// disabling all enemy animators recovered the frame from ~147 to ~85 ms.
    ///
    /// While active, combat fidelity degrades in known ways (attack cadence falls
    /// back to its 2 s wall-clock timer, stuns clear next tick, movement uses the
    /// supplementary displacement path instead of root motion) - the same
    /// trade-class as TickGuard, but nothing despawns and clients see no visual
    /// difference (zombie animation is client-local).
    ///
    /// KNOWN DEFECT (human eval + es animstate, RESULTS 3s): exit cannot fully
    /// restore. After enabled=false->true the animator evaluates (state advances,
    /// applyRootMotion intact, AvatarRootMotion forwarder alive) but deltaPosition
    /// stays 0 forever, so restored zombies move at supplementary-path crawl until
    /// death. Rebind + re-pushing one-shot params does not revive the delta.
    /// Planned rework: switch cullingMode to CullCompletely instead of toggling
    /// enabled (headless = everything culled = evaluation stops, binding survives).
    /// Until then this tier stays config-default-false.
    /// </summary>
    public static class AnimatorEmergency
    {
        static readonly List<Animator> Disabled = new List<Animator>();
        public static bool Active { get; private set; }

        // Called on entering the tier, and periodically while in it so zombies
        // spawned mid-emergency get swept too.
        public static void Enter()
        {
            World world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) return;
            int swept = 0;
            List<Entity> entities = world.Entities.list;
            for (int i = 0; i < entities.Count; i++)
            {
                if (!(entities[i] is EntityEnemy enemy)) continue;
                if (enemy.IsDead()) continue;
                Animator[] anims = enemy.GetComponentsInChildren<Animator>(true);
                for (int a = 0; a < anims.Length; a++)
                {
                    if (!anims[a].enabled) continue;
                    anims[a].enabled = false;
                    Disabled.Add(anims[a]);
                    swept++;
                }
            }
            if (!Active || swept > 0)
                ModApi.Log($"Governor: animator emergency {(Active ? "sweep" : "ENTER")} - disabled {swept} rigs");
            Active = true;
        }

        public static void Exit()
        {
            if (!Active) return;
            Disabled.Clear();
            int restored = RestoreAllEnemyAnimators();
            Active = false;
            ModApi.Log($"Governor: animator emergency EXIT - restored {restored} rigs");
        }

        /// <summary>
        /// Re-enable every disabled enemy animator by sweeping live entities (a
        /// saved-ref list goes stale as entities die/pool-recycle while disabled).
        /// Revival needs three steps (human eval found each missing one wedges
        /// zombies into a pushed-only shuffle): Rebind resets a state machine
        /// stuck mid-transition, but also wipes the one-shot spawn parameters
        /// (WalkType, IsAlive) that the AI never rewrites - so re-push those via
        /// the game's own setters, then pump one evaluation.
        /// </summary>
        public static int RestoreAllEnemyAnimators(bool bare = false)
        {
            World world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) return 0;
            int restored = 0;
            List<Entity> entities = world.Entities.list;
            for (int i = 0; i < entities.Count; i++)
            {
                if (!(entities[i] is EntityEnemy enemy)) continue;
                // Corpses stay in Entities.list; reviving their animators poses dead
                // bodies upright as statues (human eval). Death disabled them, not us.
                if (enemy.IsDead()) continue;
                Animator[] anims = enemy.GetComponentsInChildren<Animator>(true);
                int revived = 0;
                for (int a = 0; a < anims.Length; a++)
                {
                    if (anims[a].enabled) continue;
                    anims[a].enabled = true;
                    if (!bare) anims[a].Rebind();
                    revived++;
                }
                if (revived == 0) continue;
                restored += revived;
                AvatarController av = enemy.emodel != null ? enemy.emodel.avatarController : null;
                if (!bare && av != null)
                {
                    av.SetAlive();
                    if (enemy.IsWalkTypeACrawl()) av.TurnIntoCrawler();
                    else av.SetWalkType(enemy.GetWalkType(), true);
                }
                for (int a = 0; a < anims.Length; a++)
                    if (anims[a].enabled) anims[a].Update(0f);
            }
            return restored;
        }
    }
}
