using System;

namespace EfficientServer
{
    /// <summary>Log severity channel for the mod's single <see cref="EsLog.Emit"/>.</summary>
    internal enum LogLevel { Info, Warn, Error }

    /// <summary>
    /// The one logging surface of the mod: every diagnostic line goes through
    /// here so the prefix and the three severity channels stay uniform. Kept
    /// separate from <see cref="ModApi"/> so low-level modules (Config) can log
    /// without depending on the mod orchestrator. The line's SHAPE (one record
    /// per line, uptime stamp) is <see cref="LogLine"/>'s, so the guarantee
    /// holds for every caller of <see cref="Emit"/> rather than depending on each
    /// one wrapping its own message.
    /// </summary>
    internal static class EsLog
    {
        // Single source of the mod prefix so console echo and log lines stay
        // greppable under one tag across all three severity channels.
        public const string LogPrefix = "[EfficientServer] ";

        // Recoverable problems an operator must notice when grepping the log for
        // WARNING (config corrections, failed applies that fell back to
        // vanilla) route to Warn; failures that leave the mod partially broken
        // route to Error. A patch group that cannot find its target is Warn,
        // not Error: the mod keeps running on vanilla, and the init sequence
        // emits one such line per unmatchable group, so Error would bury the
        // rest of the startup log under expected notices. The game's Log
        // static writes to the dedicated log file and console; if it is unavailable
        // (very early init, odd host), fall back to stdout rather than losing the line.
        // Bound once: a method-group conversion to Action<string> allocates a new
        // delegate on every evaluation, and Emit is called from per-tick and
        // per-entity paths (TickGuardPatch's per-tick shed gate, the
        // ClientListSnapshotPatch receive thread), so resolving the sink per
        // call put one short-lived delegate in every log line's path.
        static readonly Action<string> InfoSink = global::Log.Out;
        static readonly Action<string> WarnSink = global::Log.Warning;
        static readonly Action<string> ErrorSink = global::Log.Error;

        // Banner guard, not a gate: the first sink failure explains itself, and
        // every later one is silent because the explanation is already on the
        // only sink that still works. Volatile because Emit is called from the
        // LiteNetLib receive thread as well as the main thread.
        static volatile bool _sinkFailureReported;

        public static void Emit(LogLevel severity, string msg)
        {
            Action<string> sink = severity == LogLevel.Warn ? WarnSink
                : severity == LogLevel.Error ? ErrorSink
                : InfoSink;
            // Rendered here rather than at each call site: several sites report a
            // caught exception by concatenating it, and an unwrapped ToString()
            // would put the continuation lines of one record into the log as
            // separate untimestamped records (see LogLine).
            string line = LogPrefix + LogLine.Format(msg);
            try { sink(line); }
            // The game Log static is the only sink that reaches the dedicated log
            // file, and it is unavailable very early in init; a line the operator
            // would need is worth more on stdout than the reason it failed here.
            // The FIRST failure also names the cause, because a mod whose lines
            // have stopped reaching the log file is otherwise invisible: the
            // operator sees an old last-written timestamp and no explanation.
            catch (Exception ex)
            {
                if (!_sinkFailureReported)
                {
                    _sinkFailureReported = true;
                    Console.WriteLine(LogPrefix + "game log sink unavailable [" + ex.GetType().Name
                        + "]: " + ex.Message + " - from here on this mod's lines go to the console ONLY,"
                        + " not to the server log file (first line: " + line + ")");
                }
                Console.WriteLine(line);
            }
        }
    }
}
