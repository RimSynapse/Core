namespace RimSynapse
{
    /// <summary>
    /// The canonical named injection points for <see cref="SynapseCoreContext.GatherGenericContext"/> /
    /// <see cref="SynapseCoreContext.OnInjectGenericContext"/> (Psychology #26). A generation site fires
    /// <c>GatherGenericContext(pawn, contextType)</c> once, just before its LLM call, naming the stage;
    /// subscribers (Conversations, Factions, …) append text to that stage without a hard reference to the
    /// producing mod.
    ///
    /// <para><b>The contract:</b></para>
    /// <list type="bullet">
    /// <item><b>Push, append-only.</b> A handler receives <c>(pawn, contextType, List&lt;string&gt;)</c>
    /// and appends lines; it returns nothing. Many handlers may contribute to one point.</item>
    /// <item><b>Named + isolated.</b> A handler switches on <paramref name="contextType"/> and contributes
    /// only to the point(s) it cares about. Text for one point must never leak into another.</item>
    /// <item><b>No hard reference.</b> Subscribers register against <see cref="SynapseCoreContext"/>
    /// (Core), never against the producing mod's types. They match on these string constants.</item>
    /// <item><b>Zero-subscriber safe.</b> With nobody subscribed, <c>GatherGenericContext</c> returns ""
    /// and generation is byte-for-byte unchanged.</item>
    /// <item><b>Fired once per generation, before the LLM call.</b> The returned block is woven into the
    /// user message, after the site's own facts.</item>
    /// </list>
    ///
    /// <para>Use these constants rather than string literals so the set is discoverable and a typo is a
    /// compile error. A new point is declared here first, then fired by exactly one generation site.
    /// Other Psychology generation sites (visitor/leader backstory — Faction Leader hooks #21 — voice,
    /// therapy, ceremony, mental-break, …) are candidates to join this set as those issues wire them.</para>
    /// </summary>
    public static class SynapseContextTypes
    {
        /// <summary>A colonist's childhood backstory memory is being generated.</summary>
        public const string BackstoryChildhood = "BackstoryChildhood";

        /// <summary>A colonist's adulthood backstory memory is being generated.</summary>
        public const string BackstoryAdulthood = "BackstoryAdulthood";

        /// <summary>A colonist's personality / archetype / voice profile is being synthesized.</summary>
        public const string PersonalityProfile = "PersonalityProfile";

        /// <summary>A relationship memory between two colonists is being evaluated. Fires for the primary pawn.</summary>
        public const string RelationshipEvaluation = "RelationshipEvaluation";

        /// <summary>The nightly clinical psychology review for a colonist is being generated.</summary>
        public const string DailyReview = "DailyReview";

        /// <summary>Every canonical injection point, for discovery and validation.</summary>
        public static readonly string[] All =
        {
            BackstoryChildhood,
            BackstoryAdulthood,
            PersonalityProfile,
            RelationshipEvaluation,
            DailyReview,
        };
    }
}
