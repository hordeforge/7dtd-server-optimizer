using System.Collections.Generic;
using System.Text;

namespace EfficientServer
{
    /// <summary>
    /// Registry of subsystems that failed OPEN and are running degraded. Every
    /// fail-open path in the mod (AI alert probe, CheckDespawn fallback, LOD cloth
    /// toggle, client-list snapshot, GC ceiling, target-fps apply, the dedicated-host
    /// read, an absent dedicated-skip target) used to carry its
    /// own one-shot log flag, so the degradation was announced once at the moment it
    /// happened and then became invisible: nothing in the log and nothing in
    /// <c>es status</c> could say "AI LOD striding has been inactive since 09:14".
    /// An operator grepping after the fact had to catch the single line in a
    /// multi-hour log, and a live operator running <c>es status</c> could not see it
    /// at all.
    ///
    /// One registry replaces those flags. <see cref="Report"/> is the announce-once
    /// channel (first report for a key logs, later ones only count), so the log
    /// volume is unchanged, and <see cref="Summary"/> gives the operator the current
    /// degraded set with hit counts on the surface they already poll.
    ///
    /// Degradations are NOT cleared by <c>es reload</c>: every one of them is game
    /// API drift or a host-level limit, not a config value, and none is repaired by
    /// reading a new config file. They persist until restart, which is exactly how
    /// the fail-open sites already behaved. The one exception is a target that
    /// RESOLVES: a drift report for an absent method stays true only until the
    /// build has that method again, so <see cref="Clear"/> retires it at the
    /// apply that finds the target, rather than listing a repaired subsystem as
    /// degraded for the rest of the process.
    ///
    /// Thread-safe: the client-list snapshot path reports from the LiteNetLib
    /// receive thread while the console and lifecycle paths report on the main
    /// thread, so every access goes through one lock.
    ///
    /// Game-type-free (no game symbols), so the unit harness covers it directly;
    /// see EfficientServer.Tests.csproj.
    /// </summary>
    internal static class Degrade
    {
        static readonly object Gate = new object();
        // Insertion-ordered so `es status` lists degradations in the order they
        // happened, which reads better than an alphabet during an incident.
        static readonly List<string> Order = new List<string>();
        static readonly Dictionary<string, int> Counts = new Dictionary<string, int>();
        static readonly Dictionary<string, string> FirstMessage = new Dictionary<string, string>();

        /// <summary>
        /// Record a degradation. Returns true the first time a key is seen, which
        /// is the caller's cue to log the explanation; every later call for the same
        /// key only bumps the count, so a per-tick or per-request fail-open path
        /// costs one increment and never floods the log.
        /// </summary>
        public static bool Report(string key, string message)
        {
            if (string.IsNullOrEmpty(key)) return false;
            bool first;
            lock (Gate)
            {
                first = !Counts.ContainsKey(key);
                if (first)
                {
                    Order.Add(key);
                    FirstMessage[key] = message;
                    Counts[key] = 1;
                }
                else
                {
                    Counts[key] = Counts[key] + 1;
                }
            }
            return first;
        }

        /// <summary>Times this key has been reported since process start.</summary>
        public static int Count(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            lock (Gate) { int n; return Counts.TryGetValue(key, out n) ? n : 0; }
        }

        /// <summary>
        /// The current degraded set for <c>es status</c>: <c>none</c> when nothing
        /// is degraded, else space-free <c>key=count</c> pairs in occurrence order.
        /// Space-free because console output is machine-scraped (same contract as
        /// the animstate fields).
        /// </summary>
        public static string Summary()
        {
            lock (Gate)
            {
                if (Order.Count == 0) return "none";
                var sb = new StringBuilder();
                for (int i = 0; i < Order.Count; i++)
                {
                    if (i > 0) sb.Append('|');
                    sb.Append(Order[i]).Append('=').Append(Counts[Order[i]]);
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// The explanation logged with the first report for a key, or null when the
        /// key was never reported. Exposed so an operator tool can print cause
        /// alongside the count without re-deriving it from the log.
        /// </summary>
        public static string FirstReport(string key)
        {
            // `null!` rather than a `string?` annotation: the net48 project builds
            // with nullable annotations off, so a `?` here would not compile in
            // the shipping build that owns this file.
            if (string.IsNullOrEmpty(key)) return null!;
            lock (Gate)
            {
                // `var` on the out slot, not `out string?`: the net48 project
                // builds with nullable annotations off, so the annotation would
                // not compile there. The null-forgiving return above covers the
                // nullable-enabled test build.
                return FirstMessage.TryGetValue(key, out var m) ? m : null!;
            }
        }

        /// <summary>
        /// Retire one key: the degradation it recorded no longer holds. Returns
        /// true when the key was on record, so the caller can log the recovery
        /// instead of leaving the operator to notice the key is gone.
        /// </summary>
        public static bool Clear(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (Gate)
            {
                if (!Counts.Remove(key)) return false;
                FirstMessage.Remove(key);
                Order.Remove(key);
                return true;
            }
        }

        /// <summary>
        /// Drop every record. Only the unit harness calls this: each fixture needs a
        /// known-empty registry to assert against, and the production registry is
        /// process-lifetime state by design (see the class comment on reload).
        /// </summary>
        public static void Reset()
        {
            lock (Gate)
            {
                Order.Clear();
                Counts.Clear();
                FirstMessage.Clear();
            }
        }
    }
}
