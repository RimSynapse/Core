using System;
using System.Collections.Generic;
using System.Linq;

namespace RimSynapse
{
    public struct SynapseToolSearchResult
    {
        public GameTool Tool;
        public double Score;
    }

    /// <summary>
    /// Precomputed search index over the tool registry.
    ///
    /// Selection previously re-serialised every tool's parameter schema on every prompt
    /// build and matched by substring, so "rain" scored "train" and "he" scored "the".
    /// The index tokenises each tool's name, keywords, description and parameters once,
    /// and matches query tokens exactly. Weights mirror the old scorer (name > keyword >
    /// description > parameter) so ranking behaviour stays familiar.
    ///
    /// The old hand-written semantic boosts are carried over as data: a synonym table
    /// expanding query tokens, and affinity rules boosting named tools when the query
    /// carries certain tokens. Rules naming tools that are not registered are inert.
    ///
    /// Built lazily; RegisterTool invalidates, so late-registered tools are findable.
    /// </summary>
    public static class SynapseToolIndex
    {
        private class ToolDoc
        {
            public GameTool Tool;
            public string NameLower;
            public HashSet<string> NameTokens;
            public HashSet<string> KeywordTokens;
            public HashSet<string> DescTokens;
            public HashSet<string> ParamTokens;
        }

        private static readonly object _lock = new object();
        private static List<ToolDoc> _docs;
        private static bool _dirty = true;

        private static readonly HashSet<string> Stopwords = new HashSet<string>(StringComparer.Ordinal)
        {
            // "them" stays out of this list: it triggers the pawn affinity rule.
            "the", "and", "for", "with", "give", "make", "have",
            "all", "any", "this", "that", "from", "into", "then", "will",
            "please", "just", "very", "some", "our", "your"
        };

        /// <summary>Query-token expansions. Extend cautiously: recall is cheap, precision is not.</summary>
        private static readonly Dictionary<string, string[]> Synonyms = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["hurt"] = new[] { "damage" },
            ["injure"] = new[] { "damage" },
            ["wound"] = new[] { "damage" },
            ["kill"] = new[] { "damage", "death" },
            ["rain"] = new[] { "weather" },
            ["storm"] = new[] { "weather" },
            ["snow"] = new[] { "weather" },
            ["fog"] = new[] { "weather" },
            ["heal"] = new[] { "medical", "tend" },
            ["sick"] = new[] { "disease", "medical" },
        };

        /// <summary>
        /// The old semantic-association boosts, as data. Each rule: when the query carries
        /// any trigger token, the named tools gain the boost. Unregistered names are inert.
        /// </summary>
        private static readonly (string[] triggers, string[] tools, double boost)[] AffinityRules =
        {
            (new[] { "his", "her", "their", "him", "them", "she", "he", "self", "own", "pawn", "colonist", "animal" },
             new[] { "modify_pawn_state", "possess_colonist", "damage_self_with_equipped" }, 10.0),
            (new[] { "equip", "prioritize", "walk", "go", "move", "build", "mine", "harvest", "clean", "haul", "repair", "work", "job", "construct" },
             new[] { "possess_colonist" }, 10.0),
            (new[] { "kill", "damage", "hurt", "die", "suicide", "stab", "shoot", "fire", "attack", "wound", "bleed" },
             new[] { "modify_pawn_state", "damage_self_with_equipped" }, 12.0),
            (new[] { "raid", "mechanoid", "threat", "infestation", "spawn", "incident", "event" },
             new[] { "spawn_incident", "trigger_raid", "spawn_threat" }, 15.0),
            (new[] { "weather", "time", "rain", "storm", "sun", "day", "night", "snow", "fog" },
             new[] { "set_weather", "set_time" }, 15.0),
        };

        public static void Invalidate()
        {
            lock (_lock) { _dirty = true; }
        }

