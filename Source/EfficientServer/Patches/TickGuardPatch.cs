using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Emergency load-shedding (default off). When the tick interval stays above
    /// ShedAboveMs (a level the governor's throttles could not fix - the server is
    /// collapsing toward single-digit TPS), despawn the enemies FARTHEST from any
    /// player in small batches until the tick recovers. Uses the game's silent
    /// despawn path (WorldBase.RemoveEntity with EnumRemoveEntityReason.Despawned -
    /// the same mechanism as vanilla distance-despawn: no loot, no XP, no corpse),
    /// so a shed zombie simply ceases to exist, exactly as if it had wandered out
    /// of range.
    ///
    /// This trades gameplay (a thinner horde) for a running server: measured, the
    /// alternative at 2x the capacity ceiling is ~3 TPS for everyone. Every shed is
    /// logged with the EMA and count. Players in combat notice the farthest zombies
    /// vanishing before the closest ones - the least-visible possible cut.
    /// </summary>
    [HarmonyPatch(typeof(GameManager), "UpdateTick")]
    public static class TickGuardPatch
    {
        static readonly TickIntervalEma TickEma = new TickIntervalEma();
        static int _overTicks;
        static int _cooldown;

        // Reusable census scratch: (distSq, entityId) per living enemy, never the
        // entity itself, so a shed at least CooldownTicks apart pins nothing
        // between batches - a strong-reference scratch would hold up to
        // MinEnemiesKept despawned enemies alive until the next one.
        static readonly List<(float distSq, int entityId)> Census = new List<(float, int)>();

        // Live state for `es status`: lifetime shed count (the tick EMA shown in
        // `es status` comes from the governor's equivalent instance - see
        // TickIntervalEma for why each holder steps its own copy).
        public static long ShedTotal { get; private set; }

        static void Postfix()
        {
            TickGuardConfig cfg = ModApi.Config != null ? ModApi.Config.TickGuard : null;
            if (!ModApi.ShouldRun() || cfg == null || !cfg.Enabled)
                return;

            double emaMs = TickEma.Advance();
            if (_cooldown > 0) { _cooldown--; return; }

            if (emaMs <= cfg.ShedAboveMs)
            {
                _overTicks = 0;
                return;
            }
            if (++_overTicks < cfg.WindowTicks)
                return;

            _overTicks = 0;
            _cooldown = cfg.CooldownTicks;
            ShedOnce(cfg, emaMs);
        }

        static void ShedOnce(TickGuardConfig cfg, double emaMs)
        {
            // A suppressed shed is the one outcome an operator cannot infer from the
            // log: no shed line means either "never triggered" or "triggered and did
            // nothing", and those need different responses. Sheds are at least
            // CooldownTicks apart, so one line per suppression is bounded.
            World world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) { Suppressed(cfg, emaMs, "no world loaded"); return; }
            List<Entity> entities = world.Entities.list;
            List<EntityPlayer> players = world.Players.list;
            if (players.Count == 0) { Suppressed(cfg, emaMs, "no players online"); return; }

            int enemies = 0;
            Census.Clear();
            for (int i = 0; i < entities.Count; i++)
            {
                if (!(entities[i] is EntityEnemy enemy) || enemy.IsDead())
                    continue;
                enemies++;
                float best = float.MaxValue;
                Vector3 pos = enemy.position;
                for (int p = 0; p < players.Count; p++)
                {
                    float d = (players[p].position - pos).sqrMagnitude;
                    if (d < best) best = d;
                }
                Census.Add((best, enemy.entityId));
            }
            if (enemies <= cfg.MinEnemiesKept)
            {
                Suppressed(cfg, emaMs, "living enemies " + enemies
                    + " at or below keep floor " + cfg.MinEnemiesKept);
                return;
            }

            // Farthest-from-any-player first, lowest entityId first inside a
            // distance tie, so a batch boundary landing in a tie group cannot
            // cut it by World.Entities.list order; never below the keep floor.
            List<int> shedIds = ShedOrder.Select(Census,
                Mathf.Min(cfg.ShedBatch, enemies - cfg.MinEnemiesKept));
            for (int i = 0; i < shedIds.Count; i++)
                world.RemoveEntity(shedIds[i], EnumRemoveEntityReason.Despawned);
            int shed = shedIds.Count;
            ShedTotal += shed;
            // WARNING, not info: shedding removes entities (a real gameplay impact)
            // and only fires while the tick is collapsing, rate-bounded by
            // CooldownTicks - the channel an operator greps when players report
            // vanished hordes.
            // Invariant floats: same log-parsing convention as the governor lines.
            EsLog.Emit(LogLevel.Warn, $"TickGuard: tick EMA {emaMs.ToString("F1", CultureInfo.InvariantCulture)}ms > "
                + $"{cfg.ShedAboveMs.ToString(CultureInfo.InvariantCulture)}ms - shed {shed} "
                + $"farthest enemies ({enemies} -> {enemies - shed}, lifetime {ShedTotal})");
        }

        // The trigger fired (tick over budget) but the shed was withheld. Same
        // WARNING channel as a real shed: the server is still collapsing, which is
        // the operator-visible fact; the reason says which knob to raise. Lifetime
        // count is included so a log-only timeline can tell suppression from shed.
        static void Suppressed(TickGuardConfig cfg, double emaMs, string reason)
        {
            EsLog.Emit(LogLevel.Warn, $"TickGuard: tick EMA {emaMs.ToString("F1", CultureInfo.InvariantCulture)}ms > "
                + $"{cfg.ShedAboveMs.ToString(CultureInfo.InvariantCulture)}ms - shed SUPPRESSED "
                + $"({reason}; lifetime {ShedTotal})");
        }
    }
}
