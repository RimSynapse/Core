using System;
using System.Collections.Generic;
using System.Linq;

namespace RimSynapse
{
    /// <summary>
    /// Declares the shape of script steps and checks scripts against it before they run.
    ///
    /// Steps are stored as (type, arguments-dictionary) because that is the wire format the
    /// model emits; the typing lives here, in one place, instead of scattered TryGetValue
    /// calls and a silent alias block inside the execution loop.
    ///
    /// Strictness is deliberately asymmetric:
    ///  - structural steps (wait_until, call_tool) are OURS — an unknown field is rejected,
    ///    naming the step and the field, because a typo there silently changes behaviour;
    ///  - tool-step arguments are checked against the tool's declared schema as WARNINGS
    ///    only — registered schemas are not all complete, and hard rejection would break
    ///    legacy shapes that must keep running. Tools already validate their own inputs and
    ///    answer with error payloads.
    ///
    /// The legacy alias shim lives here too, applied up front and logged per rewrite, so
    /// drift toward old shapes is visible instead of silently absorbed mid-execution.
    /// </summary>
    public static class SynapseScriptValidator
    {
        /// <summary>Fields the wait_until step accepts. The schema of record.</summary>
        public static readonly string[] WaitUntilFields = { "condition", "pawnName", "timeoutTicks" };

        /// <summary>Fields the call_tool step accepts.</summary>
        public static readonly string[] CallToolFields = { "tool", "arguments", "resultKey" };

        /// <summary>Step metadata accepted on any tool step, stripped before the tool sees the args.</summary>
        public static readonly string[] ToolStepMetaFields = { "resultKey" };

        /// <summary>
        /// Apply the legacy alias rewrites (equip_item/equip_weapon/equip, damage_self,
        /// clear_queue/stop_movement), logging each one. Kept for compatibility; the
        /// published schema (#22) means new scripts should not need it, and the log lines
        /// are the measure of whether the model has stopped emitting old shapes.
        /// </summary>
        public static int NormalizeAliases(SynapseScript script, Action<string> log)
        {
            if (script?.steps == null) return 0;
            int rewrites = 0;

            for (int i = 0; i < script.steps.Count; i++)
            {
                var step = script.steps[i];
                if (step?.type == null) continue;
                string original = step.type;

                if (Is(step, "equip_item") || Is(step, "equip_weapon") || Is(step, "equip"))
                {
                    step.type = "possess_colonist";
                    step.arguments = step.arguments ?? new Dictionary<string, object>();
                    step.arguments["action"] = "equip";
                    Rename(step, "weaponName", "targetItemName");
                    Rename(step, "itemName", "targetItemName");
                    Rename(step, "weaponDef", "targetItemDef");
                    Rename(step, "itemDef", "targetItemDef");
                    if (!step.arguments.ContainsKey("commandName")) step.arguments["commandName"] = "Equipping Weapon";
                }
                else if (Is(step, "damage_self"))
                {
                    step.type = "damage_self_with_equipped";
                }
                else if (Is(step, "clear_queue") || Is(step, "stop_movement"))
                {
                    step.type = "possess_colonist";
                    step.arguments = step.arguments ?? new Dictionary<string, object>();
                    step.arguments["action"] = "clear";
                    if (!step.arguments.ContainsKey("commandName")) step.arguments["commandName"] = "Stopping Command";
                }

                if (!ReferenceEquals(original, step.type) && original != step.type)
                {
                    rewrites++;
                    log?.Invoke($"[Script Runner] Step {i + 1}: alias '{original}' rewritten to '{step.type}' (legacy shape — prefer the declared schema).");
                }
            }
            return rewrites;
        }

        /// <summary>
        /// Validate a script (after alias normalisation). Returns rejection errors; softer
        /// findings are emitted through the log callback as warnings.
        /// </summary>
        public static List<string> Validate(SynapseScript script, Action<string> log)
        {
            return Validate(script, log, toolScope: null);
        }

