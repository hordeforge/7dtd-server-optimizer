using System;
using System.IO;

// Stub the only external symbol Config.cs touches (game-type-free), so the real
// Config source compiles and runs under the plain .NET SDK.
namespace EfficientServer
{
    internal static class ModApi
    {
        public static void Log(string msg) { /* swallow in tests */ }
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
            var bad = ServerPerfConfig.Load(WriteTemp("{ this is not json ]["));
            Check(bad != null && bad.Enabled, "malformed json -> defaults");

            // Empty object -> defaults filled.
            var empty = ServerPerfConfig.Load(WriteTemp("{}"));
            Check(empty != null && empty.Pathfinding != null && empty.Gc != null, "empty object -> sub-configs filled");

            // Valid round-trip.
            var ok = ServerPerfConfig.Load(WriteTemp("{\"Pathfinding\":{\"GraphUpdateEveryTicks\":8,\"MoveRescanThresholdSq\":400}}"));
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
            var typo = ServerPerfConfig.Load(WriteTemp("{\"Pathfinding\":{\"GraphUpdateEveryTick\":8}}"));
            Check(typo != null && typo.Pathfinding.GraphUpdateEveryTicks == 4,
                "typo'd knob keeps default (and is logged), other fields unaffected");
            var caseBind = ServerPerfConfig.Load(WriteTemp("{\"ailod\":{\"enabled\":false}}"));
            Check(caseBind.AiLod.Enabled == false,
                "case-variant key binds like Newtonsoft (value applied)");

            // Normalize: GraphUpdateEveryTicks clamps [1,200].
            var big = ServerPerfConfig.Load(WriteTemp("{\"Pathfinding\":{\"GraphUpdateEveryTicks\":1000000}}"));
            Check(big.Pathfinding.GraphUpdateEveryTicks == 200, "GraphUpdateEveryTicks 1e6 -> 200");
            var neg = ServerPerfConfig.Load(WriteTemp("{\"Pathfinding\":{\"GraphUpdateEveryTicks\":-5}}"));
            Check(neg.Pathfinding.GraphUpdateEveryTicks == 1, "GraphUpdateEveryTicks -5 -> 1");

            // Normalize: MoveRescanThresholdSq clamps [100,10000].
            var lowThr = ServerPerfConfig.Load(WriteTemp("{\"Pathfinding\":{\"MoveRescanThresholdSq\":5}}"));
            Check(lowThr.Pathfinding.MoveRescanThresholdSq == 100f, "MoveRescanThresholdSq 5 -> 100");
            var hiThr = ServerPerfConfig.Load(WriteTemp("{\"Pathfinding\":{\"MoveRescanThresholdSq\":999999}}"));
            Check(hiThr.Pathfinding.MoveRescanThresholdSq == 10000f, "MoveRescanThresholdSq 999999 -> 10000");
            var pathCap = ServerPerfConfig.Load(WriteTemp("{\"Pathfinding\":{\"MaxPathEnqueuesPerTick\":99999,\"DropPathWhenFarDistSq\":-1}}"));
            Check(pathCap.Pathfinding.MaxPathEnqueuesPerTick == 2000, "MaxPathEnqueuesPerTick 99999 -> 2000");
            Check(pathCap.Pathfinding.DropPathWhenFarDistSq == 0f, "DropPathWhenFarDistSq -1 -> 0");
            var pathOk = ServerPerfConfig.Load(WriteTemp("{\"Pathfinding\":{\"MaxPathEnqueuesPerTick\":64,\"DropPathWhenFarDistSq\":2500}}"));
            Check(pathOk.Pathfinding.MaxPathEnqueuesPerTick == 64, "MaxPathEnqueuesPerTick round-trip 64");
            Check(pathOk.Pathfinding.DropPathWhenFarDistSq == 2500f, "DropPathWhenFarDistSq round-trip 2500");

            // Normalize: NaN/Inf fall back.
            var nan = ServerPerfConfig.Load(WriteTemp("{\"AiLod\":{\"FullAiDistSq\":\"NaN\"}}"));
            Check(!float.IsNaN(nan.AiLod.FullAiDistSq), "NaN FullAiDistSq -> finite fallback");

