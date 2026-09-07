using Verse;

namespace RimSynapse.Internal
{
    [StaticConstructorOnStartup]
    internal static class Startup
    {
        static Startup()
        {
            RimSynapse.SynapseLogger.InitMainThread();

            // Letter-enhancement whitelist (Core #134) ships EMPTY and dormant: nothing is intercepted,
            // so every vanilla letter fires unchanged. Intercepting letters (even raids) conflicts with
            // mods that own that logic — e.g. World Domination 2.0 — and could delay time-critical
            // alerts, so RimSynapse no longer rewrites/holds letters. World enrichment now comes from an
            // additive, external source (WorldNews-style world-event notifications feeding the
            // storyteller), not by quieting vanilla. The registry stays available for opt-in hooks.
        }
    }
}
