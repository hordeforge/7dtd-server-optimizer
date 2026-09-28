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

        // Re-base the shed window when a NEW world loads (called from the
        // GameStartDone hook, next to GovernorPatch.OnWorldChanged). Both counters
        // and the EMA describe the tick history of the world that just unloaded:
        // the interval average spans the whole world load (see
        // TickIntervalEma.Reseed), and the over-budget window is counted in ticks of
        // that world. Carried over, they can complete a shed window on the new
        // world's spawn load alone - the most irreversible decision the mod makes,
        // removing entities, fired before anyone has even seen the new world - and
        // a cooldown from the old world would suppress the new world's first
        // legitimate shed. ShedTotal is deliberately NOT reset: it is a lifetime
        // count an operator reads from `es status` and the log, not window state.
        // Main-thread only (the lifecycle hook fires on the same thread as the
        // UpdateTick postfix).
        public static void OnWorldChanged()
        {
            TickEma.Reseed();
            _overTicks = 0;
            _cooldown = 0;
        }

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
            ServerPerfConfig config = ModApi.Config;
            TickGuardConfig cfg = config != null ? config.TickGuard : null;
            if (!ModApi.ShouldRun(config) || cfg == null || !cfg.Enabled)
            {
                // Same re-base the governor does (see GovernorPatch.Postfix): no
                // tick is being measured while the gate is closed, so the average
                // must not span the closed period. Here the consequence is the
                // worst one the mod can produce - an `es reload` re-enabling the
                // guard after a long disable would open with the EMA far past
                // ShedAboveMs and shed living enemies on the very first window.
                TickEma.Reseed();
                return;
            }

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
            // Removal is IRREVERSIBLE and per-entity, so a throw part-way through
            // the batch is the one place this loop can leave the world in a state
            // no log line describes: the entities before the failure are gone, the
            // rest stay, and an unguarded loop escapes before ShedTotal is updated
            // and before the shed line is printed, so the lifetime count
            // under-reports what actually happened while the cooldown still starts.
            // Count what really left the world, keep going, and name the failure.
            int shed = 0;
            List<string> failures = null;
            for (int i = 0; i < shedIds.Count; i++)
            {
                try
                {
                    world.RemoveEntity(shedIds[i], EnumRemoveEntityReason.Despawned);
                    shed++;
                }
                catch (System.Exception ex)
                {
                    if (failures == null) failures = new List<string>();
                    failures.Add(shedIds[i] + "[" + ex.GetType().Name + "]: " + ex.Message);
                }
            }
            ShedTotal += shed;
            // A partial batch names the ids it could not remove, so "shed 7 of 15"
            // is never read as a full batch of seven.
            string why = failures == null
                ? ""
                : " PARTIAL: " + failures.Count + " of " + shedIds.Count
                    + " removals failed: " + string.Join("|", failures.ToArray());
            EmitShedLine(cfg, emaMs, "shed " + shed + " farthest enemies ("
                + enemies + " -> " + (enemies - shed) + ", lifetime " + ShedTotal + ")" + why);
        }

        // The trigger fired (tick over budget) but the shed was withheld. Same
        // WARNING channel as a real shed: the server is still collapsing, which is
        // the operator-visible fact; the reason says which knob to raise. Lifetime
        // count is included so a log-only timeline can tell suppression from shed.
        static void Suppressed(TickGuardConfig cfg, double emaMs, string reason)
        {
            EmitShedLine(cfg, emaMs, "shed SUPPRESSED (" + reason + "; lifetime " + ShedTotal + ")");
        }

        // The one line shape both outcomes share, so a shed and a suppression are
        // never two differently-worded lines an operator has to grep for. WARNING,
        // not info: shedding removes entities (a real gameplay impact) and only
        // fires while the tick is collapsing, rate-bounded by CooldownTicks - the
        // channel an operator greps when players report vanished hordes.
        // Invariant floats: same log-parsing convention as the governor lines.
        static void EmitShedLine(TickGuardConfig cfg, double emaMs, string outcome)
        {
            EsLog.Emit(LogLevel.Warn, "TickGuard: tick EMA " + Ms(emaMs) + "ms > "
                + cfg.ShedAboveMs.ToString(CultureInfo.InvariantCulture) + "ms - " + outcome);
        }

        static string Ms(double value) => value.ToString("F1", CultureInfo.InvariantCulture);
    }
}