            // Normalize: inverted Medium>Full scale clamps (Medium <= Full).
            var inv = ServerPerfConfig.Load(WriteTemp("{\"AiLod\":{\"FullScale\":0.3,\"MediumScale\":0.9}}"));
            Check(inv.AiLod.MediumScale <= inv.AiLod.FullScale, "MediumScale clamped <= FullScale");

            // Correctness invariant: the AiLod bands are monotonically nested and
            // the scales monotonically decreasing, so a loaded config can never
            // produce a broken band ordering (full inside medium inside far).
            var bands = ServerPerfConfig.Load(WriteTemp(
                "{\"AiLod\":{\"FullAiDistSq\":999999,\"MediumAiDistSq\":0.1,\"SkipTasksFarDistSq\":0.05," +
                "\"FullScale\":0.0,\"MediumScale\":1.0,\"FarScale\":0.9}}"));
            Check(bands.AiLod.FullAiDistSq <= bands.AiLod.MediumAiDistSq,
                "band invariant: FullAiDistSq <= MediumAiDistSq after normalize");
            Check(bands.AiLod.MediumAiDistSq <= bands.AiLod.SkipTasksFarDistSq,
                "band invariant: MediumAiDistSq <= SkipTasksFarDistSq after normalize");
            Check(bands.AiLod.FullScale >= bands.AiLod.MediumScale,
                "scale invariant: FullScale >= MediumScale after normalize");
            Check(bands.AiLod.MediumScale >= bands.AiLod.FarScale,
                "scale invariant: MediumScale >= FarScale after normalize");
            var bandsOk = ServerPerfConfig.Load(WriteTemp(
                "{\"AiLod\":{\"FullAiDistSq\":50,\"MediumAiDistSq\":200,\"SkipTasksFarDistSq\":900," +
                "\"FullScale\":1.0,\"MediumScale\":0.4,\"FarScale\":0.1}}"));
            Check(bandsOk.AiLod.FullAiDistSq == 50f && bandsOk.AiLod.MediumAiDistSq == 200f
                && bandsOk.AiLod.SkipTasksFarDistSq == 900f,
                "band round-trip: valid nested distances preserved");
            Check(bandsOk.AiLod.FullScale == 1f && bandsOk.AiLod.MediumScale == 0.4f
                && bandsOk.AiLod.FarScale == 0.1f,
                "scale round-trip: valid decreasing scales preserved");

            // Normalize: Gc 0-sentinels preserved; garbage clamped.
            var gcSent = ServerPerfConfig.Load(WriteTemp("{\"Gc\":{\"SafetyCollectAboveMB\":0,\"IncrementalPauseTargetMs\":0}}"));
            Check(gcSent.Gc.SafetyCollectAboveMB == 0, "SafetyCollectAboveMB 0 stays 0 (AUTO)");
            Check(gcSent.Gc.IncrementalPauseTargetMs == 0, "IncrementalPauseTargetMs 0 stays 0 (no limit)");
            var gcBad = ServerPerfConfig.Load(WriteTemp("{\"Gc\":{\"SafetyCollectAboveMB\":-100,\"SafetyCollectRamFraction\":5.0}}"));
            Check(gcBad.Gc.SafetyCollectAboveMB == 0, "SafetyCollectAboveMB -100 -> 0");
            Check(gcBad.Gc.SafetyCollectRamFraction <= 0.95f, "SafetyCollectRamFraction 5.0 -> <=0.95");

