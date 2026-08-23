using System;
using System.Collections.Generic;
using RimSynapse;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Covers the regionalizable-incident lifecycle hook (Core #64): both start and first-level
    /// resolution are emitted for a tracked incident, resolution dedups on an oscillating condition,
    /// and the fan-out is safe with zero subscribers. The pure classification/dedup is pinned Tier-1;
    /// these assert the broadcast contract other mods subscribe to (by reflection) at runtime.
    /// </summary>
    [SynapseTestSet]
    public static class IncidentLifecycleCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_IncidentLifecycleHookFires", () =>
            {
                int starts = 0, resolutions = 0;
                string startKind = null, resolveOutcome = null;
                Action<string, string, float, string, int> onStart = (k, r, m, o, l) => { starts++; startKind = k; };
                Action<string, string, string> onResolve = (k, r, o) => { resolutions++; resolveOutcome = o; };

                SynapseIncidentLifecycle.OnIncidentStarted += onStart;
                SynapseIncidentLifecycle.OnIncidentResolved += onResolve;
                SynapseIncidentLifecycle.ResetResolvedForTest();
                try
                {
                    SynapseIncidentLifecycle.BroadcastStarted("ToxicFallout", "BorealForest", 0f, "", 0);
                    SynapseIncidentLifecycle.BroadcastResolved("ToxicFallout", "BorealForest", "ended", "ToxicFallout:1:BorealForest");

                    Assert.True(starts == 1, "start hook must fire once for a tracked incident");
                    Assert.Equal("ToxicFallout", startKind, "start payload must carry the incident kind");
                    Assert.True(resolutions == 1, "resolution hook must fire once");
                    Assert.Equal("ended", resolveOutcome, "resolution payload must carry the outcome");
                }
                finally
                {
                    SynapseIncidentLifecycle.OnIncidentStarted -= onStart;
                    SynapseIncidentLifecycle.OnIncidentResolved -= onResolve;
                    SynapseIncidentLifecycle.ResetResolvedForTest();
                }
                return "start + first-level resolution both emitted with correct payloads";
            },
                tier: "Execution", polarity: "positive",
                scenario: "A regionalizable incident starts and later resolves",
                expectation: "Core broadcasts both a start and a first-level-resolution event");

            yield return new SynapseTestCase("Core_ResolutionEmittedOnce", () =>
            {
                int resolutions = 0;
                Action<string, string, string> onResolve = (k, r, o) => resolutions++;
                SynapseIncidentLifecycle.OnIncidentResolved += onResolve;
                SynapseIncidentLifecycle.ResetResolvedForTest();
                try
                {
                    // Simulate an oscillating condition: End() reached, re-registered, End() again.
                    string key = "SolarFlare:500:BorealForest";
                    bool a = SynapseIncidentLifecycle.BroadcastResolved("SolarFlare", "BorealForest", "ended", key);
                    bool b = SynapseIncidentLifecycle.BroadcastResolved("SolarFlare", "BorealForest", "ended", key);
                    bool c = SynapseIncidentLifecycle.BroadcastResolved("SolarFlare", "BorealForest", "ended", key);

                    Assert.True(a, "first resolution must be accepted");
                    Assert.False(b, "second resolution with the same key must be refused (anti-oscillation)");
                    Assert.False(c, "third resolution with the same key must be refused");
                    Assert.True(resolutions == 1, "subscribers must see exactly one resolution for an oscillating condition");

                    // A genuinely different instance (new startTick) is a new resolution.
                    bool d = SynapseIncidentLifecycle.BroadcastResolved("SolarFlare", "BorealForest", "ended", "SolarFlare:900:BorealForest");
                    Assert.True(d && resolutions == 2, "a distinct incident instance resolves independently");
                }
                finally
                {
                    SynapseIncidentLifecycle.OnIncidentResolved -= onResolve;
                    SynapseIncidentLifecycle.ResetResolvedForTest();
                }
                return "duplicate resolution keys suppressed; distinct instances resolve independently";
            },
                tier: "Execution", polarity: "negative",
                scenario: "A condition oscillates (ends, re-registers, ends again)",
                expectation: "Only one resolution event is emitted per incident instance");

            yield return new SynapseTestCase("Core_LifecycleSafeWithNoSubscribers", () =>
            {
                SynapseIncidentLifecycle.ResetResolvedForTest();
                // No subscribers attached: broadcasting must not throw and resolution still dedups.
                SynapseIncidentLifecycle.BroadcastStarted("Eclipse", "x", 0f, "", 0);
                bool first = SynapseIncidentLifecycle.BroadcastResolved("Eclipse", "x", "ended", "Eclipse:1:x");
                bool dup = SynapseIncidentLifecycle.BroadcastResolved("Eclipse", "x", "ended", "Eclipse:1:x");
                Assert.True(first, "resolution is accepted even with no subscribers");
                Assert.False(dup, "dedup still applies with no subscribers");
                return "no-subscriber broadcast is safe and still dedups";
            },
                tier: "Execution", polarity: "positive",
                scenario: "An incident fires with no lifecycle subscribers registered",
                expectation: "No exception; the bounded dedup still holds");
        }
    }
}
