using System;

namespace EfficientServer
{
    /// <summary>Log severity channel for the mod's single <see cref="EsLog.Emit"/>.</summary>
    internal enum LogLevel { Info, Warn, Error }

    /// <summary>
    /// The one logging surface of the mod: every diagnostic line goes through
    /// here so the prefix and the three severity channels stay uniform. Kept
    /// separate from <see cref="ModApi"/> so low-level modules (Config) can log
    /// without depending on the mod orchestrator.
    /// </summary>
    internal static class EsLog
    {
        // Single source of the mod prefix so console echo and log lines stay
        // greppable under one tag across all three severity channels.
        public const string LogPrefix = "[EfficientServer] ";

        // Recoverable problems an operator must notice when grepping the log for
        // WARNING (config corrections, elided optional targets, failed applies that
        // fell back to vanilla) route to Warn; failures that leave a patch group
        // INACTIVE or the mod partially broken route to Error. The game's Log
        // static writes to the dedicated log file and console; if it is unavailable
        // (very early init, odd host), fall back to stdout rather than losing the line.
        // Bound once: a method-group conversion to Action<string> allocates a new
        // delegate on every evaluation, and Emit is called from per-tick and
        // per-entity paths (a disabled governor still logs its transitions at tick
        // rate, and the skip patches run inside the tick loop), so resolving the
        // sink per call put one short-lived delegate in every log line's path.
        static readonly Action<string> InfoSink = global::Log.Out;
        static readonly Action<string> WarnSink = global::Log.Warning;
        static readonly Action<string> ErrorSink = global::Log.Error;

        public static void Emit(LogLevel severity, string msg)
        {
            Action<string> sink = severity == LogLevel.Warn ? WarnSink
                : severity == LogLevel.Error ? ErrorSink
                : InfoSink;
            string line = LogPrefix + msg;
            try { sink(line); }
            // The game Log static is the only sink that reaches the dedicated log
            // file, and it is unavailable very early in init; a line the operator
            // would need is worth more on stdout than the reason it failed here.
            catch { Console.WriteLine(line); }
        }
    }
}
