using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Resolves a coarse, stable region string for the incident-lifecycle hooks (Core #64). Region
    /// is a biome-or-tile label a consumer (WorldNews, a territory mod) can resolve further;
    /// Core does not depend on any region system, so this is deliberately lightweight.
    /// </summary>
    internal static class IncidentLifecycleRegion
    {
        internal static string Of(IIncidentTarget target)
        {
            var map = target as Map;
            if (map != null) return map.Biome?.defName ?? map.Tile.ToString();
            if (target != null) return target.Tile.ToString();
            return "unknown";
        }

        internal static string OfMaps(System.Collections.Generic.List<Map> maps)
        {
            var map = maps?.FirstOrDefault();
            return map != null ? (map.Biome?.defName ?? map.Tile.ToString()) : "world";
        }
    }

    /// <summary>
    /// First-level RESOLUTION hook (Core #64): when a regionalizable GameCondition ends (solar flare,
    /// toxic fallout, weather spike, psychic drone, etc.), broadcast its resolution. Deduped by
    /// def+startTick+region so a condition that is re-registered or whose End() is called twice never
    /// double-fires. Disease/raid resolution is a separate detection path (episode close / lord end)
    /// and is not covered here.
    /// </summary>
    [HarmonyPatch(typeof(GameCondition), "End")]
    internal static class Patch_GameCondition_End
    {
        [HarmonyPostfix]
        public static void Postfix(GameCondition __instance)
        {
            if (Current.ProgramState != ProgramState.Playing) return;

            var def = __instance?.def;
            if (def == null) return;
            if (!SynapseIncidentLifecycle.IsRegionalizable(def.defName, null)) return;

            string region = IncidentLifecycleRegion.OfMaps(__instance.AffectedMaps);
            string dedupKey = def.defName + ":" + __instance.startTick + ":" + region;
            string outcome = __instance.Permanent ? "became permanent" : "ended";

            SynapseIncidentLifecycle.BroadcastResolved(def.defName, region, outcome, dedupKey);
        }
    }
}
