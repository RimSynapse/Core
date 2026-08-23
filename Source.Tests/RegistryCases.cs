using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Baseline cases for the agent tool registry. These verify the plumbing every
    /// per-issue test plan builds on: that tools register, that ExecuteTool returns
    /// well-formed JSON, and that bad input produces a structured error instead of a throw.
    ///
    /// Per-feature cases (e.g. Factions_GetMotivatedFactions) should live in their own
    /// file under Cases/ and be added to TestRunnerGameComponent.
    /// </summary>
    [SynapseTestSet(TestPhase.Contract)]
    public static class RegistryCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Registry_Initializes", () =>
            {
                SynapseToolRegistry.EnsureInitialized();
                var tools = SynapseToolRegistry.AllTools?.ToList();
                Assert.NotNull(tools, "AllTools should not be null after EnsureInitialized");
                Assert.True(tools.Count > 0, "expected at least one registered tool");
                return $"{tools.Count} tools registered";
            });

            yield return new SynapseTestCase("Registry_ToolsAreWellFormed", () =>
            {
                var tools = SynapseToolRegistry.AllTools.ToList();
                var bad = new List<string>();

                foreach (var t in tools)
                {
                    if (string.IsNullOrEmpty(t.name)) { bad.Add("<unnamed tool>"); continue; }
                    if (string.IsNullOrEmpty(t.description)) bad.Add($"{t.name}: no description");
                    if (t.handler == null) bad.Add($"{t.name}: null handler");
                    if (t.parameters == null) bad.Add($"{t.name}: null parameters schema");
                }

                Assert.True(bad.Count == 0, "malformed tools: " + string.Join("; ", bad.Take(5)));
                return $"all {tools.Count} tools have name, description, schema and handler";
            });

            yield return new SynapseTestCase("Registry_ToolNamesAreUnique", () =>
            {
                var names = SynapseToolRegistry.AllTools.Select(t => t.name).ToList();
                var dupes = names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                Assert.True(dupes.Count == 0, "duplicate tool names: " + string.Join(", ", dupes));
                return $"{names.Count} unique tool names";
            });

            yield return new SynapseTestCase("Registry_UnknownToolReturnsError", () =>
            {
                var result = SynapseToolRegistry.ExecuteTool("definitely_not_a_real_tool_xyz", "{}");
                Assert.NotEmpty(result, "ExecuteTool should return a payload for an unknown tool");
                Assert.Contains(result, "error", "unknown tool should produce an error payload");

                // Must be structured JSON, not a bare string, so callers can parse it.
                var parsed = TryParse(result);
                Assert.NotNull(parsed, "unknown-tool error should be valid JSON");
                return "unknown tool returns structured error JSON";
            });

            yield return new SynapseTestCase("Registry_HandlersReturnJsonOnEmptyArgs", () =>
            {
                // Every non-debug tool should tolerate "{}" — returning either a result or a
                // structured error. It must never throw or return non-JSON.
                var tools = SynapseToolRegistry.NonDebugTools.ToList();
                var offenders = new List<string>();

                foreach (var t in tools)
                {
                    string result;
                    try
                    {
                        result = SynapseToolRegistry.ExecuteTool(t.name, "{}");
                    }
                    catch (System.Exception ex)
                    {
                        offenders.Add($"{t.name} threw {ex.GetType().Name}");
                        continue;
                    }

                    if (string.IsNullOrEmpty(result)) { offenders.Add($"{t.name} returned empty"); continue; }
                    if (TryParse(result) == null) offenders.Add($"{t.name} returned non-JSON");
                }

                Assert.True(offenders.Count == 0,
                    $"{offenders.Count}/{tools.Count} tools misbehaved on empty args: " +
                    string.Join("; ", offenders.Take(5)));
                return $"all {tools.Count} non-debug tools returned JSON for empty args";
            });

            yield return new SynapseTestCase("Registry_HandlersRejectMalformedArgs", () =>
            {
                // Malformed JSON must produce a structured error, never an unhandled throw.
                var tools = SynapseToolRegistry.NonDebugTools.ToList();
                var throwers = new List<string>();

                foreach (var t in tools)
                {
                    try
                    {
                        var result = SynapseToolRegistry.ExecuteTool(t.name, "{not valid json");
                        if (string.IsNullOrEmpty(result)) throwers.Add($"{t.name} returned empty");
                    }
                    catch (System.Exception ex)
                    {
                        throwers.Add($"{t.name} threw {ex.GetType().Name}");
                    }
                }

                Assert.True(throwers.Count == 0,
                    "tools threw on malformed args: " + string.Join("; ", throwers.Take(5)));
                return $"all {tools.Count} non-debug tools handled malformed args gracefully";
            });
        }

        /// <summary>Returns the parsed token, or null when the payload is not valid JSON.</summary>
        private static JToken TryParse(string json)
        {
            try { return JToken.Parse(json); }
            catch { return null; }
        }
    }
}
