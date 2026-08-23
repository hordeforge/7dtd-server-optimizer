using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace EfficientServer
{
    public class ModApi : IModApi
    {
        public const string HarmonyId = "com.7dtd.efficientserver";
        // Single source of the mod prefix so console echo and log lines stay
        // greppable under one tag across all three severity channels.
        public const string LogPrefix = "[EfficientServer] ";
        public static ServerPerfConfig Config { get; private set; } = new ServerPerfConfig();
        public static string ModPath { get; private set; } = "";
        public static bool Active { get; private set; }
        static Harmony _harmony;

        public void InitMod(Mod _modInstance)
        {
            try
            {
                ModPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                Config = ServerPerfConfig.Load(ServerPerfConfig.DefaultPathBesideAssembly());
                if (!Config.Enabled)
                {
                    Log("disabled by config; patches are installed so reload can enable it");
                }

                Active = true;
                _harmony = new Harmony(HarmonyId);
                LogVersions();
                // Only CLASS-ANNOTATED ([HarmonyPatch]) groups go through the class
                // processor; each is REQUIRED to match a game method, so a zero-match
                // means the target moved on a new build - fail visibly. The
                // imperative patches (DedicatedSkipPatch, DynamicMeshBudgetPatch)
                // apply their own Harmony patches at GameStartDone and log their own
                // status ("skip-patch ...", "mesh budgets ..."), so they are not
                // listed here (the class processor would find nothing on them).
                Type[] groups =
                {
                    typeof(Patches.AiLodPatch), typeof(Patches.UpdateTasksLodPatch),
                    typeof(Patches.GcGuardPatch), typeof(Patches.AstarGraphThrottlePatch),
                    typeof(Patches.AstarMoveThresholdPatch), typeof(Patches.PathAdmissionPatch),
                    typeof(Patches.FastSendPatch),
                    typeof(Patches.InitScanPoolPatch), typeof(Patches.ChunkSendThrottlePatch),
                    typeof(Patches.ExplosionParticlesPatch),
                    typeof(Patches.EntityDistributionStridePatch),
                    typeof(Patches.GovernorPatch), typeof(Patches.TickGuardPatch),
                    typeof(Patches.BenchGodPatch), typeof(Patches.CrowdCollisionLodPatch),
                    typeof(Patches.TargetFpsPatch), typeof(Patches.AnimatorLodPatch.UpdatePatch),
                    typeof(Patches.AnimatorLodPatch.LateUpdatePatch),
                };
                int methods = 0, missing = 0;
                foreach (Type g in groups)
                {
                    List<MethodInfo> matched = PatchAllSafe(g);
                    if (matched.Count > 0)
                    {
                        methods += matched.Count;
                        Log($"patched {g.Name} -> " + string.Join(", ",
                            matched.Select(mm => (mm.DeclaringType != null ? mm.DeclaringType.Name + "." : "") + mm.Name).ToArray())
                            + ConfigNote(g));
                    }
                    else
                    {
                        missing++;
                        Warn($"MISSING TARGET: {g.Name} matched no game method (version drift?) - this optimization is INACTIVE");
                    }
                }
                string summary = $"init {(missing == 0 ? "OK" : "with " + missing + " MISSING required target(s)")}. "
                    + $"matched methods={methods} dedicatedOnly={Config.DedicatedOnly} path={ModPath}";
                if (missing == 0) Log(summary); else Warn(summary);

                // Post-start setup runs via the sanctioned lifecycle hook, not a
                // Harmony patch on StartGame (no IL match needed just for timing).
                try { ModEvents.GameStartDone.RegisterHandler(Patches.GameStartPatch.OnGameStartDone); }
                catch (Exception ex)
                {
                    Warn("GameStartDone register failed [" + ex.GetType().Name + "]: "
                        + ex.Message + " - start-time knobs (mesh budgets, target fps, job workers, skips) will not apply");
                }
            }
            catch (Exception ex)
            {
                Error("InitMod failed: " + ex);
            }
        }

        public static void ReloadConfig()
        {
            Config = ServerPerfConfig.Load(ServerPerfConfig.DefaultPathBesideAssembly());
            // The governor holds state derived from the PREVIOUS config object
            // (cached vanilla base + in-place throttle levers); re-base it before
            // anything reads the new object mid-tier.
            Patches.GovernorPatch.OnConfigReloaded();
            // Re-run the apply-once knobs so "reload takes effect immediately" holds
            // for them too (idempotent; they log only real changes).
            Patches.DynamicMeshBudgetPatch.ApplyBudgets();
            Patches.GameStartPatch.ApplyTargetFps();
            Patches.GameStartPatch.ApplyJobWorkers();
            // The imperative skip group is installed at GameStartDone ONLY when the
            // then-current config was enabled (ApplyOptional early-outs otherwise),
            // so a disabled->enabled reload must install it here or the contract
            // above ("patches are installed so reload can enable it") silently fails
            // for music/splash/env-audio/spectrum skips until restart. Idempotent:
            // Harmony replaces an existing patch by MethodInfo instead of stacking,
            // and SkipIfDedicated live-gates on ShouldRun either way.
            // GcIncremental joins for the same reason: its one-shot guard is what
            // makes late-enable possible (disable stays impossible by design).
            // Both calls self-guard on Enabled/ShouldRun. The megapause diagnostic
            // is deliberately NOT re-run here: it blocks threads for minutes on
            // purpose, so it stays a start-time-only lever.
            Patches.DedicatedSkipPatch.ApplyOptional(new Harmony(HarmonyId + ".optional"));
            GcIncremental.Apply(Config != null ? Config.Gc : null);
            Log("config reloaded; enabled=" + Config.Enabled);
        }

        // A patch can IL-match yet be inert because its config toggle is off. Say so
        // in the init summary so an operator can tell "matched" from "active".
        // Feature keys are the shared ServerPerfConfig.Key* constants, so this map
        // and FeatureActive cannot drift apart by typo; a new patch group adds one
        // constant plus one entry here and one case there.
        static readonly Dictionary<Type, string> FeatureKeys = new Dictionary<Type, string>
        {
            { typeof(Patches.AiLodPatch), ServerPerfConfig.KeyAiLod },
            { typeof(Patches.UpdateTasksLodPatch), ServerPerfConfig.KeyAiLod },
            { typeof(Patches.GcGuardPatch), ServerPerfConfig.KeyGc },
            { typeof(Patches.AstarGraphThrottlePatch), ServerPerfConfig.KeyGraphThrottle },
            { typeof(Patches.AstarMoveThresholdPatch), ServerPerfConfig.KeyMoveThreshold },
            { typeof(Patches.PathAdmissionPatch), ServerPerfConfig.KeyPathAdmission },
            { typeof(Patches.FastSendPatch), ServerPerfConfig.KeyFastSend },
            { typeof(Patches.InitScanPoolPatch), ServerPerfConfig.KeyInitScanPool },
            { typeof(Patches.ChunkSendThrottlePatch), ServerPerfConfig.KeyChunkSendThrottle },
            { typeof(Patches.ExplosionParticlesPatch), ServerPerfConfig.KeyExplosionParticles },
            { typeof(Patches.EntityDistributionStridePatch), ServerPerfConfig.KeyEntityDistributionStride },
            { typeof(Patches.GovernorPatch), ServerPerfConfig.KeyGovernor },
            { typeof(Patches.TickGuardPatch), ServerPerfConfig.KeyTickGuard },
            { typeof(Patches.TargetFpsPatch), ServerPerfConfig.KeyTargetFps },
            { typeof(Patches.BenchGodPatch), ServerPerfConfig.KeyBenchGod },
            { typeof(Patches.CrowdCollisionLodPatch), ServerPerfConfig.KeyCrowdCollisionLod },
            { typeof(Patches.AnimatorLodPatch.UpdatePatch), ServerPerfConfig.KeyAnimatorLod },
            { typeof(Patches.AnimatorLodPatch.LateUpdatePatch), ServerPerfConfig.KeyAnimatorLod },
        };

        static string ConfigNote(Type g)
        {
            if (Config == null) return "";
            return FeatureKeys.TryGetValue(g, out string feature)
                && !Config.FeatureActive(feature, Patches.BenchGodPatch.BenchGod)
                ? " (matched but config-disabled)"
                : "";
        }

        static List<MethodInfo> PatchAllSafe(Type t)
        {
            try
            {
                return _harmony.CreateClassProcessor(t).Patch() ?? new List<MethodInfo>();
            }
            catch (Exception ex)
            {
                // Full exception, not just Message: Harmony failures name the failing
                // IL stage in inner exceptions, and this fires once per group at init.
                Error($"patch {t.Name} failed: {ex}");
                return new List<MethodInfo>();
            }
        }

        static void LogVersions()
        {
            string mod = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
            string asm = "?";
            try { asm = typeof(GameManager).Assembly.GetName().Version?.ToString() ?? "?"; }
            catch { /* ignore */ }
            string game = "?";
            try { game = Constants.cVersionInformation?.LongString ?? "?"; }
            catch { /* older/newer builds may differ */ }
            bool inc = Config.Gc != null && Config.Gc.Incremental;
            Log($"versions: mod={mod} Assembly-CSharp={asm} game={game}; "
                + $"config(enabled={Config.Enabled}, dedicatedOnly={Config.DedicatedOnly}, "
                + $"gcGuard={(Config.Gc != null && Config.Gc.SkipForcedCollect)}, gcIncremental={inc})");
        }

        public static bool ShouldRun()
        {
            bool? isDedicated = null;
            if (Config != null && Config.Enabled && Config.DedicatedOnly)
            {
                try
                {
                    isDedicated = GameManager.IsDedicatedServer;
                }
                catch
                {
                    // Fail closed: unknown host must not activate server-only patches.
                    isDedicated = false;
                }
            }
            return ServerPerfConfig.ShouldRunFor(Active, Config?.Enabled ?? false, Config?.DedicatedOnly ?? false, isDedicated);
        }

        public static void Log(string msg)
        {
            Emit(global::Log.Out, msg);
        }

        // Recoverable problems an operator must notice when grepping the log for
        // WARNING: config corrections, skipped/missing optional targets, failed
        // applies that fell back to vanilla behavior.
        public static void Warn(string msg)
        {
            Emit(global::Log.Warning, msg);
        }

        // Failures that leave a patch group INACTIVE or the mod partially broken:
        // version drift, patch application exceptions, init aborts.
        public static void Error(string msg)
        {
            Emit(global::Log.Error, msg);
        }

        // The game's Log static writes to the dedicated log file and console; if it
        // is unavailable (very early init, odd host), fall back to stdout rather
        // than losing the line.
        static void Emit(Action<string> sink, string msg)
        {
            string line = LogPrefix + msg;
            try { sink(line); }
            catch { Console.WriteLine(line); }
        }
    }
}
