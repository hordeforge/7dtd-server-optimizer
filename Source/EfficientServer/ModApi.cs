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
        // These are the mod's whole read-mostly cross-thread state. `Config` is
        // SWAPPED at runtime (InitMod, ReloadConfig) while non-main threads read
        // it: the LiteNetLib receive thread through
        // ClientListSnapshotPatch's duplicate-IP scan, the connection writer
        // through the send path. Plain static fields give no publication
        // guarantee there - a reader can observe the new Config reference before
        // the field writes that built it are visible, i.e. a config whose
        // sections are still null, and `Active` can be observed true before the
        // config it gates. Volatile makes each write a release, so a reader either
        // sees the whole previous generation or the whole new one, never a
        // half-built mix. Config goes out through ConfigPublication, whose
        // volatile reference carries the release/acquire pair the cross-thread
        // read needs; `Active` gets the same guarantee from its own volatile
        // backing field. (Same reasoning, and the same qualifier, for the
        // dedicated flag resolved in ShouldRun below.) `ModPath` has no
        // cross-thread reader and is resolved once at init.
        public static ServerPerfConfig Config
        {
            get { return ConfigPublication.Current; }
            private set { ConfigPublication.Current = value; }
        }
        public static string ModPath { get; private set; } = "";
        // The path the live config was actually read from, recorded at the same
        // moment the config object is swapped. `es status` prints it, so a knob
        // that does not match the file the operator has open is diagnosable from
        // the console alone. Re-resolving at print time would answer with
        // whatever the search finds NOW, which is a different file if the live
        // one was deleted or a shadow copy appeared mid-session.
        public static string ConfigPath { get; private set; } = "";
        static volatile bool _active;
        public static bool Active { get { return _active; } }
        static Harmony _harmony;

        /// <summary>
        /// Seconds since the mod loaded; the shared age stamp. The clock itself
        /// lives in <see cref="LogLine"/> next to the line renderer that stamps
        /// every record with it, so the log line and this number cannot be two
        /// different clocks. Read it from here in mod code; nothing else should
        /// start its own stopwatch for correlation purposes.
        /// </summary>
        public static double UptimeSeconds { get { return LogLine.UptimeSeconds; } }

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
                    EsLog.Emit(LogLevel.Warn, "EfficientServer is already patched in this process "
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
                ConfigPath = cfgPath;
                EsLog.Emit(LogLevel.Info, cfgFound
                    ? "config: " + cfgPath
                    : "NO CONFIG FILE at " + cfgPath + " - built-in defaults applied");
                if (ServerPerfConfig.LastLoadFailed)
                {
                    // Distinct from the missing-file line above, which is a normal
                    // "no config shipped" case: here the file is there and was
                    // rejected, so the ERROR from Load already named the cause.
                    EsLog.Emit(LogLevel.Error, "CONFIG FILE REJECTED at " + cfgPath
                        + " - the mod runs on built-in defaults, not the operator's tuning; fix the file and 'es reload'");
                }
                if (!Config.Enabled)
                {
                    EsLog.Emit(LogLevel.Info, "disabled by config; patches are installed so reload can enable it");
                }

                _active = true;
                _harmony = new Harmony(HarmonyId);
                LogVersions();
                // Only CLASS-ANNOTATED ([HarmonyPatch]) groups go through the class
                // processor; each is REQUIRED to match a game method, so a zero-match
                // means the target moved on a new build - fail visibly.
                // DedicatedSkipPatch applies its own Harmony prefixes at
                // GameStartDone and logs its own status ("skip-patch ..."), so it is
                // not listed here (the class processor would find nothing on it).
                // DynamicMeshBudgetPatch runs in the same start-time chain but
                // patches no code: it writes stock DynamicMeshSettings statics and
                // logs "mesh budgets ...", so it has no target to match here.
                int methods = 0, missing = 0;
                foreach (KeyValuePair<Type, string> row in RequiredGroups)
                {
                    List<MethodInfo> matched = PatchAllSafe(row.Key);
                    if (matched.Count > 0)
                    {
                        methods += matched.Count;
                        EsLog.Emit(LogLevel.Info, $"patched {row.Key.Name} -> " + string.Join(", ",
                            matched.Select(mm => (mm.DeclaringType != null ? mm.DeclaringType.Name + "." : "") + mm.Name).ToArray())
                            + ConfigNote(row));
                    }
                    else
                    {
                        missing++;
                        EsLog.Emit(LogLevel.Warn, $"MISSING TARGET: {row.Key.Name} matched no game method (version drift?) - this optimization is INACTIVE");
                    }
                }
                string summary = $"init {(missing == 0 ? "OK" : "with " + missing + " MISSING required target(s)")}. "
                    + $"matched methods={methods} dedicatedOnly={Config.DedicatedOnly} path={ModPath}";
                if (missing == 0) EsLog.Emit(LogLevel.Info, summary); else EsLog.Emit(LogLevel.Warn, summary);

                // Post-start setup runs via the sanctioned lifecycle hook, not a
                // Harmony patch on StartGame (no IL match needed just for timing).
                try { ModEvents.GameStartDone.RegisterHandler(Patches.GameStartPatch.OnGameStartDone); }
                catch (Exception ex)
                {
                    EsLog.Emit(LogLevel.Warn, "GameStartDone register failed [" + ex.GetType().Name + "]: "
                        + ex.Message + " - start-time knobs (mesh budgets, target fps, job workers, skips) will not apply");
                }
            }
            catch (Exception ex)
            {
                EsLog.Emit(LogLevel.Error, "InitMod failed: " + ex);
            }
        }

        /// <summary>
        /// Re-read the config file and apply it. Returns false (config object
        /// untouched, no apply) when the file was present but unusable, so the
        /// console never prints a success echo over a reload that did not happen.
        /// </summary>
        public static bool ReloadConfig()
        {
            string path = ServerPerfConfig.DefaultPathBesideAssembly();
            ServerPerfConfig loaded = ServerPerfConfig.Load(path);
            // A rejected file is not recorded below, so `es status` keeps naming
            // the file the live config really came from.
            if (ServerPerfConfig.LastLoadFailed)
            {
                // A rejected file is not a config. Swapping it in would revert every
                // knob the operator tuned back to built-in defaults mid-session, which
                // is worse than the stale value it would have replaced; the ERROR
                // line from Load already names the parse failure and the path.
                EsLog.Emit(LogLevel.Error, "config reload REJECTED: " + path
                    + " was not readable, keeping the previous config; fix the file and rerun 'es reload'");
                return false;
            }
            Config = loaded;
            ConfigPath = path;
            // A probe armed through the console is process state, not config state:
            // it outlives the config edit that armed it, and a bench harness that
            // was killed before its own restore (or an operator taking the opt-in
            // back) leaves players damage-immune / enemy rigs culled with no knob
            // explaining it. Releasing here makes the reload that re-reads the
            // allow-switches the same undo, and both releases are no-ops when
            // nothing is armed, so a repeated reload converges.
            if (!ServerPerfConfig.BenchGodArmAllowed(Config))
                Patches.BenchGodPatch.BenchGod = false;
            if (!ServerPerfConfig.FidelityProbeArmAllowed(Config)
                && ConsoleCmdEfficientServer.ReleaseArmedProbes() > 0)
            {
                EsLog.Emit(LogLevel.Warn, "config reloaded without Diagnostics.AllowFidelityProbes: "
                    + "armed animator/rig probes released");
            }
            ApplyChainResult applied;
            try
            {
                applied = ApplyChain.Run(ReloadSteps);
            }
            catch (Exception ex)
            {
                // A failure in the chain's own machinery (building or walking the
                // step table), so the per-step isolation never ran and an unknown
                // number of levers are un-applied.
                EsLog.Emit(LogLevel.Error, "config reload apply failed [" + ex.GetType().Name
                    + "] - new config loaded, some levers may not have applied: " + ex);
                throw;
            }
            if (applied.AnyFailed)
            {
                // Load already swapped the config object, so after a failed apply the
                // state is "new values live, some levers not applied". Log that with
                // the mod prefix (the game's own command-exception dump is unprefixed),
                // then rethrow so the caller's success echo never prints over a
                // partial apply.
                EsLog.Emit(LogLevel.Error, "config reload partially applied: "
                    + applied.Failed + " of "
                    + (applied.Applied + applied.Failed)
                    + " step(s) did not apply, new config loaded - " + applied.Summary());
                throw new InvalidOperationException(
                    "config reload partially applied: " + applied.Summary());
            }
            EsLog.Emit(LogLevel.Info, "config reloaded; enabled=" + Config.Enabled
                + (File.Exists(path) ? ""
                    : " (NO CONFIG FILE at " + path + " - built-in defaults applied)"));
            return true;
        }

        // The re-apply chain `es reload` runs, in apply order.
        //
        // The governor re-base leads because it is the one step whose effect is
        // not visible in the swapped-in config: the tier machine derives its
        // live levers from the operator's declared intent on every read
        // (GovernorTiers.EffectiveEntityStride / EffectiveGraphEvery), so a
        // reload has to settle the TIER itself and release a standing animator
        // emergency the new config no longer authorizes (GovernorPatch.
        // OnConfigReloaded). The rest re-run the apply-once knobs, so "reload
        // takes effect immediately" holds for them too (all idempotent; they log
        // only real changes). The imperative skip group is installed at
        // GameStartDone ONLY when the then-current config was enabled
        // (ApplyOptional early-outs otherwise), so a disabled->enabled reload
        // must install it here or the contract above ("patches are installed so
        // reload can enable it") silently fails for music/splash/env-audio/
        // spectrum skips until restart; Harmony replaces an existing patch by
        // MethodInfo instead of stacking, and each skip's prefix live-gates on
        // ShouldRun AND its own knob per call, so a reload can also take a skip
        // away again without a restart. GcIncremental joins for the same reason:
        // its one-shot guard is what makes late-enable possible (disable stays
        // impossible by design). Both self-guard on Enabled/ShouldRun.
        //
        // Run through ApplyChain, not one try, so a single throwing lever cannot
        // skip the ones behind it. It used to: a governor re-base failing on a
        // new build left the other five un-reapplied, and the console suppressed
        // its success echo over the "failure", so the operator was told the
        // reload failed with no way to learn that five of the six levers were in
        // fact live.
        static readonly ApplyStep[] ReloadSteps =
        {
            new ApplyStep("governorReBase", Patches.GovernorPatch.OnConfigReloaded),
            new ApplyStep("meshBudgets", Patches.DynamicMeshBudgetPatch.ApplyBudgets),
            new ApplyStep("targetFps", Patches.GameStartPatch.ApplyTargetFps),
            new ApplyStep("jobWorkers", Patches.GameStartPatch.ApplyJobWorkers),
            new ApplyStep("dedicatedSkips", Patches.DedicatedSkipPatch.ApplyOptional),
            new ApplyStep("gcIncremental", GcIncremental.Apply),
        };

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
            new(typeof(Patches.AiLodPatch), ServerPerfConfig.KeyAiLod),
            new(typeof(Patches.UpdateTasksLodPatch), ServerPerfConfig.KeyAiLod),
            new(typeof(Patches.GcGuardPatch), ServerPerfConfig.KeyGc),
            new(typeof(Patches.AstarGraphThrottlePatch), ServerPerfConfig.KeyGraphThrottle),
            new(typeof(Patches.AstarMoveThresholdPatch), ServerPerfConfig.KeyMoveThreshold),
            new(typeof(Patches.PathAdmissionPatch), ServerPerfConfig.KeyPathAdmission),
            new(typeof(Patches.FastSendPatch), ServerPerfConfig.KeyFastSend),
            new(typeof(Patches.ClientListSnapshotPatch), ServerPerfConfig.KeyClientListSnapshot),
            new(typeof(Patches.InitScanPoolPatch), ServerPerfConfig.KeyInitScanPool),
            new(typeof(Patches.ChunkSendThrottlePatch), ServerPerfConfig.KeyChunkSendThrottle),
            new(typeof(Patches.ExplosionParticlesPatch), ServerPerfConfig.KeyExplosionParticles),
            new(typeof(Patches.EntityDistributionStridePatch), ServerPerfConfig.KeyEntityDistributionStride),
            new(typeof(Patches.GovernorPatch), ServerPerfConfig.KeyGovernor),
            new(typeof(Patches.TickGuardPatch), ServerPerfConfig.KeyTickGuard),
            new(typeof(Patches.BenchGodPatch), ServerPerfConfig.KeyBenchGod),
            new(typeof(Patches.CrowdCollisionLodPatch), ServerPerfConfig.KeyCrowdCollisionLod),
            new(typeof(Patches.TargetFpsPatch), ServerPerfConfig.KeyTargetFps),
            new(typeof(Patches.TickClockPatch), null),
            new(typeof(Patches.AnimatorLodPatch.UpdatePatch), ServerPerfConfig.KeyAnimatorLod),
            new(typeof(Patches.AnimatorLodPatch.LateUpdatePatch), ServerPerfConfig.KeyAnimatorLod),
        };

        // A patch can IL-match yet be inert because its config toggle is off. Say so
        // in the init summary so an operator can tell "matched" from "active".
        // Feature keys are the shared ServerPerfConfig.Key* constants from the same
        // table InitMod patches, so this note and FeatureActive cannot drift apart
        // by typo; a new patch group adds one constant plus one row there.
        static string ConfigNote(KeyValuePair<Type, string> row)
        {
            if (Config == null || row.Value == null) return "";
            // The governor drives these two levers itself, off the configured
            // baseline, so a configured-off note would be wrong on the stock
            // template (EntityDistributionEveryTicks 1 + Governor enabled): the
            // patch is in force from governor tier 1 on, and `es status` inForce
            // would contradict an init log that called it config-disabled.
            if (Config.Governor != null && Config.Governor.Enabled
                && (row.Value == ServerPerfConfig.KeyEntityDistributionStride
                    || row.Value == ServerPerfConfig.KeyGraphThrottle))
                return "";
            return Config.FeatureActive(row.Value, Patches.BenchGodPatch.BenchGod)
                ? "" : " (matched but config-disabled)";
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
                EsLog.Emit(LogLevel.Error, $"patch {t.Name} failed: {ex}");
                return new List<MethodInfo>();
            }
        }

        static void LogVersions()
        {
            string mod = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
            string asm = "?";
            // Both reads are best-effort version REPORTING, not a gate: a build
            // whose assembly metadata or Constants shape differs must still get
            // an init log, so an unreadable field stays "?" instead of aborting
            // init. Nothing downstream branches on these strings.
            try { asm = typeof(GameManager).Assembly.GetName().Version?.ToString() ?? "?"; }
            catch { }
            string game = "?";
            try { game = Constants.cVersionInformation?.LongString ?? "?"; }
            catch { }
            bool inc = Config.Gc != null && Config.Gc.Incremental;
            EsLog.Emit(LogLevel.Info, $"versions: mod={mod} Assembly-CSharp={asm} game={game}; "
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
        // and the gate must stay fail-closed until a real answer exists. That is
        // also why the failure is announce-once through Degrade: a host whose
        // singleton read keeps throwing would otherwise re-raise per patch call
        // (far more expensive than the report itself) with nothing logged, and
        // `es status` would show it only as modActive=false, which reads the same
        // as a disabled config; the degraded count is how long the server has run
        // unpatched.
        internal const string DedicatedGateDegradeKey = "dedicatedGate";
        // The answer itself lives in DedicatedHostGate, which owns the
        // cross-thread half: one volatile word for the three states, and a lock
        // that makes the resolution first-writer-wins. Two plain volatile fields
        // here (a resolved flag beside the value) let every thread that found the
        // flag clear run the probe and store its own answer, so a main thread and
        // the LiteNetLib receive thread (the client-list snapshot's duplicate-IP
        // scan calls this gate on every connection request) could interleave and
        // leave a stale "not dedicated" pinned for the life of the process, which
        // deactivates every patch prefix with nothing in the log. The probe is
        // cached in a static field so the slow path allocates nothing.
        static readonly Patches.DedicatedHostGate HostType = new Patches.DedicatedHostGate();
        static readonly Func<bool> ReadHostType = () => GameManager.IsDedicatedServer;

        public static bool ShouldRun() => ShouldRun(ConfigPublication.Current);

        /// <summary>
        /// The host type as every thread sees it, resolving it on first use.
        /// Returns null while it is still unknown, which is not a cached answer:
        /// a read that throws (the game has not published the host type this
        /// early in boot) leaves the gate unresolved, fails closed, and is
        /// retried by the next caller.
        /// </summary>
        static bool? DedicatedHost()
        {
            if (HostType.State == Patches.DedicatedHostGate.Unresolved)
            {
                try
                {
                    HostType.Resolve(ReadHostType);
                }
                catch (Exception ex)
                {
                    // Fail closed: unknown host must not activate server-only patches.
                    // Reported rather than swallowed: this is the one gate every
                    // patch prefix calls, so a persistent failure here leaves the
                    // WHOLE mod inert, and `es status` would show it only as
                    // modActive=false, which reads the same as a disabled config.
                    // A read that keeps throwing is retried on the next call (the
                    // gate is left unresolved, so nothing is cached from a failed
                    // read), so the count on the degraded line is how long the
                    // server has been silently unpatched.
                    if (Degrade.Report(DedicatedGateDegradeKey, "dedicated-host read failed ["
                            + ex.GetType().Name + "]: " + ex.Message
                            + " - the host type is unknown, so EVERY lever is INACTIVE until restart"))
                        EsLog.Emit(LogLevel.Warn, Degrade.FirstReport(DedicatedGateDegradeKey));
                    return null;
                }
            }
            return HostType.IsDedicated;
        }

        /// <summary>
        /// The gate against a config generation the caller has ALREADY read.
        /// Every patch prefix needs the config twice (its own section, then the
        /// master gate), and <see cref="Config"/> reads a volatile reference
        /// (<see cref="ConfigPublication.Current"/>) that is swapped under
        /// non-main-thread readers. Reading it separately for each
        /// use costs a second volatile acquire on paths that run per entity per
        /// tick, and worse, the two reads can straddle a <c>ReloadConfig</c>
        /// swap: a prefix would gate on one generation and then read its knob out
        /// of the NEXT one. Passing the reference the caller already holds pins
        /// both to the same generation. Same decision as the no-arg overload, which
        /// is now this one.
        /// </summary>
        public static bool ShouldRun(ServerPerfConfig cfg)
        {
            bool? isDedicated;
            if (cfg != null && cfg.Enabled && cfg.DedicatedOnly)
            {
                isDedicated = DedicatedHost();
            }
            else
            {
                isDedicated = null; // host type not needed to decide
            }
            return ServerPerfConfig.ShouldRunFor(Active, cfg?.Enabled ?? false, cfg?.DedicatedOnly ?? false, isDedicated);
        }
    }
}
