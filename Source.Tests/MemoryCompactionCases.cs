using System.Collections.Generic;
using System.Linq;
using RimSynapse.Comps;
using RimSynapse.Models;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Automatic memory compaction (Core #131): the deterministic half — protection, source selection per
    /// tier, and the fold-apply — exercised on a bare comp with no LLM. The prose call and its cadence are
    /// validated in-game. <see cref="SynapseCorePawnComp.CompactionMinAgeTicks"/> depends on settings, which
    /// are null in a bare-comp test, so selection uses the <c>force</c> path (age gate bypassed) where age
    /// isn't the thing under test.
    /// </summary>
    [SynapseTestSet]
    public static class MemoryCompactionCases
    {
        private static WeightedMemory Mem(string summary, string type = "social", long absTick = 100,
            float weight = 0.1f, List<string> tags = null, List<string> pawnIds = null, bool longTerm = false)
            => new WeightedMemory
            {
                summary = summary, memoryType = type, absTick = absTick, weight = weight, baseWeight = weight,
                isLongTerm = longTerm, tags = tags ?? new List<string>(), subjectPawnIds = pawnIds ?? new List<string>()
            };

        public static IEnumerable<SynapseTestCase> All()
        {
            // Protection: long-term, significant tags, pivotal tags, and high salience are never compacted.
            yield return new SynapseTestCase("Core_CompactionProtection", () =>
            {
                var comp = new SynapseCorePawnComp();
                Assert.True(comp.IsCompactionProtected(Mem("permanent", longTerm: true)), "long-term is protected");
                Assert.True(comp.IsCompactionProtected(Mem("loss", tags: new List<string> { "Grief" })), "significant tag is protected");
                Assert.True(comp.IsCompactionProtected(Mem("joined", tags: new List<string> { "pivotal:LifeEvent_Recruited" })), "pivotal tag is protected");
                var hot = Mem("salient"); hot.salience = 2f;
                Assert.True(comp.IsCompactionProtected(hot), "salience over the consolidation threshold is protected");
                Assert.False(comp.IsCompactionProtected(Mem("just chatter")), "ordinary chatter is compactable");
                return "protection ok";
            });

            // Night selection: groups by day, needs the fold minimum, takes the OLDEST eligible day, and
            // excludes protected + already-compacted memories.
            yield return new SynapseTestCase("Core_CompactionNightSelection", () =>
            {
                var comp = new SynapseCorePawnComp();
                long d1 = 100, d2 = 60000 + 100; // day 0 and day 1
                comp.memories.Add(Mem("d1 a", absTick: d1));
                comp.memories.Add(Mem("d1 b", absTick: d1 + 50));
                comp.memories.Add(Mem("d1 c", absTick: d1 + 90));
                comp.memories.Add(Mem("d2 a", absTick: d2));
                comp.memories.Add(Mem("d2 b", absTick: d2 + 10)); // only 2 on day 1 → below the min of 3
                comp.memories.Add(Mem("d1 grief", absTick: d1 + 20, tags: new List<string> { "Grief" }));
                comp.memories.Add(Mem("d1 already folded", type: "Compacted_Day", absTick: d1 + 30));
                foreach (var m in comp.memories) m.EnsureMemId();

                var night = comp.SelectNightSources(nowAbs: 500000, force: true);
                Assert.Equal(3, night.Count, "the oldest day with >= min compactable memories is chosen");
                Assert.True(night.All(m => m.summary.StartsWith("d1 ") && m.summary != "d1 grief" && m.summary != "d1 already folded"),
                    "only raw, unprotected day-0 memories; the grief and the existing fold are excluded");
                Assert.True(night[0].absTick <= night[2].absTick, "sources are ordered oldest-first");
                return $"night=[{string.Join(",", night.Select(m => m.summary))}]";
            });

            // Age gate: without force, memories younger than the min age are not eligible.
            yield return new SynapseTestCase("Core_CompactionAgeGate", () =>
            {
                var comp = new SynapseCorePawnComp();
                long now = 10_000_000;
                for (int i = 0; i < 4; i++) comp.memories.Add(Mem("fresh " + i, absTick: now - 1000 - i)); // just now
                foreach (var m in comp.memories) m.EnsureMemId();
                // Settings are null in a bare-comp test → CompactionMinAgeTicks falls back to 48h (120000 ticks).
                Assert.Equal(0, comp.SelectNightSources(now, force: false).Count, "fresh memories are within the review window, not eligible");
                Assert.Equal(4, comp.SelectNightSources(now, force: true).Count, "forcing bypasses the age gate");
                return "age gate ok";
            });

            // Pentad selection: only Compacted_Day memories, and at least two.
            yield return new SynapseTestCase("Core_CompactionPentadSelection", () =>
            {
                var comp = new SynapseCorePawnComp();
                comp.memories.Add(Mem("day one", type: "Compacted_Day", absTick: 100));
                comp.memories.Add(Mem("day two", type: "Compacted_Day", absTick: 60000 + 100));
                comp.memories.Add(Mem("raw", type: "social", absTick: 200));
                comp.memories.Add(Mem("a pentad", type: "Compacted_Pentad", absTick: 300));
                foreach (var m in comp.memories) m.EnsureMemId();
                var pentad = comp.SelectPentadSources(nowAbs: 500000, force: true);
                Assert.Equal(2, pentad.Count, "only the two Compacted_Days");
                Assert.True(pentad.All(m => m.memoryType == "Compacted_Day"), "raw memories and existing pentads are excluded");
                return "pentad ok";
            });

            // Apply: the fold replaces its sources with one compacted memory that unions their metadata,
            // and re-resolves by memId so a source removed since selection is simply skipped.
            yield return new SynapseTestCase("Core_CompactionApplyFold", () =>
            {
                var comp = new SynapseCorePawnComp();
                var a = Mem("chatted with Bran", absTick: 100, weight: 0.2f, tags: new List<string> { "conversation" }, pawnIds: new List<string> { "Bran" });
                var b = Mem("helped in the kitchen", absTick: 200, weight: 0.15f, tags: new List<string> { "work" }, pawnIds: new List<string> { "Mei" });
                var c = Mem("watched the sunset", absTick: 150, weight: 0.1f);
                foreach (var m in new[] { a, b, c }) { m.EnsureMemId(); comp.memories.Add(m); }
                int before = comp.memories.Count;

                comp.ApplyFold(new List<string> { a.memId, b.memId, c.memId }, "A quiet day — talked with Bran, cooked, watched the sunset.", "Compacted_Day");
                Assert.Equal(before - 3 + 1, comp.memories.Count, "three sources replaced by one fold");
                var fold = comp.memories.Single(m => m.memoryType == "Compacted_Day");
                Assert.Contains(fold.summary, "Bran", "the fold carries the prose");
                Assert.Equal(0.2f, fold.weight, "weight is the max of the sources (day factor 1.0)");
                Assert.Equal(200L, fold.absTick, "absTick is the newest source");
                Assert.True(fold.subjectPawnIds.Contains("Bran") && fold.subjectPawnIds.Contains("Mei"), "subjects are unioned");
                Assert.True(fold.tags.Contains("conversation") && fold.tags.Contains("work"), "tags are unioned");
                Assert.False(comp.memories.Any(m => m.memId == a.memId), "a source is actually removed");
                return $"folded to \"{fold.summary}\"";
            });

            // Apply is safe when the prose failed or the sources are gone: nothing is removed.
            yield return new SynapseTestCase("Core_CompactionApplyGuards", () =>
            {
                var comp = new SynapseCorePawnComp();
                var a = Mem("one", absTick: 100); var b = Mem("two", absTick: 200);
                foreach (var m in new[] { a, b }) { m.EnsureMemId(); comp.memories.Add(m); }

                comp.ApplyFold(new List<string> { a.memId, b.memId }, "", "Compacted_Day"); // empty prose
                Assert.Equal(2, comp.memories.Count, "empty prose removes nothing");
                comp.ApplyFold(new List<string> { a.memId, "ghost-id" }, "prose", "Compacted_Day"); // only 1 live source
                Assert.Equal(2, comp.memories.Count, "fewer than two live sources removes nothing");
                Assert.False(comp.memories.Any(m => m.memoryType == "Compacted_Day"), "no fold was created");
                return "guards ok";
            });

            // Prose extraction tolerates the wrapping small models add.
            yield return new SynapseTestCase("Core_CompactionProseExtraction", () =>
            {
                Assert.Equal("I loved him.", SynapseCorePawnComp.ExtractMemoryProse("{\"memory\": \"I loved him.\"}"), "plain JSON");
                Assert.Equal("kept it", SynapseCorePawnComp.ExtractMemoryProse("sure! {\"memory\":\"kept it\"} hope that helps"), "wrapped JSON");
                Assert.True(SynapseCorePawnComp.ExtractMemoryProse("not json at all") == null, "non-JSON yields null");
                Assert.True(SynapseCorePawnComp.ExtractMemoryProse("{\"memory\":\"\"}") == null, "empty memory yields null");
                return "extraction ok";
            });
        }
    }
}
