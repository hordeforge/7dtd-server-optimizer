using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace EfficientServer
{
    /// <summary>
    /// Line shape for every <see cref="EsLog"/> write: ONE line per record, and
    /// the mod's uptime clock on it. Game-type-free (no game symbols) so the unit
    /// harness covers it directly; see EfficientServer.Tests.csproj.
    ///
    /// Two properties, both load-bearing for an operator reading the server log:
    ///
    /// <list type="bullet">
    /// <item>One record, one line. Several call sites report a caught exception by
    /// concatenating it, and <c>Exception.ToString()</c> embeds "\n   at ...". The
    /// game's log file prefixes its own timestamp per physical line, so the
    /// continuation lines of one record reach an aggregator as separate,
    /// untimestamped records: a stack trace splits across N entries, and a
    /// line-oriented grep for the failure returns only its first fragment.
    /// Collapsing the newlines keeps the record whole and keeps the record count
    /// equal to the event count.</item>
    /// <item>Same clock as <c>es status</c>. <c>uptimeS=</c> is the field name both
    /// surfaces use, so a line and a later status capture join on one number even
    /// though the log's wall-clock stamps and the console's do not share a
    /// timezone-independent format. Without it, a governor or TickGuard line
    /// cannot be placed against the uptime the server is at now, and after a
    /// restart of the log reader, or across a 6-day uptime with three world
    /// loads, the position of a line in the stream is the only age information
    /// available.</item>
    /// </list>
    ///
    /// The uptime suffix is appended, not prepended: every existing consumer
    /// (the harness assertions, the operator greps quoted in docs/PRODUCTION.md)
    /// matches on the message text, and a suffix leaves those prefixes intact.
    /// </summary>
    internal static class LogLine
    {
        /// <summary>Stands in for a line break inside a record (see the class comment).</summary>
        internal const string Continuation = " | ";

        // Bound once, at type init: Emit runs on the tick path, and building a
        // Stopwatch per line would put a clock read and an allocation in the
        // path of every log write.
        static readonly Stopwatch UptimeClock = Stopwatch.StartNew();

        // The source UptimeSeconds reads, so a replay can stamp records from a
        // virtual clock instead of the host's. Never null: SetUptimeClockSource
        // falls back to the stopwatch.
        static Func<double> _uptimeSource = () => UptimeClock.Elapsed.TotalSeconds;

        /// <summary>Seconds since the mod loaded; the shared age stamp.</summary>
        public static double UptimeSeconds { get { return _uptimeSource(); } }

        /// <summary>
        /// Replace the uptime source (seconds, any origin) so a caller driving
        /// its own clock stamps the same record the same way; null restores the
        /// stopwatch. Only the unit harness replaces it, and always back to null
        /// (see the process-lifetime note on <see cref="Degrade.Reset"/> for the
        /// same rule on shared state).
        /// </summary>
        public static void SetUptimeSource(Func<double> source)
        {
            _uptimeSource = source ?? (() => UptimeClock.Elapsed.TotalSeconds);
        }

        /// <summary>
        /// Render one record: newlines collapsed, then <c>uptimeS=&lt;seconds&gt;</c>
        /// appended. Integral seconds, invariant culture, matching the
        /// <c>uptimeS</c> field <c>es status</c> prints (so a log line and a status
        /// capture read the same number at the same moment) and the project's
        /// log convention of invariant numerics, so a comma-decimal host locale
        /// cannot reformat the stamp.
        /// </summary>
        public static string Format(string msg)
        {
            return Format(msg, UptimeSeconds);
        }

        /// <summary>
        /// <see cref="Format(string)"/> with the clock passed in, so a caller
        /// with a measurement of its own (a bench harness stamping elapsed time)
        /// does not have to race the shared one.
        /// </summary>
        public static string Format(string msg, double uptimeSeconds)
        {
            string body = Flatten(msg ?? "");
            // A message ending in a break would otherwise render a separator with
            // nothing after it, and the stamp would land after that dangling " | ".
            if (body.EndsWith(Continuation, StringComparison.Ordinal))
                body = body.Substring(0, body.Length - Continuation.Length);
            return body + " uptimeS="
                + uptimeSeconds.ToString("F0", CultureInfo.InvariantCulture);
        }

        // One pass, no intermediate string: "\r\n" is consumed as a single break
        // so a Windows-flavored exception does not leave an empty segment
        // between two separators, and a trailing break does not leave a
        // separator dangling at the end of the record.
        static string Flatten(string msg)
        {
            int first = msg.IndexOfAny(Breaks);
            if (first < 0) return msg;
            var sb = new StringBuilder(msg.Length);
            sb.Append(msg, 0, first);
            for (int i = first; i < msg.Length; i++)
            {
                char c = msg[i];
                if (c == '\r')
                {
                    if (i + 1 < msg.Length && msg[i + 1] == '\n') i++;
                    sb.Append(Continuation);
                }
                else if (c == '\n') sb.Append(Continuation);
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static readonly char[] Breaks = { '\r', '\n' };
    }
}
