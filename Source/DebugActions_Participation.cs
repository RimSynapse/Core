using System.Linq;
using System.Text;
using LudeonTK;
using RimWorld;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for the MayConverse participation predicate (Core #122), grouped under
    /// "RimSynapse". Headlessly runnable via the toolkit's run_debug_action. Dumps MayConverse +
    /// ConversationRole for every pawn on the current map so the roster (colonists, prisoners, slaves,
    /// guests, residents in; raiders, traders, animals out) can be confirmed against intent.
    /// </summary>
    public static class DebugActions_Participation
    {
        [DebugAction("RimSynapse", "Participation: dump MayConverse roster (#122)",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DumpRoster()
        {
            var map = Find.CurrentMap;
            var sb = new StringBuilder();
            sb.AppendLine($"[RimSynapse] MayConverse roster (Core #122) — map {map?.uniqueID}:");
            if (map == null) { sb.AppendLine("  <no current map>"); Log.Message(sb.ToString()); return; }

            int mayCount = 0;
            foreach (var pawn in map.mapPawns.AllPawns.OrderByDescending(p => SynapseCoreProviders.MayConverse(p)))
            {
                bool may = SynapseCoreProviders.MayConverse(pawn);
                if (may) mayCount++;
                string faction = pawn.Faction?.Name ?? (pawn.Faction == null ? "no faction" : pawn.Faction.def.defName);
                sb.AppendLine($"  [{(may ? "Y" : "-")}] {SynapseCoreProviders.ConversationRole(pawn),-9} {pawn.LabelShortCap} ({faction})");
            }
            sb.AppendLine($"  → {mayCount} of {map.mapPawns.AllPawnsCount} pawn(s) may converse.");
            Log.Message(sb.ToString());
        }
    }
}
