using System;
using System.Collections.Generic;

namespace EfficientServer
{
    internal static class ConsoleCommandUtil
    {
        // Subcommand/argument matching is case-insensitive everywhere, so the
        // lookup folds case itself; both callers are subcommand words.
        public static string Arg(List<string> args, int index)
        {
            if (args == null || index < 0 || index >= args.Count) return "";
            return (args[index] ?? "").Trim().ToLowerInvariant();
        }

        /// <summary>
        /// One choke point for command output that must outlive the console
        /// session: echoes to the live console AND persists via ModApi.Log, so
        /// state-changing commands (animprobe, rigprobe, benchgod) leave an audit
        /// trail in the server log for incident investigation. Read-only bulk
        /// output (status, animstate dumps) stays on SdtdConsole only.
        /// Pass text WITHOUT the mod prefix; both sinks get exactly one.
        /// </summary>
        public static void Output(string message)
        {
            try
            {
                var console = SingletonMonoBehaviour<SdtdConsole>.Instance;
                if (console != null)
                    console.Output(ModApi.LogPrefix + message);
            }
            catch (Exception ex)
            {
                ModApi.Warn("console output failed [" + ex.GetType().Name + "]: " + ex.Message);
            }
            ModApi.Log(message);
        }
    }
}
