using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// AstarManager.UpdateGraphPos queues a follow-graph for a rescan (NavGraph.Scan
    /// -> AstarVoxelGrid.InitScan, the #1 heap allocator under load) only after the
    /// grid drifts more than a dead-zone from the observer: it compares the grid-move
    /// SqrMagnitude against a constant 100 (squared grid units) and skips the enqueue
    /// below it. Raising that dead-zone means a grid rescans only after moving more
    /// cells, cutting InitScan CPU AND allocation from the frequency side. This
    /// complements the P1 cadence throttle (they multiply): P1 lowers how often
    /// UpdateGraphs runs, P2 lowers the per-visit rescan probability.
    ///
    /// Strands nothing: a below-threshold grid simply is not queued this visit and is
    /// re-tested next visit; the IsFullUpdateNeeded branch snaps fresh grids
    /// immediately, bypassing the dead-zone. The only cost is a slightly staler
    /// walkability window on the leading edge of fast motion - the same failure class
    /// the P1 fidelity gate already exercises. Ships at default 100 (= vanilla).
    /// Server-internal, no wire change (vanilla / EAC client connects); code = EAC-off.
    /// </summary>
    [HarmonyPatch(typeof(AstarManager), "UpdateGraphPos",
        new[] { typeof(AstarVoxelGrid), typeof(UnityEngine.Vector2) })]
    public static class AstarMoveThresholdPatch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo threshold = AccessTools.Method(typeof(AstarMoveThresholdPatch), nameof(Threshold));
            int swapped = 0;
            foreach (CodeInstruction ins in instructions)
            {
                // The sole ldc.r4 in UpdateGraphPos is the 100 sqr-unit dead-zone
                // compared against the grid-move SqrMagnitude before the enqueue.
                if (swapped == 0 && ins.opcode == OpCodes.Ldc_R4
                    && ins.operand is float f && f == 100f)
                {
                    swapped++;
                    // Route through the live getter (same pattern as
                    // ChunkSendThrottlePatch.BatchCap) so `es reload` retunes it
                    // without a restart, instead of freezing the init-time value.
                    yield return new CodeInstruction(OpCodes.Call, threshold)
                    { labels = ins.labels, blocks = ins.blocks };
                }
                else
                {
                    yield return ins;
                }
            }
            ModApi.Log("AstarMoveThresholdPatch: rerouted " + swapped + " rescan dead-zone constant(s)");
            // Matched-but-untransformed is a silent failure; fail loudly on drift so
            // it surfaces as MISSING rather than pretending the threshold is applied.
            if (swapped == 0)
                throw new InvalidOperationException(
                    "AstarMoveThresholdPatch: ldc.r4 100 rescan threshold not found in "
                    + "UpdateGraphPos; target drifted - patch inactive.");
        }

        // Replaces the vanilla constant in the comparison; returns the vanilla 100
        // whenever the mod is inactive or the config absent, so the default path is
        // byte-equivalent to stock.
        public static float Threshold()
        {
            PathfindingConfig cfg = ModApi.Config != null ? ModApi.Config.Pathfinding : null;
            if (!ModApi.ShouldRun() || cfg == null)
                return 100f;
            return cfg.MoveRescanThresholdSq;
        }
    }
}
