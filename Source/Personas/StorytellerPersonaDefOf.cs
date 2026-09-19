using RimWorld;

namespace RimSynapse.Personas
{
    /// <summary>Named handle for the shipped reference persona. Third-party personas are resolved by
    /// name from the storyteller comp, not through this DefOf.</summary>
    [DefOf]
    public static class StorytellerPersonaDefOf
    {
        /// <summary>The default RimSynapse storyteller voice (Core #89).</summary>
        public static StorytellerPersonaDef Aura;

        static StorytellerPersonaDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(StorytellerPersonaDefOf));
        }
    }
}
