using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EfficientServer.Tests
{
    // Fuzz targets for the one untrusted-input surface this mod ships: the JSON
    // config loader (ServerPerfConfig.Load + FindUnknownKeys). The file sits in
    // Mods/EfficientServer/Config/, is hand-editable, and travels with mod
    // packages, so it is parsed as hostile input on every dedicated start.
    // Contract under fuzz: Load never throws (fail-soft to defaults) and
    // Normalize lands EVERY knob inside its documented clamp on ANY input.
    //
    // Two deterministic (fixed-seed) targets, so failures reproduce under
    // `make test` with no libFuzzer host:
    //   StructureAware: schema-driven mutations of the serialized default
    //     config. Pure character soup almost never parses, so hostile VALUES
    //     (NaN, 1e999, wrong types) would otherwise never reach Normalize.
    //     Single-knob rounds are followed by COMBINED rounds (1..4 knobs at
    //     once plus whole-section type swaps): sibling-linked clamps
    //     (HealthyMs vs OverBudgetMs, MediumScale vs FullScale, ShedAboveMs
    //     over the governor band) can only misbehave when both sides of the
    //     link are hostile together, and the per-section null-backfill lines
    //     must be reachable by fuzzing, not only by fixtures.
    //   GarbageText: truncations of a real config, deep nesting, duplicate
    //     keys, lone surrogates, invalid UTF-8 and BOM-prefixed junk through
    //     both the string path and the raw-byte read path, plus valid-JSON
    //     documents whose SHAPE is wrong for a config (top-level scalars and
    //     arrays, trailing content, arrays where a section object belongs).
    //
    // Every failure line embeds the offending JSON, so an artifact becomes a
    // repro by pasting it as a LoadTemp fixture next to Main.
    internal static class ConfigFuzz
    {
        public delegate void CheckFn(bool cond, string what);

        const int StructureIterations = 2000;
        const int CombinedIterations = 1200;
        const int GarbageIterations = 1200;

        // Alphabet kept from the original hand-rolled soup target: JSON syntax
        // characters plus the letter set of true/false/null/NaN spelled apart.
        const string SoupChars = "{}[]\":,.0123456789abcTruefalsngP_ \t\n";

        public static void StructureAware(CheckFn check, Func<string, ServerPerfConfig> load)
        {
            var seed = JObject.FromObject(new ServerPerfConfig());
            var leaves = ReflectedLeaves();
            check(leaves.Count >= 40, "structure fuzz: reflected leaf set covers the knob surface");
            foreach (var leaf in leaves)
                check(NodeAt(seed, leaf) != null,
                    "structure fuzz: leaf '" + string.Join(".", leaf) + "' present in serialized defaults");

            var rng = new Random(20260823);
            for (int i = 0; i < StructureIterations; i++)
            {
                // The warning sink grows per corrected knob; clear per iteration
                // so a long fuzz run stays O(1) memory like the fixtures.
                EsLog.Warnings.Clear();
                var root = (JObject)seed.DeepClone();
                Mutate(root, leaves[rng.Next(leaves.Count)], rng);
                string json = JsonConvert.SerializeObject(root);
                ServerPerfConfig loaded;
                try { loaded = load(json); }
                catch (Exception ex)
                {
                    check(false, "structure fuzz iter " + i + ": Load threw "
                        + ex.GetType().Name + " for: " + json);
                    continue;
                }
                string? bad = Violations(loaded);
                check(bad == null, "structure fuzz iter " + i + ": " + bad + " for: " + json);
            }

            // Combined rounds: several hostile knobs at once. Normalize resolves
            // sibling-linked ranges in a fixed order with clamped fallbacks, so
            // the failure mode to hunt here is a fallback landing OUTSIDE a range
            // a sibling shifted (e.g. HealthyMs's max moves with OverBudgetMs).
            var sections = ReflectedSections();
            check(sections.Count >= 10,
                "structure fuzz: reflected section set covers the knob groups");
            for (int i = 0; i < CombinedIterations; i++)
            {
                EsLog.Warnings.Clear();
                var root = (JObject)seed.DeepClone();
                int hits = 1 + rng.Next(4);
                var done = new HashSet<int>();
                for (int m = 0; m < hits; m++)
                {
                    int li = rng.Next(leaves.Count);
                    if (!done.Add(li)) continue;
                    Mutate(root, leaves[li], rng);
                }
                if (rng.Next(4) == 0)
                {
                    // Whole-section type swap: null exercises the per-section
                    // backfill line, array/scalar the fail-soft conversion error.
                    string sect = sections[rng.Next(sections.Count)];
                    root[sect] = rng.Next(4) switch
                    {
                        0 => JValue.CreateNull(),
                        1 => (JToken)new JArray { 1 },
                        2 => rng.Next(2) == 0 ? (JToken)"abc" : (JToken)true,
                        _ => new JValue(-1),
                    };
                }
                string json = JsonConvert.SerializeObject(root);
                ServerPerfConfig loaded;
                try { loaded = load(json); }
                catch (Exception ex)
                {
                    check(false, "combined fuzz iter " + i + ": Load threw "
                        + ex.GetType().Name + " for: " + json);
                    continue;
                }
                string? badCombined = Violations(loaded);
                check(badCombined == null,
                    "combined fuzz iter " + i + ": " + badCombined + " for: " + json);
            }
        }

        public static void GarbageText(
            CheckFn check,
            Func<string, ServerPerfConfig> loadString,
            Func<byte[], ServerPerfConfig> loadBytes)
        {
            string defaultJson = JsonConvert.SerializeObject(new ServerPerfConfig());
            var rng = new Random(777001);

            for (int i = 0; i < GarbageIterations; i++)
            {
                EsLog.Warnings.Clear();
                string json = GarbageCase(rng, defaultJson);
                RunThroughLoadAndKeyScan(check, "garbage iter " + i, json, loadString);
            }

            // Valid JSON whose document shape is wrong for a config object:
            // top-level scalars/arrays, trailing content, an array where a
            // section object belongs, whitespace-only text. Truncation sweeps
            // and character soup almost never synthesize these exactly, yet
            // each takes a distinct failure branch (value-conversion error,
            // reader error, silent null deserialization) that must stay
            // fail-soft to defaults with FindUnknownKeys still returning a list.
            string[] shapeCases =
            {
                "[]",
                "[{\"Enabled\":false}]",
                "\"text\"",
                "-42",
                "3.5e2",
                "true",
                "{\"AiLod\":[1,2]}",
                "{}{}",
                "   ",
            };
            foreach (string shape in shapeCases)
            {
                EsLog.Warnings.Clear();
                RunThroughLoadAndKeyScan(check, "shape '" + shape + "'", shape, loadString);
            }

            // Raw-byte cases hit File.ReadAllText(path, UTF8), which string-level
            // cases never cross: invalid sequences become U+FFFD replacements, a
            // BOM is stripped, and UTF-16LE without BOM turns to mojibake. All
            // three must end in defaults or a clean parse, never an exception.
            byte[][] byteCases =
            {
                new byte[] { 0xEF, 0xBB, 0xBF }, // BOM alone -> empty -> defaults
                Concat(Encoding.UTF8.GetBytes("{\"Enabled\":"), new byte[] { 0xC0, 0xAF }, Encoding.UTF8.GetBytes("}")),
                Concat(Encoding.UTF8.GetBytes("{\"Gc\":{"), new byte[] { 0x00 }, Encoding.UTF8.GetBytes("}}")),
                Concat(new byte[] { 0xEF, 0xBB, 0xBF }, Encoding.UTF8.GetBytes(defaultJson)),
                Encoding.Unicode.GetBytes(defaultJson), // UTF-16LE, no BOM
                new byte[] { 0xFF, 0xFE, 0x00 }, // stray UTF-16LE BOM prefix + NUL
                new byte[0], // empty file
            };
            for (int i = 0; i < byteCases.Length; i++)
            {
                EsLog.Warnings.Clear();
                ServerPerfConfig loaded;
                try { loaded = loadBytes(byteCases[i]); }
                catch (Exception ex)
                {
                    check(false, "byte fuzz case " + i + ": Load threw " + ex.GetType().Name);
                    continue;
                }
                string? bad = Violations(loaded);
                check(bad == null, "byte fuzz case " + i + ": " + bad);
            }

            // Truncated-at-every-offset sweep over a real config: the classic
            // corruption shape (crash mid-write). Deterministic, one pass.
            for (int cut = 0; cut <= defaultJson.Length; cut += 37)
            {
                EsLog.Warnings.Clear();
                RunThroughLoadAndKeyScan(check, "truncate@" + cut, defaultJson.Substring(0, cut), loadString);
            }
        }

        static void RunThroughLoadAndKeyScan(
            CheckFn check, string label, string json, Func<string, ServerPerfConfig> load)
        {
            ServerPerfConfig loaded;
            try { loaded = load(json); }
            catch (Exception ex)
            {
                check(false, label + ": Load threw " + ex.GetType().Name + " for: " + json);
                return;
            }
            // Whatever bound, the loaded result must satisfy the clamp contract.
            string? bad = Violations(loaded);
            check(bad == null, label + ": " + bad + " for: " + json);
            // FindUnknownKeys re-parses the same hostile text through its own
            // walk; it promises to return a list (possibly empty), never throw.
            List<string> unknown;
            try { unknown = ServerPerfConfig.FindUnknownKeys(json); }
            catch (Exception ex)
            {
                check(false, label + ": FindUnknownKeys threw " + ex.GetType().Name + " for: " + json);
                return;
            }
            check(unknown != null, label + ": FindUnknownKeys returned null for: " + json);
        }

        static string GarbageCase(Random rng, string defaultJson)
        {
            switch (rng.Next(6))
            {
                case 0: // truncation of a real config at an arbitrary offset
                    return defaultJson.Substring(0, rng.Next(defaultJson.Length + 1));
                case 1: // nesting past Newtonsoft's MaxDepth (64): must die inside
                        // Load's catch as JsonReaderException, not escape or SOE
                    int depth = 65 + rng.Next(400);
                    return "{\"AiLod\":" + new string('[', depth) + new string(']', depth) + "}";
                case 2: // malformed escapes and control bytes
                    return "{\"Enabled\":tr" + (char)rng.Next(1, 32)
                        + "ue,\"x\":\"\\ud800\",\"y\":\"\\q\"}";
                case 3: // numeric extremes at a real numeric knob
                    return "{\"Pathfinding\":{\"MoveRescanThresholdSq\":" + ExtremeNumber(rng) + "}}";
                case 4: // character soup (retained from the original fuzz target)
                    var sb = new StringBuilder();
                    int len = rng.Next(0, 120);
                    for (int j = 0; j < len; j++) sb.Append(SoupChars[rng.Next(SoupChars.Length)]);
                    return sb.ToString();
                default: // duplicate keys across case variants: binding is either
                         // last-wins or a caught error; both must stay in range
                    return "{\"Enabled\":false,\"enabled\":true,\"ENABLED\":false,"
                        + "\"Pathfinding\":{\"GraphUpdateEveryTicks\":9,"
                        + "\"graphupdateeveryticks\":2}}";
            }
        }

        static string ExtremeNumber(Random rng)
        {
            switch (rng.Next(6))
            {
                case 0: return "NaN";
                case 1: return "Infinity";
                case 2: return "-Infinity";
                case 3: return "1e999";
                case 4: return int.MinValue.ToString(CultureInfo.InvariantCulture);
                // Invariant: a comma-decimal host culture would emit "1,5E+30",
                // which is not a JSON number and silently turns this case into
                // a parse-error fixture instead of an extreme-value one.
                default: return (rng.NextDouble() * 8e30 - 4e30).ToString("R", CultureInfo.InvariantCulture);
            }
        }

        // Dotted leaf paths derived from the config schema itself, so newly added
        // knobs join the fuzz corpus automatically instead of drifting stale.
        static List<string[]> ReflectedLeaves()
        {
            var leaves = new List<string[]>();
            foreach (var top in typeof(ServerPerfConfig).GetProperties())
            {
                if (top.PropertyType == typeof(bool) || top.PropertyType == typeof(int))
                {
                    leaves.Add(new[] { top.Name });
                    continue;
                }
                if (!top.PropertyType.IsClass
                    || top.PropertyType.Namespace != typeof(ServerPerfConfig).Namespace)
                    continue;
                foreach (var sub in top.PropertyType.GetProperties())
                    leaves.Add(new[] { top.Name, sub.Name });
            }
            return leaves;
        }

        // Top-level knob-group names, from the schema like ReflectedLeaves, so a
        // future section joins the combined fuzz automatically.
        static List<string> ReflectedSections()
        {
            var names = new List<string>();
            foreach (var top in typeof(ServerPerfConfig).GetProperties())
                if (top.PropertyType.IsClass
                    && top.PropertyType.Namespace == typeof(ServerPerfConfig).Namespace)
                    names.Add(top.Name);
            return names;
        }

        static JToken NodeAt(JObject root, string[] path)
        {
            JToken? cur = root;
            foreach (string seg in path)
            {
                cur = cur[seg];
                if (cur == null) return null!;
            }
            return cur!;
        }

        static void Mutate(JObject root, string[] leaf, Random rng)
        {
            if (leaf.Length == 1)
            {
                // Top-level scalar: hostile types here force the whole-document
                // fail-soft path (defaults) rather than per-knob correction.
                switch (rng.Next(3))
                {
                    case 0: root[leaf[0]] = rng.Next(2) == 0; break;
                    case 1: root[leaf[0]] = rng.Next(2) == 0 ? (JValue)"yes" : (JValue)1; break;
                    default: root[leaf[0]] = JValue.CreateNull(); break;
                }
                return;
            }
            if (!(root[leaf[0]] is JObject section)) return;
            string key = leaf[1];
            switch (rng.Next(7))
            {
                case 0: section[key] = rng.Next(2) == 0 ? int.MinValue : int.MaxValue; break;
                case 1: section[key] = rng.NextDouble() * 4e30 - 2e30; break;
                case 2: section[key] = double.NaN; break;
                case 3: section[key] = rng.Next(2) == 0 ? float.PositiveInfinity : float.NegativeInfinity; break;
                case 4: // structural type swap: array/object/null where a scalar belongs
                    section[key] = rng.Next(3) switch
                    {
                        0 => JValue.CreateNull(),
                        1 => (JToken)new JArray(),
                        _ => new JObject(),
                    };
                    break;
                case 5: // strings that coerce badly: text, negative zero text, overflow
                    section[key] = rng.Next(3) switch
                    {
                        0 => (JValue)"abc",
                        1 => (JValue)"-0",
                        _ => (JValue)"1e999",
                    };
                    break;
                default: // typo'd twin of a real key: must be named, never bound
                    section[key + "X"] = 123456;
                    break;
            }
        }

        // Post-Normalize contract, mirrored from ServerPerfConfig.Normalize: every
        // bound here is also enforced there, so ANY successfully loaded config -
        // fuzzed, corrupted, or hostile - must satisfy all of them simultaneously.
        // Returns the first violation description, or null when clean.
        static string? Violations(ServerPerfConfig c)
        {
            var v = new List<string>();
            void I(int actual, int min, int max, string name)
            {
                if (actual < min || actual > max) v.Add(name + "=" + actual + " outside [" + min + "," + max + "]");
            }
            void F(float actual, float min, float max, string name)
            {
                if (float.IsNaN(actual) || float.IsInfinity(actual))
                    v.Add(name + " not finite");
                else if (actual < min || actual > max)
                    v.Add(name + "=" + actual + " outside [" + min + "," + max + "]");
            }
            void NN(object o, string name) { if (o == null) v.Add(name + " null"); }

            NN(c.AiLod, "AiLod"); NN(c.SkipOnDedicated, "SkipOnDedicated"); NN(c.DynamicMesh, "DynamicMesh");
            NN(c.Gc, "Gc"); NN(c.Pathfinding, "Pathfinding"); NN(c.Network, "Network");
            NN(c.WorldTransfer, "WorldTransfer"); NN(c.Server, "Server"); NN(c.AnimatorLod, "AnimatorLod");
            NN(c.CrowdCollisionLod, "CrowdCollisionLod"); NN(c.Governor, "Governor");
            NN(c.TickGuard, "TickGuard"); NN(c.Diagnostics, "Diagnostics");
            if (v.Count > 0) return Join(v);

            F(c.AiLod.FullAiDistSq, 1f, 1000000f, "AiLod.FullAiDistSq");
            F(c.AiLod.MediumAiDistSq, c.AiLod.FullAiDistSq, 1000000f, "AiLod.MediumAiDistSq");
            F(c.AiLod.SkipTasksFarDistSq, c.AiLod.MediumAiDistSq, 4000000f, "AiLod.SkipTasksFarDistSq");
            I(c.AiLod.MidTickStride, 1, 20, "AiLod.MidTickStride");
            F(c.AiLod.FullScale, 0f, 1f, "AiLod.FullScale");
            F(c.AiLod.MediumScale, 0f, c.AiLod.FullScale, "AiLod.MediumScale");
            F(c.AiLod.FarScale, 0f, c.AiLod.MediumScale, "AiLod.FarScale");

            I(c.DynamicMesh.PlayerAreaChunkBuffer, 0, 64, "DynamicMesh.PlayerAreaChunkBuffer");
            I(c.DynamicMesh.MaxRegionLoadMsPerFrame, 1, 1000, "DynamicMesh.MaxRegionLoadMsPerFrame");
            I(c.DynamicMesh.MaxActiveSyncs, 1, 128, "DynamicMesh.MaxActiveSyncs");

            I(c.Pathfinding.GraphUpdateEveryTicks, 1, GovernorTiers.GraphUpdateMax, "Pathfinding.GraphUpdateEveryTicks");
            F(c.Pathfinding.MoveRescanThresholdSq, 100f, 10000f, "Pathfinding.MoveRescanThresholdSq");
            I(c.Pathfinding.MaxPathEnqueuesPerTick, 0, 2000, "Pathfinding.MaxPathEnqueuesPerTick");
            F(c.Pathfinding.DropPathWhenFarDistSq, 0f, 4000000f, "Pathfinding.DropPathWhenFarDistSq");

            I(c.WorldTransfer.ChunkPackagesPerObserverPerTick, 1, 32, "WorldTransfer.ChunkPackagesPerObserverPerTick");
            I(c.Network.EntityDistributionEveryTicks, 1, GovernorTiers.EntityStrideMax, "Network.EntityDistributionEveryTicks");

            I(c.CrowdCollisionLod.ResolveEveryNTicks, 1, 16, "CrowdCollisionLod.ResolveEveryNTicks");
            F(c.AnimatorLod.FullRateDistSq, 100f, 1000000f, "AnimatorLod.FullRateDistSq");
            I(c.AnimatorLod.FarStride, 1, 10, "AnimatorLod.FarStride");
            I(c.Server.TargetFps, 0, 120, "Server.TargetFps");
            I(c.Server.JobWorkerCount, 0, 64, "Server.JobWorkerCount");

            F(c.Governor.OverBudgetMs, 20f, 500f, "Governor.OverBudgetMs");
            F(c.Governor.HealthyMs, 10f, c.Governor.OverBudgetMs - 5f, "Governor.HealthyMs");
            F(c.Governor.EmergencyOverMs, c.Governor.OverBudgetMs + 5f, 1000f, "Governor.EmergencyOverMs");
            I(c.Governor.WindowTicks, 20, 6000, "Governor.WindowTicks");
            I(c.Governor.CooldownTicks, 0, 36000, "Governor.CooldownTicks");

            F(c.TickGuard.ShedAboveMs, Math.Max(60f, c.Governor.OverBudgetMs + 5f), 1000f, "TickGuard.ShedAboveMs");
            I(c.TickGuard.WindowTicks, 20, 6000, "TickGuard.WindowTicks");
            I(c.TickGuard.ShedBatch, 1, 100, "TickGuard.ShedBatch");
            I(c.TickGuard.CooldownTicks, 20, 36000, "TickGuard.CooldownTicks");
            I(c.TickGuard.MinEnemiesKept, 0, 10000, "TickGuard.MinEnemiesKept");

            I(c.Gc.SafetyCollectAboveMB, 0, 1048576, "Gc.SafetyCollectAboveMB");
            F(c.Gc.SafetyCollectRamFraction, 0f, 0.95f, "Gc.SafetyCollectRamFraction");
            I(c.Gc.IncrementalPauseTargetMs, 0, 10000, "Gc.IncrementalPauseTargetMs");
            I(c.Diagnostics.WarmupSeconds, 0, 3600, "Diagnostics.WarmupSeconds");
            I(c.Diagnostics.GrowSeconds, 1, 7200, "Diagnostics.GrowSeconds");

            return v.Count == 0 ? null : Join(v);
        }

        static string Join(List<string> parts) => string.Join("; ", parts);

        static byte[] Concat(params byte[][] chunks)
        {
            using var ms = new MemoryStream();
            foreach (var chunk in chunks) ms.Write(chunk, 0, chunk.Length);
            return ms.ToArray();
        }
    }
}
