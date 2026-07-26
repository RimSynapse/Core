using System;
using System.Collections.Generic;
using RimSynapse.Models;

namespace RimSynapse
{
    /// <summary>
    /// Handles the multi-turn conversation memory, prompt construction, and LLM requests for natural language actions.
    /// </summary>
    public class SynapseLlmPlanner
    {
        private const int MaxTurns = 5;
        private readonly string _command;
        private readonly Action<string> _logCallback;
        private readonly Action<bool, string> _onComplete;
        private readonly List<ChatMessage> _messages;
        private int _turnsCount = 0;

        public SynapseLlmPlanner(string command, Action<string> logCallback, Action<bool, string> onComplete)
        {
            _command = command;
            _logCallback = logCallback;
            _onComplete = onComplete;
            _messages = new List<ChatMessage>();
        }

        public void Start()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("You are the RimWorld Synapse Storyteller Mode action resolver.");
            sb.AppendLine("The user will input an instruction in plain English. Your job is to translate this instruction into sequential game actions.");
            sb.AppendLine();
            sb.AppendLine("You operate as a stateful, planning-based agent across a multi-turn loop.");
            sb.AppendLine();
            sb.AppendLine("You MUST follow this exact 3-step reasoning lifecycle:");
            sb.AppendLine("1. **PLAN**: Brainstorm the logical requirements to fulfill the user's prompt. Identify constraints and dependencies (e.g. 'To make Dole commit suicide, he needs a weapon. I must first check his inventory, and search the map for weapons in case he has none. Then I will direct him to equip the best weapon, wait for it, and trigger the suicide.').");
            sb.AppendLine("2. **QUERY**: On your first turn, output ONLY search/query calls (like 'search_map_entities') to locate target pawns, items, or weapons on the map.");
            sb.AppendLine("3. **ASSEMBLE & EXECUTE**: Once you receive the search results:");
            sb.AppendLine("   - Analyze the active game state (e.g. proximity of weapons, pawn inventories).");
            sb.AppendLine("   - If the action is immediate and single-tick (e.g. modify skill level, change weather, trigger incident), output a flat JSON list of 'calls'.");
            sb.AppendLine("   - If the action is sequential or time-delayed (e.g. walk to a weapon, wait to equip it, and then perform an action), you MUST output a stateful JSON 'script'.");
            sb.AppendLine("   - CRITICAL WARNING: Do NOT combine movement/equipping and dependent actions in a flat 'calls' list! Flat 'calls' execute in the exact same game tick, so a dependent action (like 'damage_self_with_equipped') will execute and fail immediately before the pawn can reach the item! You must use a 'script' with a 'wait_until' step for these sequences.");
            sb.AppendLine("   - Enforce thematic and immersive flavor text: use descriptive 'commandName' values.");
            sb.AppendLine();
            sb.AppendLine("CRITICAL CONSTRAINTS:");
            sb.AppendLine("- NEVER use 'send_notification_letter' in scripts or tool calls. RimWorld's vanilla engine naturally posts letter alerts when pawns die or events trigger. Creating letters programmatically will cause duplicate or false notifications.");
            sb.AppendLine("- NEVER use 'modify_pawn_state' with action='kill' to resolve suicide, combat, or murder instructions. You must simulate the actions naturally (e.g. by equipping a weapon and calling 'damage_self_with_equipped'). The 'kill' action is a developer-only debugging override and must never be used to cheat or bypass pathing/weapons checks.");
            sb.AppendLine();
            sb.AppendLine("Format for immediate calls:");
            sb.AppendLine("{");
            sb.AppendLine("  \"calls\": [");
            sb.AppendLine("    {");
            sb.AppendLine("      \"tool\": \"modify_pawn_state\",");
            sb.AppendLine("      \"arguments\": {");
            sb.AppendLine("        \"thingId\": \"Pawn_Cow123\",");
            sb.AppendLine("        \"action\": \"kill\"");
            sb.AppendLine("      }");
            sb.AppendLine("    }");
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("Format for stateful scripts (supports sequential execution and 'wait_until' delays):");
            sb.AppendLine("{");
            sb.AppendLine("  \"scriptName\": \"Dole's Tragic End\",");
            sb.AppendLine("  \"steps\": [");
            sb.AppendLine("    {");
            sb.AppendLine("      \"type\": \"possess_colonist\",");
            sb.AppendLine("      \"arguments\": {");
            sb.AppendLine("        \"pawnName\": \"Dole\",");
            sb.AppendLine("        \"action\": \"clear\",");
            sb.AppendLine("        \"commandName\": \"Overcome with grief\"");
            sb.AppendLine("      }");
            sb.AppendLine("    },");
            sb.AppendLine("    {");
            sb.AppendLine("      \"type\": \"possess_colonist\",");
            sb.AppendLine("      \"arguments\": {");
            sb.AppendLine("        \"pawnName\": \"Dole\",");
            sb.AppendLine("        \"action\": \"equip\",");
            sb.AppendLine("        \"targetItemName\": \"shotgun\"");
            sb.AppendLine("      }");
            sb.AppendLine("    },");
            sb.AppendLine("    {");
            sb.AppendLine("      \"type\": \"wait_until\",");
            sb.AppendLine("      \"arguments\": {");
            sb.AppendLine("        \"condition\": \"has_weapon\",");
            sb.AppendLine("        \"pawnName\": \"Dole\",");
            sb.AppendLine("        \"timeoutTicks\": 6000");
            sb.AppendLine("      }");
            sb.AppendLine("    },");
            sb.AppendLine("    {");
            sb.AppendLine("      \"type\": \"damage_self_with_equipped\",");
            sb.AppendLine("      \"arguments\": {");
            sb.AppendLine("        \"pawnName\": \"Dole\",");
            sb.AppendLine("        \"targetBodyPart\": \"Head\",");
            sb.AppendLine("        \"damageMultiplier\": 4.0");
            sb.AppendLine("      }");
            sb.AppendLine("    }");
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("IMPORTANT: If you have finished all actions or if there is nothing left to do, output a friendly natural language summary of your actions without any JSON block.");
            sb.AppendLine();
            sb.AppendLine("Available Tools (output them inside your JSON calls or steps):");

