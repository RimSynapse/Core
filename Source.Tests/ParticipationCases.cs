using System.Linq;
using System.Collections.Generic;
using RimSynapse;
using RimAgentic.Testing;
using RimWorld;
using Verse;

namespace RimSynapse.Tests
{
    /// <summary>
    /// The MayConverse participation predicate (Core #122): Core owns the single answer to "who may
    /// take part in colony conversations?" so Conversations (#41) and its outsider passes agree. Tier-2
    /// (needs real pawns): guard branches, the colonist positive case, and the non-humanlike exclusion.
    /// </summary>
    [SynapseTestSet]
    public static class ParticipationCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_MayConverse", () =>
            {
                // Guard branches — no game state needed.
                Assert.False(SynapseCoreProviders.MayConverse(null), "null pawn may not converse");
                Assert.Equal("none", SynapseCoreProviders.ConversationRole(null), "null pawn has no role");

                Map map = Find.CurrentMap ?? Find.Maps.FirstOrDefault();
                Assert.True(map != null, "no map available");

                // A free colonist is a conversation participant, role 'colonist'.
                var colonist = map.mapPawns.FreeColonists.FirstOrDefault();
                Assert.True(colonist != null, "no colonist available");
                Assert.True(SynapseCoreProviders.MayConverse(colonist), "a spawned living colonist may converse");
                Assert.Equal("colonist", SynapseCoreProviders.ConversationRole(colonist), "role is colonist");

                // A non-humanlike (animal) is excluded — MayConverse is humanlike-only.
                var animal = map.mapPawns.AllPawns.FirstOrDefault(p =>
                    p?.RaceProps != null && !p.RaceProps.Humanlike && !p.Dead);
                if (animal != null)
                {
                    Assert.False(SynapseCoreProviders.MayConverse(animal), "an animal may not converse");
                    Assert.Equal("outsider", SynapseCoreProviders.ConversationRole(animal), "a non-participant is an outsider");
                }

                return $"colonist {colonist.LabelShort} converses; animal excluded={(animal != null)}";
            });
        }
    }
}
