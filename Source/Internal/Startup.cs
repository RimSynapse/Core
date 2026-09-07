using RimWorld;
using Verse;

namespace RimSynapse.Internal
{
    [StaticConstructorOnStartup]
    internal static class Startup
    {
        static Startup()
        {
            RimSynapse.SynapseLogger.InitMainThread();

            // Seed the letter-enhancement whitelist (Core #134). Only whitelisted letters are held for
            // LLM rewrite / storyteller TTS; everything else is vanilla, so time-critical alerts (e.g. a
            // crash-pod rescue) arrive on time. Raids are the one hook we ship enabled; other event types
            // opt in by registering their own hook.
            RimSynapse.SynapseLetterEnhancement.RegisterLetterDef(LetterDefOf.ThreatBig.defName);
        }
    }
}
