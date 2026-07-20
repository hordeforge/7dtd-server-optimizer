using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Per-tick chunk-send throttle (P6). ChunkManager.SendChunksToClients drains each
    /// client's pending-chunk list and synchronously encodes each chunk through
    /// NetPackageChunk.Setup (Chunk.write on the sim thread). Vanilla batches up to
    /// three packages per observer per tick - the `ldc.i4.3` guarding the batch `bge`
    /// in the send loop. When many clients transfer in at once the global cost is
    /// (observers x batch) synchronous encodes per tick, a spike every other player
    /// feels as a hitch. This transpiler routes that single batch constant through
    /// config so an operator can LOWER it (spread a join transfer across more ticks,
    /// smaller per-tick spike, slightly slower per-client transfer) or raise it.
    ///
    /// Default 3 = vanilla (byte-identical behavior). The constant is unique in the
    /// method, so the swap is unambiguous and fail-visible (throws -> MISSING) if the
    /// chunk send loop changes on a new build. Code -> EAC-off.
    /// </summary>
    [HarmonyPatch(typeof(ChunkManager), "SendChunksToClients")]
    public static class ChunkSendThrottlePatch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            MethodInfo cap = AccessTools.Method(typeof(ChunkSendThrottlePatch), nameof(BatchCap));
            int swapped = 0;
            for (int i = 0; i < code.Count; i++)
            {
                // The batch cap is the only `ldc.i4.3` and it feeds a `bge` (packages
                // this observer >= 3 -> stop batching). Match both to stay unambiguous.
                bool nextIsBge = i + 1 < code.Count && IsBge(code[i + 1].opcode);
                if (code[i].opcode == OpCodes.Ldc_I4_3 && nextIsBge)
                {
                    code[i] = new CodeInstruction(OpCodes.Call, cap)
                    {
                        labels = code[i].labels,
                        blocks = code[i].blocks,
                    };
                    swapped++;
                }
            }
            ModApi.Log("ChunkSendThrottlePatch: rerouted " + swapped + " chunk batch-cap constant");
            if (swapped != 1)
                throw new InvalidOperationException(
                    "ChunkSendThrottlePatch: expected exactly one `ldc.i4.3 ; bge` batch cap in "
                    + "SendChunksToClients, found " + swapped + " - the send loop changed, patch inactive.");
            return code;
        }

        static bool IsBge(OpCode op) =>
            op == OpCodes.Bge || op == OpCodes.Bge_S || op == OpCodes.Bge_Un || op == OpCodes.Bge_Un_S;

        // Called once per batched package in the send loop; a cheap config read. Returns
        // the vanilla 3 whenever the mod is inactive or the config is absent, so the
        // default path is byte-identical to stock.
        public static int BatchCap()
        {
            WorldTransferConfig cfg = ModApi.Config != null ? ModApi.Config.WorldTransfer : null;
            if (!ModApi.ShouldRun() || cfg == null)
                return 3;
            return cfg.ChunkPackagesPerObserverPerTick;
        }
    }
}
