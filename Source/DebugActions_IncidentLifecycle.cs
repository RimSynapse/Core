using System;
using System.Text;
using LudeonTK;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for the regionalizable-incident lifecycle hook (Core #64), grouped under
    /// "RimSynapse". Subscribes a probe to both hooks, fires a start and a resolution (plus a
    /// duplicate resolution to prove dedup), and reports what the probe received — the same events a
    /// real consumer (Storyteller / WorldNews / Regions) subscribes to by reflection. Headlessly
    /// runnable via run_debug_action.
    /// </summary>
    public static class DebugActions_IncidentLifecycle
    {
        [DebugAction("RimSynapse", "Incident lifecycle: fire start + resolution probe",
            allowedGameStates = AllowedGameStates.Playing)]
        private static void FireLifecycleProbe()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[RimSynapse] Regionalizable-incident lifecycle (Core #64):");

            int starts = 0, resolutions = 0;
            string startSummary = null, resolveSummary = null;
            Action<string, string, float, string, int> onStart =
                (kind, region, mag, origin, lead) => { starts++; startSummary = $"{kind} @ {region} mag={mag:F0} origin='{origin}' lead={lead}"; };
            Action<string, string, string> onResolve =
                (kind, region, outcome) => { resolutions++; resolveSummary = $"{kind} @ {region} -> {outcome}"; };

            SynapseIncidentLifecycle.OnIncidentStarted += onStart;
            SynapseIncidentLifecycle.OnIncidentResolved += onResolve;
            try
            {
                sb.AppendLine($"  classification: SolarFlare regionalizable={SynapseIncidentLifecycle.IsRegionalizable("SolarFlare", null)}, " +
                              $"RaidEnemy regionalizable={SynapseIncidentLifecycle.IsRegionalizable("RaidEnemy", "ThreatBig")}");

                SynapseIncidentLifecycle.BroadcastStarted("SolarFlare", "TestRegion", 350f, "", 0);

                SynapseIncidentLifecycle.ResetResolvedForTest();
                bool r1 = SynapseIncidentLifecycle.BroadcastResolved("SolarFlare", "TestRegion", "ended", "probe-key");
                bool r2 = SynapseIncidentLifecycle.BroadcastResolved("SolarFlare", "TestRegion", "ended", "probe-key");

                sb.AppendLine($"  start fired: {starts} ({startSummary})");
                sb.AppendLine($"  resolution: r1={r1}, r2(dup)={r2}; subscriber saw {resolutions} resolution(s) ({resolveSummary})");
                sb.AppendLine($"  dedup works: {(r1 && !r2 && resolutions == 1 ? "YES" : "NO (bug)")}");
                sb.AppendLine($"  last subscriber error: {SynapseIncidentLifecycle.LastSubscriberError ?? "(none)"}");
            }
            finally
            {
                SynapseIncidentLifecycle.OnIncidentStarted -= onStart;
                SynapseIncidentLifecycle.OnIncidentResolved -= onResolve;
                SynapseIncidentLifecycle.ResetResolvedForTest();
            }

            SynapseLogger.Message(sb.ToString().TrimEnd());
        }
    }
}
