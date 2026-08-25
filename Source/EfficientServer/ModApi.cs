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
                // Idempotency guard: patches under this Harmony id already present
                // means a second EfficientServer copy loaded (or init re-ran) in
                // this process. Re-running from here would STACK every prefix -
                // TickClock.Advance would step the shared tick clock twice per tick
                // and corrupt every stride consumer, per-patch counters would double,
                // and GameStartDone would fire this handler twice - so converge to a
                // logged no-op and leave the process to the first-loaded copy.
                if (Harmony.HasAnyPatches(HarmonyId))
                {
                    EsLog.Warn("EfficientServer is already patched in this process "
                        + "(duplicate mod copy or repeated init); skipping init");
                    return;
                }
                ModPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                // Name the exact file that was consulted BEFORE loading, so an
                // operator who edited a copy in the wrong place sees why their knobs
                // did not apply (missing file = built-in defaults, not an error).
                string cfgPath = ServerPerfConfig.DefaultPathBesideAssembly();
                bool cfgFound = File.Exists(cfgPath);
                Config = ServerPerfConfig.Load(cfgPath);
                EsLog.Log(cfgFound
                    ? "config: " + cfgPath
                    : "NO CONFIG FILE at " + cfgPath + " - built-in defaults applied");
                if (!Config.Enabled)
                {
                    EsLog.Log("disabled by config; patches are installed so reload can enable it");
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
                int methods = 0, missing = 0;
                foreach (KeyValuePair<Type, string> row in RequiredGroups)
                {
                    List<MethodInfo> matched = PatchAllSafe(row.Key);
                    if (matched.Count > 0)
                    {
                        methods += matched.Count;
                        EsLog.Log($"patched {row.Key.Name} -> " + string.Join(", ",
                            matched.Select(mm => (mm.DeclaringType != null ? mm.DeclaringType.Name + "." : "") + mm.Name).ToArray())
                            + ConfigNote(row));
                    }
                    else
                    {
                        missing++;
                        EsLog.Warn($"MISSING TARGET: {row.Key.Name} matched no game method (version drift?) - this optimization is INACTIVE");
                    }
                }
                string summary = $"init {(missing == 0 ? "OK" : "with " + missing + " MISSING required target(s)")}. "
                    + $"matched methods={methods} dedicatedOnly={Config.DedicatedOnly} path={ModPath}";
                if (missing == 0) EsLog.Log(summary); else EsLog.Warn(summary);

                // Post-start setup runs via the sanctioned lifecycle hook, not a
                // Harmony patch on StartGame (no IL match needed just for timing).
                try { ModEvents.GameStartDone.RegisterHandler(Patches.GameStartPatch.OnGameStartDone); }
                catch (Exception ex)
                {
                    EsLog.Warn("GameStartDone register failed [" + ex.GetType().Name + "]: "
                        + ex.Message + " - start-time knobs (mesh budgets, target fps, job workers, skips) will not apply");
                }
            }
            catch (Exception ex)
            {
                EsLog.Error("InitMod failed: " + ex);
            }
        }

        public static void ReloadConfig()
        {
            string path = ServerPerfConfig.DefaultPathBesideAssembly();
            Config = ServerPerfConfig.Load(path);
            try
            {
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
                // Harmony replaces an existing patch by MethodInfo instead of stacking.
                // Each skip's prefix live-gates on ShouldRun AND its own knob per call,
                // so a reload can take a skip away again without a restart too.
                // GcIncremental joins for the same reason: its one-shot guard is what
                // makes late-enable possible (disable stays impossible by design).
                // Both calls self-guard on Enabled/ShouldRun. The megapause diagnostic
                // is deliberately NOT re-run here: it blocks threads for minutes on
                // purpose, so it stays a start-time-only lever.
                Patches.DedicatedSkipPatch.ApplyOptional();
                GcIncremental.Apply();
            }
            catch (Exception ex)
            {
                // Load already swapped the config object, so after a failed apply the
                // state is "new values live, some levers not applied". Log that with
                // the mod prefix (the game's own command-exception dump is unprefixed),
                // then rethrow so the caller's success echo never prints over a
                // partial apply.
                EsLog.Error("config reload apply failed [" + ex.GetType().Name
                    + "] - new config loaded, some levers may not have applied: " + ex);
                throw;
            }
            EsLog.Log("config reloaded; enabled=" + Config.Enabled
                + (File.Exists(path) ? ""
                    : " (NO CONFIG FILE at " + path + " - built-in defaults applied)"));
        }

        // One ordered table owns BOTH lists that used to live apart: the required
        // patch groups InitMod installs (in this order) and the
        // ServerPerfConfig.Key* feature key each group's status note reports
        // against. A single row per group means the apply list and the note map
        // cannot drift apart; under the old array + dictionary pair, a new group
        // added to only one side either silently never patched or silently lost
        // its "(matched but config-disabled)" note. A null feature marks a group
        // with no config knob behind it - TickClockPatch is unconditional by
        // design (no gate may stop a clock other stripes read), so it can never
        // be config-disabled.
        static readonly KeyValuePair<Type, string>[] RequiredGroups =
        {
            new KeyValuePair<Type, string>(typeof(Patches.AiLodPatch), ServerPerfConfig.KeyAiLod),
            new KeyValuePair<Type, string>(typeof(Patches.UpdateTasksLodPatch), ServerPerfConfig.KeyAiLod),
            new KeyValuePair<Type, string>(typeof(Patches.GcGuardPatch), ServerPerfConfig.KeyGc),
            new KeyValuePair<Type, string>(typeof(Patches.AstarGraphThrottlePatch), ServerPerfConfig.KeyGraphThrottle),
            new KeyValuePair<Type, string>(typeof(Patches.AstarMoveThresholdPatch), ServerPerfConfig.KeyMoveThreshold),
            new KeyValuePair<Type, string>(typeof(Patches.PathAdmissionPatch), ServerPerfConfig.KeyPathAdmission),
            new KeyValuePair<Type, string>(typeof(Patches.FastSendPatch), ServerPerfConfig.KeyFastSend),
            new KeyValuePair<Type, string>(typeof(Patches.InitScanPoolPatch), ServerPerfConfig.KeyInitScanPool),
            new KeyValuePair<Type, string>(typeof(Patches.ChunkSendThrottlePatch), ServerPerfConfig.KeyChunkSendThrottle),
            new KeyValuePair<Type, string>(typeof(Patches.ExplosionParticlesPatch), ServerPerfConfig.KeyExplosionParticles),
            new KeyValuePair<Type, string>(typeof(Patches.EntityDistributionStridePatch), ServerPerfConfig.KeyEntityDistributionStride),
            new KeyValuePair<Type, string>(typeof(Patches.GovernorPatch), ServerPerfConfig.KeyGovernor),
            new KeyValuePair<Type, string>(typeof(Patches.TickGuardPatch), ServerPerfConfig.KeyTickGuard),
            new KeyValuePair<Type, string>(typeof(Patches.BenchGodPatch), ServerPerfConfig.KeyBenchGod),
            new KeyValuePair<Type, string>(typeof(Patches.CrowdCollisionLodPatch), ServerPerfConfig.KeyCrowdCollisionLod),
            new KeyValuePair<Type, string>(typeof(Patches.TargetFpsPatch), ServerPerfConfig.KeyTargetFps),
            new KeyValuePair<Type, string>(typeof(Patches.TickClockPatch), null),
            new KeyValuePair<Type, string>(typeof(Patches.AnimatorLodPatch.UpdatePatch), ServerPerfConfig.KeyAnimatorLod),
            new KeyValuePair<Type, string>(typeof(Patches.AnimatorLodPatch.LateUpdatePatch), ServerPerfConfig.KeyAnimatorLod),
        };

        // A patch can IL-match yet be inert because its config toggle is off. Say so
        // in the init summary so an operator can tell "matched" from "active".
        // Feature keys are the shared ServerPerfConfig.Key* constants from the same
        // table InitMod patches, so this note and FeatureActive cannot drift apart
        // by typo; a new patch group adds one constant plus one row there.
        static string ConfigNote(KeyValuePair<Type, string> row)
        {
            if (Config == null) return "";
            return row.Value != null
                && !Config.FeatureActive(row.Value, Patches.BenchGodPatch.BenchGod)
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
                EsLog.Error($"patch {t.Name} failed: {ex}");
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
            EsLog.Log($"versions: mod={mod} Assembly-CSharp={asm} game={game}; "
                + $"config(enabled={Config.Enabled}, dedicatedOnly={Config.DedicatedOnly}, "
                + $"gcGuard={(Config.Gc != null && Config.Gc.SkipForcedCollect)}, gcIncremental={inc})");
        }

        // Resolved-once host type. Dedicated-ness is fixed for the process lifetime
        // (set by the server command line / prefs before mods load), so the answer
        // never changes after the first successful read. This gate runs on every
        // patch call, including per-entity-per-tick paths (updateTasks LOD,
        // animator Update/LateUpdate gates) and the FastSend replication fan-out
        // (~7 sends x entities x players per tick), so repeating the singleton
        // read + exception scaffolding each time is pure overhead. A failed read
        // is NOT cached: early during boot the game singleton may not exist yet,
        // and the gate must stay fail-closed until a real answer exists.
        // volatile publication: ShouldRun is the one gate every patch prefix
        // calls, including surfaces whose caller set could grow off-main (the
        // ARCHITECTURE concurrency rule reserves plain statics for proven
        // main-thread paths). Volatile keeps the resolved flag from publishing
        // before the value it guards, so no thread can ever observe
        // "_dedicatedResolved == true" with a stale _isDedicated.
        static volatile bool _dedicatedResolved;
        static volatile bool _isDedicated;

        public static bool ShouldRun()
        {
            ServerPerfConfig cfg = Config;
            bool? isDedicated;
            if (cfg != null && cfg.Enabled && cfg.DedicatedOnly)
            {
                if (!_dedicatedResolved)
                {
                    try
                    {
                        _isDedicated = GameManager.IsDedicatedServer;
                        _dedicatedResolved = true;
                    }
                    catch
                    {
                        // Fail closed: unknown host must not activate server-only patches.
                        return false;
                    }
                }
                isDedicated = _isDedicated;
            }
            else
            {
                isDedicated = null; // host type not needed to decide
            }
            return ServerPerfConfig.ShouldRunFor(Active, cfg?.Enabled ?? false, cfg?.DedicatedOnly ?? false, isDedicated);
        }
    }
}
