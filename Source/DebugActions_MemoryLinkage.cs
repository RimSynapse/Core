using System.Linq;
using System.Text;
using LudeonTK;
using RimWorld;
using Verse;
using RimSynapse.Comps;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for the Core-owned memory-linkage framework (Core #80), grouped under
    /// "RimSynapse". Records two memories ABOUT the same pawn through AddMemoryAbout and shows they
    /// share the ONE canonical subjectPawnId (GetUniqueLoadID) and index together — the property that
    /// lets relational consolidation connect "chit-chat about X" to "X died". Cleans up its probe
    /// memories. Headlessly runnable via run_debug_action.
    /// </summary>
    public static class DebugActions_MemoryLinkage
    {
        [DebugAction("RimSynapse", "Memory linkage: two memories about one pawn share subjectId",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ProbeMemoryLinkage()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[RimSynapse] Memory-linkage framework (Core #80):");

            var colonists = Find.CurrentMap?.mapPawns?.FreeColonists?.ToList();
            var owner = colonists?.FirstOrDefault(p => p.TryGetComp<SynapseCorePawnComp>() != null);
            if (owner == null) { sb.AppendLine("  no colonist with a SynapseCorePawnComp on this map."); SynapseLogger.Message(sb.ToString().TrimEnd()); return; }
            var subject = colonists.FirstOrDefault(p => p != owner) ?? owner;
            var comp = owner.TryGetComp<SynapseCorePawnComp>();

            string subjectId = SynapseCorePawnComp.MemoryPawnId(subject);
            sb.AppendLine($"  owner: {owner.LabelShort}; subject: {subject.LabelShort}");
            sb.AppendLine($"  canonical subject id (GetUniqueLoadID): {subjectId}");
            sb.AppendLine($"  (ThingID, the OLD inconsistent scheme, would have been: {subject.ThingID})");

            RimSynapse.Models.WeightedMemory chit = null, death = null;
            try
            {
                chit = comp.AddMemoryAbout(subject, "probe: chit-chat about subject", "social", 0.10f,
                    tags: new System.Collections.Generic.List<string> { "conversation" });
                death = comp.AddMemoryAbout(subject, "probe: subject died", "event", 0.60f);

                var linked = comp.GetMemoriesByPawnId(subjectId);
                sb.AppendLine($"  memories indexed under the subject id: {linked?.Count ?? 0} " +
                              $"(chit present={linked?.Contains(chit)}, death present={linked?.Contains(death)})");
                sb.AppendLine($"  both share subjectPawnId: {chit.subjectPawnIds.Contains(subjectId) && death.subjectPawnIds.Contains(subjectId)} " +
                              $"-> consolidation CAN connect chit-chat-about-X to X-died.");
            }
            finally
            {
                if (chit != null) comp.RemoveMemory(chit);
                if (death != null) comp.RemoveMemory(death);
                sb.AppendLine("  probe memories removed.");
            }

            SynapseLogger.Message(sb.ToString().TrimEnd());
        }
    }
}
