using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using RimSynapse.Comps;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Covers the Core-owned memory-linkage framework (Core #80): a memory recorded "about" a pawn
    /// via <see cref="SynapseCorePawnComp.AddMemoryAbout"/> keys its subject on the ONE canonical
    /// scheme (<see cref="SynapseCorePawnComp.MemoryPawnId"/> = GetUniqueLoadID), routes through the
    /// indexed AddMemory, and is therefore findable alongside other memories about the same pawn —
    /// so relational consolidation can actually connect "chit-chat about X" to "X died". Producers
    /// (Conversations, Psychology) leverage this instead of hand-rolling a WeightedMemory with a
    /// ThingID stuffed in a tag.
    /// </summary>
    [SynapseTestSet]
    public static class MemoryLinkageCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_MemoryAboutLinksBySubjectId", () =>
            {
                var colonists = Find.CurrentMap?.mapPawns?.FreeColonists?.ToList() ?? new List<Pawn>();
                var owner = colonists.FirstOrDefault(p => p.TryGetComp<SynapseCorePawnComp>() != null);
                if (owner == null) return "SKIP: no colonist with a SynapseCorePawnComp";
                var subject = colonists.FirstOrDefault(p => p != owner) ?? owner; // a memory about someone
                var comp = owner.TryGetComp<SynapseCorePawnComp>();

                string subjectId = SynapseCorePawnComp.MemoryPawnId(subject);
                Assert.NotEmpty(subjectId, "the canonical subject id must be non-empty");
                Assert.Equal(subject.GetUniqueLoadID(), subjectId,
                    "the canonical scheme must be GetUniqueLoadID (not ThingID)");

                RimSynapse.Models.WeightedMemory chit = null, death = null;
                try
                {
                    // Two producers' worth of memory ABOUT the same pawn, recorded through the framework.
                    chit = comp.AddMemoryAbout(subject, "#80 test: chit-chat about the subject", "social", 0.10f,
                        tags: new List<string> { "conversation" });
                    death = comp.AddMemoryAbout(subject, "#80 test: the subject died", "event", 0.60f);

                    Assert.True(chit.subjectPawnIds.Contains(subjectId),
                        "chit-chat memory must carry the canonical subject id");
                    Assert.True(death.subjectPawnIds.Contains(subjectId),
                        "event memory must carry the canonical subject id");
                    Assert.True(chit.subjectPawnIds.Contains(subjectId) && !chit.subjectPawnIds.Contains(subject.ThingID)
                        || subject.GetUniqueLoadID() == subject.ThingID,
                        "linkage must use GetUniqueLoadID, not ThingID");

                    // Both are findable under the ONE key — this is what lets consolidation connect them.
                    var linked = comp.GetMemoriesByPawnId(subjectId);
                    Assert.True(linked != null && linked.Contains(chit) && linked.Contains(death),
                        "both memories about the subject must be indexed under the same subject id");

                    // Both routed through the indexed AddMemory (memId assigned, present in the store).
                    Assert.True(comp.memories.Contains(chit) && comp.memories.Contains(death),
                        "AddMemoryAbout must route through the indexed AddMemory");
                }
                finally
                {
                    if (chit != null) comp.RemoveMemory(chit);
                    if (death != null) comp.RemoveMemory(death);
                }
                return "two memories about the same pawn share the canonical subjectPawnId and index together";
            },
                tier: "Execution", polarity: "positive",
                scenario: "Two producers record memories about the same pawn",
                expectation: "They share one canonical subjectPawnId and index together, so consolidation can link them");
        }
    }
}
