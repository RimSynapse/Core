using System.Collections.Generic;
using System.Linq;
using RimSynapse.Comps;
using RimSynapse.Models;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Stage 1 of the 0.7.1 redesign: the weight lifecycle (Core #73–#78). Scale normalisation,
    /// class-driven decay + fast pruning, relational salience &amp; consolidation, tier-based context
    /// selection, and stable memory ids. Deterministic — operates on a bare comp with populated memories.
    /// </summary>
    [SynapseTestSet]
    public static class MemoryLifecycleCases
    {
        private static WeightedMemory Mem(string summary, float weight, string type, long absTick = 100,
            List<string> tags = null, List<string> pawnIds = null, bool longTerm = false)
        {
            return new WeightedMemory
            {
                summary = summary,
                weight = weight,
                baseWeight = weight,
                memoryType = type,
                absTick = absTick,
                isLongTerm = longTerm,
                tags = tags ?? new List<string>(),
                subjectPawnIds = pawnIds ?? new List<string>()
            };
        }

        public static IEnumerable<SynapseTestCase> All()
        {
            // #74: AddMemory clamps a legacy over-scale weight down to 0–1.
            yield return new SynapseTestCase("Core_MemoryScaleClampsOnAdd", () =>
            {
                var comp = new SynapseCorePawnComp();
                comp.AddMemory(Mem("legacy defining event", 10f, "EventReflection"));
                comp.AddMemory(Mem("already normalised", 0.4f, "EventReflection"));
                Assert.True(comp.memories[0].weight <= 1.0f, "weight 10 must clamp to <=1");
                Assert.True(comp.memories[0].weight > 0f, "clamped weight must stay positive");
                Assert.Equal(0.4f, comp.memories[1].weight, "an in-range weight is left untouched");
                return $"10 -> {comp.memories[0].weight}, 0.4 -> {comp.memories[1].weight}";
            });

            // #73: memId is assigned on add and is deterministic for the same summary+absTick.
            yield return new SynapseTestCase("Core_MemIdAssignedAndStable", () =>
            {
                var comp = new SynapseCorePawnComp();
                comp.AddMemory(Mem("shared a meal", 0.3f, "social", 500));
                Assert.NotEmpty(comp.memories[0].memId, "memId assigned on AddMemory");

                var a = Mem("identical text", 0.2f, "social", 777);
                var b = Mem("identical text", 0.2f, "social", 777);
                a.EnsureMemId(); b.EnsureMemId();
                Assert.Equal(a.memId, b.memId, "same summary+absTick -> same deterministic id");
                var c = Mem("identical text", 0.2f, "social", 778);
                c.EnsureMemId();
                Assert.True(a.memId != c.memId, "different absTick -> different id");
                return $"memId={comp.memories[0].memId}";
            });

            // #75: idle chit-chat with no significant links decays out within one maintenance pass.
            yield return new SynapseTestCase("Core_ChitChatPrunesFast", () =>
            {
                SynapseCorePawnComp.MemoryDecayMultiplier = 1.0f;
                var comp = new SynapseCorePawnComp();
                comp.memories.Add(Mem("idle banter about the weather", 0.1f, "social", 100, pawnIds: new List<string> { "P1" }));
                comp.RunMemoryMaintenance();
                Assert.Equal(0, comp.memories.Count, "lone chit-chat must prune (social decay 0.5 > weight 0.1)");
                return "pruned in one pass";
            });

            // #76: the SAME chit-chat, linked to a pawn who then dies, is consolidated instead of pruned.
            yield return new SynapseTestCase("Core_ChitChatLinkedToDeathConsolidates", () =>
            {
                SynapseCorePawnComp.MemoryDecayMultiplier = 1.0f;
                var comp = new SynapseCorePawnComp();
                comp.memories.Add(Mem("idle banter with Tynan", 0.1f, "social", 100, pawnIds: new List<string> { "Tynan" }));
                comp.memories.Add(Mem("Tynan died in the raid", 1.0f, "EventReflection", 200,
                    tags: new List<string> { "Death" }, pawnIds: new List<string> { "Tynan" }));

                comp.RunMemoryMaintenance();

                var chat = comp.memories.FirstOrDefault(m => m.summary.StartsWith("idle banter"));
                Assert.NotNull(chat, "linked chit-chat must survive (not pruned)");
                Assert.True(chat.isLongTerm, "linked chit-chat must be consolidated to long-term");
                return $"chatter salience={chat.salience:F2}, longTerm={chat.isLongTerm}";
            });

            // #78: context selection prefers long-term/high-salience over a stale higher-weight short-term.
            yield return new SynapseTestCase("Core_ContextSelectionPrefersLongTerm", () =>
            {
                var comp = new SynapseCorePawnComp();
                var stale = Mem("stale minor short-term", 0.5f, "EventReflection", 100);
                var lt = Mem("consolidated long-term", 0.3f, "EventReflection", 100, longTerm: true);
                lt.salience = 1.5f;
                comp.AddMemory(stale);
                comp.AddMemory(lt);

                var selected = comp.SelectMemoriesForContext(comp.memories, 1);
                Assert.Equal(1, selected.Count, "budget of 1 returns one memory");
                Assert.True(selected[0].isLongTerm, "long-term is preferred over a higher-weight stale short-term");
                return $"picked '{selected[0].summary}'";
            });

            // #77: reinforcement/surfacing never lowers a max-tier memory's weight.
            yield return new SynapseTestCase("Core_SurfacingDoesNotCrushMaxWeight", () =>
            {
                var comp = new SynapseCorePawnComp();
                var strong = Mem("a defining, maxed-out memory", 1.0f, "EventReflection", 100, longTerm: true);
                comp.AddMemory(strong);
                int before = strong.timesReferenced;
                comp.SelectMemoriesForContext(comp.memories, 5);
                Assert.Equal(1.0f, strong.weight, "surfacing must not lower a max-tier weight");
                Assert.True(strong.timesReferenced > before, "surfacing counts as a reference");
                return $"weight stayed {strong.weight}, refs {before}->{strong.timesReferenced}";
            });
        }
    }
}
