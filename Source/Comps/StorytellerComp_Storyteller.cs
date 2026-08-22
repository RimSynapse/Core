using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimSynapse.Comps
{
    /// <summary>
    /// Main storyteller component. Handles the interval tick loop
    /// that decides which events fire, factoring in LLM pacing and faction perceptions.
    /// </summary>
    public partial class StorytellerComp_Storyteller : StorytellerComp
    {
        protected StorytellerCompProperties_Storyteller Props => (StorytellerCompProperties_Storyteller)props;

        public static StorytellerCompProperties_Storyteller GetActiveStorytellerProps()
        {
            var storytellerComp = Find.Storyteller?.storytellerComps?.OfType<StorytellerComp_Storyteller>().FirstOrDefault();
            return storytellerComp?.props as StorytellerCompProperties_Storyteller;
        }

        public override IEnumerable<FiringIncident> MakeIntervalIncidents(IIncidentTarget target)
        {
            var coreComp = Find.World.GetComponent<RimSynapse.SynapseCoreWorldComponent>();
            if (coreComp == null) yield break;

            var settings = RimSynapseMod.Instance?.Settings;

            if (Find.CurrentMap != null)
            {
                int currentHour = GenLocalDate.HourOfDay(Find.CurrentMap);
                bool triggerPacing = false;

                if (settings?.enableTrainingMode == true && settings?.fastTelemetryMode == true)
                {
                    triggerPacing = (Find.TickManager.TicksGame % 1000 == 0);
                }
                else
                {
                    triggerPacing = (currentHour % 6 == 0 && coreComp.lastInvestigationHour != currentHour);
                }

                if (triggerPacing)
                {
                    coreComp.lastInvestigationHour = currentHour;
                    RimSynapse.Comps.SynapseStorytellerOpportunistic.TriggerPacingAdjustment();
                }
            }

            if (settings?.enableTrainingMode == true && settings?.fastTelemetryMode == true)
            {
                if (Find.TickManager.TicksGame % 2000 == 0)
                {
                    var categories = new List<IncidentCategoryDef> { IncidentCategoryDefOf.ThreatBig, IncidentCategoryDefOf.ThreatSmall, IncidentCategoryDefOf.Misc };
                    var category = categories.RandomElement();
                    RimSynapse.Comps.SynapseStorytellerOpportunistic.TriggerEventSelection(category, target);
                }
                yield break;
            }

            float pacingMultiplier = coreComp.GlobalPacingMultiplier;

            // Adjust the target days (higher multiplier means fewer days between incidents)
            float actualTargetDays = Props.incidentsTargetDays / Math.Max(0.1f, pacingMultiplier);

            // #67: the game's OWN deterministic cadence decides how many beats are due this interval;
            // the LLM never rolls timing, it only picks WHAT fires. IncidentCountThisInterval is
            // seeded from the target + interval + comp salt, so the beat schedule is reproducible and
            // independent of whether an async selection ever lands. Mapping to the vanilla knobs:
            // onDays = target days between incidents, one incident spread across that window — this
            // reproduces the old probability rate (1000 / (actualTargetDays * 60000) per interval)
            // while making it deterministic rather than a per-tick coin flip.
            int salt = Find.Storyteller.storytellerComps.IndexOf(this);
            int incidentCount = IncidentCycleUtility.IncidentCountThisInterval(
                target, salt,
                minDaysPassed: 0f,
                onDays: actualTargetDays,
                offDays: 0f,
                minSpacingDays: 0f,
                minIncidents: 1f,
                maxIncidents: 1f,
                acceptFraction: 1f);

            if (!RimSynapse.Comps.StorytellerDecisionGate.ShouldConsult(incidentCount))
                yield break; // no beat due — the tick passes silently

            // One decision in flight at a time (#67): a slow call must not overlap the next beat.
            if (coreComp.StorytellerDecisionInFlight)
                yield break;

            IncidentCategoryDef chosenCategory = ChooseCategory(target, coreComp);
            if (chosenCategory == null) yield break;

            // Backend unavailable → vanilla selection for this beat, synchronously, so the game is
            // always fully playable and no beat is silently dropped. Vanilla incidents are the
            // guaranteed baseline; the LLM path is additive intention on top of it.
            if (!SynapseClient.IsOnline)
            {
                FiringIncident fallback = BuildVanillaFallback(chosenCategory, target, coreComp);
                if (fallback != null) yield return fallback;
                yield break;
            }

            // Schedule, don't drive live: the selection is async and lands via IncidentQueue on the
            // game thread (see ApplyEventSelection), which revalidates CanFireNow on fire. The
            // in-flight slot is claimed and released inside TriggerEventSelection.
            RimSynapse.Comps.SynapseStorytellerOpportunistic.TriggerEventSelection(chosenCategory, target);
        }
    }
}
