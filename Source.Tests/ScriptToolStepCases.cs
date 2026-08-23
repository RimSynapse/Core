using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Covers how SynapseScriptRunner dispatches a step to the tool registry.
    ///
    /// A step type is a tool name: anything not handled as an alias or as wait_until falls
    /// through to SynapseToolRegistry.ExecuteTool. That worked, but nothing validated the name
    /// first, and the registry answers an unknown tool with {"error": ...} rather than throwing —
    /// which the runner logged as [Result], so a mistyped step read as a success.
    /// </summary>
    [SynapseTestSet]
    public static class ScriptToolStepCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_ScriptUnknownToolIsReported", () =>
            {
                var log = RunScript("unknown-tool", Step("definitely_not_a_tool_xyz", null));

                Assert.True(log.Any(l => l.Contains("unknown tool")),
                    "expected an unknown-tool report, got: " + Join(log));
                Assert.False(log.Any(l => l.Contains("[Result]")),
                    "an unknown tool must not be logged as a result: " + Join(log));

                return "unknown tool reported, not logged as a result";
            });

            yield return new SynapseTestCase("Core_ScriptRunsRegisteredTool", () =>
            {
                string tool = FirstReadOnlyTool();
                Assert.NotEmpty(tool, "no non-debug tool available to exercise");

                var log = RunScript("run-tool", Step(tool, null));
                Assert.True(log.Any(l => l.Contains("Executing step 1: " + tool)),
                    $"expected step 1 to execute '{tool}', got: " + Join(log));

                return $"executed '{tool}' as a step type";
            });

            yield return new SynapseTestCase("Core_ScriptCallToolStep", () =>
            {
                string tool = FirstReadOnlyTool();
                Assert.NotEmpty(tool, "no non-debug tool available to exercise");

                var args = new Dictionary<string, object> { { "tool", tool }, { "arguments", new Dictionary<string, object>() } };
                var log = RunScript("call-tool", Step("call_tool", args));

                Assert.True(log.Any(l => l.Contains("Executing step 1: " + tool)),
                    $"call_tool should have run '{tool}', got: " + Join(log));

                return "explicit call_tool step dispatched";
            });

            yield return new SynapseTestCase("Core_ScriptCallToolNeedsToolName", () =>
            {
                // Originally caught at execution time ("needs a 'tool' argument"); since the
                // step schema landed, validation rejects it earlier with a named error.
                // Either surface satisfies the contract: the missing field is reported and
                // the step never executes.
                var log = RunScript("call-tool-bad", Step("call_tool", new Dictionary<string, object>()));
                Assert.True(log.Any(l => l.Contains("missing required field 'tool'")
                                      || l.Contains("needs a 'tool' argument")),
                    "the missing tool name must be reported, got: " + Join(log));
                Assert.False(log.Any(l => l.Contains("Executing step")),
                    "a call_tool with no tool must not execute");
                return "missing tool name reported before execution";
            });

            yield return new SynapseTestCase("Core_ScriptResultKeyReachesLog", () =>
            {
                string tool = FirstReadOnlyTool();
                Assert.NotEmpty(tool, "no non-debug tool available to exercise");

                var args = new Dictionary<string, object> { { "resultKey", "probe" } };
                var log = RunScript("result-key", Step(tool, args));

                // The completion log is what SynapseActionExecutor hands back to the agent, so
                // a stored result only counts if it appears there.
                Assert.True(log.Any(l => l.Contains("probe = ")),
                    "expected the stored result in the completion log, got: " + Join(log));

                return "resultKey surfaced in the completion log";
            });
        }

        private static SynapseScriptStep Step(string type, Dictionary<string, object> arguments)
        {
            return new SynapseScriptStep { type = type, arguments = arguments ?? new Dictionary<string, object>() };
        }

        /// <summary>Runs a one-step script synchronously and returns everything it logged.</summary>
        private static List<string> RunScript(string name, SynapseScriptStep step)
        {
            var log = new List<string>();
            var script = new SynapseScript { scriptName = name, steps = new List<SynapseScriptStep> { step } };

            try
            {
                SynapseScriptRunner.StartScript(script, line => log.Add(line ?? string.Empty));
            }
            catch (Exception ex)
            {
                log.Add("[threw] " + ex.GetType().Name + ": " + ex.Message);
            }

            return log;
        }

        /// <summary>A non-debug tool to exercise. Avoids anything that mutates game state.</summary>
        private static string FirstReadOnlyTool()
        {
            var tool = SynapseToolRegistry.NonDebugTools
                .Select(t => t.name)
                .Where(n => !string.IsNullOrEmpty(n) && n.StartsWith("get_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n)
                .FirstOrDefault();
            return tool ?? SynapseToolRegistry.NonDebugTools.Select(t => t.name).FirstOrDefault();
        }

        private static string Join(IEnumerable<string> lines)
        {
            var list = lines.ToList();
            if (list.Count == 0) return "<no output>";
            return string.Join(" | ", list.Take(5).Select(l => l.Length > 110 ? l.Substring(0, 110) + "..." : l));
        }
    }
}
