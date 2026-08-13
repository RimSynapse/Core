using HarmonyLib;
using RimWorld;
using Verse;
using RimSynapse.Models;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;

namespace RimSynapse.Patches
{
    /// <summary>
    /// Social event patches: recruitment, enslavement, surgery, limb regrowth,
    /// visitor departure, and faction changes.
    /// </summary>

    [HarmonyPatch(typeof(Pawn), "SetFaction")]
    internal static class Patch_Pawn_SetFaction_RecruitOrEnslave
    {
        private static bool wasPrisonerBefore = false;
        private static string raidIdBefore = null;

        [HarmonyPriority(Priority.First)]
        static void Prefix(Pawn __instance, Faction newFaction)
        {
            if (__instance == null || newFaction == null) return;
            wasPrisonerBefore = __instance.IsPrisonerOfColony;

            var coreComp = Find.World?.GetComponent<SynapseCoreWorldComponent>();
            if (coreComp != null && coreComp.pawnToRaidId.TryGetValue(__instance.ThingID, out string raidId))
            {
                raidIdBefore = raidId;
            }
            else
            {
                raidIdBefore = null;
            }
        }

        static void Postfix(Pawn __instance, Faction newFaction)
        {
            if (__instance == null || newFaction == null || !newFaction.IsPlayer) return;
            if (Current.ProgramState != ProgramState.Playing || Find.World == null) return;

            var coreComp = Find.World.GetComponent<SynapseCoreWorldComponent>();
            if (coreComp == null) return;

            string category;
            string description;

            if (__instance.guest?.GuestStatus == GuestStatus.Slave)
            {
                category = "PawnEnslaved";
                description = $"{__instance.Name?.ToStringShort ?? __instance.KindLabel} has been enslaved.";
                // #92: enslavement is an identity-defining event — secure it as a long-term memory.
                SynapsePivotalMemory.Record(__instance, SynapsePivotalMemory.Enslaved, description);
            }
            else if (wasPrisonerBefore)
            {
                category = "PrisonerRecruited";
                description = $"{__instance.Name?.ToStringShort ?? __instance.KindLabel} has been recruited from prison.";
                SynapsePivotalMemory.Record(__instance, SynapsePivotalMemory.Recruited, description);
            }
            else
            {
                category = "PawnRecruited";
                description = $"{__instance.Name?.ToStringShort ?? __instance.KindLabel} has joined the colony.";
                SynapsePivotalMemory.Record(__instance, SynapsePivotalMemory.Recruited, description);
            }

            coreComp.EnqueuePastEvent(new PastEvent
            {
                gameTick = GenTicks.TicksGame,
                category = category,
                eventDescription = description
            });

            // Track raid recruitment
            if (!string.IsNullOrEmpty(raidIdBefore))
            {
                if (!coreComp.raidRecruitedPawns.ContainsKey(raidIdBefore))
                {
                    coreComp.raidRecruitedPawns[raidIdBefore] = new List<string>();
                }
                coreComp.raidRecruitedPawns[raidIdBefore].Add(__instance.Name?.ToStringShort ?? __instance.KindLabel);
            }
        }
    }

