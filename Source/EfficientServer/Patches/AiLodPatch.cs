using System;
using System.Collections.Generic;
using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Stock EntityActivityUpdate sets aiActiveScale to 1.0 for the closest
    /// N = clamp(60/playerCount, 4, 20) entities regardless of distance, then 0.3
    /// inside 15 m and 0.1 beyond. We re-apply tighter distance bands after the
    /// stock pass for dedicated servers; the per-player top-N full-AI quota is not
    /// replicated, which the band audit accepted (FEATURES.md).
    /// </summary>
    [HarmonyPatch(typeof(World), nameof(World.EntityActivityUpdate))]
    public static class AiLodPatch
    {
        // Cloth suppression failure is model API variance; the LOD scale itself is
        // unaffected. Cosmetic-only, but a permanently silent catch here would hide
        // the drift, so announce once (per-entity-per-tick rate forbids per-call
        // logs) and keep the degradation listed in `es status` as aiLodCloth.
        internal const string DegradeKey = "aiLodCloth";

        static void Postfix(World __instance)
        {
            if (!ModApi.ShouldRun()) return;
            var cfg = ModApi.Config.AiLod;
            if (cfg == null || !cfg.Enabled) return;

            List<EntityAlive> alives = __instance.EntityAlives;
            if (alives == null || alives.Count == 0) return;

            float fullSq = cfg.FullAiDistSq;
            float medSq = cfg.MediumAiDistSq;
            bool killCloth = ModApi.Config.SkipOnDedicated != null
                && ModApi.Config.SkipOnDedicated.ClothAndJiggleBoneSimulation;

            for (int i = 0; i < alives.Count; i++)
            {
                EntityAlive e = alives[i];
                if (e == null || e is EntityPlayer) continue;

                float d = e.aiClosestPlayerDistSq;
                float scale;
                if (d < fullSq) scale = cfg.FullScale;
                else if (d < medSq) scale = cfg.MediumScale;
                else scale = cfg.FarScale;

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
                        if (Degrade.Report(DegradeKey, "AI LOD cloth toggle failed [" + ex.GetType().Name + "]: " + ex.Message
                                + " - cloth suppression skipped (LOD scale unaffected)"))
                            EsLog.Emit(LogLevel.Warn, Degrade.FirstReport(DegradeKey));
                    }
                }
            }
        }
    }
}
