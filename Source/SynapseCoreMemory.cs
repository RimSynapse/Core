using RimSynapse.Models;
using Verse;

namespace RimSynapse
{
    /// <summary>Where a pawn stands relative to a remembered event (Core #103).</summary>
    public enum MemoryInvolvement
    {
        /// <summary>Not involved and did not witness — a stranger to the memory.</summary>
        None,
        /// <summary>Saw the event but did not take part in it.</summary>
        Witness,
        /// <summary>Took part in the event first-hand, alongside others.</summary>
        Participant,
        /// <summary>The primary first-hand subject of the event.</summary>
        Protagonist,
    }

    /// <summary>
    /// Core-owned queries over episodic-memory involvement (#103). A consumer (Conversations,
    /// WorldNews) can ask whether a given pawn legitimately shares a memory first-hand, only
    /// witnessed it, or is a stranger to it — so a bystander never asserts first-hand memory of an
    /// event they did not live. Core owns memory, so this is a direct static API, not a provider slot.
    /// </summary>
    public static class SynapseCoreMemory
    {
        /// <summary>
        /// How <paramref name="pawn"/> relates to <paramref name="mem"/>. The first entry in the
        /// memory's involved roster is the Protagonist; other involved pawns are Participants;
        /// witnesses are Witness; anyone else is None. A legacy or non-event memory has an empty
        /// roster and therefore resolves to None for everyone — the safe default (no one falsely
        /// reads as first-hand).
        /// </summary>
        public static MemoryInvolvement InvolvementOf(WeightedMemory mem, Pawn pawn)
        {
            if (mem == null || pawn == null) return MemoryInvolvement.None;
            string id = pawn.GetUniqueLoadID();

            var involved = mem.involvedPawnIds;
            if (involved != null && involved.Count > 0)
            {
                if (involved[0] == id) return MemoryInvolvement.Protagonist;
                if (involved.Contains(id)) return MemoryInvolvement.Participant;
            }
            if (mem.witnessPawnIds != null && mem.witnessPawnIds.Contains(id))
                return MemoryInvolvement.Witness;

            return MemoryInvolvement.None;
        }

        /// <summary>
        /// True when <paramref name="pawn"/> experienced <paramref name="mem"/> first-hand
        /// (Protagonist or Participant) and so may speak of it in the first person. A memory with no
        /// involvement roster returns false for everyone — the safe default.
        /// </summary>
        public static bool SharesFirsthand(WeightedMemory mem, Pawn pawn)
        {
            var tier = InvolvementOf(mem, pawn);
            return tier == MemoryInvolvement.Protagonist || tier == MemoryInvolvement.Participant;
        }
    }
}
