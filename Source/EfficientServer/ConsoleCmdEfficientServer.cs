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
                    + $"rescanSq={c.Pathfinding.MoveRescanThresholdSq} poolInitScan={c.Pathfinding.PoolInitScanNodes} | "
                    + $"fastSend={c.Network.FastSingleTargetSend} stride={c.Network.EntityDistributionEveryTicks} | "
                    + $"chunkBatch={c.WorldTransfer.ChunkPackagesPerObserverPerTick} | "
                    + $"governor={c.Governor.Enabled} tickGuard={c.TickGuard.Enabled} | "
                    + $"gcGuard={c.Gc.SkipForcedCollect} explosionParticlesSkip={c.SkipOnDedicated.ExplosionParticles}");
            }
            else if (sub == "animoff" || sub == "animon")
            {
                // DIAGNOSTIC probe: RE found every zombie runs a full Unity Animator
                // on the headless server (AlwaysAnimate; the dedicated strip path is
                // bypassed for RootMotion/HasRagdoll entities). The engine-side eval
                // hides in unsymbolized UnityPlayer.so CPU, so the only honest sizing
                // is a runtime A/B: disable all enemy animators, read the frame time,
                // re-enable. GAMEPLAY BREAKS WHILE OFF (root motion, stuns, attack
                // cadence read animator state) - bench servers only, never production.
                World world = GameManager.Instance != null ? GameManager.Instance.World : null;
                if (world == null) { SdtdConsole.Instance.Output("[EfficientServer] no world"); return; }
                if (sub == "animoff")
                {
                    _probeDisabled.Clear();
                    List<Entity> entities = world.Entities.list;
                    for (int i = 0; i < entities.Count; i++)
                    {
                        if (!(entities[i] is EntityEnemy enemy)) continue;
                        Animator[] anims = enemy.GetComponentsInChildren<Animator>(true);
                        for (int a = 0; a < anims.Length; a++)
                        {
                            if (!anims[a].enabled) continue;
                            anims[a].enabled = false;
                            _probeDisabled.Add(anims[a]);
                        }
                    }
                    SdtdConsole.Instance.Output(
                        $"[EfficientServer] animprobe: DISABLED {_probeDisabled.Count} enemy animators "
                        + "- read world.unityDeltaMs, then run 'es animon' to restore (bench only!)");
                }
                else
                {
                    int restored = 0;
                    for (int i = 0; i < _probeDisabled.Count; i++)
                        if (_probeDisabled[i] != null) { _probeDisabled[i].enabled = true; restored++; }
                    _probeDisabled.Clear();
                    SdtdConsole.Instance.Output($"[EfficientServer] animprobe: restored {restored} animators");
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
            else
            {
                SdtdConsole.Instance.Output(
                    "[EfficientServer] unknown subcommand; use: es reload | es status | "
                    + "es animoff | es animon | es rigoff | es rigon (diagnostics)");
            }
        }

        static readonly List<Animator> _probeDisabled = new List<Animator>();
        static readonly List<Behaviour> _rigDisabled = new List<Behaviour>();
    }
}
