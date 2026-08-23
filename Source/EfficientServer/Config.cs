using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EfficientServer
{
    public sealed class AiLodConfig
    {
        public bool Enabled { get; set; } = true;
        public float FullAiDistSq { get; set; } = 100f;
        public float MediumAiDistSq { get; set; } = 400f;
        public float FullScale { get; set; } = 1f;
        public float MediumScale { get; set; } = 0.2f;
        public float FarScale { get; set; } = 0.05f;
        public float SkipTasksFarDistSq { get; set; } = 2500f;
        public bool SkipTasksUnlessAlerted { get; set; } = true;
        // Mid-band entity-AI tick-striding: entities between MediumAiDistSq and
        // SkipTasksFarDistSq run the heavy updateTasks tail (path follow + EAI +
        // the 1236-IL UpdateMoveHelper) only every Nth frame, striped by entity id
        // so the per-tick entity cost is spread. 1 = off (every tick). CheckDespawn
        // still runs every tick; alerted/targeting entities are never strided.
        public int MidTickStride { get; set; } = 1;
    }

    // Each member names the WORK BEING SKIPPED on the dedicated server (true = skip
    // it). All of it produces output only a renderer/speaker could show, so skipping
    // is gameplay-neutral by construction.
    public sealed class SkipConfig
    {
        // The dynamic-music conductor (mood-driven soundtrack selection).
        public bool DynamicMusicSystem { get; set; } = true;
        // Water splash particle-cube updates (visual splashes).
        public bool WaterSplashParticles { get; set; } = true;
        // Ambient environment-audio graph updates (wind, biome beds).
        public bool EnvironmentAudioUpdates { get; set; } = true;
        // Cloth physics + jiggle-bone simulation on characters (capes, flapping
        // clothes, body jiggle - pure visual deformation).
        public bool ClothAndJiggleBoneSimulation { get; set; } = true;
        // Per-frame ambient light-spectrum lerp writing RenderSettings colors that
        // nothing headless reads (light-level -> stealth is client-computed).
        public bool AmbientLightSpectrumUpdates { get; set; } = true;
        // Skip Object.Instantiate of the explosion particle prefab (headless server,
        // never rendered). Gameplay side effects (physics push, block changes, quest
        // event) are preserved. Measured A/B at blood-moon load: ~1.1 ms of the ~10 ms
        // per explosion (~10%); the bulk is block-destruction application (gameplay).
        public bool ExplosionParticles { get; set; } = true;
    }

    public sealed class DynamicMeshConfig
    {
        public bool Enabled { get; set; } = true;
        public bool OnlyPlayerAreas { get; set; } = true;
        public int PlayerAreaChunkBuffer { get; set; } = 2;
        public int MaxRegionLoadMsPerFrame { get; set; } = 2;
        public int MaxActiveSyncs { get; set; } = 2;
    }

    public sealed class GcConfig
    {
        public bool Enabled { get; set; } = true;
        // Skip the forced periodic GC.Collect() in gmUpdate (every ~120 s).
        public bool SkipForcedCollect { get; set; } = true;
        // Safety net: collect anyway once the managed heap exceeds the ceiling.
        // SafetyCollectAboveMB is an absolute MB override; 0 = AUTO, derived from
        // host RAM (SafetyCollectRamFraction x SystemInfo.systemMemorySize). Auto
        // avoids the trap of a fixed ceiling below the real working heap (which
        // is 5-10 GB under load) that would fire every frame and defeat the guard.
        public int SafetyCollectAboveMB { get; set; } = 0;
        public float SafetyCollectRamFraction { get; set; } = 0.5f;
        // Opt-in: switch Boehm into incremental/generational mode so collection
        // happens in bounded increments across frames instead of one long STW.
        public bool Incremental { get; set; } = false;
        // 0 = no explicit limit; N = cap each incremental pause via GC_set_time_limit.
        public int IncrementalPauseTargetMs { get; set; } = 0;
    }

    public sealed class PathfindingConfig
    {
        // Run AstarManager.UpdateGraphs (player-following nav-graph maintenance,
        // the top managed section at load) every N ticks instead of every tick.
        // 1 = vanilla (run every tick, no throttle); >1 = throttle to (20/N) Hz.
        // It does NOT enable or disable pathfinding itself - path compute and the
        // scan drain always run. Named for exactly what it controls so it cannot be
        // misread as an on/off switch. Clamp ceiling is GovernorTiers.GraphUpdateMax,
        // the same cap the governor's doubled throttle uses.
        public int GraphUpdateEveryTicks { get; set; } = 4;

        // Rescan dead-zone in SQUARED grid units: a follow-graph is queued for a
        // rescan (InitScan, the #1 allocator) only after it drifts more than this
        // from the observer. 100 = vanilla. Larger = fewer rebuilds (less CPU and
        // allocation) at the cost of a slightly staler walkability window on fast
        // motion. Multiplies with GraphUpdateEveryTicks (cadence x per-visit rate).
        public float MoveRescanThresholdSq { get; set; } = 100f;

        // UNSAFE (default off): reuse the LayerGridGraph node array across scans
        // instead of `newarr LevelGridNode[]` every grid move (the #1 large-alloc /
        // megapause feeder). Grid dims are fixed, so the array size is constant;
        // Array.Clear makes a reused buffer identical to a fresh one. Concurrency is
        // safe (scans hold the A* work-item lock, no path worker reads mid-scan).
        // Transpiles an external-DLL iterator - fail-visibly if the IL drifts.
        public bool PoolInitScanNodes { get; set; } = false;

        // Path admission at EntityAlive.FindPath (A2). 0 = unlimited (vanilla).
        // Caps non-priority path enqueues per Unity frame; alerted / attack-target /
        // investigate / active-sleeper always admit and do not consume the budget.
        // Does not change path compute drain (still ~8 starts/frame stock).
        public int MaxPathEnqueuesPerTick { get; set; } = 0;

        // Drop non-priority FindPath when aiClosestPlayerDistSq >= this. 0 = off
        // (vanilla). Units are squared meters (same as AI LOD distance knobs).
        // Alerted entities never drop. Example: 2500 = 50 m.
        public float DropPathWhenFarDistSq { get; set; } = 0f;
    }

    // Each network lever is an independent toggle. FastSingleTargetSend ships ON
    // (provably equivalent to vanilla); EntityDistributionEveryTicks ships at
    // 1 = vanilla cadence.
    public sealed class NetworkConfig
    {
        // Bang-for-buck #1: single-target SendPackage resolves the recipient via the
        // O(1) entityId map (ClientInfoCollection.ForEntityId) instead of the linear
        // Clients scan. Only the pure single-target case is short-circuited; every
        // other filter mode falls through to vanilla. Provably equivalent (entityId
        // is unique, so vanilla also enqueues to exactly one client) - hence default
        // ON: perf win with zero gameplay impact.
        public bool FastSingleTargetSend { get; set; } = true;

        // Run the entity-replication pass (NetEntityDistribution.OnUpdateEntities)
        // every N ticks. 1 = vanilla (20 Hz). 2 = 10 Hz replication (+50 ms staleness;
        // clients interpolate) - halves one of the two O(N^2) player-axis walls.
        // State-driven scan, so skipped ticks delay (not lose) replication. Higher
        // strides trade visible rubber-banding for CPU; needs a human-eye fidelity
        // pass before production use.
        public int EntityDistributionEveryTicks { get; set; } = 1;
    }

    // Chunk/world transfer to joining clients (independent toggle).
    public sealed class WorldTransferConfig
    {
        // Max chunk packages ChunkManager.SendChunksToClients batches per observer per
        // tick (vanilla = 3). Each package is a synchronous Chunk.write encode on the
        // sim thread, so the per-tick cost is (observers x this). Lower it (1-2) to
        // spread a mass join transfer across more ticks - smaller per-tick spike, so
        // players already on the server hitch less when others connect, at the cost of
        // slightly slower per-client transfer. 3 = vanilla (no change).
        public int ChunkPackagesPerObserverPerTick { get; set; } = 3;
    }

    // Animator LOD: run calm, distant zombies' animation rigs at a reduced rate.
    // Measured prize: engine animator evaluation is ~20 ms/frame (28%) at ~380
    // endgame zombies on a headless server. Default OFF until the fidelity A/B and
    // a human visual pass clear it (root motion, attack cadence, and stuns read
    // animator state; the LOD preserves them with at most FarStride frames of lag,
    // and near/fighting/stunned/dead zombies always run full rate).
    public sealed class AnimatorLodConfig
    {
        public bool Enabled { get; set; } = false;
        // Full-rate band (squared meters): zombies closer than this to any player
        // always animate every frame. 400 = 20 m, matching the AI medium band.
        public float FullRateDistSq { get; set; } = 400f;
        // Far zombies evaluate every Nth frame via a manual Animator.Update pump
        // (delta-scaled, so motion aggregates correctly). 4 = 5 Hz at 20 fps.
        public int FarStride { get; set; } = 4;
    }

    // Crowd-collision LOD: stagger zombie entity-vs-entity collision QUERIES at
    // the broadphase (vanilla already staggers only the response). Movement/
    // collision integration is 54% of the per-zombie tick; the per-neighbor share
    // exists only in dense packs. Off-tick zombies still collide with the world
    // and are still soft-push separated. Default OFF pending the A/B.
    public sealed class CrowdCollisionLodConfig
    {
        public bool Enabled { get; set; } = false;
        // Each zombie fully resolves entity collision every Nth tick (striped by
        // entityId). 4 = vanilla's own response-stagger cadence family.
        public int ResolveEveryNTicks { get; set; } = 4;
    }

    // Server loop settings.
    public sealed class ServerConfig
    {
        // Target FRAME rate (persistent form of `settargetfps`). NOT the tick
        // rate: the full entity-sim/replication tick stays gated at ~20 Hz at any
        // fps (measured: TickEntities/OnUpdateEntities 19.9 calls/s at fps 20 and
        // 60 alike). Extra frames run housekeeping, work slices, and the network
        // pump more often - steadier delivery / lower jitter of the same 20 Hz
        // data, human-observed as slightly smoother motion. Modest per-frame CPU
        // cost. 0 = leave vanilla (default 20); 20-60 reasonable.
        public int TargetFps { get; set; } = 0;
        // Unity job-system worker thread count (0 = leave vanilla). Runtime-settable;
        // applied at game start and on `es reload`. Experimental: the saturated frame
        // is partly main-thread job-FENCE waiting (RESULTS 3o); worker-pool size is
        // the one untested variable in that equation. Sweep at saturation before
        // trusting any value.
        public int JobWorkerCount { get; set; } = 0;
    }

    // Adaptive load governor (default on): moves the proven throttle levers
    // (replication stride, graph-update cadence) between vanilla and throttled
    // based on the measured tick interval. Hysteresis via the OverBudgetMs /
    // HealthyMs gap + CooldownTicks. See GovernorPatch.
    public sealed class GovernorConfig
    {
        // Default ON: inert while the tick is healthy (zero gameplay impact), and
        // under sustained overload it trades minor replication staleness for a
        // running server - the regime where vanilla fidelity is already gone.
        public bool Enabled { get; set; } = true;
        // A healthy 20 TPS loop IDLES at exactly ~50 ms interval (it never goes
        // lower), so "healthy" must be a hair ABOVE 50, not below - the first live
        // test proved a sub-50 recovery threshold is unreachable and the governor
        // never stepped back down. 57/52 gives a 5 ms hysteresis band around the
        // 50 ms target.
        public float OverBudgetMs { get; set; } = 57f;
        public float HealthyMs { get; set; } = 52f;
        // Tier 2 (opt-in, gameplay-affecting): when throttling has not recovered the
        // tick and the EMA is past this, put all zombie animators into CullCompletely
        // (v1.17.0+; keeps enabled so root-motion can restore). Measured ~40% of the
        // saturated 64-player frame (RESULTS 3o). Combat timing degrades
        // (timer-only attack cadence, no stagger) but nothing despawns and clients
        // see no visual change. Steps back down through tier 1 on recovery.
        // Default false until human es animstate dp check clears exit.
        public bool AnimatorEmergency { get; set; } = false;
        public float EmergencyOverMs { get; set; } = 80f;
        // Ticks the EMA must stay over/under before a transition (~5 s at 20 TPS).
        public int WindowTicks { get; set; } = 100;
        // Minimum ticks between transitions (~20 s at 20 TPS).
        public int CooldownTicks { get; set; } = 400;
    }

    // Emergency load-shedding (default off: it REMOVES entities, a real gameplay
    // impact). Fires only when the tick is collapsing past what the governor's
    // throttles can fix; sheds the farthest-from-any-player enemies in batches via
    // the game's silent despawn. See TickGuardPatch.
    public sealed class TickGuardConfig
    {
        public bool Enabled { get; set; } = false;
        // Well above the governor's OverBudgetMs: shedding is the last resort.
        public float ShedAboveMs { get; set; } = 70f;
        public int WindowTicks { get; set; } = 60;
        public int ShedBatch { get; set; } = 15;
        public int CooldownTicks { get; set; } = 100;
        // Never shed below this many living enemies (keeps the horde a horde).
        public int MinEnemiesKept { get; set; } = 60;
    }

    // DIAGNOSTIC ONLY (default off). Not a performance feature.
    public sealed class DiagnosticsConfig
    {
        // Disable Boehm, grow the heap under load, then time one forced full collect
        // to measure the "megapause" freeze. Never enable on a live server.
        public bool GcMegapauseTest { get; set; } = false;
        public int WarmupSeconds { get; set; } = 60;
        public int GrowSeconds { get; set; } = 240;
    }

    public sealed class ServerPerfConfig
    {
        // Feature keys name one patch group to the init log and gate its activity.
        // Single source of truth for the ModApi <-> config vocabulary: ModApi.ConfigNote
        // maps patch types to these constants and FeatureActive switches on them, so
        // the two sides cannot drift by typo. A new patch group adds one constant plus
        // one entry in each place; not serialized to JSON (internal vocabulary only).
        public const string KeyAiLod = "AiLod";
        public const string KeyGc = "Gc";
        public const string KeyGraphThrottle = "GraphThrottle";
        public const string KeyMoveThreshold = "MoveThreshold";
        public const string KeyPathAdmission = "PathAdmission";
        public const string KeyFastSend = "FastSend";
        public const string KeyInitScanPool = "InitScanPool";
        public const string KeyChunkSendThrottle = "ChunkSendThrottle";
        public const string KeyExplosionParticles = "ExplosionParticles";
        public const string KeyEntityDistributionStride = "EntityDistributionStride";
        public const string KeyGovernor = "Governor";
        public const string KeyTickGuard = "TickGuard";
        public const string KeyTargetFps = "TargetFps";
        public const string KeyBenchGod = "BenchGod";
        public const string KeyCrowdCollisionLod = "CrowdCollisionLod";
        public const string KeyAnimatorLod = "AnimatorLod";

        public bool Enabled { get; set; } = true;
        public bool DedicatedOnly { get; set; } = true;
        public AiLodConfig AiLod { get; set; } = new AiLodConfig();
        public SkipConfig SkipOnDedicated { get; set; } = new SkipConfig();
        public DynamicMeshConfig DynamicMesh { get; set; } = new DynamicMeshConfig();
        public GcConfig Gc { get; set; } = new GcConfig();
        public PathfindingConfig Pathfinding { get; set; } = new PathfindingConfig();
        public NetworkConfig Network { get; set; } = new NetworkConfig();
        public WorldTransferConfig WorldTransfer { get; set; } = new WorldTransferConfig();
        public ServerConfig Server { get; set; } = new ServerConfig();
        public AnimatorLodConfig AnimatorLod { get; set; } = new AnimatorLodConfig();
        public CrowdCollisionLodConfig CrowdCollisionLod { get; set; } = new CrowdCollisionLodConfig();
        public GovernorConfig Governor { get; set; } = new GovernorConfig();
        public TickGuardConfig TickGuard { get; set; } = new TickGuardConfig();
        public DiagnosticsConfig Diagnostics { get; set; } = new DiagnosticsConfig();

        public static ServerPerfConfig Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return new ServerPerfConfig();

            try
            {
                string json = File.ReadAllText(path);
                // A misspelled key binds to nothing and silently keeps the built-in
                // default, so name every ignored key at load (fail-soft per group;
                // unknown keys are still ignored, just not silently).
                foreach (string key in FindUnknownKeys(json))
                    ModApi.Warn("config unknown key '" + key + "' ignored (no such option; check spelling)");
                var loaded = JsonConvert.DeserializeObject<ServerPerfConfig>(json);
                if (loaded == null) return new ServerPerfConfig();
                if (loaded.AiLod == null) loaded.AiLod = new AiLodConfig();
                if (loaded.SkipOnDedicated == null) loaded.SkipOnDedicated = new SkipConfig();
                if (loaded.DynamicMesh == null) loaded.DynamicMesh = new DynamicMeshConfig();
                if (loaded.Gc == null) loaded.Gc = new GcConfig();
                if (loaded.Pathfinding == null) loaded.Pathfinding = new PathfindingConfig();
                if (loaded.Network == null) loaded.Network = new NetworkConfig();
                if (loaded.WorldTransfer == null) loaded.WorldTransfer = new WorldTransferConfig();
                if (loaded.Server == null) loaded.Server = new ServerConfig();
                if (loaded.AnimatorLod == null) loaded.AnimatorLod = new AnimatorLodConfig();
                if (loaded.CrowdCollisionLod == null) loaded.CrowdCollisionLod = new CrowdCollisionLodConfig();
                if (loaded.Governor == null) loaded.Governor = new GovernorConfig();
                if (loaded.TickGuard == null) loaded.TickGuard = new TickGuardConfig();
                if (loaded.Diagnostics == null) loaded.Diagnostics = new DiagnosticsConfig();
                loaded.Normalize();
                return loaded;
            }
            catch (Exception ex)
            {
                // Type name + message: a parse error names its JSON line in Message,
                // and the type separates syntax errors from IO failures.
                ModApi.Warn("Config load failed [" + ex.GetType().Name + "], using defaults: " + ex.Message);
                return new ServerPerfConfig();
            }
        }

        /// <summary>
        /// Dotted paths of JSON keys that match no config property (typo guard).
        /// Mirrors Newtonsoft's case-insensitive property binding, so a case variant
        /// of a real key is NOT reported (it binds). Never throws: malformed or
        /// non-object JSON yields an empty list and Load reports the parse error.
        /// </summary>
        public static List<string> FindUnknownKeys(string json)
        {
            var unknown = new List<string>();
            if (string.IsNullOrEmpty(json)) return unknown;
            JObject root;
            try { root = JObject.Parse(json); }
            catch { return unknown; }
            CollectUnknown(root, typeof(ServerPerfConfig), "", unknown);
            return unknown;
        }

        static void CollectUnknown(JObject obj, Type schema, string prefix, List<string> unknown)
        {
            foreach (JProperty prop in obj.Properties())
            {
                // var (not PropertyInfo): GetProperty is annotated nullable, so
                // an explicit type fails CS8600 under the tests project's
                // nullable context; var infers PropertyInfo? in both projects.
                var known = schema.GetProperty(prop.Name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (known == null)
                {
                    unknown.Add(prefix.Length == 0 ? prop.Name : prefix + "." + prop.Name);
                    continue;
                }
                Type t = known.PropertyType;
                bool nestedConfig = t.IsClass && t != typeof(string) && t.Namespace == typeof(ServerPerfConfig).Namespace;
                if (nestedConfig && prop.Value.Type == JTokenType.Object)
                    CollectUnknown((JObject)prop.Value, t, prefix.Length == 0 ? prop.Name : prefix + "." + prop.Name, unknown);
            }
        }

        public void Normalize()
        {
            AiLod.FullAiDistSq = FiniteRange("AiLod.FullAiDistSq", AiLod.FullAiDistSq, 1f, 1000000f, 100f);
            AiLod.MediumAiDistSq = FiniteRange("AiLod.MediumAiDistSq", AiLod.MediumAiDistSq, AiLod.FullAiDistSq, 1000000f, 400f);
            AiLod.SkipTasksFarDistSq = FiniteRange("AiLod.SkipTasksFarDistSq", AiLod.SkipTasksFarDistSq, AiLod.MediumAiDistSq, 4000000f, 2500f);
            AiLod.MidTickStride = IntRange("AiLod.MidTickStride", AiLod.MidTickStride, 1, 20);
            AiLod.FullScale = FiniteRange("AiLod.FullScale", AiLod.FullScale, 0f, 1f, 1f);
            AiLod.MediumScale = FiniteRange("AiLod.MediumScale", AiLod.MediumScale, 0f, AiLod.FullScale, 0.2f);
            AiLod.FarScale = FiniteRange("AiLod.FarScale", AiLod.FarScale, 0f, AiLod.MediumScale, 0.05f);
            DynamicMesh.PlayerAreaChunkBuffer = IntRange("DynamicMesh.PlayerAreaChunkBuffer", DynamicMesh.PlayerAreaChunkBuffer, 0, 64);
            DynamicMesh.MaxRegionLoadMsPerFrame = IntRange("DynamicMesh.MaxRegionLoadMsPerFrame", DynamicMesh.MaxRegionLoadMsPerFrame, 1, 1000);
            DynamicMesh.MaxActiveSyncs = IntRange("DynamicMesh.MaxActiveSyncs", DynamicMesh.MaxActiveSyncs, 1, 128);
            // 1 = vanilla; cap at GovernorTiers.GraphUpdateMax (~0.1 Hz) so a
            // fat-finger like 1e6 (nav graphs repositioning once per ~14 h) is
            // clamped and logged, not silently accepted. A legitimate low-pop tune
            // (e.g. 40) still passes.
            Pathfinding.GraphUpdateEveryTicks = IntRange("Pathfinding.GraphUpdateEveryTicks", Pathfinding.GraphUpdateEveryTicks, 1, GovernorTiers.GraphUpdateMax);
            Pathfinding.MoveRescanThresholdSq = FiniteRange("Pathfinding.MoveRescanThresholdSq", Pathfinding.MoveRescanThresholdSq, 100f, 10000f, 100f);
            // 0 = unlimited / off. Cap admits high enough for a full BM wave of
            // non-priority wander requests without clipping combat (combat bypasses).
            Pathfinding.MaxPathEnqueuesPerTick = IntRange("Pathfinding.MaxPathEnqueuesPerTick", Pathfinding.MaxPathEnqueuesPerTick, 0, 2000);
            Pathfinding.DropPathWhenFarDistSq = FiniteRange("Pathfinding.DropPathWhenFarDistSq", Pathfinding.DropPathWhenFarDistSq, 0f, 4000000f, 0f);
            // 3 = vanilla batch; floor 1 (never stall the transfer entirely), cap 32
            // (a generous ceiling; above vanilla speeds transfer at a bigger per-tick
            // spike). A fat-finger 0 or negative would deadlock the send loop, so the
            // floor of 1 is a correctness guard, not just a tuning bound.
            WorldTransfer.ChunkPackagesPerObserverPerTick = IntRange("WorldTransfer.ChunkPackagesPerObserverPerTick", WorldTransfer.ChunkPackagesPerObserverPerTick, 1, 32);
            // 4 = 5 Hz replication, already aggressive; anything higher is unplayable.
            // Same ceiling the governor's doubled throttle uses (GovernorTiers).
            Network.EntityDistributionEveryTicks = IntRange("Network.EntityDistributionEveryTicks", Network.EntityDistributionEveryTicks, 1, GovernorTiers.EntityStrideMax);
            CrowdCollisionLod.ResolveEveryNTicks = IntRange("CrowdCollisionLod.ResolveEveryNTicks", CrowdCollisionLod.ResolveEveryNTicks, 1, 16);
            AnimatorLod.FullRateDistSq = FiniteRange("AnimatorLod.FullRateDistSq", AnimatorLod.FullRateDistSq, 100f, 1000000f, 400f);
            AnimatorLod.FarStride = IntRange("AnimatorLod.FarStride", AnimatorLod.FarStride, 1, 10);
            // Server.TargetFps: 0 = leave vanilla; cap 120 (beyond is pure waste).
            Server.TargetFps = IntRange("Server.TargetFps", Server.TargetFps, 0, 120);
            Server.JobWorkerCount = IntRange("Server.JobWorkerCount", Server.JobWorkerCount, 0, 64);
            // Governor thresholds are TICK-INTERVAL milliseconds; the tick rate equals
            // the target frame rate, so calibrate to it: HealthyMs must sit ABOVE the
            // idle frame time (50 ms at fps 20, 25 at 40, 16.7 at 60) or recovery
            // never triggers. Defaults assume the vanilla fps 20. Clamps are wide
            // enough for high-fps tunes; the hysteresis gap is still enforced.
            Governor.OverBudgetMs = FiniteRange("Governor.OverBudgetMs", Governor.OverBudgetMs, 20f, 500f, 57f);
            Governor.HealthyMs = FiniteRange("Governor.HealthyMs", Governor.HealthyMs, 10f, Governor.OverBudgetMs - 5f, 52f);
            Governor.EmergencyOverMs = FiniteRange("Governor.EmergencyOverMs", Governor.EmergencyOverMs, Governor.OverBudgetMs + 5f, 1000f, 80f);
            Governor.WindowTicks = IntRange("Governor.WindowTicks", Governor.WindowTicks, 20, 6000);
            Governor.CooldownTicks = IntRange("Governor.CooldownTicks", Governor.CooldownTicks, 0, 36000);
            // TickGuard: shed threshold must sit above the governor band (last resort,
            // hence the dynamic floor over the already-normalized OverBudgetMs), batch
            // and keep-floor bounded so a bad config cannot wipe the horde.
            TickGuard.ShedAboveMs = FiniteRange("TickGuard.ShedAboveMs", TickGuard.ShedAboveMs,
                Math.Max(60f, Governor.OverBudgetMs + 5f), 1000f, 70f);
            TickGuard.WindowTicks = IntRange("TickGuard.WindowTicks", TickGuard.WindowTicks, 20, 6000);
            TickGuard.ShedBatch = IntRange("TickGuard.ShedBatch", TickGuard.ShedBatch, 1, 100);
            TickGuard.CooldownTicks = IntRange("TickGuard.CooldownTicks", TickGuard.CooldownTicks, 20, 36000);
            TickGuard.MinEnemiesKept = IntRange("TickGuard.MinEnemiesKept", TickGuard.MinEnemiesKept, 0, 10000);
            // Gc knobs keep their 0-sentinels (SafetyCollectAboveMB 0 = AUTO ceiling;
            // IncrementalPauseTargetMs 0 = no pause limit), so the floor is 0, not a
            // forced positive. This centralizes the previously ad-hoc use-site clamps
            // and adds the "config corrected" log they lacked.
            Gc.SafetyCollectAboveMB = IntRange("Gc.SafetyCollectAboveMB", Gc.SafetyCollectAboveMB, 0, 1048576);
            Gc.SafetyCollectRamFraction = FiniteRange("Gc.SafetyCollectRamFraction", Gc.SafetyCollectRamFraction, 0f, 0.95f, 0.5f);
            Gc.IncrementalPauseTargetMs = IntRange("Gc.IncrementalPauseTargetMs", Gc.IncrementalPauseTargetMs, 0, 10000);
            // Diagnostics seconds feed Thread.Sleep(WarmupSeconds * 1000) and bound the
            // grow loop, so they must be clamped like every other knob: an unclamped
            // fat-finger above ~2.1M makes `seconds * 1000` wrap negative (Sleep throws,
            // probe dies with a misleading log), and a huge GrowSeconds runs the grow
            // loop for months. Caps: 1 h warmup, 2 h grow.
            Diagnostics.WarmupSeconds = IntRange("Diagnostics.WarmupSeconds", Diagnostics.WarmupSeconds, 0, 3600);
            Diagnostics.GrowSeconds = IntRange("Diagnostics.GrowSeconds", Diagnostics.GrowSeconds, 1, 7200);
        }

        static float FiniteRange(string name, float value, float min, float max, float fallback)
        {
            // NaN/Inf take the fallback, and the fallback itself is clamped: ranges
            // can shift with sibling knobs (e.g. HealthyMs max = OverBudgetMs - 5), so
            // an unclamped fallback could re-violate the invariant Normalize enforces.
            float chosen = float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
            float normalized = Math.Max(min, Math.Min(max, chosen));
            if (normalized != value)
                ModApi.Warn("config corrected " + name + ": " + value + " -> " + normalized);
            return normalized;
        }

        static int IntRange(string name, int value, int min, int max)
        {
            int normalized = Math.Max(min, Math.Min(max, value));
            if (normalized != value)
                ModApi.Warn("config corrected " + name + ": " + value + " -> " + normalized);
            return normalized;
        }

        public static string DefaultPathBesideAssembly()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            string a = Path.Combine(dir, "Config", "efficientserver.json");
            if (File.Exists(a)) return a;
            return Path.Combine(dir, "efficientserver.json");
        }

        /// <summary>
        /// Pure dedicated-only gate decision (no game types), so the policy is
        /// unit-testable: disabled config never runs; DedicatedOnly requires a
        /// confirmed dedicated host; an unknown host fails closed (false).
        /// </summary>
        public static bool ShouldRunFor(bool active, bool enabled, bool dedicatedOnly, bool? isDedicatedServer)
        {
            if (!active || !enabled)
                return false;
            if (!dedicatedOnly)
                return true;
            // Fail closed: DedicatedOnly means "only on a confirmed dedicated
            // server", so an unknown host must not activate server-only patches.
            return isDedicatedServer ?? false;
        }

        /// <summary>
        /// Pure per-feature config gating (no game types): whether a patch group is
        /// actually active given this config, not merely IL-matched. Keys are the
        /// Key* constants above, shared with ModApi.ConfigNote; `KeyBenchGod` is the
        /// console-toggled diagnostic flag.
        /// </summary>
        public bool FeatureActive(string featureKey, bool benchGod = false)
        {
            switch (featureKey)
            {
                case KeyAiLod:
                    return AiLod != null && AiLod.Enabled;
                case KeyGc:
                    return Gc != null && Gc.Enabled && Gc.SkipForcedCollect;
                case KeyGraphThrottle:
                    return Pathfinding != null && Pathfinding.GraphUpdateEveryTicks > 1;
                case KeyMoveThreshold:
                    return Pathfinding != null && Pathfinding.MoveRescanThresholdSq > 100f;
                case KeyPathAdmission:
                    return Pathfinding != null
                        && (Pathfinding.MaxPathEnqueuesPerTick > 0 || Pathfinding.DropPathWhenFarDistSq > 0f);
                case KeyFastSend:
                    return Network != null && Network.FastSingleTargetSend;
                case KeyInitScanPool:
                    return Pathfinding != null && Pathfinding.PoolInitScanNodes;
                case KeyChunkSendThrottle:
                    return WorldTransfer != null && WorldTransfer.ChunkPackagesPerObserverPerTick != 3;
                case KeyExplosionParticles:
                    return SkipOnDedicated != null && SkipOnDedicated.ExplosionParticles;
                case KeyEntityDistributionStride:
                    return Network != null && Network.EntityDistributionEveryTicks > 1;
                case KeyGovernor:
                    return Governor != null && Governor.Enabled;
                case KeyTickGuard:
                    return TickGuard != null && TickGuard.Enabled;
                case KeyTargetFps:
                    return Server != null && Server.TargetFps > 0;
                case KeyBenchGod:
                    return benchGod;
                case KeyCrowdCollisionLod:
                    return CrowdCollisionLod != null && CrowdCollisionLod.Enabled;
                case KeyAnimatorLod:
                    return AnimatorLod != null && AnimatorLod.Enabled;
                default:
                    return false;
            }
        }
    }
}
