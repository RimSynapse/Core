using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;
using RimSynapse.Utils;
using RimSynapse.Models;
using Newtonsoft.Json;

namespace RimSynapse.Comps
{
    /// <summary>
    /// LLM query methods for storyteller pacing adjustment and event selection.
    /// Each method sends an async request to the LLM, with optional two-pass tool usage.
    /// </summary>
    public static partial class SynapseStorytellerOpportunistic
    {
        public static bool TriggerPacingAdjustment()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) return false;
            if (Find.Storyteller?.storytellerComps?.OfType<StorytellerComp_Storyteller>().Any() != true) return false;

            var map = Find.CurrentMap;
            string metrics = GetColonyDetailedMetrics(map);
            
            var coreComp = Find.World.GetComponent<SynapseCoreWorldComponent>();
            string recentEvents = BuildRecentEventsText(coreComp);

            var props = StorytellerComp_Storyteller.GetActiveStorytellerProps();
            string systemPrompt = BuildPacingSystemPrompt(props);

            string userMessage = $@"Colony Status:
{metrics}

Recent Events:
{recentEvents}

Analyze the situation and provide the PacingMultiplier and CategoryMultipliers.";

            var request = new LlmTextRequest
            {
                SystemPrompt = systemPrompt,
                Messages = new List<ChatMessage> { ChatMessage.User(userMessage) },
                EnforceJson = true,
                Tools = null // Keep pass 1 extremely small and fast
            };

