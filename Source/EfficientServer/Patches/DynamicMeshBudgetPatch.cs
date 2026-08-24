using System;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Lower DynamicMesh per-frame budgets on dedicated so mesh work cannot dominate ticks.
    /// The four settings are plain stock statics (not patched code), so a disable
    /// must actively restore what was there before our first apply - otherwise the
    /// budgets would outlive the config that set them and `es reload` could not
    /// take DynamicMesh.Enabled back to false live.
    /// </summary>
    public static class DynamicMeshBudgetPatch
    {
        static bool _captured;
        static bool _applied;
        static bool _onlyPlayerAreas;
        static int _buffer;
        static int _loadMs;
        static int _syncs;

        public static void ApplyBudgets()
        {
            var cfg = ModApi.Config != null ? ModApi.Config.DynamicMesh : null;
            if (!ModApi.ShouldRun() || cfg == null || !cfg.Enabled)
            {
                RestoreStock();
                return;
            }

            try
            {
                CaptureStockOnce();
                // Already applied with these exact values (repeat `es reload`):
                // stay silent, matching the reload contract that the re-applied
                // knobs log only real changes.
                if (_applied
                    && DynamicMeshSettings.OnlyPlayerAreas == cfg.OnlyPlayerAreas
                    && DynamicMeshSettings.PlayerAreaChunkBuffer == cfg.PlayerAreaChunkBuffer
                    && DynamicMeshSettings.MaxRegionLoadMsPerFrame == cfg.MaxRegionLoadMsPerFrame
                    && DynamicMeshServer.MaxActiveSyncs == cfg.MaxActiveSyncs)
                    return;
                // No ad-hoc clamps here: ServerPerfConfig.Normalize owns these
                // ranges (floors included), and every config reaching this method
                // has passed through it.
                DynamicMeshSettings.OnlyPlayerAreas = cfg.OnlyPlayerAreas;
                DynamicMeshSettings.PlayerAreaChunkBuffer = cfg.PlayerAreaChunkBuffer;
                DynamicMeshSettings.MaxRegionLoadMsPerFrame = cfg.MaxRegionLoadMsPerFrame;
                DynamicMeshServer.MaxActiveSyncs = cfg.MaxActiveSyncs;
                _applied = true;
                EsLog.Log(
                    "mesh budgets: OnlyPlayerAreas=" + DynamicMeshSettings.OnlyPlayerAreas +
                    " buf=" + DynamicMeshSettings.PlayerAreaChunkBuffer +
                    " loadMs=" + DynamicMeshSettings.MaxRegionLoadMsPerFrame +
                    " syncs=" + DynamicMeshServer.MaxActiveSyncs);
            }
            catch (Exception ex)
            {
                EsLog.Warn("mesh budget apply failed [" + ex.GetType().Name + "]: " + ex.Message);
            }
        }

        // Remember the stock values right before our FIRST overwrite; capture failure
        // leaves us uncaptured and RestoreStock a no-op (never invent replacements).
        static void CaptureStockOnce()
        {
            if (_captured) return;
            _onlyPlayerAreas = DynamicMeshSettings.OnlyPlayerAreas;
            _buffer = DynamicMeshSettings.PlayerAreaChunkBuffer;
            _loadMs = DynamicMeshSettings.MaxRegionLoadMsPerFrame;
            _syncs = DynamicMeshServer.MaxActiveSyncs;
            _captured = true;
        }

        // Undo one prior apply (no-op when we never applied or already restored).
        static void RestoreStock()
        {
            if (!_applied) return;
            try
            {
                DynamicMeshSettings.OnlyPlayerAreas = _onlyPlayerAreas;
                DynamicMeshSettings.PlayerAreaChunkBuffer = _buffer;
                DynamicMeshSettings.MaxRegionLoadMsPerFrame = _loadMs;
                DynamicMeshServer.MaxActiveSyncs = _syncs;
                _applied = false;
                EsLog.Log("mesh budgets restored to stock (dynamic mesh group disabled)");
            }
            catch (Exception ex)
            {
                EsLog.Warn("mesh budget restore failed [" + ex.GetType().Name + "]: " + ex.Message);
            }
        }
    }
}
