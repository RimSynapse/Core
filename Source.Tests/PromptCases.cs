using System;
using System.Collections.Generic;
using System.Linq;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Covers what the model is told and shown before its first turn: the script step
    /// schema (#22) and pre-seeded read-only observations (#28).
    /// </summary>
    [SynapseTestSet]
    public static class PromptCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_ScriptSchemaInPrompt", () =>
            {
                SynapseScriptRunner.RegisterWaitCondition("zz_test_condition", (p, a) => false);
                string schema = SynapseScriptRunner.DescribeStepSchema();

                foreach (var required in new[] { "call_tool", "wait_until", "resultKey", "get_stored_result", "timeoutTicks", "describe_tool" })
                {
                    Assert.Contains(schema, required, $"the step schema must mention {required}");
                }
                Assert.Contains(schema, "zz_test_condition",
                    "companion-registered wait conditions must be discoverable");
                Assert.True(schema.Length < 2000,
                    $"the schema section must stay compact, got {schema.Length} chars");
                return $"schema section {schema.Length} chars, all step types and the custom condition present";
            });

            yield return new SynapseTestCase("Core_PreSeedRunsReadOnlyMatch", () =>
            {
                RegisterReadOnly("get_zz_probe", new[] { "zzprobe" },
                    "{\"success\": true, \"data\": \"PROBE_DATA_MARKER\"}");

                string section = SynapseLlmPlanner.BuildPreSeedSection("check the zzprobe status", out var run);
                Assert.True(run.Contains("get_zz_probe"), "the matching read-only tool must be pre-executed");
                Assert.Contains(section, "PROBE_DATA_MARKER", "the tool's result must be inlined");
                Assert.Contains(section, "Pre-fetched observations", "the section must be labelled as pre-fetched");
                return "keyword match pre-executed and inlined with label";
            });

            yield return new SynapseTestCase("Core_PreSeedSkipsMutating", () =>
            {
                SynapseToolRegistry.RegisterTool("get_zz_mut", "mutating despite the prefix",
                    Schema(), args => "{\"success\": true}", false, new List<string> { "zzmutquery" }, isMutating: true);

                SynapseLlmPlanner.BuildPreSeedSection("run the zzmutquery", out var run);
                Assert.False(run.Contains("get_zz_mut"),
                    "a flagged mutating tool must never be pre-executed, whatever its name");
                return "mutating flag beats the read-only prefix";
            });

            yield return new SynapseTestCase("Core_PreSeedSkipsNonPrefixed", () =>
            {
                RegisterReadOnly("zz_do_thing", new[] { "zzdothing" }, "{\"success\": true}");
                SynapseLlmPlanner.BuildPreSeedSection("please zzdothing now", out var run);
                Assert.False(run.Contains("zz_do_thing"),
                    "tools outside the get_/search_/list_ allowlist must not be speculatively run");
                return "prefix allowlist enforced";
            });

            yield return new SynapseTestCase("Core_PreSeedSkipsVague", () =>
            {
                string section = SynapseLlmPlanner.BuildPreSeedSection("please do something nice today", out var run);
                Assert.Equal(0, run.Count,
                    "a vague command must pre-seed nothing, ran: " + string.Join(", ", run));
                Assert.True(string.IsNullOrEmpty(section), "no matches means no section");
                return "vague command pre-seeds nothing";
            });

            yield return new SynapseTestCase("Core_PreSeedSkipsErrorResults", () =>
            {
                RegisterReadOnly("get_zz_err", new[] { "zzerrprobe" }, "{\"error\": \"needs arguments\"}");
                string section = SynapseLlmPlanner.BuildPreSeedSection("fetch the zzerrprobe", out var run);
                Assert.False(run.Contains("get_zz_err"), "an error result must not count as pre-seeded");
                Assert.False(section.Contains("needs arguments"), "error payloads must not spend prompt space");
                return "failed speculation discarded";
            });

            yield return new SynapseTestCase("Core_PreSeedScalesWithTier", () => WithTier(s =>
            {
                RegisterReadOnly("get_zz_multi_a", new[] { "zzmulti" }, "{\"success\": true, \"v\": \"a\"}");
                RegisterReadOnly("get_zz_multi_b", new[] { "zzmulti" }, "{\"success\": true, \"v\": \"b\"}");
                RegisterReadOnly("get_zz_multi_c", new[] { "zzmulti" }, "{\"success\": true, \"v\": \"c\"}");

                s.agentTierMode = 1; // Minimal
                SynapseLlmPlanner.BuildPreSeedSection("show zzmulti data", out var minimalRun);

                s.agentTierMode = 3; // Rich
                SynapseLlmPlanner.BuildPreSeedSection("show zzmulti data", out var richRun);

                Assert.Equal(1, minimalRun.Count, $"Minimal must pre-seed one tool, got {minimalRun.Count}");
                Assert.Equal(3, richRun.Count, $"Rich must pre-seed three tools, got {richRun.Count}");
                return $"Minimal ran {minimalRun.Count}, Rich ran {richRun.Count}";
            }));
        }

        private static Dictionary<string, object> Schema()
        {
            return new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>()
            };
        }

        private static void RegisterReadOnly(string name, string[] keywords, string result)
        {
            SynapseToolRegistry.RegisterTool(name, "test read-only tool", Schema(),
                args => result, false, keywords?.ToList());
        }

        private static string WithTier(Func<RimSynapseSettings, string> body)
        {
            var s = RimSynapseMod.Instance?.Settings;
            Assert.NotNull(s, "settings unavailable");
            var saved = s.agentTierMode;
            try { return body(s); }
            finally { s.agentTierMode = saved; }
        }
    }
}
