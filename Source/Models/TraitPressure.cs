using Verse;

namespace RimSynapse.Models
{
    /// <summary>
    /// Accumulated multi-day evidence toward (or away from) a single personality trait change.
    /// Owned by <see cref="RimSynapse.Comps.SynapseCorePawnComp"/> (Core owns the persisted data);
    /// the folding/threshold logic that reads it lives in Psychology. Decays toward 0 when no fresh
    /// evidence arrives, so one bad day does not linger forever.
    /// </summary>
    public class TraitPressure : IExposable
    {
        /// <summary>Current accumulated pressure (0+). Fires a change when it crosses the shift threshold.</summary>
        public float pressure;
        /// <summary>"add" | "remove" — the direction the evidence points.</summary>
        public string direction;
        /// <summary>Absolute tick this entry last received evidence; drives decay-by-elapsed-days.</summary>
        public long lastUpdatedTick;
        /// <summary>Highest pressure ever reached (for narrative/trajectory display).</summary>
        public float peak;

        /// <summary>
        /// The trait axis this pressure belongs to (a <see cref="Verse.TraitDef"/> defName), independent of
        /// the dictionary key. For a spectrum axis the key is <c>"{axisId}#{targetDegree}"</c> so opposite
        /// directions accumulate separately; <see cref="targetDegree"/> is the degree the pawn would move to.
        /// Additive fields — old saves load the defaults and are rewritten once by the key migration.
        /// </summary>
        public string axisId;
        /// <summary>The degree the pawn would move to on this axis if the shift fires (0 = neutral/absent).</summary>
        public int targetDegree;

        public void ExposeData()
        {
            Scribe_Values.Look(ref pressure, "pressure", 0f);
            Scribe_Values.Look(ref direction, "direction");
            Scribe_Values.Look(ref lastUpdatedTick, "lastUpdatedTick", 0L);
            Scribe_Values.Look(ref peak, "peak", 0f);
            Scribe_Values.Look(ref axisId, "axisId");
            Scribe_Values.Look(ref targetDegree, "targetDegree", 0);
        }
    }
}
