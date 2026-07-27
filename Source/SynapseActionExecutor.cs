using System;
using System.Collections.Generic;
using Verse;
using Newtonsoft.Json;
using RimSynapse.Models;

namespace RimSynapse
{
    /// <summary>
    /// Processes LLM responses, extracts JSON payloads, and schedules script or tool call runs on RimWorld's main loop.
    /// </summary>
    public static class SynapseActionExecutor
    {
        public static void ProcessResponse(
            SynapseLlmPlanner planner, 
            List<ChatMessage> messages, 
            string responseContent, 
            ChatOptions options, 
            Action<string> logCallback, 
            Action<bool, string> onComplete)
        {
            string json = RimSynapse.Utils.JsonHelper.ExtractJson(responseContent);

            // The turn's plan, as emitted — the inspector shows it verbatim rather than
            // reconstructing it from what ended up executing.
            SynapseAgentRunLog.RecordPlan(planner?.RunId ?? 0, responseContent);

            if (string.IsNullOrEmpty(json))
            {
                logCallback?.Invoke($"[Assistant] {responseContent}");
                onComplete?.Invoke(true, responseContent);
                return;
            }

            messages.Add(ChatMessage.Assistant(responseContent));

            // 1. Try to run as a stateful script
            if (TryExecuteScript(planner, messages, json, options, logCallback, onComplete))
            {
                return;
            }

            // 2. Fallback: Parse and run flat calls sequentially
            ExecuteFlatCalls(planner, messages, json, options, logCallback, onComplete);
        }

        private static bool TryExecuteScript(
            SynapseLlmPlanner planner, 
            List<ChatMessage> messages, 
            string json, 
            ChatOptions options, 
            Action<string> logCallback, 
            Action<bool, string> onComplete)
        {
            // A response that never claimed to be a script should fall through quietly, but one
            // that carries a "steps" array and then fails to run is a real problem. Without this
            // distinction a malformed script was silently downgraded to the flat-call path and
            // its steps were dropped with nothing in the log to say so.
            bool looksLikeScript = json.IndexOf("\"steps\"", StringComparison.OrdinalIgnoreCase) >= 0;

            try
            {
                var script = JsonConvert.DeserializeObject<SynapseScript>(json);

                if (looksLikeScript && script != null)
                {
                    if (string.IsNullOrEmpty(script.scriptName))
                    {
                        logCallback?.Invoke("[Script] Rejected: the response has steps but no scriptName. Falling back to flat calls, so those steps will not run.");
                        SynapseLogger.Warning($"Script rejected (missing scriptName): {Excerpt(json)}");
                        return false;
                    }

                    if (script.steps == null || script.steps.Count == 0)
                    {
                        logCallback?.Invoke($"[Script] Rejected: '{script.scriptName}' declares no steps. Falling back to flat calls.");
                        SynapseLogger.Warning($"Script rejected (no steps): {Excerpt(json)}");
                        return false;
                    }
                }

                if (script != null && !string.IsNullOrEmpty(script.scriptName) && script.steps != null && script.steps.Count > 0)
                {
                    bool allowMutating = AllowMutating(planner);
                    SynapseGameComponent.Enqueue(() =>
                    {
                        var scriptLog = new List<string>();
                        SynapseScriptRunner.StartScript(script, msg =>
                        {
                            logCallback?.Invoke(msg);
                            scriptLog.Add(msg);
                            // Per-step results, error payloads included, flow to the
                            // inspector as they happen — not only after the script ends.
                            SynapseAgentRunLog.RecordAction(planner?.RunId ?? 0, msg);
                        }, () =>
                        {
                            // Long script logs become excerpt + handle: the model rarely needs
                            // every line, and on a small window it cannot afford them.
                            string scriptOutcome = SynapseResultStore.AbbreviateIfLarge(string.Join("\n", scriptLog), 1200);
                            SynapseAgentRunLog.RecordOutcome(planner?.RunId ?? 0, scriptOutcome);
                            messages.Add(ChatMessage.User($"Script execution finished. Logs:\n{scriptOutcome}\n\nPlease review and either generate next tool calls/script, or respond with a final summary if done."));
                            planner.RunAgentLoop(options);
                        }, allowMutatingTools: allowMutating);
                    });
                    return true;
                }
            }
            catch (Exception ex)
            {
                // Only worth reporting when the payload was trying to be a script. Anything else
                // is an ordinary flat-call response that simply does not deserialise as one.
                if (looksLikeScript)
                {
                    // Type name in brackets rather than "Type: message" — the latter reads as a
                    // thrown-exception report to log scrapers, and this is a handled warning.
                    logCallback?.Invoke($"[Script] Malformed script, falling back to flat calls: {ex.Message}");
                    SynapseLogger.Warning($"Malformed script [{ex.GetType().Name}] {ex.Message} | payload: {Excerpt(json)}");
                }
            }
            return false;
        }

