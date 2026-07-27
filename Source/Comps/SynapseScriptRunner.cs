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

            /// <summary>
            /// Set on restore from a save. The next Tick re-anchors the wait timeout (or
            /// continues execution) instead of comparing against a stale waitStartTick.
            /// </summary>
            public bool pendingResume = false;
        }

        /// <summary>
        /// What was persisted for one active script. Scripts already travel as JSON (that is
        /// the wire format the model emits), so persistence serialises this with Newtonsoft
        /// into a single scribed string rather than scribing each field.
        /// </summary>
        private class PersistedScript
        {
            public SynapseScript script;
            public int currentStepIndex;
            public bool isWaiting;
            public string waitCondition;
            public string waitPawnName;
            public int waitRemainingTicks;
            public Dictionary<string, string> results;
            public bool allowMutatingTools = true;
        }

        /// <summary>Read-only view of one active script, for persistence tests and the debugger.</summary>
        public class SynapseScriptState
        {
            public string name;
            public int currentStep;
            public int totalSteps;
            public bool isWaiting;
            public string waitCondition;
            public int remainingWaitTicks;
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

        public static List<SynapseScriptState> GetActiveScriptStates()
        {
            int now = Find.TickManager?.TicksGame ?? 0;
            var list = new List<SynapseScriptState>();
            foreach (var a in _activeScripts)
            {
                list.Add(new SynapseScriptState
                {
                    name = a.script?.scriptName ?? "Unnamed",
                    currentStep = a.currentStepIndex + 1,
                    totalSteps = a.script?.steps?.Count ?? 0,
                    isWaiting = a.isWaiting,
                    waitCondition = a.waitCondition,
                    remainingWaitTicks = !a.isWaiting ? 0
                        : a.pendingResume ? a.waitTimeoutTicks
                        : Math.Max(0, a.waitTimeoutTicks - (now - a.waitStartTick)),
                });
            }
            return list;
        }

        /// <summary>
        /// Serialise every active script for the save, or null when there are none (so
        /// nothing is written and pre-feature saves stay shape-identical). Any script in
        /// the list is normally mid-wait — StartScript runs synchronously until a wait or
        /// completion — but currentStepIndex is persisted regardless.
        /// </summary>
        public static string SnapshotForSave()
        {
            if (_activeScripts.Count == 0) return null;

            int now = Find.TickManager?.TicksGame ?? 0;
            var persisted = new List<PersistedScript>();
            foreach (var a in _activeScripts)
            {
                persisted.Add(new PersistedScript
                {
                    script = a.script,
                    currentStepIndex = a.currentStepIndex,
                    isWaiting = a.isWaiting,
                    waitCondition = a.waitCondition,
                    waitPawnName = a.waitPawnName,
                    // Remaining, not absolute: TicksGame in the loaded game continues from
                    // the save, but re-anchoring from remaining time is what keeps a restored
                    // wait from expiring instantly if anything shifts the clock.
                    waitRemainingTicks = a.isWaiting
                        ? Math.Max(1, a.waitTimeoutTicks - (now - a.waitStartTick))
                        : 0,
                    results = a.results.Count > 0 ? new Dictionary<string, string>(a.results) : null,
                    allowMutatingTools = a.allowMutatingTools,
                });
            }

            try
            {
                return JsonConvert.SerializeObject(persisted);
            }
            catch (Exception ex)
            {
                SynapseLogger.Warning($"[Script Runner] Could not serialise {persisted.Count} active script(s) for the save: [{ex.GetType().Name}] {ex.Message}", "performance");
                return null;
            }
        }

        /// <summary>
        /// Restore scripts persisted by <see cref="SnapshotForSave"/>. Returns how many were
        /// restored; null/empty/unreadable input is a no-op (a pre-feature save has no entry
        /// at all and lands here as null).
        ///
        /// The continuation decision (issue #20): the script itself resumes, but the agent
        /// chain does not. logCallback and onFinished are closures over the old process's
        /// planner state and cannot be serialised, so a restored script logs through
        /// SynapseLogger and finishes without a continuation — and says so, once, at restore.
        /// Persisting the whole agent conversation was rejected: the game state the
        /// conversation reasoned about is stale after a load, so a "resumed" agent would be
        /// acting on a world that no longer matches its history.
        /// </summary>
        public static int RestoreFromSave(string json)
        {
            if (string.IsNullOrEmpty(json)) return 0;

            List<PersistedScript> persisted;
            try
            {
                persisted = JsonConvert.DeserializeObject<List<PersistedScript>>(json);
            }
            catch (Exception ex)
            {
                SynapseLogger.Warning($"[Script Runner] Persisted scripts in the save could not be read: [{ex.GetType().Name}] {ex.Message}", "performance");
                return 0;
            }
            if (persisted == null || persisted.Count == 0) return 0;

            int restored = 0;
            foreach (var p in persisted)
            {
                if (p?.script?.steps == null || p.script.steps.Count == 0) continue;

                var active = new ActiveScript
                {
                    script = p.script,
                    currentStepIndex = Math.Max(0, Math.Min(p.currentStepIndex, p.script.steps.Count - 1)),
                    isWaiting = p.isWaiting,
                    waitCondition = p.waitCondition,
                    waitPawnName = p.waitPawnName,
                    waitTimeoutTicks = Math.Max(1, p.waitRemainingTicks),
                    waitStartTick = 0,
                    allowMutatingTools = p.allowMutatingTools,
                    pendingResume = true,
                    logCallback = line => SynapseLogger.Message(line, "performance"),
                    onFinished = null,
                };
                if (p.results != null)
                {
                    foreach (var kv in p.results) active.results[kv.Key] = kv.Value;
                }

                _activeScripts.Add(active);
                restored++;
                SynapseLogger.Message(
                    $"[Script Runner] Restored script '{active.script.scriptName}' at step {active.currentStepIndex + 1}/{active.script.steps.Count} from save. Its agent chain was interrupted by the save and will not resume; further output goes to the log.",
                    "performance");
            }
            return restored;
        }

        /// <summary>
        /// Drop every active script before a game loads. The old game's scripts (and their
        /// agent chains) belong to a world that is being replaced; onFinished is deliberately
        /// NOT invoked — those closures reference state mid-teardown, and the planner's own
        /// run limits bound any chain left dangling.
        /// </summary>
        public static void ClearForLoad()
        {
            if (_activeScripts.Count == 0) return;
            SynapseLogger.Message($"[Script Runner] Discarding {_activeScripts.Count} active script(s) from the previous session.", "performance");
            _activeScripts.Clear();
        }

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

            // Normalise legacy aliases up front (each rewrite is logged), then check the
            // script against the declared step schema. A script that fails validation is
            // refused with every error named — onFinished still runs, so an agent chain
            // receives the errors through its normal feedback and can correct the shape.
            SynapseScriptValidator.NormalizeAliases(script, logCallback);
            var errors = SynapseScriptValidator.Validate(script, logCallback);
            if (errors.Count > 0)
            {
                logCallback?.Invoke($"[Script Runner] Script '{script.scriptName}' rejected — {errors.Count} validation error(s):");
                foreach (var error in errors)
                {
                    logCallback?.Invoke($"[Script Runner]   {error}");
                }
                SynapseLogger.Warning($"Script '{script.scriptName}' rejected: {string.Join(" | ", errors)}", "performance");
                try
                {
                    onFinished?.Invoke();
                }
                catch (Exception ex)
                {
                    logCallback?.Invoke($"[Error] onFinished after rejection failed: {ex.Message}");
                }
                return;
            }

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
                // First tick after a restore: re-anchor the wait against the loaded game's
                // clock (waitTimeoutTicks already holds the remaining time), or pick the
                // script back up if it was somehow persisted between steps.
                if (active.pendingResume)
                {
                    active.pendingResume = false;
                    if (active.isWaiting)
                    {
                        active.waitStartTick = currentTick;
                        active.logCallback?.Invoke($"[Script Runner] Script '{active.script?.scriptName}' resumed: wait timeout re-anchored with {active.waitTimeoutTicks} ticks remaining.");
                    }
                    else
                    {
                        active.logCallback?.Invoke($"[Script Runner] Script '{active.script?.scriptName}' resumed at step {active.currentStepIndex + 1}.");
                        ExecuteNextStep(active);
                        continue;
                    }
                }

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

                // Aliases were normalised (and logged) by the validator at StartScript.

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
