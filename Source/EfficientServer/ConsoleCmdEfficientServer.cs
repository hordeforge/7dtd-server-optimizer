using System;
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
            + "'es benchgod on|off' (arming on requires Diagnostics.AllowBenchGod=true)";

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            string sub = Arg(_params, 0);
            if (sub.Length == 0) sub = "status";
            switch (sub)
            {
                case "reload":
                    // ReloadConfig re-bases the governor and re-applies the start-time
                    // knobs (mesh budgets, target fps, job workers, dedicated skips,
                    // GC incremental) - single choke point.
                    ModApi.ReloadConfig();
                    SdtdConsole.Instance.Output(EsLog.LogPrefix + "config reloaded");
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
                        EsLog.LogPrefix + "unknown subcommand; use: es reload | es status | "
                        + "es animoff | es animon | es animstate | es rigoff | es rigon | es benchgod on|off (diagnostics)");
                    break;
            }
        }

        // Subcommand/argument matching is case-insensitive everywhere, so the
        // lookup folds case itself; both call sites are subcommand words.
        static string Arg(List<string> args, int index)
        {
            if (args == null || index < 0 || index >= args.Count) return "";
            return (args[index] ?? "").Trim().ToLowerInvariant();
        }

        /// <summary>
        /// One choke point for command output that must outlive the console
        /// session: echoes to the live console AND persists via EsLog.Log, so
        /// state-changing commands (animprobe, rigprobe, benchgod) leave an audit
        /// trail in the server log for incident investigation. Read-only bulk
        /// output (status, animstate dumps) stays on SdtdConsole only.
        /// Pass text WITHOUT the mod prefix; both sinks get exactly one.
        /// </summary>
        static void Output(string message)
        {
            try
            {
                var console = SingletonMonoBehaviour<SdtdConsole>.Instance;
                if (console != null)
                    console.Output(EsLog.LogPrefix + message);
            }
            catch (Exception ex)
            {
                EsLog.Warn("console output failed [" + ex.GetType().Name + "]: " + ex.Message);
            }
            EsLog.Log(message);
        }

        static void Status()
        {
            ServerPerfConfig c = ModApi.Config;
            if (c == null) { SdtdConsole.Instance.Output(EsLog.LogPrefix + "no config"); return; }
            SdtdConsole.Instance.Output(
                $"{EsLog.LogPrefix}enabled={c.Enabled} dedicatedOnly={c.DedicatedOnly} | "
                + $"aiLod={c.AiLod.Enabled}(midStride={c.AiLod.MidTickStride}) | "
                + $"graphEvery={c.Pathfinding.GraphUpdateEveryTicks} rescanSq={c.Pathfinding.MoveRescanThresholdSq.ToString(CultureInfo.InvariantCulture)} "
                + $"poolInitScan={c.Pathfinding.PoolInitScanNodes} "
                + $"pathCap={c.Pathfinding.MaxPathEnqueuesPerTick} pathDropFarSq={c.Pathfinding.DropPathWhenFarDistSq.ToString(CultureInfo.InvariantCulture)}");
            SdtdConsole.Instance.Output(
                $"{EsLog.LogPrefix}fastSend={c.Network.FastSingleTargetSend} clientListSnapshot={c.Network.ClientListSnapshot} "
                + $"stride={c.Network.EntityDistributionEveryTicks} "
                + $"chunkBatch={c.WorldTransfer.ChunkPackagesPerObserverPerTick} "
                + $"dynamicMesh={c.DynamicMesh.Enabled}(buffer={c.DynamicMesh.PlayerAreaChunkBuffer} regionMs={c.DynamicMesh.MaxRegionLoadMsPerFrame}) | "
                + $"targetFps={c.Server.TargetFps} jobWorkers={c.Server.JobWorkerCount}");
            SdtdConsole.Instance.Output(
                $"{EsLog.LogPrefix}governor={c.Governor.Enabled}(overMs={c.Governor.OverBudgetMs.ToString(CultureInfo.InvariantCulture)} healthyMs={c.Governor.HealthyMs.ToString(CultureInfo.InvariantCulture)} animEmergency={c.Governor.AnimatorEmergency}) "
                + $"tickGuard={c.TickGuard.Enabled}(shedAboveMs={c.TickGuard.ShedAboveMs.ToString(CultureInfo.InvariantCulture)} batch={c.TickGuard.ShedBatch} minKept={c.TickGuard.MinEnemiesKept})");
            SdtdConsole.Instance.Output(
                $"{EsLog.LogPrefix}gcGuard={c.Gc.SkipForcedCollect}(ceilingMB={c.Gc.SafetyCollectAboveMB} incremental={c.Gc.Incremental}) | "
                + $"animatorLod={c.AnimatorLod.Enabled}(farStride={c.AnimatorLod.FarStride}) "
                + $"crowdCollision={c.CrowdCollisionLod.Enabled}(every={c.CrowdCollisionLod.ResolveEveryNTicks}) | "
                + $"skip(music={c.SkipOnDedicated.DynamicMusicSystem} waterSplash={c.SkipOnDedicated.WaterSplashParticles} "
                + $"envAudio={c.SkipOnDedicated.EnvironmentAudioUpdates} cloth={c.SkipOnDedicated.ClothAndJiggleBoneSimulation} "
                + $"lightSpectrum={c.SkipOnDedicated.AmbientLightSpectrumUpdates} explosionFx={c.SkipOnDedicated.ExplosionParticles}) | "
                + $"diagGcMegapauseProbe={c.Diagnostics.GcMegapauseTest} benchgodAllow={c.Diagnostics.AllowBenchGod}");
            OutputRuntime();
        }

        static void AnimProbe(string sub)
        {
            // DIAGNOSTIC probe / emergency path: set enemy Animator.cullingMode to
            // CullCompletely (keeps enabled=true so root-motion can restore). Same
            // path the governor tier-2 uses. GAMEPLAY DEGRADES WHILE OFF (timer
            // attack cadence, supplementary movement) - bench or emergency only.
            World world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) { SdtdConsole.Instance.Output(EsLog.LogPrefix + "no world"); return; }
            if (sub == "animoff")
            {
                Patches.AnimatorEmergency.Enter();
                Output(
                    "animprobe: ENTER CullCompletely emergency "
                    + $"(active={Patches.AnimatorEmergency.Active}) - read frame time, then 'es animon'");
            }
            else
            {
                Patches.AnimatorEmergency.Exit();
                Output(
                    "animprobe: EXIT emergency; "
                    + "check 'es animstate' for dp>0 on moving zombies");
            }
        }

        static void AnimState()
        {
            // Per-zombie animator truth table for debugging revival wedges.
            World world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) { SdtdConsole.Instance.Output(EsLog.LogPrefix + "no world"); return; }
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
        // Components this probe disabled and has not yet restored, across ALL
        // `es rigoff` calls since the last `es rigon`. Cumulative by design: a
        // repeated rigoff must not drop the first batch from tracking, or `es
        // rigon` would restore only the newest sweep and leave every earlier
        // component disabled until restart. Destroyed rigs are pruned each
        // sweep (PruneDestroyedTracked), so the bound is "live components",
        // not lifetime spawns.
        static readonly List<Behaviour> _rigDisabled = new List<Behaviour>();

        // Tracked components whose entity died or despawned while disabled can
        // never be restored (only an unusable Unity wrapper remains), so keep
        // sweeping them would grow one entry per spawn/despawn for the whole
        // bench session. Drop them at every rigoff, the same prune-per-sweep
        // contract as AnimatorEmergency.PruneDespawnedSavedModes: alive entries
        // stay tracked, so a repeated rigoff still converges additive and one
        // rigon undoes everything still alive.
        static void PruneDestroyedTracked()
        {
            int removed = 0;
            for (int i = _rigDisabled.Count - 1; i >= 0; i--)
            {
                // Unity's overloaded null comparison: a destroyed component
                // reads == null even though the reference is non-null.
                if (_rigDisabled[i] == null)
                {
                    _rigDisabled.RemoveAt(i);
                    removed++;
                }
            }
            if (removed > 0)
                EsLog.Log("rigprobe: pruned " + removed + " tracked component(s) whose "
                    + "rig despawned (tracked=" + _rigDisabled.Count + ")");
        }

        static void RigProbe(string sub)
        {
            // DIAGNOSTIC probe #2 (RE sweep 3n): unguarded visual MonoBehaviours
            // on entity rigs - eyelid blink, gaze, feather flutter, held-light
            // raycast, drone lights. Disable/enable by type name to size their
            // per-frame cost without new assembly references. Visual-only per RE
            // (RagdollWhenHit deliberately excluded: touches physics).
            World world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) { SdtdConsole.Instance.Output(EsLog.LogPrefix + "no world"); return; }
            if (sub == "rigoff")
            {
                // Additive sweep: only still-enabled, not-yet-tracked components
                // are disabled and appended, so rigoff N times converges to the
                // same state as rigoff once and one rigon undoes it all.
                PruneDestroyedTracked();
                int disabled = 0;
                List<Entity> entities = world.Entities.list;
                for (int i = 0; i < entities.Count; i++)
                {
                    Behaviour[] behaviours = entities[i].GetComponentsInChildren<Behaviour>(true);
                    for (int b = 0; b < behaviours.Length; b++)
                    {
                        if (behaviours[b] == null || !behaviours[b].enabled) continue;
                        if (!RigTypes.Contains(behaviours[b].GetType().Name)) continue;
                        if (_rigDisabled.Contains(behaviours[b])) continue;
                        behaviours[b].enabled = false;
                        _rigDisabled.Add(behaviours[b]);
                        disabled++;
                    }
                }
                Output(
                    $"rigprobe: DISABLED {disabled} rig components ({_rigDisabled.Count} tracked; bench only)");
            }
            else
            {
                int restored = 0;
                for (int i = 0; i < _rigDisabled.Count; i++)
                    if (_rigDisabled[i] != null) { _rigDisabled[i].enabled = true; restored++; }
                _rigDisabled.Clear();
                Output($"rigprobe: restored {restored} components");
            }
        }

        static void BenchGod(List<string> _params)
        {
            // BENCH ONLY: player damage immunity so synthetic bots survive
            // endgame hordes and the load stays an active siege (RESULTS 3q).
            string arg = Arg(_params, 1);
            bool changed = arg == "on" || arg == "off";
            if (arg == "on")
            {
                // Second gate beyond console access (THREAT_MODEL R3): arming global
                // damage immunity needs an explicit config opt-in, so a bench-only
                // flag cannot be flipped on a live server by whoever holds telnet.
                // Disabling is always allowed. Refusal goes through the audited
                // output path (console AND log), not just an echo.
                if (!ServerPerfConfig.BenchGodArmAllowed(ModApi.Config))
                {
                    Output(
                        "benchgod REFUSED (flag stays OFF): arming global player damage immunity "
                        + "requires Diagnostics.AllowBenchGod=true in Config/efficientserver.json "
                        + "+ es reload; see docs/CONFIG.md");
                    return;
                }
                Patches.BenchGodPatch.BenchGod = true;
            }
            else if (arg == "off") Patches.BenchGodPatch.BenchGod = false;
            string state = $"benchgod={(Patches.BenchGodPatch.BenchGod ? "ON (bench only!)" : "off")}";
            // A real toggle is audited in the server log (the flag is invisible
            // in game state); a bare `es benchgod` stays a read-only peek.
            if (changed) Output(state);
            else SdtdConsole.Instance.Output(EsLog.LogPrefix + state + " (use on|off)");
        }

        // Live state the config dump above cannot show: which levers are engaged
        // at this instant, the tick EMA driving the governor/tick-guard, and how
        // much work the silent hot-path gates have shed so far. Read-only, so it
        // stays console-only (no log echo).
        static void OutputRuntime()
        {
            SdtdConsole.Instance.Output(
                $"{EsLog.LogPrefix}runtime: modActive={ModApi.ShouldRun()} "
                + $"governorTier={Patches.GovernorPatch.Level} tickEmaMs={Patches.GovernorPatch.EmaMs.ToString("F1", CultureInfo.InvariantCulture)} "
                + $"animatorEmergency={Patches.AnimatorEmergency.Active} | "
                + $"gcSafetyCollects={Patches.GcGuardPatch.SafetyCollects} "
                + $"tickGuardShedTotal={Patches.TickGuardPatch.ShedTotal}");
            SdtdConsole.Instance.Output(
                EsLog.LogPrefix + "runtime: pathDroppedCap=" + Patches.PathAdmissionPatch.DroppedCapTotal
                + " pathDroppedFar=" + Patches.PathAdmissionPatch.DroppedFarTotal
                + " | tasksSkippedFar=" + Patches.UpdateTasksLodPatch.SkippedFarTotal
                + " tasksStridedOff=" + Patches.UpdateTasksLodPatch.StridedOffTotal);
        }
    }
}
