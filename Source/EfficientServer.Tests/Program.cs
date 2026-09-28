// Goal: pin the shipped behavior of the game-type-free config surface
// (ServerPerfConfig load / normalize / gate) plus the two pure seams the hot
// patches read, TickIntervalEma and TickClock. These are the parts of the mod
// that run before the game is loaded, so they are the parts a server operator
// hits first and the only ones testable without a dedicated install.
//
// Method: every check is a fixture built from a documented expected value, not
// from a previous run - clamp endpoints, sibling-linked invariants, fail-soft
// IO branches, decision-table rows. Where a behavior is an invariant rather
// than a constant (band nesting, hysteresis, tick-slot coverage) the assertion
// states the property directly. Anything that cannot be built on this host
// (POSIX mode bits, BOM-less encodings) prints SKIP instead of passing
// silently, so an unexercised branch never reads as a covered one.
// Companions: Fuzz.cs drives the same loader from hostile input, EsLogStub.cs
// stands in for the game's logger.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using EfficientServer.Patches;

namespace EfficientServer.Tests
{
    internal static class Program
    {
        static int _failures;

        // Selection for the edit-test loop. A check whose description does not
        // match _matcher is neither judged nor counted, so one fixture can be
        // rerun while debugging without the rest of the suite's noise.
        // _selected counts the checks a filter kept: zero means the
        // contributor typed a pattern that matches nothing, which must be a
        // red run, not a silent pass. _list prints descriptions instead of
        // judging them, and is how the names to filter on are discovered.
        static CheckPattern? _matcher;
        static bool _list;
        static int _selected;

        // Case-insensitive '*' glob over a check description. A plain word
        // ('Governor') selects every check mentioning it, a trailing '*'
        // ('*endpoints*') narrows a family, and quoting in the shell keeps the
        // stars away from the globber.
        sealed class CheckPattern
        {
            readonly string _pattern;

            public CheckPattern(string pattern) { _pattern = pattern.ToLowerInvariant(); }

            public bool IsMatch(string what) => Glob(_pattern, 0, what.ToLowerInvariant(), 0);

            // Two-pointer backtracking: linear in practice, and no recursion, so
            // a '*' run at the tail cannot blow the stack on a long description.
            static bool Glob(string pat, int p, string text, int t)
            {
                while (p < pat.Length)
                {
                    if (pat[p] == '*')
                    {
                        // '**' is the same as '*'; collapsing keeps the
                        // backtracking loop from re-entering per star.
                        while (p < pat.Length && pat[p] == '*') p++;
                        if (p == pat.Length) return true;
                        for (int i = t; i <= text.Length; i++)
                        {
                            if (Glob(pat, p, text, i)) return true;
                        }
                        return false;
                    }
                    if (t >= text.Length || text[t] != pat[p]) return false;
                    p++; t++;
                }
                return t == text.Length;
            }
        }

        // The whole harness in one call: RunChecks is the ordered list of
        // fixtures, the catch turns a throwing fixture into a red run rather
        // than a stack trace that skips every check after it. Kept at the top
        // so a first read of this file starts where execution does.
        //
        // args: --filter <pattern> runs only the checks whose description
        // matches it; --list prints every matching description and asserts
        // nothing. Both exist so a contributor can narrow the suite to the
        // check they are editing (`make unit FILTER=...`).
        static int Main(string[] args)
        {
            string? filter = null;
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == "--list")
                {
                    _list = true;
                }
                else if (arg == "--filter")
                {
                    if (i + 1 >= args.Length)
                    {
                        Console.WriteLine("ERROR: --filter needs a pattern, e.g. --filter 'Governor*'");
                        Console.WriteLine("  List the check names with: make unit-list");
                        return 2;
                    }
                    filter = args[++i];
                }
                else if (arg.StartsWith("--filter=", StringComparison.Ordinal))
                {
                    filter = arg.Substring("--filter=".Length);
                }
                else
                {
                    Console.WriteLine("ERROR: unknown argument '" + arg + "'.");
                    Console.WriteLine("  Usage: EfficientServer.Tests [--filter <pattern>] [--list]");
                    return 2;
                }
            }
            if (!string.IsNullOrEmpty(filter))
            {
                _matcher = new CheckPattern(filter);
            }

