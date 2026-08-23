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
            "EfficientServer: 'es reload' re-reads the config (applies live), "
            + "'es status' shows active levers plus live governor/gate counters";

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            string sub = _params.Count > 0 ? _params[0].ToLowerInvariant() : "status";
            if (sub == "reload")
            {
                // ReloadConfig re-bases the governor and re-applies the start-time
                // knobs (mesh budgets, target fps, job workers) - single choke point.
                ModApi.ReloadConfig();
                SdtdConsole.Instance.Output("[EfficientServer] config reloaded");
                sub = "status";
            }
            if (sub == "status")
            {
                ServerPerfConfig c = ModApi.Config;
                if (c == null) { SdtdConsole.Instance.Output("[EfficientServer] no config"); return; }
                SdtdConsole.Instance.Output(
                    $"[EfficientServer] enabled={c.Enabled} dedicatedOnly={c.DedicatedOnly} | "
                    + $"aiLod={c.AiLod.Enabled}(midStride={c.AiLod.MidTickStride}) | "
                    + $"graphEvery={c.Pathfinding.GraphUpdateEveryTicks} rescanSq={c.Pathfinding.MoveRescanThresholdSq} "
                    + $"poolInitScan={c.Pathfinding.PoolInitScanNodes} "
                    + $"pathCap={c.Pathfinding.MaxPathEnqueuesPerTick} pathDropFarSq={c.Pathfinding.DropPathWhenFarDistSq}");
                SdtdConsole.Instance.Output(
                    $"[EfficientServer] fastSend={c.Network.FastSingleTargetSend} stride={c.Network.EntityDistributionEveryTicks} "
                    + $"chunkBatch={c.WorldTransfer.ChunkPackagesPerObserverPerTick} "
                    + $"dynamicMesh={c.DynamicMesh.Enabled}(buffer={c.DynamicMesh.PlayerAreaChunkBuffer} regionMs={c.DynamicMesh.MaxRegionLoadMsPerFrame}) | "
                    + $"targetFps={c.Server.TargetFps} jobWorkers={c.Server.JobWorkerCount}");
                SdtdConsole.Instance.Output(
                    $"[EfficientServer] governor={c.Governor.Enabled}(overMs={c.Governor.OverBudgetMs} healthyMs={c.Governor.HealthyMs} animEmergency={c.Governor.AnimatorEmergency}) "
                    + $"tickGuard={c.TickGuard.Enabled}(shedAboveMs={c.TickGuard.ShedAboveMs} batch={c.TickGuard.ShedBatch} minKept={c.TickGuard.MinEnemiesKept})");
                SdtdConsole.Instance.Output(
                    $"[EfficientServer] gcGuard={c.Gc.SkipForcedCollect}(ceilingMB={c.Gc.SafetyCollectAboveMB} incremental={c.Gc.Incremental}) | "
                    + $"animatorLod={c.AnimatorLod.Enabled}(farStride={c.AnimatorLod.FarStride}) "
                    + $"crowdCollision={c.CrowdCollisionLod.Enabled}(every={c.CrowdCollisionLod.ResolveEveryNTicks}) | "
                    + $"skip(music={c.SkipOnDedicated.DynamicMusicSystem} waterSplash={c.SkipOnDedicated.WaterSplashParticles} "
                    + $"envAudio={c.SkipOnDedicated.EnvironmentAudioUpdates} cloth={c.SkipOnDedicated.ClothAndJiggleBoneSimulation} "
                    + $"lightSpectrum={c.SkipOnDedicated.AmbientLightSpectrumUpdates} explosionFx={c.SkipOnDedicated.ExplosionParticles}) | "
                    + $"diagGcMegapauseProbe={c.Diagnostics.GcMegapauseTest}");
                OutputRuntime();
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
                    ConsoleCommandUtil.Output(
                        "animprobe: ENTER CullCompletely emergency "
                        + $"(active={Patches.AnimatorEmergency.Active}) - read frame time, then 'es animon'");
                }
                else
                {
                    // bare is accepted for CLI compat but culling restore ignores it.
                    bool bare = _params.Count > 1 && _params[1] == "bare";
                    Patches.AnimatorEmergency.Exit();
                    ConsoleCommandUtil.Output(
                        $"animprobe: EXIT emergency (bare={bare}); "
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
            else if (sub == "benchgod")
            {
                // BENCH ONLY: player damage immunity so synthetic bots survive
                // endgame hordes and the load stays an active siege (RESULTS 3q).
                // The toggle itself is invisible in game state, so the confirmation
                // doubles as the audit line in the server log.
                string arg = _params.Count > 1 ? _params[1].ToLowerInvariant() : "";
                bool changed = arg == "on" || arg == "off";
                if (arg == "on") Patches.BenchGodPatch.BenchGod = true;
                else if (arg == "off") Patches.BenchGodPatch.BenchGod = false;
                string state = $"benchgod={(Patches.BenchGodPatch.BenchGod ? "ON (bench only!)" : "off")}";
                // A real toggle is audited in the server log (the flag is invisible
                // in game state); a bare `es benchgod` stays a read-only peek.
                if (changed) ConsoleCommandUtil.Output(state);
                else SdtdConsole.Instance.Output(ModApi.LogPrefix + state + " (use on|off)");
            }
            else
            {
                SdtdConsole.Instance.Output(
                    "[EfficientServer] unknown subcommand; use: es reload | es status | "
                    + "es animoff | es animon | es animstate | es rigoff | es rigon | es benchgod on|off (diagnostics)");
            }
        }

        static readonly List<Behaviour> _rigDisabled = new List<Behaviour>();

        // Live state the config dump above cannot show: which levers are engaged
        // at this instant, the tick EMA driving the governor/tick-guard, and how
        // much work the silent hot-path gates have shed so far. Read-only, so it
        // stays console-only (no log echo).
        static void OutputRuntime()
        {
            SdtdConsole.Instance.Output(
                $"[EfficientServer] runtime: modActive={ModApi.ShouldRun()} "
                + $"governorTier={Patches.GovernorPatch.Level} tickEmaMs={Patches.GovernorPatch.EmaMs:F1} "
                + $"animatorEmergency={Patches.AnimatorEmergency.Active} | "
                + $"gcSafetyCollects={Patches.GcGuardPatch.SafetyCollects} "
                + $"tickGuardShedTotal={Patches.TickGuardPatch.ShedTotal}");
            SdtdConsole.Instance.Output(
                "[EfficientServer] runtime: pathDroppedCap=" + Patches.PathAdmissionPatch.DroppedCapTotal
                + " pathDroppedFar=" + Patches.PathAdmissionPatch.DroppedFarTotal
                + " | tasksSkippedFar=" + Patches.UpdateTasksLodPatch.SkippedFarTotal
                + " tasksStridedOff=" + Patches.UpdateTasksLodPatch.StridedOffTotal);
        }
    }
}
