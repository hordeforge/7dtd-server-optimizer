using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace EfficientServer
{
    /// <summary>
    /// Operator console command (auto-discovered by the game's console from loaded
    /// assemblies). Every patch reads the live config object per call, so a file
    /// reload takes effect immediately - no restart. Usage (console or telnet):
    ///   es status   - print active lever values plus live counters
    ///   es reload   - re-read Config/efficientserver.json and apply it
    ///   diagnostics - animoff | animon | animstate | rigoff | rigon | benchgod on|off
    /// </summary>
    public class ConsoleCmdEfficientServer : ConsoleCmdAbstract
    {
        public override string[] getCommands() => new[] { "efficientserver", "es" };
        public override string getDescription() =>
            "EfficientServer control: 'es status' shows active levers plus live "
            + "counters, 'es reload' re-reads the config (applies live); diagnostics: "
            + "'es animoff' | 'es animon' | 'es animstate' | 'es rigoff' | 'es rigon' | "
            + "'es benchgod on|off'";

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            string sub = ConsoleCommandUtil.Arg(_params, 0);
            if (sub.Length == 0) sub = "status";
            switch (sub)
            {
                case "reload":
                    // ReloadConfig re-bases the governor and re-applies the start-time
                    // knobs (mesh budgets, target fps, job workers, dedicated skips,
                    // GC incremental) - single choke point.
                    ModApi.ReloadConfig();
                    SdtdConsole.Instance.Output(ModApi.LogPrefix + "config reloaded");
                    Status();
                    break;
                case "status":
                    Status();
                    break;
                case "animoff":
                case "animon":
                    AnimProbe(sub);
                    break;
                case "animstate":
                    AnimState();
                    break;
                case "rigoff":
                case "rigon":
                    RigProbe(sub);
                    break;
                case "benchgod":
                    BenchGod(_params);
                    break;
                default:
                    SdtdConsole.Instance.Output(
                        ModApi.LogPrefix + "unknown subcommand; use: es reload | es status | "
                        + "es animoff | es animon | es animstate | es rigoff | es rigon | es benchgod on|off (diagnostics)");
                    break;
            }
        }

        static void Status()
        {
            ServerPerfConfig c = ModApi.Config;
            if (c == null) { SdtdConsole.Instance.Output(ModApi.LogPrefix + "no config"); return; }
            SdtdConsole.Instance.Output(
                $"{ModApi.LogPrefix}enabled={c.Enabled} dedicatedOnly={c.DedicatedOnly} | "
                + $"aiLod={c.AiLod.Enabled}(midStride={c.AiLod.MidTickStride}) | "
                + $"graphEvery={c.Pathfinding.GraphUpdateEveryTicks} rescanSq={c.Pathfinding.MoveRescanThresholdSq.ToString(CultureInfo.InvariantCulture)} "
                + $"poolInitScan={c.Pathfinding.PoolInitScanNodes} "
                + $"pathCap={c.Pathfinding.MaxPathEnqueuesPerTick} pathDropFarSq={c.Pathfinding.DropPathWhenFarDistSq.ToString(CultureInfo.InvariantCulture)}");
            SdtdConsole.Instance.Output(
                $"{ModApi.LogPrefix}fastSend={c.Network.FastSingleTargetSend} stride={c.Network.EntityDistributionEveryTicks} "
                + $"chunkBatch={c.WorldTransfer.ChunkPackagesPerObserverPerTick} "
                + $"dynamicMesh={c.DynamicMesh.Enabled}(buffer={c.DynamicMesh.PlayerAreaChunkBuffer} regionMs={c.DynamicMesh.MaxRegionLoadMsPerFrame}) | "
                + $"targetFps={c.Server.TargetFps} jobWorkers={c.Server.JobWorkerCount}");
            SdtdConsole.Instance.Output(
                $"{ModApi.LogPrefix}governor={c.Governor.Enabled}(overMs={c.Governor.OverBudgetMs.ToString(CultureInfo.InvariantCulture)} healthyMs={c.Governor.HealthyMs.ToString(CultureInfo.InvariantCulture)} animEmergency={c.Governor.AnimatorEmergency}) "
                + $"tickGuard={c.TickGuard.Enabled}(shedAboveMs={c.TickGuard.ShedAboveMs.ToString(CultureInfo.InvariantCulture)} batch={c.TickGuard.ShedBatch} minKept={c.TickGuard.MinEnemiesKept})");
            SdtdConsole.Instance.Output(
                $"{ModApi.LogPrefix}gcGuard={c.Gc.SkipForcedCollect}(ceilingMB={c.Gc.SafetyCollectAboveMB} incremental={c.Gc.Incremental}) | "
                + $"animatorLod={c.AnimatorLod.Enabled}(farStride={c.AnimatorLod.FarStride}) "
                + $"crowdCollision={c.CrowdCollisionLod.Enabled}(every={c.CrowdCollisionLod.ResolveEveryNTicks}) | "
                + $"skip(music={c.SkipOnDedicated.DynamicMusicSystem} waterSplash={c.SkipOnDedicated.WaterSplashParticles} "
                + $"envAudio={c.SkipOnDedicated.EnvironmentAudioUpdates} cloth={c.SkipOnDedicated.ClothAndJiggleBoneSimulation} "
                + $"lightSpectrum={c.SkipOnDedicated.AmbientLightSpectrumUpdates} explosionFx={c.SkipOnDedicated.ExplosionParticles}) | "
                + $"diagGcMegapauseProbe={c.Diagnostics.GcMegapauseTest}");
            OutputRuntime();
        }

        static void AnimProbe(string sub)
        {
            // DIAGNOSTIC probe / emergency path: set enemy Animator.cullingMode to
            // CullCompletely (keeps enabled=true so root-motion can restore). Same
            // path the governor tier-2 uses. GAMEPLAY DEGRADES WHILE OFF (timer
            // attack cadence, supplementary movement) - bench or emergency only.
            World world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) { SdtdConsole.Instance.Output(ModApi.LogPrefix + "no world"); return; }
            if (sub == "animoff")
            {
                Patches.AnimatorEmergency.Enter();
                ConsoleCommandUtil.Output(
                    "animprobe: ENTER CullCompletely emergency "
                    + $"(active={Patches.AnimatorEmergency.Active}) - read frame time, then 'es animon'");
            }
            else
            {
                Patches.AnimatorEmergency.Exit();
                ConsoleCommandUtil.Output(
                    "animprobe: EXIT emergency; "
                    + "check 'es animstate' for dp>0 on moving zombies");
            }
        }

        static void AnimState()
        {
            // Per-zombie animator truth table for debugging revival wedges.
            World world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) { SdtdConsole.Instance.Output(ModApi.LogPrefix + "no world"); return; }
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
                    st = $"state={si.shortNameHash} t={si.normalizedTime.ToString("F2", CultureInfo.InvariantCulture)} trans={anim.IsInTransition(0)}";
                }
                AvatarRootMotion rm = enemy.GetComponentInChildren<AvatarRootMotion>(true);
                // Invariant numerics: this telnet output is machine-parsed (the
                // validate_anim_path_admission harness reads vel/dp as [0-9.]+),
                // so a comma-decimal host locale must not reach these fields -
                // there "0,120" would parse as 0 and mask real root motion.
                SdtdConsole.Instance.Output(
                    $"  {enemy.entityId} {enemy.EntityName}: en={anim.enabled} spd={anim.speed.ToString("F2", CultureInfo.InvariantCulture)} rootMotion={anim.applyRootMotion} "
                    + $"cull={anim.cullingMode} move={anim.GetInteger(movementHash)} alive={anim.GetBool(aliveHash)} walk={anim.GetInteger(walkHash)} "
                    + $"vel={enemy.motion.magnitude.ToString("F3", CultureInfo.InvariantCulture)} dp={anim.deltaPosition.magnitude.ToString("F4", CultureInfo.InvariantCulture)} rmFwd={(rm == null ? "none" : rm.enabled.ToString())} "
                    + $"attackTarget={(enemy.GetAttackTarget() != null)} {st}");
            }
        }

        static readonly HashSet<string> RigTypes = new HashSet<string> {
            "EyeLidController", "CharacterGazeController", "FeatherFlutter",
            "LightLODHeld", "DroneRunningLight", "DroneBeamParticle",
        };
        static readonly List<Behaviour> _rigDisabled = new List<Behaviour>();

        static void RigProbe(string sub)
        {
            // DIAGNOSTIC probe #2 (RE sweep 3n): unguarded visual MonoBehaviours
            // on entity rigs - eyelid blink, gaze, feather flutter, held-light
            // raycast, drone lights. Disable/enable by type name to size their
            // per-frame cost without new assembly references. Visual-only per RE
            // (RagdollWhenHit deliberately excluded: touches physics).
            World world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) { SdtdConsole.Instance.Output(ModApi.LogPrefix + "no world"); return; }
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
                        if (!RigTypes.Contains(behaviours[b].GetType().Name)) continue;
                        behaviours[b].enabled = false;
                        _rigDisabled.Add(behaviours[b]);
                    }
                }
                ConsoleCommandUtil.Output(
                    $"rigprobe: DISABLED {_rigDisabled.Count} rig components (bench only)");
            }
            else
            {
                int restored = 0;
                for (int i = 0; i < _rigDisabled.Count; i++)
                    if (_rigDisabled[i] != null) { _rigDisabled[i].enabled = true; restored++; }
                _rigDisabled.Clear();
                ConsoleCommandUtil.Output($"rigprobe: restored {restored} components");
            }
        }

        static void BenchGod(List<string> _params)
        {
            // BENCH ONLY: player damage immunity so synthetic bots survive
            // endgame hordes and the load stays an active siege (RESULTS 3q).
            string arg = ConsoleCommandUtil.Arg(_params, 1);
            bool changed = arg == "on" || arg == "off";
            if (arg == "on") Patches.BenchGodPatch.BenchGod = true;
            else if (arg == "off") Patches.BenchGodPatch.BenchGod = false;
            string state = $"benchgod={(Patches.BenchGodPatch.BenchGod ? "ON (bench only!)" : "off")}";
            // A real toggle is audited in the server log (the flag is invisible
            // in game state); a bare `es benchgod` stays a read-only peek.
            if (changed) ConsoleCommandUtil.Output(state);
            else SdtdConsole.Instance.Output(ModApi.LogPrefix + state + " (use on|off)");
        }

        // Live state the config dump above cannot show: which levers are engaged
        // at this instant, the tick EMA driving the governor/tick-guard, and how
        // much work the silent hot-path gates have shed so far. Read-only, so it
        // stays console-only (no log echo).
        static void OutputRuntime()
        {
            SdtdConsole.Instance.Output(
                $"{ModApi.LogPrefix}runtime: modActive={ModApi.ShouldRun()} "
                + $"governorTier={Patches.GovernorPatch.Level} tickEmaMs={Patches.GovernorPatch.EmaMs.ToString("F1", CultureInfo.InvariantCulture)} "
                + $"animatorEmergency={Patches.AnimatorEmergency.Active} | "
                + $"gcSafetyCollects={Patches.GcGuardPatch.SafetyCollects} "
                + $"tickGuardShedTotal={Patches.TickGuardPatch.ShedTotal}");
            SdtdConsole.Instance.Output(
                ModApi.LogPrefix + "runtime: pathDroppedCap=" + Patches.PathAdmissionPatch.DroppedCapTotal
                + " pathDroppedFar=" + Patches.PathAdmissionPatch.DroppedFarTotal
                + " | tasksSkippedFar=" + Patches.UpdateTasksLodPatch.SkippedFarTotal
                + " tasksStridedOff=" + Patches.UpdateTasksLodPatch.StridedOffTotal);
        }
    }
}