            // Compile the list of tools to send using relevance-based RAG matching
            var coreTools = new List<string> { 
                "search_map_entities", 
                "search_game_definitions", 
                "possess_colonist", 
                "damage_self_with_equipped", 
                "list_available_tools"
            };

            bool enableCheating = RimSynapseMod.Instance?.Settings?.enableCheatingActions ?? false;
            if (enableCheating)
            {
                coreTools.Add("modify_pawn_state");
            }

            var nonCoreScoredTools = new List<KeyValuePair<double, GameTool>>();

            foreach (var tool in SynapseToolRegistry.AllTools)
            {
                if (tool.name == "execute_game_tool")
                    continue;

                // Hide debug/cheating tools if disabled
                if (tool.isDebugAction && !enableCheating)
                    continue;

                if (coreTools.Contains(tool.name))
                    continue;

                // Build a quick string representation of parameters for RAG indexing
                var paramsSb = new System.Text.StringBuilder();
                try
                {
                    var parametersJObj = Newtonsoft.Json.Linq.JObject.FromObject(tool.parameters);
                    if (parametersJObj != null && parametersJObj.TryGetValue("properties", out var propsToken) && propsToken is Newtonsoft.Json.Linq.JObject propsObj)
                    {
                        foreach (var prop in propsObj)
                        {
                            paramsSb.Append(" ").Append(prop.Key);
                            if (prop.Value is Newtonsoft.Json.Linq.JObject pDetail && pDetail.TryGetValue("description", out var dToken))
                            {
                                paramsSb.Append(" ").Append(dToken.ToString());
                            }
                        }
                    }
                }
                catch {}

                double score = CalculateToolScore(_command, tool, paramsSb.ToString());
                if (score > 0)
                {
                    nonCoreScoredTools.Add(new KeyValuePair<double, GameTool>(score, tool));
                }
            }

            // Sort non-core tools descending by relevance score
            nonCoreScoredTools.Sort((a, b) => b.Key.CompareTo(a.Key));

            // Select only the tools to describe (6 Core + up to 6 top scoring Non-Core tools, or all non-core tools if matched count is 5 or less)
            var finalToolsList = new List<GameTool>();
            
