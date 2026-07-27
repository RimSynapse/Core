using System;
using System.Collections.Generic;

namespace RimSynapse
{
    /// <summary>
    /// Keeps an agent conversation inside its token budget across turns.
    ///
    /// RunAgentLoop resends the full message list every turn, and turn feedback embeds
    /// script logs and tool payloads — so on a small window the history overflows within a
    /// few turns. Compaction preserves what still matters: the system prompt, the original
    /// command, and the latest exchange stay verbatim; older turns collapse to one-line
    /// summaries, oldest first, until the estimate fits.
    /// </summary>
    public static class SynapseAgentHistory
    {
        public const string CompactedMarker = "[compacted]";

        /// <summary>Per-message structural overhead on top of chars/4 content estimation.</summary>
        private const int PerMessageOverheadTokens = 4;

        public static int EstimateTokens(List<ChatMessage> messages)
        {
            if (messages == null) return 0;
            int total = 0;
            foreach (var m in messages)
            {
                total += PerMessageOverheadTokens + (m?.content?.Length ?? 0) / 4;
            }
            return total;
        }

        /// <summary>
        /// Compact older turns until the history fits the budget. Indices 0 (system prompt)
        /// and 1 (original command) and the final two messages (latest exchange) are never
        /// touched. Returns the post-compaction estimate; logs what was collapsed.
        /// </summary>
        public static int CompactToBudget(List<ChatMessage> messages, int budgetTokens, Action<string> log)
        {
            int estimate = EstimateTokens(messages);
            if (messages == null || estimate <= budgetTokens) return estimate;

            int before = estimate;
            int compacted = 0;

            // Middle region: everything between the preserved head and the latest exchange.
            int first = 2;
            int lastExclusive = Math.Max(first, messages.Count - 2);

            for (int i = first; i < lastExclusive && estimate > budgetTokens; i++)
            {
                var msg = messages[i];
                if (msg?.content == null) continue;
                if (msg.content.StartsWith(CompactedMarker, StringComparison.Ordinal)) continue;

                // Mutate content in place: replacing the object would drop any fields
                // beyond role/content that a message might carry.
                msg.content = Summarize(msg.role, msg.content);
                compacted++;
                estimate = EstimateTokens(messages);
            }

            if (compacted > 0)
            {
                log?.Invoke($"[Agent] Compacted {compacted} older message(s): ~{before} -> ~{estimate} tokens (budget {budgetTokens}).");
            }

            if (estimate > budgetTokens)
            {
                // Nothing left to collapse — the head and latest exchange alone exceed the
                // budget. Say so rather than silently overflowing; shrinking the system
                // prompt's tool section is the search-index issue's job.
                log?.Invoke($"[Agent] History still ~{estimate} tokens after compaction (budget {budgetTokens}); the preserved head and latest exchange do not fit.");
            }

            return estimate;
        }

        private static string Summarize(string role, string content)
        {
            string firstLine = content;
            int nl = content.IndexOf('\n');
            if (nl > 0) firstLine = content.Substring(0, nl);
            if (firstLine.Length > 120) firstLine = firstLine.Substring(0, 120);

            return $"{CompactedMarker} {role}: {firstLine} ... ({content.Length} chars elided)";
        }
    }
}
