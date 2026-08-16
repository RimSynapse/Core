using System.Linq;
using System.Text;
using LudeonTK;
using Verse;
using RimSynapse.Comps;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for the pivotal life-event secured memories (Core #92), grouped under
    /// "RimSynapse". Forces every pivotal event on a pawn (bypassing its real trigger) and dumps the
    /// result — headlessly runnable via the toolkit's run_debug_action, so the mechanic is proven
    /// without staging an actual arrest/conversion.
    /// </summary>
    public static class DebugActions_PivotalLife
    {
        private static readonly string[] PivotalTypes =
        {
            SynapsePivotalMemory.Recruited,
            SynapsePivotalMemory.Arrested,
            SynapsePivotalMemory.Captured,
            SynapsePivotalMemory.Enslaved,
            SynapsePivotalMemory.Converted,
            SynapsePivotalMemory.Freed,
            SynapsePivotalMemory.EscapeAttempt,
        };

        [DebugAction("RimSynapse", "Seed pivotal life-events",
            actionType = DebugActionType.ToolMapForPawns,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SeedPivotalLifeEvents(Pawn p)
        {
            if (p == null) return;
            var comp = p.TryGetComp<SynapseCorePawnComp>();
            if (comp == null)
            {
                SynapseLogger.Message($"[RimSynapse] Pivotal seed: {p.LabelShortCap} has no SynapseCorePawnComp.");
                return;
            }

            foreach (string t in PivotalTypes)
                SynapsePivotalMemory.Record(p, t, $"[debug] {p.LabelShortCap}: {t}.");

            var sb = new StringBuilder();
            sb.AppendLine($"[RimSynapse] Pivotal life-events seeded on {p.LabelShortCap}:");
            int secured = 0;
            foreach (string t in PivotalTypes)
            {
                var m = comp.GetMemoriesByTag(SynapsePivotalMemory.PivotalTagPrefix + t).FirstOrDefault();
                if (m == null) { sb.AppendLine($"    {t}: (not recorded)"); continue; }
                if (m.isLongTerm) secured++;
                sb.AppendLine($"    {t}: isLongTerm={m.isLongTerm}, weight={m.weight:F2} — \"{m.summary}\"");
            }
            sb.AppendLine($"  {secured}/{PivotalTypes.Length} secured as long-term.");
            SynapseLogger.Message(sb.ToString());
        }
    }
}
