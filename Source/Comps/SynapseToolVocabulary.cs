using System;
using System.Collections.Generic;
using System.Linq;

namespace RimSynapse
{
    /// <summary>
    /// Named tool-vocabulary scopes: each scope is an allowlist of tool names, enforced at
    /// the executor boundary (Core #63). Security rests on capability, not prompt-level
    /// persuasion-resistance — a verb not in the active scope cannot execute regardless of
    /// prompt content. A null/empty scope means unscoped (every registered tool), which is
    /// the behaviour of every caller that predates scopes; a named scope nobody defined
    /// fails closed.
    ///
    /// Deliberately game-free (no Verse/RimWorld references) so the Tier-1 sandbox can
    /// compile it under mono. Enforcement and logging live in SynapseToolRegistry.
    ///
    /// Names are matched case-insensitively — the script runner compares step types with
    /// OrdinalIgnoreCase, so a case-sensitive allowlist would let 'Fire_Incident' slip
    /// past the scope check and then fail the registry lookup. Unregistered names are
    /// permitted in a scope definition (mirroring MarkMutating): companion mods register
    /// tools Core only knows by name, e.g. get_faction_relations_history from Factions.
    /// </summary>
    public static class SynapseToolVocabulary
    {
        /// <summary>The Storyteller agent's scope: storytelling verbs only (Core #63).</summary>
        public const string StorytellerScope = "storyteller";

        /// <summary>
        /// The player-facing Chat agent's scope (Core #68): defined but empty, so it fails closed —
        /// no consequence tool can execute under it, no matter what the model is persuaded to emit.
        /// Isolation rests on capability, not the prompt: a jailbroken Chat can be rude, not
        /// dangerous. Chat never ingests raw player text into the Storyteller; only a typed sentiment
        /// signal crosses that boundary (see <see cref="StorytellerChatSentiment"/>).
        /// </summary>
        public const string ChatScope = "chat";

        private static readonly Dictionary<string, HashSet<string>> _scopes =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        static SynapseToolVocabulary()
        {
            // The storytelling allowlist, curated from the debug-command thematic gate
            // review on Core #63 ("could this happen in an unfolding story, through an
            // in-world cause the colony could perceive?"). ✅ IN and ⚠️ GATE-with-wrapper
            // verbs are vocabulary; 🚩 FLAG (god-mode/authoring) and 🔧 DIAG stay out —
            // structurally unreachable, not merely discouraged in the prompt.
            //
            // Deliberately excluded, with the review's reasoning:
            //   possess_colonist, modify_pawn_state   — character-sheet editing by fiat
            //   damage_self_with_equipped             — death by fiat, not via a story chain
            //   control_turret, fire_weapon_at_cell,
            //   attempt_remote_hack, spawn_hacker_base — dev/scenario tooling, no in-world cause
            //   modify_object_state                   — object editing by fiat
            //   send_notification_letter              — the narration path owns letters (#69)
            //   execute_game_tool                     — launders any tool by name
            //   set_game_volume, inspect_csharp_field,
            //   write_debugger_log, debug_*           — engine/diagnostic, not story actions
            Define(StorytellerScope,
                // Read-only colony context — the storyteller's senses.
                "get_active_threats",
                "get_colonists_profile",
                "get_colony_moods",
                "get_map_environment",
                "get_stockpile_details",
                "find_items_on_map",
                "search_map_entities",
                "search_game_definitions",
                "get_stored_result",
                "get_available_incidents",
                "get_faction_relations_history", // registered by Factions when present
                // Discovery, filtered to this scope by the meta-tool handlers.
                "list_available_tools",
                "describe_tool",
                // Consequence verbs — incidents are the job; a stressed pawn pushed over
                // the edge is a beat. Both clamp to the difficulty budget.
                "fire_incident",
                "trigger_colonist_break");

            // The Chat scope is defined with NO tools: it exists (so it is not "unscoped =
            // everything") yet permits nothing, so IsAllowed(ChatScope, anyTool) is always false.
            Define(ChatScope);
        }

        /// <summary>Define (or redefine) a scope as exactly the given tool names.</summary>
        public static void Define(string scope, params string[] toolNames)
        {
            if (string.IsNullOrEmpty(scope)) return;
            _scopes[scope] = new HashSet<string>(
                (toolNames ?? new string[0]).Where(n => !string.IsNullOrEmpty(n)),
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Add tool names to a scope, creating it if needed. This is how a companion mod
        /// contributes a storytelling verb of its own to the Storyteller scope.
        /// </summary>
        public static void Add(string scope, params string[] toolNames)
        {
            if (string.IsNullOrEmpty(scope)) return;
            if (!_scopes.TryGetValue(scope, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _scopes[scope] = set;
            }
            foreach (var n in toolNames ?? new string[0])
            {
                if (!string.IsNullOrEmpty(n)) set.Add(n);
            }
        }

        public static bool IsDefined(string scope)
            => !string.IsNullOrEmpty(scope) && _scopes.ContainsKey(scope);

        /// <summary>
        /// Whether a tool may execute under a scope. Null/empty scope is unscoped and
        /// allows everything; a scope with no definition allows nothing (fail closed).
        /// </summary>
        public static bool IsAllowed(string scope, string toolName)
        {
            if (string.IsNullOrEmpty(scope)) return true;
            if (string.IsNullOrEmpty(toolName)) return false;
            return _scopes.TryGetValue(scope, out var set) && set.Contains(toolName);
        }

        /// <summary>The tool names a scope permits (empty for undefined scopes).</summary>
        public static IEnumerable<string> Tools(string scope)
        {
            if (!string.IsNullOrEmpty(scope) && _scopes.TryGetValue(scope, out var set))
                return set.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            return new List<string>();
        }

        /// <summary>
        /// Clamp a requested magnitude to a ceiling. A non-positive ceiling means no
        /// ceiling is known and the request passes through unchanged.
        /// </summary>
        public static float ClampToBudget(float requested, float ceiling)
            => (ceiling > 0f && requested > ceiling) ? ceiling : requested;

        /// <summary>
        /// Resolve the points a consequence tool should use: an unscoped caller keeps its
        /// requested override untouched (operator/dev god-mode), a scoped caller is clamped
        /// to the difficulty ceiling. requested &lt;= 0 means "no override" and yields the
        /// ceiling itself.
        /// </summary>
        public static float ResolvePoints(float requested, float ceiling, string scope)
        {
            if (requested <= 0f) return ceiling;
            return string.IsNullOrEmpty(scope) ? requested : ClampToBudget(requested, ceiling);
        }
    }
}
