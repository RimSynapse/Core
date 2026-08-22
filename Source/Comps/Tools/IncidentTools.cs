using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Newtonsoft.Json;

namespace RimSynapse
{
    /// <summary>
    /// Tool handlers: get_available_incidents and fire_incident.
    /// Provides incident browsing and direct incident firing for the storyteller.
    /// </summary>
    public static partial class SynapseToolRegistry
    {
        private static void RegisterIncidentTools()
        {
            // Get Available Incidents Tool
            RegisterTool(
                "get_available_incidents",
                "Get the list of all storyteller incidents available to be fired, including their def names, base weights, description, and modder-supplied thematic guides (narrative context guidelines).",
                new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["compact"] = new Dictionary<string, object>
                        {
                            ["type"] = "boolean",
                            ["description"] = "If true, returns extremely compact details to save token costs by omitting description and thematicGuide text."
                        }
                    }
                },
                args =>
                {
                    bool compact = false;
                    try
                    {
                        var dict = JsonConvert.DeserializeObject<Dictionary<string, object>>(args);
                        if (dict != null && dict.TryGetValue("compact", out var val) && val is bool b)
                        {
                            compact = b;
                        }
                    }
                    catch {}

                    var props = RimSynapse.Comps.StorytellerComp_Storyteller.GetActiveStorytellerProps();
                    var list = new List<object>();

                    foreach (var def in DefDatabase<IncidentDef>.AllDefs)
                    {
                        if (def.category == null) continue;

                        float baseWeight = 1.0f;
                        var config = props?.incidentWeights?.FirstOrDefault(w => w.incidentDefName == def.defName);
                        if (config != null)
                        {
                            baseWeight = config.baseWeight;
                        }

                        if (compact)
                        {
                            list.Add(new
                            {
                                def = def.defName,
                                cat = def.category.defName,
                                weight = baseWeight
                            });
                        }
                        else
                        {
                            string desc = def.description ?? "";
                            string guide = "";
                            if (config != null)
                            {
                                guide = config.thematicGuide ?? "";
                                if (string.IsNullOrEmpty(desc)) desc = config.description ?? "";
                            }

                            list.Add(new
                            {
                                incidentDefName = def.defName,
                                category = def.category.defName,
                                baseWeight = baseWeight,
                                description = desc,
                                thematicGuide = guide
                            });
                        }
                    }

                    return JsonConvert.SerializeObject(list);
                }
            );

