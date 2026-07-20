using System;
using System.Collections.Generic;

namespace EfficientServer
{
    internal static class ConsoleCommandUtil
    {
        public static string Arg(List<string> args, int index, bool lower = false)
        {
            if (args == null || index < 0 || index >= args.Count) return "";
            string value = (args[index] ?? "").Trim();
            return lower ? value.ToLowerInvariant() : value;
        }

        public static void Output(string message)
        {
            string value = message ?? "";
            try
            {
                var console = SingletonMonoBehaviour<SdtdConsole>.Instance;
                if (console != null)
                    foreach (string line in value.Split(new[] { '\n' }, StringSplitOptions.None))
                        console.Output(line);
            }
            catch (Exception ex)
            {
                ModApi.Log("console output failed: " + ex.Message);
            }
            ModApi.Log(value.Replace("\n", " | "));
        }
    }
}