    [HarmonyPatch]
    internal static class Patch_Recipe_Surgery_ApplyOnPawn
    {
        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Recipe_InstallImplant), "ApplyOnPawn");
            yield return AccessTools.Method(typeof(Recipe_InstallArtificialBodyPart), "ApplyOnPawn");
        }

        static void Postfix(Pawn pawn, BodyPartRecord part, Pawn billDoer, List<Thing> ingredients, Bill bill)
        {
            if (pawn == null || billDoer == null || !pawn.IsColonist) return;
            if (Current.ProgramState != ProgramState.Playing || Find.World == null) return;

            var coreComp = Find.World.GetComponent<SynapseCoreWorldComponent>();
            if (coreComp == null) return;

            string partLabel = part?.Label ?? "unknown body part";
            string ingredientNames = "";
            if (ingredients != null && ingredients.Count > 0)
            {
                ingredientNames = string.Join(", ", ingredients.Select(i => i.Label));
            }

            coreComp.EnqueuePastEvent(new PastEvent
            {
                gameTick = GenTicks.TicksGame,
                category = "SurgeryPerformed",
                eventDescription = $"{billDoer.Name?.ToStringShort ?? billDoer.KindLabel} performed surgery on {pawn.Name?.ToStringShort ?? pawn.KindLabel}'s {partLabel} using {ingredientNames}."
            });
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), "RemoveHediff")]
    internal static class Patch_Pawn_HealthTracker_RemoveHediff_LimbRegrowth
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn_HealthTracker __instance, Hediff hediff)
        {
            if (__instance == null || hediff == null) return;
            if (Current.ProgramState != ProgramState.Playing || Find.World == null) return;

            Pawn pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
            if (pawn == null || !pawn.IsColonist) return;

            if (hediff.def.defName != "MissingBodyPart") return;
            if (hediff.Part == null) return;

            // Only fire if the part is now present (no longer missing)
            if (pawn.health.hediffSet.PartIsMissing(hediff.Part)) return;

            var coreComp = Find.World.GetComponent<SynapseCoreWorldComponent>();
            if (coreComp == null) return;

            string partLabel = hediff.Part.Label ?? "body part";
            coreComp.EnqueuePastEvent(new PastEvent
            {
                gameTick = GenTicks.TicksGame,
                category = "LimbRegrown",
                eventDescription = $"{pawn.Name?.ToStringShort ?? pawn.KindLabel}'s {partLabel} has regrown or been restored."
            });
        }
    }

    [HarmonyPatch(typeof(Pawn), "DeSpawn")]
    internal static class Patch_Pawn_DeSpawn_Visitor
    {
        [HarmonyPriority(Priority.Last)]
        [HarmonyPrefix]
        public static void Prefix(Pawn __instance)
        {
            if (__instance == null) return;
            if (Current.ProgramState != ProgramState.Playing || Find.World == null) return;

            if (__instance.Faction == null || __instance.Faction.IsPlayer || __instance.Faction.HostileTo(Faction.OfPlayer)) return;
            if (__instance.IsPrisoner || __instance.IsSlave) return;

            var coreComp = Find.World.GetComponent<SynapseCoreWorldComponent>();
            if (coreComp == null) return;

            if (coreComp.visitorEntryTicks.TryGetValue(__instance.ThingID, out int entryTick))
            {
                int duration = GenTicks.TicksGame - entryTick;
                CheckWitnessedReconstruction(coreComp, __instance, entryTick);

                coreComp.visitorEntryTicks.Remove(__instance.ThingID);
            }
        }

        private static void CheckWitnessedReconstruction(SynapseCoreWorldComponent coreComp, Pawn visitor, int entryTick)
        {
            if (coreComp.backlogQueueList == null) return;

            int visitDuration = GenTicks.TicksGame - entryTick;
            if (visitDuration < 2500) return; // Need at least ~1 hour of visit

            int surgeryCount = 0;
            int regrowthCount = 0;
            var colonistNames = new HashSet<string>();

            foreach (var ev in coreComp.backlogQueueList)
            {
                if (ev.gameTick < entryTick) continue;
                if (ev.gameTick > GenTicks.TicksGame) continue;

                if (ev.category == "SurgeryPerformed")
                {
                    surgeryCount++;
                    var match = System.Text.RegularExpressions.Regex.Match(ev.eventDescription, @"surgery on (\w+)'s");
                    if (match.Success) colonistNames.Add(match.Groups[1].Value);
                }
                else if (ev.category == "LimbRegrown")
                {
                    regrowthCount++;
                    var match = System.Text.RegularExpressions.Regex.Match(ev.eventDescription, @"^(\w+)'s");
                    if (match.Success) colonistNames.Add(match.Groups[1].Value);
                }
            }

            if (surgeryCount == 0 && regrowthCount == 0) return;

            float daysVisiting = visitDuration / 60000f;
            string factionName = visitor.Faction?.Name ?? "unknown faction";
            string visitorName = visitor.Name?.ToStringShort ?? visitor.KindLabel;

            string description;
            if (regrowthCount > 0)
            {
                description = $"Visitor {visitorName} of {factionName} witnessed {regrowthCount} limb regrowths and {surgeryCount} surgeries during their {daysVisiting:F1}-day stay. Colonists involved: {string.Join(", ", colonistNames)}.";
            }
            else
            {
                description = $"Visitor {visitorName} of {factionName} witnessed {surgeryCount} medical procedures during their {daysVisiting:F1}-day stay. Colonists involved: {string.Join(", ", colonistNames)}.";
            }

            coreComp.EnqueuePastEvent(new PastEvent
            {
                gameTick = GenTicks.TicksGame,
                category = "VisitorWitnessedSurgery",
                eventDescription = description
            });
        }
    }
}
