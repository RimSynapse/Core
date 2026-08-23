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
    /// Covers the save-backed world-history store (Core #65): it records regional incidents +
    /// resolutions queryable by region/kind/time, surfaces open threads to agent context, and
    /// round-trips through save/load. The eviction policy is pinned Tier-1; these exercise the live
    /// store on the world component. All cases run under a temporary RimSynapse storyteller (the
    /// store is inert under vanilla) and clean up their probe entries.
    /// </summary>
    [SynapseTestSet]
    public static class WorldHistoryCases
    {
        private const string R = "WorldHistoryCaseRegion";

        private static string WithRimStoryteller(Func<SynapseCoreWorldComponent, string> body)
        {
            var def = DefDatabase<StorytellerDef>.AllDefsListForReading
                .FirstOrDefault(d => d.comps != null && d.comps.Any(c => c is StorytellerCompProperties_Storyteller));
            if (def == null) return "SKIP: no RimSynapse storyteller def loaded";
            var worldComp = Find.World?.GetComponent<SynapseCoreWorldComponent>();
            if (worldComp == null) return "SKIP: no world component";

            var storyteller = Find.Storyteller;
            var original = storyteller.def;
            try
            {
                storyteller.def = def;
                storyteller.Notify_DefChanged();
                return body(worldComp);
            }
            finally
            {
                worldComp.worldHistory.RemoveAll(e => e.region == R);
                storyteller.def = original;
                storyteller.Notify_DefChanged();
            }
        }

        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_WorldHistoryPersists", () =>
                WithRimStoryteller(worldComp =>
                {
                    int now = Find.TickManager?.TicksGame ?? 0;
                    worldComp.RecordIncidentStart("SolarFlare", R, 350f, "", now);
                    worldComp.RecordIncidentResolution("SolarFlare", R, "ended", now + 100);

                    var byRegion = worldComp.QueryWorldHistory(region: R).ToList();
                    Assert.True(byRegion.Count == 1, "one entry should exist for the region");
                    Assert.True(byRegion[0].resolved && byRegion[0].outcome == "ended",
                        "the start must have been closed by the matching resolution");

                    Assert.True(worldComp.QueryWorldHistory(kind: "SolarFlare").Any(e => e.region == R),
                        "query by kind must find the entry");
                    Assert.True(!worldComp.QueryWorldHistory(region: R, sinceTick: now + 1000).Any(),
                        "time filter must exclude entries before sinceTick");
                    return "start recorded, resolution closed it, queryable by region/kind/time";
                }),
                tier: "Execution", polarity: "positive",
                scenario: "A regional incident starts and resolves",
                expectation: "Recorded to the world history, queryable by region/kind/time");

            yield return new SynapseTestCase("Core_OpenThreadsSurfaced", () =>
                WithRimStoryteller(worldComp =>
                {
                    int now = Find.TickManager?.TicksGame ?? 0;
                    worldComp.RecordIncidentStart("ToxicFallout", R, 0f, "", now - 60000); // open, ~1 day ago
                    worldComp.RecordIncidentStart("SolarFlare", R, 0f, "", now - 120000);
                    worldComp.RecordIncidentResolution("SolarFlare", R, "ended", now);      // resolved

                    var open = worldComp.OpenThreads().Where(e => e.region == R).ToList();
                    Assert.True(open.Count == 1 && open[0].kind == "ToxicFallout",
                        "only the unresolved incident is an open thread");

                    string block = worldComp.WorldHistoryContextBlock();
                    Assert.Contains(block, "ToxicFallout", "the open thread must surface into the context block");
                    Assert.DoesNotContain(block, "SolarFlare",
                        "a resolved incident must NOT appear as an open thread");
                    return "open thread surfaced to context; resolved incident excluded";
                }),
                tier: "Execution", polarity: "positive",
                scenario: "An unresolved incident lingers",
                expectation: "It is surfaced to the Storyteller as an open thread");

            yield return new SynapseTestCase("Core_WorldHistorySaveRoundTrip", () =>
                WithRimStoryteller(worldComp =>
                {
                    int now = Find.TickManager?.TicksGame ?? 0;
                    worldComp.RecordIncidentStart("SolarFlare", R, 350f, "origin-x", now);
                    var entry = worldComp.QueryWorldHistory(region: R).First();

                    // Round-trip the entry through Scribe the way a save/load would.
                    var reloaded = ScribeRoundTrip(entry);
                    Assert.NotNull(reloaded, "entry must survive a scribe round-trip");
                    Assert.Equal("SolarFlare", reloaded.kind, "kind must survive save/load");
                    Assert.Equal(R, reloaded.region, "region must survive save/load");
                    Assert.True(!reloaded.resolved && reloaded.resolvedTick == -1,
                        "an open entry must round-trip as open with resolvedTick -1 (safe default)");
                    return "world-history entry round-trips through Scribe with safe defaults";
                }),
                tier: "Execution", polarity: "positive",
                scenario: "A save is written and reloaded with world history present",
                expectation: "Entries round-trip cleanly with safe defaults");
        }

        /// <summary>Save one entry to a scratch file and load it back, exercising ExposeData both ways.</summary>
        private static WorldHistoryEntry ScribeRoundTrip(WorldHistoryEntry entry)
        {
            string path = System.IO.Path.Combine(GenFilePaths.ConfigFolderPath, "synapse_worldhistory_roundtrip.xml");
            try
            {
                var toSave = entry;
                Scribe.saver.InitSaving(path, "test");
                try { Scribe_Deep.Look(ref toSave, "entry"); }
                finally { Scribe.saver.FinalizeSaving(); }

                WorldHistoryEntry loaded = null;
                Scribe.loader.InitLoading(path);
                try { Scribe_Deep.Look(ref loaded, "entry"); }
                finally { Scribe.loader.FinalizeLoading(); }
                return loaded;
            }
            finally
            {
                try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }
            }
        }
    }
}
