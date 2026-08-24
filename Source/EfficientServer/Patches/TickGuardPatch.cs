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
        static readonly List<(float distSq, Entity entity)> Scratch = new List<(float, Entity)>();

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
            Shed(cfg, emaMs);
        }

        static void Shed(TickGuardConfig cfg, double emaMs)
        {
            World world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null) return;
            List<Entity> entities = world.Entities.list;
            List<EntityPlayer> players = world.Players.list;
            if (players.Count == 0) return;

            Scratch.Clear();
            int enemies = 0;
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
                Scratch.Add((best, enemy));
            }
            if (enemies <= cfg.MinEnemiesKept)
                return;

            // Farthest-from-any-player first; never below the keep floor.
            Scratch.Sort((a, b) => b.distSq.CompareTo(a.distSq));
            int shed = Mathf.Min(cfg.ShedBatch, enemies - cfg.MinEnemiesKept);
            for (int i = 0; i < shed; i++)
                world.RemoveEntity(Scratch[i].entity.entityId, EnumRemoveEntityReason.Despawned);
            ShedTotal += shed;
            // WARNING, not info: shedding removes entities (a real gameplay impact)
            // and only fires while the tick is collapsing, rate-bounded by
            // CooldownTicks - the channel an operator greps when players report
            // vanished hordes.
            // Invariant floats: same log-parsing convention as the governor lines.
            ModApi.Warn($"TickGuard: tick EMA {emaMs.ToString("F1", CultureInfo.InvariantCulture)}ms > "
                + $"{cfg.ShedAboveMs.ToString(CultureInfo.InvariantCulture)}ms - shed {shed} "
                + $"farthest enemies ({enemies} -> {enemies - shed}, lifetime {ShedTotal})");
            Scratch.Clear();
        }
    }
}
