using System;
using System.Collections.Generic;
using System.Text;
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
    //
    // Two targets over that one surface: StructureAware mutates the VALUE schema
    // (what Normalize clamps), FileSurface mutates the FILE bytes (what the
    // decoder and the JSON reader survive). A value-schema fuzz cannot produce
    // a malformed byte, and a byte fuzz that only used character soup would
    // never reach a clamp, so the two are not redundant.
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

        // Fuzz target for the other half of the same surface: the FILE, not the
        // C# string. Load's real input is a byte[] off disk (Mods/EfficientServer/
        // Config/efficientserver.json), so the byte layer is where hostile input
        // actually lands: invalid UTF-8, overlong forms, encoded surrogates, NULs,
        // control bytes, a BOM, a truncated document. StructureAware starts from a
        // serialized default and can only ever produce well-formed UTF-8 JSON, so
        // every one of those vectors is invisible to it.
        //
        // Contracts asserted per case, all of them things a hostile file must not
        // be able to do:
        //   1. Load never throws (fail-soft to defaults) and never returns null.
        //   2. Whatever comes back satisfies the full post-Normalize invariant
        //      table, same as StructureAware.
        //   3. The warning sink stays bounded: one line per corrected knob plus
        //      at most one parse failure, never unbounded growth per file.
        //   4. Round trip: re-serializing the loaded config and loading it again
        //      is value-stable and silent. This is the pair assertion across the
        //      persistence boundary (write, then read back) - a clamp whose
        //      fallback sat outside its own range would drift on every `es
        //      reload`, and only a re-read catches it. Run on the spliced cases,
        //      where a hostile document usually still parses; the truncated and
        //      pure-noise cases land on the built-in defaults, whose round trip
        //      carries no extra signal for a second disk read each.
        public static void FileSurface(CheckFn check, Func<byte[], ServerPerfConfig> loadBytes, Func<string, ServerPerfConfig> loadText)
        {
            string defaultsJson = JsonConvert.SerializeObject(new ServerPerfConfig());
            byte[] defaults = Encoding.UTF8.GetBytes(defaultsJson);

            // Every prefix of a real config is a real hand-edit accident (a
            // half-saved file, a truncated copy, an editor that lost the tail).
            // Sampled on a fixed stride rather than every offset: the config is
            // ~1.8 kB, and each case is a real file write plus a read, so a
            // stride keeps the whole target inside the unit suite's time budget
            // while still cutting mid-token on both key and value text.
            for (int cut = 0; cut <= defaults.Length; cut += TruncationStride)
            {
                var sliced = new byte[cut];
                Array.Copy(defaults, sliced, cut);
                OneCase(check, loadBytes, loadText, sliced, "truncation at byte " + cut, roundTrip: false);
            }
            OneCase(check, loadBytes, loadText, new byte[0], "empty file", roundTrip: false);
            OneCase(check, loadBytes, loadText, new byte[] { 0xEF, 0xBB, 0xBF }, "BOM-only file", roundTrip: false);
            // Past Newtonsoft's default MaxDepth: a hostile operator file must
            // fail soft to defaults, not blow the stack or abort the load.
            OneCase(check, loadBytes, loadText,
                Encoding.UTF8.GetBytes(DeepNest(4000)), "nesting far past the reader depth limit", roundTrip: false);

            var rng = new Random(20260824);
            for (int i = 0; i < SoupIterations; i++)
                OneCase(check, loadBytes, loadText, Splice(rng, defaults), "soup iter " + i, roundTrip: true);
            for (int i = 0; i < NoiseIterations; i++)
            {
                var buf = new byte[rng.Next(0, 256)];
                rng.NextBytes(buf);
                OneCase(check, loadBytes, loadText, buf, "noise iter " + i, roundTrip: false);
            }
        }

        // A load must log at most one correction per Normalize knob plus one parse
        // failure line. Anything past that means a hostile file can make the server
        // write unbounded log volume (a disk-fill vector through the log sink).
        const int MaxWarningsPerLoad = 64;

        const int SoupIterations = 600;
        const int NoiseIterations = 80;
        const int TruncationStride = 5;

        static void OneCase(CheckFn check, Func<byte[], ServerPerfConfig> loadBytes,
            Func<string, ServerPerfConfig> loadText, byte[] bytes, string what, bool roundTrip)
        {
            EsLog.Warnings.Clear();
            EsLog.Errors.Clear();
            ServerPerfConfig loaded;
            try { loaded = loadBytes(bytes); }
            catch (Exception ex)
            {
                check(false, what + ": Load threw " + ex.GetType().Name
                    + " for: " + Truncate(Encoding.UTF8.GetString(bytes)));
                return;
            }
            check(loaded != null, what + ": Load returned null");
            // Both channels: a rejected file is an ERROR, an unknown key or a clamp
            // is a WARNING, and the log-volume bound below covers the load either way.
            check(EsLog.Warnings.Count + EsLog.Errors.Count <= MaxWarningsPerLoad,
                what + ": " + EsLog.Warnings.Count + " warning(s) + " + EsLog.Errors.Count
                + " error(s) from one file (max " + MaxWarningsPerLoad + ")");
            if (loaded == null) return;
            string? bad = Violations(loaded);
            check(bad == null, what + ": " + bad + " for: " + Truncate(Encoding.UTF8.GetString(bytes)));
            if (!roundTrip || bad != null) return;

            // Persistence round trip: what a reload would read back off disk.
            string reSerialized = JsonConvert.SerializeObject(loaded);
            EsLog.Warnings.Clear();
            EsLog.Errors.Clear();
            try
            {
                ServerPerfConfig again = loadText(reSerialized);
                check(JsonConvert.SerializeObject(again) == reSerialized,
                    what + ": round-trip drifted: " + Truncate(reSerialized) + " -> "
                    + Truncate(JsonConvert.SerializeObject(again)));
            }
            catch (Exception ex)
            {
                check(false, what + ": round-trip Load threw " + ex.GetType().Name);
                return;
            }
            check(EsLog.Warnings.Count == 0 && EsLog.Errors.Count == 0,
                what + ": re-loading the normalized config reported " + EsLog.Warnings.Count
                + " correction(s) and " + EsLog.Errors.Count + " error(s), first: "
                + (EsLog.Warnings.Count > 0 ? EsLog.Warnings[0] : EsLog.Errors.Count > 0 ? EsLog.Errors[0] : ""));
        }

        // Build a hostile file by cutting and splicing the real default config, so
        // every case starts from something that actually parses and the mutations
        // are what break it, rather than soup that only ever exercises the
        // catch-all branch.
        static byte[] Splice(Random rng, byte[] defaults)
        {
            var buf = new List<byte>(defaults);
            int edits = 1 + rng.Next(3);
            for (int e = 0; e < edits; e++)
            {
                int at = rng.Next(0, buf.Count + 1);
                switch (rng.Next(6))
                {
                    case 0: Insert(buf, at, Hostile(rng)); break;
                    case 1: buf.RemoveRange(at, rng.Next(0, Math.Min(24, buf.Count - at))); break;
                    case 2: Overwrite(buf, at, Hostile(rng)); break;
                    case 3: if (at < buf.Count) buf.RemoveRange(at, Math.Min(at + 1 + rng.Next(40), buf.Count - at)); break;
                    // Amplify nesting: replay a span so a well-formed prefix
                    // becomes deep structural nesting the reader must reject.
                    case 4: Repeat(buf, at, DeepNest(1 + rng.Next(120))); break;
                    default: if (at > 0) buf.RemoveRange(0, rng.Next(1, Math.Min(at, 32) + 1)); break;
                }
            }
            return buf.ToArray();
        }

        // Byte runs that a UTF-8 decoder, a JSON reader, or a text editor meets in
        // the wild: continuation bytes without a lead, overlong two/three-byte
        // forms, CESU-8 style encoded surrogates, code points past U+10FFFF, a
        // BOM in the middle of the file, NUL and control bytes.
        static byte[] Hostile(Random rng)
        {
            switch (rng.Next(9))
            {
                case 0: return new byte[] { 0x80, 0xBF, 0x80 };
                case 1: return new byte[] { 0xC3, 0x28 };
                case 2: return new byte[] { 0xE0, 0x80, 0xAF };
                case 3: return new byte[] { 0xED, 0xA0, 0x80 };
                case 4: return new byte[] { 0xF5, 0x80, 0x80, 0x80 };
                case 5: return new byte[] { 0xE2, 0x82 };
                case 6: return new byte[] { 0xEF, 0xBB, 0xBF };
                case 7: return new byte[] { 0x00, 0x1F, 0x7F };
                default: return Encoding.UTF8.GetBytes(HostileText[rng.Next(HostileText.Length)]);
            }
        }


        // Numeric and structural junk that a lenient reader may accept where a
        // number belongs, plus a mid-file BOM, an escaped NUL, an unterminated
        // string, and non-ASCII text (the config is UTF-8 on any host locale).
        // The non-ASCII entries are the shapes an ASCII-only corpus cannot
        // reach: an astral character (a surrogate PAIR in UTF-16), a lone
        // surrogate escape, a combining mark that must not be split from its
        // base, and a zero-width joiner inside a grapheme cluster. Each is
        // well-formed enough to decode, so it exercises the reader rather than
        // the replacement path the invalid byte runs above already cover.
        static readonly string[] HostileText = {
            "NaN", "-Infinity", "1e999999", "1e-999999", "0x10", "01", "1.", ".1", "-",
            "﻿", "  ", "\"\\u0000\"", "\"unterminated",
            "{\"AiLod\":", "[[[[[", "}}}}}", "\t\r\n", "\"é中文\"",
            "\"\\ud83d\\ude00\"", "\"\\ud800\"", "\"é́\"", "\"\U0001f468‍\U0001f469‍\U0001f467\"",
        };

        static string DeepNest(int depth) => new string('[', depth) + new string(']', depth);

        static void Insert(List<byte> buf, int at, byte[] payload)
            => buf.InsertRange(at, payload);

        static void Overwrite(List<byte> buf, int at, byte[] payload)
        {
            for (int i = 0; i < payload.Length && at + i < buf.Count; i++)
                buf[at + i] = payload[i];
        }

        static void Repeat(List<byte> buf, int at, string text)
            => buf.InsertRange(at, Encoding.UTF8.GetBytes(text));

        // Failure lines must stay paste-able as a repro, so cap the embedded input.
        // The cap is in UTF-16 code units, so land it on a pair boundary: cutting
        // between a high and a low surrogate emits a lone surrogate, which
        // Console.WriteLine then replaces with U+FFFD and which no longer
        // decodes back to the input the operator needs to reproduce the failure.
        static string Truncate(string s)
        {
            const int Max = 400;
            if (s.Length <= Max) return s;
            int cut = Max;
            if (char.IsHighSurrogate(s[cut - 1])) cut--;
            return s.Substring(0, cut) + "...[" + s.Length + " chars]";
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

            I(c.Pathfinding.GraphUpdateEveryTicks, 1, ServerPerfConfig.GraphUpdateMax, "Pathfinding.GraphUpdateEveryTicks");
            F(c.Pathfinding.MoveRescanThresholdSq, 100f, 10000f, "Pathfinding.MoveRescanThresholdSq");
            I(c.Pathfinding.MaxPathEnqueuesPerTick, 0, 2000, "Pathfinding.MaxPathEnqueuesPerTick");
            F(c.Pathfinding.DropPathWhenFarDistSq, 0f, 4000000f, "Pathfinding.DropPathWhenFarDistSq");

            I(c.WorldTransfer.ChunkPackagesPerObserverPerTick, 1, 32, "WorldTransfer.ChunkPackagesPerObserverPerTick");
            I(c.Network.EntityDistributionEveryTicks, 1, ServerPerfConfig.EntityStrideMax, "Network.EntityDistributionEveryTicks");

            I(c.CrowdCollisionLod.ResolveEveryNTicks, 1, 16, "CrowdCollisionLod.ResolveEveryNTicks");
            F(c.AnimatorLod.FullRateDistSq, 100f, 1000000f, "AnimatorLod.FullRateDistSq");
            I(c.AnimatorLod.FarStride, 1, 10, "AnimatorLod.FarStride");
            I(c.Server.TargetFps, 0, 120, "Server.TargetFps");
            I(c.Server.JobWorkerCount, 0, 64, "Server.JobWorkerCount");

            // The band ceiling moves with the frame target (Normalize), so the
            // mirrored bound has to move with it: a config that survived
            // Normalize must still satisfy the tightened ceiling.
            float overCeiling = Math.Max(20f, c.Server.TargetFps > 0
                ? Math.Min(500f, (float)Math.Ceiling(1200.0 / c.Server.TargetFps))
                : 500f);
            F(c.Governor.OverBudgetMs, 20f, overCeiling, "Governor.OverBudgetMs");
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

            return v.Count == 0 ? null : Join(v);
        }

        static string Join(List<string> parts) => string.Join("; ", parts);
    }
}
