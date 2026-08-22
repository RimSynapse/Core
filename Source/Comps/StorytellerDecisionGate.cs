namespace RimSynapse.Comps
{
    /// <summary>
    /// Pure, game-free decision logic for LLM-driven incident selection (Core #67).
    ///
    /// Two questions, both deterministic and side-effect-free so they can be pinned by the
    /// Tier-1 sandbox (Tests/StorytellerDecisionGateTests.cs):
    ///
    ///  - <see cref="ShouldConsult"/>: a beat is due only when the game's own deterministic
    ///    interval count is positive. The LLM never rolls timing — it only picks *what* fires.
    ///  - <see cref="CanBegin"/>: exactly one decision may be in flight at a time. A slow call
    ///    must not overlap the next beat. A flag that was scribed mid-flight and never cleared
    ///    (the process that would have cleared it is gone) goes stale after
    ///    <paramref name="staleTicks"/> so a save/quit can never wedge the storyteller.
    /// </summary>
    public static class StorytellerDecisionGate
    {
        /// <summary>A beat is due this interval iff the deterministic incident count is positive.</summary>
        public static bool ShouldConsult(int incidentCountThisInterval)
            => incidentCountThisInterval > 0;

        /// <summary>
        /// True if a new decision may begin: none in flight, or the in-flight flag is stale
        /// (older than <paramref name="staleTicks"/>, i.e. left over from an interrupted session).
        /// </summary>
        public static bool CanBegin(bool inFlight, int startTick, int now, int staleTicks)
        {
            if (!inFlight) return true;
            return IsStale(startTick, now, staleTicks);
        }

        /// <summary>An in-flight flag is stale once it has outlived the longest plausible async call.</summary>
        public static bool IsStale(int startTick, int now, int staleTicks)
            => (now - startTick) >= staleTicks;
    }
}
