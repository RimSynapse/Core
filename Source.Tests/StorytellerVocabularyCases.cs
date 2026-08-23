using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Covers the storyteller tool-vocabulary allowlist at the executor boundary (Core #63).
    ///
    /// Security rests on capability, not prompt persuasion: a verb outside the active scope
    /// must be refused by SynapseToolRegistry.ExecuteTool (never run, one logged line), a
    /// scoped script with an out-of-scope step must be rejected at validation, the ambient
    /// scope must survive execute_game_tool's re-entry so nothing launders out of scope, and
    /// consequence tools must clamp their magnitude to the difficulty budget.
    /// </summary>
    [SynapseTestSet]
    public static class StorytellerVocabularyCases
    {
        private const string Scope = SynapseToolVocabulary.StorytellerScope;

        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_StorytellerVocabularyRejectsUnlisted", () =>
            {
                bool executed = false;
                SynapseToolRegistry.RegisterTool(
                    "zz_vocab_probe", "test tool outside the storyteller vocabulary",
                    new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object>() },
                    args => { executed = true; return "{\"success\": true}"; });

                // Direct executor boundary: refused with an error payload, handler never runs.
                string refused = SynapseToolRegistry.ExecuteTool("zz_vocab_probe", "{}", true, Scope);
                Assert.Contains(refused, "error", "a scoped run must get a refusal payload");
                Assert.Contains(refused, "vocabulary", "the refusal must name the vocabulary");
                Assert.False(executed, "the handler must not run when the verb is out of scope");

                // Unscoped call still works — the scope is the boundary, not the tool.
                string allowed = SynapseToolRegistry.ExecuteTool("zz_vocab_probe", "{}", true, null);
                Assert.True(executed, "an unscoped call to the same tool must execute");
                Assert.Contains(allowed, "success", "the unscoped call must return the handler's payload");

                // Script boundary: a scoped script with an out-of-scope step is rejected at
                // validation — no step executes at all.
                var log = RunScopedScript("vocab-reject", Step("zz_vocab_probe", null));
                Assert.True(log.Any(l => l.Contains("rejected")),
                    "the scoped script must be rejected at validation, got: " + Join(log));
                Assert.True(log.Any(l => l.Contains("vocabulary")),
                    "the rejection must name the vocabulary, got: " + Join(log));
                Assert.False(log.Any(l => l.Contains("Executing step")),
                    "no step of a rejected script may execute");

                // An in-vocabulary read-only verb passes the same scoped script path.
                var okLog = RunScopedScript("vocab-accept", Step("get_colony_moods", null));
                Assert.True(okLog.Any(l => l.Contains("Executing step 1: get_colony_moods")),
                    "an in-vocabulary verb must execute under the scope, got: " + Join(okLog));

                return "out-of-scope verb refused at executor and validation; in-scope verb runs";
            });

            yield return new SynapseTestCase("Core_StorytellerVocabularyScopeSurvivesReentry", () =>
            {
                // execute_game_tool is itself out of the storyteller scope, so the launder
                // path dies at the outer gate…
                string outer = SynapseToolRegistry.ExecuteTool("execute_game_tool",
                    "{\"tool_name\": \"possess_colonist\", \"arguments_json\": \"{}\"}", true, Scope);
                Assert.Contains(outer, "error", "execute_game_tool must be refused under the storyteller scope");

                // …and even a scoped in-vocabulary handler that re-enters the registry
                // inherits the ambient scope, so it cannot reach out-of-scope verbs.
                bool innerExecuted = false;
                SynapseToolRegistry.RegisterTool(
                    "zz_vocab_inner", "test tool outside the vocabulary",
                    new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object>() },
                    args => { innerExecuted = true; return "{\"success\": true}"; });
                SynapseToolRegistry.RegisterTool(
                    "zz_vocab_outer", "test tool that re-enters the registry",
                    new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object>() },
                    args => SynapseToolRegistry.ExecuteTool("zz_vocab_inner", "{}"));
                SynapseToolVocabulary.Add(Scope, "zz_vocab_outer");
                string nested = SynapseToolRegistry.ExecuteTool("zz_vocab_outer", "{}", true, Scope);
                Assert.Contains(nested, "error", "a nested call must inherit the ambient scope and be refused");
                Assert.False(innerExecuted, "the nested out-of-scope handler must not run");

                return "ambient scope survives re-entry; launder paths refused";
            });

            yield return new SynapseTestCase("Core_ConsequenceToolClampsToBudget", () =>
            {
                // The pure contract every consequence tool applies (fire_incident uses it
                // verbatim): scoped callers are clamped to the ceiling, unscoped callers
                // keep their override, no override yields the ceiling.
                Assert.True(SynapseToolVocabulary.ResolvePoints(999999f, 500f, Scope) == 500f,
                    "a scoped override above the ceiling must clamp to it");
                Assert.True(SynapseToolVocabulary.ResolvePoints(200f, 500f, Scope) == 200f,
                    "a scoped override under the ceiling must pass");
                Assert.True(SynapseToolVocabulary.ResolvePoints(999999f, 500f, null) == 999999f,
                    "an unscoped override must pass untouched");
                Assert.True(SynapseToolVocabulary.ResolvePoints(0f, 500f, Scope) == 500f,
                    "no override must yield the ceiling");

                // And the ambient scope actually reaches a handler mid-execution, which is
                // what fire_incident keys the clamp on.
                string seen = "unset";
                SynapseToolRegistry.RegisterTool(
                    "zz_scope_witness", "test tool reporting the ambient scope",
                    new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object>() },
                    args => { seen = SynapseToolRegistry.CurrentScope ?? "(null)"; return "{\"success\": true}"; });
                SynapseToolVocabulary.Add(Scope, "zz_scope_witness");
                SynapseToolRegistry.ExecuteTool("zz_scope_witness", "{}", true, Scope);
                Assert.True(seen == Scope, $"the handler must observe the ambient scope, saw '{seen}'");
                SynapseToolRegistry.ExecuteTool("zz_scope_witness", "{}", true, null);
                Assert.True(seen == "(null)", $"an unscoped call must observe no scope, saw '{seen}'");

                return "points clamp holds under scope; ambient scope visible to handlers";
            });
        }

        private static SynapseScriptStep Step(string type, Dictionary<string, object> arguments)
        {
            return new SynapseScriptStep { type = type, arguments = arguments ?? new Dictionary<string, object>() };
        }

        /// <summary>Runs a one-step script under the storyteller scope and returns its log.</summary>
        private static List<string> RunScopedScript(string name, SynapseScriptStep step)
        {
            var log = new List<string>();
            var script = new SynapseScript { scriptName = name, steps = new List<SynapseScriptStep> { step } };
            try
            {
                SynapseScriptRunner.StartScript(script, line => log.Add(line ?? string.Empty), null, true, Scope);
            }
            catch (Exception ex)
            {
                log.Add("[threw] " + ex.GetType().Name + ": " + ex.Message);
            }
            return log;
        }

        private static string Join(IEnumerable<string> lines)
        {
            var list = lines.ToList();
            if (list.Count == 0) return "<no output>";
            return string.Join(" | ", list.Take(5).Select(l => l.Length > 110 ? l.Substring(0, 110) + "..." : l));
        }
    }
}