            // Diagnostics seconds: WarmupSeconds feeds Sleep(seconds * 1000), so a
            // value above ~2.1M would wrap the int product negative (Sleep throws);
            // GrowSeconds bounds the grow loop. Both must clamp like every other knob.
            var diagBig = ServerPerfConfig.Load(WriteTemp(
                "{\"Diagnostics\":{\"WarmupSeconds\":2500000,\"GrowSeconds\":2000000000}}"));
            Check(diagBig.Diagnostics.WarmupSeconds == 3600, "WarmupSeconds 2500000 -> 3600 (ms overflow guard)");
            Check(diagBig.Diagnostics.GrowSeconds == 7200, "GrowSeconds 2000000000 -> 7200 (grow-loop bound)");
            var diagNeg = ServerPerfConfig.Load(WriteTemp("{\"Diagnostics\":{\"WarmupSeconds\":-1,\"GrowSeconds\":-50}}"));
            Check(diagNeg.Diagnostics.WarmupSeconds == 0, "WarmupSeconds -1 -> 0");
            Check(diagNeg.Diagnostics.GrowSeconds == 1, "GrowSeconds -50 -> 1");
            var diagOk = ServerPerfConfig.Load(WriteTemp("{\"Diagnostics\":{\"WarmupSeconds\":30,\"GrowSeconds\":120}}"));
            Check(diagOk.Diagnostics.WarmupSeconds == 30 && diagOk.Diagnostics.GrowSeconds == 120,
                "valid Diagnostics seconds round-trip");

            // v1.7.0 fields: MidTickStride clamp, Network + Diagnostics defaults.
            var d2 = new ServerPerfConfig();
            Check(d2.AiLod.MidTickStride == 1, "default MidTickStride=1 (off)");
            Check(d2.Network != null && d2.Network.FastSingleTargetSend, "default FastSingleTargetSend=true (v1.13.0: provably equivalent, no gameplay impact)");
            Check(d2.Diagnostics != null && !d2.Diagnostics.GcMegapauseTest, "default GcMegapauseTest=false");
            var stride = ServerPerfConfig.Load(WriteTemp("{\"AiLod\":{\"MidTickStride\":999}}"));
            Check(stride.AiLod.MidTickStride == 20, "MidTickStride 999 -> 20 (clamp)");
            var strideNeg = ServerPerfConfig.Load(WriteTemp("{\"AiLod\":{\"MidTickStride\":-3}}"));
            Check(strideNeg.AiLod.MidTickStride == 1, "MidTickStride -3 -> 1 (clamp)");
            var net = ServerPerfConfig.Load(WriteTemp("{\"Network\":{\"FastSingleTargetSend\":false}}"));
            Check(!net.Network.FastSingleTargetSend, "Network round-trip FastSingleTargetSend=false (opt-out)");

            // v1.9.0: WorldTransfer chunk batch cap. Default 3 = vanilla; floor 1 is a
            // correctness guard (0 would deadlock the send loop).
            Check(d2.WorldTransfer != null && d2.WorldTransfer.ChunkPackagesPerObserverPerTick == 3,
                "default ChunkPackagesPerObserverPerTick=3 (vanilla)");
            var chunkZero = ServerPerfConfig.Load(WriteTemp("{\"WorldTransfer\":{\"ChunkPackagesPerObserverPerTick\":0}}"));
            Check(chunkZero.WorldTransfer.ChunkPackagesPerObserverPerTick == 1, "ChunkPackagesPerObserverPerTick 0 -> 1 (deadlock guard)");
            var chunkHi = ServerPerfConfig.Load(WriteTemp("{\"WorldTransfer\":{\"ChunkPackagesPerObserverPerTick\":999}}"));
            Check(chunkHi.WorldTransfer.ChunkPackagesPerObserverPerTick == 32, "ChunkPackagesPerObserverPerTick 999 -> 32 (clamp)");

