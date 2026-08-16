using System.Linq;
using System.Text;
using RimWorld;
using Verse;
using RimSynapse.Comps;

namespace RimSynapse
{
    /// <summary>
    /// Difficulty and personality context for the Storyteller and Chat agents (Core #66).
    ///
    /// Difficulty is injected as two derived values: a hard threat-points ceiling the
    /// storyteller chooses within (difficulty stays intact — a peaceful colony cannot be
    /// carpet-bombed), and a mood mandate — the difficulty read as Aura's stance. The
    /// custom threat-scale slider is read directly, not the preset name, so "peaceful
    /// base, cranked threats" stays legible.
    ///
    /// Everything here is gated on a RimSynapse storyteller being active: under a vanilla
    /// storyteller the builders return empty strings and nothing is injected anywhere.
    /// The Format* methods are the pure formatting seams the TestRunner cases exercise.
    /// </summary>
    public static class SynapseStorytellerContext
    {
        /// <summary>The master gate: is a RimSynapse storyteller (with our comp) active?</summary>
        public static bool IsRimSynapseStorytellerActive
            => Current.ProgramState == ProgramState.Playing
               && Find.Storyteller?.storytellerComps?.OfType<StorytellerComp_Storyteller>().Any() == true;

        /// <summary>The custom threat-scale slider value (1.0 when unavailable).</summary>
        public static float ReadThreatScale()
            => Find.Storyteller?.difficulty?.threatScale ?? 1f;

        /// <summary>
        /// The hard points ceiling for a target: DefaultThreatPointsNow, which the RimSynapse
        /// patch already redirects to the dynamic calculation when our storyteller is active.
        /// </summary>
        public static float ThreatPointsCeiling(IIncidentTarget target)
        {
            if (target == null) return 0f;
            return StorytellerUtility.DefaultThreatPointsNow(target);
        }

        /// <summary>Difficulty block for an agent prompt; empty when dormant.</summary>
        public static string BuildDifficultyContext(IIncidentTarget target)
        {
            if (!IsRimSynapseStorytellerActive) return string.Empty;

            var difficultyDef = Find.Storyteller?.difficultyDef;
            bool bigThreats = Find.Storyteller?.difficulty?.allowBigThreats ?? true;
            return FormatDifficultyBlock(
                difficultyDef?.LabelCap.ToString() ?? "Unknown",
                difficultyDef?.isCustom ?? false,
                ReadThreatScale(),
                bigThreats,
                ThreatPointsCeiling(target ?? Find.CurrentMap));
        }

        /// <summary>Personality block for an agent prompt; empty when dormant.</summary>
        public static string BuildPersonalityContext()
        {
            if (!IsRimSynapseStorytellerActive) return string.Empty;

            var props = StorytellerComp_Storyteller.GetActiveStorytellerProps();
            return FormatPersonalityBlock(
                props?.characterName ?? Find.Storyteller?.def?.label ?? "AI Storyteller",
                props?.speakingStyle,
                props?.personalityProfile);
        }

        /// <summary>
        /// Pure formatting of the difficulty block — the seam Core_DifficultyContextInjected
        /// and Core_CustomDifficultySliderRead assert against.
        /// </summary>
        public static string FormatDifficultyBlock(string presetLabel, bool isCustom, float threatScale, bool bigThreatsAllowed, float ceiling)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Difficulty Context:");
            sb.AppendLine($"- Difficulty preset: {presetLabel}{(isCustom ? " (custom settings — trust the sliders below, not the preset name)" : "")}");
            sb.AppendLine($"- Threat scale slider: {threatScale:0.00}{(bigThreatsAllowed ? "" : "; big threats are disabled")}");
            if (ceiling > 0f)
            {
                sb.AppendLine($"- Hard threat-points ceiling: {ceiling:F0}. Choose incidents and magnitudes within this budget — it is a bound, not a target.");
            }
            sb.AppendLine($"- Your stance (mood mandate): {SynapseDifficultyMood.MoodMandate(threatScale, bigThreatsAllowed)}");
            return sb.ToString().TrimEnd();
        }

        /// <summary>Pure formatting of the personality block.</summary>
        public static string FormatPersonalityBlock(string characterName, string speakingStyle, string personalityProfile)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Personality Profile:");
            sb.AppendLine($"- You are {characterName}.");
            if (!string.IsNullOrEmpty(speakingStyle))
                sb.AppendLine($"- Speaking style: {speakingStyle}.");
            if (!string.IsNullOrEmpty(personalityProfile))
                sb.AppendLine(personalityProfile.Trim());
            sb.AppendLine("This profile shapes both your storytelling decisions and your conversational voice.");
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// The combined context both agents inject: personality + difficulty. Empty when
        /// dormant, so callers can append unconditionally.
        /// </summary>
        public static string BuildAgentContext(IIncidentTarget target)
        {
            if (!IsRimSynapseStorytellerActive) return string.Empty;
            string personality = BuildPersonalityContext();
            string difficulty = BuildDifficultyContext(target);
            return (personality + "\n\n" + difficulty).Trim();
        }
    }
}
