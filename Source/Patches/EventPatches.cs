using HarmonyLib;
using RimWorld;
using Verse;
using RimSynapse.Models;
using System.Linq;

namespace RimSynapse.Patches
{
    /// <summary>
    /// Shared helper for native event interception.
    /// Both Tale and Kill patches route through here to avoid code duplication.
    /// </summary>
    internal static class EventPatchHelper
    {
        public static void EnqueueIfPlaying(string category, string description)
        {
            if (Current.ProgramState != ProgramState.Playing || Find.World == null) return;

            var coreComp = Find.World.GetComponent<RimSynapse.SynapseCoreWorldComponent>();
            if (coreComp == null) return;

            coreComp.EnqueuePastEvent(new PastEvent
            {
                gameTick = GenTicks.TicksGame,
                category = category,
                eventDescription = description
            });
        }
    }

    /// <summary>
    /// Intercepts RimWorld's Tale system (marriages, legendary crafting, recruitment, etc.)
    /// and enqueues a PastEvent so the LLM can generate personalized memories.
    /// </summary>
    [HarmonyPatch(typeof(TaleRecorder), "RecordTale")]
    internal static class Patch_TaleRecorder_RecordTale
    {
        static void Postfix(TaleDef def, params object[] args)
        {
            Pawn primaryPawn = args.OfType<Pawn>().FirstOrDefault();
            if (primaryPawn != null && (primaryPawn.IsColonist || primaryPawn.IsPrisonerOfColony))
            {
                string taleLabel = def.label ?? def.defName;
                string pawnName = primaryPawn.Name?.ToStringShort ?? primaryPawn.KindLabel;
                EventPatchHelper.EnqueueIfPlaying("Tale", $"{pawnName}: {taleLabel}");
            }
        }
    }

    /// <summary>
    /// Logs completed quests as past events.
    /// </summary>
    [HarmonyPatch(typeof(Quest), "End")]
    internal static class Patch_Quest_End
    {
        static void Postfix(Quest __instance, QuestEndOutcome outcome)
        {
            if (Current.ProgramState != ProgramState.Playing || Find.World == null) return;

            var coreComp = Find.World.GetComponent<SynapseCoreWorldComponent>();
            if (coreComp == null) return;

            string label = __instance.name ?? "Unknown quest";
            string outcomeStr = outcome.ToString();

            coreComp.EnqueuePastEvent(new PastEvent
            {
                gameTick = GenTicks.TicksGame,
                category = "QuestEnded",
                eventDescription = $"Quest '{label}' ended: {outcomeStr}."
            });
        }
    }

    /// <summary>
    /// Intercepts fired incidents and records them for storyteller tracking.
    /// </summary>
    [HarmonyPatch(typeof(IncidentWorker), "TryExecute")]
    internal static class Patch_IncidentWorker_TryExecute
    {
        [HarmonyPostfix]
        public static void Postfix(IncidentWorker __instance, IncidentParms parms, bool __result)
        {
            if (!__result) return;
            if (Current.ProgramState != ProgramState.Playing || Find.World == null) return;

            var coreComp = Find.World.GetComponent<SynapseCoreWorldComponent>();
            if (coreComp == null) return;

            string defName = __instance.def?.defName ?? "unknown";
            coreComp.RegisterFiredIncident(defName);

            // Regionalizable-incident lifecycle START hook (Core #64). Only Tier A–D environmental
            // incidents emit a start; raids/infestations do not. Broadcast is contained.
            var def = __instance.def;
            string category = def?.category?.defName;
            if (SynapseIncidentLifecycle.IsRegionalizable(def?.defName, category))
            {
                SynapseIncidentLifecycle.BroadcastStarted(
                    def.defName,
                    IncidentLifecycleRegion.Of(parms.target),
                    parms.points,
                    parms.faction?.Name ?? "",
                    leadTimeTicks: 0);
            }
        }
    }
}
