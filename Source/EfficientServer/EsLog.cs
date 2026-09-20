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
        public static void Emit(LogLevel severity, string msg)
        {
            Action<string> sink = severity == LogLevel.Warn ? (Action<string>)global::Log.Warning
                : severity == LogLevel.Error ? (Action<string>)global::Log.Error
                : (Action<string>)global::Log.Out;
            string line = LogPrefix + msg;
            try { sink(line); }
            catch { Console.WriteLine(line); }
        }
    }
}
