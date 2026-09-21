using System.Collections.Generic;
using System.Linq;
using System.Text;
using LudeonTK;
using RimSynapse.Comps;
using RimSynapse.Models;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for memory coalescing (Core #129). Adds, through the normal AddMemory path,
    /// several near-identical owner-witness memories (which collapse to one) and several memories
    /// sharing a sourceEventId (which collapse to one), plus a distinct memory (which survives), then
    /// asserts the collapse and occurrence counts. Cleans up its probe memories. Headlessly runnable.
    /// </summary>
    public static class DebugActions_MemoryCoalescing
    {
        [DebugAction("RimSynapse", "Memory coalescing: dup collapse probe",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ProbeCoalescing()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[RimSynapse] Memory coalescing (Core #129):");

            var colonists = Find.CurrentMap?.mapPawns?.FreeColonists?.ToList();
            var owner = colonists?.FirstOrDefault(p => p.TryGetComp<SynapseCorePawnComp>() != null);
            if (owner == null) { sb.AppendLine("  no colonist with a SynapseCorePawnComp."); SynapseLogger.Message(sb.ToString().TrimEnd()); return; }
            var subject = colonists.FirstOrDefault(p => p != owner) ?? owner;
            var comp = owner.TryGetComp<SynapseCorePawnComp>();

            string ownerId = SynapseCorePawnComp.MemoryPawnId(owner);
            string subjId = SynapseCorePawnComp.MemoryPawnId(subject);
            long now = Find.TickManager != null ? Find.TickManager.TicksAbs : 0L;

            var before = new HashSet<WeightedMemory>(comp.memories);
            var newOnes = new List<WeightedMemory>();
            try
            {
                // 4 near-identical owner-witness observations, no event id -> collapse to 1 (heuristic).
                for (int i = 0; i < 4; i++)
                {
                    var m = new WeightedMemory { summary = "probe witness obs " + i, memoryType = "social", absTick = now, weight = 0.4f };
                    m.subjectPawnIds.Add(subjId);
                    m.witnessPawnIds.Add(ownerId);
                    comp.AddMemory(m);
                }
                // 3 memories of one event (owner first-hand) -> collapse to 1 (sourceEventId).
                for (int i = 0; i < 3; i++)
                {
                    var m = new WeightedMemory { summary = "probe event A " + i, memoryType = "EventReflection", absTick = now, weight = 0.5f, sourceEventId = "probe-evt-A" };
                    m.involvedPawnIds.Add(ownerId);
                    comp.AddMemory(m);
                }
                // 1 distinct event -> survives on its own.
                var d = new WeightedMemory { summary = "probe event B (distinct)", memoryType = "EventReflection", absTick = now, weight = 0.6f, sourceEventId = "probe-evt-B" };
                d.involvedPawnIds.Add(ownerId);
                comp.AddMemory(d);

                newOnes = comp.memories.Where(m => !before.Contains(m)).ToList();
                var witnessKeeper = newOnes.FirstOrDefault(m => m.memoryType == "social");
                var eventKeeper = newOnes.FirstOrDefault(m => m.sourceEventId == "probe-evt-A");
                var distinctKeeper = newOnes.FirstOrDefault(m => m.sourceEventId == "probe-evt-B");

                sb.AppendLine($"  added 8 memories -> {newOnes.Count} records kept (expect 3)");
                sb.AppendLine($"  witness collapse: occurrenceCount={witnessKeeper?.occurrenceCount} (expect 4)");
                sb.AppendLine($"  same-event collapse: occurrenceCount={eventKeeper?.occurrenceCount} (expect 3)");
                sb.AppendLine($"  distinct survives: occurrenceCount={distinctKeeper?.occurrenceCount} (expect 1)");

                bool pass = newOnes.Count == 3
                            && witnessKeeper != null && witnessKeeper.occurrenceCount == 4
                            && eventKeeper != null && eventKeeper.occurrenceCount == 3
                            && distinctKeeper != null && distinctKeeper.occurrenceCount == 1;
                sb.AppendLine($"  RESULT: {(pass ? "PASS" : "FAIL")}");
            }
            finally
            {
                foreach (var m in newOnes) comp.RemoveMemory(m);
                sb.AppendLine($"  probe memories removed ({newOnes.Count}).");
            }

            SynapseLogger.Message(sb.ToString().TrimEnd());
        }
    }
}