        /// <summary>
        /// Player-initiated runs may always mutate; autonomous runs only when the player
        /// has enabled it in settings.
        /// </summary>
        private static bool AllowMutating(SynapseLlmPlanner planner)
        {
            if (planner == null || !planner.IsAutonomous) return true;
            return RimSynapseMod.Instance?.Settings?.allowAutonomousMutations ?? false;
        }

        /// <summary>Short, single-line excerpt of a payload for diagnostics.</summary>
        private static string Excerpt(string json, int max = 300)
        {
            if (string.IsNullOrEmpty(json)) return "<empty>";
            string oneLine = json.Replace("\r", " ").Replace("\n", " ").Trim();
            return oneLine.Length <= max ? oneLine : oneLine.Substring(0, max) + "...";
        }

        private static void ExecuteFlatCalls(
            SynapseLlmPlanner planner, 
            List<ChatMessage> messages, 
            string json, 
            ChatOptions options, 
            Action<string> logCallback, 
            Action<bool, string> onComplete)
        {
            GodModeResponse response = null;
            try
            {
                response = JsonConvert.DeserializeObject<GodModeResponse>(json);
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"[Error] Failed to deserialize calls: {ex.Message}");
                onComplete?.Invoke(false, $"Failed to deserialize calls: {ex.Message}");
                return;
            }

            if (response == null || response.calls == null || response.calls.Count == 0)
            {
                SynapseAgentRunLog.RecordOutcome(planner?.RunId ?? 0, "[] (No actions resolved)");
                messages.Add(ChatMessage.User("Execution outcomes of the action plan:\n[] (No actions resolved)\n\nPlease review and either generate next tool calls, or respond with a final summary if done."));
                planner.RunAgentLoop(options);
                return;
            }

            SynapseGameComponent.Enqueue(() =>
            {
                var outcomes = new List<object>();

                foreach (var call in response.calls)
                {
                    logCallback?.Invoke($"[Executing] Call: {call.tool} with args: {JsonConvert.SerializeObject(call.arguments)}");
                    string outcome = "";
                    try
                    {
                        string argsJson = JsonConvert.SerializeObject(call.arguments);
                        outcome = SynapseToolRegistry.ExecuteTool(call.tool, argsJson, AllowMutating(planner));
                        logCallback?.Invoke($"[Result] {outcome}");

                        // Oversized results travel back to the model as excerpt + handle.
                        outcome = SynapseResultStore.AbbreviateIfLarge(outcome);
                    }
                    catch (Exception ex)
                    {
                        outcome = $"{{\"success\": false, \"reason\": \"Execution threw exception: {ex.Message}\"}}";
                        logCallback?.Invoke($"[Error] {outcome}");
                    }

                    outcomes.Add(new
                    {
                        tool = call.tool,
                        arguments = call.arguments,
                        result = outcome
                    });
                    SynapseAgentRunLog.RecordAction(planner?.RunId ?? 0, $"{call.tool}: {outcome}");
                }

                string outcomesJson = JsonConvert.SerializeObject(outcomes);
                SynapseAgentRunLog.RecordOutcome(planner?.RunId ?? 0, outcomesJson);
                string followUpPrompt = $@"Execution outcomes of the action plan:
{outcomesJson}

Please review the results. If you need to perform more actions, output a new JSON calls block or script.
If everything is done, output your final summary.";

                messages.Add(ChatMessage.User(followUpPrompt));
                planner.RunAgentLoop(options);
            });
        }
    }
}
