using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

// Stub the only external symbols Config.cs touches (game-type-free), so the real
// Config source compiles and runs under the plain .NET SDK. Warnings are recorded
// so tests can pin which channel each config problem is reported on.
namespace EfficientServer
{
    internal static class EsLog
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

        // Scratch JSON blob, removed before returning: Load reads it
        // synchronously, so nothing needs it afterwards. Without this every
        // run leaks ~500 fuzz + ~40 fixture files into the temp dir.
        static string WriteTemp(string json)
            => WriteTempBytes(System.Text.Encoding.UTF8.GetBytes(json));

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
            EsLog.Warnings.Clear();
            return LoadTemp(json);
        }

        // Discovery precedence of DefaultPathBesideAssembly: the packaged
        // Config/efficientserver.json must win over a legacy sibling file, and
        // with neither present the sibling path is returned so Load takes the
        // missing-file branch. Both candidates resolve next to THIS test binary
        // (the method walks its own assembly), so the fixtures are created and
        // removed around each probe; any pre-existing files are saved and put
        // back, so a crashed earlier run cannot wedge or pollute the harness.
        static void CheckDefaultPathDiscovery()
        {
            string asmDir = Path.GetDirectoryName(typeof(ServerPerfConfig).Assembly.Location) ?? ".";
            string subDir = Path.Combine(asmDir, "Config");
            string subPath = Path.Combine(subDir, "efficientserver.json");
            string sibPath = Path.Combine(asmDir, "efficientserver.json");
            byte[]? subBefore = File.Exists(subPath) ? File.ReadAllBytes(subPath) : null;
            byte[]? sibBefore = File.Exists(sibPath) ? File.ReadAllBytes(sibPath) : null;
            bool weMadeSubDir = false;
            try
            {
                if (subBefore != null) File.Delete(subPath);
                if (sibBefore != null) File.Delete(sibPath);
                Check(ServerPerfConfig.DefaultPathBesideAssembly() == sibPath,
                    "DefaultPathBesideAssembly: no config anywhere -> sibling fallback path");

                Directory.CreateDirectory(subDir);
                weMadeSubDir = true;
                File.WriteAllText(subPath, "{}");
                File.WriteAllText(sibPath, "{}");
                Check(ServerPerfConfig.DefaultPathBesideAssembly() == subPath,
                    "DefaultPathBesideAssembly: Config/efficientserver.json preferred over sibling");
                File.Delete(subPath);

                Check(ServerPerfConfig.DefaultPathBesideAssembly() == sibPath,
                    "DefaultPathBesideAssembly: sibling file picked up once Config/ copy is gone");
                File.Delete(sibPath);

                var fresh = ServerPerfConfig.Load(ServerPerfConfig.DefaultPathBesideAssembly());
                Check(fresh != null && fresh.Enabled,
                    "DefaultPathBesideAssembly: Load on the discovered-but-missing path -> defaults");
            }
            finally
            {
                if (subBefore != null) File.WriteAllBytes(subPath, subBefore);
                else if (File.Exists(subPath)) File.Delete(subPath);
                if (sibBefore != null) File.WriteAllBytes(sibPath, sibBefore);
                else if (File.Exists(sibPath)) File.Delete(sibPath);
                if (weMadeSubDir && Directory.Exists(subDir) && Directory.GetFiles(subDir).Length == 0)
                    Directory.Delete(subDir);
            }
        }

        // IO-failure branch of Load: a file that EXISTS but cannot be read must
        // take the same fail-soft path as a parse error (defaults + one WARNING
        // naming the failure), never escape as an exception out of dedicated
        // start. Self-skipping: on hosts that do not enforce the mode bits
        // (Windows) or accounts above them (root) the fixture stays readable,
        // and the arrangement simply cannot be built there.
        static void CheckUnreadableFileFailSoft()
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
            string p = WriteTemp("{\"Enabled\":false}");
            try
            {
                var prevMode = File.GetUnixFileMode(p);
                File.SetUnixFileMode(p, UnixFileMode.None);
                try
                {
                    bool stillReadable;
                    try { File.ReadAllText(p); stillReadable = true; }
                    catch { stillReadable = false; }
                    if (!stillReadable)
                    {
                        EsLog.Warnings.Clear();
                        var cfg = ServerPerfConfig.Load(p);
                        Check(cfg != null && cfg.Enabled,
                            "unreadable config file -> defaults (fail-soft like parse errors)");
                        Check(EsLog.Warnings.Count == 1 && EsLog.Warnings[0].StartsWith("Config load failed ["),
                            "unreadable config file -> one WARNING naming the exception type");
                    }
                }
                finally
                {
                    File.SetUnixFileMode(p, prevMode);
                }
            }
            finally
            {
                File.Delete(p);
            }
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

            // Shipped-default VALUE pins for the rest of the knob surface. The
            // normalize-silence check below only proves defaults sit IN RANGE,
            // and the fuzz corpus mutates FROM the serialized defaults, so
            // neither can see an initializer quietly change VALUE (a shed batch
            // of 150 or a horde floor of 6 would ship as "the tuned default"
            // with every clamp and endpoint still green). Expected values are
            // the documented initializers in Config.cs; safety-relevant ones
            // (TickGuard floors, governor band, DedicatedOnly gate) included.
            var dAll = new ServerPerfConfig();
            Check(dAll.Enabled && dAll.DedicatedOnly,
                "defaults: Enabled + DedicatedOnly ship true (dedicated gate closed)");
            Check(dAll.AiLod.FullAiDistSq == 100f && dAll.AiLod.MediumAiDistSq == 400f
                && dAll.AiLod.SkipTasksFarDistSq == 2500f && dAll.AiLod.SkipTasksUnlessAlerted,
                "AiLod distance defaults: 100/400/2500 bands, alerted exempt");
            Check(dAll.AiLod.FullScale == 1f && dAll.AiLod.MediumScale == 0.2f && dAll.AiLod.FarScale == 0.05f,
                "AiLod scale defaults: 1/0.2/0.05");
            Check(dAll.SkipOnDedicated.DynamicMusicSystem && dAll.SkipOnDedicated.WaterSplashParticles
                && dAll.SkipOnDedicated.EnvironmentAudioUpdates
                && dAll.SkipOnDedicated.ClothAndJiggleBoneSimulation
                && dAll.SkipOnDedicated.AmbientLightSpectrumUpdates
                && dAll.SkipOnDedicated.ExplosionParticles,
                "SkipOnDedicated defaults: every render-only skip ships ON");
            Check(dAll.DynamicMesh.Enabled && dAll.DynamicMesh.OnlyPlayerAreas
                && dAll.DynamicMesh.PlayerAreaChunkBuffer == 2
                && dAll.DynamicMesh.MaxRegionLoadMsPerFrame == 2 && dAll.DynamicMesh.MaxActiveSyncs == 2,
                "DynamicMesh defaults: player-area budget 2/2/2");
            Check(dAll.Gc.SkipForcedCollect && !dAll.Gc.Incremental && dAll.Gc.IncrementalPauseTargetMs == 0,
                "Gc defaults: forced collect skipped, incremental OFF, no pause limit");
            Check(!dAll.Pathfinding.PoolInitScanNodes,
                "default PoolInitScanNodes=false (unsafe transpile ships off)");
            Check(dAll.Network.EntityDistributionEveryTicks == 1,
                "default EntityDistributionEveryTicks=1 (vanilla cadence)");
            Check(dAll.Server.JobWorkerCount == 0, "default JobWorkerCount=0 (leave vanilla)");
            Check(!dAll.AnimatorLod.Enabled && dAll.AnimatorLod.FullRateDistSq == 400f
                && dAll.AnimatorLod.FarStride == 4,
                "AnimatorLod defaults: OFF, 20 m full-rate band, far stride 4");
            Check(!dAll.CrowdCollisionLod.Enabled && dAll.CrowdCollisionLod.ResolveEveryNTicks == 4,
                "CrowdCollisionLod defaults: OFF, resolve stride 4");
            Check(dAll.Governor.HealthyMs == 52f && dAll.Governor.EmergencyOverMs == 80f
                && !dAll.Governor.AnimatorEmergency
                && dAll.Governor.WindowTicks == 100 && dAll.Governor.CooldownTicks == 400,
                "Governor defaults: 52/80 band, emergency OFF, 100/400 windows");
            Check(dAll.TickGuard.ShedAboveMs == 70f && dAll.TickGuard.WindowTicks == 60
                && dAll.TickGuard.ShedBatch == 15 && dAll.TickGuard.CooldownTicks == 100
                && dAll.TickGuard.MinEnemiesKept == 60,
                "TickGuard defaults: shed at 70 ms, batch 15, horde floor 60");
            Check(dAll.Diagnostics.WarmupSeconds == 60 && dAll.Diagnostics.GrowSeconds == 240,
                "Diagnostics defaults: warmup 60 s, grow 240 s");

            // Shipped-default drift: Normalize on untouched defaults must be a silent
            // no-op (FiniteRange/IntRange warn exactly when a value moves). A default
            // edited outside its own clamp would otherwise log "config corrected" on
            // every fresh install and silently shift the knob.
            var defNorm = new ServerPerfConfig();
            EsLog.Warnings.Clear();
            defNorm.Normalize();
            Check(EsLog.Warnings.Count == 0, "defaults need no normalization (silent no-op)");

            // Missing file -> defaults, no throw.
            var miss = ServerPerfConfig.Load(Path.Combine(Path.GetTempPath(), "does_not_exist_" + Guid.NewGuid().ToString("N") + ".json"));
            Check(miss != null && miss.Pathfinding.GraphUpdateEveryTicks == 4, "missing file -> defaults");

            // Null / empty path -> the same guard branch as a missing file
            // (Load checks string.IsNullOrEmpty before File.Exists), never a
            // throw. Deliberate runtime-null probe: NRT annotations are
            // erased, so the IsNullOrEmpty guard is the only real defense.
            var nullPath = ServerPerfConfig.Load(null!);
            Check(nullPath != null && nullPath.Enabled, "null path -> defaults");
            var emptyPath = ServerPerfConfig.Load("");
            Check(emptyPath != null && emptyPath.Enabled, "empty path -> defaults");

            // Malformed JSON -> defaults, no throw.
            var bad = LoadTempTracked("{ this is not json ][");
            Check(bad != null && bad.Enabled, "malformed json -> defaults");
            // The failure must surface on the WARNING channel (operators grep the
            // dedicated log for WARNING/ERROR; info-level config failures vanish).
            Check(EsLog.Warnings.Count == 1 && EsLog.Warnings[0].StartsWith("Config load failed ["),
                "malformed json -> one WARNING naming the exception type");

            // Non-object value for a section key: deserialization throws ->
            // Load fails soft with full defaults.
            var secBad = LoadTemp("{\"AiLod\":5}");
            Check(secBad != null && secBad.Enabled && secBad.Pathfinding.GraphUpdateEveryTicks == 4,
                "section value of wrong type -> full defaults");

            // Whole-document JSON null deserializes to a null reference -> the
            // dedicated defaults branch, silently (valid JSON, so not a parse error).
            var docNull = LoadTempTracked("null");
            Check(docNull != null && docNull.Enabled, "whole-document null -> defaults");
            Check(EsLog.Warnings.Count == 0, "whole-document null -> no warning (not malformed)");

            // Empty object -> defaults filled.
            var empty = LoadTemp("{}");
            Check(empty != null && empty.Pathfinding != null && empty.Gc != null, "empty object -> sub-configs filled");

            // Explicit JSON null for a section binds as a null reference and must be
            // backfilled with defaults. Assert EVERY section via reflection so a
            // future knob group cannot skip its backfill line and NRE downstream.
            var secNull = LoadTemp(
                "{\"AiLod\":null,\"SkipOnDedicated\":null,\"DynamicMesh\":null,\"Gc\":null," +
                "\"Pathfinding\":null,\"Network\":null,\"WorldTransfer\":null,\"Server\":null," +
                "\"AnimatorLod\":null,\"CrowdCollisionLod\":null,\"Governor\":null," +
                "\"TickGuard\":null,\"Diagnostics\":null}");
            bool allSectionsBackfilled = true;
            foreach (var sect in typeof(ServerPerfConfig).GetProperties())
                if (sect.PropertyType.IsClass && sect.PropertyType != typeof(string)
                    && sect.PropertyType.Namespace == typeof(ServerPerfConfig).Namespace
                    && sect.GetValue(secNull) == null)
                    { allSectionsBackfilled = false; break; }
            Check(allSectionsBackfilled, "explicit null sections -> every section backfilled");
            Check(secNull.AiLod.FullAiDistSq == 100f && secNull.Governor.OverBudgetMs == 57f,
                "backfilled sections carry defaults, not zeros");

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
            // Case folding must be OrdinalIgnoreCase (what Newtonsoft's binding
            // uses), not the host locale: under a tr-TR culture a culture-sensitive
            // comparison folds 'i' and 'I' apart, which would report all-caps keys
            // as unknown while Newtonsoft still binds them. All-caps variants of
            // I-bearing names must therefore stay "known" here.
            Check(ServerPerfConfig.FindUnknownKeys(
                    "{\"PATHFINDING\":{\"GRAPHUPDATEEVERYTICKS\":8,\"MAXPATHENQUEUESPERTICK\":0}}").Count == 0,
                "FindUnknownKeys: all-caps keys fold ordinally, independent of host locale");
            Check(ServerPerfConfig.KeyNameMatches("AILOD", "AiLod"),
                "KeyNameMatches: AILOD == AiLod under OrdinalIgnoreCase");
            // And a distinct key must not fold: the comparator is an equality,
            // not a prefix/substring match, or typos sharing a prefix with a
            // real key would bind silently instead of being reported.
            Check(!ServerPerfConfig.KeyNameMatches("AiLodX", "AiLod"),
                "KeyNameMatches: distinct keys do not match (no prefix folding)");
            // And a key that only differs by a Unicode case twin (dotless ı U+0131,
            // which Turkish folding maps to/from 'I') is NOT known: ordinal equality
            // keeps it a reported typo.
            Check(ServerPerfConfig.FindUnknownKeys("{\"pathf\u0131nding\":{}}")[0] == "pathf\u0131nding",
                "FindUnknownKeys: dotless-ı spelling is a distinct key (ordinal, no locale folding)");
            Check(ServerPerfConfig.FindUnknownKeys("{ this is not json ][").Count == 0,
                "FindUnknownKeys: malformed json -> empty, no throw");
            Check(ServerPerfConfig.FindUnknownKeys("").Count == 0,
                "FindUnknownKeys: empty input -> empty");
            Check(ServerPerfConfig.FindUnknownKeys(null!).Count == 0,
                "FindUnknownKeys: null input -> empty");
            var typo = LoadTempTracked("{\"Pathfinding\":{\"GraphUpdateEveryTick\":8}}");
            Check(typo != null && typo.Pathfinding.GraphUpdateEveryTicks == 4,
                "typo'd knob keeps default (and is logged), other fields unaffected");
            Check(EsLog.Warnings.Any(w => w.Contains("unknown key 'Pathfinding.GraphUpdateEveryTick'")),
                "typo'd knob -> WARNING names the dotted key");
            var caseBind = LoadTemp("{\"ailod\":{\"enabled\":false}}");
            Check(caseBind.AiLod.Enabled == false,
                "case-variant key binds like Newtonsoft (value applied)");
            var caseBindCaps = LoadTemp("{\"AILOD\":{\"ENABLED\":false}}");
            Check(caseBindCaps.AiLod.Enabled == false,
                "all-caps I-bearing key binds (guard stays in sync with the ordinal binder)");

            // Normalize: GraphUpdateEveryTicks clamps [1,200].
            var big = LoadTempTracked("{\"Pathfinding\":{\"GraphUpdateEveryTicks\":1000000}}");
            Check(big.Pathfinding.GraphUpdateEveryTicks == 200, "GraphUpdateEveryTicks 1e6 -> 200");
            Check(EsLog.Warnings.Any(w => w.StartsWith("config corrected Pathfinding.GraphUpdateEveryTicks")
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

            // Normalize: NaN/Inf fall back EXACTLY to the documented fallback (100),
            // not merely somewhere inside the [1,1e6] clamp. An exact pin keeps a
            // leaked Infinity (which shares the fallback path) and a wrong fallback
            // value both failing here.
            var nan = LoadTemp("{\"AiLod\":{\"FullAiDistSq\":\"NaN\"}}");
            Check(nan.AiLod.FullAiDistSq == 100f, "NaN FullAiDistSq -> exact fallback 100");
            var negInf = LoadTemp("{\"AiLod\":{\"FullAiDistSq\":\"-Infinity\"}}");
            Check(negInf.AiLod.FullAiDistSq == 100f, "-Infinity FullAiDistSq -> exact fallback 100");

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

            // Locale boundary: "config corrected" lines are grepped by operators and
            // matched by these tests, so FiniteRange must format with the INVARIANT
            // culture. Under a comma-decimal host locale a CurrentCulture slip would
            // emit "1,5 -> 0,95" and silently break every log-matching consumer -
            // undetectable on the dot-decimal hosts CI runs on unless forced here.
            var prevCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                var loc = LoadTempTracked("{\"Gc\":{\"SafetyCollectRamFraction\":1.5}}");
                Check(loc.Gc.SafetyCollectRamFraction == 0.95f, "comma-decimal locale: 1.5 still clamps to 0.95");
                Check(EsLog.Warnings.Count == 1 && EsLog.Warnings[0].Contains("1.5 -> 0.95"),
                    "'config corrected' formats floats with dot decimals under a comma-decimal host locale");
            }
            finally
            {
                CultureInfo.CurrentCulture = prevCulture;
            }

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

            // Bench-god arm gate: global player damage immunity must NOT arm from the
            // console without an explicit config opt-in. Pin the secure default, the
            // JSON round-trip, sibling-flag isolation, and the fail-closed paths of
            // the pure predicate the console command consults (null config / null
            // section must refuse, mirroring the deliberate runtime-null probes of
            // FindUnknownKeys above).
            Check(new ServerPerfConfig().Diagnostics != null
                && !new ServerPerfConfig().Diagnostics.AllowBenchGod,
                "default AllowBenchGod=false (benchgod refuses until opted in)");
            var bgOn = LoadTemp("{\"Diagnostics\":{\"AllowBenchGod\":true}}");
            Check(bgOn.Diagnostics != null && bgOn.Diagnostics.AllowBenchGod,
                "Diagnostics.AllowBenchGod=true round-trips");
            Check(bgOn.Diagnostics != null && !bgOn.Diagnostics.GcMegapauseTest,
                "AllowBenchGod=true leaves sibling diagnostic flags at defaults");
            Check(!ServerPerfConfig.BenchGodArmAllowed(null!),
                "BenchGodArmAllowed(null config) -> fail closed");
            Check(!ServerPerfConfig.BenchGodArmAllowed(new ServerPerfConfig()),
                "BenchGodArmAllowed(defaults) -> refused");
            // Deliberate runtime-null probe promised by the guard's contract:
            // NRT annotations are erased, so a null Diagnostics section must
            // fail closed too, not NRE the console command.
            Check(!ServerPerfConfig.BenchGodArmAllowed(new ServerPerfConfig { Diagnostics = null! }),
                "BenchGodArmAllowed(null diagnostics section) -> fail closed");
            Check(ServerPerfConfig.BenchGodArmAllowed(bgOn),
                "BenchGodArmAllowed(opt-in) -> allowed");

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
            // v1.17.x: join-churn race fix ships ON (removes the stock receive-thread
            // crash; the opt-out restores the exact vanilla enumerator).
            Check(d2.Network.ClientListSnapshot, "default ClientListSnapshot=true (join-churn race fix ships on)");
            var clsOff = LoadTemp("{\"Network\":{\"ClientListSnapshot\":false}}");
            Check(clsOff.Network != null && !clsOff.Network.ClientListSnapshot,
                "ClientListSnapshot round-trip false (vanilla enumerator opt-out)");
            Check(net.Network.ClientListSnapshot, "FastSingleTargetSend=false leaves sibling ClientListSnapshot at default");

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
            // Exact pin like the other dynamic-floor cases (ShedAboveMs 61 -> 205
            // below): the floor here is deterministic from the inputs alone,
            // max(60, default OverBudgetMs 57 + 5) = 62.
            Check(shed.TickGuard.ShedAboveMs == 62f,
                "TickGuard.ShedAboveMs 10 -> floored to exactly max(60, OverBudgetMs+5)=62");
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

            // TickStride: the one stride gate shared by both cadence levers.
            // Semantics: exactly every Nth call owns the slot and the counter
            // advances on EVERY call, run or not (a skipped tick must consume
            // its slot, or a burst of missed ticks would collapse into one).
            var s1 = 0;
            Check(EfficientServer.Patches.TickStride.RunThisTick(ref s1, 1), "stride every=1 runs on call 1");
            Check(EfficientServer.Patches.TickStride.RunThisTick(ref s1, 1), "stride every=1 runs on call 2");
            var s4 = 0;
            Check(!EfficientServer.Patches.TickStride.RunThisTick(ref s4, 4), "stride every=4: call 1 -> no run");
            Check(!EfficientServer.Patches.TickStride.RunThisTick(ref s4, 4), "stride every=4: call 2 -> no run");
            Check(!EfficientServer.Patches.TickStride.RunThisTick(ref s4, 4), "stride every=4: call 3 -> no run");
            Check(EfficientServer.Patches.TickStride.RunThisTick(ref s4, 4), "stride every=4: call 4 -> run");
            Check(s4 == 4, "stride counter advanced on non-run calls too (== 4 after four calls)");

            // Signed-wrap boundary: the gate casts through uint so the counter
            // wrapping past int.MaxValue keeps the same slot phase instead of
            // going negative and flipping which ticks run (TickStride doc).
            // Hand-derived expectation, not copied from a run: starting the
            // counter at int.MaxValue-2 with every=3, the unsigned sequence
            // 2147483646.. wraps to 2147483648.. and hits 0 mod 3 exactly on
            // calls 1, 4 and 7. Naive signed modulo would return FALSE on call
            // 4 (-2147483647 % 3 == -1), so this window genuinely pins the cast.
            var sw = int.MaxValue - 2;
            Check(EfficientServer.Patches.TickStride.RunThisTick(ref sw, 3), "stride wrap: call 1 (int.MaxValue-1) -> run");
            Check(!EfficientServer.Patches.TickStride.RunThisTick(ref sw, 3), "stride wrap: call 2 (int.MaxValue) -> no run");
            Check(!EfficientServer.Patches.TickStride.RunThisTick(ref sw, 3), "stride wrap: call 3 (int.MinValue) -> no run");
            Check(sw == int.MinValue, "stride wrap: counter wrapped to int.MinValue after call 3");
            Check(EfficientServer.Patches.TickStride.RunThisTick(ref sw, 3),
                "stride wrap: call 4 (int.MinValue+1) still owns the slot (uint phase kept)");
            Check(!EfficientServer.Patches.TickStride.RunThisTick(ref sw, 3), "stride wrap: call 5 -> no run");
            Check(!EfficientServer.Patches.TickStride.RunThisTick(ref sw, 3), "stride wrap: call 6 -> no run");
            Check(EfficientServer.Patches.TickStride.RunThisTick(ref sw, 3), "stride wrap: call 7 -> run (phase continues)");

            // Concurrent hammer. Production callers are main-thread today (the
            // ARCHITECTURE concurrency model pins every patch surface to the
            // Unity main loop), but RunThisTick uses Interlocked precisely so a
            // future off-main caller composes safely; this pins that guarantee.
            // With T total calls from many threads the counter must advance
            // exactly once per call (no lost increments) and slot ownership must
            // total exactly T / every (each increment draws a unique value in
            // 1..T no matter how calls interleave, so the owned count is
            // order-independent and exact, not statistical).
            const int hammerThreads = 8;
            const int hammerCallsPerThread = 1000000;
            const int hammerEvery = 7;
            int hammerTick = 0;
            var ownedPerThread = new long[hammerThreads];
            var hammers = new Thread[hammerThreads];
            // Start barrier: without it thread-start jitter serializes the
            // workers and the hammer proves nothing (each runs alone).
            var startGate = new Barrier(hammerThreads);
            for (int t = 0; t < hammerThreads; t++)
            {
                int slot = t; // per-thread result cell; no shared write besides RunThisTick's own counter
                hammers[t] = new Thread(() =>
                {
                    long owned = 0;
                    startGate.SignalAndWait();
                    for (int i = 0; i < hammerCallsPerThread; i++)
                        if (EfficientServer.Patches.TickStride.RunThisTick(ref hammerTick, hammerEvery))
                            owned++;
                    ownedPerThread[slot] = owned;
                })
                { IsBackground = true, Name = "es-test-stride-hammer-" + t };
                hammers[t].Start();
            }
            for (int t = 0; t < hammerThreads; t++)
                hammers[t].Join(); // join edges make every thread's writes visible to these asserts
            int hammerTotal = hammerThreads * hammerCallsPerThread;
            Check(hammerTick == hammerTotal,
                "stride hammer: counter advanced exactly once per call under concurrency (" + hammerTick + "/" + hammerTotal + ")");
            Check(ownedPerThread.Sum() == hammerTotal / hammerEvery,
                "stride hammer: exactly " + (hammerTotal / hammerEvery) + " slots owned under concurrency (got " + ownedPerThread.Sum() + ")");

            // TickClock: the per-entity slot predicate behind the updateTasks
            // mid-band stride and the crowd-collision resolve stagger. The invariant
            // pinned here is COVERAGE under CONSECUTIVE sampling: over any `stride`
            // consecutive counter values, EVERY entityId owns exactly one slot.
            // Production samples once per entity per game tick while the counter
            // steps per UpdateTick invocation (= per frame above the vanilla 20 fps,
            // RESULTS 3k), so consecutive sampling - and this exact coverage - holds
            // at 20 fps; above it, jumps of F = fps/20 between samples narrow the
            // guarantee to gcd(F, stride) = 1 (see TickClock). This test pins the
            // pure predicate; the wiring caveat lives with the clock itself.
            foreach (int strideLen in new[] { 2, 3, 4, 5, 8, 16 })
            {
                bool everyIdOwnsExactlyOncePerWindow = true;
                for (int id = 0; id < 64 && everyIdOwnsExactlyOncePerWindow; id++)
                    for (int t0 = 0; t0 < strideLen && everyIdOwnsExactlyOncePerWindow; t0++)
                    {
                        int owned = 0;
                        for (int k = 0; k < strideLen; k++)
                            if (EfficientServer.Patches.TickClock.OwnsSlot(id, t0 + k, strideLen)) owned++;
                        if (owned != 1) everyIdOwnsExactlyOncePerWindow = false;
                    }
                Check(everyIdOwnsExactlyOncePerWindow,
                    "tick clock slot coverage: every id owns exactly one slot per window of " + strideLen);
            }
            // Liveness seam: consumers (updateTasks mid-stride, crowd-collision
            // striping, path-admission window) fail open to vanilla until the
            // driver prefix has fired at least once, so a MISSING TickClockPatch
            // degrades instead of freezing slots at 0. Alive must start false and
            // latch true on the first Advance - never flip back.
            Check(!EfficientServer.Patches.TickClock.Alive,
                "tick clock liveness: not alive before the first Advance (consumers fail open)");
            EfficientServer.Patches.TickClock.Advance();
            Check(EfficientServer.Patches.TickClock.Alive,
                "tick clock liveness: alive after the first Advance");
            // Advance() steps Ticks by exactly one and OwnsCurrentSlot reads the current
            // index: pin a full cycle so the wrapper cannot drift off the pure
            // predicate it wraps. One advance was already consumed by the liveness
            // pin above, so Ticks reads 1 here and the loop continues from t=2.
            bool cycleHeld = EfficientServer.Patches.TickClock.Ticks == 1;
            for (int t = 2; t <= 12 && cycleHeld; t++)
            {
                EfficientServer.Patches.TickClock.Advance();
                if (EfficientServer.Patches.TickClock.Ticks != t) { cycleHeld = false; break; }
                bool expected = t % 4 == 0; // id 0, stride 4 -> owns ticks 4, 8, 12
                if (EfficientServer.Patches.TickClock.OwnsCurrentSlot(0, 4) != expected) cycleHeld = false;
            }
            Check(cycleHeld, "tick clock Advance/OwnsCurrentSlot track consecutive ticks from the zero seed");
            // Signed-wrap boundary via the uint cast: with id=2 and stride=3 the
            // sums 2+(int.MaxValue-1), 2+int.MaxValue, 2+int.MinValue cross zero as
            // unsigned values 2147483648..50, whose residues mod 3 are 2, 0, 1 -
            // so exactly the MIDDLE call owns its slot. A naive signed modulo
            // would read (-2147483647) % 3 == -1 there and own nothing: the same
            // frozen-id failure mode the cast exists to prevent.
            var wrapT = int.MaxValue - 1;
            Check(!EfficientServer.Patches.TickClock.OwnsSlot(2, wrapT, 3), "tick clock wrap: pre-wrap tick -> no run");
            Check(EfficientServer.Patches.TickClock.OwnsSlot(2, wrapT + 1, 3),
                "tick clock wrap: signed-negative sum still owns its uint slot");
            Check(!EfficientServer.Patches.TickClock.OwnsSlot(2, wrapT + 2, 3), "tick clock wrap: post-wrap tick -> no run");

            // TickIntervalEma deterministic replay. The explicit-timestamp overload is
            // the pure transition function behind BOTH the governor's tier machine and
            // the tick guard's shed decision; driving it here with synthetic tick
            // sequences pins those decisions to their inputs instead of host scheduler
            // jitter. For a constant interval D stepped n times past the 50 ms seed the
            // recurrence has the closed form ema_n = D + (Seed - D) * (31/32)^n, so the
            // expectations below are derived from the spec, not copied from a run.
            var ema = new EfficientServer.Patches.TickIntervalEma();
            Check(ema.Value == 50.0, "tick EMA seeds at the vanilla 50 ms idle interval");
            Check(ema.Advance(100.0) == 50.0, "tick EMA first positive advance only records the baseline");
            double closedForm = 50.0;
            bool closedFormHeld = true;
            for (int i = 2; i <= 640; i++)
            {
                closedForm += (100.0 - closedForm) / 32.0;
                if (ema.Advance(i * 100.0) != closedForm)
                    { closedFormHeld = false; break; }
            }
            Check(closedFormHeld, "tick EMA constant-interval trace matches the recurrence bit-for-bit");
            Check(Math.Abs(ema.Value - 100.0) < 0.05,
                "tick EMA converges toward the sustained interval (got " + ema.Value.ToString("F3", CultureInfo.InvariantCulture) + ")");

            // Replay determinism: the same seeded gap sequence fed to two fresh
            // instances must produce bitwise-identical traces, so a failing simulated
            // run reproduces exactly from its seed.
            var gaps = new List<double>();
            var seqRng = new Random(424242);
            for (int i = 0; i < 2000; i++)
                gaps.Add(40.0 + seqRng.NextDouble() * 80.0); // 40..120 ms mixed load
            var replayA = new EfficientServer.Patches.TickIntervalEma();
            var replayB = new EfficientServer.Patches.TickIntervalEma();
            double clockMs = 0.0, prevA = replayA.Value;
            bool tracesIdentical = true, noOvershoot = true;
            for (int i = 0; i < gaps.Count; i++)
            {
                clockMs += gaps[i];
                double a = replayA.Advance(clockMs);
                if (a != replayB.Advance(clockMs)) { tracesIdentical = false; break; }
                // Hysteresis depends on the smoother never overshooting the current
                // gap: each step closes strictly less than the full distance to the
                // sample (|new - dt| == |old - dt| * 31/32), so the EMA cannot ring.
                // i == 0 is exempt: that first advance only records the baseline
                // (asserted above) and averages no gap yet, so its distance is
                // expected to be unchanged, not shrunk.
                if (i > 0)
                {
                    double distBefore = Math.Abs(prevA - gaps[i]);
                    double distAfter = Math.Abs(a - gaps[i]);
                    if (!(distAfter < distBefore || distAfter == 0.0)) { noOvershoot = false; break; }
                }
                prevA = a;
            }
            Check(tracesIdentical, "tick EMA same-seed replay is bitwise identical across instances");
            Check(noOvershoot, "tick EMA never overshoots the sampled interval (hysteresis precondition)");

            // Decision-input tie-in with REAL defaults: how many sustained slow ticks
            // until the EMA the governor reads crosses OverBudgetMs must be derivable
            // ahead of time; assert the instance crosses on exactly that advance.
            var govCfg = LoadTemp("{}").Governor;
            var crossEma = new EfficientServer.Patches.TickIntervalEma();
            // The instance records its baseline on the first advance (n=1) and starts
            // averaging from the second, so the prediction runs the recurrence from
            // n=2 to mirror that seeding.
            int expectedCross = -1;
            double simMs = 50.0;
            for (int n = 2; n <= 100000; n++)
            {
                simMs += (120.0 - simMs) / 32.0;
                if (expectedCross < 0 && simMs > govCfg.OverBudgetMs) { expectedCross = n; break; }
            }
            int actualCross = -1;
            for (int n = 1; n <= 100000; n++)
            {
                if (crossEma.Advance(n * 120.0) > govCfg.OverBudgetMs) { actualCross = n; break; }
            }
            Check(actualCross == expectedCross && actualCross > 0,
                "tick EMA crosses OverBudgetMs on advance " + actualCross + " (predicted " + expectedCross + ")");

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

            // Discovery + IO-failure branches of the load path itself, which no
            // string-level fixture reaches (they all go through LoadTemp):
            CheckDefaultPathDiscovery();
            CheckUnreadableFileFailSoft();

            // Fuzz: the config file is the mod's untrusted-input surface, so two
            // deterministic targets hammer Load + FindUnknownKeys (Fuzz.cs):
            // structure-aware mutations of the default config reach Normalize's
            // value paths that character soup never parses into, and garbage
            // text covers truncation, deep nesting, bad escapes and raw bytes.
            // Both assert the full post-Normalize invariant table, so a
            // wrong-but-non-crashing load fails the run instead of hiding.
            ConfigFuzz.StructureAware(Check, LoadTemp);
            ConfigFuzz.GarbageText(Check, LoadTemp, bytes => LoadTempFile(WriteTempBytes(bytes)));

            // Dedicated-only gate (ShouldRunFor): disabled config never runs.
            Check(!ServerPerfConfig.ShouldRunFor(false, true, true, true), "active=false -> no run");
            Check(!ServerPerfConfig.ShouldRunFor(true, false, true, true), "enabled=false -> no run");
            // DedicatedOnly=false runs anywhere, host confirmed or not: the
            // operator explicitly opted out of the gate, so even a detected
            // client must not silently deactivate the mod.
            Check(ServerPerfConfig.ShouldRunFor(true, true, false, null), "dedicatedOnly=false -> run (host unknown)");
            Check(ServerPerfConfig.ShouldRunFor(true, true, false, true), "dedicatedOnly=false -> run (host any)");
            Check(ServerPerfConfig.ShouldRunFor(true, true, false, false), "dedicatedOnly=false -> run (confirmed client)");
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
            Check(fa.FeatureActive("ExplosionParticles"), "default ExplosionParticles -> active (ships on)");
            Check(fa.FeatureActive("FastSend"), "default FastSend -> active (opt-out feature)");
            Check(fa.FeatureActive("ClientListSnapshot"), "default ClientListSnapshot -> active (race fix ships on)");
            Check(fa.FeatureActive("Governor"), "default Governor -> active (inert when healthy)");
            // Both levers of the Gc AND-gate ship true, so a default flip of either
            // would silently deactivate the forced-collect guard; every other
            // shipping-on feature above has its default pinned, so pin this one too.
            Check(fa.FeatureActive("Gc"), "default Gc -> active (Enabled and SkipForcedCollect both ship true)");
            var govOff = LoadTemp("{\"Governor\":{\"Enabled\":false}}");
            Check(!govOff.FeatureActive("Governor"), "Governor disabled by config -> inactive");
            Check(!fa.FeatureActive("AnimatorLod"), "default AnimatorLod -> inactive (Enabled=false default)");
            Check(!fa.FeatureActive("CrowdCollisionLod"), "default CrowdCollisionLod -> inactive (Enabled=false default)");
            Check(!fa.FeatureActive("UnknownFeature"), "unknown feature key -> inactive");
            // Config-driven features flip with their knobs.
            var faOn = LoadTemp(
                "{\"AiLod\":{\"Enabled\":true},\"TickGuard\":{\"Enabled\":true}," +
                "\"Pathfinding\":{\"GraphUpdateEveryTicks\":8,\"MoveRescanThresholdSq\":400," +
                "\"MaxPathEnqueuesPerTick\":64,\"PoolInitScanNodes\":true}," +
                "\"Network\":{\"EntityDistributionEveryTicks\":4,\"ClientListSnapshot\":true}," +
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
            Check(faOn.FeatureActive("ClientListSnapshot"), "ClientListSnapshot true -> active");
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
                "\"Network\":{\"FastSingleTargetSend\":false,\"EntityDistributionEveryTicks\":1," +
                "\"ClientListSnapshot\":false}," +
                "\"WorldTransfer\":{\"ChunkPackagesPerObserverPerTick\":3}," +
                "\"Server\":{\"TargetFps\":0},\"SkipOnDedicated\":{\"ExplosionParticles\":false}}");
            Check(!faOff.FeatureActive("GraphThrottle"), "GraphUpdateEveryTicks 1 -> inactive");
            Check(!faOff.FeatureActive("MoveThreshold"), "MoveRescanThresholdSq 100 -> inactive");
            Check(!faOff.FeatureActive("PathAdmission"), "no path admission knobs -> inactive");
            Check(!faOff.FeatureActive("InitScanPool"), "PoolInitScanNodes false -> inactive");
            Check(!faOff.FeatureActive("FastSend"), "FastSingleTargetSend false -> inactive");
            Check(!faOff.FeatureActive("ClientListSnapshot"), "ClientListSnapshot false -> inactive");
            Check(!faOff.FeatureActive("EntityDistributionStride"), "EntityDistributionEveryTicks 1 -> inactive");
            Check(!faOff.FeatureActive("ChunkSendThrottle"), "ChunkPackagesPerObserverPerTick 3 (vanilla) -> inactive");
            Check(!faOff.FeatureActive("TargetFps"), "TargetFps 0 -> inactive");
            Check(!faOff.FeatureActive("ExplosionParticles"), "ExplosionParticles false -> inactive");
            // Admission is an OR of two independent levers; faOn above only
            // exercises the enqueue-cap side, so pin the drop-distance side too.
            var dropOnly = LoadTemp("{\"Pathfinding\":{\"DropPathWhenFarDistSq\":2500}}");
            Check(dropOnly.FeatureActive("PathAdmission"),
                "DropPathWhenFarDistSq 2500 with cap 0 -> PathAdmission active");

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

            // Clamp-table boundary sweep. The fuzz targets only prove every knob
            // lands INSIDE its range; these fixtures pin the exact documented
            // ENDPOINTS on both sides: a bound that quietly tightens or loosens by
            // one must fail here, not on an operator server. Endpoints are legal
            // tunes, so each fixture must also load with ZERO "config corrected"
            // warnings. Sibling-linked knobs stay mutually consistent (HealthyMs <=
            // OverBudgetMs-5, scales <= parent, ShedAboveMs over the governor band).
            EsLog.Warnings.Clear();
            var atMax = LoadTemp(
                "{\"AiLod\":{\"FullAiDistSq\":1000000,\"MediumAiDistSq\":1000000,\"SkipTasksFarDistSq\":4000000," +
                "\"MidTickStride\":20,\"FullScale\":1,\"MediumScale\":1,\"FarScale\":1}," +
                "\"DynamicMesh\":{\"PlayerAreaChunkBuffer\":64,\"MaxRegionLoadMsPerFrame\":1000,\"MaxActiveSyncs\":128}," +
                "\"Pathfinding\":{\"GraphUpdateEveryTicks\":200,\"MoveRescanThresholdSq\":10000," +
                "\"MaxPathEnqueuesPerTick\":2000,\"DropPathWhenFarDistSq\":4000000}," +
                "\"WorldTransfer\":{\"ChunkPackagesPerObserverPerTick\":32}," +
                "\"Network\":{\"EntityDistributionEveryTicks\":4}," +
                "\"CrowdCollisionLod\":{\"ResolveEveryNTicks\":16}," +
                "\"AnimatorLod\":{\"FullRateDistSq\":1000000,\"FarStride\":10}," +
                "\"Server\":{\"TargetFps\":120,\"JobWorkerCount\":64}," +
                "\"Governor\":{\"OverBudgetMs\":500,\"HealthyMs\":495,\"EmergencyOverMs\":1000," +
                "\"WindowTicks\":6000,\"CooldownTicks\":36000}," +
                "\"TickGuard\":{\"ShedAboveMs\":1000,\"WindowTicks\":6000,\"ShedBatch\":100," +
                "\"CooldownTicks\":36000,\"MinEnemiesKept\":10000}," +
                "\"Gc\":{\"SafetyCollectAboveMB\":1048576,\"SafetyCollectRamFraction\":0.95," +
                "\"IncrementalPauseTargetMs\":10000}," +
                "\"Diagnostics\":{\"WarmupSeconds\":3600,\"GrowSeconds\":7200}}");
            Check(atMax.AiLod.FullAiDistSq == 1000000f && atMax.AiLod.MediumAiDistSq == 1000000f
                && atMax.AiLod.SkipTasksFarDistSq == 4000000f && atMax.AiLod.MidTickStride == 20
                && atMax.AiLod.FullScale == 1f && atMax.AiLod.MediumScale == 1f && atMax.AiLod.FarScale == 1f,
                "AiLod upper endpoints preserved verbatim");
            Check(atMax.DynamicMesh.PlayerAreaChunkBuffer == 64 && atMax.DynamicMesh.MaxRegionLoadMsPerFrame == 1000
                && atMax.DynamicMesh.MaxActiveSyncs == 128, "DynamicMesh upper endpoints preserved verbatim");
            Check(atMax.Pathfinding.GraphUpdateEveryTicks == 200 && atMax.Pathfinding.MoveRescanThresholdSq == 10000f
                && atMax.Pathfinding.MaxPathEnqueuesPerTick == 2000 && atMax.Pathfinding.DropPathWhenFarDistSq == 4000000f,
                "Pathfinding upper endpoints preserved verbatim");
            Check(atMax.WorldTransfer.ChunkPackagesPerObserverPerTick == 32
                && atMax.Network.EntityDistributionEveryTicks == 4
                && atMax.CrowdCollisionLod.ResolveEveryNTicks == 16,
                "transfer/network/crowd upper endpoints preserved verbatim");
            Check(atMax.AnimatorLod.FullRateDistSq == 1000000f && atMax.AnimatorLod.FarStride == 10,
                "AnimatorLod upper endpoints preserved verbatim");
            Check(atMax.Server.TargetFps == 120 && atMax.Server.JobWorkerCount == 64,
                "Server upper endpoints preserved verbatim");
            Check(atMax.Governor.OverBudgetMs == 500f && atMax.Governor.HealthyMs == 495f
                && atMax.Governor.EmergencyOverMs == 1000f && atMax.Governor.WindowTicks == 6000
                && atMax.Governor.CooldownTicks == 36000, "Governor upper endpoints preserved verbatim");
            Check(atMax.TickGuard.ShedAboveMs == 1000f && atMax.TickGuard.WindowTicks == 6000
                && atMax.TickGuard.ShedBatch == 100 && atMax.TickGuard.CooldownTicks == 36000
                && atMax.TickGuard.MinEnemiesKept == 10000, "TickGuard upper endpoints preserved verbatim");
            Check(atMax.Gc.SafetyCollectAboveMB == 1048576 && atMax.Gc.SafetyCollectRamFraction == 0.95f
                && atMax.Gc.IncrementalPauseTargetMs == 10000, "Gc upper endpoints preserved verbatim");
            Check(atMax.Diagnostics.WarmupSeconds == 3600 && atMax.Diagnostics.GrowSeconds == 7200,
                "Diagnostics upper endpoints preserved verbatim");
            Check(EsLog.Warnings.Count == 0, "upper endpoints load without any 'config corrected' warning");

            EsLog.Warnings.Clear();
            var atMin = LoadTemp(
                "{\"AiLod\":{\"FullAiDistSq\":1,\"MediumAiDistSq\":1,\"SkipTasksFarDistSq\":1," +
                "\"MidTickStride\":1,\"FullScale\":0,\"MediumScale\":0,\"FarScale\":0}," +
                "\"DynamicMesh\":{\"PlayerAreaChunkBuffer\":0,\"MaxRegionLoadMsPerFrame\":1,\"MaxActiveSyncs\":1}," +
                "\"Pathfinding\":{\"GraphUpdateEveryTicks\":1,\"MoveRescanThresholdSq\":100," +
                "\"MaxPathEnqueuesPerTick\":0,\"DropPathWhenFarDistSq\":0}," +
                "\"WorldTransfer\":{\"ChunkPackagesPerObserverPerTick\":1}," +
                "\"Network\":{\"EntityDistributionEveryTicks\":1}," +
                "\"CrowdCollisionLod\":{\"ResolveEveryNTicks\":1}," +
                "\"AnimatorLod\":{\"FullRateDistSq\":100,\"FarStride\":1}," +
                "\"Server\":{\"TargetFps\":0,\"JobWorkerCount\":0}," +
                "\"Governor\":{\"OverBudgetMs\":20,\"HealthyMs\":10,\"EmergencyOverMs\":25," +
                "\"WindowTicks\":20,\"CooldownTicks\":0}," +
                "\"TickGuard\":{\"ShedAboveMs\":60,\"WindowTicks\":20,\"ShedBatch\":1," +
                "\"CooldownTicks\":20,\"MinEnemiesKept\":0}," +
                "\"Gc\":{\"SafetyCollectAboveMB\":0,\"SafetyCollectRamFraction\":0," +
                "\"IncrementalPauseTargetMs\":0}," +
                "\"Diagnostics\":{\"WarmupSeconds\":0,\"GrowSeconds\":1}}");
            Check(atMin.AiLod.FullAiDistSq == 1f && atMin.AiLod.MediumAiDistSq == 1f
                && atMin.AiLod.SkipTasksFarDistSq == 1f && atMin.AiLod.MidTickStride == 1
                && atMin.AiLod.FullScale == 0f && atMin.AiLod.MediumScale == 0f && atMin.AiLod.FarScale == 0f,
                "AiLod lower endpoints preserved verbatim");
            Check(atMin.DynamicMesh.PlayerAreaChunkBuffer == 0 && atMin.DynamicMesh.MaxRegionLoadMsPerFrame == 1
                && atMin.DynamicMesh.MaxActiveSyncs == 1, "DynamicMesh lower endpoints preserved verbatim");
            Check(atMin.Pathfinding.GraphUpdateEveryTicks == 1 && atMin.Pathfinding.MoveRescanThresholdSq == 100f
                && atMin.Pathfinding.MaxPathEnqueuesPerTick == 0 && atMin.Pathfinding.DropPathWhenFarDistSq == 0f,
                "Pathfinding lower endpoints preserved verbatim");
            Check(atMin.WorldTransfer.ChunkPackagesPerObserverPerTick == 1
                && atMin.Network.EntityDistributionEveryTicks == 1
                && atMin.CrowdCollisionLod.ResolveEveryNTicks == 1,
                "transfer/network/crowd lower endpoints preserved verbatim");
            Check(atMin.AnimatorLod.FullRateDistSq == 100f && atMin.AnimatorLod.FarStride == 1,
                "AnimatorLod lower endpoints preserved verbatim");
            Check(atMin.Server.TargetFps == 0 && atMin.Server.JobWorkerCount == 0,
                "Server lower endpoints preserved verbatim");
            Check(atMin.Governor.OverBudgetMs == 20f && atMin.Governor.HealthyMs == 10f
                && atMin.Governor.EmergencyOverMs == 25f && atMin.Governor.WindowTicks == 20
                && atMin.Governor.CooldownTicks == 0, "Governor lower endpoints preserved verbatim");
            Check(atMin.TickGuard.ShedAboveMs == 60f && atMin.TickGuard.WindowTicks == 20
                && atMin.TickGuard.ShedBatch == 1 && atMin.TickGuard.CooldownTicks == 20
                && atMin.TickGuard.MinEnemiesKept == 0, "TickGuard lower endpoints preserved verbatim");
            Check(atMin.Gc.SafetyCollectAboveMB == 0 && atMin.Gc.SafetyCollectRamFraction == 0f
                && atMin.Gc.IncrementalPauseTargetMs == 0, "Gc lower endpoints preserved verbatim");
            Check(atMin.Diagnostics.WarmupSeconds == 0 && atMin.Diagnostics.GrowSeconds == 1,
                "Diagnostics lower endpoints preserved verbatim");
            Check(EsLog.Warnings.Count == 0, "lower endpoints load without any 'config corrected' warning");

            // Idempotency: re-normalizing an already-normalized config must be
            // silent and value-stable. Every FiniteRange/IntRange fallback is
            // clamped into its own range precisely so this holds; a clamp whose
            // fallback landed outside its (possibly sibling-shifted) bounds
            // would spam "config corrected" on every reload AND drift the value
            // each time - `es reload` re-runs this exact path.
            var corrected = LoadTempTracked(
                "{\"Pathfinding\":{\"GraphUpdateEveryTicks\":1000000}," +
                "\"Governor\":{\"OverBudgetMs\":20,\"HealthyMs\":NaN}}");
            Check(corrected.Pathfinding.GraphUpdateEveryTicks == 200 && corrected.Governor.HealthyMs == 15f,
                "idempotency setup: first normalize corrected both out-of-range knobs");
            Check(EsLog.Warnings.Count > 0, "idempotency setup: first normalize logged the corrections");
            EsLog.Warnings.Clear();
            corrected.Normalize();
            Check(EsLog.Warnings.Count == 0, "re-normalize is silent (no repeated 'config corrected' warnings)");
            Check(corrected.Pathfinding.GraphUpdateEveryTicks == 200 && corrected.Governor.HealthyMs == 15f,
                "re-normalize keeps the already-corrected values stable");

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
