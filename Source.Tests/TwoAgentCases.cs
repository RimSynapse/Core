using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using RimSynapse;
using RimSynapse.Comps;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Covers the two-agent Chat/Storyteller boundary (Core #68): isolation by capability, not
    /// prompt. The player-facing Chat agent holds no consequence tools (fails closed under the chat
    /// scope), a player chat message never triggers a Storyteller turn (that trigger is the cadence
    /// beat alone), and the window is storyteller-gated. The typed sentiment deriver itself is pinned
    /// Tier-1; these are the live wiring assertions.
    /// </summary>
    [SynapseTestSet]
    public static class TwoAgentCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_ChatCannotExecute", () =>
            {
                Assert.True(SynapseToolVocabulary.IsDefined(SynapseToolVocabulary.ChatScope),
                    "the chat scope must be defined (so it is not treated as unscoped=everything)");

                var executorTools = SynapseToolVocabulary.Tools(SynapseToolVocabulary.StorytellerScope).ToList();
                Assert.True(executorTools.Count > 0, "the storyteller scope must be non-empty (sanity)");

                foreach (var tool in executorTools)
                    Assert.False(SynapseToolVocabulary.IsAllowed(SynapseToolVocabulary.ChatScope, tool),
                        $"chat scope must refuse executor tool '{tool}' — isolation is capability, not prompt");

                Assert.False(SynapseToolVocabulary.IsAllowed(SynapseToolVocabulary.ChatScope, "fire_incident"),
                    "chat scope must refuse fire_incident");
                Assert.True(!SynapseToolVocabulary.Tools(SynapseToolVocabulary.ChatScope).Any(),
                    "chat scope must permit zero tools");

                return $"chat scope refuses all {executorTools.Count} executor tool(s); permits zero";
            },
                tier: "Execution", polarity: "negative",
                scenario: "A jailbroken Chat agent tries to run a consequence tool",
                expectation: "Nothing executes — the chat scope holds no tools (capability, not prompt)");

            yield return new SynapseTestCase("Core_StorytellerNotChatTriggered", () =>
            {
                var worldComp = Find.World?.GetComponent<SynapseCoreWorldComponent>();
                Assert.NotNull(worldComp, "no SynapseCoreWorldComponent on the world");

                // A chat submission must not start a Storyteller decision — that path is the cadence
                // beat alone (Core #67/#68). The in-flight guard is claimed synchronously by a real
                // storyteller turn, so if chat triggered one it would be set the instant we return.
                bool inFlightBefore = worldComp.StorytellerDecisionInFlight;
                Assert.False(inFlightBefore, "precondition: no storyteller decision should be pending at case start");

                int logBefore = worldComp.storytellerChatHistory.Count;
                var window = new StorytellerConversationWindow();
                try
                {
                    window.SubmitMessage(worldComp, "please have mercy, is that all you've got?");

                    Assert.False(worldComp.StorytellerDecisionInFlight,
                        "a chat message must NOT start a Storyteller turn — only the cadence beat does");
                    Assert.True(worldComp.storytellerChatHistory.Count >= logBefore + 1,
                        "the player message must be appended to the shared log");
                    Assert.Equal("Player", worldComp.storytellerChatHistory[logBefore].sender,
                        "the appended entry must be the player's message");

                    // The typed sentiment the Storyteller would read from this log carries the flags,
                    // never the raw words.
                    var playerMsgs = worldComp.storytellerChatHistory.Where(m => m.sender == "Player").Select(m => m.message);
                    var sentiment = StorytellerChatSentiment.Derive(playerMsgs);
                    Assert.True(sentiment.RequestedMercy && sentiment.Taunted,
                        "derived sentiment must reflect the player's mercy + taunt signals");
                }
                finally
                {
                    // Keep the live log clean: drop everything this case appended (player line and,
                    // if the mocked reply already landed, the storyteller line).
                    if (worldComp.storytellerChatHistory.Count > logBefore)
                        worldComp.storytellerChatHistory.RemoveRange(logBefore, worldComp.storytellerChatHistory.Count - logBefore);
                }

                return "chat submission appended to the log and derived typed sentiment, without starting a storyteller turn";
            },
                tier: "Execution", polarity: "negative",
                scenario: "The player sends a chat message to the storyteller",
                expectation: "No Storyteller turn fires from it; only a typed sentiment signal is available");

            yield return new SynapseTestCase("Core_ChatWindowGatedByStorytellerDef", () =>
            {
                // The window/toolbar toggle is gated on a live RimSynapse storyteller. Under the
                // quicktest's vanilla storyteller the gate is closed; swapping to a RimSynapse def
                // opens it. Self-skips if no RimSynapse storyteller def is loaded.
                var rimSynapseDef = DefDatabase<StorytellerDef>.AllDefsListForReading
                    .FirstOrDefault(d => d.comps != null && d.comps.Any(c => c is StorytellerCompProperties_Storyteller));
                if (rimSynapseDef == null)
                    return "SKIP: no RimSynapse storyteller def loaded";

                var storyteller = Find.Storyteller;
                var originalDef = storyteller.def;
                bool closedUnderVanilla;
                bool openUnderRimSynapse;
                try
                {
                    // Force a known-vanilla storyteller to observe the closed gate (unless the only
                    // loaded def is a RimSynapse one).
                    var vanillaDef = DefDatabase<StorytellerDef>.AllDefsListForReading
                        .FirstOrDefault(d => d.comps == null || !d.comps.Any(c => c is StorytellerCompProperties_Storyteller));
                    if (vanillaDef != null)
                    {
                        storyteller.def = vanillaDef;
                        storyteller.Notify_DefChanged();
                    }
                    closedUnderVanilla = !SynapseStorytellerContext.IsRimSynapseStorytellerActive;

                    storyteller.def = rimSynapseDef;
                    storyteller.Notify_DefChanged();
                    openUnderRimSynapse = SynapseStorytellerContext.IsRimSynapseStorytellerActive;
                }
                finally
                {
                    storyteller.def = originalDef;
                    storyteller.Notify_DefChanged();
                }

                Assert.True(openUnderRimSynapse, "the window gate must OPEN under a RimSynapse storyteller");
                Assert.True(closedUnderVanilla, "the window gate must be CLOSED under a vanilla storyteller");
                return "window gate closed under vanilla, open under RimSynapse storyteller";
            },
                tier: "Execution", polarity: "positive",
                scenario: "The chat window under vanilla vs RimSynapse storyteller",
                expectation: "Inert under vanilla; available only under a RimSynapse storyteller");
        }
    }
}
