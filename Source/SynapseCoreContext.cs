using System.Collections.Generic;
using System.Linq;
using RimSynapse.Internal;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Public static API for context embedding.
    /// Companion mods can use this to manually trigger context assembly,
    /// though in normal usage context is assembled automatically when
    /// </summary>
    public static class SynapseCoreContext
    {
        public delegate void ContextInjectionHandler(Pawn pawn, string contextType, List<string> extraContext);
        
        /// <summary>
        /// Global hook for other modules (like Factions) to seamlessly inject context into 
        /// generic requests (like backstory generation in Psychology) without hard dependencies.
        /// </summary>
        public static event ContextInjectionHandler OnInjectGenericContext;

        /// <summary>
        /// Fires when global news is published. Used by RimSynapse-Factions to simulate rumor spread.
        /// (float wealthDelta, float strengthDelta)
        /// </summary>
        public static event System.Action<float, float> OnGlobalKnowledgeBroadcast;

        public static void BroadcastGlobalKnowledge(float wealthDelta, float strengthDelta)
        {
            OnGlobalKnowledgeBroadcast?.Invoke(wealthDelta, strengthDelta);
        }

        /// <summary>
        /// Publish a world-event notification for the storyteller to consider (Core #135). The
        /// additive, outbound model: an external source (WorldNews, World Domination, …) pushes a
        /// world event here; the RimSynapse storyteller reads the pending set as incident-selection
        /// context and weight and may manifest a qualifying one AT the colony. Purely additive — with
        /// no source publishing, the storyteller is exactly vanilla.
        ///
        /// <para>Deliberately all primitives so a producer registers it by reflection with Core
        /// absent: <c>GenTypes.GetTypeInAnyAssembly("RimSynapse.SynapseCoreContext")
        /// .GetMethod("PublishWorldEvent").Invoke(null, new object[]{ kind, region, magnitude, origin,
        /// summary, ttlTicks })</c>.</para>
        /// </summary>
        /// <param name="kind">Event kind, matched against incident defNames (e.g. "plague", "raid").</param>
        /// <param name="region">Coarse location, for flavour. May be empty.</param>
        /// <param name="magnitude">Severity/scale (nudges selection weight). 0 if unknown.</param>
        /// <param name="origin">Faction/source name. May be empty.</param>
        /// <param name="summary">One-line human description for the selection prompt. May be empty.</param>
        /// <param name="ttlTicks">How long the event stays live; &lt;= 0 uses the default (~2 days).</param>
        public static void PublishWorldEvent(string kind, string region, float magnitude, string origin, string summary, int ttlTicks)
        {
            SynapseWorldEventInbox.Publish(kind, region, magnitude, origin, summary, ttlTicks);
        }

        /// <summary>
        /// Fires the OnInjectGenericContext event and returns a concatenated string of all injected context.
        /// </summary>
        public static string GatherGenericContext(Pawn pawn, string contextType)
        {
            var extraContext = new List<string>();
            OnInjectGenericContext?.Invoke(pawn, contextType, extraContext);
            if (extraContext.Count == 0) return "";
            return "\n" + string.Join("\n", extraContext);
        }
        /// <summary>
        /// Build a context packet for a pawn and event type.
        /// Returns null if context embedding is disabled in settings.
        /// </summary>
        /// <param name="eventType">Event type (dialogue, event, thought, etc.)</param>
        /// <param name="sourcePawn">Primary pawn (null for colony-level events)</param>
        /// <param name="targetPawn">Secondary pawn (optional)</param>
        /// <param name="tiers">Override tier selection (null = use profile default)</param>
        /// <param name="weightOverrides">Per-slot weight overrides (null = use defaults)</param>
        /// <returns>ContextPacket or null if disabled</returns>
        public static ContextPacket BuildContext(
            string eventType,
            Pawn sourcePawn = null,
            Pawn targetPawn = null,
            ContextTierMask? tiers = null,
            Dictionary<string, float> weightOverrides = null)
        {
            if (!IsEnabled())
            {
                SynapseLogger.Message("Context embedding is disabled.");
                return null;
            }

            return ContextAssembler.Build(eventType, sourcePawn, targetPawn,
                tiers, weightOverrides);
        }

        /// <summary>
        /// Serialize a context packet to a text block for injection
        /// into a system message.
        /// </summary>
        public static string SerializeContext(ContextPacket packet)
        {
            return ContextAssembler.SerializeToText(packet);
        }

        /// <summary>
        /// Build and serialize in one call. Convenience method.
        /// Returns empty string if disabled.
        /// </summary>
        public static string GetContextText(
            string eventType,
            Pawn sourcePawn = null,
            Pawn targetPawn = null,
            ContextTierMask? tiers = null,
            Dictionary<string, float> weightOverrides = null)
        {
            var packet = BuildContext(eventType, sourcePawn, targetPawn,
                tiers, weightOverrides);
            if (packet == null) return "";
            return SerializeContext(packet);
        }

        /// <summary>
        /// Resolve the system prompt for a given event type and mod.
        /// Reads SynapsePromptDef XML, resolving mod-specific prompts first,
        /// then falling back to default prompts.
        /// </summary>
        /// <param name="eventType">Event type to resolve prompt for</param>
        /// <param name="modId">Mod requesting the prompt (for mod-specific matching)</param>
        /// <returns>System prompt text, or empty string if none found</returns>
        public static string ResolvePrompt(string eventType, string modId = null)
        {
            // 1. Look for mod-specific prompt (highest priority first)
            if (!string.IsNullOrEmpty(modId))
            {
                var modSpecific = DefDatabase<SynapsePromptDef>.AllDefs
                    .Where(d => d.eventType == eventType && d.targetModId == modId)
                    .OrderByDescending(d => d.priority)
                    .FirstOrDefault();

                if (modSpecific != null)
                    return modSpecific.systemPrompt?.Trim() ?? "";
            }

            // 2. Fall back to default prompt for this event type
            var defaultPrompt = DefDatabase<SynapsePromptDef>.AllDefs
                .Where(d => d.eventType == eventType &&
                           string.IsNullOrEmpty(d.targetModId))
                .OrderByDescending(d => d.priority)
                .FirstOrDefault();

            return defaultPrompt?.systemPrompt?.Trim() ?? "";
        }

        /// <summary>
        /// Check if context embedding is enabled in settings.
        /// </summary>
        public static bool IsEnabled()
        {
            return RimSynapseMod.Instance?.Settings?.enableContextEmbedding ?? false;
        }
    }
}
