using System.Collections.Generic;
using UnityEngine;

namespace EfficientServer
{
    /// <summary>
    /// Operator console command (auto-discovered by the game's console from loaded
    /// assemblies). Every patch reads the live config object per call, so a file
    /// reload takes effect immediately - no restart. Usage (console or telnet):
    ///   es reload   - re-read Config/efficientserver.json and apply it
    ///   es status   - print the active lever values
    /// </summary>
    public class ConsoleCmdEfficientServer : ConsoleCmdAbstract
    {
        public override string[] getCommands() => new[] { "efficientserver", "es" };
        public override string getDescription() =>
            "EfficientServer: 'es reload' re-reads the config (applies live), 'es status' shows active levers";

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            string sub = _params.Count > 0 ? _params[0].ToLowerInvariant() : "status";
            if (sub == "reload")
            {
                ModApi.ReloadConfig();
                Patches.GameStartPatch.ApplyTargetFps();
                Patches.GameStartPatch.ApplyJobWorkers();
                SdtdConsole.Instance.Output("[EfficientServer] config reloaded");
                sub = "status";
            }
            if (sub == "status")
            {
                ServerPerfConfig c = ModApi.Config;
                if (c == null) { SdtdConsole.Instance.Output("[EfficientServer] no config"); return; }
                SdtdConsole.Instance.Output(
                    $"[EfficientServer] enabled={c.Enabled} | targetFps={c.Server.TargetFps} jobWorkers={c.Server.JobWorkerCount} | graphEvery={c.Pathfinding.GraphUpdateEveryTicks} "
                    + $"rescanSq={c.Pathfinding.MoveRescanThresholdSq} poolInitScan={c.Pathfinding.PoolInitScanNodes} "
                    + $"pathCap={c.Pathfinding.MaxPathEnqueuesPerTick} pathDropFarSq={c.Pathfinding.DropPathWhenFarDistSq} | "
                    + $"fastSend={c.Network.FastSingleTargetSend} stride={c.Network.EntityDistributionEveryTicks} | "
                    + $"chunkBatch={c.WorldTransfer.ChunkPackagesPerObserverPerTick} | "
                    + $"governor={c.Governor.Enabled} tickGuard={c.TickGuard.Enabled} | "
                    + $"gcGuard={c.Gc.SkipForcedCollect} explosionParticlesSkip={c.SkipOnDedicated.ExplosionParticles}");
            }
            else if (sub == "animoff" || sub == "animon")
            {
                // DIAGNOSTIC probe / emergency path: set enemy Animator.cullingMode to
                // CullCompletely (keeps enabled=true so root-motion can restore). Same
                // path the governor tier-2 uses. GAMEPLAY DEGRADES WHILE OFF (timer
                // attack cadence, supplementary movement) - bench or emergency only.
                World world = GameManager.Instance != null ? GameManager.Instance.World : null;
                if (world == null) { SdtdConsole.Instance.Output("[EfficientServer] no world"); return; }
                if (sub == "animoff")
                {
                    Patches.AnimatorEmergency.Enter();
                    SdtdConsole.Instance.Output(
                        "[EfficientServer] animprobe: ENTER CullCompletely emergency "
                        + $"(active={Patches.AnimatorEmergency.Active}) - read frame time, then 'es animon'");
                }
                else
                {
                    // bare is accepted for CLI compat but culling restore ignores it.
                    bool bare = _params.Count > 1 && _params[1] == "bare";
                    Patches.AnimatorEmergency.Exit();
                    SdtdConsole.Instance.Output(
                        $"[EfficientServer] animprobe: EXIT emergency (bare={bare}); "
                        + "check 'es animstate' for dp>0 on moving zombies");
                }
            }
            else if (sub == "animstate")
            {
                // Per-zombie animator truth table for debugging revival wedges.
                World world = GameManager.Instance != null ? GameManager.Instance.World : null;
                if (world == null) { SdtdConsole.Instance.Output("[EfficientServer] no world"); return; }
                int movementHash = Animator.StringToHash("MovementState");
                int aliveHash = Animator.StringToHash("IsAlive");
                int walkHash = Animator.StringToHash("WalkType");
                List<Entity> entities = world.Entities.list;
                for (int i = 0; i < entities.Count; i++)
                {
                    if (!(entities[i] is EntityEnemy enemy)) continue;
                    Animator[] anims = enemy.GetComponentsInChildren<Animator>(true);
                    Animator anim = anims.Length > 0 ? anims[0] : null;
                    if (anim == null) { SdtdConsole.Instance.Output($"  {enemy.entityId} NO ANIMATOR"); continue; }
                    string st = "n/a";
                    if (anim.enabled && anim.isActiveAndEnabled)
                    {
                        AnimatorStateInfo si = anim.GetCurrentAnimatorStateInfo(0);
                        st = $"state={si.shortNameHash} t={si.normalizedTime:F2} trans={anim.IsInTransition(0)}";
                    }
                    AvatarRootMotion rm = enemy.GetComponentInChildren<AvatarRootMotion>(true);
                    SdtdConsole.Instance.Output(
                        $"  {enemy.entityId} {enemy.EntityName}: en={anim.enabled} spd={anim.speed:F2} rootMotion={anim.applyRootMotion} "
                        + $"cull={anim.cullingMode} move={anim.GetInteger(movementHash)} alive={anim.GetBool(aliveHash)} walk={anim.GetInteger(walkHash)} "
                        + $"vel={enemy.motion.magnitude:F3} dp={anim.deltaPosition.magnitude:F4} rmFwd={(rm == null ? "none" : rm.enabled.ToString())} "
                        + $"attackTarget={(enemy.GetAttackTarget() != null)} {st}");
                }
            }
            else if (sub == "rigoff" || sub == "rigon")
            {
                // DIAGNOSTIC probe #2 (RE sweep 3n): unguarded visual MonoBehaviours
                // on entity rigs - eyelid blink, gaze, feather flutter, held-light
                // raycast, drone lights. Disable/enable by type name to size their
                // per-frame cost without new assembly references. Visual-only per RE
                // (RagdollWhenHit deliberately excluded: touches physics).
                World world = GameManager.Instance != null ? GameManager.Instance.World : null;
                if (world == null) { SdtdConsole.Instance.Output("[EfficientServer] no world"); return; }
                var rigTypes = new HashSet<string> {
                    "EyeLidController", "CharacterGazeController", "FeatherFlutter",
                    "LightLODHeld", "DroneRunningLight", "DroneBeamParticle",
                };
                if (sub == "rigoff")
                {
                    _rigDisabled.Clear();
                    List<Entity> entities = world.Entities.list;
                    for (int i = 0; i < entities.Count; i++)
                    {
                        Behaviour[] behaviours = entities[i].GetComponentsInChildren<Behaviour>(true);
                        for (int b = 0; b < behaviours.Length; b++)
                        {
                            if (behaviours[b] == null || !behaviours[b].enabled) continue;
                            if (!rigTypes.Contains(behaviours[b].GetType().Name)) continue;
                            behaviours[b].enabled = false;
                            _rigDisabled.Add(behaviours[b]);
                        }
                    }
                    SdtdConsole.Instance.Output(
                        $"[EfficientServer] rigprobe: DISABLED {_rigDisabled.Count} rig components (bench only)");
                }
                else
                {
                    int restored = 0;
                    for (int i = 0; i < _rigDisabled.Count; i++)
                        if (_rigDisabled[i] != null) { _rigDisabled[i].enabled = true; restored++; }
                    _rigDisabled.Clear();
                    SdtdConsole.Instance.Output($"[EfficientServer] rigprobe: restored {restored} components");
                }
            }
            else if (sub == "benchgod")
            {
                // BENCH ONLY: player damage immunity so synthetic bots survive
                // endgame hordes and the load stays an active siege (RESULTS 3q).
                string arg = _params.Count > 1 ? _params[1].ToLowerInvariant() : "";
                if (arg == "on") Patches.BenchGodPatch.BenchGod = true;
                else if (arg == "off") Patches.BenchGodPatch.BenchGod = false;
                SdtdConsole.Instance.Output(
                    $"[EfficientServer] benchgod={(Patches.BenchGodPatch.BenchGod ? "ON (bench only!)" : "off")}");
            }
            else
            {
                SdtdConsole.Instance.Output(
                    "[EfficientServer] unknown subcommand; use: es reload | es status | "
                    + "es animoff | es animon | es animstate | es rigoff | es rigon | es benchgod on|off (diagnostics)");
            }
        }

        static readonly List<Behaviour> _rigDisabled = new List<Behaviour>();
    }
}
