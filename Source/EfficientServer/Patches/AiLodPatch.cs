using System;
using System.Collections.Generic;
using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Stock EntityActivityUpdate sets aiActiveScale to 1.0 / 0.3 / 0.1 at 8m / 15m.
    /// We re-apply tighter bands after the stock pass for dedicated servers.
    /// </summary>
    [HarmonyPatch(typeof(World), nameof(World.EntityActivityUpdate))]
    public static class AiLodPatch
    {
        // Cloth suppression failure is model API variance; the LOD scale itself is
        // unaffected. Cosmetic-only, but a permanently silent catch here would hide
        // the drift, so warn once (per-entity-per-tick rate forbids per-call logs).
        static bool _clothWarned;
        static void Postfix(World __instance)
        {
            if (!ModApi.ShouldRun()) return;
            var cfg = ModApi.Config.AiLod;
            if (cfg == null || !cfg.Enabled) return;

            List<EntityAlive> alives = __instance.EntityAlives;
            if (alives == null || alives.Count == 0) return;

            float fullSq = cfg.FullAiDistSq;
            float medSq = cfg.MediumAiDistSq;
            float full = cfg.FullScale;
            float med = cfg.MediumScale;
            float far = cfg.FarScale;
            bool killCloth = ModApi.Config.SkipOnDedicated != null
                && ModApi.Config.SkipOnDedicated.ClothAndJiggleBoneSimulation;

            for (int i = 0; i < alives.Count; i++)
            {
                EntityAlive e = alives[i];
                if (e == null || e is EntityPlayer) continue;

                float d = e.aiClosestPlayerDistSq;
                float scale;
                if (d < fullSq) scale = full;
                else if (d < medSq) scale = med;
                else scale = far;

                e.aiActiveScale = scale;

                if (killCloth && e.emodel != null)
                {
                    try
                    {
                        // Level-triggered so it self-heals: cloth off when far, back
                        // ON when the entity returns near. Stock never re-enables
                        // zombie cloth, so a one-way disable would leave it off
                        // permanently after one far excursion (visible on a player
                        // host; cosmetic-only on a true dedicated server). Jiggle is
                        // re-enabled by the entity's own tick, so we only suppress it far.
                        bool near = d < fullSq;
                        e.emodel.ClothSimOn(near, false);
                        if (!near) e.emodel.JiggleOn(false);
                    }
                    catch (Exception ex)
                    {
                        // model API variance across versions: keep the LOD scale,
                        // drop only the cloth toggle, and name the failure once.
                        if (!_clothWarned)
                        {
                            _clothWarned = true;
                            EsLog.Warn("AI LOD cloth toggle failed [" + ex.GetType().Name + "]: " + ex.Message
                                + " - cloth suppression skipped (LOD scale unaffected)");
                        }
                    }
                }
            }
        }
    }
}
