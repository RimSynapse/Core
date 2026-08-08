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

