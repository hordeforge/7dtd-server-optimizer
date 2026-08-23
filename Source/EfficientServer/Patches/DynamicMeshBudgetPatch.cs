using System;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Lower DynamicMesh per-frame budgets on dedicated so mesh work cannot dominate ticks.
    /// </summary>
    public static class DynamicMeshBudgetPatch
    {
        public static void ApplyBudgets()
        {
            if (!ModApi.ShouldRun()) return;
            var cfg = ModApi.Config.DynamicMesh;
            if (cfg == null || !cfg.Enabled) return;

            try
            {
                DynamicMeshSettings.OnlyPlayerAreas = cfg.OnlyPlayerAreas;
                DynamicMeshSettings.PlayerAreaChunkBuffer = cfg.PlayerAreaChunkBuffer;
                DynamicMeshSettings.MaxRegionLoadMsPerFrame = Math.Max(1, cfg.MaxRegionLoadMsPerFrame);
                DynamicMeshServer.MaxActiveSyncs = Math.Max(1, cfg.MaxActiveSyncs);
                ModApi.Log(
                    "mesh budgets: OnlyPlayerAreas=" + DynamicMeshSettings.OnlyPlayerAreas +
                    " buf=" + DynamicMeshSettings.PlayerAreaChunkBuffer +
                    " loadMs=" + DynamicMeshSettings.MaxRegionLoadMsPerFrame +
                    " syncs=" + DynamicMeshServer.MaxActiveSyncs);
            }
            catch (Exception ex)
            {
                ModApi.Warn("mesh budget apply failed [" + ex.GetType().Name + "]: " + ex.Message);
            }
        }
    }
}
