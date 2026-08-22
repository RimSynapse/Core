using System.Text;
using LudeonTK;
using Verse;
using RimSynapse.Comps;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for difficulty + personality context injection (Core #66), grouped
    /// under "RimSynapse". Dumps the live gate state and the built context blocks; when the
    /// active storyteller is not a RimSynapse one (the dormant case), it additionally forces
    /// the formatting path with the live slider values so the mechanic is still demonstrated.
    /// Headlessly runnable via the toolkit's run_debug_action.
    /// </summary>
    public static class DebugActions_StorytellerContext
    {
        [DebugAction("RimSynapse", "Storyteller context: dump difficulty + personality",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DumpStorytellerContext()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[RimSynapse] Storyteller context dump:");
            sb.AppendLine($"  active storyteller: {Find.Storyteller?.def?.defName ?? "(none)"} | RimSynapse gate: {SynapseStorytellerContext.IsRimSynapseStorytellerActive}");
            sb.AppendLine($"  difficulty preset: {Find.Storyteller?.difficultyDef?.defName ?? "(none)"} | threatScale slider: {SynapseStorytellerContext.ReadThreatScale():0.00} | ceiling: {SynapseStorytellerContext.ThreatPointsCeiling(Find.CurrentMap):F0}");

            string live = SynapseStorytellerContext.BuildAgentContext(Find.CurrentMap);
            if (!string.IsNullOrEmpty(live))
            {
                sb.AppendLine("  live agent context (injected into Storyteller + Chat):");
                sb.AppendLine(live);
            }
            else
            {
                sb.AppendLine("  live agent context: (empty — dormant under a non-RimSynapse storyteller, as designed)");
                sb.AppendLine("  forced formatting with live values (what an active RimSynapse storyteller would inject):");
                sb.AppendLine(SynapseStorytellerContext.FormatDifficultyBlock(
                    Find.Storyteller?.difficultyDef?.LabelCap.ToString() ?? "Unknown",
                    Find.Storyteller?.difficultyDef?.isCustom ?? false,
                    SynapseStorytellerContext.ReadThreatScale(),
                    Find.Storyteller?.difficulty?.allowBigThreats ?? true,
                    SynapseStorytellerContext.ThreatPointsCeiling(Find.CurrentMap)));
                sb.AppendLine(SynapseStorytellerContext.FormatPersonalityBlock("Aura", "sassy, dramatic, and snarky", null));
            }

            SynapseLogger.Message(sb.ToString().TrimEnd());
        }
    }
}
