using System.Linq;
using System.Text;
using LudeonTK;
using RimSynapse.Models;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for the memory-involvement API (Core #103). Builds a throwaway memory with a
    /// known roster over real colonists and asserts <see cref="SynapseCoreMemory.InvolvementOf"/> /
    /// <see cref="SynapseCoreMemory.SharesFirsthand"/> classify protagonist / participant / witness /
    /// stranger correctly, and that an empty-roster (legacy) memory resolves to None for everyone.
    /// Headlessly runnable via run_debug_action.
    /// </summary>
    public static class DebugActions_MemoryInvolvement
    {
        [DebugAction("RimSynapse", "Memory involvement: probe tiers",
            allowedGameStates = AllowedGameStates.Playing)]
        private static void ProbeInvolvement()
        {
            var pawns = Find.CurrentMap?.mapPawns?.FreeColonists?.ToList();
            if (pawns == null || pawns.Count < 3)
            {
                SynapseLogger.Warning("[RimSynapse] Memory involvement probe needs >=3 colonists on the map.");
                return;
            }

            Pawn protagonist = pawns[0], participant = pawns[1], witness = pawns[2];
            Pawn stranger = pawns.Count >= 4 ? pawns[3] : null;

            var mem = new WeightedMemory
            {
                summary = "debug involvement probe",
                memoryType = "EventReflection",
                sourceEventId = "debug-event-1",
            };
            mem.involvedPawnIds.Add(protagonist.GetUniqueLoadID()); // first = protagonist
            mem.involvedPawnIds.Add(participant.GetUniqueLoadID());
            mem.witnessPawnIds.Add(witness.GetUniqueLoadID());

            var empty = new WeightedMemory { summary = "legacy, no roster", memoryType = "social" };

            var sb = new StringBuilder();
            sb.AppendLine("[RimSynapse] Memory involvement probe (Core #103):");
            sb.AppendLine($"  {protagonist.LabelShort}: {SynapseCoreMemory.InvolvementOf(mem, protagonist)} " +
                          $"(firsthand={SynapseCoreMemory.SharesFirsthand(mem, protagonist)})  [expect Protagonist/true]");
            sb.AppendLine($"  {participant.LabelShort}: {SynapseCoreMemory.InvolvementOf(mem, participant)} " +
                          $"(firsthand={SynapseCoreMemory.SharesFirsthand(mem, participant)})  [expect Participant/true]");
            sb.AppendLine($"  {witness.LabelShort}: {SynapseCoreMemory.InvolvementOf(mem, witness)} " +
                          $"(firsthand={SynapseCoreMemory.SharesFirsthand(mem, witness)})  [expect Witness/false]");
            if (stranger != null)
                sb.AppendLine($"  {stranger.LabelShort}: {SynapseCoreMemory.InvolvementOf(mem, stranger)} " +
                              $"(firsthand={SynapseCoreMemory.SharesFirsthand(mem, stranger)})  [expect None/false]");
            sb.AppendLine($"  legacy empty-roster vs {protagonist.LabelShort}: {SynapseCoreMemory.InvolvementOf(empty, protagonist)} " +
                          $"(firsthand={SynapseCoreMemory.SharesFirsthand(empty, protagonist)})  [expect None/false]");

            bool pass =
                SynapseCoreMemory.InvolvementOf(mem, protagonist) == MemoryInvolvement.Protagonist &&
                SynapseCoreMemory.InvolvementOf(mem, participant) == MemoryInvolvement.Participant &&
                SynapseCoreMemory.InvolvementOf(mem, witness) == MemoryInvolvement.Witness &&
                SynapseCoreMemory.SharesFirsthand(mem, protagonist) &&
                !SynapseCoreMemory.SharesFirsthand(mem, witness) &&
                SynapseCoreMemory.InvolvementOf(empty, protagonist) == MemoryInvolvement.None;
            sb.AppendLine($"  RESULT: {(pass ? "PASS" : "FAIL")}");

            SynapseLogger.Message(sb.ToString().TrimEnd());
        }
    }
}
