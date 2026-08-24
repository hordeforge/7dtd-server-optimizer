using System;

namespace EfficientServer
{
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

        public static void Log(string msg)
        {
            Emit(global::Log.Out, msg);
        }

        // Recoverable problems an operator must notice when grepping the log for
        // WARNING: config corrections, skipped/missing optional targets, failed
        // applies that fell back to vanilla behavior, and engaged opt-in emergency
        // levers (governor tier-2 animator emergency, tick-guard entity sheds).
        public static void Warn(string msg)
        {
            Emit(global::Log.Warning, msg);
        }

        // Failures that leave a patch group INACTIVE or the mod partially broken:
        // version drift, patch application exceptions, init aborts.
        public static void Error(string msg)
        {
            Emit(global::Log.Error, msg);
        }

        // The game's Log static writes to the dedicated log file and console; if it
        // is unavailable (very early init, odd host), fall back to stdout rather
        // than losing the line.
        static void Emit(Action<string> sink, string msg)
        {
            string line = LogPrefix + msg;
            try { sink(line); }
            catch { Console.WriteLine(line); }
        }
    }
}
