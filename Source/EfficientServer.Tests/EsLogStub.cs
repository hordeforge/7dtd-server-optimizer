using System.Collections.Generic;

// Stub the only external symbols Config.cs touches (game-type-free), so the real
// Config source compiles and runs under the plain .NET SDK. Warnings are recorded
// so tests can pin which channel each config problem is reported on.
//
// This is a hand-maintained mirror of Source/EfficientServer/EsLog.cs, kept
// separate because the real one calls the game's global::Log (LogLibrary.dll),
// which this project deliberately does not reference. Nothing checks the two
// agree, so a signature change to Emit or a new LogLevel member must be copied
// here by hand, or this project keeps compiling against a shape the net48
// build no longer has.
//
// Same filename, same namespace, same declaration order as the real EsLog.cs so
// a side-by-side diff is the drift check the gate above cannot automate.
namespace EfficientServer
{
    internal enum LogLevel { Info, Warn, Error }

    internal static class EsLog
    {
        public static readonly List<string> Warnings = new List<string>();

        public static void Emit(LogLevel severity, string msg)
        {
            if (severity == LogLevel.Warn) Warnings.Add(msg);
        }
    }
}