        /// <summary>Rank registered tools against a free-text query. Only positive scores return.</summary>
        public static List<SynapseToolSearchResult> Search(string query, int maxResults, bool includeDebug = false)
        {
            var results = new List<SynapseToolSearchResult>();
            if (string.IsNullOrWhiteSpace(query) || maxResults <= 0) return results;

            var queryTokens = Tokenize(query);
            foreach (var t in queryTokens.ToList())
            {
                if (Synonyms.TryGetValue(t, out var extra))
                {
                    foreach (var e in extra) queryTokens.Add(e);
                }
            }

            List<ToolDoc> docs;
            lock (_lock)
            {
                EnsureBuiltLocked();
                docs = _docs;
            }

            foreach (var doc in docs)
            {
                if (doc.Tool.isDebugAction && !includeDebug) continue;

                double score = 0;
                foreach (var token in queryTokens)
                {
                    if (doc.NameLower == token) score += 15.0;
                    else if (doc.NameTokens.Contains(token)) score += 5.0;
                    if (doc.KeywordTokens.Contains(token)) score += 12.0;
                    if (doc.DescTokens.Contains(token)) score += 3.0;
                    if (doc.ParamTokens.Contains(token)) score += 2.0;
                }

                foreach (var rule in AffinityRules)
                {
                    if (!rule.tools.Contains(doc.Tool.name)) continue;
                    if (rule.triggers.Any(queryTokens.Contains)) score += rule.boost;
                }

                if (score > 0)
                {
                    results.Add(new SynapseToolSearchResult { Tool = doc.Tool, Score = score });
                }
            }

            return results
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.Tool.name, StringComparer.Ordinal)
                .Take(maxResults)
                .ToList();
        }

        private static void EnsureBuiltLocked()
        {
            if (!_dirty && _docs != null) return;

            var docs = new List<ToolDoc>();
            foreach (var tool in SynapseToolRegistry.AllTools)
            {
                var doc = new ToolDoc
                {
                    Tool = tool,
                    NameLower = tool.name?.ToLowerInvariant() ?? string.Empty,
                    NameTokens = Tokenize(tool.name),
                    KeywordTokens = new HashSet<string>(StringComparer.Ordinal),
                    DescTokens = Tokenize(tool.description),
                    ParamTokens = new HashSet<string>(StringComparer.Ordinal)
                };

                if (tool.keywords != null)
                {
                    foreach (var kw in tool.keywords)
                    {
                        foreach (var t in Tokenize(kw)) doc.KeywordTokens.Add(t);
                    }
                }

                // Parameter names and descriptions, serialised exactly once per rebuild
                // instead of on every prompt build.
                try
                {
                    var parametersJObj = Newtonsoft.Json.Linq.JObject.FromObject(tool.parameters);
                    if (parametersJObj != null
                        && parametersJObj.TryGetValue("properties", out var propsToken)
                        && propsToken is Newtonsoft.Json.Linq.JObject propsObj)
                    {
                        foreach (var prop in propsObj)
                        {
                            foreach (var t in Tokenize(prop.Key)) doc.ParamTokens.Add(t);
                            if (prop.Value is Newtonsoft.Json.Linq.JObject pDetail
                                && pDetail.TryGetValue("description", out var dToken))
                            {
                                foreach (var t in Tokenize(dToken.ToString())) doc.ParamTokens.Add(t);
                            }
                        }
                    }
                }
                catch { }

                docs.Add(doc);
            }

            _docs = docs;
            _dirty = false;
        }

        private static HashSet<string> Tokenize(string text)
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text)) return tokens;

            var current = new System.Text.StringBuilder();
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c))
                {
                    current.Append(char.ToLowerInvariant(c));
                }
                else if (current.Length > 0)
                {
                    AddToken(tokens, current.ToString());
                    current.Clear();
                }
            }
            if (current.Length > 0) AddToken(tokens, current.ToString());
            return tokens;
        }

        private static void AddToken(HashSet<string> tokens, string token)
        {
            // Pronouns like "he" are meaningful triggers for the affinity rules, so keep
            // two-letter tokens; single letters and stopwords are noise.
            if (token.Length < 2) return;
            if (Stopwords.Contains(token)) return;
            tokens.Add(token);
        }
    }
}
