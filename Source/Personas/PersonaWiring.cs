using Verse;

namespace RimSynapse.Personas
{
    /// <summary>
    /// Subscribes the persona engine (Core #69) to the incident lifecycle hook (Core #64): a kickoff
    /// line when an incident starts, a positive/negative resolution line when it first resolves. The
    /// lifecycle events are static and the engine self-gates on dormancy, so we subscribe exactly once
    /// per process here. Callbacks (world-history payoffs) and idle lines fire on their own cadence, not
    /// off these events.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class PersonaWiring
    {
        static PersonaWiring()
        {
            SynapseIncidentLifecycle.OnIncidentStarted += OnStarted;
            SynapseIncidentLifecycle.OnIncidentResolved += OnResolved;
        }

        private static void OnStarted(string kind, string region, float magnitude, string origin, int leadTimeTicks)
            => SynapsePersonaEngine.SpeakKickoff(kind, region, magnitude, origin);

        private static void OnResolved(string kind, string region, string outcome)
            => SynapsePersonaEngine.SpeakResolution(kind, region, outcome);
    }
}
