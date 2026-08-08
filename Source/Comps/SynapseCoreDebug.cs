using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Verse;
using RimSynapse.Comps;
using RimSynapse.Models;

namespace RimSynapse
{
    /// <summary>
    /// Shared implementation for the memory debug commands (Core#81), called by both the registered
    /// game tools (DebugMemoryTools) and the in-game DevMode gizmos. Core owns the memory system, so the
    /// seed / inspect / force-maintenance logic lives here; Psychology's live-LLM debug reuses it.
    /// </summary>
    public static class SynapseCoreDebug
    {
        /// <summary>Resolve a pawn by short label across all maps (colonists first).</summary>
        public static Pawn FindPawn(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var map in Find.Maps)
            {
                var p = map.mapPawns.FreeColonists.FirstOrDefault(x => x.LabelShort.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (p != null) return p;
            }
            foreach (var map in Find.Maps)
            {
                var p = map.mapPawns.AllPawns.FirstOrDefault(x => x.Name != null && x.LabelShort.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (p != null) return p;
            }
            return null;
        }

        /// <summary>Seed a memory on a pawn (routed through AddMemory). Returns the memId.</summary>
        public static string AddMemory(Pawn pawn, string summary, string memoryType, float weight,
            List<string> tags, Pawn subject, bool isLongTerm)
            => AddMemory(pawn?.TryGetComp<SynapseCorePawnComp>(), summary, memoryType, weight, tags, subject?.GetUniqueLoadID(), isLongTerm);

        /// <summary>Comp-level seed (0-1 normalise, memId, index applied by AddMemory). Testable without a Pawn.</summary>
        public static string AddMemory(SynapseCorePawnComp comp, string summary, string memoryType, float weight,
            List<string> tags, string subjectId, bool isLongTerm)
        {
            if (comp == null || string.IsNullOrEmpty(summary)) return null;
            var mem = new WeightedMemory
            {
                summary = summary,
                memoryType = string.IsNullOrEmpty(memoryType) ? "EventReflection" : memoryType,
                weight = weight,
                baseWeight = weight,
                tags = tags ?? new List<string>(),
                subjectPawnIds = string.IsNullOrEmpty(subjectId) ? new List<string>() : new List<string> { subjectId },
                isLongTerm = isLongTerm,
                absTick = Find.TickManager?.TicksAbs ?? 0L
            };
            comp.AddMemory(mem);
            return mem.memId;
        }

        public static string DumpMemories(Pawn pawn) => DumpMemories(pawn?.TryGetComp<SynapseCorePawnComp>());

        /// <summary>Human-readable dump of a comp's memories (long-term first, then by weight).</summary>
        public static string DumpMemories(SynapseCorePawnComp comp)
        {
            if (comp == null) return "(no SynapseCorePawnComp)";
            if (comp.memories.Count == 0) return "(no memories)";
            var sb = new StringBuilder();
            sb.AppendLine($"{comp.memories.Count} memories ({comp.memories.Count(m => m.isLongTerm)} long-term)");
            foreach (var m in comp.memories.OrderByDescending(x => x.isLongTerm).ThenByDescending(x => x.weight))
            {
                sb.AppendLine($"  [{m.memoryType}] w={m.weight:0.00} sal={m.salience:0.00} {(m.isLongTerm ? "LONG" : "short")} refs={m.timesReferenced} tags=[{string.Join(",", m.tags ?? new List<string>())}] :: {m.summary}");
            }
            return sb.ToString().TrimEnd();
        }

        public static string RunMaintenance(Pawn pawn) => RunMaintenance(pawn?.TryGetComp<SynapseCorePawnComp>());

        /// <summary>Force the daily decay + salience/consolidation pass now. Returns a before/after summary.</summary>
        public static string RunMaintenance(SynapseCorePawnComp comp)
        {
            if (comp == null) return "(no SynapseCorePawnComp)";
            int before = comp.memories.Count;
            int ltBefore = comp.memories.Count(m => m.isLongTerm);
            comp.RunMemoryMaintenance();
            int after = comp.memories.Count;
            int ltAfter = comp.memories.Count(m => m.isLongTerm);
            return $"memories {before}->{after} (pruned {before - after}); long-term {ltBefore}->{ltAfter} (consolidated {ltAfter - ltBefore})";
        }
    }
}
