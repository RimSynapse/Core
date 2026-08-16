namespace RimSynapse
{
    /// <summary>
    /// Options for a chat completion request. All fields are optional.
    /// </summary>
    public class ChatOptions
    {
        /// <summary>Model name to use. Null = auto-map to active model in LM Studio.</summary>
        public string model;

        /// <summary>Maximum tokens to generate. Null = LM Studio default.</summary>
        public int? maxTokens;

        /// <summary>Sampling temperature. Null = LM Studio default.</summary>
        public float? temperature;

        /// <summary>Friendly name for the task type, used for UI display and tracking token averages. E.g., "Generating Childhood Memory"</summary>
        public string requestName;

        /// <summary>Optional unique identifier for this query type to allow user routing preferences (e.g. "storyteller_event")</summary>
        public string queryId;

        /// <summary>Explicit routing override for testing or forcing a specific provider. Bypass capability defaults.</summary>
        public string providerOverride;

        /// <summary>Friendly name for the target, used for UI display. E.g., "Pawn: John"</summary>
        public string targetName;

        /// <summary>Enable response sanitization (strip think blocks, repair JSON). Default: true.</summary>
        public bool sanitize = true;

        /// <summary>
        /// Enable model thinking/reasoning. Null = use global setting,
        /// true = force thinking on, false = force thinking off.
        /// Disabling thinking saves tokens and reduces latency.
        /// </summary>
        public bool? thinking;

        /// <summary>Request priority. 0 = normal, higher = processed sooner within mod's budget.</summary>
        public int priority;

        /// <summary>Maximum time in milliseconds this request can sit in the queue before being dropped. Null = infinite.</summary>
        public int? maxWaitMs;

        /// <summary>
        /// Tool-vocabulary scope for native tool calls made by this request (Core #63).
        /// Null = unscoped (every registered tool). The request queue passes it to
        /// SynapseToolRegistry.ExecuteTool, so a verb outside the scope is refused at the
        /// executor no matter what the model asks for.
        /// </summary>
        public string toolScope;

        // --- Context Embedding ---

        /// <summary>
        /// Event type for context assembly. Controls which context profile
        /// (tiers, budget fraction, weight overrides) is used.
        /// Standard types: "dialogue", "event", "thought", "reaction",
        /// "relationship", "quest", "custom".
        /// If null, no context is assembled (raw passthrough).
        /// </summary>
        public string eventType;

        /// <summary>
        /// Override context tier selection for this request.
        /// If null, uses the mod's DefaultTiers or the profile default.
        /// </summary>
        public ContextTierMask? contextTiers;

        /// <summary>
        /// Per-slot weight overrides for this request.
        /// These override both XML weights and profile overrides.
        /// Used by Storyteller for dynamic weight boosting.
        /// </summary>
        public System.Collections.Generic.Dictionary<string, float> weightOverrides;

        /// <summary>
        /// Source pawn for context assembly (the primary pawn in the interaction).
        /// If null and eventType is set, context will be colony-level only.
        /// </summary>
        public Verse.Pawn sourcePawn;

        /// <summary>
        /// Target pawn for context assembly (secondary pawn in two-pawn interactions).
        /// </summary>
        public Verse.Pawn targetPawn;

        /// <summary>Default options with sanitization enabled and auto model mapping.</summary>
        public static ChatOptions Default => new ChatOptions();
    }
}
