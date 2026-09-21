using System.Collections.Generic;
using System.Reflection;
using RimSynapse;
using RimAgentic.Testing;
using Verse;

namespace RimSynapse.Tests
{
    /// <summary>
    /// The world-event publish surface + inbox (Core #135): the additive, outbound model where
    /// external sources push world events and the storyteller consumes the pending set as context and
    /// weight. Deterministic seams — publish/consume, expiry, source-gating, the primitives-only
    /// reflection path, and the weight boost. Uses the explicit-clock overload so no game tick is
    /// needed; clears the shared inbox on entry so cases are order-independent.
    /// </summary>
    [SynapseTestSet]
    public static class WorldEventInboxCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_WorldEventPublishAndConsume", () =>
            {
                SynapseWorldEventInbox.Clear();
                SynapseWorldEventInbox.Publish("plague", "the northern reaches", 3f, "caravans", "a plague spreads", 1000, 0);
                var pending = SynapseWorldEventInbox.Pending(0);
                Assert.Equal(1, pending.Count, "the published event is pending");
                Assert.Equal("plague", pending[0].kind, "kind round-trips");
                var note = SynapseWorldEventInbox.SelectionContextNote(0);
                Assert.Contains(note, "plague", "the selection note names the event");
                Assert.Contains(note, "additive", "the note frames the model as additive, never suppressing vanilla");
                SynapseWorldEventInbox.Clear();
                return "publish + consume";
            });

            yield return new SynapseTestCase("Core_WorldEventExpires", () =>
            {
                SynapseWorldEventInbox.Clear();
                SynapseWorldEventInbox.Publish("raid", "east", 1f, "", "raiders muster", 500, 0);
                Assert.Equal(1, SynapseWorldEventInbox.Pending(499).Count, "still live just before expiry");
                Assert.Equal(0, SynapseWorldEventInbox.Pending(500).Count, "pruned at expiry");
                return "expiry prunes";
            });

            // Source-gated: with nothing published the inbox is empty, the note is blank, and the
            // weight boost is neutral — the storyteller behaves exactly as vanilla.
            yield return new SynapseTestCase("Core_WorldEventSourceGated", () =>
            {
                SynapseWorldEventInbox.Clear();
                Assert.Equal(0, SynapseWorldEventInbox.Pending(0).Count, "no source → empty inbox");
                Assert.Equal("", SynapseWorldEventInbox.SelectionContextNote(0), "no source → no context note");
                Assert.Equal(1f, SynapseWorldEventInbox.WeightBoostFor("RaidEnemy", 0), "no source → neutral weight");
                return "gated to vanilla without a source";
            });

            // A qualifying event boosts the matching incident's weight; an unmatched incident is untouched.
            yield return new SynapseTestCase("Core_WorldEventBoostsMatchingIncident", () =>
            {
                SynapseWorldEventInbox.Clear();
                SynapseWorldEventInbox.Publish("Flu", "north", 2f, "", "a flu spreads", 1000, 0);
                Assert.True(SynapseWorldEventInbox.WeightBoostFor("Disease_Flu", 0) > 1f,
                    "an incident whose name contains the event kind is boosted");
                Assert.Equal(1f, SynapseWorldEventInbox.WeightBoostFor("Eclipse", 0),
                    "an unrelated incident is not boosted");
                SynapseWorldEventInbox.Clear();
                return "matching incident boosted";
            });

            // The publish surface is reachable purely by reflection with no Core type in the signature —
            // the contract that lets a producer build with Core absent.
            yield return new SynapseTestCase("Core_WorldEventReflectionRegisterable", () =>
            {
                SynapseWorldEventInbox.Clear();
                var t = GenTypes.GetTypeInAnyAssembly("RimSynapse.SynapseCoreContext");
                Assert.True(t != null, "SynapseCoreContext resolves by name");
                var m = t.GetMethod("PublishWorldEvent", BindingFlags.Public | BindingFlags.Static);
                Assert.True(m != null, "PublishWorldEvent is a public static method");
                var ps = m.GetParameters();
                Assert.Equal(6, ps.Length, "signature is (kind, region, magnitude, origin, summary, ttlTicks)");
                foreach (var p in ps)
                    Assert.True(p.ParameterType.IsPrimitive || p.ParameterType == typeof(string),
                        $"parameter '{p.Name}' must be a primitive/string so producers need no Core type (was {p.ParameterType.Name})");
                m.Invoke(null, new object[] { "SolarFlare", "orbit", 1f, "", "a flare looms", 1000 });
                Assert.Equal(1, SynapseWorldEventInbox.Pending(0).Count, "the reflected publish landed in the inbox");
                SynapseWorldEventInbox.Clear();
                return "reflection-registerable, no Core type";
            });
        }
    }
}
