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

        // Frames to wait before calling an applied budget lever inert. UpdateTick
        // runs every frame, so 1200 frames is 20 s at 60 fps and 60 s at the
        // vanilla 20 fps: long enough that a running subsystem has counted many
        // updates, short enough to surface inside one operator session.
        internal const int InertCheckFrames = 1200;

        static bool _inertReported;
        static bool _runningReported;

        /// <summary>Dynamic-mesh frame updates observed since mod load.</summary>
        public static long Updates { get { return DynamicMeshProbePatch.Updates; } }

        /// <summary>
        /// True once budgets applied and the probed updates never ran. Read by
        /// `es status` and the startup chain, so an inert lever has a reported
        /// state instead of only a log line an operator may have missed.
        /// </summary>
        public static bool Inert
        {
            get { return _applied && !DynamicMeshProbePatch.HasRun; }
        }

        /// <summary>
        /// One-shot liveness report for the budget lever, called once per
        /// UpdateTick frame from <see cref="TickClockPatch"/>. The dynamic-mesh
        /// methods resolve on V3.4.0 but nothing on the server calls them, so the
        /// lever would otherwise look applied while changing nothing. A later
        /// start (console) is reported too, so the pair of lines tells the whole
        /// story: inert after the window, or started and effective.
        /// </summary>
        public static void CheckLiveness()
        {
            if (!_applied) return;
            if (DynamicMeshProbePatch.HasRun)
            {
                if (!_runningReported)
                {
                    _runningReported = true;
                    EsLog.Emit(LogLevel.Info, "mesh budgets now in force: dynamic mesh has started ("
                        + DynamicMeshProbePatch.Updates + " updates)");
                }
                return;
            }
            if (_inertReported || TickClock.Ticks < InertCheckFrames) return;
            _inertReported = true;
            EsLog.Emit(LogLevel.Warn, "mesh budgets are inert: dynamic mesh has not run in "
                + InertCheckFrames + " UpdateTick frames (no server-side call site on this build; "
                + "the subsystem starts only from the console), so the DynamicMesh group changes nothing");
        }

        public static void ApplyBudgets()
        {
            ServerPerfConfig config = ModApi.Config;
            var cfg = config != null ? config.DynamicMesh : null;
            if (!ModApi.ShouldRun(config) || cfg == null || !cfg.Enabled)
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
                EsLog.Emit(LogLevel.Info, 
                    "mesh budgets: OnlyPlayerAreas=" + DynamicMeshSettings.OnlyPlayerAreas +
                    " buf=" + DynamicMeshSettings.PlayerAreaChunkBuffer +
                    " loadMs=" + DynamicMeshSettings.MaxRegionLoadMsPerFrame +
                    " syncs=" + DynamicMeshServer.MaxActiveSyncs);
            }
            catch (Exception ex)
            {
                EsLog.Emit(LogLevel.Warn, "mesh budget apply failed [" + ex.GetType().Name + "]: " + ex.Message);
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
                EsLog.Emit(LogLevel.Info, "mesh budgets restored to stock (dynamic mesh group disabled)");
            }
            catch (Exception ex)
            {
                EsLog.Emit(LogLevel.Warn, "mesh budget restore failed [" + ex.GetType().Name + "]: " + ex.Message);
            }
        }
    }
}