            // Fire Incident Tool
            RegisterTool(
                "fire_incident",
                "Fire a specific game incident immediately on the player's map.",
                new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["incidentDefName"] = new Dictionary<string, string>
                        {
                            ["type"] = "string",
                            ["description"] = "The defName of the incident to fire (e.g. RaidEnemy, Infestation, CropBlight)."
                        },
                        ["pointsOverride"] = new Dictionary<string, string>
                        {
                            ["type"] = "number",
                            ["description"] = "Optional: Override the threat points difficulty (e.g. 500). If omitted, standard calculated threat points are used."
                        }
                    },
                    ["required"] = new List<string> { "incidentDefName" }
                },
                args =>
                {
                    if (Find.CurrentMap == null) return "{\"success\": false, \"reason\": \"No active map loaded.\"}";

                    try
                    {
                        var parsedArgs = JsonConvert.DeserializeObject<Dictionary<string, object>>(args);
                        if (parsedArgs == null || !parsedArgs.TryGetValue("incidentDefName", out var nameVal))
                        {
                            return "{\"success\": false, \"reason\": \"Missing required argument: incidentDefName.\"}";
                        }

                        string defName = nameVal?.ToString();
                        IncidentDef incidentDef = DefDatabase<IncidentDef>.GetNamedSilentFail(defName);
                        if (incidentDef == null)
                        {
                            return "{\"success\": false, \"reason\": \"IncidentDef '" + defName + "' not found.\"}";
                        }

                        float requestedPoints = 0f;
                        if (parsedArgs.TryGetValue("pointsOverride", out var ptsVal) && ptsVal != null)
                        {
                            float.TryParse(ptsVal.ToString(), out requestedPoints);
                        }

                        IncidentParms parms = new IncidentParms();
                        parms.target = Find.CurrentMap;

                        // The difficulty ceiling: the same number the storyteller budgets
                        // with (dynamic when the RimSynapse comp is present, vanilla otherwise).
                        float ceiling;
                        var comp = Find.World.GetComponent<SynapseCoreWorldComponent>();
                        if (comp != null)
                        {
                            float vanillaPoints = StorytellerUtility.DefaultThreatPointsNow(parms.target);
                            ceiling = comp.CalculateDynamicThreatPoints(Find.CurrentMap, vanillaPoints);
                        }
                        else
                        {
                            ceiling = StorytellerUtility.DefaultThreatPointsNow(parms.target);
                        }

                        // A vocabulary-scoped caller (the Storyteller agent) is clamped to the
                        // ceiling; an unscoped caller (operator console, dev tools) keeps its
                        // override untouched (Core #63).
                        parms.points = SynapseToolVocabulary.ResolvePoints(requestedPoints, ceiling, SynapseToolRegistry.CurrentScope);
                        if (requestedPoints > 0f && parms.points < requestedPoints)
                        {
                            SynapseLogger.Message($"[Tool Vocabulary] fire_incident points clamped from {requestedPoints:F0} to the difficulty ceiling {parms.points:F0} for scope '{SynapseToolRegistry.CurrentScope}'.", "performance");
                        }

                        parms.forced = true;

                        bool success = false;
                        success = incidentDef.Worker.TryExecute(parms);

                        if (success)
                        {
                            comp?.RegisterFiredIncident(defName);
                        }

                        return JsonConvert.SerializeObject(new
                        {
                            success = success,
                            message = success ? $"Fired {defName} successfully with {parms.points:F0} points." : $"Incident worker for {defName} returned false."
                        });
                    }
                    catch (Exception ex)
                    {
                        return $"{{\"success\": false, \"reason\": \"Crash during execution: {ex.Message}\"}}";
                    }
                },
                true
            );

            // Send Notification Letter Tool
            RegisterTool(
                "send_notification_letter",
                "Post a letter notification alert on the player's screen. Perfect for storyteller announcements, death notices, or faction notifications.",
                new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["title"] = new Dictionary<string, string>
                        {
                            ["type"] = "string",
                            ["description"] = "The title header of the letter (e.g. 'Death: Fred')."
                        },
                        ["text"] = new Dictionary<string, string>
                        {
                            ["type"] = "string",
                            ["description"] = "The detailed text body of the letter."
                        },
                        ["letterType"] = new Dictionary<string, string>
                        {
                            ["type"] = "string",
                            ["description"] = "The style/type of letter: 'positive', 'negative', 'death', 'neutral' (default: 'neutral')."
                        },
                        ["pawnName"] = new Dictionary<string, string>
                        {
                            ["type"] = "string",
                            ["description"] = "Optional: Name of a pawn to target and center the camera on when the letter is double-clicked."
                        }
                    },
                    ["required"] = new List<string> { "title", "text" }
                },
                args =>
                {
                    try
                    {
                        var parsedArgs = JsonConvert.DeserializeObject<Dictionary<string, object>>(args);
                        if (parsedArgs == null || !parsedArgs.TryGetValue("title", out var tVal) || !parsedArgs.TryGetValue("text", out var txVal))
                        {
                            return "{\"success\": false, \"reason\": \"Missing title or text.\"}";
                        }

                        string title = tVal?.ToString();
                        string text = txVal?.ToString();
                        string letterType = parsedArgs.TryGetValue("letterType", out var ltVal) ? ltVal?.ToString()?.ToLower() : "neutral";
                        string pawnName = parsedArgs.TryGetValue("pawnName", out var pVal) ? pVal?.ToString() : null;

                        LetterDef letterDef = LetterDefOf.NeutralEvent;
                        if (letterType == "death") letterDef = LetterDefOf.Death;
                        else if (letterType == "negative") letterDef = LetterDefOf.NegativeEvent;
                        else if (letterType == "positive") letterDef = LetterDefOf.PositiveEvent;

                        Pawn lookTarget = null;
                        if (Find.CurrentMap != null && !string.IsNullOrEmpty(pawnName))
                        {
                            lookTarget = Find.CurrentMap.mapPawns.AllPawns.FirstOrDefault(p => p.LabelShort.Equals(pawnName, StringComparison.OrdinalIgnoreCase));
                        }

                        Find.LetterStack.ReceiveLetter(title, text, letterDef, lookTarget);
                        return "{\"success\": true, \"message\": \"Letter posted successfully.\"}";
                    }
                    catch (Exception ex)
                    {
                        return $"{{\"success\": false, \"reason\": \"Failed to post letter: {ex.Message}\"}}";
                    }
                },
                true
            );
        }
    }
}
