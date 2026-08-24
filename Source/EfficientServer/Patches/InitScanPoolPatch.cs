using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Pathfinding;

namespace EfficientServer.Patches
{
    /// <summary>
    /// UNSAFE lever (bang-for-buck: the #1 large-allocation / megapause feeder).
    /// LayerGridGraph.ScanInternal rebuilds the grid's node array with
    /// `newarr LevelGridNode[width*depth*layerCount]` on every grid move. The grid
    /// dimensions are fixed, so the array size is constant across moves - it is
    /// re-minted purely because the scan always allocates fresh. This transpiler
    /// reroutes that single `newarr` through <see cref="ReuseOrAlloc"/>, which reuses
    /// the graph's existing node array (cleared to null = identical to a fresh array)
    /// when the size matches. Concurrency is safe: scans run under AstarPath's
    /// work-item lock, so no path worker reads `graph.nodes` mid-scan.
    ///
    /// Targets a compiler-generated iterator MoveNext in the EXTERNAL
    /// AstarPathfindingProject.dll, so it is fragile: it fails visibly (throws ->
    /// MISSING) if the exact `newarr LevelGridNode` is not found, and it is gated by
    /// config (default off) + must pass a nav fidelity check. Code -> EAC-off.
    /// </summary>
    [HarmonyPatch]
    public static class InitScanPoolPatch
    {
        static MethodBase TargetMethod()
        {
            MethodInfo scan = AccessTools.Method(typeof(LayerGridGraph), "ScanInternal");
            return scan != null ? AccessTools.EnumeratorMoveNext(scan) : null;
        }

        static bool Prepare() => TargetMethod() != null;

        static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            // The graph is the iterator's <>4__this field.
            FieldInfo thisField = AccessTools.GetDeclaredFields(original.DeclaringType)
                .Find(f => f.Name.Contains("__this") && f.FieldType == typeof(LayerGridGraph));
            MethodInfo reuse = AccessTools.Method(typeof(InitScanPoolPatch), nameof(ReuseOrAlloc));

            int swapped = 0;
            foreach (CodeInstruction ins in instructions)
            {
                if (thisField != null
                    && ins.opcode == OpCodes.Newarr
                    && ins.operand is Type t && t == typeof(LevelGridNode))
                {
                    // Stack before newarr: [.., graphForStfld, count]. Push the graph
                    // and call ReuseOrAlloc(count, graph) -> [.., graphForStfld, array].
                    swapped++;
                    yield return new CodeInstruction(OpCodes.Ldarg_0) { labels = ins.labels };
                    yield return new CodeInstruction(OpCodes.Ldfld, thisField);
                    yield return new CodeInstruction(OpCodes.Call, reuse);
                }
                else
                {
                    yield return ins;
                }
            }
            EsLog.Log("InitScanPoolPatch: rerouted " + swapped + " LevelGridNode newarr");
            if (swapped == 0)
                throw new InvalidOperationException(
                    "InitScanPoolPatch: `newarr LevelGridNode` not found in ScanInternal; "
                    + "the A* scan changed - patch inactive.");
        }

        // count is on the stack under the pushed graph; signature (int, graph).
        public static LevelGridNode[] ReuseOrAlloc(int count, LayerGridGraph graph)
        {
            PathfindingConfig cfg = ModApi.Config != null ? ModApi.Config.Pathfinding : null;
            if (!ModApi.ShouldRun() || cfg == null || !cfg.PoolInitScanNodes)
                return new LevelGridNode[count]; // vanilla when disabled
            LevelGridNode[] existing = graph != null ? graph.nodes : null;
            if (existing != null && existing.Length == count)
            {
                Array.Clear(existing, 0, count); // null-fill == fresh newarr; scan re-populates
                return existing;
            }
            return new LevelGridNode[count];
        }
    }
}
