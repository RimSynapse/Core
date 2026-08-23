using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using RimSynapse;
using RimSynapse.Comps;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Covers LLM-driven incident selection (Core #67): the engine that lets the storyteller pick
    /// WHAT fires on the game's own deterministic cadence, within the difficulty budget, and always
    /// keeps the colony playable.
    ///
    /// These are Execution-tier (downstream mechanics): given the machinery, does it only ever offer
    /// eligible incidents, fall back to a guaranteed baseline when the model is unreachable, stay
    /// dormant under a vanilla storyteller, and keep exactly one decision in flight across a
    /// save/quit? Whether the model's *choices* feel intentional is a playtest concern, not CI.
    /// </summary>
    [SynapseTestSet]
    public static class IncidentSelectionCases
    {
        /// <summary>The RimSynapse storyteller def (carries our comp), or null if none is loaded.</summary>
        private static StorytellerDef RimSynapseStorytellerDef()
            => DefDatabase<StorytellerDef>.AllDefsListForReading
                .FirstOrDefault(d => d.comps != null && d.comps.Any(c => c is StorytellerCompProperties_Storyteller));

        /// <summary>Run a body with the live storyteller temporarily swapped to the RimSynapse one.</summary>
        private static string WithRimSynapseStoryteller(Func<StorytellerComp_Storyteller, string> body)
        {
            var def = RimSynapseStorytellerDef();
            if (def == null) return "SKIP: no RimSynapse storyteller def loaded";

            var storyteller = Find.Storyteller;
            var originalDef = storyteller.def;
            try
            {
                storyteller.def = def;
                storyteller.Notify_DefChanged();
                var comp = storyteller.storytellerComps.OfType<StorytellerComp_Storyteller>().FirstOrDefault();
                Assert.NotNull(comp, "the RimSynapse storyteller must carry our comp after the swap");
                return body(comp);
            }
            finally
            {
                storyteller.def = originalDef;
                storyteller.Notify_DefChanged();
            }
        }

        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_StorytellerSelectsEligibleOnly", () =>
                WithRimSynapseStoryteller(comp =>
                {
                    var target = (IIncidentTarget)Find.CurrentMap;
                    var worldComp = Find.World.GetComponent<SynapseCoreWorldComponent>();

                    // Every category's fallback pick must be CanFireNow-eligible and in that category.
                    // (The LLM path funnels through the same eligibility filter — an ineligible pick is
                    // dropped at schedule time and re-checked again on fire by the IncidentQueue.)
                    var categories = new[] { IncidentCategoryDefOf.ThreatBig, IncidentCategoryDefOf.ThreatSmall, IncidentCategoryDefOf.Misc };
                    int checkedNonNull = 0;
                    foreach (var cat in categories)
                    {
                        var fi = comp.BuildVanillaFallback(cat, target, worldComp);
                        if (fi == null) continue; // nothing eligible in this category right now — allowed
                        checkedNonNull++;
                        Assert.Equal(cat, fi.def.category, $"fallback for {cat.defName} must stay in-category");
                        var parms = StorytellerUtility.DefaultParmsNow(fi.def.category, target);
                        Assert.True(fi.def.Worker.CanFireNow(parms),
                            $"fallback picked '{fi.def.defName}' which is not CanFireNow — selection must offer eligible only");
                    }
                    Assert.True(checkedNonNull > 0,
                        "no category yielded any eligible incident — cannot prove the eligible-only invariant");
                    return $"verified {checkedNonNull} category pick(s) are all CanFireNow-eligible and in-category";
                }),
                tier: "Execution", polarity: "positive",
                scenario: "Storyteller selects an incident to fire",
                expectation: "Only CanFireNow-eligible incidents of the chosen category are ever offered");

            yield return new SynapseTestCase("Core_StorytellerFallsBackToVanilla", () =>
                WithRimSynapseStoryteller(comp =>
                {
                    var target = (IIncidentTarget)Find.CurrentMap;
                    var worldComp = Find.World.GetComponent<SynapseCoreWorldComponent>();

                    // The guaranteed baseline is synchronous and consults no backend — this is exactly
                    // what MakeIntervalIncidents yields when SynapseClient.IsOnline is false, so a beat
                    // is never dropped. At least one standard category must yield a playable incident,
                    // sourced from our comp.
                    FiringIncident picked = null;
                    foreach (var cat in new[] { IncidentCategoryDefOf.ThreatBig, IncidentCategoryDefOf.Misc, IncidentCategoryDefOf.ThreatSmall })
                    {
                        picked = comp.BuildVanillaFallback(cat, target, worldComp);
                        if (picked != null) break;
                    }
                    Assert.NotNull(picked, "backend-independent fallback must yield a playable incident for a standard category");
                    Assert.NotNull(picked.def, "the fallback FiringIncident must carry an IncidentDef");
                    Assert.True(picked.source == comp, "the fallback incident must be sourced from the RimSynapse comp");
                    return $"offline fallback yielded '{picked.def.defName}' with no backend consulted";
                }),
                tier: "Execution", polarity: "positive",
                scenario: "The LLM backend is unreachable on a due beat",
                expectation: "A vanilla weighted pick fires instead — the colony stays fully playable");

            yield return new SynapseTestCase("Core_StorytellerDormantWithoutRimSynapseStoryteller", () =>
            {
                // Under a non-RimSynapse storyteller the whole engine is inert: no comp, the master
                // gate is closed, and no agent context is injected. Self-skips if the live storyteller
                // already happens to be a RimSynapse one (then the dormant state cannot be observed).
                var comp = Find.Storyteller?.storytellerComps?.OfType<StorytellerComp_Storyteller>().FirstOrDefault();
                if (comp != null)
                    return "SKIP: the active storyteller is a RimSynapse one — dormant state not observable here";

                Assert.False(SynapseStorytellerContext.IsRimSynapseStorytellerActive,
                    "master gate must be closed under a non-RimSynapse storyteller");
                Assert.Equal(string.Empty, SynapseStorytellerContext.BuildAgentContext(Find.CurrentMap),
                    "no agent context may be injected while dormant");
                return "no comp, gate closed, no context injected — engine inert as designed";
            },
                tier: "Execution", polarity: "negative",
                scenario: "A vanilla (non-RimSynapse) storyteller is selected",
                expectation: "The selection engine is fully dormant — vanilla runs unchanged");

            yield return new SynapseTestCase("Core_StorytellerDecisionSaveRoundTrip", () =>
            {
                // The single-in-flight guard is scribed; a decision interrupted by save/quit must not
                // wedge the storyteller. Model the round-trip on the live component: a freshly-claimed
                // flag reads as in-flight, but a flag whose start tick is older than the stale window
                // (as it would be after loading a save made mid-decision) reads as free again.
                var worldComp = Find.World.GetComponent<SynapseCoreWorldComponent>();
                Assert.NotNull(worldComp, "no SynapseCoreWorldComponent on the world");

                bool savedFlag = worldComp.storytellerDecisionInFlight;
                int savedTick = worldComp.storytellerDecisionStartTick;
                try
                {
                    worldComp.EndStorytellerDecision();
                    Assert.False(worldComp.StorytellerDecisionInFlight, "a released slot must read as free");

                    Assert.True(worldComp.TryBeginStorytellerDecision(), "first claim must succeed");
                    Assert.True(worldComp.StorytellerDecisionInFlight, "a fresh claim must read as in-flight");
                    Assert.False(worldComp.TryBeginStorytellerDecision(), "a second concurrent claim must be refused");

                    // Simulate the post-load state: the flag survived the save, but its start tick is
                    // now far in the past because the process that would have cleared it is gone.
                    worldComp.storytellerDecisionStartTick = Find.TickManager.TicksGame - 100000;
                    Assert.False(worldComp.StorytellerDecisionInFlight,
                        "a stale in-flight flag (interrupted session) must clear so the storyteller never wedges");
                    Assert.True(worldComp.TryBeginStorytellerDecision(),
                        "a fresh decision must be claimable once the stale flag has cleared");
                }
                finally
                {
                    worldComp.storytellerDecisionInFlight = savedFlag;
                    worldComp.storytellerDecisionStartTick = savedTick;
                }
                return "fresh claim holds; stale claim clears — single-in-flight survives save/quit";
            },
                tier: "Execution", polarity: "positive",
                scenario: "A save is made while a storyteller decision is in flight",
                expectation: "The scribed flag round-trips and a stale one clears — no wedged storyteller");
        }
    }
}