            // Add core tools first
            foreach (var tool in SynapseToolRegistry.AllTools)
            {
                if (coreTools.Contains(tool.name))
                {
                    // Double check debug action criteria
                    if (tool.isDebugAction && !enableCheating)
                        continue;

                    finalToolsList.Add(tool);
                }
            }

            // Append top non-core tools or fallback to all non-core tools
            if (nonCoreScoredTools.Count <= 5)
            {
                foreach (var tool in SynapseToolRegistry.AllTools)
                {
                    if (tool.name != "execute_game_tool" && !coreTools.Contains(tool.name))
                    {
                        if (tool.isDebugAction && !enableCheating)
                            continue;

                        if (!finalToolsList.Contains(tool))
                        {
                            finalToolsList.Add(tool);
                        }
                    }
                }
            }
            else
            {
                for (int i = 0; i < Math.Min(6, nonCoreScoredTools.Count); i++)
                {
                    finalToolsList.Add(nonCoreScoredTools[i].Value);
                }
            }

            // Format the list for the system prompt
            foreach (var tool in finalToolsList)
            {
                sb.AppendLine($"- **{tool.name}**: {tool.description}");
                try
                {
                    var parametersJObj = Newtonsoft.Json.Linq.JObject.FromObject(tool.parameters);
                    if (parametersJObj != null && parametersJObj.TryGetValue("properties", out var propsToken) && propsToken is Newtonsoft.Json.Linq.JObject propsObj)
                    {
                        foreach (var prop in propsObj)
                        {
                            string pName = prop.Key;
                            string pType = "string";
                            string pDesc = "";
                            if (prop.Value is Newtonsoft.Json.Linq.JObject pDetail)
                            {
                                if (pDetail.TryGetValue("type", out var tToken)) pType = tToken.ToString();
                                if (pDetail.TryGetValue("description", out var dToken)) pDesc = dToken.ToString();
                            }
                            sb.AppendLine($"  * {pName} ({pType}): {pDesc}");
                        }
                    }
                }
                catch (Exception)
                {
                    // Fallback to name if serialization fails
                }
            }

            string systemPrompt = sb.ToString();

            _messages.Add(ChatMessage.System(systemPrompt));
            _messages.Add(ChatMessage.User($"User Instruction: {_command}"));

            var options = new ChatOptions { priority = 10, requestName = "API Command Resolver" };
            RunAgentLoop(options);
        }

