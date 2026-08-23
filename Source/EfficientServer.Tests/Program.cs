using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// Stub the only external symbols Config.cs touches (game-type-free), so the real
// Config source compiles and runs under the plain .NET SDK. Warnings are recorded
// so tests can pin which channel each config problem is reported on.
namespace EfficientServer
{
    internal static class ModApi
    {
        public static readonly List<string> Warnings = new List<string>();

        public static void Log(string msg) { /* swallow in tests */ }

        public static void Warn(string msg) { Warnings.Add(msg); }
    }
}

namespace EfficientServer.Tests
{
    internal static class Program
    {
        static int _failures;

        static void Check(bool cond, string what)
        {
            if (cond) return;
            _failures++;
            Console.WriteLine("FAIL: " + what);
        }

        static string WriteTemp(string json)
        {
            string p = Path.Combine(Path.GetTempPath(), "es_cfg_" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(p, json);
            return p;
        }

        // Load from a scratch JSON blob and remove the file before returning:
        // Load reads it synchronously, so nothing needs it afterwards. Without
        // this every run leaks ~500 fuzz + ~40 fixture files into the temp dir.
        static ServerPerfConfig LoadTemp(string json)
        {
            string p = WriteTemp(json);
            try { return ServerPerfConfig.Load(p); }
            finally { File.Delete(p); }
        }

        // Same scratch contract as WriteTemp, but the bytes land exactly as given
        // (BOM included), so encoding-boundary behavior is pinned independent of
        // any default-encoding choice in the write path.
        static string WriteTempBytes(byte[] bytes)
        {
            string p = Path.Combine(Path.GetTempPath(), "es_cfg_" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllBytes(p, bytes);
            return p;
        }

        static ServerPerfConfig LoadTempFile(string p)
        {
            try { return ServerPerfConfig.Load(p); }
            finally { File.Delete(p); }
        }

        // Load with a clean warning sink so channel assertions see only this file.
        static ServerPerfConfig LoadTempTracked(string json)
        {
            ModApi.Warnings.Clear();
            return LoadTemp(json);
        }

        static int Main()
        {
            // Defaults.
            var d = new ServerPerfConfig();
            Check(d.Pathfinding.GraphUpdateEveryTicks == 4, "default GraphUpdateEveryTicks=4");
            Check(d.Pathfinding.MoveRescanThresholdSq == 100f, "default MoveRescanThresholdSq=100");
            Check(d.Pathfinding.MaxPathEnqueuesPerTick == 0, "default MaxPathEnqueuesPerTick=0 (unlimited)");
            Check(d.Pathfinding.DropPathWhenFarDistSq == 0f, "default DropPathWhenFarDistSq=0 (off)");
            Check(Math.Abs(d.Gc.SafetyCollectRamFraction - 0.5f) < 1e-6, "default RamFraction=0.5");
            Check(d.Gc.SafetyCollectAboveMB == 0, "default SafetyCollectAboveMB=0 (AUTO)");

            // Missing file -> defaults, no throw.
            var miss = ServerPerfConfig.Load(Path.Combine(Path.GetTempPath(), "does_not_exist_" + Guid.NewGuid().ToString("N") + ".json"));
            Check(miss != null && miss.Pathfinding.GraphUpdateEveryTicks == 4, "missing file -> defaults");

            // Malformed JSON -> defaults, no throw.
            var bad = LoadTempTracked("{ this is not json ][");
            Check(bad != null && bad.Enabled, "malformed json -> defaults");
            // The failure must surface on the WARNING channel (operators grep the
            // dedicated log for WARNING/ERROR; info-level config failures vanish).
            Check(ModApi.Warnings.Count == 1 && ModApi.Warnings[0].StartsWith("Config load failed ["),
                "malformed json -> one WARNING naming the exception type");

            // Non-object value for a section key: deserialization throws ->
            // Load fails soft with full defaults.
            var secBad = LoadTemp("{\"AiLod\":5}");
            Check(secBad != null && secBad.Enabled && secBad.Pathfinding.GraphUpdateEveryTicks == 4,
                "section value of wrong type -> full defaults");

            // Empty object -> defaults filled.
            var empty = LoadTemp("{}");
            Check(empty != null && empty.Pathfinding != null && empty.Gc != null, "empty object -> sub-configs filled");

            // Valid round-trip.
            var ok = LoadTemp("{\"Pathfinding\":{\"GraphUpdateEveryTicks\":8,\"MoveRescanThresholdSq\":400}}");
            Check(ok.Pathfinding.GraphUpdateEveryTicks == 8, "round-trip GraphUpdateEveryTicks=8");
            Check(ok.Pathfinding.MoveRescanThresholdSq == 400f, "round-trip MoveRescanThresholdSq=400");

            // Unknown-key guard: a misspelled knob must be named at load instead of
            // silently keeping its default (Newtonsoft binds case-insensitively, so
            // case variants of real keys are NOT unknown - they bind).
            Check(ServerPerfConfig.FindUnknownKeys("{\"Pathfinding\":{\"GraphUpdateEveryTicks\":4}}").Count == 0,
                "FindUnknownKeys: valid keys -> none");
            Check(ServerPerfConfig.FindUnknownKeys("{\"notAKey\":1}")[0] == "notAKey",
                "FindUnknownKeys: top-level typo reported");
            var unk = ServerPerfConfig.FindUnknownKeys("{\"Pathfinding\":{\"GraphUpdateEveryTick\":8}}");
            Check(unk.Count == 1 && unk[0] == "Pathfinding.GraphUpdateEveryTick",
                "FindUnknownKeys: nested typo reported with dotted path");
            Check(ServerPerfConfig.FindUnknownKeys("{\"AiLod\":{\"ENABLED\":true}}").Count == 0,
                "FindUnknownKeys: case-variant of real key binds, not reported");
            Check(ServerPerfConfig.FindUnknownKeys("{ this is not json ][").Count == 0,
                "FindUnknownKeys: malformed json -> empty, no throw");
            Check(ServerPerfConfig.FindUnknownKeys("").Count == 0,
                "FindUnknownKeys: empty input -> empty");
            var typo = LoadTempTracked("{\"Pathfinding\":{\"GraphUpdateEveryTick\":8}}");
            Check(typo != null && typo.Pathfinding.GraphUpdateEveryTicks == 4,
                "typo'd knob keeps default (and is logged), other fields unaffected");
            Check(ModApi.Warnings.Any(w => w.Contains("unknown key 'Pathfinding.GraphUpdateEveryTick'")),
                "typo'd knob -> WARNING names the dotted key");
            var caseBind = LoadTemp("{\"ailod\":{\"enabled\":false}}");
            Check(caseBind.AiLod.Enabled == false,
                "case-variant key binds like Newtonsoft (value applied)");

            // Normalize: GraphUpdateEveryTicks clamps [1,200].
            var big = LoadTempTracked("{\"Pathfinding\":{\"GraphUpdateEveryTicks\":1000000}}");
            Check(big.Pathfinding.GraphUpdateEveryTicks == 200, "GraphUpdateEveryTicks 1e6 -> 200");
            Check(ModApi.Warnings.Any(w => w.StartsWith("config corrected Pathfinding.GraphUpdateEveryTicks")
                && w.Contains("1000000") && w.Contains("200")),
                "out-of-range knob -> WARNING records old and corrected value");
            var neg = LoadTemp("{\"Pathfinding\":{\"GraphUpdateEveryTicks\":-5}}");
            Check(neg.Pathfinding.GraphUpdateEveryTicks == 1, "GraphUpdateEveryTicks -5 -> 1");

            // Normalize: MoveRescanThresholdSq clamps [100,10000].
            var lowThr = LoadTemp("{\"Pathfinding\":{\"MoveRescanThresholdSq\":5}}");
            Check(lowThr.Pathfinding.MoveRescanThresholdSq == 100f, "MoveRescanThresholdSq 5 -> 100");
            var hiThr = LoadTemp("{\"Pathfinding\":{\"MoveRescanThresholdSq\":999999}}");
            Check(hiThr.Pathfinding.MoveRescanThresholdSq == 10000f, "MoveRescanThresholdSq 999999 -> 10000");
            var pathCap = LoadTemp("{\"Pathfinding\":{\"MaxPathEnqueuesPerTick\":99999,\"DropPathWhenFarDistSq\":-1}}");
            Check(pathCap.Pathfinding.MaxPathEnqueuesPerTick == 2000, "MaxPathEnqueuesPerTick 99999 -> 2000");
            Check(pathCap.Pathfinding.DropPathWhenFarDistSq == 0f, "DropPathWhenFarDistSq -1 -> 0");
            var pathOk = LoadTemp("{\"Pathfinding\":{\"MaxPathEnqueuesPerTick\":64,\"DropPathWhenFarDistSq\":2500}}");
            Check(pathOk.Pathfinding.MaxPathEnqueuesPerTick == 64, "MaxPathEnqueuesPerTick round-trip 64");
            Check(pathOk.Pathfinding.DropPathWhenFarDistSq == 2500f, "DropPathWhenFarDistSq round-trip 2500");

            // Normalize: NaN/Inf fall back. IsNaN alone would also pass a leaked
            // Infinity, so assert finite AND inside the [1,1e6] clamp.
            var nan = LoadTemp("{\"AiLod\":{\"FullAiDistSq\":\"NaN\"}}");
            var nanV = nan.AiLod.FullAiDistSq;
            Check(!float.IsNaN(nanV) && !float.IsInfinity(nanV) && nanV >= 1f && nanV <= 1000000f,
                "NaN FullAiDistSq -> finite fallback inside [1,1e6] clamp");

            // Normalize: inverted Medium>Full scale clamps (Medium <= Full).
            var inv = LoadTemp("{\"AiLod\":{\"FullScale\":0.3,\"MediumScale\":0.9}}");
            Check(inv.AiLod.MediumScale == inv.AiLod.FullScale,
                "MediumScale 0.9 clamped down exactly to FullScale 0.3");

            // Correctness invariant: the AiLod bands are monotonically nested and
            // the scales monotonically decreasing, so a loaded config can never
            // produce a broken band ordering (full inside medium inside far).
            var bands = LoadTemp(
                "{\"AiLod\":{\"FullAiDistSq\":999999,\"MediumAiDistSq\":0.1,\"SkipTasksFarDistSq\":0.05," +
                "\"FullScale\":0.0,\"MediumScale\":1.0,\"FarScale\":0.9}}");
            Check(bands.AiLod.FullAiDistSq <= bands.AiLod.MediumAiDistSq,
                "band invariant: FullAiDistSq <= MediumAiDistSq after normalize");
            Check(bands.AiLod.MediumAiDistSq <= bands.AiLod.SkipTasksFarDistSq,
                "band invariant: MediumAiDistSq <= SkipTasksFarDistSq after normalize");
            Check(bands.AiLod.FullScale >= bands.AiLod.MediumScale,
                "scale invariant: FullScale >= MediumScale after normalize");
            Check(bands.AiLod.MediumScale >= bands.AiLod.FarScale,
                "scale invariant: MediumScale >= FarScale after normalize");
            var bandsOk = LoadTemp(
                "{\"AiLod\":{\"FullAiDistSq\":50,\"MediumAiDistSq\":200,\"SkipTasksFarDistSq\":900," +
                "\"FullScale\":1.0,\"MediumScale\":0.4,\"FarScale\":0.1}}");
            Check(bandsOk.AiLod.FullAiDistSq == 50f && bandsOk.AiLod.MediumAiDistSq == 200f
                && bandsOk.AiLod.SkipTasksFarDistSq == 900f,
                "band round-trip: valid nested distances preserved");
            Check(bandsOk.AiLod.FullScale == 1f && bandsOk.AiLod.MediumScale == 0.4f
                && bandsOk.AiLod.FarScale == 0.1f,
                "scale round-trip: valid decreasing scales preserved");

            // Normalize: Gc 0-sentinels preserved; garbage clamped.
            var gcSent = LoadTemp("{\"Gc\":{\"SafetyCollectAboveMB\":0,\"IncrementalPauseTargetMs\":0}}");
            Check(gcSent.Gc.SafetyCollectAboveMB == 0, "SafetyCollectAboveMB 0 stays 0 (AUTO)");
            Check(gcSent.Gc.IncrementalPauseTargetMs == 0, "IncrementalPauseTargetMs 0 stays 0 (no limit)");
            var gcBad = LoadTemp("{\"Gc\":{\"SafetyCollectAboveMB\":-100,\"SafetyCollectRamFraction\":5.0}}");
            Check(gcBad.Gc.SafetyCollectAboveMB == 0, "SafetyCollectAboveMB -100 -> 0");
            Check(gcBad.Gc.SafetyCollectRamFraction == 0.95f, "SafetyCollectRamFraction 5.0 -> 0.95 (max clamp)");

            // Diagnostics seconds: WarmupSeconds feeds Sleep(seconds * 1000), so a
            // value above ~2.1M would wrap the int product negative (Sleep throws);
            // GrowSeconds bounds the grow loop. Both must clamp like every other knob.
            var diagBig = LoadTemp(
                "{\"Diagnostics\":{\"WarmupSeconds\":2500000,\"GrowSeconds\":2000000000}}");
            Check(diagBig.Diagnostics.WarmupSeconds == 3600, "WarmupSeconds 2500000 -> 3600 (ms overflow guard)");
            Check(diagBig.Diagnostics.GrowSeconds == 7200, "GrowSeconds 2000000000 -> 7200 (grow-loop bound)");
            var diagNeg = LoadTemp("{\"Diagnostics\":{\"WarmupSeconds\":-1,\"GrowSeconds\":-50}}");
            Check(diagNeg.Diagnostics.WarmupSeconds == 0, "WarmupSeconds -1 -> 0");
            Check(diagNeg.Diagnostics.GrowSeconds == 1, "GrowSeconds -50 -> 1");
            var diagOk = LoadTemp("{\"Diagnostics\":{\"WarmupSeconds\":30,\"GrowSeconds\":120}}");
            Check(diagOk.Diagnostics.WarmupSeconds == 30 && diagOk.Diagnostics.GrowSeconds == 120,
                "valid Diagnostics seconds round-trip");

            // v1.7.0 fields: MidTickStride clamp, Network + Diagnostics defaults.
            var d2 = new ServerPerfConfig();
            Check(d2.AiLod.MidTickStride == 1, "default MidTickStride=1 (off)");
            Check(d2.Network != null && d2.Network.FastSingleTargetSend, "default FastSingleTargetSend=true (v1.13.0: provably equivalent, no gameplay impact)");
            Check(d2.Diagnostics != null && !d2.Diagnostics.GcMegapauseTest, "default GcMegapauseTest=false");
            var stride = LoadTemp("{\"AiLod\":{\"MidTickStride\":999}}");
            Check(stride.AiLod.MidTickStride == 20, "MidTickStride 999 -> 20 (clamp)");
            var strideNeg = LoadTemp("{\"AiLod\":{\"MidTickStride\":-3}}");
            Check(strideNeg.AiLod.MidTickStride == 1, "MidTickStride -3 -> 1 (clamp)");
            var net = LoadTemp("{\"Network\":{\"FastSingleTargetSend\":false}}");
            Check(!net.Network.FastSingleTargetSend, "Network round-trip FastSingleTargetSend=false (opt-out)");

            // v1.9.0: WorldTransfer chunk batch cap. Default 3 = vanilla; floor 1 is a
            // correctness guard (0 would deadlock the send loop).
            Check(d2.WorldTransfer != null && d2.WorldTransfer.ChunkPackagesPerObserverPerTick == 3,
                "default ChunkPackagesPerObserverPerTick=3 (vanilla)");
            var chunkZero = LoadTemp("{\"WorldTransfer\":{\"ChunkPackagesPerObserverPerTick\":0}}");
            Check(chunkZero.WorldTransfer.ChunkPackagesPerObserverPerTick == 1, "ChunkPackagesPerObserverPerTick 0 -> 1 (deadlock guard)");
            var chunkHi = LoadTemp("{\"WorldTransfer\":{\"ChunkPackagesPerObserverPerTick\":999}}");
            Check(chunkHi.WorldTransfer.ChunkPackagesPerObserverPerTick == 32, "ChunkPackagesPerObserverPerTick 999 -> 32 (clamp)");

            // v1.12.0: governor defaults + the hysteresis invariant (Healthy < OverBudget).
            Check(d2.Governor != null && d2.Governor.Enabled, "default Governor.Enabled=true (inert when healthy)");
            Check(d2.TickGuard != null && !d2.TickGuard.Enabled, "default TickGuard.Enabled=false (removes entities)");
            var shed = LoadTemp("{\"TickGuard\":{\"ShedBatch\":9999,\"ShedAboveMs\":10}}");
            Check(shed.TickGuard.ShedBatch == 100, "TickGuard.ShedBatch 9999 -> 100 (clamp)");
            Check(shed.TickGuard.ShedAboveMs >= 60f && shed.TickGuard.ShedAboveMs >= shed.Governor.OverBudgetMs + 5f,
                "TickGuard.ShedAboveMs 10 -> floored above the governor band (last-resort floor)");
            var gov = LoadTemp("{\"Governor\":{\"OverBudgetMs\":60,\"HealthyMs\":90}}");
            Check(gov.Governor.HealthyMs <= gov.Governor.OverBudgetMs - 5f,
                "Governor hysteresis: HealthyMs forced below OverBudgetMs-5");
            // v1.14.0: thresholds are tick-interval ms and the tick rate follows
            // Server.TargetFps, so sub-50 HealthyMs is legitimate on high-fps tunes;
            // clamps are wide, hysteresis still enforced, defaults assume fps 20.
            var govLow = LoadTemp("{\"Governor\":{\"HealthyMs\":20,\"OverBudgetMs\":30}}");
            Check(govLow.Governor.HealthyMs == 20f && govLow.Governor.OverBudgetMs == 30f,
                "high-fps governor tune 30/20 accepted");
            // NaN/Infinity take the FiniteRange fallback, and the fallback itself must
            // land inside the (possibly sibling-shifted) clamps: an unclamped fallback
            // would re-violate the very invariant Normalize enforces.
            var nanHyst = LoadTemp("{\"Governor\":{\"OverBudgetMs\":20,\"HealthyMs\":NaN}}");
            Check(nanHyst.Governor.HealthyMs == 15f,
                "NaN HealthyMs fallback clamped to exactly OverBudgetMs-5 (=15)");
            var infHyst = LoadTemp("{\"Governor\":{\"OverBudgetMs\":20,\"HealthyMs\":Infinity}}");
            Check(infHyst.Governor.HealthyMs == 15f,
                "Infinity HealthyMs fallback clamped to exactly OverBudgetMs-5 (=15)");
            var nanEmerg = LoadTemp("{\"Governor\":{\"OverBudgetMs\":500,\"EmergencyOverMs\":NaN}}");
            Check(nanEmerg.Governor.EmergencyOverMs == 505f,
                "NaN EmergencyOverMs fallback clamped to exactly OverBudgetMs+5 (=505)");
            var nanScale = LoadTemp("{\"AiLod\":{\"FullScale\":0.1,\"MediumScale\":NaN}}");
            Check(nanScale.AiLod.MediumScale == 0.1f,
                "NaN MediumScale fallback clamped to exactly FullScale (=0.1)");
            // Shed threshold must sit ABOVE the governor band even when the governor
            // is tuned high (shedding is the last resort, past throttling).
            var shedBand = LoadTemp(
                "{\"Governor\":{\"OverBudgetMs\":200},\"TickGuard\":{\"ShedAboveMs\":61}}");
            Check(shedBand.TickGuard.ShedAboveMs == 205f,
                "ShedAboveMs 61 floored to OverBudgetMs 200 + 5 (dynamic last-resort floor)");
            Check(new ServerPerfConfig().Server.TargetFps == 0, "default Server.TargetFps=0 (leave vanilla)");
            var fps = LoadTemp("{\"Server\":{\"TargetFps\":999}}");
            Check(fps.Server.TargetFps == 120, "Server.TargetFps 999 -> 120 (clamp)");
            var govStride = LoadTemp("{\"Network\":{\"EntityDistributionEveryTicks\":9}}");
            Check(govStride.Network.EntityDistributionEveryTicks == 4, "EntityDistributionEveryTicks 9 -> 4 (clamp)");

            // Governor tier math: escalation doubles each lever from its CONFIGURED
            // baseline (never below it - the old hard-coded stride 2 sped replication
            // UP for operators tuned to a static base of 3+), and recovery maps back
            // to that exact baseline. Ceilings mirror Normalize ([1,4] stride,
            // [1,200] graph cadence).
            Check(GovernorTiers.ThrottleLever(1, 4) == 2, "governor: stride baseline 1 -> 2 (default behavior unchanged)");
            Check(GovernorTiers.ThrottleLever(2, 4) == 4, "governor: stride baseline 2 -> 4");
            Check(GovernorTiers.ThrottleLever(3, 4) == 4, "governor: stride baseline 3 -> 4 (ceiling)");
            Check(GovernorTiers.ThrottleLever(4, 4) == 4, "governor: stride baseline 4 stays 4 (no speed-up under load)");
            for (int b = 1; b <= 4; b++)
                Check(GovernorTiers.ThrottleLever(b, 4) >= b,
                    "governor invariant: throttled stride >= configured baseline for base " + b);
            Check(GovernorTiers.ThrottleLever(1, 200) == 2, "governor: graph cadence baseline 1 -> 2 (unchanged)");
            Check(GovernorTiers.ThrottleLever(100, 200) == 200, "governor: graph cadence baseline 100 -> 200 (ceiling)");
            Check(GovernorTiers.ThrottleLever(200, 200) == 200, "governor: graph cadence baseline 200 stays 200");
            var missing = LoadTemp("{}");
            Check(missing.Network != null && missing.Diagnostics != null, "missing Network/Diagnostics -> filled");

            // Encoding boundary: the config file is UTF-8. A non-ASCII unknown key
            // must survive the read verbatim (no mojibake), and a UTF-8 BOM must be
            // tolerated, so operator configs behave identically on every host.
            var cyr = ServerPerfConfig.FindUnknownKeys("{\"Путь\":1}");
            Check(cyr.Count == 1 && cyr[0] == "Путь",
                "FindUnknownKeys: non-ASCII key reported verbatim (UTF-8 read path)");
            string bomP = WriteTempBytes(
                new byte[] { 0xEF, 0xBB, 0xBF }
                .Concat(System.Text.Encoding.UTF8.GetBytes("{\"Enabled\":false}"))
                .ToArray());
            var bom = LoadTempFile(bomP);
            Check(bom != null && !bom.Enabled,
                "UTF-8 BOM prefix tolerated at config load");
            // No-BOM non-ASCII value bytes round-trip through Load without error too.
            string noBomP = WriteTempBytes(
                System.Text.Encoding.UTF8.GetBytes("{\"Pathfinding\":{\"GraphUpdateEveryTicks\":6}}"));
            var noBom = LoadTempFile(noBomP);
            Check(noBom != null && noBom.Pathfinding.GraphUpdateEveryTicks == 6,
                "UTF-8 no-BOM config loads normally");

            // Fuzz: random JSON into Load never throws.
            var rng = new Random(1234);
            const string chars = "{}[]\":,.0123456789abcTruefalsngP_ \t\n";
            for (int i = 0; i < 500; i++)
            {
                var sb = new System.Text.StringBuilder();
                int len = rng.Next(0, 80);
                for (int j = 0; j < len; j++) sb.Append(chars[rng.Next(chars.Length)]);
                try
                {
                    var got = LoadTemp(sb.ToString());
                    Check(got != null, "fuzz load returns non-null");
                }
                catch (Exception ex)
                {
                    Check(false, "fuzz load threw: " + ex.GetType().Name);
                    break;
                }
            }

            // Dedicated-only gate (ShouldRunFor): disabled config never runs.
            Check(!ServerPerfConfig.ShouldRunFor(false, true, true, true), "active=false -> no run");
            Check(!ServerPerfConfig.ShouldRunFor(true, false, true, true), "enabled=false -> no run");
            // DedicatedOnly=false runs anywhere.
            Check(ServerPerfConfig.ShouldRunFor(true, true, false, null), "dedicatedOnly=false -> run (host unknown)");
            Check(ServerPerfConfig.ShouldRunFor(true, true, false, true), "dedicatedOnly=false -> run (host any)");
            // DedicatedOnly=true requires a confirmed dedicated host.
            Check(ServerPerfConfig.ShouldRunFor(true, true, true, true), "dedicatedOnly=true + dedicated -> run");
            Check(!ServerPerfConfig.ShouldRunFor(true, true, true, false), "dedicatedOnly=true + client -> no run");
            Check(!ServerPerfConfig.ShouldRunFor(true, true, true, null), "dedicatedOnly=true + unknown -> fail closed");

            // Per-feature gating (FeatureActive): off-by-default features are inert.
            var fa = new ServerPerfConfig();
            Check(fa.FeatureActive("AiLod"), "default AiLod -> active (Enabled=true default)");
            Check(!fa.FeatureActive("TickGuard"), "default TickGuard -> inactive (Enabled=false default)");
            Check(!fa.FeatureActive("BenchGod"), "default BenchGod -> inactive (console flag off)");
            Check(fa.FeatureActive("BenchGod", true), "BenchGod -> active when console flag on");
            Check(fa.FeatureActive("FastSend"), "default FastSend -> active (opt-out feature)");
            Check(fa.FeatureActive("Governor"), "default Governor -> active (inert when healthy)");
            Check(!fa.FeatureActive("AnimatorLod"), "default AnimatorLod -> inactive (Enabled=false default)");
            Check(!fa.FeatureActive("CrowdCollisionLod"), "default CrowdCollisionLod -> inactive (Enabled=false default)");
            Check(!fa.FeatureActive("UnknownFeature"), "unknown feature key -> inactive");
            // Config-driven features flip with their knobs.
            var faOn = LoadTemp(
                "{\"AiLod\":{\"Enabled\":true},\"TickGuard\":{\"Enabled\":true}," +
                "\"Pathfinding\":{\"GraphUpdateEveryTicks\":8,\"MoveRescanThresholdSq\":400," +
                "\"MaxPathEnqueuesPerTick\":64,\"PoolInitScanNodes\":true}," +
                "\"Network\":{\"EntityDistributionEveryTicks\":4}," +
                "\"WorldTransfer\":{\"ChunkPackagesPerObserverPerTick\":8}," +
                "\"SkipOnDedicated\":{\"ExplosionParticles\":true}," +
                "\"AnimatorLod\":{\"Enabled\":true},\"CrowdCollisionLod\":{\"Enabled\":true}," +
                "\"Server\":{\"TargetFps\":60}}");
            Check(faOn.FeatureActive("AiLod"), "AiLod enabled -> active");
            Check(faOn.FeatureActive("TickGuard"), "TickGuard enabled -> active");
            Check(faOn.FeatureActive("GraphThrottle"), "GraphUpdateEveryTicks 8 -> active");
            Check(faOn.FeatureActive("MoveThreshold"), "MoveRescanThresholdSq 400 -> active");
            Check(faOn.FeatureActive("PathAdmission"), "MaxPathEnqueuesPerTick 64 -> active");
            Check(faOn.FeatureActive("InitScanPool"), "PoolInitScanNodes -> active");
            Check(faOn.FeatureActive("EntityDistributionStride"), "EntityDistributionEveryTicks 4 -> active");
            Check(faOn.FeatureActive("ChunkSendThrottle"), "ChunkPackagesPerObserverPerTick 8 -> active");
            Check(faOn.FeatureActive("ExplosionParticles"), "SkipOnDedicated.ExplosionParticles -> active");
            Check(faOn.FeatureActive("AnimatorLod"), "AnimatorLod enabled -> active");
            Check(faOn.FeatureActive("CrowdCollisionLod"), "CrowdCollisionLod enabled -> active");
            Check(faOn.FeatureActive("TargetFps"), "TargetFps 60 -> active");
            // Gc needs both Enabled and SkipForcedCollect.
            var gcOn = LoadTemp("{\"Gc\":{\"Enabled\":true,\"SkipForcedCollect\":true}}");
            Check(gcOn.FeatureActive("Gc"), "Gc enabled + SkipForcedCollect -> active");
            var gcHalf = LoadTemp("{\"Gc\":{\"Enabled\":true,\"SkipForcedCollect\":false}}");
            Check(!gcHalf.FeatureActive("Gc"), "Gc enabled but SkipForcedCollect=false -> inactive");
            // Off values keep features inactive.
            var faOff = LoadTemp(
                "{\"Pathfinding\":{\"GraphUpdateEveryTicks\":1,\"MoveRescanThresholdSq\":100," +
                "\"MaxPathEnqueuesPerTick\":0,\"PoolInitScanNodes\":false}," +
                "\"Network\":{\"FastSingleTargetSend\":false,\"EntityDistributionEveryTicks\":1}," +
                "\"WorldTransfer\":{\"ChunkPackagesPerObserverPerTick\":3}," +
                "\"Server\":{\"TargetFps\":0}}");
            Check(!faOff.FeatureActive("GraphThrottle"), "GraphUpdateEveryTicks 1 -> inactive");
            Check(!faOff.FeatureActive("MoveThreshold"), "MoveRescanThresholdSq 100 -> inactive");
            Check(!faOff.FeatureActive("PathAdmission"), "no path admission knobs -> inactive");
            Check(!faOff.FeatureActive("InitScanPool"), "PoolInitScanNodes false -> inactive");
            Check(!faOff.FeatureActive("FastSend"), "FastSingleTargetSend false -> inactive");
            Check(!faOff.FeatureActive("EntityDistributionStride"), "EntityDistributionEveryTicks 1 -> inactive");
            Check(!faOff.FeatureActive("ChunkSendThrottle"), "ChunkPackagesPerObserverPerTick 3 (vanilla) -> inactive");
            Check(!faOff.FeatureActive("TargetFps"), "TargetFps 0 -> inactive");

            // Normalize bounds for the remaining knob groups, each with its own
            // range (floors differ: buffer 0, region-ms/syncs/stride 1). Exact
            // clamped values are deterministic from the inputs alone.
            var clamps = LoadTemp(
                "{\"DynamicMesh\":{\"PlayerAreaChunkBuffer\":-1,\"MaxRegionLoadMsPerFrame\":0," +
                "\"MaxActiveSyncs\":999},\"Server\":{\"JobWorkerCount\":65}," +
                "\"AnimatorLod\":{\"FarStride\":99,\"FullRateDistSq\":50}," +
                "\"CrowdCollisionLod\":{\"ResolveEveryNTicks\":99}," +
                "\"Governor\":{\"WindowTicks\":5,\"CooldownTicks\":-1}," +
                "\"TickGuard\":{\"WindowTicks\":10,\"CooldownTicks\":5,\"MinEnemiesKept\":-5}}");
            Check(clamps.DynamicMesh.PlayerAreaChunkBuffer == 0, "PlayerAreaChunkBuffer -1 -> 0");
            Check(clamps.DynamicMesh.MaxRegionLoadMsPerFrame == 1, "MaxRegionLoadMsPerFrame 0 -> 1");
            Check(clamps.DynamicMesh.MaxActiveSyncs == 128, "MaxActiveSyncs 999 -> 128");
            Check(clamps.Server.JobWorkerCount == 64, "JobWorkerCount 65 -> 64");
            Check(clamps.AnimatorLod.FarStride == 10, "AnimatorLod.FarStride 99 -> 10");
            Check(clamps.AnimatorLod.FullRateDistSq == 100f, "AnimatorLod.FullRateDistSq 50 -> 100");
            Check(clamps.CrowdCollisionLod.ResolveEveryNTicks == 16, "CrowdCollisionLod.ResolveEveryNTicks 99 -> 16");
            Check(clamps.Governor.WindowTicks == 20, "Governor.WindowTicks 5 -> 20");
            Check(clamps.Governor.CooldownTicks == 0, "Governor.CooldownTicks -1 -> 0");
            Check(clamps.TickGuard.WindowTicks == 20, "TickGuard.WindowTicks 10 -> 20");
            Check(clamps.TickGuard.CooldownTicks == 20, "TickGuard.CooldownTicks 5 -> 20");
            Check(clamps.TickGuard.MinEnemiesKept == 0, "TickGuard.MinEnemiesKept -5 -> 0");

            if (_failures == 0)
            {
                Console.WriteLine("PASS: all Config Load/Normalize checks");
                return 0;
            }
            Console.WriteLine($"FAILED: {_failures} check(s)");
            return 1;
        }
    }
}
