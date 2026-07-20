using System;
using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Post-start setup. NOT a Harmony patch - invoked via the sanctioned
    /// <c>ModEvents.GameStartDone</c> lifecycle hook (registered in ModApi), so
    /// there is no IL patch just to get "run after startup" timing. It still
    /// installs the optional dedicated skips via Harmony (those genuinely need
    /// per-call interception) and switches on incremental GC if configured.
    /// </summary>
    public static class GameStartPatch
    {
        public static void OnGameStartDone(ref ModEvents.SGameStartDoneData _data)
        {
            try
            {
                DynamicMeshBudgetPatch.ApplyBudgets();
                var h = new Harmony(ModApi.HarmonyId + ".optional");
                DedicatedSkipPatch.ApplyOptional(h);
                GcIncremental.Apply(ModApi.Config != null ? ModApi.Config.Gc : null);
                GcDiagnostics.StartMegapauseTest(ModApi.Config != null ? ModApi.Config.Diagnostics : null);
            }
            catch (Exception ex)
            {
                ModApi.Log("GameStartDone handler: " + ex.Message);
            }
        }
    }
}
