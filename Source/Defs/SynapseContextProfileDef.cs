using System.Collections.Generic;

namespace RimSynapse
{
    /// <summary>
    /// Defines a context profile for a specific event type.
    /// Controls which context tiers are included, what fraction of
    /// the token budget to use, and per-slot weight overrides.
    ///
    /// Loaded from XML in Defs/SynapseProfiles/. Companion mods can
    /// define profiles for custom event types without C#.
    /// </summary>
    public class SynapseContextProfileDef : Verse.Def
    {
        /// <summary>
        /// The event type this profile applies to.
        /// Standard types: "dialogue", "event", "thought", "reaction",
        /// "relationship", "quest", "custom".
        /// Companion mods may define additional types (e.g., "storyteller_narration").
        /// </summary>
        public string eventType;

        /// <summary>
        /// Fraction of the available context window to allocate for this event type.
        /// Range: 0.0 to 1.0. Example: 0.50 = use 50% of the available budget.
        /// The actual token budget is: contextWindow × budgetFraction × performanceScalar.
        /// </summary>
        public float budgetFraction = 0.40f;

        /// <summary>
        /// Target total response latency for this event type, in milliseconds.
        /// 0 means "use the built-in default for this type" (e.g. dialogue: 500).
        /// The tier controller sizes prompts so responses land under this target —
        /// or, when the backend's measured floor exceeds it, under floor × 1.15.
        /// </summary>
        public int latencyTargetMs = 0;

        /// <summary>
        /// Which context tiers to include. Valid values:
        /// "Identity" (Tier 1), "PawnState" (Tier 2), "Colony" (Tier 3),
        /// "World" (Tier 4), "Synthetic" (Tier 5).
        /// </summary>
        public List<string> includeTiers;

        /// <summary>
        /// Per-slot weight overrides for this profile.
        /// These override the base weights from SynapseWeightDef
        /// for this specific event type only.
        /// If a slot isn't listed, the default weight applies.
        /// </summary>
        public List<SlotWeightOverride> weightOverrides;
    }

    /// <summary>
    /// A per-slot weight override within a context profile.
    /// Allows event types to boost or reduce specific slot weights
    /// without affecting the global weight table.
    /// </summary>
    public class SlotWeightOverride
    {
        /// <summary>
        /// The context slot to override (must match a SynapseWeightDef.slot value).
        /// </summary>
        public string slot;

        /// <summary>
        /// The overridden weight for this slot within this profile.
        /// </summary>
        public float weight;
    }
}
