using System;
using System.Collections.Generic;
using System.Linq;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Guards the deferred-callback pattern used throughout RimSynapse.
    ///
    /// LLM responses arrive off the main thread and are handed back via
    /// SynapseGameComponent.Enqueue, so the body runs on a later frame. A try/catch placed
    /// around the PromptAsync callback does NOT cover that body — anything thrown inside
    /// escapes to ProcessMainThreadQueue and is reported as a bare "Callback error" with no
    /// indication of which feature failed.
    /// </summary>
    [SynapseTestSet]
    public static class CallbackCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_NoUnhandledQueueCallbackErrors", () =>
            {
                var offenders = TestLog.RecentLines()
                    .Where(l => l.IndexOf("Callback error", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

                Assert.True(offenders.Count == 0,
                    $"{offenders.Count} unhandled main-thread queue failure(s): " +
                    string.Join(" | ", offenders.Take(3).Select(Shorten)));

                return "main-thread queue drained cleanly";
            });
        }

        private static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s)) return "<empty>";
            var oneLine = s.Replace("\r", " ").Replace("\n", " ");
            return oneLine.Length <= 160 ? oneLine : oneLine.Substring(0, 160) + "...";
        }
    }
}
