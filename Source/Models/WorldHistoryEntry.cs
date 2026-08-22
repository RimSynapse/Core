using Verse;

namespace RimSynapse
{
    /// <summary>
    /// One record in the save-backed world-history store (Core #65): a regional incident, its
    /// magnitude and origin, and — once it resolves — its outcome. This is the canonical history the
    /// Storyteller reasons over and calls back to; WorldNews's ephemeral feed is a view over it.
    ///
    /// Populated from the incident-lifecycle hook (Core #64): a start creates an open entry, a
    /// first-level resolution closes it. An entry that never resolves stays an <b>open thread</b>.
    /// </summary>
    public class WorldHistoryEntry : IExposable
    {
        public string kind;        // incident def name, e.g. "SolarFlare"
        public string region;      // coarse region label (biome or tile)
        public float magnitude;    // threat points / severity at start
        public string origin;      // faction name or ""
        public string outcome;     // set at resolution; null while open
        public int startTick;
        public int resolvedTick;   // -1 while open
        public bool resolved;

        public WorldHistoryEntry() { }

        public WorldHistoryEntry(string kind, string region, float magnitude, string origin, int startTick)
        {
            this.kind = kind;
            this.region = region;
            this.magnitude = magnitude;
            this.origin = origin;
            this.startTick = startTick;
            this.resolvedTick = -1;
            this.resolved = false;
        }

        /// <summary>The tick used for age/eviction ordering: resolution tick if closed, else start.</summary>
        public int LastTick => resolved ? resolvedTick : startTick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref kind, "kind");
            Scribe_Values.Look(ref region, "region");
            Scribe_Values.Look(ref magnitude, "magnitude", 0f);
            Scribe_Values.Look(ref origin, "origin");
            Scribe_Values.Look(ref outcome, "outcome");
            Scribe_Values.Look(ref startTick, "startTick", 0);
            Scribe_Values.Look(ref resolvedTick, "resolvedTick", -1);
            Scribe_Values.Look(ref resolved, "resolved", false);
        }
    }
}
