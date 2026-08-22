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
                        Log($"MISSING TARGET: {g.Name} matched no game method (version drift?) - this optimization is INACTIVE");
                    }
                }
                Log($"init {(missing == 0 ? "OK" : "with " + missing + " MISSING required target(s)")}. "
                    + $"matched methods={methods} dedicatedOnly={Config.DedicatedOnly} path={ModPath}");

                // Post-start setup runs via the sanctioned lifecycle hook, not a
                // Harmony patch on StartGame (no IL match needed just for timing).
                try { ModEvents.GameStartDone.RegisterHandler(Patches.GameStartPatch.OnGameStartDone); }
                catch (Exception ex) { Log("ModEvents.GameStartDone register failed: " + ex.Message); }
            }
            catch (Exception ex)
            {
                Log("InitMod failed: " + ex);
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
            Log("config reloaded; enabled=" + Config.Enabled);
        }

        // A patch can IL-match yet be inert because its config toggle is off. Say so
        // in the init summary so an operator can tell "matched" from "active".
        // Feature keys are the shared ServerPerfConfig.Key* constants, so this map
        // and FeatureActive cannot drift apart by typo; a new patch group adds one
        // constant plus one entry here and one case there.
        static string ConfigNote(Type g)
        {
            ServerPerfConfig c = Config;
            if (c == null) return "";
            bool active = true;
            string feature = null;
            if (g == typeof(Patches.AiLodPatch) || g == typeof(Patches.UpdateTasksLodPatch))
                feature = ServerPerfConfig.KeyAiLod;
            else if (g == typeof(Patches.GcGuardPatch))
                feature = ServerPerfConfig.KeyGc;
            else if (g == typeof(Patches.AstarGraphThrottlePatch))
                feature = ServerPerfConfig.KeyGraphThrottle;
            else if (g == typeof(Patches.AstarMoveThresholdPatch))
                feature = ServerPerfConfig.KeyMoveThreshold;
            else if (g == typeof(Patches.PathAdmissionPatch))
                feature = ServerPerfConfig.KeyPathAdmission;
            else if (g == typeof(Patches.FastSendPatch))
                feature = ServerPerfConfig.KeyFastSend;
            else if (g == typeof(Patches.InitScanPoolPatch))
                feature = ServerPerfConfig.KeyInitScanPool;
            else if (g == typeof(Patches.ChunkSendThrottlePatch))
                feature = ServerPerfConfig.KeyChunkSendThrottle;
            else if (g == typeof(Patches.ExplosionParticlesPatch))
                feature = ServerPerfConfig.KeyExplosionParticles;
            else if (g == typeof(Patches.EntityDistributionStridePatch))
                feature = ServerPerfConfig.KeyEntityDistributionStride;
            else if (g == typeof(Patches.GovernorPatch))
                feature = ServerPerfConfig.KeyGovernor;
            else if (g == typeof(Patches.TickGuardPatch))
                feature = ServerPerfConfig.KeyTickGuard;
            else if (g == typeof(Patches.TargetFpsPatch))
                feature = ServerPerfConfig.KeyTargetFps;
            else if (g == typeof(Patches.BenchGodPatch))
                feature = ServerPerfConfig.KeyBenchGod;
            else if (g == typeof(Patches.CrowdCollisionLodPatch))
                feature = ServerPerfConfig.KeyCrowdCollisionLod;
            else if (g == typeof(Patches.AnimatorLodPatch.UpdatePatch) || g == typeof(Patches.AnimatorLodPatch.LateUpdatePatch))
                feature = ServerPerfConfig.KeyAnimatorLod;
            if (feature != null)
                active = c.FeatureActive(feature, Patches.BenchGodPatch.BenchGod);
            return active ? "" : " (matched but config-disabled)";
        }

        static List<MethodInfo> PatchAllSafe(Type t)
        {
            try
            {
                return _harmony.CreateClassProcessor(t).Patch() ?? new List<MethodInfo>();
            }
            catch (Exception ex)
            {
                Log($"patch {t.Name} failed: {ex.Message}");
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
            try { global::Log.Out("[EfficientServer] " + msg); }
            catch { Console.WriteLine("[EfficientServer] " + msg); }
        }
    }
}
