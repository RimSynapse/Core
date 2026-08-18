using System.Linq;
using System.Text;
using LudeonTK;
using RimWorld;
using Verse;
using RimSynapse.Comps;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for LLM-driven incident selection (Core #67), grouped under "RimSynapse".
    /// Headlessly runnable via the toolkit's run_debug_action. Exercises the mechanism end to end
    /// without depending on a live backend:
    ///   - the master gate (RimSynapse storyteller active?),
    ///   - the deterministic beat count (called twice — it must agree),
    ///   - the single-decision-in-flight guard (a second claim must be refused),
    ///   - the guaranteed vanilla fallback (an eligible incident is picked with the backend ignored),
    ///   - and finally kicks the live async selection so the queue path is exercised when online.
    /// </summary>
    public static class DebugActions_IncidentSelection
    {
        [DebugAction("RimSynapse", "Storyteller: force an incident-selection beat",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ForceIncidentSelectionBeat()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[RimSynapse] Incident-selection beat (Core #67):");

            bool active = SynapseStorytellerContext.IsRimSynapseStorytellerActive;
            sb.AppendLine($"  RimSynapse storyteller active (master gate): {active}");
            sb.AppendLine($"  backend online: {SynapseClient.IsOnline}");

            // Bypass the master gate so the mechanic is exercised regardless of the live storyteller:
            // temporarily swap to a RimSynapse storyteller def (restored in the finally). Under a real
            // RimSynapse game the swap is a no-op the player never sees.
            var storyteller = Find.Storyteller;
            var originalDef = storyteller?.def;
            var comp = storyteller?.storytellerComps?.OfType<StorytellerComp_Storyteller>().FirstOrDefault();
            bool swapped = false;
            if (comp == null)
            {
                var rimSynapseDef = DefDatabase<StorytellerDef>.AllDefsListForReading
                    .FirstOrDefault(d => d.comps != null && d.comps.Any(c => c is StorytellerCompProperties_Storyteller));
                if (rimSynapseDef == null || storyteller == null)
                {
                    sb.AppendLine("  -> no RimSynapse storyteller def loaded; cannot exercise the active path.");
                    SynapseLogger.Message(sb.ToString().TrimEnd());
                    return;
                }
                storyteller.def = rimSynapseDef;
                storyteller.Notify_DefChanged();
                swapped = true;
                comp = storyteller.storytellerComps.OfType<StorytellerComp_Storyteller>().FirstOrDefault();
                sb.AppendLine($"  (bypassed master gate: temporarily swapped storyteller to '{rimSynapseDef.defName}')");
            }

            try
            {
            var target = (IIncidentTarget)Find.CurrentMap;
            var worldComp = Find.World.GetComponent<SynapseCoreWorldComponent>();

            // Deterministic beat count: same interval, same salt -> same answer. The LLM never rolls timing.
            int salt = Find.Storyteller.storytellerComps.IndexOf(comp);
            int a = IncidentCycleUtility.IncidentCountThisInterval(target, salt, 0f, 1f, 0f, 0f, 1f, 1f, 1f);
            int b = IncidentCycleUtility.IncidentCountThisInterval(target, salt, 0f, 1f, 0f, 0f, 1f, 1f, 1f);
            sb.AppendLine($"  deterministic count (onDays=1): {a} == {b} -> {(a == b ? "stable" : "NON-DETERMINISTIC (bug)")}");

            // Single-in-flight guard: first claim succeeds, second is refused; then restore.
            bool wasInFlight = worldComp.StorytellerDecisionInFlight;
            bool first = worldComp.TryBeginStorytellerDecision();
            bool second = worldComp.TryBeginStorytellerDecision();
            worldComp.EndStorytellerDecision();
            sb.AppendLine($"  in-flight guard: firstClaim={first}, secondClaim={second} (expected true/false), preexisting={wasInFlight}");

            // Guaranteed baseline: a weighted pick among CanFireNow-eligible incidents, backend ignored.
            foreach (var category in new[] { IncidentCategoryDefOf.ThreatBig, IncidentCategoryDefOf.Misc })
            {
                var fb = comp.BuildVanillaFallback(category, target, worldComp);
                sb.AppendLine($"  vanilla fallback [{category.defName}]: {(fb?.def?.defName ?? "(none eligible)")}");
            }

            // Kick the live async selection (uses the LLM when online; the queue path revalidates on fire).
            var chosen = IncidentCategoryDefOf.ThreatBig;
            sb.AppendLine($"  kicking live async selection for '{chosen.defName}' (result lands via IncidentQueue if online)...");
            SynapseStorytellerOpportunistic.TriggerEventSelection(chosen, target);
            }
            finally
            {
                if (swapped && storyteller != null)
                {
                    storyteller.def = originalDef;
                    storyteller.Notify_DefChanged();
                    sb.AppendLine("  (restored original storyteller def)");
                }
            }

            SynapseLogger.Message(sb.ToString().TrimEnd());
        }
    }
}
