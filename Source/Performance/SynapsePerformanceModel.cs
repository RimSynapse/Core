using System;
using System.Collections.Generic;

namespace RimSynapse
{
    /// <summary>
    /// Rolling latency history per work class, and the curve estimate derived from it.
    ///
    /// The queue has always measured each request (QueuedRequest.LlmLatencyMs) but kept
    /// nothing beyond a five-second display history, so there was no data to make sizing
    /// decisions with. This retains the last few samples per event type and fits a simple
    /// line to (promptTokens, latencyMs):
    ///
    ///     latency ≈ floor + msPerToken × promptTokens
    ///
    /// The floor (intercept) is the latency you cannot remove — network plus model
    /// overhead. The slope classifies the regime: steep means prefill-bound local
    /// inference where prompt size governs latency; near-zero means a remote backend
    /// where size is almost free time-wise and cost or the window governs instead.
    ///
    /// Thread-safe: recording happens on queue worker threads, reads from the main thread.
    /// </summary>
    public static class SynapsePerformanceModel
    {
        public const int MaxSamplesPerClass = 20;

        private struct Sample
        {
            public long Ms;
            public int PromptTokens;
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, List<Sample>> _samples =
            new Dictionary<string, List<Sample>>(StringComparer.OrdinalIgnoreCase);

        // Daily token accounting for metered backends. Not persisted across restarts:
        // it protects against runaway sessions, not against restarting the game.
        private static int _tokensSpentToday;
        private static DateTime _tokensDay = DateTime.UtcNow.Date;

        /// <summary>Record one completed request. workClass is the ChatOptions.eventType ("custom" when unset).</summary>
        public static void Record(string workClass, long latencyMs, int promptTokens)
        {
            if (latencyMs <= 0) return;
            string key = string.IsNullOrEmpty(workClass) ? "custom" : workClass;

            lock (_lock)
            {
                if (!_samples.TryGetValue(key, out var list))
                {
                    list = new List<Sample>(MaxSamplesPerClass);
                    _samples[key] = list;
                }
                if (list.Count >= MaxSamplesPerClass) list.RemoveAt(0);
                list.Add(new Sample { Ms = latencyMs, PromptTokens = Math.Max(0, promptTokens) });
            }
        }

        public static int SampleCount(string workClass)
        {
            lock (_lock)
            {
                return _samples.TryGetValue(workClass ?? "custom", out var list) ? list.Count : 0;
            }
        }

        public static int TotalSampleCount()
        {
            lock (_lock)
            {
                int n = 0;
                foreach (var list in _samples.Values) n += list.Count;
                return n;
            }
        }

        /// <summary>P95 latency across every class — the overall responsiveness signal. 0 when empty.</summary>
        public static float P95All()
        {
            var all = new List<long>();
            lock (_lock)
            {
                foreach (var list in _samples.Values)
                    foreach (var s in list)
                        all.Add(s.Ms);
            }
            if (all.Count == 0) return 0f;
            all.Sort();
            int idx = Math.Max(0, (int)Math.Ceiling(all.Count * 0.95) - 1);
            return all[idx];
        }

        /// <summary>
        /// Fit floor (intercept) and per-token slope for a class. Falls back to
        /// floor = fastest sample, slope = 0 when there is too little token spread to
        /// regress meaningfully. False with fewer than three samples.
        /// </summary>
        public static bool TryEstimateCurve(string workClass, out float floorMs, out float msPerToken)
        {
            floorMs = 0f;
            msPerToken = 0f;

            List<Sample> copy;
            lock (_lock)
            {
                if (!_samples.TryGetValue(workClass ?? "custom", out var list) || list.Count < 3)
                    return false;
                copy = new List<Sample>(list);
            }

            long minMs = long.MaxValue;
            int minTok = int.MaxValue, maxTok = int.MinValue;
            double sumX = 0, sumY = 0;
            foreach (var s in copy)
            {
                if (s.Ms < minMs) minMs = s.Ms;
                if (s.PromptTokens < minTok) minTok = s.PromptTokens;
                if (s.PromptTokens > maxTok) maxTok = s.PromptTokens;
                sumX += s.PromptTokens;
                sumY += s.Ms;
            }

            // Regression needs spread; identical-size prompts tell us nothing about slope.
            if (copy.Count < 5 || (maxTok - minTok) < 256)
            {
                floorMs = minMs;
                return true;
            }

            double meanX = sumX / copy.Count;
            double meanY = sumY / copy.Count;
            double sxx = 0, sxy = 0;
            foreach (var s in copy)
            {
                double dx = s.PromptTokens - meanX;
                sxx += dx * dx;
                sxy += dx * (s.Ms - meanY);
            }

            double slope = sxx > 0 ? sxy / sxx : 0;
            if (slope < 0) slope = 0; // noise; latency does not genuinely fall with size
            double intercept = meanY - slope * meanX;
            if (intercept < 0) intercept = minMs;

            floorMs = (float)intercept;
            msPerToken = (float)slope;
            return true;
        }

        /// <summary>Clear all history — the model or backend changed, old timings are meaningless.</summary>
        public static void Reset(string reason)
        {
            lock (_lock)
            {
                _samples.Clear();
            }
            SynapseLogger.Message($"[Tier] Latency history reset: {reason}", "performance");
        }

        public static void RecordTokensSpent(int tokens)
        {
            if (tokens <= 0) return;
            lock (_lock)
            {
                RolloverIfNeeded();
                _tokensSpentToday += tokens;
            }
        }

        public static int TokensSpentToday
        {
            get
            {
                lock (_lock)
                {
                    RolloverIfNeeded();
                    return _tokensSpentToday;
                }
            }
        }

        private static void RolloverIfNeeded()
        {
            var today = DateTime.UtcNow.Date;
            if (today != _tokensDay)
            {
                _tokensDay = today;
                _tokensSpentToday = 0;
            }
        }
    }
}
