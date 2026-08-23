using System;
using System.Collections.Generic;
using System.Linq;
using RimSynapse.Models;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Covers how SynapseActionExecutor decides between the script path and the flat tool-call
    /// path, and what it reports when a script cannot run.
    ///
    /// Background: TryExecuteScript ended in `catch (Exception) { }` and picked between the two
    /// paths purely on whether SynapseScript happened to deserialise with a non-empty scriptName
    /// and steps. A response carrying steps but missing a scriptName was therefore treated as
    /// flat calls, its steps silently dropped, with nothing in the log to say a script had been
    /// attempted at all.
    /// </summary>
    [SynapseTestSet]
    public static class ActionExecutorCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_ScriptMissingNameIsReported", () =>
            {
                var log = Run("{\"steps\":[{\"type\":\"wait_until\",\"arguments\":{}}]}");
                Assert.True(log.Any(l => l.Contains("no scriptName")),
                    "expected a diagnostic naming the missing scriptName, got: " + Join(log));
                return "missing scriptName reported";
            });

            yield return new SynapseTestCase("Core_ScriptWithNoStepsIsReported", () =>
            {
                var log = Run("{\"scriptName\":\"empty\",\"steps\":[]}");
                Assert.True(log.Any(l => l.Contains("no steps")),
                    "expected a diagnostic naming the empty step list, got: " + Join(log));
                return "empty step list reported";
            });

            yield return new SynapseTestCase("Core_MalformedScriptIsReported", () =>
            {
                // "steps" present but the wrong shape, so deserialisation throws.
                var log = Run("{\"scriptName\":\"bad\",\"steps\":\"not-an-array\"}");
                Assert.True(log.Any(l => l.Contains("Malformed script")),
                    "expected a malformed-script diagnostic, got: " + Join(log));
                return "malformed script reported";
            });

            yield return new SynapseTestCase("Core_FlatCallsStayQuiet", () =>
            {
                // An ordinary flat-call response never claimed to be a script, so it must not
                // produce script diagnostics — otherwise the log fills with false alarms.
                var log = Run("{\"calls\":[{\"tool\":\"definitely_not_a_tool\",\"arguments\":{}}]}");
                var noisy = log.Where(l => l.Contains("[Script]")).ToList();
                Assert.True(noisy.Count == 0,
                    "flat calls should not emit script diagnostics, got: " + Join(noisy));
                return "flat-call responses stay quiet";
            });
        }

        /// <summary>
        /// Drives ProcessResponse and returns the lines it emitted. The rejection paths return
        /// before anything is queued, so the diagnostics are available synchronously.
        /// </summary>
        private static List<string> Run(string json)
        {
            var log = new List<string>();
            var messages = new List<ChatMessage>();
            var planner = new SynapseLlmPlanner("test", _ => { }, (_, __) => { });

            try
            {
                SynapseActionExecutor.ProcessResponse(
                    planner, messages, json,
                    new ChatOptions { priority = 1, requestName = "TestRunner ProcessResponse" },
                    line => { lock (log) log.Add(line ?? string.Empty); },
                    (_, __) => { });
            }
            catch (Exception ex)
            {
                log.Add("[threw] " + ex.GetType().Name + ": " + ex.Message);
            }

            lock (log) return log.ToList();
        }

        private static string Join(IEnumerable<string> lines)
        {
            var list = lines.ToList();
            if (list.Count == 0) return "<no output>";
            return string.Join(" | ", list.Take(4).Select(l => l.Length > 120 ? l.Substring(0, 120) + "..." : l));
        }
    }
}
