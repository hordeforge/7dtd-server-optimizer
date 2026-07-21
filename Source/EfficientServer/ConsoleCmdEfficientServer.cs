using System.Collections.Generic;

namespace EfficientServer
{
    /// <summary>
    /// Operator console command (auto-discovered by the game's console from loaded
    /// assemblies). Every patch reads the live config object per call, so a file
    /// reload takes effect immediately - no restart. Usage (console or telnet):
    ///   es reload   - re-read Config/efficientserver.json and apply it
    ///   es status   - print the active lever values
    /// </summary>
    public class ConsoleCmdEfficientServer : ConsoleCmdAbstract
    {
        public override string[] getCommands() => new[] { "efficientserver", "es" };
        public override string getDescription() =>
            "EfficientServer: 'es reload' re-reads the config (applies live), 'es status' shows active levers";

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            string sub = _params.Count > 0 ? _params[0].ToLowerInvariant() : "status";
            if (sub == "reload")
            {
                ModApi.ReloadConfig();
                SdtdConsole.Instance.Output("[EfficientServer] config reloaded");
                sub = "status";
            }
            if (sub == "status")
            {
                ServerPerfConfig c = ModApi.Config;
                if (c == null) { SdtdConsole.Instance.Output("[EfficientServer] no config"); return; }
                SdtdConsole.Instance.Output(
                    $"[EfficientServer] enabled={c.Enabled} | graphEvery={c.Pathfinding.GraphUpdateEveryTicks} "
                    + $"rescanSq={c.Pathfinding.MoveRescanThresholdSq} poolInitScan={c.Pathfinding.PoolInitScanNodes} | "
                    + $"fastSend={c.Network.FastSingleTargetSend} stride={c.Network.EntityDistributionEveryTicks} | "
                    + $"chunkBatch={c.WorldTransfer.ChunkPackagesPerObserverPerTick} | "
                    + $"governor={c.Governor.Enabled} tickGuard={c.TickGuard.Enabled} | "
                    + $"gcGuard={c.Gc.SkipForcedCollect} explosionParticlesSkip={c.SkipOnDedicated.ExplosionParticles}");
            }
            else
            {
                SdtdConsole.Instance.Output("[EfficientServer] unknown subcommand; use: es reload | es status");
            }
        }
    }
}
