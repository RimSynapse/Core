using HarmonyLib;
using RimWorld;
using Verse;
using RimSynapse.Models;
using System.Collections.Generic;

namespace RimSynapse.Patches
{
    /// <summary>
    /// Per-wound severity capture + after-effect attribution (Core#88). Trivial scratches roll up into
    /// one coalesced episode ("N claw marks from a crow"); serious wounds (downed, heavy bleed, big hits)
    /// enqueue as distinct, colorful entries. A tend hook attaches the doctor as an after-effect
    /// participant of the patient's open injury episode — no memory/LLM call, just a list add.
    /// All main-thread (Harmony postfixes).
    /// </summary>
    [HarmonyPatch(typeof(Pawn), "PostApplyDamage")]
    internal static class Patch_Pawn_PostApplyDamage_Memory
    {
        static void Postfix(Pawn __instance, DamageInfo dinfo, float totalDamageDealt)
        {
            if (__instance == null || totalDamageDealt <= 0f || !__instance.IsColonist) return;
            if (Current.ProgramState != ProgramState.Playing || Find.World == null) return;

            var coreComp = Find.World.GetComponent<SynapseCoreWorldComponent>();
            if (coreComp == null) return;

            Pawn aggressorPawn = dinfo.Instigator as Pawn;
            string aggressor = aggressorPawn?.LabelShort ?? dinfo.Instigator?.LabelShort ?? dinfo.Def?.label ?? "something";
            string wound = dinfo.Def?.label ?? "wound";

            var involved = new List<string> { __instance.ThingID };
            if (aggressorPawn != null && aggressorPawn.IsColonist && aggressorPawn != __instance)
                involved.Add(aggressorPawn.ThingID);
            var witnesses = NearbyColonistIds(__instance, involved);

            var ev = new PastEvent
            {
                gameTick = GenTicks.TicksGame,
                category = "ColonistInjured",
                severity = ClassifyWound(__instance, totalDamageDealt),
                eventDescription = $"{__instance.LabelShort} suffered a {wound} from {aggressor}.",
                involvedPawnIds = involved,
                witnessPawnIds = witnesses
            };
            // Stable per victim+aggressor identity so repeated hits of the same ordeal coalesce.
            ev.mcpTag = $"Injury({__instance.LabelShort} vs {aggressor})";
            coreComp.EnqueuePastEvent(ev);
        }

        // Cheap first-pass classification (Core#88): downed / heavy bleed / big single hit = Serious
        // (distinct, colorful); everything else = Trivial (rolls up into a counted episode).
        private static EventSeverity ClassifyWound(Pawn p, float totalDamageDealt)
        {
            if (p.Downed) return EventSeverity.Serious;
            var hs = p.health?.hediffSet;
            if (hs != null && hs.BleedRateTotal > 0.3f) return EventSeverity.Serious;
            if (totalDamageDealt >= 12f) return EventSeverity.Serious;
            return EventSeverity.Trivial;
        }

        private static List<string> NearbyColonistIds(Pawn center, List<string> exclude)
        {
            var ids = new List<string>();
            if (center?.Map == null || !center.Spawned) return ids;
            var colonists = center.Map.mapPawns?.FreeColonistsSpawned;
            if (colonists == null) return ids;
            for (int i = 0; i < colonists.Count; i++)
            {
                var c = colonists[i];
                if (c == center || c.Dead) continue;
                if (exclude != null && exclude.Contains(c.ThingID)) continue;
                if (center.Position.DistanceTo(c.Position) < 20f) ids.Add(c.ThingID);
            }
            return ids;
        }
    }

    /// <summary>Tend completion: attribute the doctor as an after-effect participant of the patient's
    /// open injury episode, once. Fires per tend (not per tick); no memory or LLM call here.</summary>
    [HarmonyPatch(typeof(TendUtility), "DoTend")]
    internal static class Patch_TendUtility_DoTend_AfterEffect
    {
        static void Postfix(Pawn doctor, Pawn patient)
        {
            if (doctor == null || patient == null || doctor == patient) return;
            if (!doctor.IsColonist || !patient.IsColonist) return;
            if (Current.ProgramState != ProgramState.Playing || Find.World == null) return;

            var coreComp = Find.World.GetComponent<SynapseCoreWorldComponent>();
            if (coreComp == null) return;

            var episode = coreComp.FindOpenEpisodeForPawn(patient);
            if (episode == null) return;
            if (!episode.afterEffectPawnIds.Contains(doctor.ThingID) && !episode.involvedPawnIds.Contains(doctor.ThingID))
                episode.afterEffectPawnIds.Add(doctor.ThingID);
        }
    }
}
