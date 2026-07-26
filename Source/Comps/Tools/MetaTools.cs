using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Meta-tools: list_available_tools and execute_game_tool.
    /// These let the LLM discover and invoke tools dynamically.
    /// </summary>
    public static partial class SynapseToolRegistry
    {
        private static void RegisterMetaTools()
        {
            // Meta-Tool: list_available_tools
            RegisterTool(
                "list_available_tools",
                "Get the directory of available game tools. Can optionally specify a search query keyword to filter the tools.",
                new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["query"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "Optional search query to filter tool names and descriptions."
                        }
                    }
                },
                args =>
                {
                    string filter = null;
                    try
                    {
                        var parsedArgs = JsonConvert.DeserializeObject<Dictionary<string, object>>(args);
                        if (parsedArgs != null && parsedArgs.TryGetValue("query", out var qVal))
                            filter = qVal?.ToString()?.ToLower();
                    }
                    catch {}

                    var list = new List<object>();
                    foreach (var tool in _tools.Values)
                    {
                        if (tool.isDebugAction || tool.name == "list_available_tools" || tool.name == "execute_game_tool")
                            continue;

                        if (!string.IsNullOrEmpty(filter))
                        {
                            bool nameMatch = tool.name.ToLower().Contains(filter);
                            bool descMatch = tool.description.ToLower().Contains(filter);
                            if (!nameMatch && !descMatch)
                                continue;
                        }

                        list.Add(new
                        {
                            name = tool.name,
                            description = tool.description,
                            parameterSchema = tool.parameters
                        });
                    }
                    return JsonConvert.SerializeObject(list);
                }
            );

            // Meta-Tool: execute_game_tool
            RegisterTool(
                "execute_game_tool",
                "Execute any game tool from the directory by name and passing a JSON arguments string.",
                new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["tool_name"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "The exact name of the tool to execute."
                        },
                        ["arguments_json"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "The JSON string of the arguments to pass. E.g. '{}' if the tool has no parameters."
                        }
                    },
                    ["required"] = new List<string> { "tool_name", "arguments_json" }
                },
                args =>
                {
                    try
                    {
                        var data = JsonConvert.DeserializeObject<Dictionary<string, string>>(args);
                        if (data == null || !data.TryGetValue("tool_name", out string toolName))
                        {
                            return "{\"error\": \"Missing tool_name parameter.\"}";
                        }
                        
                        string subArgs = "{}";
                        if (data.TryGetValue("arguments_json", out string tempArgs))
                        {
                            subArgs = tempArgs;
                        }

                        return ExecuteTool(toolName, subArgs);
                    }
                    catch (Exception ex)
                    {
                        return $"{{\"error\": \"Failed to parse meta-tool arguments: {ex.Message}\"}}";
                    }
                }
            );

            // Developer Tool: set_game_volume
            RegisterTool(
                "set_game_volume",
                "Sets the master or category audio volume preferences of the game instance at runtime.",
                new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["volume"] = new Dictionary<string, object>
                        {
                            ["type"] = "number",
                            ["description"] = "Target volume level from 0.0 (fully muted) to 1.0 (maximum volume)."
                        },
                        ["category"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "Optional volume category to target ('master', 'game', 'music', 'ambient', 'ui'). Defaults to 'master'."
                        }
                    },
                    ["required"] = new List<string> { "volume" }
                },
                args =>
                {
                    try
                    {
                        var dict = JsonConvert.DeserializeObject<Dictionary<string, object>>(args);
                        if (dict == null || !dict.TryGetValue("volume", out var volVal))
                        {
                            return "{\"error\": \"Missing volume parameter.\"}";
                        }
                        
                        float vol = Convert.ToSingle(volVal);
                        vol = Math.Max(0f, Math.Min(1f, vol));

                        string cat = "master";
                        if (dict.TryGetValue("category", out var catVal) && catVal != null)
                        {
                            cat = catVal.ToString().ToLower();
                        }

                        if (cat == "master") Prefs.VolumeMaster = vol;
                        else if (cat == "game") Prefs.VolumeGame = vol;
                        else if (cat == "music") Prefs.VolumeMusic = vol;
                        else if (cat == "ambient") Prefs.VolumeAmbient = vol;
                        else if (cat == "ui") Prefs.VolumeUI = vol;
                        else
                        {
                            return $"{{\"error\": \"Invalid category '{cat}'. Valid options: master, game, music, ambient, ui.\"}}";
                        }

                        Prefs.Save();
                        return $"{{\"success\": true, \"message\": \"Volume category '{cat}' set to {vol}\"}}";
                    }
                    catch (Exception ex)
                    {
                        return $"{{\"error\": \"Failed to set volume: {ex.Message}\"}}";
                    }
                }
            );

            // Developer Tool: inspect_csharp_field
            RegisterTool(
                "inspect_csharp_field",
                "Queries the value of any static or nested C# field/property in the game using reflection. Example: 'Verse.Prefs.VolumeMaster' or 'RimWorld.Find.CurrentMap.Biome.defName'.",
                new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["path"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "The fully qualified member path to resolve."
                        }
                    },
                    ["required"] = new List<string> { "path" }
                },
                args =>
                {
                    try
                    {
                        var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(args);
                        if (dict == null || !dict.TryGetValue("path", out string path))
                        {
                            return "{\"error\": \"Missing path parameter.\"}";
                        }

                        object resultVal = ResolveReflectionPath(path);
                        if (resultVal == null)
                        {
                            return "{\"value\": null}";
                        }

                        Type t = resultVal.GetType();
                        if (t.IsPrimitive || t == typeof(string) || t.IsEnum || t == typeof(decimal))
                        {
                            string jsonVal = JsonConvert.SerializeObject(resultVal);
                            return $"{{\"value\": {jsonVal}, \"type\": \"{t.FullName}\"}}";
                        }

                        try
                        {
                            var settings = new JsonSerializerSettings
                            {
                                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                                MaxDepth = 3
                            };
                            string jsonVal = JsonConvert.SerializeObject(resultVal, settings);
                            return $"{{\"value\": {jsonVal}, \"type\": \"{t.FullName}\"}}";
                        }
                        catch
                        {
                            string strVal = resultVal.ToString();
                            return $"{{\"value\": {JsonConvert.SerializeObject(strVal)}, \"type\": \"{t.FullName}\", \"fallback\": \"ToString\"}}";
                        }
                    }
                    catch (Exception ex)
                    {
                        return $"{{\"error\": \"Failed to inspect C# field: {ex.Message}\"}}";
                    }
                }
            );

            // Developer Tool: write_debugger_log
            RegisterTool(
                "write_debugger_log",
                "Writes a custom debug message into RimWorld's log system (Player.log). Useful for injecting diagnostic trace points.",
                new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["message"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "The message text to write to the log."
                        },
                        ["severity"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "Optional severity ('info', 'warning', 'error'). Defaults to 'info'."
                        }
                    },
                    ["required"] = new List<string> { "message" }
                },
                args =>
                {
                    try
                    {
                        var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(args);
                        if (dict == null || !dict.TryGetValue("message", out string msg))
                        {
                            return "{\"error\": \"Missing message parameter.\"}";
                        }

                        string sev = "info";
                        if (dict.TryGetValue("severity", out string sVal) && sVal != null)
                        {
                            sev = sVal.ToLower();
                        }

                        if (sev == "error") Log.Error($"[AI Debugger] {msg}");
                        else if (sev == "warning") Log.Warning($"[AI Debugger] {msg}");
                        else Log.Message($"[AI Debugger] {msg}");

                        return "{\"success\": true}";
                    }
                    catch (Exception ex)
                    {
                        return $"{{\"error\": \"Failed to write log: {ex.Message}\"}}";
                    }
                }
            );
        }

        private static Type FindType(string typeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(typeName);
                if (type != null) return type;
            }
            return null;
        }

        private static object ResolveReflectionPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Path cannot be empty.");

            string[] parts = path.Split('.');
            Type type = null;
            int typePartsCount = 0;

            for (int i = parts.Length; i >= 1; i--)
            {
                string candidateTypeName = string.Join(".", parts.Take(i));
                var t = FindType(candidateTypeName);
                if (t != null)
                {
                    type = t;
                    typePartsCount = i;
                    break;
                }
            }

            if (type == null)
                throw new Exception($"Could not resolve type from path prefix in '{path}'");

            object currentObj = null;

            if (typePartsCount == parts.Length)
            {
                return type;
            }

            string firstMember = parts[typePartsCount];
            var prop = type.GetProperty(firstMember, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
            {
                currentObj = prop.GetValue(null);
            }
            else
            {
                var field = type.GetField(firstMember, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    currentObj = field.GetValue(null);
                }
                else
                {
                    var staticMembers = type.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Select(p => p.Name)
                        .Concat(type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Select(f => f.Name));
                    string suggestions = string.Join(", ", staticMembers);
                    throw new Exception($"Member '{firstMember}' not found on type '{type.FullName}' as a static property or field. Available static members: {suggestions}");
                }
            }

            for (int i = typePartsCount + 1; i < parts.Length; i++)
            {
                if (currentObj == null)
                    return null;

                string memberName = parts[i];
                var tInstance = currentObj.GetType();
                var pInstance = tInstance.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pInstance != null)
                {
                    currentObj = pInstance.GetValue(currentObj);
                }
                else
                {
                    var fInstance = tInstance.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (fInstance != null)
                    {
                        currentObj = fInstance.GetValue(currentObj);
                    }
                    else
                    {
                        var instanceMembers = tInstance.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(p => p.Name)
                            .Concat(tInstance.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(f => f.Name));
                        string suggestions = string.Join(", ", instanceMembers);
                        throw new Exception($"Member '{memberName}' not found on instance of type '{tInstance.FullName}'. Available members: {suggestions}");
                    }
                }
            }

            return currentObj;
        }
    }
}
