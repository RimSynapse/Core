using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;
using Newtonsoft.Json;

namespace RimSynapse
{
    public class SynapseScript
    {
        public string scriptName;
        public List<SynapseScriptStep> steps;
    }

    public class SynapseScriptStep
    {
        public string type;
        public Dictionary<string, object> arguments;
    }

    public static class SynapseScriptRunner
    {
        private class ActiveScript
        {
            public SynapseScript script;
            public int currentStepIndex = 0;
            public bool isWaiting = false;
            public string waitCondition = null;
            public string waitPawnName = null;
            public int waitTimeoutTicks = 0;
            public int waitStartTick = 0;
            public Action<string> logCallback;
            public Action onFinished;

            /// <summary>Results kept by a step's resultKey, surfaced in the completion log.</summary>
            public readonly Dictionary<string, string> results = new Dictionary<string, string>();

            /// <summary>Whether this script's tool steps may mutate game state.</summary>
            public bool allowMutatingTools = true;
        }

        private static readonly List<ActiveScript> _activeScripts = new List<ActiveScript>();
        private static readonly List<ActiveScript> _toRemove = new List<ActiveScript>();
        
        private static readonly Dictionary<string, Func<Pawn, Dictionary<string, object>, bool>> _customConditions = 
            new Dictionary<string, Func<Pawn, Dictionary<string, object>, bool>>(StringComparer.OrdinalIgnoreCase);

        public static void RegisterWaitCondition(string conditionName, Func<Pawn, Dictionary<string, object>, bool> evaluator)
        {
            _customConditions[conditionName] = evaluator;
        }

