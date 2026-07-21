using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Stock EntityActivityUpdate sets aiActiveScale to 1.0 / 0.3 / 0.1 at 8m / 15m.
    /// We re-apply tighter bands after the stock pass for dedicated servers.
    /// </summary>
    [HarmonyPatch(typeof(World), nameof(World.EntityActivityUpdate))]
    public static class AiLodPatch
    {
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
                    catch
                    {
                        // model API variance across versions
                    }
                }
            }
        }
    }
}
