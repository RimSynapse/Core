using System.Collections.Generic;
using Verse;

namespace RimSynapse.Models
{
    public class WeightedMemory : IExposable
    {
        public string summary;
        public string memoryType;        // raid, social, event, trade, quest, backstory, etc.
        public List<string> tags = new List<string>();
        public List<string> subjectPawnIds = new List<string>();
        public bool isLongTerm = false;

        // ── Involvement roster (Core #103) ──
        // Who actually experienced the underlying event, so a consumer can tell whether a given pawn
        // legitimately shares this memory first-hand or is a stranger to it. Canonical LoadIds (Core
        // #80 convention, same as subjectPawnIds). Both empty for legacy/non-event memories, which
        // SynapseCoreMemory.InvolvementOf resolves to None — the safe default (a non-owner never reads
        // as first-hand). The FIRST involved id is treated as the protagonist.
        /// <summary>Pawns who experienced this memory first-hand (protagonist + co-participants).</summary>
        public List<string> involvedPawnIds = new List<string>();
        /// <summary>Pawns who witnessed the event but did not take part in it.</summary>
        public List<string> witnessPawnIds = new List<string>();

        /// <summary>Source <see cref="PastEvent.eventId"/> when this memory was derived from a recorded
        /// event (Core #103/#129). Lets memories tracing to one event — witness and participant alike —
        /// coalesce into a single record, and ties the involvement roster back to its origin. Null for
        /// memories not derived from an event.</summary>
        public string sourceEventId = null;

        /// <summary>How many times this memory has been reinforced by a coalesced duplicate (Core #129).
        /// 1 for a fresh memory; higher when repeated near-identical observations collapsed into it.</summary>
        public int occurrenceCount = 1;
        
        /// <summary>Absolute tick when this memory occurred. Used for date display and chronological sorting.</summary>
        public long absTick;
        
        /// <summary>DEPRECATED — kept for save compatibility. New code should use absTick.</summary>
        public int gameTick;
        
        public float weight = 1.0f;             // 0.0 to 1.0
        public float baseWeight = 1.0f;
        public float decayRate = 0.05f;          // default 0.05
        public int timesReferenced;

        // ── Stage 1 (0.7.1) weight-lifecycle fields — all Scribe-defaulted & back-compat ──
        /// <summary>Absolute tick this memory was last bumped or surfaced into context; drives recency.</summary>
        public long lastReferencedTick = 0;
        /// <summary>Cached relational salience, recomputed in the daily maintenance pass (see comp).</summary>
        public float salience = 0f;
        /// <summary>For combat-derived memories: "humanlike" | "animal" | "object" | "self". Null otherwise.</summary>
        public string targetKind = null;
        /// <summary>Explicit graph edges. Implicit edges via shared tags/subjectPawnIds remain primary.</summary>
        public List<string> linkedMemoryIds = new List<string>();
        /// <summary>Stable id so links survive save/load. Assigned on AddMemory / back-filled on load.</summary>
        public string memId = null;

        public WeightedMemory()
        {
        }

        /// <summary>
        /// Deterministically derive a stable id from summary + absTick so back-fill on load produces
        /// the same id every time (no per-process hash randomisation reliance).
        /// </summary>
        public void EnsureMemId()
        {
            if (!string.IsNullOrEmpty(memId)) return;
            int h = 17;
            if (summary != null)
            {
                foreach (char c in summary) h = unchecked(h * 31 + c);
            }
            memId = absTick.ToString() + "_" + (h & 0x7fffffff).ToString("x");
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref summary, "summary");
            Scribe_Values.Look(ref memoryType, "memoryType");
            Scribe_Collections.Look(ref tags, "tags", LookMode.Value);
            Scribe_Values.Look(ref gameTick, "gameTick");
            Scribe_Values.Look(ref absTick, "absTick", 0L);
            Scribe_Values.Look(ref weight, "weight", 1.0f);
            Scribe_Values.Look(ref baseWeight, "baseWeight", 1.0f);
            Scribe_Values.Look(ref decayRate, "decayRate", 0.05f);
            Scribe_Values.Look(ref timesReferenced, "timesReferenced", 0);
            Scribe_Values.Look(ref isLongTerm, "isLongTerm", false);
            Scribe_Collections.Look(ref subjectPawnIds, "subjectPawnIds", LookMode.Value);
            Scribe_Collections.Look(ref involvedPawnIds, "involvedPawnIds", LookMode.Value);
            Scribe_Collections.Look(ref witnessPawnIds, "witnessPawnIds", LookMode.Value);
            Scribe_Values.Look(ref sourceEventId, "sourceEventId", null);
            Scribe_Values.Look(ref occurrenceCount, "occurrenceCount", 1);

            // Stage 1 additive fields — absent in old saves ⇒ Scribe default, then initialised in the
            // comp's PostLoadInit migration (memId, lastReferencedTick) and the daily pass (salience).
            Scribe_Values.Look(ref lastReferencedTick, "lastReferencedTick", 0L);
            Scribe_Values.Look(ref salience, "salience", 0f);
            Scribe_Values.Look(ref targetKind, "targetKind", null);
            Scribe_Collections.Look(ref linkedMemoryIds, "linkedMemoryIds", LookMode.Value);
            Scribe_Values.Look(ref memId, "memId", null);

            // Ensure lists are initialized after loading
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (tags == null) tags = new List<string>();
                if (subjectPawnIds == null) subjectPawnIds = new List<string>();
                if (linkedMemoryIds == null) linkedMemoryIds = new List<string>();
                if (involvedPawnIds == null) involvedPawnIds = new List<string>();
                if (witnessPawnIds == null) witnessPawnIds = new List<string>();
            }
        }

        /// <summary>
        /// Called after all game data is loaded. Migrates old gameTick-only memories
        /// to use absTick by applying the adjustment offset.
        /// Should be called from the owning comp's PostLoadInit or equivalent.
        /// </summary>
        public void MigrateTickIfNeeded()
        {
            if (absTick == 0L && gameTick != 0)
            {
                // Old save: absTick was never written. Convert gameTick to absolute.
                absTick = Utils.SynapseDateHelper.GameTickToAbsTick(gameTick);
            }
        }
    }
}

