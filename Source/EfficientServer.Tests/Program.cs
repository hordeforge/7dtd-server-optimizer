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

            // Normalize: NaN/Inf fall back.
            var nan = ServerPerfConfig.Load(WriteTemp("{\"AiLod\":{\"FullAiDistSq\":\"NaN\"}}"));
            Check(!float.IsNaN(nan.AiLod.FullAiDistSq), "NaN FullAiDistSq -> finite fallback");

            // Normalize: inverted Medium>Full scale clamps (Medium <= Full).
            var inv = ServerPerfConfig.Load(WriteTemp("{\"AiLod\":{\"FullScale\":0.3,\"MediumScale\":0.9}}"));
            Check(inv.AiLod.MediumScale <= inv.AiLod.FullScale, "MediumScale clamped <= FullScale");

            // Normalize: Gc 0-sentinels preserved; garbage clamped.
            var gcSent = ServerPerfConfig.Load(WriteTemp("{\"Gc\":{\"SafetyCollectAboveMB\":0,\"IncrementalPauseTargetMs\":0}}"));
            Check(gcSent.Gc.SafetyCollectAboveMB == 0, "SafetyCollectAboveMB 0 stays 0 (AUTO)");
            Check(gcSent.Gc.IncrementalPauseTargetMs == 0, "IncrementalPauseTargetMs 0 stays 0 (no limit)");
            var gcBad = ServerPerfConfig.Load(WriteTemp("{\"Gc\":{\"SafetyCollectAboveMB\":-100,\"SafetyCollectRamFraction\":5.0}}"));
            Check(gcBad.Gc.SafetyCollectAboveMB == 0, "SafetyCollectAboveMB -100 -> 0");
            Check(gcBad.Gc.SafetyCollectRamFraction <= 0.95f, "SafetyCollectRamFraction 5.0 -> <=0.95");

            // v1.7.0 fields: MidTickStride clamp, Network + Diagnostics defaults.
            var d2 = new ServerPerfConfig();
            Check(d2.AiLod.MidTickStride == 1, "default MidTickStride=1 (off)");
            Check(d2.Network != null && !d2.Network.FastSingleTargetSend, "default FastSingleTargetSend=false");
            Check(d2.Diagnostics != null && !d2.Diagnostics.GcMegapauseTest, "default GcMegapauseTest=false");
            var stride = ServerPerfConfig.Load(WriteTemp("{\"AiLod\":{\"MidTickStride\":999}}"));
            Check(stride.AiLod.MidTickStride == 20, "MidTickStride 999 -> 20 (clamp)");
            var strideNeg = ServerPerfConfig.Load(WriteTemp("{\"AiLod\":{\"MidTickStride\":-3}}"));
            Check(strideNeg.AiLod.MidTickStride == 1, "MidTickStride -3 -> 1 (clamp)");
            var net = ServerPerfConfig.Load(WriteTemp("{\"Network\":{\"FastSingleTargetSend\":true}}"));
            Check(net.Network.FastSingleTargetSend, "Network round-trip FastSingleTargetSend=true");

            // v1.9.0: WorldTransfer chunk batch cap. Default 3 = vanilla; floor 1 is a
            // correctness guard (0 would deadlock the send loop).
            Check(d2.WorldTransfer != null && d2.WorldTransfer.ChunkPackagesPerObserverPerTick == 3,
                "default ChunkPackagesPerObserverPerTick=3 (vanilla)");
            var chunkZero = ServerPerfConfig.Load(WriteTemp("{\"WorldTransfer\":{\"ChunkPackagesPerObserverPerTick\":0}}"));
            Check(chunkZero.WorldTransfer.ChunkPackagesPerObserverPerTick == 1, "ChunkPackagesPerObserverPerTick 0 -> 1 (deadlock guard)");
            var chunkHi = ServerPerfConfig.Load(WriteTemp("{\"WorldTransfer\":{\"ChunkPackagesPerObserverPerTick\":999}}"));
            Check(chunkHi.WorldTransfer.ChunkPackagesPerObserverPerTick == 32, "ChunkPackagesPerObserverPerTick 999 -> 32 (clamp)");
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
