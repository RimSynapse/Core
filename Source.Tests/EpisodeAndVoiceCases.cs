using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using RimSynapse.Comps;
using RimSynapse.Models;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Core's episode model (Core#88) and the Kokoro voice catalog (Conversations#33's Core half).
    /// Split out of the Conversations case set when cases moved to the repos they test.
    /// </summary>
    [SynapseTestSet]
    public static class EpisodeAndVoiceCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            // Kokoro voice catalog (Conversations#33): gender-appropriate, stable, known ids.
            yield return new SynapseTestCase("Core_KokoroVoiceCatalog", () =>
            {
                Assert.True(KokoroVoices.EnglishMale.Length > 0 && KokoroVoices.EnglishFemale.Length > 0, "catalog populated");
                Assert.True(KokoroVoices.IsKnown("am_michael") && KokoroVoices.IsKnown("af_bella"), "known ids recognized");
                Assert.False(KokoroVoices.IsKnown("zz_nobody"), "unknown id rejected");
                Assert.False(KokoroVoices.EnglishFemale.Contains("am_michael"), "pools are gender-separated");

                Map map = Find.CurrentMap ?? Find.Maps.FirstOrDefault();
                Assert.True(map != null, "no map available");
                var p = map.mapPawns.FreeColonists.FirstOrDefault();
                Assert.True(p != null, "no colonist available");

                string v1 = KokoroVoices.RandomVoiceFor(p);
                Assert.True(KokoroVoices.IsKnown(v1), "assigned voice is a known id");
                Assert.True(KokoroVoices.PoolFor(p).Contains(v1), "assigned voice is gender-appropriate");
                Assert.Equal(v1, KokoroVoices.RandomVoiceFor(p), "voice is stable for a given pawn");
                return $"catalog M={KokoroVoices.EnglishMale.Length} F={KokoroVoices.EnglishFemale.Length}; {p.LabelShort}->{v1}";
            });

            // Episode coalescing (Core#88): trivial repeats of one ordeal roll up; serious stays distinct.
            yield return new SynapseTestCase("Core_EpisodeCoalescing", () =>
            {
                var wc = new SynapseCoreWorldComponent(Find.World);
                for (int i = 0; i < 5; i++)
                    wc.EnqueuePastEvent(new PastEvent
                    {
                        mcpTag = "Injury(TestPawn vs a crow)", category = "ColonistInjured",
                        severity = EventSeverity.Trivial, eventDescription = "clawed",
                        involvedPawnIds = new List<string> { "T1" }
                    });
                Assert.Equal(1, wc.BacklogCount, "5 trivial same-key wounds coalesce to one episode");
                var ep = wc.AllEvents.First();
                Assert.Equal(5, ep.occurrenceCount, "occurrenceCount rolled up to 5");

                wc.EnqueuePastEvent(new PastEvent
                {
                    mcpTag = "Injury(TestPawn vs a crow)", category = "ColonistInjured",
                    severity = EventSeverity.Serious, eventDescription = "mauled",
                    involvedPawnIds = new List<string> { "T1" }
                });
                Assert.Equal(2, wc.BacklogCount, "a serious wound stays a distinct entry (not coalesced)");
                return $"coalesced 5->1 (count {ep.occurrenceCount}) + 1 serious distinct";
            });

            // Settling + significance floor (Core#88): unsettled withheld; lone trivial dropped.
            yield return new SynapseTestCase("Core_EpisodeSettleAndFloor", () =>
            {
                int now = Find.TickManager.TicksGame;

                var wc = new SynapseCoreWorldComponent(Find.World);
                wc.EnqueuePastEvent(new PastEvent
                {
                    mcpTag = "Raid(Alpha)", severity = EventSeverity.Standard,
                    eventDescription = "raid", involvedPawnIds = new List<string> { "T1" }
                });
                Assert.False(wc.TryDequeuePastEvent(out _), "a fresh (unsettled) episode is withheld");
                wc.AllEvents.First().lastUpdateTick = now - 5000;   // force settled
                Assert.True(wc.TryDequeuePastEvent(out var got) && got != null, "a settled episode is returned");

                var wc2 = new SynapseCoreWorldComponent(Find.World);
                wc2.EnqueuePastEvent(new PastEvent
                {
                    mcpTag = "Injury(TestPawn vs a rat)", severity = EventSeverity.Trivial,
                    eventDescription = "nip", involvedPawnIds = new List<string> { "T1" }
                });
                wc2.AllEvents.First().lastUpdateTick = now - 5000;
                Assert.False(wc2.TryDequeuePastEvent(out _), "a lone settled trivial is dropped by the significance floor");
                Assert.Equal(0, wc2.BacklogCount, "dropped trivial is removed from the backlog");
                return "settle withholds then releases; floor drops lone trivial";
            });
        }
    }
}
