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
    /// difference (zombie animation is client-local). Exit re-enables every rig
    /// with an immediate Update(0) pump (the bare-enable revival trap, 3m-bis).
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
            int restored = 0;
            for (int i = 0; i < Disabled.Count; i++)
            {
                if (Disabled[i] == null) continue;
                Disabled[i].enabled = true;
                Disabled[i].Update(0f); // proper revival: resume from a fresh evaluation
                restored++;
            }
            Disabled.Clear();
            Active = false;
            ModApi.Log($"Governor: animator emergency EXIT - restored {restored} rigs");
        }
    }
}
