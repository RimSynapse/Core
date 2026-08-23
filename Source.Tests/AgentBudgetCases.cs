using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Covers the agent's token budget: history compaction that preserves the head and the
    /// latest exchange, and the excerpt-plus-handle scheme for oversized tool results.
    /// </summary>
    [SynapseTestSet]
    public static class AgentBudgetCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_ResultStoreRoundtrip", () =>
            {
                SynapseResultStore.Clear();

                string small = "short result";
                Assert.Equal(small, SynapseResultStore.AbbreviateIfLarge(small),
                    "small content must pass through untouched");

                string big = new string('x', 5000) + "END";
                string abbreviated = SynapseResultStore.AbbreviateIfLarge(big);
                Assert.True(abbreviated.Length < big.Length, "large content must shrink");
                var m = Regex.Match(abbreviated, @"res_\d+");
                Assert.True(m.Success, "the excerpt must carry a res_N handle");

                string fetched = SynapseToolRegistry.ExecuteTool(
                    "get_stored_result", $"{{\"key\": \"{m.Value}\"}}");
                Assert.Equal(big, fetched, "get_stored_result must return the full payload");

                return $"5003 chars -> {abbreviated.Length}-char excerpt, retrievable via {m.Value}";
            });

            yield return new SynapseTestCase("Core_ResultStoreUnknownKey", () =>
            {
                string result = SynapseToolRegistry.ExecuteTool(
                    "get_stored_result", "{\"key\": \"res_999999\"}");
                Assert.Contains(result, "error", "an unknown key must return an error payload");
                return "unknown key answered with a structured error";
            });

            yield return new SynapseTestCase("Core_CompactionRespectsBudget", () =>
            {
                // Budget chosen so the preserved head + latest exchange fit: compaction's
                // contract is to make the *history* fit, while individual message size is
                // bounded upstream by the abbreviation layer.
                var messages = BuildHistory(turnPairs: 5, contentChars: 4000);
                int before = SynapseAgentHistory.EstimateTokens(messages);

                var log = new List<string>();
                int after = SynapseAgentHistory.CompactToBudget(messages, 3000, log.Add);

                Assert.True(after <= 3000,
                    $"estimate must land under budget, got {after} (was {before})");
                Assert.True(log.Any(l => l.Contains("Compacted")),
                    "compaction must announce what it collapsed");
                Assert.True(messages.Skip(2).Take(messages.Count - 4)
                        .Any(msg => msg.content.StartsWith(SynapseAgentHistory.CompactedMarker)),
                    "middle messages should carry the compacted marker");

                return $"~{before} -> ~{after} tokens under a 3000 budget";
            });

            yield return new SynapseTestCase("Core_CompactionWarnsWhenImpossible", () =>
            {
                // When even the preserved head and latest exchange exceed the budget,
                // compaction must say so — silent overflow would fail at the API instead.
                var messages = BuildHistory(turnPairs: 5, contentChars: 4000);
                var log = new List<string>();
                int after = SynapseAgentHistory.CompactToBudget(messages, 500, log.Add);

                Assert.True(after > 500, "a 500-token budget is not achievable for this history");
                Assert.True(log.Any(l => l.Contains("do not fit")),
                    "the impossible budget must be reported, got: " +
                    string.Join(" | ", log.Take(3)));

                return "unmeetable budget reported instead of silently overflowing";
            });

            yield return new SynapseTestCase("Core_CompactionPreservesHeadAndLatest", () =>
            {
                var messages = BuildHistory(turnPairs: 5, contentChars: 4000);
                string system = messages[0].content;
                string command = messages[1].content;
                string lastAssistant = messages[messages.Count - 2].content;
                string lastUser = messages[messages.Count - 1].content;

                SynapseAgentHistory.CompactToBudget(messages, 1500, null);

                Assert.Equal(system, messages[0].content, "system prompt must stay verbatim");
                Assert.Equal(command, messages[1].content, "original command must stay verbatim");
                Assert.Equal(lastAssistant, messages[messages.Count - 2].content,
                    "latest assistant message must stay verbatim");
                Assert.Equal(lastUser, messages[messages.Count - 1].content,
                    "latest user message must stay verbatim");

                return "head and latest exchange untouched";
            });

            yield return new SynapseTestCase("Core_FiveTurn2kSimulation", () =>
            {
                // Simulate the cap a 2048-window model yields: 2048 - max(512, 25%) = 1536.
                const int cap = 1536;
                var messages = new List<ChatMessage>
                {
                    NewMessage("system", new string('s', 1200)),
                    NewMessage("user", "User Instruction: do the thing")
                };

                for (int turn = 1; turn <= 5; turn++)
                {
                    messages.Add(NewMessage("assistant", Filler("plan for turn " + turn, 2000)));
                    messages.Add(NewMessage("user", Filler("Execution outcomes of turn " + turn, 2000)));

                    int estimate = SynapseAgentHistory.CompactToBudget(messages, cap, null);
                    Assert.True(estimate <= cap,
                        $"turn {turn}: history must fit a 2k window, got ~{estimate} tokens");
                }

                Assert.Equal("User Instruction: do the thing", messages[1].content,
                    "the original command must survive five turns of compaction");
                return "five 2k-window turns, always under the cap";
            });
        }

        private static List<ChatMessage> BuildHistory(int turnPairs, int contentChars)
        {
            var messages = new List<ChatMessage>
            {
                NewMessage("system", Filler("system prompt", 1600)),
                NewMessage("user", "User Instruction: test command")
            };
            for (int i = 0; i < turnPairs; i++)
            {
                messages.Add(NewMessage("assistant", Filler("assistant turn " + i, contentChars)));
                messages.Add(NewMessage("user", Filler("outcomes " + i, contentChars)));
            }
            return messages;
        }

        private static ChatMessage NewMessage(string role, string content)
        {
            return new ChatMessage { role = role, content = content };
        }

        private static string Filler(string firstLine, int totalChars)
        {
            var sb = new System.Text.StringBuilder(firstLine);
            sb.Append('\n');
            while (sb.Length < totalChars) sb.Append("filler content line for testing budgets\n");
            return sb.ToString();
        }
    }
}
