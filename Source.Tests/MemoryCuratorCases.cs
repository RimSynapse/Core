using System.Collections.Generic;
using System.Linq;
using RimSynapse.Comps;
using RimSynapse.Models;
using RimAgentic.Testing;
using Verse;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Manual memory curation (Core #138): the add/remove paths the curator dialog and its debug
    /// actions drive. Deterministic — exercises <see cref="SynapseCorePawnComp.AddMemory"/> /
    /// <see cref="SynapseCorePawnComp.RemoveMemory"/> on a bare comp, asserting count, memId, and
    /// removal idempotence.
    /// </summary>
    [SynapseTestSet]
    public static class MemoryCuratorCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_MemoryManualAddRemove", () =>
            {
                var comp = new SynapseCorePawnComp();
                Assert.Equal(0, comp.memories.Count, "starts empty");

                var a = new WeightedMemory { summary = "alpha: a raider fell at the east gate", memoryType = "raid", weight = 0.6f };
                var b = new WeightedMemory { summary = "beta: shared a quiet meal in the hall", memoryType = "social", weight = 0.3f };
                comp.AddMemory(a);
                comp.AddMemory(b);
                Assert.Equal(2, comp.memories.Count, "two distinct memories added, not coalesced");
                Assert.True(!a.memId.NullOrEmpty() && !b.memId.NullOrEmpty(), "AddMemory assigns a memId");

                // Manual remove of a specific memory.
                Assert.True(comp.RemoveMemory(a), "removing a present memory returns true");
                Assert.Equal(1, comp.memories.Count, "count drops to one");
                Assert.True(!comp.memories.Any(m => m.memId == a.memId), "the removed memory is gone from the bank");
                Assert.True(comp.memories.Any(m => m.memId == b.memId), "the untouched memory remains");

                // Removing the same memory again is a no-op, not a throw.
                Assert.False(comp.RemoveMemory(a), "removing an absent memory returns false");
                Assert.Equal(1, comp.memories.Count, "count unchanged by the no-op remove");

                // Null-safe.
                Assert.False(comp.RemoveMemory(null), "removing null is a safe false");
                return "manual add/remove ok";
            });
        }
    }
}
