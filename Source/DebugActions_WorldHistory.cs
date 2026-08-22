using System.Linq;
using System.Text;
using LudeonTK;
using RimWorld;
using Verse;
using RimSynapse.Comps;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for the save-backed world-history store (Core #65), grouped under
    /// "RimSynapse". Bypasses the master gate (temporarily swaps in a RimSynapse storyteller),
    /// records a start + resolution + a lingering open thread through the public store API, then dumps
    /// the query, the open threads, and the context block the Storyteller would receive — and cleans
    /// up its probe entries. Headlessly runnable via run_debug_action.
    /// </summary>
    public static class DebugActions_WorldHistory
    {
        private const string ProbeRegion = "DebugProbeRegion";

        [DebugAction("RimSynapse", "World history: record start/resolution + dump store",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RecordAndDump()
        {
            var worldComp = Find.World?.GetComponent<SynapseCoreWorldComponent>();
            if (worldComp == null) { SynapseLogger.Warn("core", "[RimSynapse] World history: no world component."); return; }

            var storyteller = Find.Storyteller;
            var originalDef = storyteller?.def;
            bool swapped = false;
            var comp = storyteller?.storytellerComps?.OfType<StorytellerComp_Storyteller>().FirstOrDefault();
            if (comp == null)
            {
                var rimDef = DefDatabase<StorytellerDef>.AllDefsListForReading
                    .FirstOrDefault(d => d.comps != null && d.comps.Any(c => c is StorytellerCompProperties_Storyteller));
                if (rimDef != null && storyteller != null) { storyteller.def = rimDef; storyteller.Notify_DefChanged(); swapped = true; }
            }

            var sb = new StringBuilder();
            sb.AppendLine("[RimSynapse] World-history store (Core #65):");
            sb.AppendLine($"  gate active: {SynapseStorytellerContext.IsRimSynapseStorytellerActive}{(swapped ? " (bypassed via storyteller swap)" : "")}");

            int now = Find.TickManager?.TicksGame ?? 0;
            try
            {
                int before = worldComp.worldHistory.Count;
                // A resolved incident and a lingering open thread. Ticks are clamped non-negative so
                // the demonstration works in early-game quicktest (where now can be < the offsets)
                // and QueryWorldHistory's sinceTick=0 floor still returns them.
                int t0 = System.Math.Max(0, now - 120000);
                int t1 = System.Math.Max(0, now - 60000);
                int t2 = System.Math.Max(0, now - 30000);
                worldComp.RecordIncidentStart("SolarFlare", ProbeRegion, 350f, "", t0);
                worldComp.RecordIncidentResolution("SolarFlare", ProbeRegion, "ended", t1);
                worldComp.RecordIncidentStart("ToxicFallout", ProbeRegion, 0f, "", t2);

                var probeEntries = worldComp.QueryWorldHistory(region: ProbeRegion).ToList();
                var open = worldComp.OpenThreads().Where(e => e.region == ProbeRegion).ToList();
                sb.AppendLine($"  store: {before} -> {worldComp.worldHistory.Count} entries; probe query returned {probeEntries.Count} " +
                              $"(resolved={probeEntries.Count(e => e.resolved)}, open={probeEntries.Count(e => !e.resolved)})");
                foreach (var e in probeEntries)
                    sb.AppendLine($"    - {e.kind} @ {e.region}: {(e.resolved ? "resolved:" + e.outcome : "OPEN")}");
                sb.AppendLine("  context block the Storyteller would receive:");
                foreach (var line in (worldComp.WorldHistoryContextBlock() ?? "(empty)").Split('\n'))
                    sb.AppendLine("    " + line);
            }
            finally
            {
                worldComp.worldHistory.RemoveAll(e => e.region == ProbeRegion); // leave no residue
                if (swapped && storyteller != null) { storyteller.def = originalDef; storyteller.Notify_DefChanged(); }
            }

            SynapseLogger.Message(sb.ToString().TrimEnd());
        }
    }
}