        private double CalculateToolScore(string command, GameTool tool, string paramsText)
        {
            double score = 0;
            string cmdLower = command.ToLower();
            string toolName = tool.name;
            string nameLower = toolName.ToLower();
            string descLower = tool.description.ToLower();
            string paramsLower = paramsText.ToLower();

            char[] splitChars = new char[] { ' ', ',', '.', '!', '?', ';', ':', '-', '_', '(', ')' };
            string[] words = cmdLower.Split(splitChars, StringSplitOptions.RemoveEmptyEntries);

            foreach (var word in words)
            {
                if (word.Length <= 2 || word == "the" || word == "and" || word == "for" || word == "with" || word == "give" || word == "make" || word == "have")
                    continue;

                // Exact word match in tool name gets high priority
                if (nameLower == word) score += 15.0;
                else if (nameLower.Contains(word)) score += 5.0;

                // Match in description
                if (descLower.Contains(word)) score += 3.0;

                // Match in parameter lists
                if (paramsLower.Contains(word)) score += 2.0;
            }

            // Apply Dynamic Extensible Keywords Boost:
            if (tool.keywords != null)
            {
                foreach (var kw in tool.keywords)
                {
                    if (cmdLower.Contains(kw.ToLower()))
                    {
                        score += 12.0;
                    }
                }
            }

            // Apply Semantic Association Boost Rules:
            
            // 1. Possessive / Pawn Pronouns -> Boost Pawn modification and possession tools
            if (cmdLower.Contains("his") || cmdLower.Contains("her") || cmdLower.Contains("their") || 
                cmdLower.Contains("him") || cmdLower.Contains("them") || cmdLower.Contains("she") || 
                cmdLower.Contains("he") || cmdLower.Contains("self") || cmdLower.Contains("own") ||
                cmdLower.Contains("pawn") || cmdLower.Contains("colonist") || cmdLower.Contains("animal"))
            {
                if (toolName == "modify_pawn_state" || toolName == "possess_colonist" || toolName == "damage_self_with_equipped")
                    score += 10.0;
            }

            // 2. Action / Job Verbs -> Boost possession tools
            if (cmdLower.Contains("equip") || cmdLower.Contains("prioritize") || cmdLower.Contains("walk") || 
                cmdLower.Contains("go") || cmdLower.Contains("move") || cmdLower.Contains("build") || 
                cmdLower.Contains("mine") || cmdLower.Contains("harvest") || cmdLower.Contains("clean") || 
                cmdLower.Contains("haul") || cmdLower.Contains("repair") || cmdLower.Contains("work") || 
                cmdLower.Contains("job") || cmdLower.Contains("construct"))
            {
                if (toolName == "possess_colonist")
                    score += 10.0;
            }

            // 3. Combat / Harm words -> Boost combat self-harm and damage tools
            if (cmdLower.Contains("kill") || cmdLower.Contains("damage") || cmdLower.Contains("hurt") || 
                cmdLower.Contains("die") || cmdLower.Contains("suicide") || cmdLower.Contains("stab") || 
                cmdLower.Contains("shoot") || cmdLower.Contains("fire") || cmdLower.Contains("attack") || 
                cmdLower.Contains("wound") || cmdLower.Contains("bleed"))
            {
                if (toolName == "modify_pawn_state" || toolName == "damage_self_with_equipped")
                    score += 12.0;
            }

            // 4. Incident / Threat words -> Boost incident and spawner tools
            if (cmdLower.Contains("raid") || cmdLower.Contains("mechanoid") || cmdLower.Contains("threat") || 
                cmdLower.Contains("infestation") || cmdLower.Contains("spawn") || cmdLower.Contains("incident") ||
                cmdLower.Contains("event"))
            {
                if (toolName == "spawn_incident" || toolName == "trigger_raid" || toolName == "spawn_threat")
                    score += 15.0;
            }

            // 5. Environment words -> Boost weather and time tools
            if (cmdLower.Contains("weather") || cmdLower.Contains("time") || cmdLower.Contains("rain") || 
                cmdLower.Contains("storm") || cmdLower.Contains("sun") || cmdLower.Contains("day") || 
                cmdLower.Contains("night") || cmdLower.Contains("snow") || cmdLower.Contains("fog"))
            {
                if (toolName == "set_weather" || toolName == "set_time")
                    score += 15.0;
            }

            return score;
        }

        public void RunAgentLoop(ChatOptions options)
        {
            _turnsCount++;
            if (_turnsCount > MaxTurns)
            {
                _logCallback?.Invoke("[Warning] Maximum execution turns reached. Terminating loop.");
                _onComplete?.Invoke(false, "Max turns reached without final summary.");
                return;
            }

            _logCallback?.Invoke($"[API Agent] Invoking LLM (Turn {_turnsCount})...");

            // Keep the history inside the class operating point. Agent traffic records under
            // the "custom" class, so its budget is measurement-driven like everything else.
            var op = SynapseTierController.GetOperatingPoint("custom");
            int estimate = SynapseAgentHistory.CompactToBudget(_messages, op.MaxPromptTokens, _logCallback);
            SynapseLogger.Message(
                $"[Agent] Turn {_turnsCount}: prompt ~{estimate} tokens (cap {op.MaxPromptTokens}, governed by {op.GovernedBy}).",
                "performance");

            var request = new LlmTextRequest
            {
                Messages = _messages,
                SystemPrompt = "",
                EnforceJson = false,
                Tools = new List<GameToolDefinition>() // Clear native tool array to prevent local provider crashes/bloat
            };

            SynapseClient.SendTextAsync(
                RimSynapseMod.ModHandle,
                request,
                options,
                result =>
                {
                    if (!result.success)
                    {
                        _logCallback?.Invoke($"[Error] LLM request failed on turn {_turnsCount}: {result.error}");
                        _onComplete?.Invoke(false, $"LLM request failed: {result.error}");
                        return;
                    }

                    SynapseActionExecutor.ProcessResponse(this, _messages, result.content, options, _logCallback, _onComplete);
                }
            );
        }
    }
}
