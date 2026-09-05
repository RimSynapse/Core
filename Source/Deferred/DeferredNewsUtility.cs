using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimSynapse
{
    /// <summary>Where a letter's news happened, for the deferred-news pipeline (Core#123).</summary>
    public enum NewsScope
    {
        /// <summary>On (or about) the player's own maps — known instantly, never deferred.</summary>
        Local,
        /// <summary>Off-world: world objects, distant tiles, faction-scoped events. Word travels.</summary>
        World,
        /// <summary>Quest-carrying letters — word from afar by definition.</summary>
        Quest,
    }

    /// <summary>
    /// The locality probe behind the deferral gate (Core#123): the "word travels slowly on the rim"
    /// fiction only applies to news from elsewhere, so only world-scoped letters are deferrable.
    /// Public because consumers tag their own records with it — WorldNews stamps recorded news lines
    /// WORLD/QUEST/COLONY from this same classification, so the gate and the newspaper's provenance
    /// can never disagree (RimSynapse/WorldNews#37).
    /// </summary>
    public static class DeferredNewsUtility
    {
        /// <summary>
        /// Classify a letter's scope. Rules, in order: a quest letter is <see cref="NewsScope.Quest"/>;
        /// any valid lookTarget on a live map is <see cref="NewsScope.Local"/> (a map only exists where
        /// the player's pawns are, so anything on one is witnessed, not reported); remaining valid
        /// targets (world objects, bare tiles) make it <see cref="NewsScope.World"/>; and a letter with
        /// no targets at all defaults to Local — most target-less letters are colony- or UI-scoped, and
        /// a wrong Local is a mild fiction break while a wrong World hides urgent information.
        /// </summary>
        public static NewsScope ClassifyScope(Letter let)
        {
            if (let == null) return NewsScope.Local;
            if (let is ChoiceLetter cl && cl.quest != null) return NewsScope.Quest;

            LookTargets lt = let.lookTargets;
            if (lt == null || lt.targets == null || lt.targets.Count == 0) return NewsScope.Local;

            bool sawOffWorld = false;
            for (int i = 0; i < lt.targets.Count; i++)
            {
                GlobalTargetInfo t = lt.targets[i];
                if (!t.IsValid) continue;

                Map map = t.Map ?? t.Thing?.MapHeld;
                if (map != null) return NewsScope.Local;

                if (t.HasWorldObject || t.Tile >= 0) sawOffWorld = true;
            }
            return sawOffWorld ? NewsScope.World : NewsScope.Local;
        }
    }
}
