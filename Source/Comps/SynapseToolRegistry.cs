using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Newtonsoft.Json;

namespace RimSynapse
{
    /// <summary>
    /// Describes a single game tool the LLM can invoke.
    /// </summary>
    public class GameTool
    {
        public string name;
        public string description;
        public object parameters; // JSON Schema parameter description
        public Func<string, string> handler;
        public bool isDebugAction = false;
        public List<string> keywords = new List<string>();
    }

    /// <summary>
    /// Central registry for all game tools the LLM can call.
    /// Tool handler registrations are split across partial-class files in the Tools/ subfolder.
    /// </summary>
    public static partial class SynapseToolRegistry
    {
        private static readonly Dictionary<string, GameTool> _tools = new Dictionary<string, GameTool>();
        private static bool _initialized = false;

        public static Func<Pawn, string, string, int?, int?, bool> CustomBreakHandler;

        public static void RegisterTool(string name, string description, object parametersSchema, Func<string, string> handler, bool isDebug = false, List<string> keywords = null)
        {
            RegisterToolCore(name, description, parametersSchema, handler, isDebug, keywords);
            SynapseToolIndex.Invalidate();
        }

        private static void RegisterToolCore(string name, string description, object parametersSchema, Func<string, string> handler, bool isDebug, List<string> keywords)
        {
            EnsureInitialized();
            _tools[name] = new GameTool
            {
                name = name,
                description = description,
                parameters = parametersSchema,
                handler = handler,
                isDebugAction = isDebug,
                keywords = keywords ?? new List<string>()
            };
        }

        public static IEnumerable<GameTool> AllTools
        {
            get
            {
                EnsureInitialized();
                return _tools.Values;
            }
        }

        public static IEnumerable<GameTool> NonDebugTools
        {
            get
            {
                EnsureInitialized();
                return _tools.Values.Where(t => !t.isDebugAction);
            }
        }

        /// <summary>
        /// Whether a tool is registered. Callers that dispatch a caller-supplied name should
        /// check this first: ExecuteTool answers an unknown name with an error payload rather
        /// than throwing, which is easy to mistake for a tool's own output.
        /// </summary>
        public static bool IsToolRegistered(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            EnsureInitialized();
            return _tools.ContainsKey(name);
        }

        public static string ExecuteTool(string name, string argumentsJson)
        {
            EnsureInitialized();
            if (_tools.TryGetValue(name, out var tool))
            {
                try
                {
                    return tool.handler(argumentsJson);
                }
                catch (Exception ex)
                {
                    return $"{{\"error\": \"Exception during tool execution: {ex.Message}\"}}";
                }
            }
            return $"{{\"error\": \"Tool '{name}' not found.\"}}";
        }

        public static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            // Register Built-in Tools
            RegisterMetaTools();
            RegisterResultTools();
            RegisterColonistTools();
            RegisterStockpileTools();
            RegisterThreatTools();
            RegisterMoodTools();
            RegisterEnvironmentTools();
            RegisterIncidentTools();
            RegisterPossessionTools();
            RegisterBreakTools();
            RegisterCombatTools();
            RegisterHackingTools();
            RegisterPawnStateTools();
            RegisterObjectStateTools();
            RegisterSearchTools();
            RegisterDefinitionTools();
            RegisterDynamicDebugActions();

            // Centralized synonym keyword registrations
            AssignDefaultKeywords();
        }

        private static void AssignDefaultKeywords()
        {
            AssignKeywords("modify_pawn_state", new List<string> { 
                "shooting", "melee", "construction", "mining", "cooking", "plants", "animals", "crafting", 
                "artistic", "medicine", "social", "intellectual", "skill", "passion", "trait", "degree", 
                "lazy", "industrious", "cannibal", "bipolar", "psychic", "jogger", "neurotic", "health", 
                "wound", "flu", "infection", "hediff", "cut", "amputate", "remove", "part", "severity", 
                "pain", "bleed", "convert", "ideology", "religion"
            });

            AssignKeywords("possess_colonist", new List<string> { 
                "equip", "prioritize", "walk", "go", "move", "build", "mine", "harvest", "clean", 
                "haul", "repair", "work", "job", "construct", "draft", "undraft"
            });

            AssignKeywords("damage_self_with_equipped", new List<string> { 
                "suicide", "harm", "damage", "kill", "shoot", "stab", "die", "hurt"
            });

            AssignKeywords("set_weather", new List<string> { 
                "weather", "rain", "storm", "sun", "snow", "fog", "clear", "wind", "climate"
            });

            AssignKeywords("set_time", new List<string> { 
                "time", "day", "night", "hour", "speed", "warp"
            });

            AssignKeywords("spawn_incident", new List<string> { 
                "raid", "incident", "event", "threat", "mechanoid", "infestation", "trader", "quest"
            });

            AssignKeywords("trigger_raid", new List<string> { 
                "raid", "siege", "attack", "enemy", "faction"
            });

            AssignKeywords("spawn_threat", new List<string> { 
                "threat", "mechanoid", "infestation", "hives", "pods", "hive"
            });

            AssignKeywords("hack_mechanoid", new List<string> { 
                "hack", "mechanoid", "scyther", "lancer", "centipede", "control"
            });

            AssignKeywords("disrupt_shield", new List<string> { 
                "shield", "disrupt", "generator", "hack", "terminal"
            });

            AssignKeywords("create_stockpile", new List<string> { 
                "stockpile", "storage", "zone", "dump", "filter"
            });

            AssignKeywords("modify_mood", new List<string> { 
                "mood", "happy", "sad", "joy", "need", "comfort"
            });

            AssignKeywords("force_mental_break", new List<string> { 
                "break", "mental", "insult", "rage", "berserk", "sadistic"
            });
        }

        private static void AssignKeywords(string toolName, List<string> kw)
        {
            if (_tools.TryGetValue(toolName, out var tool))
            {
                tool.keywords = kw;
            }
        }
    }
}