        /// <summary>
        /// Validate a script under a tool-vocabulary scope (Core #63). A step whose verb is
        /// outside the scope is a hard rejection error — the scope is a capability boundary,
        /// so it belongs with the structural checks, not the warn-only argument checks.
        /// Null scope skips the vocabulary check (unscoped, pre-existing behaviour).
        /// </summary>
        public static List<string> Validate(SynapseScript script, Action<string> log, string toolScope)
        {
            var errors = new List<string>();
            if (script?.steps == null) return errors;

            for (int i = 0; i < script.steps.Count; i++)
            {
                var step = script.steps[i];
                int n = i + 1;

                if (step == null) continue;
                if (string.IsNullOrEmpty(step.type))
                {
                    errors.Add($"Step {n}: missing 'type'.");
                    continue;
                }

                var args = step.arguments;

                if (Is(step, "wait_until"))
                {
                    CheckUnknownFields(errors, n, "wait_until", args, WaitUntilFields);
                    if (args == null || !args.ContainsKey("condition"))
                        errors.Add($"Step {n} (wait_until): missing required field 'condition'.");
                    if (args != null && args.TryGetValue("timeoutTicks", out var t) && t != null
                        && !int.TryParse(t.ToString(), out _))
                        errors.Add($"Step {n} (wait_until): 'timeoutTicks' must be an integer, got '{t}'.");
                    continue;
                }

                if (Is(step, "call_tool"))
                {
                    CheckUnknownFields(errors, n, "call_tool", args, CallToolFields);
                    if (args == null || !args.TryGetValue("tool", out var toolName) || string.IsNullOrEmpty(toolName?.ToString()))
                        errors.Add($"Step {n} (call_tool): missing required field 'tool'.");
                    else if (!SynapseToolVocabulary.IsAllowed(toolScope, toolName.ToString()))
                        errors.Add($"Step {n} (call_tool): tool '{toolName}' is not in the '{toolScope}' vocabulary.");
                    continue;
                }

                if (!SynapseToolVocabulary.IsAllowed(toolScope, step.type))
                {
                    errors.Add($"Step {n}: tool '{step.type}' is not in the '{toolScope}' vocabulary.");
                    continue;
                }

                // Tool step: warnings only, against the tool's declared schema.
                var tool = SynapseToolRegistry.AllTools.FirstOrDefault(
                    t => string.Equals(t.name, step.type, StringComparison.OrdinalIgnoreCase));
                if (tool == null) continue; // unknown tools are reported at execution time

                var declared = DeclaredProperties(tool);
                if (declared.Count == 0 || args == null) continue;

                foreach (var key in args.Keys)
                {
                    if (ToolStepMetaFields.Contains(key)) continue;
                    if (!declared.Contains(key))
                    {
                        log?.Invoke($"[Script Runner] Step {n} ({tool.name}): argument '{key}' is not in the tool's declared schema — it may be ignored.");
                    }
                }
            }

            return errors;
        }

        private static void CheckUnknownFields(List<string> errors, int stepNumber, string stepType,
            Dictionary<string, object> args, string[] allowed)
        {
            if (args == null) return;
            foreach (var key in args.Keys)
            {
                if (!allowed.Contains(key))
                {
                    errors.Add($"Step {stepNumber} ({stepType}): unknown field '{key}' — allowed: {string.Join(", ", allowed)}.");
                }
            }
        }

        private static HashSet<string> DeclaredProperties(GameTool tool)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var parametersJObj = Newtonsoft.Json.Linq.JObject.FromObject(tool.parameters);
                if (parametersJObj != null
                    && parametersJObj.TryGetValue("properties", out var propsToken)
                    && propsToken is Newtonsoft.Json.Linq.JObject propsObj)
                {
                    foreach (var prop in propsObj) set.Add(prop.Key);
                }
            }
            catch { }
            return set;
        }

        private static bool Is(SynapseScriptStep step, string type)
            => step.type.Equals(type, StringComparison.OrdinalIgnoreCase);

        private static void Rename(SynapseScriptStep step, string from, string to)
        {
            if (step.arguments.TryGetValue(from, out var v)) step.arguments[to] = v;
        }
    }
}