            int exit = RunOrReport();
            if (exit == 0 && !_list && _matcher != null && _selected == 0)
            {
                // A filter that matches nothing is a mistyped pattern, not a
                // clean run: saying so beats an exit-0 that looks like the
                // check just passed.
                Console.WriteLine("ERROR: no check matches --filter '" + filter + "'.");
                Console.WriteLine("  List the check names with: make unit-list, or drop the filter to run all of them.");
                return 1;
            }
            return exit;
        }

        static int RunOrReport()
        {
            try
            {
                return RunChecks();
            }
            catch (Exception ex)
            {
                // A fixture that throws is a failure, not a crash: the run still
                // exits non-zero, but it reports the same FAIL/count shape as any
                // other red run instead of dumping a stack trace and skipping
                // every check that never got reached.
                Console.WriteLine("FAIL: harness aborted before the remaining checks: "
                    + ex.GetType().Name + ": " + ex.Message);
                Console.WriteLine("FAILED: " + (_failures + 1) + " check(s)");
                return 1;
            }
        }

        static void Check(bool cond, string what)
        {
            if (_matcher != null && !_matcher.IsMatch(what)) return;
            _selected++;
            if (_list)
            {
                Console.WriteLine(Printable(what));
                return;
            }
            if (cond) return;
            _failures++;
            Console.WriteLine("FAIL: " + Printable(what));
        }

        // Fuzz descriptions embed the hostile bytes that produced them, so a
        // failing case can carry a NUL or a lone surrogate into the output. Raw,
        // that turns the run's stdout into a binary stream: grep stops printing
        // matches, `make unit-list | less` renders mojibake, and the FAIL line
        // cannot be pasted into a ticket. Print the control characters as
        // \uXXXX; the bytes themselves are still in the fuzz corpus, only the
        // report is escaped. Filtering still matches the raw description.
        static string Printable(string what)
        {
            bool clean = true;
            foreach (char c in what)
            {
                if (char.IsControl(c) || char.IsSurrogate(c)) { clean = false; break; }
            }
            if (clean) return what;
            var buf = new StringBuilder(what.Length + 8);
            foreach (char c in what)
            {
                if (char.IsControl(c) || char.IsSurrogate(c))
                    buf.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                else
                    buf.Append(c);
            }
            return buf.ToString();
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

        // The byte-level entry point the file-surface fuzz target drives: the
        // bytes land on disk exactly as generated, so invalid UTF-8, NULs and a
        // BOM reach Load's real decode path.
        static ServerPerfConfig LoadTempBytes(byte[] bytes) => LoadTempFile(WriteTempBytes(bytes));

        // Load with a clean warning sink so channel assertions see only this file.
        static ServerPerfConfig LoadTempTracked(string json)
        {
            EsLog.Warnings.Clear();
            EsLog.Errors.Clear();
            return LoadTemp(json);
        }

        // The shipped template, located by walking up from this binary for the
        // directory that holds it (the same "find the project root by its
        // marker" rule the scripts use) rather than by a relative path that
        // only resolves from one build layout. Returns null when the harness
        // runs outside the source tree (a bare published copy of this binary),
        // and the caller SKIPs instead of passing on an absent file.
        static string? ConfigTemplatePath() => FindRepoFile("config", "efficientserver.json");

        // Project-root-relative lookup by walking up from this binary, so a
        // check against a shipped file resolves from any build layout and from
        // a worktree, and reports "absent" (null) instead of throwing when the
        // harness runs outside the source tree.
        static string? FindRepoFile(params string[] relativeParts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }

        // Same walk, for a path that names a directory rather than a file.
        static string? FindRepoDir(params string[] relativeParts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }

        // The csproj's <Compile Include> list is the ONLY coupling between this
        // harness and the mod, and it is hand-maintained: a new production file
        // gets no coverage until its path is added, and nothing fails when it is
        // forgotten. The convention that makes the gap checkable is the *Patch
        // suffix: a `*Patch.cs` is a Harmony group and needs the game, a
        // non-suffixed file is a support module that should be testable here.
        // So assert the two sets partition: every non-suffixed production file is
        // either compiled into this project or named in GameCoupled, with a
        // reason. Adding a support module now fails the run instead of shipping
        // untested. Self-skipping outside the source tree, like the checks above.
        static void CheckHarnessCoverageMap()
        {
            string? srcDir = FindRepoDir("Source", "EfficientServer");
            if (srcDir == null)
            {
                Console.WriteLine("SKIP: harness coverage map (no source tree above this binary)");
                return;
            }
            var dir = new DirectoryInfo(srcDir);
            string[] compiled;
            var csproj = FindRepoFile("Source", "EfficientServer.Tests", "EfficientServer.Tests.csproj");
            if (csproj == null)
            {
                Check(false, "the test csproj is reachable, so the coverage map can be checked");
                return;
            }
            compiled = Regex.Matches(File.ReadAllText(csproj, Encoding.UTF8),
                    @"<Compile\s+Include=""(?<p>[^""]+)""")
                .Cast<Match>()
                .Select(m => m.Groups["p"].Value.Replace('\\', '/'))
                .ToArray();

            var support = new SortedSet<string>(StringComparer.Ordinal);
            foreach (FileInfo file in dir.GetFiles("*.cs", SearchOption.AllDirectories))
            {
                // obj/ and bin/ carry the SDK's generated sources, not ours.
                string rel = file.FullName.Substring(dir.FullName.Length).TrimStart('/', '\\').Replace('\\', '/');
                if (rel.StartsWith("obj/", StringComparison.Ordinal)
                    || rel.StartsWith("bin/", StringComparison.Ordinal)) continue;
                if (rel.EndsWith("Patch.cs", StringComparison.Ordinal)) continue;
                support.Add(rel);
            }
            Check(support.Count > 0, "the support-module scan found the non-Patch production files");

            var accounted = new HashSet<string>(StringComparer.Ordinal);
            foreach (string entry in compiled)
            {
                string rel = entry.StartsWith("../EfficientServer/", StringComparison.Ordinal)
                    ? entry.Substring("../EfficientServer/".Length)
                    : entry;
                Check(support.Contains(rel), "csproj compiles a support module that still exists: " + rel);
                accounted.Add(rel);
            }
            foreach (string rel in support)
            {
                if (accounted.Contains(rel)) continue;
                Check(GameCoupled.ContainsKey(rel),
                    "support module is covered or declared game-coupled: " + rel
                    + (GameCoupled.ContainsKey(rel) ? "" : " (add its <Compile Include>, or a GameCoupled entry with a reason)"));
            }
            foreach (string rel in GameCoupled.Keys.OrderBy(n => n, StringComparer.Ordinal))
                Check(support.Contains(rel), "GameCoupled entry names a file that still exists: " + rel);
        }

        // Production files the harness deliberately does NOT compile, each with
        // why. A support module missing from this table and from the csproj is
        // the failure CheckHarnessCoverageMap exists to catch, so an entry is only
        // correct while the reason is true.
        static readonly Dictionary<string, string> GameCoupled = new Dictionary<string, string>
        {
            ["AssemblyInfo.cs"] = "assembly attributes, no types to exercise",
            ["BoehmNative.cs"] = "P/Invoke into monobdwgc; nothing host-independent to assert",
            ["ConsoleCmdEfficientServer.cs"] = "game console types (ConsoleCmdAbstract, SdtdConsole)",
            ["EsLog.cs"] = "calls the game's global::Log; mirrored by EsLogStub.cs and fidelity-pinned instead",
            ["GcIncremental.cs"] = "drives GameManager's GC mode",
            ["ModApi.cs"] = "IModApi, GameManager and Harmony wiring",
            ["Patches/AiAlertGate.cs"] = "reads entity AI state through game types",
            ["Patches/AnimatorEmergency.cs"] = "sweeps Animator rigs on a live world",
            ["Patches/RigVisualProbe.cs"] = "sweeps rig Behaviours on a live world",
        };

        // The EsLog stub above REPLACES the mod's real logging class, so every
        // "a warning was (not) emitted" assertion in this suite is only as
        // truthful as the mirror. Nothing in the build links the two, so pin the
        // contract they must agree on by reading the real source: the severity
        // members the stub switches on, and the Emit signature Config.cs calls.
        // A rename or a reshaped signature then fails here instead of leaving
        // this project compiling against a shape the net48 build no longer has
        // (or, worse, compiling while the checks silently watch a different
        // channel set). Self-skipping outside the source tree.
        static void CheckLogStubFidelity()
        {
            string? real = FindRepoFile("Source", "EfficientServer", "EsLog.cs");
            if (real == null)
            {
                Console.WriteLine("SKIP: EsLog stub fidelity (no source tree above this binary)");
                return;
            }
            string src = File.ReadAllText(real, Encoding.UTF8);

            Match enumMatch = Regex.Match(src, @"enum\s+LogLevel\s*\{(?<body>[^}]*)\}");
            Check(enumMatch.Success, "real EsLog.cs still declares enum LogLevel");
            if (enumMatch.Success)
            {
                // Members may carry an explicit value or an attribute; the name is
                // the part the stub's switch has to know about.
                var realMembers = Regex.Matches(enumMatch.Groups["body"].Value, @"[A-Za-z_][A-Za-z0-9_]*")
                    .Cast<Match>().Select(m => m.Value).OrderBy(n => n, StringComparer.Ordinal).ToList();
                var stubMembers = Enum.GetNames(typeof(LogLevel)).OrderBy(n => n, StringComparer.Ordinal).ToList();
                Check(realMembers.SequenceEqual(stubMembers),
                    "EsLog stub declares the same LogLevel members as the real class (stub: "
                    + string.Join(",", stubMembers) + "; real: " + string.Join(",", realMembers) + ")");
            }
            Check(Regex.IsMatch(src, @"static\s+void\s+Emit\s*\(\s*LogLevel\s+\w+\s*,\s*string\s+\w+\s*\)"),
                "real EsLog.Emit keeps the (LogLevel, string) signature Config.cs calls");
            // The stub runs every message through LogLine (see the check group
            // below), so the shipped logger has to do the same or the harness
            // would pin a record shape the server log never produces. Grepped
            // rather than imported: the real class cannot be loaded here (it
            // calls the game's Log static).
            Check(Regex.IsMatch(src, @"LogPrefix\s*\+\s*LogLine\.Format\("),
                "real EsLog.Emit renders through LogLine, the shared record shape (uptime stamp, one line per record)");
        }

        // The knob groups Load's backfill and FeatureActive's null guards walk,
        // read through the loader's own property list
        // (ServerPerfConfig.ConfigProperties) so the fixtures below cannot drift
        // from the definition production uses. The section predicate is the
        // loader's rule: a nested class in the config namespace, string excluded.
        static List<PropertyInfo> ConfigSections()
        {
            return ServerPerfConfig.ConfigProperties
                .Where(p => p.PropertyType.IsClass
                    && p.PropertyType != typeof(string)
                    && p.PropertyType.Namespace == typeof(ServerPerfConfig).Namespace)
                .ToList();
        }

        // A JSON document naming EVERY public property of ServerPerfConfig and of
        // every config section, so the unknown-key scan can be checked against the
        // declared surface rather than a hand-copied key list. Values are
        // type-shaped (null sections, zero scalars); the caller asserts only on
        // unknown-key lines, which a value correction never produces.
        static string EveryDeclaredKeyJson()
        {
            var buf = new System.Text.StringBuilder("{");
            bool first = true;
            foreach (PropertyInfo prop in ServerPerfConfig.ConfigProperties)
            {
                if (!first) buf.Append(',');
                first = false;
                bool section = prop.PropertyType.IsClass
                    && prop.PropertyType != typeof(string)
                    && prop.PropertyType.Namespace == typeof(ServerPerfConfig).Namespace;
                if (!section) { buf.Append('"').Append(prop.Name).Append("\":0"); continue; }
                buf.Append('"').Append(prop.Name).Append("\":{");
                bool inner = true;
                foreach (PropertyInfo leaf in ServerPerfConfig
                    .ConfigPropertiesOf(prop.PropertyType))
                {
                    if (!inner) buf.Append(',');
                    inner = false;
                    buf.Append('"').Append(leaf.Name).Append("\":0");
                }
                buf.Append('}');
            }
            return buf.Append('}').ToString();
        }

        // {"AiLod":null,"Gc":null,...} - one explicit JSON null per knob group,
        // built from the declared surface rather than a transcribed list, so a
        // section added tomorrow reaches Load's backfill branch in this fixture
        // without anyone remembering to add it to a string literal.
        static string EverySectionNullJson()
        {
            var sections = ConfigSections();
            var buf = new StringBuilder("{");
            for (int i = 0; i < sections.Count; i++)
            {
                if (i > 0) buf.Append(',');
                buf.Append('"').Append(sections[i].Name).Append("\":null");
            }
            return buf.Append('}').ToString();
        }

        // The same shape as an in-memory config: every section pointer nulled, so
        // FeatureActive's per-arm null guard is exercised against a config that
        // really is missing every section rather than against the default object.
        static ServerPerfConfig ConfigWithNullSections()
        {
            var cfg = new ServerPerfConfig();
            foreach (PropertyInfo section in ConfigSections()) section.SetValue(cfg, null);
            return cfg;
        }

        // "Config load failed [<CLR type name>], using defaults: <message>". Only
        // the type name is asserted, not the OS-specific message text, so the
        // check pins the shape operators grep for without pinning errno text.
        static bool NamesExceptionType(string message)
        {
            const string prefix = "Config load failed [";
            const string suffix = "], using defaults: ";
            if (!message.StartsWith(prefix, StringComparison.Ordinal)) return false;
            int close = message.IndexOf(suffix, prefix.Length, StringComparison.Ordinal);
            if (close < 0) return false;
            string type = message.Substring(prefix.Length, close - prefix.Length);
            return type.EndsWith("Exception", StringComparison.Ordinal) && type.Length > "Exception".Length;
        }

        // Discovery precedence of DefaultPathBesideAssembly: $ES_CONFIG_PATH
        // first, then the packaged Config/efficientserver.json, then a legacy
        // sibling file, and with none present the sibling path is returned so
        // Load takes the missing-file branch. Every losing copy that exists is
        // named in a log line, and a set-but-empty override is an ERROR rather
        // than a silent fallthrough. Both candidates resolve next to THIS test
        // binary (the method walks its own assembly), so the fixtures are
        // created and removed around each probe; any pre-existing files and the
        // prior override are saved and put back, so a crashed earlier run cannot
        // wedge or pollute the harness.
        static void CheckDefaultPathDiscovery()
        {
            string asmDir = Path.GetDirectoryName(typeof(ServerPerfConfig).Assembly.Location) ?? ".";
            string subDir = Path.Combine(asmDir, "Config");
            string subPath = Path.Combine(subDir, "efficientserver.json");
            string sibPath = Path.Combine(asmDir, "efficientserver.json");
            byte[]? subBefore = File.Exists(subPath) ? File.ReadAllBytes(subPath) : null;
            byte[]? sibBefore = File.Exists(sibPath) ? File.ReadAllBytes(sibPath) : null;
            string? envBefore = Environment.GetEnvironmentVariable(ServerPerfConfig.ConfigPathEnvVar);
            string envPath = Path.Combine(asmDir, "env-named-config.json");
            bool weMadeSubDir = false;
            try
            {
                if (subBefore != null) File.Delete(subPath);
                if (sibBefore != null) File.Delete(sibPath);
                Environment.SetEnvironmentVariable(ServerPerfConfig.ConfigPathEnvVar, null);
                Check(ServerPerfConfig.DefaultPathBesideAssembly() == sibPath,
                    "DefaultPathBesideAssembly: no config anywhere -> sibling fallback path");

                Directory.CreateDirectory(subDir);
                weMadeSubDir = true;
                // Distinct values on each candidate: asserting the returned path
                // alone would pass even if the precedence decision were never
                // consulted, so probe the CONTENT that actually wins too.
                File.WriteAllText(subPath, "{\"Server\":{\"TargetFps\":111}}");
                File.WriteAllText(sibPath, "{\"Server\":{\"TargetFps\":112}}");
                EsLog.Warnings.Clear();
                Check(ServerPerfConfig.DefaultPathBesideAssembly() == subPath,
                    "DefaultPathBesideAssembly: Config/efficientserver.json preferred over sibling");
                Check(EsLog.Warnings.Count == 1 && EsLog.Warnings[0].Contains(sibPath),
                    "DefaultPathBesideAssembly: the shadowed sibling file is named in a warning");
                Check(ServerPerfConfig.Load(ServerPerfConfig.DefaultPathBesideAssembly()).Server.TargetFps == 111,
                    "DefaultPathBesideAssembly: the Config/ copy's values are the ones loaded");

                // The env override outranks both beside-assembly copies, and both
                // losers are named: an operator editing either of them is editing a
                // file that parses cleanly and does nothing.
                File.WriteAllText(envPath, "{\"Server\":{\"TargetFps\":113}}");
                Environment.SetEnvironmentVariable(ServerPerfConfig.ConfigPathEnvVar, envPath);
                EsLog.Warnings.Clear();
                Check(ServerPerfConfig.DefaultPathBesideAssembly() == envPath,
                    "DefaultPathBesideAssembly: $ES_CONFIG_PATH outranks the beside-assembly copies");
                Check(EsLog.Warnings.Count == 2
                    && EsLog.Warnings.Any(w => w.Contains(subPath))
                    && EsLog.Warnings.Any(w => w.Contains(sibPath)),
                    "DefaultPathBesideAssembly: both shadowed copies are named under an env override");
                Check(ServerPerfConfig.Load(ServerPerfConfig.DefaultPathBesideAssembly()).Server.TargetFps == 113,
                    "DefaultPathBesideAssembly: the env-named file's values are the ones loaded");

                // Set-but-empty is a misconfiguration, not "unset": it must say so
                // and fall back to the documented search, never read a config the
                // operator did not name with no word about the empty variable.
                Environment.SetEnvironmentVariable(ServerPerfConfig.ConfigPathEnvVar, "   ");
                EsLog.Warnings.Clear();
                EsLog.Errors.Clear();
                Check(ServerPerfConfig.DefaultPathBesideAssembly() == subPath,
                    "DefaultPathBesideAssembly: an empty $ES_CONFIG_PATH falls back to the search");
                Check(EsLog.Errors.Count == 1
                    && EsLog.Errors[0].Contains(ServerPerfConfig.ConfigPathEnvVar),
                    "DefaultPathBesideAssembly: an empty $ES_CONFIG_PATH is an ERROR, not a silent fallthrough");

                Environment.SetEnvironmentVariable(ServerPerfConfig.ConfigPathEnvVar, null);
                File.Delete(envPath);
                File.Delete(subPath);

                EsLog.Warnings.Clear();
                Check(ServerPerfConfig.DefaultPathBesideAssembly() == sibPath,
                    "DefaultPathBesideAssembly: sibling file picked up once Config/ copy is gone");
                Check(EsLog.Warnings.Count == 0,
                    "DefaultPathBesideAssembly: a lone copy is read, so nothing is reported as ignored");
                Check(ServerPerfConfig.Load(ServerPerfConfig.DefaultPathBesideAssembly()).Server.TargetFps == 112,
                    "DefaultPathBesideAssembly: the sibling's values are the ones loaded");
                File.Delete(sibPath);

                var fresh = ServerPerfConfig.Load(ServerPerfConfig.DefaultPathBesideAssembly());
                Check(fresh != null && fresh.Enabled,
                    "DefaultPathBesideAssembly: Load on the discovered-but-missing path -> defaults");
            }
            finally
            {
                Environment.SetEnvironmentVariable(ServerPerfConfig.ConfigPathEnvVar, envBefore);
                if (File.Exists(envPath)) File.Delete(envPath);
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
        // start. Self-skipping: where the mode bits are not available (Windows,
        // where GetUnixFileMode/SetUnixFileMode throw) or not enforced (root),
        // the fixture stays readable and the arrangement cannot be built. A skip
        // still prints, so a run that asserted nothing here does not read as a
        // covered branch.
        static void CheckUnreadableFileFailSoft()
        {
            // Probe the capability, not the OS name: every POSIX host supports
            // the mode API, so an IsLinux/IsMacOS allowlist silently dropped this
            // branch on any other Unix (FreeBSD, the BSDs, Solaris). Windows is
            // the one platform where the call itself throws, and whether the bits
            // are then ENFORCED is decided by the readability probe below, which
            // is what skips a root account.
            if (OperatingSystem.IsWindows())
            {
                Console.WriteLine("SKIP: unreadable-config-file branch (no POSIX mode bits on Windows)");
                return;
            }
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
                        EsLog.Errors.Clear();
                        var cfg = ServerPerfConfig.Load(p);
                        Check(cfg != null && cfg.Enabled,
                            "unreadable config file -> defaults (fail-soft like parse errors)");
                        Check(ServerPerfConfig.LastLoadFailed,
                            "unreadable config file -> LastLoadFailed set");
                        // The bracketed token is the exception type name the operator
                        // greps for; require a plausible CLR type name in it, so a
                        // message that dropped the type fails here instead of
                        // passing on the prefix alone.
                        Check(EsLog.Errors.Count == 1 && NamesExceptionType(EsLog.Errors[0]),
                            "unreadable config file -> one ERROR naming the exception type");
                    }
                    else
                    {
                        Console.WriteLine("SKIP: unreadable-config-file branch (mode bits not enforced for this account)");
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

        // ConfigPublication is the one piece of shared state the mod reads off the
        // main thread: ClientListSnapshotPatch's duplicate-IP scan runs on LiteNetLib's
        // socket-receive thread and dereferences the live config, while `es reload`
        // swaps that object on the main thread. Two properties are pinned here.
        //
        // 1. The swap is PUBLISHED, not just stored. Reference writes are atomic, so a
        //    reader can never see a torn pointer - which is exactly the guarantee a
        //    plain static field gives, and exactly the guarantee that is NOT enough:
        //    without a release barrier the reference store can be observed before the
        //    constructor's field stores of the object it points at. The reflection pin
        //    is the load-bearing one; a stress test cannot prove the barrier on a
        //    strong-memory host, so it must not be the only witness.
        // 2. A reader never observes a half-built config. The writer stamps a marker
        //    INSIDE a nested section, so a section pointer observed before its contents
        //    would surface as the default marker rather than the generation's.
        static void CheckCrossThreadConfigPublication()
        {
            FieldInfo? backing = typeof(ConfigPublication)
                .GetField("_current", BindingFlags.NonPublic | BindingFlags.Static);
            // net8.0 dropped FieldInfo.IsVolatile, so read the required custom
            // modifier the C# `volatile` qualifier emits instead.
            bool volatileField = backing != null
                && backing.GetRequiredCustomModifiers().Any(m => m.Name == "IsVolatile");
            Check(volatileField,
                "ConfigPublication backing field is volatile (release publish / acquire read)");

            const int Generations = 20000;
            const int Readers = 4;
            // The seed's MaxPathEnqueuesPerTick, the one value the writer never
            // publishes; see the seed below.
            const int SeedMarker = 0;
            bool[] readerOk = new bool[Readers];
            long[] readerReads = new long[Readers];
            long[] readerRaced = new long[Readers];

            // Seed the holder with a writer-shaped generation before any thread
            // runs. ConfigPublication's static initializer publishes built-in
            // defaults, whose PoolInitScanNodes is false; a reader that samples
            // that seed before the writer's first store trips the marker assertion
            // on a correctly published config, which is a startup race, not the
            // tearing this test is about.
            //
            // MaxPathEnqueuesPerTick = SeedMarker is the "no writer generation
            // observed" marker: the writer publishes gen+1 for gen in
            // [0, Generations), so every value it stores is >= 1 and a reader can
            // tell a sample it actually raced the writer from one that only saw
            // this seed.
            var seed = new ServerPerfConfig();
            seed.Pathfinding.PoolInitScanNodes = true;
            seed.Pathfinding.MaxPathEnqueuesPerTick = SeedMarker;
            seed.Enabled = true;
            ConfigPublication.Current = seed;
            var stop = new ManualResetEventSlim(false);
            // Every reader parks here before the writer starts, and the writer
            // waits for all of them. Without the barrier a writer that finishes
            // its 20000 stores before a reader thread is ever scheduled leaves
            // that reader seeing stop already set, sampling nothing, and failing
            // the "readers actually sampled" check below on a fast or loaded
            // host. Each reader has signalled, so each reader is guaranteed at
            // least one sample no matter how the host schedules them.
            var readersParked = new CountdownEvent(Readers);

            // Writer: publish a config whose nested Pathfinding section carries a
            // marker unique to its generation, with Enabled flipped on alternate
            // generations so a stale top-level field is distinguishable too.
            var writer = new Thread(() =>
            {
                readersParked.Wait();
                for (int gen = 0; gen < Generations; gen++)
                {
                    var cfg = new ServerPerfConfig();
                    cfg.Pathfinding.PoolInitScanNodes = true;
                    cfg.Pathfinding.MaxPathEnqueuesPerTick = gen + 1;
                    cfg.Enabled = (gen & 1) == 0;
                    ConfigPublication.Current = cfg;
                }
                stop.Set();
            });

            Thread[] readerThreads = new Thread[Readers];
            for (int r = 0; r < Readers; r++)
            {
                int id = r;
                readerThreads[r] = new Thread(() =>
                {
                    bool ok = true;
                    long seen = 0;
                    long raced = 0;
                    // Parked before the first sample so the writer cannot start
                    // publishing before this reader has looked at the holder even
                    // once.
                    readersParked.Signal();
                    // Sample FIRST, then ask whether the writer is done. A
                    // while(!stop.IsSet) loop around the sample body is a timing
                    // bug: signalling does not keep the reader running, so a
                    // reader descheduled between Signal and the loop's first test
                    // resumes to find the writer's 20000 generations already
                    // published and stop set, samples nothing, and fails the
                    // "reader actually sampled" check on a correctly published
                    // config. Sampling before the test makes one sample
                    // unconditional, and the loop then runs until the writer is
                    // finished.
                    do
                    {
                        ServerPerfConfig cfg = ConfigPublication.Current;
                        if (cfg == null) { ok = false; break; }
                        // A fully built config is never DefaultPathBesideAssembly's
                        // placeholder: every section exists and the section field the
                        // writer stamped is one the writer actually set.
                        if (cfg.Pathfinding == null || cfg.Network == null || !cfg.Pathfinding.PoolInitScanNodes)
                        { ok = false; break; }
                        if (cfg.Pathfinding.MaxPathEnqueuesPerTick < SeedMarker
                            || cfg.Pathfinding.MaxPathEnqueuesPerTick > Generations)
                        { ok = false; break; }
                        if (cfg.Pathfinding.MaxPathEnqueuesPerTick != SeedMarker) raced++;
                        seen++;
                    }
                    while (!stop.IsSet);
                    readerOk[id] = ok;
                    readerReads[id] = seen;
                    readerRaced[id] = raced;
                });
                readerThreads[r].IsBackground = true;
            }

            writer.IsBackground = true;
            writer.Start();
            for (int r = 0; r < Readers; r++) readerThreads[r].Start();
            writer.Join();
            for (int r = 0; r < Readers; r++) readerThreads[r].Join();

            // Per reader, not summed: an aggregate "somebody sampled" passes even
            // when three of the four reader threads were never scheduled before
            // the writer finished, which is exactly the scheduling the barrier
            // above removes.
            for (int r = 0; r < Readers; r++)
                Check(readerReads[r] > 0,
                    "config publication: reader " + r + " actually sampled the holder");
            Check(readerRaced.Sum() > 0,
                "config publication: readers sampled a published generation, not just the seed");
            for (int r = 0; r < Readers; r++)
                Check(readerOk[r],
                    $"config publication: reader {r} never saw a null or half-built config");

            // Leave the holder on built-in defaults so a later check cannot inherit
            // this test's last generation.
            ConfigPublication.Current = new ServerPerfConfig();
        }

        // The dedicated-host gate: the one answer every patch prefix reads, on
        // the main thread AND on the LiteNetLib receive thread (the client-list
        // snapshot's duplicate-IP scan calls the shared gate on every connection
        // request). The invariant is first-writer-wins, and it matters most when
        // two threads disagree, which is the boot window before the game has
        // published the host type: the loser of the race used to overwrite the
        // winner's answer for the life of the process, deactivating the whole mod
        // with nothing in the log.
        static void CheckDedicatedHostGate()
        {
            // Unresolved until a probe publishes an answer, and unresolved is a
            // legal steady state, not a cached "no".
            var gate = new DedicatedHostGate();
            Check(gate.State == DedicatedHostGate.Unresolved && !gate.IsDedicated,
                "host gate: unresolved until a probe publishes an answer");

            // A throwing probe is the early-boot case. It must not publish
            // anything (the caller fails closed and retries) and must not poison
            // the gate for later callers.
            bool threw = false;
            try { gate.Resolve(() => throw new InvalidOperationException("game not up")); }
            catch (InvalidOperationException) { threw = true; }
            Check(threw && gate.State == DedicatedHostGate.Unresolved,
                "host gate: a probe that throws leaves the answer unresolved");

            int probes = 0;
            Check(gate.Resolve(() => { probes++; return true; })
                && gate.State == DedicatedHostGate.Dedicated && gate.IsDedicated,
                "host gate: the first successful probe publishes a dedicated server");
            Check(gate.Resolve(() => { probes++; return false; }) == false && probes == 1,
                "host gate: a later probe neither runs nor overwrites the published answer");
            Check(gate.State == DedicatedHostGate.Dedicated,
                "host gate: an opposite later answer cannot flip a resolved gate");

            var notDedicated = new DedicatedHostGate();
            Check(notDedicated.Resolve(() => false)
                && notDedicated.State == DedicatedHostGate.NotDedicated
                && !notDedicated.IsDedicated,
                "host gate: a client host resolves to its own state, not to dedicated");

            // The race itself. Every thread's probe claims a DIFFERENT answer, so
            // the run passes only if exactly one of them gets to publish and every
            // other thread ends up reading that same answer. A last-writer-wins
            // gate passes the "exactly one publisher" count only by luck of
            // scheduling, so the sampling loop below is the load-bearing part:
            // once the winner published, no later probe (these threads keep
            // re-resolving in a second wave) may change what a reader sees.
            const int Racers = 8;
            const int Waves = 200;
            var raced = new DedicatedHostGate();
            int[] claims = new int[Racers];
            var publishers = new int[Racers];
            Exception? raceError = null;
            var observed = new int[Racers];
            observed.AsSpan().Fill(DedicatedHostGate.Unresolved);
            var start = new ManualResetEventSlim(false);
            var racerThreads = new Thread[Racers];
            for (int r = 0; r < Racers; r++)
            {
                int id = r;
                racerThreads[r] = new Thread(() =>
                {
                    try
                    {
                        // Parked before the first probe: without the barrier the
                        // first thread to be scheduled can publish every wave
                        // before the others start, and the run would assert
                        // nothing about a race at all.
                        start.Wait();
                        for (int wave = 0; wave < Waves; wave++)
                        {
                            // Even racers claim dedicated, odd ones claim a
                            // client host, so both published values are
                            // represented.
                            bool claim = (id & 1) == 0;
                            claims[id] = claim ? DedicatedHostGate.Dedicated
                                : DedicatedHostGate.NotDedicated;
                            if (raced.Resolve(() => claim)) publishers[id]++;
                        }
                        observed[id] = raced.State;
                    }
                    catch (Exception ex) { raceError = ex; }
                });
                racerThreads[r].Start();
            }
            start.Set();
            for (int r = 0; r < Racers; r++) racerThreads[r].Join();
            Check(raceError == null, "host gate: concurrent resolvers do not throw ("
                + (raceError?.GetType().Name ?? "none") + ")");

            int published = 0;
            for (int r = 0; r < Racers; r++) published += publishers[r];
            Check(published == 1,
                $"host gate: exactly one of {Racers} racing resolvers publishes (got {published})");
            Check(Enumerable.Range(0, Racers).All(r => observed[r] == raced.State),
                "host gate: every racing thread ends up on the one published answer");

            int winner = -1;
            for (int r = 0; r < Racers; r++)
                if (publishers[r] == 1) { winner = r; break; }
            Check(winner >= 0 && raced.State == claims[winner],
                "host gate: the published answer is the publisher's own claim, not a mixture");
            Check(raced.State == DedicatedHostGate.Dedicated
                || raced.State == DedicatedHostGate.NotDedicated,
                "host gate: a resolved gate never reads back as unresolved");
        }

        // Governor tier machine (the pure half of GovernorPatch): hysteresis,
        // windows, cooldown, lever math, and the reload stand-down. Driven with
        // explicit tick intervals, so no host scheduler jitter decides a tier.
        static GovernorConfig GovCfg(bool animatorEmergency, int cooldown)
        {
            var c = new GovernorConfig();
            c.WindowTicks = 5;
            c.CooldownTicks = cooldown;
            c.AnimatorEmergency = animatorEmergency;
            return c;
        }

        // Feed n ticks at one smoothed interval; returns the transition count.
        static int Drive(GovernorTiers tiers, GovernorConfig cfg, double emaMs, int ticks)
        {
            int transitions = 0;
            for (int i = 0; i < ticks; i++)
                if (tiers.Advance(cfg, emaMs)) transitions++;
            return transitions;
        }

        static void CheckGovernorTiers()
        {
            const double Over = 100.0;   // past OverBudgetMs, so escalating
            const double Healthy = 40.0; // under HealthyMs, so stepping down
            GovernorConfig plain = GovCfg(false, 3);

            var t = new GovernorTiers();
            Check(t.Level == 0, "governor starts at the configured baseline");
            Check(Drive(t, plain, Over, 4) == 0 && t.Level == 0,
                "no escalation before WindowTicks of over-budget ticks");
            Check(t.Advance(plain, Over) && t.Level == 1, "escalates to tier 1 on the window");
            Check(Drive(t, plain, Over, 3) == 0, "cooldown holds the tier right after a transition");
            Check(Drive(t, plain, Over, 20) == 0 && t.Level == 1,
                "tier 2 never entered without the AnimatorEmergency opt-in");

            // Hysteresis band: between the thresholds neither window advances.
            var band = new GovernorTiers();
            Drive(band, plain, plain.OverBudgetMs - 1.0, 100);
            Check(band.Level == 0 && Drive(band, plain, plain.OverBudgetMs - 1.0, 100) == 0,
                "hysteresis band never accumulates into a transition");

            // The exact thresholds themselves. Both comparisons in Advance are
            // strict, so an interval sitting ON a threshold is inside the band,
            // not one tick toward a transition. The fixtures above probe the band
            // from inside and the two arms from well outside; nothing lands on the
            // edge, so widening either comparison to >= (or <=) would keep them
            // green while moving the whole escalation band by one threshold width.
            var onOver = new GovernorTiers();
            Check(Drive(onOver, plain, plain.OverBudgetMs, 100) == 0 && onOver.Level == 0,
                "an interval exactly AT OverBudgetMs is still in the band (strict over-budget test)");
            var onHealthy = new GovernorTiers();
            Drive(onHealthy, plain, Over, 5);
            Check(onHealthy.Level == 1, "on-threshold setup: standing in tier 1");
            Check(Drive(onHealthy, plain, plain.HealthyMs, 100) == 0 && onHealthy.Level == 1,
                "an interval exactly AT HealthyMs still holds the tier (strict under-budget test)");
            Check(Drive(onHealthy, plain, plain.HealthyMs - 0.001, 100) == 1 && onHealthy.Level == 0,
                "one hair below HealthyMs steps the tier down once the window closes");

            // Levers are derived from the operator's own value, doubled and
            // capped, never faster than configured.
            Check(t.EffectiveEntityStride(1) == 2, "stride 1 -> 2 while throttled");
            Check(t.EffectiveEntityStride(3) == 4 && t.EffectiveEntityStride(4) == 4,
                "stride caps at the Normalize ceiling of 4");
            Check(t.EffectiveGraphEvery(4) == 8, "graph cadence 4 -> 8 while throttled");
            Check(t.EffectiveGraphEvery(200) == 200, "graph cadence caps at 200");
            Check(GovernorTiers.ThrottleLever(1, 4) == 2, "ThrottleLever doubles");
            Check(GovernorTiers.ThrottleLever(0, 4) == 0, "ThrottleLever never lowers a 1..N cadence");
            // Int-overflow boundary of the doubling: 1.5e9 * 2 wraps to
            // -1294967296 in int arithmetic, and that negative double loses to
            // Math.Max(configured, ...), so an int-computed lever would read
            // back as 1500000000 - the operator's own value, unthrottled -
            // under a 2e9 ceiling that allows the double. The long form
            // doubles and caps as documented.
            Check(GovernorTiers.ThrottleLever(1500000000, 2000000000) == 2000000000,
                "ThrottleLever doubles past the int-overflow boundary instead of wrapping negative");
            Check(GovernorTiers.ThrottleLever(int.MaxValue, 2000000000) == 2000000000,
                "ThrottleLever caps int.MaxValue at the ceiling rather than wrapping negative");
            // The lever ceilings ARE the Normalize ceilings, by contract ("so the
            // two sites cannot drift"). Pin the shared constants against the same
            // endpoints the clamp fixtures above load, so a constant edited on one
            // side alone fails here.
            Check(ServerPerfConfig.EntityStrideMax == 4 && ServerPerfConfig.GraphUpdateMax == 200,
                "throttle ceilings match the Normalize endpoints (stride 4, graph cadence 200)");
            Check(t.EffectiveEntityStride(1) <= ServerPerfConfig.EntityStrideMax
                && t.EffectiveGraphEvery(1) <= ServerPerfConfig.GraphUpdateMax,
                "no throttled lever exceeds its shared ceiling constant");

            // Tier 2 and its periodic rig sweep.
            GovernorConfig em = GovCfg(true, 0);
            var e = new GovernorTiers();
            Drive(e, em, Over, 5);
            Check(e.Level == 1, "tier 1 before the emergency");
            Check(Drive(e, em, Over, 5) == 1 && e.Level == 2, "tier 2 entered once opted in");
            Check(!e.SweepDue, "the entry tick does not also sweep");
            Drive(e, em, Over, 99);
            Check(!e.SweepDue, "no rig sweep before the period elapses");
            e.Advance(em, Over);
            Check(e.SweepDue, "tier 2 asks for a periodic rig sweep");

            // EmergencyOverMs is a SEPARATE threshold, so a server sustained over
            // the governor band but under the emergency band must keep throttling
            // indefinitely without the rigs being culled. Every tier-2 fixture
            // above drives a single over-everything interval, which cannot see a
            // missing (or too low) emergency test. The exact edge is pinned too:
            // the comparison is strict, so the threshold value itself does not arm.
            var betweenBands = new GovernorTiers();
            Check(Drive(betweenBands, em, em.OverBudgetMs + 1.0, 500) == 1
                && betweenBands.Level == 1,
                "sustained over-budget intervals below EmergencyOverMs never arm the emergency");
            var onEmergency = new GovernorTiers();
            Check(Drive(onEmergency, em, em.EmergencyOverMs, 500) == 1
                && onEmergency.Level == 1,
                "an interval exactly AT EmergencyOverMs does not arm the emergency (strict test)");
            var pastEmergency = new GovernorTiers();
            Check(Drive(pastEmergency, em, em.EmergencyOverMs + 0.001, 500) == 2
                && pastEmergency.Level == 2,
                "one hair past EmergencyOverMs arms the emergency after both windows close");

            // Recovery steps down one tier at a time and restores the levers.
            Drive(e, em, Healthy, 5);
            Check(e.Level == 1, "recovery steps 2 -> 1 first");
            Check(e.EffectiveEntityStride(1) == 2, "tier 1 keeps the throttles applied");
            Drive(e, em, Healthy, 5);
            Check(e.Level == 0 && e.EffectiveEntityStride(1) == 1,
                "recovery steps 1 -> 0 and the levers read as configured again");

            // Reload stand-down: a tier the new config no longer authorizes ends
            // now, because the postfix that would step it down may never run.
            GovernorConfig em2 = GovCfg(true, 0);
            var r = new GovernorTiers();
            Drive(r, em2, Over, 10);
            Check(r.Level == 2, "reload setup: standing in the emergency tier");
            Check(!r.ApplyReloadedConfig(em2, true), "an authorized reload keeps the rigs");
            Check(r.ApplyReloadedConfig(em2, false) && r.Level == 0,
                "a disabled governor stands down to baseline and releases the rigs");
            Check(r.EffectiveEntityStride(1) == 1, "levers read as configured after the stand-down");
            Check(!r.ApplyReloadedConfig(em2, false), "stand-down is idempotent");
            var r2 = new GovernorTiers();
            Drive(r2, em2, Over, 10);
            Check(r2.ApplyReloadedConfig(GovCfg(false, 0), true) && r2.Level == 1,
                "AnimatorEmergency off mid-emergency releases the rigs but keeps throttling");
            Check(r2.EffectiveEntityStride(1) == 2, "tier 1 throttles stay in force after that step-down");
            // Governor switched off entirely while standing in tier 1: nothing to
            // release (no rigs are held below tier 2), so the stand-down reports
            // false but MUST still drop the tier, or the throttles outlive the
            // config that asked for them.
            Check(!r2.ApplyReloadedConfig(em2, false) && r2.Level == 0,
                "a governor disabled from tier 1 stands down without claiming a rig release");
            Check(r2.EffectiveEntityStride(1) == 1 && r2.EffectiveGraphEvery(200) == 200,
                "both levers read as configured after a tier-1 stand-down");

            // World change: every window and the tier describe the world that just
            // unloaded, so the machine re-bases instead of carrying them over. A
            // fresh world that really is over budget re-escalates on its own ticks.
            var w = new GovernorTiers();
            Drive(w, em2, Over, 10);
            Check(w.Level == 2, "world-change setup: standing in the emergency tier");
            w.ResetForNewWorld();
            Check(w.Level == 0 && w.EffectiveEntityStride(1) == 1,
                "a new world re-bases the governor to the configured baseline");
            Check(!w.SweepDue, "a new world asks for no rig sweep on its first tick");
            Check(Drive(w, em2, Over, 4) == 0 && w.Level == 0,
                "the previous world's over-budget window does not re-escalate the new one");
            Check(w.Advance(em2, Over) && w.Level == 1,
                "the new world escalates on its own over-budget ticks");
        }

        // The degradation registry every fail-open path in the mod reports to.
        // The contract that matters operationally is two-sided: the log gets
        // exactly ONE line per degradation no matter how often the fail-open
        // branch runs (these paths fire per tick or per connection request), and
        // `es status` keeps showing the key with a count, so a degradation
        // announced hours ago is still visible without re-grepping the log.
        static void CheckDegradeRegistry()
        {
            EfficientServer.Degrade.Reset();
            Check(EfficientServer.Degrade.Summary() == "none",
                "degrade registry: clean process reports degraded=none");
            Check(EfficientServer.Degrade.Count("neverReported") == 0,
                "degrade registry: unreported key counts zero");

            Check(EfficientServer.Degrade.Report("aiAlertProbe", "first explanation"),
                "degrade registry: the first report of a key is the announce");
            bool anyRepeatReannounced = false;
            for (int i = 0; i < 999; i++)
                if (EfficientServer.Degrade.Report("aiAlertProbe", "repeats must not re-announce"))
                    anyRepeatReannounced = true;
            Check(!anyRepeatReannounced,
                "degrade registry: a per-tick fail-open path re-announces never, however often it fires");
            Check(EfficientServer.Degrade.Count("aiAlertProbe") == 1000,
                "degrade registry: every repeat is counted, not just the first");
            Check(EfficientServer.Degrade.FirstReport("aiAlertProbe") == "first explanation",
                "degrade registry: the announced explanation is the first one, not the latest");
            Check(EfficientServer.Degrade.Summary() == "aiAlertProbe=1000",
                "degrade registry: `es status` summary is key=count");

            // Two keys, in occurrence order, space-free: the summary is
            // machine-scraped console output like the animstate fields.
            EfficientServer.Degrade.Report("skip:WaterSplashCubes.Update", "type not found");
            EfficientServer.Degrade.Report("clientListSnapshot", "copy raced");
            Check(EfficientServer.Degrade.Summary()
                    == "aiAlertProbe=1000|skip:WaterSplashCubes.Update=1|clientListSnapshot=1",
                "degrade registry: summary lists every key in occurrence order, pipe-separated");
            Check(EfficientServer.Degrade.Summary().IndexOf(' ') < 0,
                "degrade registry: summary carries no spaces (machine-scraped console field)");

            // A null/empty key must not become an entry: the callers all pass
            // constants, and a nameless row in the status line would be
            // unattributable to any subsystem.
            EfficientServer.Degrade.Report(null!, "nameless");
            EfficientServer.Degrade.Report("", "nameless");
            Check(!EfficientServer.Degrade.Report("", "nameless"),
                "degrade registry: an empty key never announces");
            Check(EfficientServer.Degrade.Summary().IndexOf("nameless") < 0
                && EfficientServer.Degrade.Count("") == 0,
                "degrade registry: an empty key is not recorded");

            // A drift report for an absent target stops being true when the
            // build has the target again, and the apply that resolves it retires
            // the key. Without the clear the status line lists a working skip as
            // degraded for the rest of the process, and its count keeps climbing
            // on every reload. Clearing one key leaves the others, and their
            // order, alone.
            Check(EfficientServer.Degrade.Clear("skip:WaterSplashCubes.Update"),
                "degrade registry: clearing a recorded key reports the recovery");
            Check(!EfficientServer.Degrade.Clear("skip:WaterSplashCubes.Update"),
                "degrade registry: clearing an already-cleared key reports nothing");
            Check(!EfficientServer.Degrade.Clear(""),
                "degrade registry: clearing an empty key is a no-op");
            Check(EfficientServer.Degrade.Summary() == "aiAlertProbe=1000|clientListSnapshot=1",
                "degrade registry: a cleared key leaves the summary, in order");
            Check(EfficientServer.Degrade.Count("skip:WaterSplashCubes.Update") == 0
                && EfficientServer.Degrade.FirstReport("skip:WaterSplashCubes.Update") == null,
                "degrade registry: a cleared key keeps no count and no explanation");
            // A key reported again after a clear announces afresh: the recovery
            // is a new fact, not a continuation of the retired one.
            Check(EfficientServer.Degrade.Report("skip:WaterSplashCubes.Update", "type not found again"),
                "degrade registry: a re-reported key after a clear announces again");

            // Cross-thread use is the production shape: the client-list snapshot
            // reports from the LiteNetLib receive thread while the console drains
            // on the main thread. Hammer both and assert no count is lost and the
            // summary stays consistent, which is what a data race here would
            // break (dropped increments, torn list, an entry per report).
            EfficientServer.Degrade.Reset();
            const int perThread = 5000;
            Exception? raceError = null;
            var racers = new Thread[4];
            for (int t = 0; t < racers.Length; t++)
            {
                int id = t;
                racers[t] = new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < perThread; i++)
                            EfficientServer.Degrade.Report("sharedKey", "worker " + id);
                    }
                    catch (Exception ex) { raceError = ex; }
                });
                racers[t].Start();
            }
            for (int t = 0; t < racers.Length; t++) racers[t].Join();
            Check(raceError == null, "degrade registry: concurrent reporters do not throw ("
                + (raceError?.GetType().Name ?? "none") + ")");
            Check(EfficientServer.Degrade.Count("sharedKey") == perThread * racers.Length,
                "degrade registry: concurrent reporters lose no counts (got "
                + EfficientServer.Degrade.Count("sharedKey") + ")");
            Check(EfficientServer.Degrade.Summary() == "sharedKey=" + (perThread * racers.Length),
                "degrade registry: summary is consistent after concurrent reporting");

            EfficientServer.Degrade.Reset();
            Check(EfficientServer.Degrade.Summary() == "none",
                "degrade registry: reset returns the process view to clean");
        }

        // The log record shape every EsLog.Emit produces. Two properties, both
        // about what an operator's log tooling sees: a record is ONE line (a
        // caught exception concatenated into a message embeds "\n   at ...",
        // which the server log would split into several untimestamped records),
        // and it carries `uptimeS=`, the same field `es status` prints, so a log
        // line and a status capture join on one number.
        static void CheckLogLineFormat()
        {
            const string StampPattern = @" uptimeS=[0-9]+$";

            string plain = EfficientServer.LogLine.Format("Governor: restored baseline");
            Check(plain.StartsWith("Governor: restored baseline", StringComparison.Ordinal)
                && Regex.IsMatch(plain, StampPattern),
                "log line: message text is untouched and the uptime stamp is appended");

            // The case the shape exists for: what a call site gets from
            // "catch (Exception ex) { Emit(..., ex) }" on a real exception.
            string fromException = EfficientServer.LogLine.Format(
                "InitMod failed: System.InvalidOperationException: boom\n"
                + "   at EfficientServer.ModApi.InitMod()\n"
                + "   at GameManager.Start()");
            Check(fromException.IndexOf('\n') < 0 && fromException.IndexOf('\r') < 0,
                "log line: a multi-line exception message stays ONE line");
            Check(fromException.Contains("System.InvalidOperationException: boom | ")
                && fromException.Contains("at EfficientServer.ModApi.InitMod() | ")
                && Regex.IsMatch(fromException, @"at GameManager\.Start\(\)" + StampPattern),
                "log line: line breaks inside the message become readable separators, stack frames intact");

            string crlf = EfficientServer.LogLine.Format("first\r\nsecond");
            Check(crlf.StartsWith("first | second", StringComparison.Ordinal),
                "log line: a CRLF pair is one break, not an empty segment between two separators");

            string trailing = EfficientServer.LogLine.Format("record\n");
            Check(trailing.StartsWith("record uptimeS=", StringComparison.Ordinal),
                "log line: a trailing break does not leave a separator dangling at the end");

            Check(EfficientServer.LogLine.Format("").StartsWith(" uptimeS=", StringComparison.Ordinal),
                "log line: an empty message still yields a stamped record rather than a bare field");

            // Invariant numerics, same convention as the governor and es status
            // lines: a comma-decimal host locale must not reformat the stamp, or
            // the field a log scraper matches stops matching there.
            CultureInfo saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                string invariant = EfficientServer.LogLine.Format("locale probe");
                Check(Regex.IsMatch(invariant, StampPattern),
                    "log line: the uptime stamp is integral and comma-decimal locales cannot reformat it (got: "
                    + invariant + ")");
            }
            finally { CultureInfo.CurrentCulture = saved; }

            double first = EfficientServer.LogLine.UptimeSeconds;
            string stamped = EfficientServer.LogLine.Format("clock probe", 42.6);
            Check(stamped.EndsWith(" uptimeS=43", StringComparison.Ordinal),
                "log line: whole seconds, matching the es status field, and a caller-supplied clock is honored");

            string later = EfficientServer.LogLine.Format("clock probe");
            var laterStamp = Regex.Match(later, StampPattern);
            Check(laterStamp.Success
                && long.Parse(laterStamp.Value.Substring(" uptimeS=".Length),
                    CultureInfo.InvariantCulture) >= (long)first,
                "log line: the stamp advances with process uptime (the correlation key into es status uptimeS)");

            // The shipping entry point, through the stub that shares LogLine with
            // the real logger: a captured record is the string the server log
            // would carry, on the channel the caller chose.
            EsLog.Warnings.Clear();
            EsLog.Errors.Clear();
            EsLog.Emit(LogLevel.Warn, "probe warn\nwith a break");
            EsLog.Emit(LogLevel.Error, "probe error");
            Check(EsLog.Warnings.Count == 1 && EsLog.Errors.Count == 1
                && EsLog.Warnings[0].StartsWith("probe warn | with a break uptimeS=", StringComparison.Ordinal)
                && Regex.IsMatch(EsLog.Errors[0], StampPattern),
                "log line: Emit records one stamped single-line entry per call, on the right channel");

            // The uptime source is injected, so a replay stamps records from a
            // virtual clock: two runs driven by the same schedule must produce
            // byte-identical log streams, and restoring the source puts the
            // process clock back for every later check. The paired property
            // matters as much as the equality - a stamp that ignores the
            // injected source would still match the regex above while making
            // every log line unreplayable.
            double virtualSeconds = 0.0;
            var stamps = new List<string>();
            try
            {
                EfficientServer.LogLine.SetUptimeSource(() => virtualSeconds);
                stamps.Add(EfficientServer.LogLine.Format("Governor: step down"));
                EsLog.Warnings.Clear();
                EsLog.Emit(LogLevel.Warn, "virtual-clock record");
                stamps.Add(EsLog.Warnings[0]);
                virtualSeconds = 3600.0;
                stamps.Add(EfficientServer.LogLine.Format("Governor: step down"));
            }
            finally { EfficientServer.LogLine.SetUptimeSource(null!); }
            Check(stamps[0].EndsWith(" uptimeS=0", StringComparison.Ordinal)
                && stamps[1].EndsWith(" uptimeS=0", StringComparison.Ordinal)
                && stamps[2].EndsWith(" uptimeS=3600", StringComparison.Ordinal),
                "log line: with an injected uptime source, every record (Emit included) carries the virtual stamp");

            var replayStamps = new List<string>();
            double replaySeconds = 0.0;
            try
            {
                EfficientServer.LogLine.SetUptimeSource(() => replaySeconds);
                foreach (double step in new[] { 0.0, 0.0, 3600.0 })
                {
                    replaySeconds = step;
                    replayStamps.Add(EfficientServer.LogLine.Format("Governor: step down"));
                }
            }
            finally { EfficientServer.LogLine.SetUptimeSource(null!); }
            Check(replayStamps.Count == 3
                && replayStamps[0] == stamps[0] && replayStamps[2] == stamps[2],
                "log line: the same virtual schedule replays to byte-identical records (no wall-clock leak into the log)");

            double restored = EfficientServer.LogLine.UptimeSeconds;
            Check(restored >= first && restored < 86400.0,
                "log line: restoring the source hands the stamp back to process uptime");
        }

        // The apply-chain runner behind GameStartPatch.OnGameStartDone and
        // ModApi.ReloadConfig. The property that matters is failure ISOLATION: a
        // chain of independent levers used to sit in one try, so a single throwing
        // step silently skipped every step behind it and the operator's only
        // record was one line with no list of what DID apply.
        static void CheckApplyChainIsolation()
        {
            var ran = new List<string>();
            var steps = new List<EfficientServer.ApplyStep>
            {
                new EfficientServer.ApplyStep("first", () => ran.Add("first")),
                new EfficientServer.ApplyStep("throws", () => { ran.Add("throws"); throw new InvalidOperationException("boom"); }),
                new EfficientServer.ApplyStep("third", () => ran.Add("third")),
                new EfficientServer.ApplyStep("alsoThrows", () => { throw new NotSupportedException("nope"); }),
                new EfficientServer.ApplyStep("last", () => ran.Add("last")),
            };
            EfficientServer.ApplyChainResult result = EfficientServer.ApplyChain.Run(steps);

            Check(ran.Count == 4 && ran[0] == "first" && ran[1] == "throws"
                    && ran[2] == "third" && ran[3] == "last",
                "apply chain: a throwing step does not skip the steps after it (ran: "
                + string.Join(",", ran.ToArray()) + ")");
            Check(result.Applied == 3 && result.Failed == 2,
                "apply chain: every step is counted as applied or failed (applied "
                + result.Applied + ", failed " + result.Failed + ")");
            Check(result.AnyFailed,
                "apply chain: a chain with a failed step reports it, so the caller can still rethrow");

            // The failure text is the operator-facing half: it must name WHICH
            // lever broke, with the exception type, in run order, one entry per
            // failed step.
            string summary = result.Summary();
            Check(summary == "throws[InvalidOperationException]: boom|alsoThrows[NotSupportedException]: nope",
                "apply chain: summary names each failed step with its type and message (got: " + summary + ")");

            // A clean chain reports nothing, so a healthy world load or reload
            // stays silent exactly as it was.
            EfficientServer.ApplyChainResult clean = EfficientServer.ApplyChain.Run(
                new List<EfficientServer.ApplyStep> { new EfficientServer.ApplyStep("ok", () => { }) });
            Check(!clean.AnyFailed && clean.Summary() == "",
                "apply chain: an all-clean run reports no failures and stays silent");
            Check(EfficientServer.ApplyChain.Run(new List<EfficientServer.ApplyStep>()).Applied == 0,
                "apply chain: an empty chain is a no-op, not a failure");
        }

        static int RunChecks()
        {
            // Defaults.
            var d = new ServerPerfConfig();
            Check(d.Pathfinding.GraphUpdateEveryTicks == 4, "default GraphUpdateEveryTicks=4");
            Check(d.Pathfinding.MoveRescanThresholdSq == 100f, "default MoveRescanThresholdSq=100");
            Check(d.Pathfinding.MaxPathEnqueuesPerTick == 0, "default MaxPathEnqueuesPerTick=0 (unlimited)");
            Check(d.Pathfinding.DropPathWhenFarDistSq == 0f, "default DropPathWhenFarDistSq=0 (off)");
            Check(d.Gc.SafetyCollectAboveMB == 0, "default SafetyCollectAboveMB=0 (AUTO)");
            // The AUTO fraction is named once because it is BOTH this section's
            // default and the value GcGuardPatch substitutes for the 0 sentinel, so
            // the shipped default and the applied default cannot drift. Pinned here
            // so changing the constant is a deliberate reviewed change, not an
            // accident that only shows up as a different guard ceiling in the field.
            Check(Math.Abs(d.Gc.SafetyCollectRamFraction
                    - GcConfig.DefaultSafetyCollectRamFraction) < 1e-6
                && Math.Abs(GcConfig.DefaultSafetyCollectRamFraction - 0.5f) < 1e-6,
                "default RamFraction is the named AUTO default (0.5)");

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
            // The failure must surface on the ERROR channel: it is the one load
            // outcome that leaves the server running on knobs the operator never
            // chose, and `es reload` keys its keep-previous behavior off the same
            // signal, so it must be distinguishable from a routine clamp line.
            Check(ServerPerfConfig.LastLoadFailed, "malformed json -> LastLoadFailed set");
            Check(EsLog.Errors.Count == 1 && NamesExceptionType(EsLog.Errors[0]),
                "malformed json -> one ERROR naming the exception type");
            Check(EsLog.Warnings.Count == 0,
                "malformed json -> not also reported as a warning");
            // A clean load clears the flag: it is per-Load, never sticky, so a
            // fixed file reloads to the operator's own values.
            var good = LoadTempTracked("{\"Server\":{\"TargetFps\":44}}");
            Check(good != null && good.Server.TargetFps == 44, "valid json after a failure -> loaded");
            Check(!ServerPerfConfig.LastLoadFailed, "valid json -> LastLoadFailed cleared");
            // A missing file is not a failure: built-in defaults are documented
            // for that case, so `es reload` must still swap the config object.
            ServerPerfConfig.Load(Path.Combine(Path.GetTempPath(),
                "does_not_exist_" + Guid.NewGuid().ToString("N") + ".json"));
            Check(!ServerPerfConfig.LastLoadFailed, "missing file -> LastLoadFailed not set");

            // Non-object value for a section key: deserialization throws ->
            // Load fails soft with full defaults.
            var secBad = LoadTemp("{\"AiLod\":5}");
            Check(secBad != null && secBad.Enabled && secBad.Pathfinding.GraphUpdateEveryTicks == 4,
                "section value of wrong type -> full defaults");

            // Whole-document JSON null deserializes to a null reference: valid
            // JSON that carries no config, so it must be reported as a rejected
            // file (defaults in effect) rather than pass as a clean load.
            var docNull = LoadTempTracked("null");
            Check(docNull != null && docNull.Enabled, "whole-document null -> defaults");
            Check(ServerPerfConfig.LastLoadFailed, "whole-document null -> LastLoadFailed set");
            Check(EsLog.Errors.Count == 1 && EsLog.Errors[0].Contains("JSON null document"),
                "whole-document null -> one ERROR naming the JSON null document");

            // Empty object -> defaults filled.
            var empty = LoadTemp("{}");
            Check(empty != null && empty.Pathfinding != null && empty.Gc != null, "empty object -> sub-configs filled");

            // Explicit JSON null for a section binds as a null reference and must be
            // backfilled with defaults. The document is built from the declared
            // surface and the assertion walks the same list, so a future knob group
            // cannot skip its backfill line (or its fixture line) and NRE downstream.
            List<PropertyInfo> declaredSections = ConfigSections();
            Check(declaredSections.Count > 0,
                "section fixture: the declared surface still yields knob groups (got "
                    + declaredSections.Count + ")");
            var secNull = LoadTemp(EverySectionNullJson());
            bool allSectionsBackfilled = true;
            foreach (var sect in declaredSections)
                if (sect.GetValue(secNull) == null)
                    { allSectionsBackfilled = false; break; }
            Check(allSectionsBackfilled, "explicit null sections -> every section backfilled");
            Check(secNull.AiLod.FullAiDistSq == 100f && secNull.Governor.OverBudgetMs == 57f,
                "backfilled sections carry defaults, not zeros");

            // Valid round-trip.
            var ok = LoadTemp("{\"Pathfinding\":{\"GraphUpdateEveryTicks\":8,\"MoveRescanThresholdSq\":400}}");
            Check(ok.Pathfinding.GraphUpdateEveryTicks == 8, "round-trip GraphUpdateEveryTicks=8");
            Check(ok.Pathfinding.MoveRescanThresholdSq == 400f, "round-trip MoveRescanThresholdSq=400");

            // A misspelled knob keeps its default (fail-soft). Newtonsoft binds
            // case-insensitively, so case variants of real keys bind as usual.
            var typo = LoadTempTracked("{\"Pathfinding\":{\"GraphUpdateEveryTick\":8}}");
            Check(typo != null && typo.Pathfinding.GraphUpdateEveryTicks == 4,
                "typo'd knob keeps default, other fields unaffected");
            // ...and it is NAMED on the warning channel (docs/CONFIG.md: an
            // unknown key must not leave the operator with a knob that silently
            // did nothing), with the dotted path locating it in the section.
            Check(EsLog.Warnings.Count == 1
                && EsLog.Warnings[0].Contains("config unknown key 'Pathfinding.GraphUpdateEveryTick'"),
                "typo'd knob -> one WARNING naming the dotted key path");
            var rootTypo = LoadTempTracked("{\"Enable\":true,\"Pathfinding\":{\"Nope\":1},\"Network\":{\"FastSingleTargetSend\":true}}");
            Check(rootTypo.Enabled == true && rootTypo.Pathfinding != null
                && rootTypo.Network.FastSingleTargetSend,
                "root and nested typos load without disturbing the valid keys beside them");
            Check(rootTypo != null
                && EsLog.Warnings.Count == 2
                && EsLog.Warnings.Any(w => w.Contains("config unknown key 'Enable'"))
                && EsLog.Warnings.Any(w => w.Contains("config unknown key 'Pathfinding.Nope'")),
                "root-level and nested typos each get their own WARNING, dotted path");
            // Case variants bind, so they must NOT be reported as typos.
            var caseNotTypo = LoadTempTracked("{\"ailod\":{\"enabled\":false},\"NETWORK\":{\"fastsingletargetsend\":false}}");
            Check(caseNotTypo.AiLod.Enabled == false && caseNotTypo.Network.FastSingleTargetSend == false,
                "case-variant keys still bind");
            Check(EsLog.Warnings.Count == 0,
                "case-variant keys are not reported as unknown keys");
            // Every key the type declares must be recognized, so no own knob is
            // ever reported as a typo (built by reflection, so a new section or
            // knob is covered the day it lands).
            var everyKey = LoadTempTracked(EveryDeclaredKeyJson());
            Check(everyKey != null
                && !EsLog.Warnings.Any(w => w.Contains("config unknown key")),
                "no declared knob or section is reported as an unknown key");
            var caseBindCaps = LoadTemp("{\"AILOD\":{\"ENABLED\":false}}");
            Check(caseBindCaps.AiLod.Enabled == false,
                "all-caps I-bearing key binds (ordinal binder)");

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

            // Bench-god arm gate: global player damage immunity must NOT arm from the
            // console without an explicit config opt-in. Pin the secure default, the
            // JSON round-trip, sibling-flag isolation, and the fail-closed paths of
            // the pure predicate the console command consults (null config / null
            // section must refuse, same runtime-null probes as the other loaders).
            Check(new ServerPerfConfig().Diagnostics != null
                && !new ServerPerfConfig().Diagnostics.AllowBenchGod,
                "default AllowBenchGod=false (benchgod refuses until opted in)");
            var bgOn = LoadTemp("{\"Diagnostics\":{\"AllowBenchGod\":true}}");
            Check(bgOn.Diagnostics != null && bgOn.Diagnostics.AllowBenchGod,
                "Diagnostics.AllowBenchGod=true round-trips");
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

            // Fidelity-probe arm gate: `es animoff` / `es rigoff` degrade combat
            // timing and rig visuals server-wide, so they need the same explicit
            // config opt-in rather than console access alone.
            Check(!new ServerPerfConfig().Diagnostics.AllowFidelityProbes,
                "default AllowFidelityProbes=false (animoff/rigoff refuse until opted in)");
            var probeOn = LoadTemp("{\"Diagnostics\":{\"AllowFidelityProbes\":true}}");
            Check(probeOn.Diagnostics != null && probeOn.Diagnostics.AllowFidelityProbes,
                "Diagnostics.AllowFidelityProbes=true round-trips");
            Check(!ServerPerfConfig.FidelityProbeArmAllowed(null!),
                "FidelityProbeArmAllowed(null config) -> fail closed");
            Check(!ServerPerfConfig.FidelityProbeArmAllowed(new ServerPerfConfig()),
                "FidelityProbeArmAllowed(defaults) -> refused");
            Check(!ServerPerfConfig.FidelityProbeArmAllowed(new ServerPerfConfig { Diagnostics = null! }),
                "FidelityProbeArmAllowed(null diagnostics section) -> fail closed");
            Check(ServerPerfConfig.FidelityProbeArmAllowed(probeOn),
                "FidelityProbeArmAllowed(opt-in) -> allowed");
            // Sibling-flag isolation: opting into one diagnostic must not arm the other.
            Check(!ServerPerfConfig.FidelityProbeArmAllowed(bgOn) && !ServerPerfConfig.BenchGodArmAllowed(probeOn),
                "AllowFidelityProbes and AllowBenchGod are independent");

            // v1.7.0 fields: MidTickStride clamp, Network + Diagnostics defaults.
            var d2 = new ServerPerfConfig();
            Check(d2.AiLod.MidTickStride == 1, "default MidTickStride=1 (off)");
            Check(d2.Network != null && d2.Network.FastSingleTargetSend, "default FastSingleTargetSend=true (v1.13.0: provably equivalent, no gameplay impact)");
            var stride = LoadTemp("{\"AiLod\":{\"MidTickStride\":999}}");
            Check(stride.AiLod.MidTickStride == 20, "MidTickStride 999 -> 20 (clamp)");
            var strideNeg = LoadTemp("{\"AiLod\":{\"MidTickStride\":-3}}");
            Check(strideNeg.AiLod.MidTickStride == 1, "MidTickStride -3 -> 1 (clamp)");
            var net = LoadTemp("{\"Network\":{\"FastSingleTargetSend\":false}}");
            Check(!net.Network.FastSingleTargetSend, "Network round-trip FastSingleTargetSend=false (opt-out)");
            // v1.17.x: join-churn race fix ships ON (removes the stock receive-thread
            // crash; the opt-out restores the exact vanilla enumerator).
            // Null-guarded like every other d2.Network assertion here: the
            // earlier `d2.Network != null` comparison narrows the reference to
            // maybe-null for the rest of the method, so a bare dereference is a
            // CS8602 build error under this project's nullable settings.
            Check(d2.Network != null && d2.Network.ClientListSnapshot,
                "default ClientListSnapshot=true (join-churn race fix ships on)");
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
            // Liveness is a latch: a later Advance must never clear it, or a
            // consumer that gates on Alive would fall back to vanilla mid-run
            // while the clock keeps ticking.
            for (int t = 13; t <= 256; t++) EfficientServer.Patches.TickClock.Advance();
            Check(EfficientServer.Patches.TickClock.Alive,
                "tick clock liveness stays latched across further advances");
            Check(EfficientServer.Patches.TickClock.Ticks == 256,
                "every advance steps the counter exactly once (no skipped or doubled ticks)");
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

            // The animator stripe (AnimatorLodPatch) pumps calm-far rigs on an Nth-frame
            // slot, so its phase has to come from the same zero-seeded cursor as every
            // other cadence consumer: a run replayed from its seed must land the same
            // rigs on the same frames. Capture what the live clock actually decides over
            // 64 advances and re-derive it from the pure predicate at the same absolute
            // frame indices; the traces must agree exactly.
            int animStride = 4;
            bool animReplayHeld = true;
            for (int k = 0; k < 64 && animReplayHeld; k++)
            {
                EfficientServer.Patches.TickClock.Advance();
                int frame = EfficientServer.Patches.TickClock.Ticks;
                for (int id = 0; id < 8; id++)
                {
                    if (EfficientServer.Patches.TickClock.OwnsCurrentSlot(id, animStride)
                        != EfficientServer.Patches.TickClock.OwnsSlot(id, frame, animStride))
                    { animReplayHeld = false; break; }
                }
            }
            Check(animReplayHeld,
                "animator stripe replays off the shared frame cursor (live trace == pure predicate)");

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

            // The production entry point Advance() takes its time from an
            // injected source rather than a hardwired clock read, so the SAME code
            // path a server runs replays from a recorded schedule. Two holders on
            // one virtual clock must agree bitwise, and must equal the
            // explicit-timestamp machine step for step: a divergence here would
            // mean the shipping path measures something the tests never assert.
            double virtualMs = 0.0;
            var injectedA = new EfficientServer.Patches.TickIntervalEma(() => virtualMs);
            var injectedB = new EfficientServer.Patches.TickIntervalEma(() => virtualMs);
            var explicitTwin = new EfficientServer.Patches.TickIntervalEma();
            double stampMs = 0.0;
            bool injectedReplayed = true;
            for (int i = 0; i < gaps.Count; i++)
            {
                virtualMs += gaps[i];
                stampMs += gaps[i];
                double a = injectedA.Advance();
                if (a != injectedB.Advance() || a != explicitTwin.Advance(stampMs))
                { injectedReplayed = false; break; }
            }
            Check(injectedReplayed,
                "tick EMA on an injected clock replays a recorded schedule bitwise, identical to the explicit-timestamp machine");

            // A null source binds the process stopwatch, so the constructor a
            // server uses still measures real time (monotonic, seeded at 50 ms)
            // rather than reading a torn-down or never-advanced source.
            var wallClockBound = new EfficientServer.Patches.TickIntervalEma(null!);
            double wallA = wallClockBound.Advance();
            Thread.Sleep(2);
            double wallB = wallClockBound.Advance();
            Check(wallA == 50.0 && wallB > 0.0 && wallB < 50.0,
                "a null clock source binds the process stopwatch (first tick seeds, later ticks measure real time)");

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

            // World-change re-base (TickIntervalEma.Reseed). The gap between the
            // last UpdateTick of the outgoing world and the first of the incoming
            // one is the whole world load, not a tick. Left in the recurrence it
            // adds gap/32 to the average in ONE step, and the alpha-1/32 memory
            // then needs tens of ticks to relax; both the governor's tier machine
            // and the tick guard's shed window count ticks, so a carried-over
            // spike can escalate (or shed) on the new world's spawn load. Reseed
            // must return the instance to exactly its seeded state, so both gates
            // re-base on the same hook and a post-reseed trace equals a fresh one.
            const double WorldLoadMs = 30000.0;
            const double LastOldWorldTickMs = 1000.0 + 199 * 50.0;
            var carriedOver = new EfficientServer.Patches.TickIntervalEma();
            var rebasePaired = new EfficientServer.Patches.TickIntervalEma();
            for (int i = 1; i <= 200; i++)
            {
                carriedOver.Advance(i * 50.0);
                rebasePaired.Advance(i * 50.0);
            }
            // The pause-for-rebase hook fires with the world already swapped; the
            // load itself sits in the gap before the next tick, so a re-based
            // instance must not average it.
            rebasePaired.Reseed();
            double carried = carriedOver.Advance(LastOldWorldTickMs + WorldLoadMs);
            double rebased = rebasePaired.Advance(LastOldWorldTickMs + WorldLoadMs);
            Check(carried > govCfg.OverBudgetMs && carried > govCfg.EmergencyOverMs,
                "a carried-over 30 s world-load gap spikes the EMA past both governor bands (got "
                    + carried.ToString("F1", CultureInfo.InvariantCulture) + "ms)");
            Check(rebased == 50.0 && rebasePaired.Value == 50.0,
                "Reseed returns the EMA to the vanilla 50 ms seed, and the first tick of the new "
                    + "world records a baseline instead of the load gap (got "
                    + rebased.ToString("F1", CultureInfo.InvariantCulture) + "ms)");
            Check(rebasePaired.Advance(LastOldWorldTickMs + WorldLoadMs + 50.0) < govCfg.OverBudgetMs,
                "the second tick after a reseed already measures the real interval, not the load (got "
                    + rebasePaired.Value.ToString("F1", CultureInfo.InvariantCulture) + "ms)");
            // Equivalence: a reseeded instance and a fresh one are the same machine,
            // so the governor and the tick guard (separate instances, one reseed
            // call each) cannot drift apart across a world change.
            var freshTwin = new EfficientServer.Patches.TickIntervalEma();
            var seededTwin = new EfficientServer.Patches.TickIntervalEma();
            seededTwin.Reseed();
            bool reseedParity = true;
            for (int i = 1; i <= 500; i++)
            {
                seededTwin.Advance(i * 73.0);
                if (seededTwin.Value != freshTwin.Advance(i * 73.0))
                {
                    reseedParity = false;
                    break;
                }
            }
            Check(reseedParity,
                "a reseeded EMA is indistinguishable from a fresh one on the new world's ticks");

            // ShedOrder: which entity ids one tick-guard batch removes. Distance is
            // not a total order (co-located enemies share a distSq exactly), so a
            // batch boundary landing in a tie group must not be cut by
            // World.Entities.list order, or the same horde at the same distances
            // sheds different zombies on two runs. The expectation is the spec,
            // read off the order itself: descending distance, ascending entityId
            // inside a tie.
            var census = new List<(float distSq, int entityId)>
            {
                (100f, 7), (900f, 3), (100f, 5), (400f, 11), (900f, 2)
            };
            var shedTop3 = ShedOrder.Select(census, 3);
            Check(shedTop3.Count == 3 && shedTop3[0] == 2 && shedTop3[1] == 3 && shedTop3[2] == 11,
                "shed order: farthest first, lowest id inside each distance tie (got "
                    + string.Join(",", shedTop3) + ")");
            // Same census, different enumeration order, one more id selected: the
            // first four must be identical, and the 900-pair tie must keep ids 2
            // before 3 rather than tracking the input.
            var reordered = new List<(float distSq, int entityId)>
            {
                (900f, 2), (100f, 5), (400f, 11), (900f, 3), (100f, 7)
            };
            var shedTop4 = ShedOrder.Select(reordered, 4);
            Check(shedTop4.Count == 4 && shedTop4[0] == 2 && shedTop4[1] == 3
                && shedTop4[2] == 11 && shedTop4[3] == 5,
                "shed order is a function of the census, not of enumeration order (got "
                    + string.Join(",", shedTop4) + ")");
            Check(census[0].entityId == 7 && census[1].entityId == 3,
                "shed order does not reorder the caller's census (it is reused between batches)");
            // Batch size is clamped to the horde, and a zero/negative batch sheds
            // nothing rather than indexing past the census.
            Check(ShedOrder.Select(census, 99).Count == 5 && ShedOrder.Select(census, 0).Count == 0
                && ShedOrder.Select(census, -1).Count == 0,
                "shed batch clamps to the horde; empty batches shed nothing");
            // A corrupt position yields a NaN distSq, and float.CompareTo calls NaN
            // less than everything in one argument order and more in the other:
            // left to CompareTo the ordering stops being transitive and the cut
            // lands on enumeration order again. NaN sheds first, id-ascending.
            var corrupt = new List<(float distSq, int entityId)>
            {
                (float.NaN, 4), (50f, 9), (float.NaN, 6)
            };
            var shedCorrupt = ShedOrder.Select(corrupt, 2);
            Check(shedCorrupt.Count == 2 && shedCorrupt[0] == 4 && shedCorrupt[1] == 6,
                "NaN distance sheds first, id-ascending inside the NaN group (got "
                    + string.Join(",", shedCorrupt) + ")");
            var shedCorruptFlip = ShedOrder.Select(
                new List<(float distSq, int entityId)> { (float.NaN, 6), (50f, 9), (float.NaN, 4) }, 2);
            Check(shedCorruptFlip[0] == 4 && shedCorruptFlip[1] == 6,
                "NaN shed order survives a reordered census (got "
                    + string.Join(",", shedCorruptFlip) + ")");

            // Encoding boundary: the config file is UTF-8, and a UTF-8 BOM must be
            // tolerated, so operator configs behave identically on every host.
            string bomP = WriteTempBytes(
                new byte[] { 0xEF, 0xBB, 0xBF }
                .Concat(System.Text.Encoding.UTF8.GetBytes("{\"Enabled\":false}"))
                .ToArray());
            var bom = LoadTempFile(bomP);
            Check(bom != null && !bom.Enabled,
                "UTF-8 BOM prefix tolerated at config load");
            // No-BOM UTF-8 config loads normally. Every knob is numeric or
            // boolean, so the ONLY place non-ASCII can reach the reader is a
            // section or key NAME, and those are echoed back verbatim in the
            // unknown-key warning: that echo is the round trip worth pinning.
            string noBomP = WriteTempBytes(
                System.Text.Encoding.UTF8.GetBytes("{\"Pathfinding\":{\"GraphUpdateEveryTicks\":6}}"));
            var noBom = LoadTempFile(noBomP);
            Check(noBom != null && noBom.Pathfinding.GraphUpdateEveryTicks == 6,
                "UTF-8 no-BOM config loads normally");

            EsLog.Warnings.Clear();
            // Code points, not literals: this file is compiled by the mcs
            // fallback backend under the host codepage, where a non-ASCII
            // character literal in source would itself be read as mojibake.
            string visibleKey = "Ena" + ((char)0x00E9).ToString() + ((char)0x0301).ToString()
                + ((char)0x4E2D).ToString() + ((char)0xD83D).ToString() + ((char)0xDE00).ToString();
            var visibleCfg = LoadTempFile(WriteTempBytes(
                System.Text.Encoding.UTF8.GetBytes("{\"" + visibleKey + "\":1}")));
            Check(visibleCfg != null && visibleCfg.Enabled,
                "config whose only non-ASCII is a key name loads without error");
            Check(EsLog.Warnings.Count == 1 && EsLog.Warnings[0].Contains("'" + visibleKey + "'"),
                "an unknown key is echoed with its exact UTF-8 spelling: precomposed"
                + " e-acute, its combining acute, CJK and an astral-plane emoji"
                + " (a UTF-16 surrogate pair) all survive the decode intact");

            EsLog.Warnings.Clear();
            // A zero-width joiner is invisible, so two keys differing only by
            // one read identically to an operator grepping the log. It is
            // replaced, not dropped, so the two keys stay distinguishable.
            string zwjKey = "En" + ((char)0x200D).ToString() + "abled";
            LoadTempFile(WriteTempBytes(
                System.Text.Encoding.UTF8.GetBytes("{\"" + zwjKey + "\":1}")));
            Check(EsLog.Warnings.Count == 1
                && EsLog.Warnings[0].Contains("'En" + (char)0xFFFD + "abled'"),
                "an invisible character in a key name is shown as U+FFFD so the"
                + " spelling stays readable and the key stays distinguishable");

            EsLog.Warnings.Clear();
            // A JSON string may spell half a surrogate ("\uD800"), which is not
            // a character at all. The encoder replaces it with U+FFFD at the log
            // sink, so every lone-half key rendered as the same name and the
            // operator could not tell them apart in the log. Replaced on the way
            // in, by the same rule as the zero-width joiner above, and a
            // MATCHED pair is still printed as the one astral character it is
            // (pinned by the visibleKey case above).
            LoadTempFile(WriteTempBytes(System.Text.Encoding.UTF8.GetBytes(
                "{\"En\\ud800abled\":1}")));
            Check(EsLog.Warnings.Count == 1
                && EsLog.Warnings[0].Contains("'En" + (char)0xFFFD + "abled'"),
                "a lone surrogate half in a key name is shown as U+FFFD instead of"
                + " being replaced downstream, so the key stays one distinguishable name");

            EsLog.Warnings.Clear();
            // JSON forbids a raw control character in a string, so the key
            // below carries the ESCAPED newline. Newtonsoft decodes it to a
            // real LF inside the name, which used to reach the warning line
            // verbatim and split one key across two log lines.
            var lfCfg = LoadTempFile(WriteTempBytes(
                System.Text.Encoding.UTF8.GetBytes("{\"Ena\\nbled\":1}")));
            Check(lfCfg != null && lfCfg.Enabled,
                "config with an escaped newline in a key name loads without error");
            Check(EsLog.Warnings.Count == 1,
                "a control character in a key name still yields exactly one warning line");
            Check(EsLog.Warnings[0].IndexOf('\n') < 0 && EsLog.Warnings[0].IndexOf('\r') < 0,
                "the unknown-key warning carries no embedded line break");
            Check(EsLog.Warnings[0].Contains("Ena" + (char)0xFFFD + "bled"),
                "the unprintable character is shown as U+FFFD and the rest of the"
                + " spelling stays readable");

            // Discovery + IO-failure branches of the load path itself, which no
            // string-level fixture reaches (they all go through LoadTemp):
            CheckLogStubFidelity();
            CheckHarnessCoverageMap();
            CheckDefaultPathDiscovery();
            CheckUnreadableFileFailSoft();
            CheckCrossThreadConfigPublication();
            CheckDedicatedHostGate();
            CheckDegradeRegistry();
            CheckLogLineFormat();
            CheckApplyChainIsolation();

            // Fuzz: the config file is the mod's untrusted-input surface, so a
            // deterministic target hammers Load. Structure-aware mutations of the
            // default config reach Normalize's value paths that character soup
            // never parses into. It asserts the full post-Normalize invariant
            // table, so a wrong-but-non-crashing load fails the run instead of
            // hiding.
            ConfigFuzz.StructureAware(Check, LoadTemp);
            // Second fuzz target, same surface one layer down: the file BYTES.
            // StructureAware can only build well-formed UTF-8, so malformed
            // encodings, NULs, mid-token truncation and runaway nesting are only
            // reachable here. Asserts the same clamp table plus the persistence
            // round trip (write the loaded config back out, read it in again).
            ConfigFuzz.FileSurface(Check, LoadTempBytes, LoadTemp);

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

            // The vanilla sentinel of a knob that REPLACES a stock constant is named
            // once on its config section, and every site that has to know "stock"
            // reads it there: the section default, the patch's inactive fallback, and
            // the FeatureActive predicate. These checks pin that the defaults ARE the
            // named sentinel, and that a knob set to it reads as inactive while any
            // other accepted value reads as active in both directions (the
            // rescan dead-zone accepts a value below stock before Normalize floors it,
            // and the patch is in force for it).
            var vanillaDefaults = new ServerPerfConfig();
            Check(vanillaDefaults.Pathfinding.MoveRescanThresholdSq
                    == PathfindingConfig.VanillaRescanThresholdSq,
                "MoveRescanThresholdSq default is the named vanilla sentinel");
            Check(vanillaDefaults.WorldTransfer.ChunkPackagesPerObserverPerTick
                    == WorldTransferConfig.VanillaBatchSize,
                "ChunkPackagesPerObserverPerTick default is the named vanilla sentinel");
            var belowVanilla = new ServerPerfConfig();
            belowVanilla.Pathfinding.MoveRescanThresholdSq = 25f;
            Check(belowVanilla.FeatureActive("MoveThreshold"),
                "a dead-zone below stock is still not stock, so the patch is in force");
            var stockValues = LoadTemp(
                "{\"Pathfinding\":{\"MoveRescanThresholdSq\":" +
                PathfindingConfig.VanillaRescanThresholdSq.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) + "}," +
                "\"WorldTransfer\":{\"ChunkPackagesPerObserverPerTick\":" +
                WorldTransferConfig.VanillaBatchSize + "}}");
            Check(!stockValues.FeatureActive("MoveThreshold"),
                "MoveRescanThresholdSq at the vanilla sentinel -> inactive");
            Check(!stockValues.FeatureActive("ChunkSendThrottle"),
                "ChunkPackagesPerObserverPerTick at the vanilla sentinel -> inactive");

            // Every FeatureActive arm null-guards its section, because ModApi calls it
            // on a runtime config the loader can hand over with any section null
            // (an explicit {"AiLod":null} in the operator file, or a section added
            // without a backfill line). The guards are what keeps a null section a
            // disabled patch group instead of an NRE during mod init, and no fixture
            // above reaches them: KeyBenchGod is the only section-independent arm.
            // The key list is read by reflection so a newly added constant cannot
            // skip this sweep, and pinned by name so a rename or a dropped constant
            // cannot silently shrink it either.
            var featureKeys = typeof(ServerPerfConfig)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string)
                    && f.Name.StartsWith("Key", StringComparison.Ordinal))
                .Select(f => f.GetRawConstantValue())
                .OfType<string>()
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
            var expectedKeys = new[]
            {
                "AiLod", "AnimatorLod", "BenchGod", "ChunkSendThrottle", "ClientListSnapshot",
                "CrowdCollisionLod", "EntityDistributionStride", "ExplosionParticles", "FastSend",
                "Gc", "Governor", "GraphThrottle", "InitScanPool", "MoveThreshold", "PathAdmission",
                "TargetFps", "TickGuard",
            };
            Check(featureKeys.SequenceEqual(expectedKeys),
                "feature-key vocabulary pinned: " + string.Join(",", featureKeys));
            // The null fixture must really be null, or the sweep below would pass
            // on a default config and prove nothing. Built by nulling every
            // declared section, so a knob group added tomorrow is null here too
            // instead of quietly keeping the sweep off the guard it needs.
            var nullSections = ConfigWithNullSections();
            int nulledSections = declaredSections.Count(p => p.GetValue(nullSections) == null);
            Check(nulledSections == declaredSections.Count,
                "null-section fixture really holds null in every declared section ("
                    + nulledSections + "/" + declaredSections.Count + ")");
            foreach (string key in featureKeys)
            {
                if (key == ServerPerfConfig.KeyBenchGod)
                {
                    Check(nullSections.FeatureActive(key, true),
                        "null sections: BenchGod still reads the console flag (section-independent)");
                    Check(!nullSections.FeatureActive(key, false),
                        "null sections: BenchGod with the console flag off -> inactive");
                    continue;
                }
                Check(!nullSections.FeatureActive(key), "null section: " + key + " -> fail closed");
            }

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
                "\"Governor\":{\"OverBudgetMs\":20,\"HealthyMs\":15,\"EmergencyOverMs\":25," +
                "\"WindowTicks\":6000,\"CooldownTicks\":36000}," +
                "\"TickGuard\":{\"ShedAboveMs\":1000,\"WindowTicks\":6000,\"ShedBatch\":100," +
                "\"CooldownTicks\":36000,\"MinEnemiesKept\":10000}," +
                "\"Gc\":{\"SafetyCollectAboveMB\":1048576,\"SafetyCollectRamFraction\":0.95," +
                "\"IncrementalPauseTargetMs\":10000}}");
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
            // At the 120 fps target the whole band is pinned to its floor: an
            // unloaded loop sits at 8.3 ms, so a wide band could never be
            // exceeded and the governor would be inert. The band's own upper
            // endpoints are pinned separately below, at the vanilla frame rate
            // that makes them legal.
            Check(atMax.Governor.OverBudgetMs == 20f && atMax.Governor.HealthyMs == 15f
                && atMax.Governor.EmergencyOverMs == 25f && atMax.Governor.WindowTicks == 6000
                && atMax.Governor.CooldownTicks == 36000,
                "Governor upper endpoints preserved verbatim, band floored to the fps-120 target");
            Check(atMax.TickGuard.ShedAboveMs == 1000f && atMax.TickGuard.WindowTicks == 6000
                && atMax.TickGuard.ShedBatch == 100 && atMax.TickGuard.CooldownTicks == 36000
                && atMax.TickGuard.MinEnemiesKept == 10000, "TickGuard upper endpoints preserved verbatim");
            Check(atMax.Gc.SafetyCollectAboveMB == 1048576 && atMax.Gc.SafetyCollectRamFraction == 0.95f
                && atMax.Gc.IncrementalPauseTargetMs == 10000, "Gc upper endpoints preserved verbatim");
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
                "\"IncrementalPauseTargetMs\":0}}");
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
            // 0 on the fraction is the documented AUTO sentinel, not a zero ceiling,
            // so it must survive Normalize untouched (the use site resolves it) AND
            // keep the named default reachable. A clamp of the 0 to a positive
            // minimum here would instead log a "config corrected" line for a value
            // the mod documents as meaningful.
            Check(atMin.Gc.SafetyCollectRamFraction == 0f
                && GcConfig.DefaultSafetyCollectRamFraction > 0f
                && GcConfig.DefaultSafetyCollectRamFraction <= 0.95f,
                "RamFraction 0 is the AUTO sentinel: preserved by Normalize, resolved at the use site");
            Check(EsLog.Warnings.Count == 0, "lower endpoints load without any 'config corrected' warning");

            // Governor band vs frame target. The band is compared against the
            // MEASURED frame interval and an unloaded loop idles at
            // 1000/TargetFps, so the fps-20 default (57) can never be exceeded
            // above ~18 fps: raising Server.TargetFps without retuning the band
            // would silently switch the governor off. Normalize therefore caps
            // OverBudgetMs at 1.2x the target frame interval, and the cap is
            // pinned here from both sides - a value that fits survives, one that
            // does not is corrected AND logged, so the operator sees it move.
            EsLog.Warnings.Clear();
            var bandAt20 = LoadTemp("{\"Server\":{\"TargetFps\":20},\"Governor\":{\"OverBudgetMs\":57,\"HealthyMs\":52}}");
            Check(bandAt20.Governor.OverBudgetMs == 57f && bandAt20.Governor.HealthyMs == 52f,
                "governor band untouched by the 20 fps target (ceiling 60)");
            Check(EsLog.Warnings.Count == 0, "a band that fits the target raises no correction");

            EsLog.Warnings.Clear();
            var bandAt40 = LoadTemp("{\"Server\":{\"TargetFps\":40},\"Governor\":{\"OverBudgetMs\":57,\"HealthyMs\":52}}");
            Check(bandAt40.Governor.OverBudgetMs == 30f && bandAt40.Governor.HealthyMs == 25f,
                "fps-40 default band is pulled down to the 30/25 tune the docs give");
            Check(EsLog.Warnings.Any(w => w.StartsWith("config corrected Governor.OverBudgetMs")
                && w.Contains("57 -> 30")),
                "the pulled-down band is named in the correction log");

            EsLog.Warnings.Clear();
            var bandTuned = LoadTemp("{\"Server\":{\"TargetFps\":60},\"Governor\":{\"OverBudgetMs\":20,\"HealthyMs\":15}}");
            Check(bandTuned.Governor.OverBudgetMs == 20f && bandTuned.Governor.HealthyMs == 15f,
                "an explicitly tuned band inside the target's ceiling is preserved");
            Check(EsLog.Warnings.Count == 0, "a tuned band that fits raises no correction");

            // The band's own upper endpoints stay legal whenever no frame target
            // is set (TargetFps 0 = vanilla 20 fps, idle 50 ms).
            EsLog.Warnings.Clear();
            var bandAtMax = LoadTemp("{\"Governor\":{\"OverBudgetMs\":500,\"HealthyMs\":495,\"EmergencyOverMs\":1000},"
                + "\"TickGuard\":{\"ShedAboveMs\":1000}}");
            Check(bandAtMax.Governor.OverBudgetMs == 500f && bandAtMax.Governor.HealthyMs == 495f
                && bandAtMax.Governor.EmergencyOverMs == 1000f,
                "Governor upper endpoints preserved verbatim at the vanilla frame rate");
            Check(EsLog.Warnings.Count == 0, "the widest band still loads silently with no target fps");

            // Unknown keys. A typo binds to nothing and keeps its default; the
            // load must still succeed (fail-soft per group) but must NAME the
            // key, or an operator who misspelled a knob has no way to learn it
            // never took effect. Names bind case-insensitively, so a
            // differently-cased key is not a typo and must not be reported.
            EsLog.Warnings.Clear();
            var typoed = LoadTemp(
                "{\"Enabld\":false,\"Pathfinding\":{\"GraphUpdateEveryTick\":9},\"Server\":{\"TargetFps\":20}}");
            Check(typoed.Enabled && typoed.Pathfinding.GraphUpdateEveryTicks == 4 && typoed.Server.TargetFps == 20,
                "unknown keys are ignored without losing the keys around them");
            // The record carries a trailing uptime stamp, so the warning text is
            // matched as a prefix: what this pins is WHICH key each warning names
            // and that it is its own line, not the absence of a field after it.
            Check(EsLog.Warnings.Count == 2
                && EsLog.Warnings.Any(w => w.StartsWith("config unknown key 'Enabld' ignored;"
                    + " that knob keeps its default (names are case-insensitive, spelling is not)",
                    StringComparison.Ordinal))
                && EsLog.Warnings.Any(w => w.StartsWith("config unknown key 'Pathfinding.GraphUpdateEveryTick' ignored;"
                    + " that knob keeps its default (names are case-insensitive, spelling is not)",
                    StringComparison.Ordinal)),
                "each unknown key is named on its own warning line, with its section path");

            EsLog.Warnings.Clear();
            var recased = LoadTemp("{\"enabled\":false,\"ailod\":{\"fullaidistsq\":50}}");
            Check(!recased.Enabled && recased.AiLod.FullAiDistSq == 50f,
                "a differently-cased key still binds, as the deserializer documents");
            Check(EsLog.Warnings.Count == 0, "a recased key is not reported as unknown");

            // The shipped template must be clean by construction: its keys are
            // the documented surface, so a regression here means CONFIG.md and
            // config/efficientserver.json drifted apart again.
            var template = ConfigTemplatePath();
            if (template == null)
            {
                Console.WriteLine("SKIP: shipped-template unknown-key check (no source tree above this binary)");
            }
            else
            {
                var shipped = ServerPerfConfig.UnknownKeys(File.ReadAllText(template, Encoding.UTF8));
                Check(shipped.Count == 0,
                    "shipped template names no unknown key: " + string.Join(", ", shipped));
            }

            // A non-object document has no keys to name and must not turn the
            // scan into a throw; the deserializer still decides what it means.
            var notAnObject = LoadTempTracked("[1,2,3]");
            Check(notAnObject != null && notAnObject.Enabled, "array document -> defaults, no throw");
            Check(EsLog.Errors.Count == 1 && NamesExceptionType(EsLog.Errors[0]),
                "array document reports the deserializer failure, not a scan failure");

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

            CheckGovernorTiers();

            if (_list)
            {
                Console.WriteLine($"listed {_selected} check name(s); nothing was asserted");
                return 0;
            }
            if (_failures == 0)
            {
                // A filter that selected nothing leaves the summary to Main,
                // which turns the empty selection into a named error instead
                // of a PASS line nobody can act on.
                if (_matcher == null || _selected > 0)
                {
                    Console.WriteLine(_matcher != null
                        ? $"PASS: {_selected} selected check(s)"
                        : "PASS: all Config Load/Normalize checks");
                }
                return 0;
            }
            Console.WriteLine(_matcher != null
                ? $"FAILED: {_failures} of {_selected} selected check(s)"
                : $"FAILED: {_failures} check(s)");
            return 1;
        }
    }
}
