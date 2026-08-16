using System.Linq;
using System.Text;
using LudeonTK;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for the storyteller tool-vocabulary allowlist (Core #63), grouped
    /// under "RimSynapse". Probes the executor boundary directly — an allowed verb runs, a
    /// disallowed verb is refused without executing, a scoped script with a disallowed step
    /// is rejected at validation, and the points clamp holds. Headlessly runnable via the
    /// toolkit's run_debug_action.
    /// </summary>
    public static class DebugActions_ToolVocabulary
    {
        [DebugAction("RimSynapse", "Storyteller vocabulary: probe executor boundary",
            allowedGameStates = AllowedGameStates.Playing)]
        private static void ProbeStorytellerVocabulary()
        {
            const string scope = SynapseToolVocabulary.StorytellerScope;
            var sb = new StringBuilder();
            int failures = 0;

            void Check(string name, bool pass, string detail)
            {
                if (!pass) failures++;
                sb.AppendLine($"  {(pass ? "PASS" : "FAIL")} {name} | {detail}");
            }

            sb.AppendLine("[RimSynapse] Storyteller vocabulary probe:");
            sb.AppendLine($"  scope '{scope}' permits: {string.Join(", ", SynapseToolVocabulary.Tools(scope))}");

            // 1. An allowed read-only verb executes under the scope.
            string moods = SynapseToolRegistry.ExecuteTool("get_colony_moods", "{}", true, scope);
            Check("allowed verb executes", moods != null && !moods.Contains("not in the"),
                Truncate(moods));

            // 2. A disallowed verb is refused and its handler never runs.
            string refused = SynapseToolRegistry.ExecuteTool("possess_colonist", "{}", true, scope);
            Check("disallowed verb refused", refused != null && refused.Contains("\"error\"") && refused.Contains("vocabulary"),
                Truncate(refused));

            // 3. execute_game_tool cannot launder a disallowed verb (it is itself out of scope).
            string laundered = SynapseToolRegistry.ExecuteTool("execute_game_tool",
                "{\"tool_name\": \"possess_colonist\", \"arguments_json\": \"{}\"}", true, scope);
            Check("launder path refused", laundered != null && laundered.Contains("\"error\""),
                Truncate(laundered));

            // 4. A scoped script with a disallowed step is rejected at validation.
            var script = new SynapseScript
            {
                scriptName = "debug_vocabulary_probe",
                steps = new System.Collections.Generic.List<SynapseScriptStep>
                {
                    new SynapseScriptStep { type = "modify_pawn_state", arguments = new System.Collections.Generic.Dictionary<string, object>() }
                }
            };
            var scriptLog = new StringBuilder();
            SynapseScriptRunner.StartScript(script, line => scriptLog.AppendLine(line), null, true, scope);
            string scriptOut = scriptLog.ToString();
            Check("scoped script rejected at validation",
                scriptOut.Contains("rejected") && scriptOut.Contains("vocabulary") && !scriptOut.Contains("[Result]"),
                Truncate(scriptOut.Replace("\n", " ").Replace("\r", "")));

            // 5. The points clamp: scoped callers cannot exceed the ceiling.
            float clamped = SynapseToolVocabulary.ResolvePoints(999999f, 500f, scope);
            float unscoped = SynapseToolVocabulary.ResolvePoints(999999f, 500f, null);
            Check("points clamped under scope", clamped == 500f && unscoped == 999999f,
                $"scoped={clamped}, unscoped={unscoped}");

            sb.AppendLine(failures == 0
                ? "[RimSynapse] Vocabulary probe: all checks passed."
                : $"[RimSynapse] Vocabulary probe: {failures} check(s) FAILED.");
            SynapseLogger.Message(sb.ToString().TrimEnd());
        }

        private static string Truncate(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(null)";
            s = s.Replace("\n", " ").Replace("\r", "");
            return s.Length <= 120 ? s : s.Substring(0, 120) + "…";
        }
    }
}
