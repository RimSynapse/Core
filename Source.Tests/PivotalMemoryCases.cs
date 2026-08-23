using System.Collections.Generic;
using System.Linq;
using RimSynapse;
using RimSynapse.Comps;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Pivotal life-event secured memories (Core #92). Deterministic — SynapsePivotalMemory.RecordOn
    /// operates on a bare comp. Each case uses a distinct pivotal class so nothing collides, and relies
    /// on the shipped bornLongTerm SynapseMemoryClassDefs being loaded (they are, in-game).
    /// </summary>
    [SynapseTestSet]
    public static class PivotalMemoryCases
    {
        private static string Tag(string t) => SynapsePivotalMemory.PivotalTagPrefix + t;

        public static IEnumerable<SynapseTestCase> All()
        {
            // A pivotal memory is secured (isLongTerm) the moment it is recorded.
            yield return new SynapseTestCase("Core_PivotalMemory_IsSecuredLongTerm", () =>
            {
                var comp = new SynapseCorePawnComp();
                string id = SynapsePivotalMemory.RecordOn(comp, SynapsePivotalMemory.Arrested, "arrested");
                Assert.NotEmpty(id, "a pivotal memory is recorded and returns its id");
                var m = comp.GetMemoriesByTag(Tag(SynapsePivotalMemory.Arrested)).FirstOrDefault();
                Assert.NotNull(m, "the pivotal memory is retrievable by its tag");
                Assert.True(m.isLongTerm, "a pivotal memory is secured as long-term at creation");
                return $"isLongTerm={m.isLongTerm}, weight={m.weight:F2}";
            });

            // It survives repeated daily maintenance (decay + consolidation) unchanged.
            yield return new SynapseTestCase("Core_PivotalMemory_SurvivesConsolidation", () =>
            {
                var comp = new SynapseCorePawnComp();
                SynapsePivotalMemory.RecordOn(comp, SynapsePivotalMemory.Converted, "converted");
                var m0 = comp.GetMemoriesByTag(Tag(SynapsePivotalMemory.Converted)).First();
                float w0 = m0.weight;
                for (int i = 0; i < 5; i++) comp.RunMemoryMaintenance();
                var m1 = comp.GetMemoriesByTag(Tag(SynapsePivotalMemory.Converted)).FirstOrDefault();
                Assert.NotNull(m1, "the secured memory survives 5 maintenance passes");
                Assert.Equal(w0, m1.weight, "its weight is untouched by decay");
                return $"survived 5 passes, weight {w0:F2}->{m1.weight:F2}";
            });

            // Idempotent: recording the same pivotal class twice keeps a single memory.
            yield return new SynapseTestCase("Core_PivotalMemory_Idempotent", () =>
            {
                var comp = new SynapseCorePawnComp();
                string a = SynapsePivotalMemory.RecordOn(comp, SynapsePivotalMemory.Enslaved, "enslaved once");
                string b = SynapsePivotalMemory.RecordOn(comp, SynapsePivotalMemory.Enslaved, "enslaved again");
                Assert.Equal(a, b, "a repeat event returns the existing memory's id");
                int count = comp.GetMemoriesByTag(Tag(SynapsePivotalMemory.Enslaved)).Count;
                Assert.Equal(1, count, "no duplicate secured memory on a repeated event");
                return "one memory, id stable";
            });

            // A non-bornLongTerm memoryType is refused — a pivotal memory that isn't secured is a bug, not a memory.
            yield return new SynapseTestCase("Core_PivotalMemory_RejectsNonBornLongTerm", () =>
            {
                var comp = new SynapseCorePawnComp();
                int before = comp.memories.Count;
                string id = SynapsePivotalMemory.RecordOn(comp, "social", "not a pivotal class");
                Assert.True(id == null, "a non-bornLongTerm memoryType is refused");
                Assert.Equal(before, comp.memories.Count, "nothing is added for a non-pivotal type");
                return "non-bornLongTerm refused";
            });
        }
    }
}
