using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EfficientServer.Tests
{
    // Fuzz target for the one untrusted-input surface this mod ships: the JSON
    // config loader (ServerPerfConfig.Load). The file sits in
    // Mods/EfficientServer/Config/, is hand-editable, and travels with mod
    // packages, so it is parsed as hostile input on every dedicated start.
    // Contract under fuzz: Load never throws (fail-soft to defaults) and
    // Normalize lands EVERY knob inside its documented clamp on ANY input.
    //
    // One deterministic (fixed-seed) target, so failures reproduce under
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
    //
    // Every failure line embeds the offending JSON, so an artifact becomes a
    // repro by pasting it as a LoadTemp fixture next to Main.
    internal static class ConfigFuzz
    {
        public delegate void CheckFn(bool cond, string what);

        const int StructureIterations = 2000;
        const int CombinedIterations = 1200;

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
                default: // typo'd twin of a real key: stays an ignored unknown key
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

            I(c.Pathfinding.GraphUpdateEveryTicks, 1, 200, "Pathfinding.GraphUpdateEveryTicks");
            F(c.Pathfinding.MoveRescanThresholdSq, 100f, 10000f, "Pathfinding.MoveRescanThresholdSq");
            I(c.Pathfinding.MaxPathEnqueuesPerTick, 0, 2000, "Pathfinding.MaxPathEnqueuesPerTick");
            F(c.Pathfinding.DropPathWhenFarDistSq, 0f, 4000000f, "Pathfinding.DropPathWhenFarDistSq");

            I(c.WorldTransfer.ChunkPackagesPerObserverPerTick, 1, 32, "WorldTransfer.ChunkPackagesPerObserverPerTick");
            I(c.Network.EntityDistributionEveryTicks, 1, 4, "Network.EntityDistributionEveryTicks");

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
    }
}
