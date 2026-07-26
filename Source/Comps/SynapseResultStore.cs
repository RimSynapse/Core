using System;
using System.Collections.Generic;

namespace RimSynapse
{
    /// <summary>
    /// Short-lived store for large tool results, so the model receives an excerpt plus a
    /// retrievable handle instead of the full payload.
    ///
    /// Feeding a multi-kilobyte result straight into the conversation spends prompt budget
    /// on every subsequent turn, which a 2k-window model cannot afford. Storing it and
    /// handing back "res_N" costs a line; the model calls get_stored_result only when it
    /// actually needs the detail — the same progressive-disclosure idea as two-stage tool
    /// discovery.
    ///
    /// Session-scoped and capped: this is working memory for an agent run, not persistence.
    /// </summary>
    public static class SynapseResultStore
    {
        public const int MaxEntries = 50;
        public const int DefaultExcerptChars = 600;

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, string> _store = new Dictionary<string, string>();
        private static readonly Queue<string> _order = new Queue<string>();
        private static int _next;

        /// <summary>Store a payload and return its key ("res_N"). Oldest entries evict past the cap.</summary>
        public static string Store(string content)
        {
            lock (_lock)
            {
                string key = "res_" + (++_next);
                _store[key] = content ?? string.Empty;
                _order.Enqueue(key);
                while (_order.Count > MaxEntries)
                {
                    _store.Remove(_order.Dequeue());
                }
                return key;
            }
        }

        public static bool TryGet(string key, out string content)
        {
            lock (_lock)
            {
                return _store.TryGetValue(key ?? string.Empty, out content);
            }
        }

        /// <summary>
        /// Pass small content through untouched; store large content and return an excerpt
        /// whose trailer tells the model how to retrieve the rest.
        /// </summary>
        public static string AbbreviateIfLarge(string content, int maxChars = DefaultExcerptChars)
        {
            if (string.IsNullOrEmpty(content) || content.Length <= maxChars) return content;

            string key = Store(content);
            return content.Substring(0, maxChars)
                + $"\n... [truncated — {content.Length} chars total; full result stored as '{key}'. Call get_stored_result with that key if you need the rest.]";
        }

        /// <summary>Test hook.</summary>
        public static void Clear()
        {
            lock (_lock)
            {
                _store.Clear();
                _order.Clear();
            }
        }
    }
}