            // v1.12.0: governor defaults + the hysteresis invariant (Healthy < OverBudget).
            Check(d2.Governor != null && d2.Governor.Enabled, "default Governor.Enabled=true (inert when healthy)");
            Check(d2.TickGuard != null && !d2.TickGuard.Enabled, "default TickGuard.Enabled=false (removes entities)");
            var shed = ServerPerfConfig.Load(WriteTemp("{\"TickGuard\":{\"ShedBatch\":9999,\"ShedAboveMs\":10}}"));
            Check(shed.TickGuard.ShedBatch == 100, "TickGuard.ShedBatch 9999 -> 100 (clamp)");
            Check(shed.TickGuard.ShedAboveMs >= 60f, "TickGuard.ShedAboveMs 10 -> >=60 (last-resort floor)");
            var gov = ServerPerfConfig.Load(WriteTemp("{\"Governor\":{\"OverBudgetMs\":60,\"HealthyMs\":90}}"));
            Check(gov.Governor.HealthyMs <= gov.Governor.OverBudgetMs - 5f,
                "Governor hysteresis: HealthyMs forced below OverBudgetMs-5");
            // v1.14.0: thresholds are tick-interval ms and the tick rate follows
            // Server.TargetFps, so sub-50 HealthyMs is legitimate on high-fps tunes;
            // clamps are wide, hysteresis still enforced, defaults assume fps 20.
            var govLow = ServerPerfConfig.Load(WriteTemp("{\"Governor\":{\"HealthyMs\":20,\"OverBudgetMs\":30}}"));
            Check(govLow.Governor.HealthyMs == 20f && govLow.Governor.OverBudgetMs == 30f,
                "high-fps governor tune 30/20 accepted");
            // NaN/Infinity take the FiniteRange fallback, and the fallback itself must
            // land inside the (possibly sibling-shifted) clamps: an unclamped fallback
            // would re-violate the very invariant Normalize enforces.
            var nanHyst = ServerPerfConfig.Load(WriteTemp("{\"Governor\":{\"OverBudgetMs\":20,\"HealthyMs\":NaN}}"));
            Check(nanHyst.Governor.HealthyMs <= nanHyst.Governor.OverBudgetMs - 5f,
                "NaN HealthyMs fallback clamped into OverBudgetMs-5 hysteresis");
            var infHyst = ServerPerfConfig.Load(WriteTemp("{\"Governor\":{\"OverBudgetMs\":20,\"HealthyMs\":Infinity}}"));
            Check(infHyst.Governor.HealthyMs <= infHyst.Governor.OverBudgetMs - 5f,
                "Infinity HealthyMs fallback clamped into OverBudgetMs-5 hysteresis");
            var nanEmerg = ServerPerfConfig.Load(WriteTemp("{\"Governor\":{\"OverBudgetMs\":500,\"EmergencyOverMs\":NaN}}"));
            Check(nanEmerg.Governor.EmergencyOverMs >= nanEmerg.Governor.OverBudgetMs + 5f,
                "NaN EmergencyOverMs fallback clamped above OverBudgetMs+5");
            var nanScale = ServerPerfConfig.Load(WriteTemp("{\"AiLod\":{\"FullScale\":0.1,\"MediumScale\":NaN}}"));
            Check(nanScale.AiLod.MediumScale <= nanScale.AiLod.FullScale,
                "NaN MediumScale fallback clamped below FullScale");
            // Shed threshold must sit ABOVE the governor band even when the governor
            // is tuned high (shedding is the last resort, past throttling).
            var shedBand = ServerPerfConfig.Load(WriteTemp(
                "{\"Governor\":{\"OverBudgetMs\":200},\"TickGuard\":{\"ShedAboveMs\":61}}"));
            Check(shedBand.TickGuard.ShedAboveMs > shedBand.Governor.OverBudgetMs,
                "ShedAboveMs floored above the tuned governor band");
            Check(new ServerPerfConfig().Server.TargetFps == 0, "default Server.TargetFps=0 (leave vanilla)");
            var fps = ServerPerfConfig.Load(WriteTemp("{\"Server\":{\"TargetFps\":999}}"));
            Check(fps.Server.TargetFps == 120, "Server.TargetFps 999 -> 120 (clamp)");
            var govStride = ServerPerfConfig.Load(WriteTemp("{\"Network\":{\"EntityDistributionEveryTicks\":9}}"));
            Check(govStride.Network.EntityDistributionEveryTicks == 4, "EntityDistributionEveryTicks 9 -> 4 (clamp)");
            var missing = ServerPerfConfig.Load(WriteTemp("{}"));
            Check(missing.Network != null && missing.Diagnostics != null, "missing Network/Diagnostics -> filled");

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
                    var got = ServerPerfConfig.Load(WriteTemp(sb.ToString()));
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
            Check(!fa.FeatureActive("UnknownFeature"), "unknown feature key -> inactive");
            // Config-driven features flip with their knobs.
            var faOn = ServerPerfConfig.Load(WriteTemp(
                "{\"AiLod\":{\"Enabled\":true},\"TickGuard\":{\"Enabled\":true}," +
                "\"Pathfinding\":{\"GraphUpdateEveryTicks\":8,\"MoveRescanThresholdSq\":400," +
                "\"MaxPathEnqueuesPerTick\":64,\"PoolInitScanNodes\":true}," +
                "\"Network\":{\"EntityDistributionEveryTicks\":4}," +
                "\"WorldTransfer\":{\"ChunkPackagesPerObserverPerTick\":8}," +
                "\"SkipOnDedicated\":{\"ExplosionParticles\":true}," +
                "\"Server\":{\"TargetFps\":60}}"));
            Check(faOn.FeatureActive("AiLod"), "AiLod enabled -> active");
            Check(faOn.FeatureActive("TickGuard"), "TickGuard enabled -> active");
            Check(faOn.FeatureActive("GraphThrottle"), "GraphUpdateEveryTicks 8 -> active");
            Check(faOn.FeatureActive("MoveThreshold"), "MoveRescanThresholdSq 400 -> active");
            Check(faOn.FeatureActive("PathAdmission"), "MaxPathEnqueuesPerTick 64 -> active");
            Check(faOn.FeatureActive("InitScanPool"), "PoolInitScanNodes -> active");
            Check(faOn.FeatureActive("EntityDistributionStride"), "EntityDistributionEveryTicks 4 -> active");
            Check(faOn.FeatureActive("ChunkSendThrottle"), "ChunkPackagesPerObserverPerTick 8 -> active");
            Check(faOn.FeatureActive("ExplosionParticles"), "SkipOnDedicated.ExplosionParticles -> active");
            Check(faOn.FeatureActive("TargetFps"), "TargetFps 60 -> active");
            // Gc needs both Enabled and SkipForcedCollect.
            var gcOn = ServerPerfConfig.Load(WriteTemp("{\"Gc\":{\"Enabled\":true,\"SkipForcedCollect\":true}}"));
            Check(gcOn.FeatureActive("Gc"), "Gc enabled + SkipForcedCollect -> active");
            var gcHalf = ServerPerfConfig.Load(WriteTemp("{\"Gc\":{\"Enabled\":true,\"SkipForcedCollect\":false}}"));
            Check(!gcHalf.FeatureActive("Gc"), "Gc enabled but SkipForcedCollect=false -> inactive");
            // Off values keep features inactive.
            var faOff = ServerPerfConfig.Load(WriteTemp(
                "{\"Pathfinding\":{\"GraphUpdateEveryTicks\":1,\"MoveRescanThresholdSq\":100," +
                "\"MaxPathEnqueuesPerTick\":0,\"PoolInitScanNodes\":false}," +
                "\"Network\":{\"FastSingleTargetSend\":false,\"EntityDistributionEveryTicks\":1}," +
                "\"WorldTransfer\":{\"ChunkPackagesPerObserverPerTick\":3}," +
                "\"Server\":{\"TargetFps\":0}}"));
            Check(!faOff.FeatureActive("GraphThrottle"), "GraphUpdateEveryTicks 1 -> inactive");
            Check(!faOff.FeatureActive("MoveThreshold"), "MoveRescanThresholdSq 100 -> inactive");
            Check(!faOff.FeatureActive("PathAdmission"), "no path admission knobs -> inactive");
            Check(!faOff.FeatureActive("InitScanPool"), "PoolInitScanNodes false -> inactive");
            Check(!faOff.FeatureActive("FastSend"), "FastSingleTargetSend false -> inactive");
            Check(!faOff.FeatureActive("EntityDistributionStride"), "EntityDistributionEveryTicks 1 -> inactive");
            Check(!faOff.FeatureActive("ChunkSendThrottle"), "ChunkPackagesPerObserverPerTick 3 (vanilla) -> inactive");
            Check(!faOff.FeatureActive("TargetFps"), "TargetFps 0 -> inactive");

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
