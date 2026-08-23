using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Covers the tool search index and two-stage discovery.
    ///
    /// Background: prompt tool selection matched by substring ("rain" scored "train"),
    /// re-serialised every schema on every prompt build, and — worst — fell back to
    /// describing the ENTIRE registry whenever fewer than six tools matched, so a vague
    /// command produced the largest possible prompt. list_available_tools likewise
    /// returned full schemas for everything.
    /// </summary>
    [SynapseTestSet]
    public static class ToolSearchCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_IndexTokenNotSubstring", () =>
            {
                RegisterTestTool("zz_test_rain_dance", "perform a ceremonial rain dance", new[] { "rain" });
                RegisterTestTool("zz_test_train_skill", "improve shooting through training drills", null);

                var results = SynapseToolIndex.Search("make it rain", 20);
                var names = results.Select(r => r.Tool.name).ToList();

                Assert.True(names.Contains("zz_test_rain_dance"),
                    "token 'rain' must find the rain tool, got: " + string.Join(", ", names.Take(8)));
                Assert.False(names.Contains("zz_test_train_skill"),
                    "'rain' must NOT match 'train' — substring matching is the old bug");
                return "'rain' matches rain, not train";
            });

            yield return new SynapseTestCase("Core_IndexParamMatch", () =>
            {
                RegisterTestTool("zz_test_frob", "adjust the widget", null,
                    paramName: "frobnicate_target", paramDesc: "which frobnicate target to adjust");

                var results = SynapseToolIndex.Search("frobnicate the blue one", 5);
                Assert.True(results.Any(r => r.Tool.name == "zz_test_frob"),
                    "a parameter-only keyword must rank the owning tool");
                return "parameter tokens are searchable";
            });

            yield return new SynapseTestCase("Core_IndexLateRegistration", () =>
            {
                var before = SynapseToolIndex.Search("xyzzyplugh", 5);
                Assert.Equal(0, before.Count, "nothing should match the nonsense token yet");

                RegisterTestTool("zz_test_xyzzyplugh", "the xyzzyplugh maneuver", null);

                var after = SynapseToolIndex.Search("xyzzyplugh", 5);
                Assert.True(after.Any(r => r.Tool.name == "zz_test_xyzzyplugh"),
                    "a tool registered after the index was built must be findable");
                return "late registration invalidates and rebuilds the index";
            });

            yield return new SynapseTestCase("Core_ListToolsCompact", () =>
            {
                string unfiltered = SynapseToolRegistry.ExecuteTool("list_available_tools", "{}");
                Assert.NotEmpty(unfiltered, "unfiltered listing must return");
                Assert.DoesNotContain(unfiltered, "parameterSchema",
                    "the directory listing must not embed full schemas any more");

                string search = SynapseToolRegistry.ExecuteTool(
                    "list_available_tools", "{\"query\": \"rain dance\"}");
                Assert.Contains(search, "zz_test_rain_dance", "search must rank via the index");
                Assert.DoesNotContain(search, "parameterSchema",
                    "search results carry parameter names, not schemas");
                return $"directory {unfiltered.Length} chars, search {search.Length} chars, no embedded schemas";
            });

            yield return new SynapseTestCase("Core_DescribeTool", () =>
            {
                string described = SynapseToolRegistry.ExecuteTool(
                    "describe_tool", "{\"name\": \"zz_test_frob\"}");
                Assert.Contains(described, "parameterSchema", "describe_tool must return the full schema");
                Assert.Contains(described, "frobnicate_target", "the schema must include the parameter");

                string missing = SynapseToolRegistry.ExecuteTool(
                    "describe_tool", "{\"name\": \"zz_no_such_tool\"}");
                Assert.Contains(missing, "error", "an unknown name must return an error payload");
                return "full schema on demand, one tool at a time";
            });

            yield return new SynapseTestCase("Core_PromptToolSectionBounded", () =>
            {
                // The exact scenario that used to explode: a vague command matching nothing.
                string section = SynapseLlmPlanner.BuildToolSection("please just do something nice today");

                int toolLines = Regex.Matches(section, @"^- \*\*", RegexOptions.Multiline).Count;
                Assert.True(toolLines <= 12,
                    $"a vague command must yield a bounded tool list, got {toolLines} tools");
                Assert.Contains(section, "list_available_tools",
                    "the section must tell the model how to search for more");
                Assert.Contains(section, "describe_tool",
                    "the section must mention schema retrieval");
                return $"vague command -> {toolLines} tools, {section.Length} chars, with search guidance";
            });

            yield return new SynapseTestCase("Core_PromptToolSectionRelevant", () =>
            {
                RegisterTestTool("zz_test_rain_dance", "perform a ceremonial rain dance", new[] { "rain" });
                string section = SynapseLlmPlanner.BuildToolSection("do a rain dance for the colony");
                Assert.Contains(section, "zz_test_rain_dance",
                    "a relevant registered tool must be inlined for a matching command");
                return "relevant match inlined";
            });
        }

        /// <summary>
        /// Registers an inert, well-formed test tool. Registration overwrites by name, so
        /// repeated calls are idempotent; handlers satisfy the registry-wide contract
        /// cases (valid JSON on empty args, no throw on malformed args).
        /// </summary>
        private static void RegisterTestTool(string name, string description, string[] keywords,
            string paramName = "note", string paramDesc = "optional note")
        {
            SynapseToolRegistry.RegisterTool(
                name,
                description,
                new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        [paramName] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = paramDesc
                        }
                    }
                },
                args => "{\"success\": true}",
                isDebug: false,
                keywords: keywords?.ToList());
        }
    }
}
