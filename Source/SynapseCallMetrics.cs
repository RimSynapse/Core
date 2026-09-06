using System;
using System.Collections.Generic;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Scoped LLM call metrics (Core #127): throughput (tokens/sec) overall and per model, token
    /// totals, call counts, and the largest prompt ("max call size") seen per endpoint. Fed from the
    /// single completion path in <c>HttpEngine</c>; read by the monitor's expanded left column.
    ///
    /// Two scopes, neither the old lifetime settings counter:
    ///   • <see cref="Session"/> — in-memory, reset when a game is started or loaded ("since I sat down").
    ///   • <see cref="Save"/>    — scribed into the save, accumulating across sessions of that colony.
    /// </summary>
    public static class SynapseCallMetrics
    {
        /// <summary>Per-model throughput accumulator.</summary>
        public class ModelStat : IExposable
        {
            public long completionTokens;
            public double seconds;
            public float Tops => seconds > 0.0 ? (float)(completionTokens / seconds) : 0f;

            public void ExposeData()
            {
                Scribe_Values.Look(ref completionTokens, "c", 0L);
                Scribe_Values.Look(ref seconds, "s", 0.0);
            }
        }

        /// <summary>One scope's worth of metrics.</summary>
        public class Bucket : IExposable
        {
            public int calls;
            public int failures;
            public long promptTokens;
            public long completionTokens;
            public double completionSeconds;
            public Dictionary<string, ModelStat> perModel = new Dictionary<string, ModelStat>();
            /// <summary>Largest total context (prompt + completion) actually driven through each endpoint —
            /// the "proven" context, against which the reported window can be judged.</summary>
            public Dictionary<string, int> maxContextByEndpoint = new Dictionary<string, int>();

            /// <summary>Overall throughput in completion tokens/sec.</summary>
            public float Tops => completionSeconds > 0.0 ? (float)(completionTokens / completionSeconds) : 0f;

            /// <summary>The largest proven context across all endpoints (0 if none recorded).</summary>
            public int ProvenMaxContext()
            {
                int max = 0;
                foreach (var v in maxContextByEndpoint.Values) if (v > max) max = v;
                return max;
            }

            public void Record(string model, string endpoint, int prompt, int completion, long durationMs, bool success)
            {
                calls++;
                if (!success) { failures++; return; }

                double sec = durationMs / 1000.0;
                promptTokens += prompt;
                completionTokens += completion;
                completionSeconds += sec;

                if (!string.IsNullOrEmpty(model))
                {
                    if (!perModel.TryGetValue(model, out var ms)) { ms = new ModelStat(); perModel[model] = ms; }
                    ms.completionTokens += completion;
                    ms.seconds += sec;
                }
                if (!string.IsNullOrEmpty(endpoint))
                {
                    int ctx = prompt + completion;
                    maxContextByEndpoint.TryGetValue(endpoint, out int cur);
                    if (ctx > cur) maxContextByEndpoint[endpoint] = ctx;
                }
            }

            public void ExposeData()
            {
                Scribe_Values.Look(ref calls, "calls", 0);
                Scribe_Values.Look(ref failures, "failures", 0);
                Scribe_Values.Look(ref promptTokens, "promptTokens", 0L);
                Scribe_Values.Look(ref completionTokens, "completionTokens", 0L);
                Scribe_Values.Look(ref completionSeconds, "completionSeconds", 0.0);
                Scribe_Collections.Look(ref perModel, "perModel", LookMode.Value, LookMode.Deep);
                Scribe_Collections.Look(ref maxContextByEndpoint, "maxContextByEndpoint", LookMode.Value, LookMode.Value);
                if (Scribe.mode == LoadSaveMode.LoadingVars)
                {
                    if (perModel == null) perModel = new Dictionary<string, ModelStat>();
                    if (maxContextByEndpoint == null) maxContextByEndpoint = new Dictionary<string, int>();
                }
            }
        }

        public static Bucket Session = new Bucket();
        public static Bucket Save = new Bucket();

        /// <summary>Reset only the in-memory session scope (on load — the save scope comes from Scribe).</summary>
        public static void ResetSession() => Session = new Bucket();

        /// <summary>Reset both scopes (a brand-new colony has no history in either).</summary>
        public static void ResetForNewGame() { Session = new Bucket(); Save = new Bucket(); }

        /// <summary>Scribe the per-save bucket. Call from a GameComponent's ExposeData.</summary>
        public static void ExposeSave()
        {
            Scribe_Deep.Look(ref Save, "synapseCallMetrics");
            if (Scribe.mode == LoadSaveMode.LoadingVars && Save == null) Save = new Bucket();
        }

        /// <summary>Record one completed (or failed) call into both scopes. Never throws to its caller.</summary>
        public static void Record(ApiProvider provider, string baseUrl, ChatResult r)
        {
            if (r == null) return;
            try
            {
                string endpoint = EndpointLabel(provider, baseUrl);
                Session.Record(r.model, endpoint, r.promptTokens, r.completionTokens, r.durationMs, r.success);
                Save.Record(r.model, endpoint, r.promptTokens, r.completionTokens, r.durationMs, r.success);
            }
            catch (Exception ex)
            {
                SynapseLogger.Warning("Call metrics record failed: " + ex.Message, "performance");
            }
        }

        /// <summary>A short, stable label for an endpoint: provider plus host:port for a local server.</summary>
        public static string EndpointLabel(ApiProvider provider, string baseUrl)
        {
            try
            {
                if (!string.IsNullOrEmpty(baseUrl))
                {
                    var u = new Uri(baseUrl);
                    return $"{provider} @ {u.Host}:{u.Port}";
                }
            }
            catch { /* not a URL — fall through */ }
            return provider.ToString();
        }
    }
}