        /// <summary>
        /// Compact step reference for the system prompt. Owned here because the runner
        /// defines step semantics; includes dynamically registered wait conditions so
        /// companion-added conditions are discoverable without prompt edits.
        /// </summary>
        public static string DescribeStepSchema()
        {
            var conditions = new List<string>
            {
                "has_weapon", "has_ranged_weapon", "has_any_weapon", "reached_cell", "pawn_downed"
            };
            foreach (var name in _customConditions.Keys)
            {
                if (!conditions.Contains(name)) conditions.Add(name);
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Script step reference:");
            sb.AppendLine("- Any tool name can be a step \"type\"; its \"arguments\" follow that tool's schema (inspect with describe_tool). Unknown tool names are reported and skipped.");
            sb.AppendLine("- \"call_tool\": run a tool named in arguments — { \"tool\": \"<name>\", \"arguments\": { ... } }. Use when a tool's name collides with a step keyword.");
            sb.AppendLine($"- \"wait_until\": pause until a condition holds — {{ \"condition\": \"<name>\", \"pawnName\": \"<pawn>\", \"timeoutTicks\": 6000 }}. Conditions: {string.Join(", ", conditions)}. On timeout the script continues to the next step.");
            sb.AppendLine("- Any tool step may include \"resultKey\": \"<label>\" to store its result; stored and oversized results are retrieved later with get_stored_result.");
            sb.AppendLine("Use a script when actions are sequential, wait on game state, or span time. Use flat \"calls\" only for immediate same-tick actions.");
            return sb.ToString();
        }

        /// <summary>
        /// Abort the first active script with the given name. onFinished still runs, so a
        /// cancelled agent chain resolves through its normal path (where the planner's
        /// cancel flag then reports the cancellation) instead of waiting forever.
        /// </summary>
        public static bool AbortScript(string scriptName)
        {
            var active = _activeScripts.FirstOrDefault(a =>
                string.Equals(a.script?.scriptName, scriptName, StringComparison.OrdinalIgnoreCase));
            if (active == null) return false;

            _activeScripts.Remove(active);
            active.logCallback?.Invoke($"[Script Runner] Script '{scriptName}' aborted at step {active.currentStepIndex + 1}.");
            try
            {
                active.onFinished?.Invoke();
            }
            catch (Exception ex)
            {
                active.logCallback?.Invoke($"[Error] onFinished after abort failed: {ex.Message}");
            }
            return true;
        }

        public static int ActiveScriptsCount => _activeScripts.Count;

        public static List<string> GetActiveScriptNames()
        {
            var list = new List<string>();
            foreach (var s in _activeScripts)
            {
                list.Add(s.script?.scriptName ?? "Unnamed");
            }
            return list;
        }

        // Binary-compatible original signature; the gating variant is a separate overload.
        public static void StartScript(SynapseScript script, Action<string> logCallback, Action onFinished = null)
        {
            StartScript(script, logCallback, onFinished, allowMutatingTools: true);
        }

        public static void StartScript(SynapseScript script, Action<string> logCallback, Action onFinished, bool allowMutatingTools)
        {
            if (script == null || script.steps == null || script.steps.Count == 0) return;

            var active = new ActiveScript
            {
                script = script,
                currentStepIndex = 0,
                logCallback = logCallback,
                onFinished = onFinished,
                allowMutatingTools = allowMutatingTools
            };
            
            _activeScripts.Add(active);
            logCallback?.Invoke($"[Script Runner] Starting script '{script.scriptName}' with {script.steps.Count} steps.");
            ExecuteNextStep(active);
        }

        public static void Tick()
        {
            if (_activeScripts.Count == 0) return;
            if (Find.TickManager == null) return;

            int currentTick = Find.TickManager.TicksGame;
            _toRemove.Clear();

            // We make a copy of active scripts to iterate safely in case the collection changes during execution
            var listCopy = _activeScripts.ToList();
            foreach (var active in listCopy)
            {
                if (active.isWaiting)
                {
                    bool conditionMet = false;
                    bool timeout = (currentTick - active.waitStartTick) >= active.waitTimeoutTicks;

                    if (!timeout)
                    {
                        var step = active.script.steps[active.currentStepIndex];
                        conditionMet = CheckCondition(active.waitCondition, active.waitPawnName, step.arguments);
                    }

                    if (conditionMet)
                    {
                        active.logCallback?.Invoke($"[Script Runner] Condition '{active.waitCondition}' met for '{active.waitPawnName}'. Resuming script.");
                        active.isWaiting = false;
                        active.currentStepIndex++;
                        ExecuteNextStep(active);
                    }
                    else if (timeout)
                    {
                        active.logCallback?.Invoke($"[Script Runner] Warning: Condition '{active.waitCondition}' timed out after {active.waitTimeoutTicks} ticks. Skipping step.");
                        active.isWaiting = false;
                        active.currentStepIndex++;
                        ExecuteNextStep(active);
                    }
                }
            }

            foreach (var remove in _toRemove)
            {
                _activeScripts.Remove(remove);
            }
        }

        /// <summary>
        /// Runs a step as a tool call.
        ///
        /// A step type is a tool name: anything not handled as an alias or as wait_until is
        /// dispatched to <see cref="SynapseToolRegistry.ExecuteTool"/>. An explicit "call_tool"
        /// step names the tool in its arguments instead, which is the only way to reach a tool
        /// whose name collides with wait_until or one of the aliases rewritten above.
        ///
        /// The registry answers an unknown tool with an error payload rather than throwing, so
        /// the name is checked first — otherwise a mistyped step logs that payload as ordinary
        /// output and the script carries on as though it worked.
        /// </summary>
        private static void ExecuteToolStep(ActiveScript active, SynapseScriptStep step)
        {
            int stepNumber = active.currentStepIndex + 1;
            var args = step.arguments ?? new Dictionary<string, object>();

            string toolName = step.type;
            string resultKey = null;

            if (step.type.Equals("call_tool", StringComparison.OrdinalIgnoreCase))
            {
                toolName = args.TryGetValue("tool", out var t) ? t?.ToString() : null;
                if (string.IsNullOrEmpty(toolName))
                {
                    active.logCallback?.Invoke($"[Error] Step {stepNumber}: call_tool needs a 'tool' argument naming the tool to run.");
                    return;
                }

                // Nested arguments belong to the tool; anything else here is step metadata.
                if (args.TryGetValue("arguments", out var inner) && inner != null)
                {
                    try { args = JsonConvert.DeserializeObject<Dictionary<string, object>>(JsonConvert.SerializeObject(inner)) ?? new Dictionary<string, object>(); }
                    catch { args = new Dictionary<string, object>(); }
                }
                else
                {
                    args = new Dictionary<string, object>();
                }
            }

            if (step.arguments != null && step.arguments.TryGetValue("resultKey", out var rk))
            {
                resultKey = rk?.ToString();
                args.Remove("resultKey");
            }

            if (!SynapseToolRegistry.IsToolRegistered(toolName))
            {
                active.logCallback?.Invoke($"[Error] Step {stepNumber}: unknown tool '{toolName}'. Skipping.");
                return;
            }

            active.logCallback?.Invoke($"[Script Runner] Executing step {stepNumber}: {toolName}");
            try
            {
                string result = SynapseToolRegistry.ExecuteTool(toolName, JsonConvert.SerializeObject(args), active.allowMutatingTools);

                // Handlers report their own failures in the payload, so a returned error is not
                // an ordinary result and should not read like one.
                if (!string.IsNullOrEmpty(result) && result.IndexOf("\"error\"", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    active.logCallback?.Invoke($"[Error] Step {stepNumber} ({toolName}) reported: {result}");
                }
                else
                {
                    active.logCallback?.Invoke($"[Result] {result}");
                }

                if (!string.IsNullOrEmpty(resultKey))
                {
                    active.results[resultKey] = result;
                    active.logCallback?.Invoke($"[Script Runner] Stored result as '{resultKey}'.");
                }
            }
            catch (Exception ex)
            {
                active.logCallback?.Invoke($"[Error] Step {stepNumber} ({toolName}) threw: {ex.Message}");
            }
        }

        private static void ExecuteNextStep(ActiveScript active)
        {
            while (active.currentStepIndex < active.script.steps.Count && !active.isWaiting)
            {
                var step = active.script.steps[active.currentStepIndex];
                if (step == null)
                {
                    active.currentStepIndex++;
                    continue;
                }

                // Alias Normalization
                if (step.type.Equals("equip_item", StringComparison.OrdinalIgnoreCase) || 
                    step.type.Equals("equip_weapon", StringComparison.OrdinalIgnoreCase) ||
                    step.type.Equals("equip", StringComparison.OrdinalIgnoreCase))
                {
                    step.type = "possess_colonist";
                    if (step.arguments == null) step.arguments = new Dictionary<string, object>();
                    step.arguments["action"] = "equip";
                    if (step.arguments.TryGetValue("weaponName", out var wn)) step.arguments["targetItemName"] = wn;
                    if (step.arguments.TryGetValue("itemName", out var itn)) step.arguments["targetItemName"] = itn;
                    if (step.arguments.TryGetValue("weaponDef", out var wd)) step.arguments["targetItemDef"] = wd;
                    if (step.arguments.TryGetValue("itemDef", out var itd)) step.arguments["targetItemDef"] = itd;
                    if (!step.arguments.ContainsKey("commandName")) step.arguments["commandName"] = "Equipping Weapon";
                }
                else if (step.type.Equals("damage_self", StringComparison.OrdinalIgnoreCase))
                {
                    step.type = "damage_self_with_equipped";
                }
                else if (step.type.Equals("clear_queue", StringComparison.OrdinalIgnoreCase) || 
                         step.type.Equals("stop_movement", StringComparison.OrdinalIgnoreCase))
                {
                    step.type = "possess_colonist";
                    if (step.arguments == null) step.arguments = new Dictionary<string, object>();
                    step.arguments["action"] = "clear";
                    if (!step.arguments.ContainsKey("commandName")) step.arguments["commandName"] = "Stopping Command";
                }

                if (step.type.Equals("wait_until", StringComparison.OrdinalIgnoreCase))
                {
                    string condition = step.arguments != null && step.arguments.TryGetValue("condition", out var cVal) ? cVal?.ToString() : null;
                    string pawnName = step.arguments != null && step.arguments.TryGetValue("pawnName", out var pVal) ? pVal?.ToString() : null;
                    int timeout = 3000;
                    if (step.arguments != null && step.arguments.TryGetValue("timeoutTicks", out var tVal) && tVal != null)
                    {
                        int.TryParse(tVal.ToString(), out timeout);
                    }

                    active.isWaiting = true;
                    active.waitCondition = condition;
                    active.waitPawnName = pawnName;
                    active.waitTimeoutTicks = timeout;
                    active.waitStartTick = Find.TickManager.TicksGame;
                    active.logCallback?.Invoke($"[Script Runner] Pausing script. Waiting for condition '{condition}' on pawn '{pawnName}' (Timeout: {timeout} ticks).");
                }
                else
                {
                    ExecuteToolStep(active, step);
                    active.currentStepIndex++;
                }
            }

            if (active.currentStepIndex >= active.script.steps.Count)
            {
                // Emit stored results before the finish line. SynapseActionExecutor hands the
                // whole log back to the agent, so anything a step kept has to appear here to be
                // visible on the next turn.
                if (active.results.Count > 0)
                {
                    foreach (var kv in active.results)
                    {
                        active.logCallback?.Invoke($"[Script Runner] {kv.Key} = {kv.Value}");
                    }
                }

                active.logCallback?.Invoke($"[Script Runner] Script '{active.script.scriptName}' finished.");
                _toRemove.Add(active);
                try
                {
                    active.onFinished?.Invoke();
                }
                catch (Exception ex)
                {
                    active.logCallback?.Invoke($"[Error] onFinished callback failed: {ex.Message}");
                }
            }
        }

        private static bool CheckCondition(string condition, string pawnName, Dictionary<string, object> arguments)
        {
            if (Find.CurrentMap == null) return false;
            
            Pawn pawn = Find.CurrentMap.mapPawns.AllPawns.FirstOrDefault(p => p.LabelShort.Equals(pawnName, StringComparison.OrdinalIgnoreCase));
            if (pawn == null) return false;

            if (_customConditions.TryGetValue(condition, out var customEvaluator))
            {
                try
                {
                    return customEvaluator(pawn, arguments);
                }
                catch (Exception ex)
                {
                    Log.Error($"[RimSynapse] Exception in custom script condition '{condition}': {ex.Message}");
                    return false;
                }
            }

            if (condition.Equals("has_ranged_weapon", StringComparison.OrdinalIgnoreCase))
            {
                return pawn.equipment?.Primary != null && pawn.equipment.Primary.def.IsRangedWeapon;
            }
            else if (condition.Equals("has_equipped_weapon", StringComparison.OrdinalIgnoreCase) || 
                     condition.Equals("has_weapon", StringComparison.OrdinalIgnoreCase) || 
                     condition.Equals("has_any_weapon", StringComparison.OrdinalIgnoreCase))
            {
                return pawn.equipment?.Primary != null;
            }
            else if (condition.Equals("reached_cell", StringComparison.OrdinalIgnoreCase))
            {
                if (arguments != null && arguments.TryGetValue("targetX", out var xVal) && arguments.TryGetValue("targetZ", out var zVal))
                {
                    if (int.TryParse(xVal.ToString(), out int tx) && int.TryParse(zVal.ToString(), out int tz))
                    {
                        var cell = new IntVec3(tx, 0, tz);
                        return pawn.Position.DistanceToSquared(cell) <= 4f || (pawn.CurJob != null && pawn.CurJob.def != JobDefOf.Goto && pawn.Position.DistanceToSquared(cell) <= 9f);
                    }
                }
                return pawn.CurJob == null || pawn.CurJob.def != JobDefOf.Goto;
            }
            else if (condition.Equals("pawn_downed", StringComparison.OrdinalIgnoreCase))
            {
                return pawn.Downed;
            }

            return false;
        }
    }
}