            PauseForTelemetry();
            SynapseClient.SendTextAsync(
                RimSynapseMod.ModHandle,
                request,
                new ChatOptions { queryId = "storyteller_pacing", priority = 1, requestName = "Storyteller Pacing", targetName = "Colony" },
                result =>
                {
                    bool runSecondPass = false;
                    try
                    {
                        if (result.success)
                        {
                            string json = JsonHelper.ExtractJson(result.content);
                            if (json != null)
                            {
                                var parsed = JsonConvert.DeserializeObject<PacingAdjustmentResult>(json);
                                if (parsed != null)
                                {
                                    if (parsed.RequestTools != null && parsed.RequestTools.Count > 0)
                                    {
                                        runSecondPass = true;
                                        RunPacingSecondPass(systemPrompt, userMessage, parsed.RequestTools);
                                    }
                                    else
                                    {
                                        ApplyPacingAdjustment(parsed);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        RimSynapse.SynapseLogger.Warn("core", $"[RimSynapse-Core] Failed to parse pacing response: {ex.Message}");
                    }
                    finally
                    {
                        if (!runSecondPass)
                        {
                            ResumeAfterTelemetry();
                        }
                    }
                }
            );

            return true;
        }

        private static void RunPacingSecondPass(string systemPrompt, string userMessage, List<string> requestedTools)
        {
            RimSynapse.SynapseLogger.Message($"[RimSynapse-Core] Storyteller Pacing requested tools: {string.Join(", ", requestedTools)}. Running second pass...");

            var secondRequest = new LlmTextRequest
            {
                SystemPrompt = systemPrompt + "\n\nYou requested tools: " + string.Join(", ", requestedTools) + ". Call them directly now to retrieve the necessary info, and then return the final pacing JSON output.",
                Messages = new List<ChatMessage> { ChatMessage.User(userMessage) },
                EnforceJson = true,
                Tools = SynapseToolRegistry.AllTools
                    .Where(t => requestedTools.Contains(t.name))
                    .Select(t => new GameToolDefinition
                    {
                        name = t.name,
                        description = t.description,
                        parameters = t.parameters
                    }).ToList()
            };

            SynapseClient.SendTextAsync(
                RimSynapseMod.ModHandle,
                secondRequest,
                new ChatOptions { queryId = "storyteller_pacing_pass2", priority = 1, requestName = "Storyteller Pacing Pass 2", targetName = "Colony" },
                secondResult =>
                {
                    try
                    {
                        if (secondResult.success)
                        {
                            string secondJson = JsonHelper.ExtractJson(secondResult.content);
                            if (secondJson != null)
                            {
                                var secondParsed = JsonConvert.DeserializeObject<PacingAdjustmentResult>(secondJson);
                                ApplyPacingAdjustment(secondParsed);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        RimSynapse.SynapseLogger.Warn("core", $"[RimSynapse-Core] Failed to parse pacing second pass: {ex.Message}");
                    }
                    finally
                    {
                        ResumeAfterTelemetry();
                    }
                }
            );
        }

        public static void TriggerEventSelection(IncidentCategoryDef category, IIncidentTarget target)
        {
            if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) return;

            var map = Find.CurrentMap;
            string metrics = GetColonyDetailedMetrics(map);
            
            var coreWorldComp = Find.World.GetComponent<SynapseCoreWorldComponent>();
            string recentEvents = BuildRecentEventsText(coreWorldComp);

            var props = StorytellerComp_Storyteller.GetActiveStorytellerProps();
            
            // Build the list of allowed incidents that can fire now
            string allowedIncidentsList = BuildAllowedIncidentsList(category, target, props, map, coreWorldComp, out var activeContextNotes);

            string narrativeContext = "";
            if (activeContextNotes.Any())
            {
                narrativeContext = "\nNarrative Context Notes:\n" + string.Join("\n", activeContextNotes.Select(n => "- " + n)) + "\n";
            }

            string systemPrompt = BuildEventSelectionSystemPrompt(category, allowedIncidentsList, props);

            // Tell the selector that whatever it picks arrives on a delay (the deferred-news pipeline).
            string deferNote = SynapseDeferredNewsComponent.PromptNote();
            if (!string.IsNullOrEmpty(deferNote)) systemPrompt += "\n\n" + deferNote;

            string userMessage = $@"Colony Status:
{metrics}

Recent Events:
{recentEvents}
{narrativeContext}
Provide the incident def name.";

            var request = new LlmTextRequest
            {
                SystemPrompt = systemPrompt,
                Messages = new List<ChatMessage> { ChatMessage.User(userMessage) },
                EnforceJson = true,
                Tools = null // Keep pass 1 extremely small and fast
            };

            PauseForTelemetry();
            SynapseClient.SendTextAsync(
                RimSynapseMod.ModHandle,
                request,
                new ChatOptions { queryId = "storyteller_event_selection", priority = 10, requestName = "Storyteller Event Selection", targetName = category.defName },
                result =>
                {
                    if (coreWorldComp != null)
                    {
                        coreWorldComp.GlobalPacingMultiplier = coreWorldComp.BasePacingMultiplier;
                    }

                    bool runSecondPass = false;
                    try
                    {
                        if (result.success)
                        {
                            string json = JsonHelper.ExtractJson(result.content);
                            if (json != null)
                            {
                                var parsed = JsonConvert.DeserializeObject<EventSelectionResult>(json);
                                if (parsed != null)
                                {
                                    if (parsed.RequestTools != null && parsed.RequestTools.Count > 0)
                                    {
                                        runSecondPass = true;
                                        RunEventSelectionSecondPass(systemPrompt, userMessage, parsed.RequestTools, category, target);
                                    }
                                    else
                                    {
                                        ApplyEventSelection(parsed, target);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        RimSynapse.SynapseLogger.Warn("core", $"[RimSynapse-Core] Failed to parse event selection: {ex.Message}");
                    }
                    finally
                    {
                        if (!runSecondPass)
                        {
                            ResumeAfterTelemetry();
                        }
                    }
                }
            );
        }

        private static void RunEventSelectionSecondPass(string systemPrompt, string userMessage, List<string> requestedTools, IncidentCategoryDef category, IIncidentTarget target)
        {
            RimSynapse.SynapseLogger.Message($"[RimSynapse-Core] Storyteller Event Selection requested tools: {string.Join(", ", requestedTools)}. Running second pass...");

            var secondRequest = new LlmTextRequest
            {
                SystemPrompt = systemPrompt + "\n\nYou requested tools: " + string.Join(", ", requestedTools) + ". Call them directly now to retrieve the necessary info, and then return the final incident selection JSON.",
                Messages = new List<ChatMessage> { ChatMessage.User(userMessage) },
                EnforceJson = true,
                Tools = SynapseToolRegistry.AllTools
                    .Where(t => requestedTools.Contains(t.name))
                    .Select(t => new GameToolDefinition
                    {
                        name = t.name,
                        description = t.description,
                        parameters = t.parameters
                    }).ToList()
            };

            SynapseClient.SendTextAsync(
                RimSynapseMod.ModHandle,
                secondRequest,
                new ChatOptions { queryId = "storyteller_event_selection_pass2", priority = 10, requestName = "Storyteller Event Selection Pass 2", targetName = category.defName },
                secondResult =>
                {
                    try
                    {
                        if (secondResult.success)
                        {
                            string secondJson = JsonHelper.ExtractJson(secondResult.content);
                            if (secondJson != null)
                            {
                                var secondParsed = JsonConvert.DeserializeObject<EventSelectionResult>(secondJson);
                                ApplyEventSelection(secondParsed, target);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        RimSynapse.SynapseLogger.Warn("core", $"[RimSynapse-Core] Failed to parse event selection second pass: {ex.Message}");
                    }
                    finally
                    {
                        ResumeAfterTelemetry();
                    }
                }
            );
        }

        private static string BuildRecentEventsText(SynapseCoreWorldComponent coreComp)
        {
            if (coreComp == null) return "None recently.";
            int maxBudget = RimSynapseMod.Instance?.Settings?.maxPacingContextTokens ?? 4096;
            int eventCount = Math.Max(2, maxBudget / 800);
            var events = coreComp.GetRecentEvents(eventCount);
            if (!events.Any()) return "None recently.";

            return string.Join("\n", events.Select(e =>
            {
                string desc = !string.IsNullOrEmpty(e.mcpTag) ? $"- {e.mcpTag}" : $"- {e.eventDescription}";
                if (e.isResolved)
                {
                    desc += $" ({e.outcome})";
                }
                return desc;
            }));
        }

        private static string BuildAllowedIncidentsList(IncidentCategoryDef category, IIncidentTarget target,
            StorytellerCompProperties_Storyteller props, Map map, SynapseCoreWorldComponent coreWorldComp,
            out List<string> activeContextNotes)
        {
            activeContextNotes = new List<string>();
            var incidentLines = new List<string>();

            foreach (var def in DefDatabase<IncidentDef>.AllDefs)
            {
                if (def.category != category) continue;

                bool canFire = false;
                try
                {
                    IncidentParms parms = StorytellerUtility.DefaultParmsNow(category, target);
                    canFire = def.Worker.CanFireNow(parms);
                }
                catch { }
                if (!canFire) continue;

                float weight = 1.0f;
                var weightConfig = props?.incidentWeights?.FirstOrDefault(w => w.incidentDefName == def.defName);
                if (weightConfig != null)
                {
                    weight = weightConfig.baseWeight;
                    if (weightConfig.rules != null)
                    {
                        foreach (var rule in weightConfig.rules)
                        {
                            if (EvaluateRule(rule, map, coreWorldComp))
                            {
                                weight *= rule.multiplier;
                                if (!string.IsNullOrEmpty(rule.contextNote) && !activeContextNotes.Contains(rule.contextNote))
                                {
                                    activeContextNotes.Add(rule.contextNote);
                                }
                            }
                        }
                    }
                }
                else if (props != null)
                {
                    if (category == IncidentCategoryDefOf.ThreatBig) weight = props.baseWeightThreatBig;
                    else if (category == IncidentCategoryDefOf.ThreatSmall) weight = props.baseWeightThreatSmall;
                    else if (category == IncidentCategoryDefOf.DiseaseHuman) weight = props.baseWeightDiseaseHuman;
                    else if (category == IncidentCategoryDefOf.Misc) weight = props.baseWeightMisc;
                    else if (category.defName == "DiseaseAnimal") weight = props.baseWeightDiseaseAnimal;
                    else if (category.defName == "OrbitalVisitor") weight = props.baseWeightOrbitalVisitor;
                    else if (category.defName == "FactionArrival") weight = props.baseWeightFactionArrival;
                }
                
                string desc = weightConfig?.description ?? "A standard " + category.defName + " event.";
                incidentLines.Add("- '" + def.defName + "' (Base Weight: " + weight.ToString("F1") + "): " + desc);
            }

            return incidentLines.Any() ? string.Join("\n", incidentLines) : "None available.";
        }

        private static string BuildPacingSystemPrompt(StorytellerCompProperties_Storyteller props)
        {
            string systemPrompt = props?.pacingSystemPrompt;
            if (!string.IsNullOrEmpty(systemPrompt)) return systemPrompt;

            string characterName = props?.characterName ?? Find.Storyteller?.def?.label ?? "AI Storyteller";
            string speakingStyle = props?.speakingStyle ?? "sassy, dramatic, or menacing";
            bool useTools = RimSynapseMod.Instance?.Settings?.enableStorytellerTools == true;
            string toolInstruction = "";
            if (useTools)
            {
                string toolsList = GetToolsTextList();
                toolInstruction = @"
You have access to tools that query the live state of the colony. If you need more details to decide pacing (e.g. colonist profiles/skills, exact stockpiles, colony moods, or active map threats), you should request them.
Available tools to query:
" + toolsList + @"

If you have enough information to decide pacing, return a JSON object with 'PacingMultiplier' and 'CategoryMultipliers'.
If you need more details to make the decision, return a JSON object containing ONLY 'RequestTools' (a JSON array of the tool names you want to run), e.g.
{
  ""RequestTools"": [""get_colonists_profile"", ""get_active_threats""]
}
";
            }

            return @"You are the " + characterName + @" Pacing and Weighting Coordinator.
Your writing style is " + speakingStyle + @".
Your role is to orchestrate the colony's challenge level and dynamic pacing based on its current successes, setbacks, and resources.
" + toolInstruction + @"
You must evaluate:
1. Successes/Triumphs (e.g. repelled raids, completed quests) -> Increase challenge (more ThreatBig/ThreatSmall, higher pacing).
2. Failures/Tragedies (e.g. dead colonists, burned buildings, kidnapped pawns) -> Soften the blow (lower pacing, decrease ThreatBig, increase Misc/FactionArrival for traders and helpers).
3. Resource state (low combat capability, low food, low silver) -> Trigger friendly events (traders, wanderers) or easy quests. High wealth but low defense -> motivated raids.
4. Legendary Art: Legendary art pieces are renowned world attractions. If the colony has legendary art pieces, it draws visitors and affluent guests. Increase the probability/likelihood of friendly visitors, trade caravans, and affluent travelers ('FactionArrival' and 'Misc') proportionally. Scale the visitor frequency and wealth based on the number and value of legendary art pieces and total colony wealth.
5. Local Population Density (Pawn dwellings): High population density indicates a civilized, protected region near city centers. In high density areas, favor pawn joins, travelers, and caravans (Misc, FactionArrival) and significantly reduce hostile raids/threats (ThreatBig, ThreatSmall). Low population density (remote frontier) is lawless and dangerous; in low density areas, increase the likelihood of raids (ThreatBig, ThreatSmall) and reduce positive join/wanderer events (Misc).

If you have enough information, return a JSON object containing:
- 'PacingMultiplier': Float. Standard is 1.0. Increase (>1.0) to speed up event frequency. Decrease (<1.0) to give the colony breathing room.
- 'CategoryMultipliers': Dictionary of category def names (e.g. 'ThreatBig', 'ThreatSmall', 'Misc', 'DiseaseHuman', 'FactionArrival') to float multipliers. Standard is 1.0. Increase to make that type of event more likely, decrease to make it less likely.

You MUST respond strictly in valid JSON format:
{
  ""PacingMultiplier"": 1.0,
  ""CategoryMultipliers"": {
    ""ThreatBig"": 1.0,
    ""ThreatSmall"": 1.0,
    ""Misc"": 1.0,
    ""DiseaseHuman"": 1.0,
    ""FactionArrival"": 1.0
  }
}";
        }

        private static string BuildEventSelectionSystemPrompt(IncidentCategoryDef category, string allowedIncidentsList, StorytellerCompProperties_Storyteller props)
        {
            string systemPrompt = props?.selectionSystemPrompt;
            if (!string.IsNullOrEmpty(systemPrompt))
            {
                return systemPrompt.Replace("{allowedIncidentsList}", allowedIncidentsList);
            }

            string characterName = props?.characterName ?? Find.Storyteller?.def?.label ?? "AI Storyteller";
            string speakingStyle = props?.speakingStyle ?? "sassy, dramatic, or menacing";
            bool useTools = RimSynapseMod.Instance?.Settings?.enableStorytellerTools == true;
            string toolInstruction = "";
            if (useTools)
            {
                string toolsList = GetToolsTextList();
                toolInstruction = @"
You have access to tools that query the live state of the colony. If you need more details to select the best incident (e.g. checking what weapons they have before sending a raid, checking food stockpiles before toxic fallout, checking their mood to decide if a mental break or trade caravan is better), you should request them.
Available tools to query:
" + toolsList + @"

If you have enough information to decide pacing, return a JSON object with 'IncidentDefName'.
If you need more details to make the decision, return a JSON object containing ONLY 'RequestTools' (a JSON array of the tool names you want to run), e.g.
{
  ""RequestTools"": [""get_colonists_profile"", ""get_active_threats""]
}
";
            }

            return @"You are the " + characterName + @" Event Selector.
Your writing style is " + speakingStyle + @".
An event trigger has occurred for category: " + category.defName + @".
You must pick the EXACT IncidentDefName from the list of allowed incidents below that fits the current narrative best.
Use the base weights as a reference for how common or rare they should be, but let narrative pacing guide the final choice.
" + toolInstruction + @"
ALLOWED INCIDENTS FOR CATEGORY " + category.defName + @":
" + allowedIncidentsList + @"

Legendary Art Attraction: If the colony has legendary art pieces (reported in metrics), choose friendly visitors, guest groups, and affluent/wealthy traders more frequently to simulate them visiting to admire the art. If colony wealth is also high, attract more affluent or exotic traders.

Civilization and Population Density context: If local population density is high (civilized lands), choose peaceful, urban, or civilized incidents (e.g. wanderers, caravans, peace talks) and avoid wild threats like raw infestations or animal stampedes. If density is low (isolated frontier wilderness), favor rogue raiders, manhunters, or harsh environmental challenges fitting a lawless outpost.

If you have enough information, return a JSON object containing:
{
  ""IncidentDefName"": ""(The exact def name of the incident)""
}";
        }
    }
}
