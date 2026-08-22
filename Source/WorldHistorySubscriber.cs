using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Wires the incident-lifecycle hook (Core #64) into the save-backed world-history store (Core
    /// #65), once per process. The handlers resolve the current world component on each call, so a
    /// single static subscription survives save/load without duplicating (the world component is
    /// recreated per game, the subscription is not). Recording is gated inside the store, so this is
    /// inert under a non-RimSynapse storyteller.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class WorldHistorySubscriber
    {
        static WorldHistorySubscriber()
        {
            SynapseIncidentLifecycle.OnIncidentStarted += OnStarted;
            SynapseIncidentLifecycle.OnIncidentResolved += OnResolved;
        }

        private static void OnStarted(string kind, string region, float magnitude, string origin, int leadTimeTicks)
        {
            var comp = Current.Game != null ? Find.World?.GetComponent<SynapseCoreWorldComponent>() : null;
            comp?.RecordIncidentStart(kind, region, magnitude, origin, Find.TickManager?.TicksGame ?? 0);
        }

        private static void OnResolved(string kind, string region, string outcome)
        {
            var comp = Current.Game != null ? Find.World?.GetComponent<SynapseCoreWorldComponent>() : null;
            comp?.RecordIncidentResolution(kind, region, outcome, Find.TickManager?.TicksGame ?? 0);
        }
    }
}
