using System;
using System.Collections.Generic;
using System.Linq;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Covers the declared step schema: logged alias rewrites, strict unknown-field
    /// rejection on structural steps, warning-only checks on tool-step arguments, and the
    /// guarantee that legacy shapes still run.
    /// </summary>
    [SynapseTestSet]
    public static class ScriptValidationCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_AliasRewritesAreLogged", () =>
            {
                var log = Run("zz-alias", Step("equip_item", Args(("pawnName", "Nobody"), ("itemName", "knife"))));
                Assert.True(log.Any(l => l.Contains("alias 'equip_item' rewritten to 'possess_colonist'")),
                    "the legacy rewrite must be logged, got: " + Join(log));
                Assert.True(log.Any(l => l.Contains("Executing step 1: possess_colonist")),
                    "the legacy shape must still execute after rewriting");
                return "legacy alias logged and still runs";
            });

            yield return new SynapseTestCase("Core_DeclaredShapeSkipsShim", () =>
            {
                var log = Run("zz-declared",
                    Step("wait_until", Args(("condition", "pawn_downed"), ("pawnName", "Nobody"), ("timeoutTicks", 1))));
                Assert.False(log.Any(l => l.Contains("alias")),
                    "a script in the declared shape must not touch the shim: " + Join(log));
                return "declared shape produces no alias lines";
            });

            yield return new SynapseTestCase("Core_WaitUnknownFieldRejected", () =>
            {
                var log = Run("zz-badwait",
                    Step("wait_until", Args(("condition", "pawn_downed"), ("pawn", "Nobody"))));
                Assert.True(log.Any(l => l.Contains("Step 1 (wait_until)") && l.Contains("'pawn'")),
                    "the rejection must name the step and the unknown field, got: " + Join(log));
                Assert.False(log.Any(l => l.Contains("Executing step")),
                    "a rejected script must not execute any step");
                return "unknown wait_until field rejected by name, nothing executed";
            });

            yield return new SynapseTestCase("Core_CallToolUnknownFieldRejected", () =>
            {
                var log = Run("zz-badcall",
                    Step("call_tool", Args(("tools", "get_stored_result"))));
                Assert.True(log.Any(l => l.Contains("unknown field 'tools'")),
                    "the typo'd field must be named, got: " + Join(log));
                Assert.True(log.Any(l => l.Contains("missing required field 'tool'")),
                    "the missing required field must also be named");
                return "call_tool typo rejected with both findings";
            });

            yield return new SynapseTestCase("Core_RejectionStillResolvesChain", () =>
            {
                bool finished = false;
                var script = new SynapseScript
                {
                    scriptName = "zz-reject-chain",
                    steps = new List<SynapseScriptStep> { Step("wait_until", Args(("bogus", 1))) }
                };
                SynapseScriptRunner.StartScript(script, _ => { }, () => finished = true);
                Assert.True(finished, "onFinished must run on rejection so an agent chain sees the errors");
                Assert.Equal(0, SynapseScriptRunner.GetActiveScriptNames().Count(n => n == "zz-reject-chain"),
                    "a rejected script must not become active");
                return "rejection resolves the chain without executing";
            });

            yield return new SynapseTestCase("Core_ToolArgMismatchWarnsOnly", () =>
            {
                SynapseToolRegistry.RegisterTool("zz_val_tool", "validation test tool",
                    new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["note"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "a note" }
                        }
                    },
                    args => "{\"success\": true}");

                var log = Run("zz-toolwarn", Step("zz_val_tool", Args(("bogus_arg", 1))));
                Assert.True(log.Any(l => l.Contains("'bogus_arg'") && l.Contains("declared schema")),
                    "the undeclared argument must be warned about, got: " + Join(log));
                Assert.True(log.Any(l => l.Contains("Executing step 1: zz_val_tool")),
                    "tool-argument mismatches warn but must not reject — registered schemas are not all complete");
                return "undeclared tool argument warned, script still ran";
            });
        }

        private static SynapseScriptStep Step(string type, Dictionary<string, object> args)
            => new SynapseScriptStep { type = type, arguments = args };

        private static Dictionary<string, object> Args(params (string key, object value)[] pairs)
        {
            var d = new Dictionary<string, object>();
            foreach (var (key, value) in pairs) d[key] = value;
            return d;
        }

        private static List<string> Run(string name, params SynapseScriptStep[] steps)
        {
            var log = new List<string>();
            var script = new SynapseScript { scriptName = name, steps = steps.ToList() };
            try
            {
                SynapseScriptRunner.StartScript(script, line => log.Add(line ?? string.Empty));
            }
            catch (Exception ex)
            {
                log.Add("[threw] " + ex.GetType().Name + ": " + ex.Message);
            }
            // Waiting scripts (wait_until with a live timeout) would linger; abort to keep
            // the registry clean for later cases.
            SynapseScriptRunner.AbortScript(name);
            return log;
        }

        private static string Join(IEnumerable<string> lines)
        {
            var list = lines.ToList();
            return list.Count == 0 ? "<no output>"
                : string.Join(" | ", list.Take(5).Select(l => l.Length > 110 ? l.Substring(0, 110) + "..." : l));
        }
    }
}
