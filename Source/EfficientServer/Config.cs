using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

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

    public sealed class SkipConfig
    {
        public bool DynamicMusic { get; set; } = true;
        public bool WaterSplash { get; set; } = true;
        public bool EnvironmentAudio { get; set; } = true;
        public bool ClothAndJiggle { get; set; } = true;
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
        // misread as an on/off switch.
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
    }

    // Each network lever is an independent toggle (default off = vanilla).
    public sealed class NetworkConfig
    {
        // Bang-for-buck #1: single-target SendPackage resolves the recipient via the
        // O(1) entityId map (ClientInfoCollection.ForEntityId) instead of the linear
        // Clients scan. Only the pure single-target case is short-circuited; every
        // other filter mode falls through to vanilla. Provably equivalent (entityId
        // is unique, so vanilla also enqueues to exactly one client).
        public bool FastSingleTargetSend { get; set; } = false;
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
        public bool Enabled { get; set; } = true;
        public bool DedicatedOnly { get; set; } = true;
        public AiLodConfig AiLod { get; set; } = new AiLodConfig();
        public SkipConfig SkipOnDedicated { get; set; } = new SkipConfig();
        public DynamicMeshConfig DynamicMesh { get; set; } = new DynamicMeshConfig();
        public GcConfig Gc { get; set; } = new GcConfig();
        public PathfindingConfig Pathfinding { get; set; } = new PathfindingConfig();
        public NetworkConfig Network { get; set; } = new NetworkConfig();
        public WorldTransferConfig WorldTransfer { get; set; } = new WorldTransferConfig();
        public DiagnosticsConfig Diagnostics { get; set; } = new DiagnosticsConfig();

        public static ServerPerfConfig Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return new ServerPerfConfig();

            try
            {
                string json = File.ReadAllText(path);
                var loaded = JsonConvert.DeserializeObject<ServerPerfConfig>(json);
                if (loaded == null) return new ServerPerfConfig();
                if (loaded.AiLod == null) loaded.AiLod = new AiLodConfig();
                if (loaded.SkipOnDedicated == null) loaded.SkipOnDedicated = new SkipConfig();
                if (loaded.DynamicMesh == null) loaded.DynamicMesh = new DynamicMeshConfig();
                if (loaded.Gc == null) loaded.Gc = new GcConfig();
                if (loaded.Pathfinding == null) loaded.Pathfinding = new PathfindingConfig();
                if (loaded.Network == null) loaded.Network = new NetworkConfig();
                if (loaded.WorldTransfer == null) loaded.WorldTransfer = new WorldTransferConfig();
                if (loaded.Diagnostics == null) loaded.Diagnostics = new DiagnosticsConfig();
                loaded.Normalize();
                return loaded;
            }
            catch (Exception ex)
            {
                ModApi.Log("Config load failed, using defaults: " + ex.Message);
                return new ServerPerfConfig();
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
            // 1 = vanilla; cap generously (200 = ~0.1 Hz) so a fat-finger like 1e6
            // (nav graphs repositioning once per ~14 h) is clamped and logged, not
            // silently accepted. A legitimate low-pop tune (e.g. 40) still passes.
            Pathfinding.GraphUpdateEveryTicks = IntRange("Pathfinding.GraphUpdateEveryTicks", Pathfinding.GraphUpdateEveryTicks, 1, 200);
            Pathfinding.MoveRescanThresholdSq = FiniteRange("Pathfinding.MoveRescanThresholdSq", Pathfinding.MoveRescanThresholdSq, 100f, 10000f, 100f);
            // 3 = vanilla batch; floor 1 (never stall the transfer entirely), cap 32
            // (a generous ceiling; above vanilla speeds transfer at a bigger per-tick
            // spike). A fat-finger 0 or negative would deadlock the send loop, so the
            // floor of 1 is a correctness guard, not just a tuning bound.
            WorldTransfer.ChunkPackagesPerObserverPerTick = IntRange("WorldTransfer.ChunkPackagesPerObserverPerTick", WorldTransfer.ChunkPackagesPerObserverPerTick, 1, 32);
            // Gc knobs keep their 0-sentinels (SafetyCollectAboveMB 0 = AUTO ceiling;
            // IncrementalPauseTargetMs 0 = no pause limit), so the floor is 0, not a
            // forced positive. This centralizes the previously ad-hoc use-site clamps
            // and adds the "config corrected" log they lacked.
            Gc.SafetyCollectAboveMB = IntRange("Gc.SafetyCollectAboveMB", Gc.SafetyCollectAboveMB, 0, 1048576);
            Gc.SafetyCollectRamFraction = FiniteRange("Gc.SafetyCollectRamFraction", Gc.SafetyCollectRamFraction, 0f, 0.95f, 0.5f);
            Gc.IncrementalPauseTargetMs = IntRange("Gc.IncrementalPauseTargetMs", Gc.IncrementalPauseTargetMs, 0, 10000);
        }

        static float FiniteRange(string name, float value, float min, float max, float fallback)
        {
            float normalized = float.IsNaN(value) || float.IsInfinity(value) ? fallback : Math.Max(min, Math.Min(max, value));
            if (normalized != value) ModApi.Log("config corrected " + name + ": " + value + " -> " + normalized);
            return normalized;
        }

        static int IntRange(string name, int value, int min, int max)
        {
            int normalized = Math.Max(min, Math.Min(max, value));
            if (normalized != value) ModApi.Log("config corrected " + name + ": " + value + " -> " + normalized);
            return normalized;
        }

        public static string DefaultPathBesideAssembly()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            string a = Path.Combine(dir, "Config", "efficientserver.json");
            if (File.Exists(a)) return a;
            return Path.Combine(dir, "efficientserver.json");
        }
    }
}
