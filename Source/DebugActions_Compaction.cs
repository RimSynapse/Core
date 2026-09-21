using System.Linq;
using System.Text;
using LudeonTK;
using Verse;
using RimSynapse.Comps;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for automatic memory compaction (Core #131), grouped under "RimSynapse".
    /// ToolMapForPawns so each is reachable headlessly via the toolkit's run_debug_action + pawnName.
    /// "Dump plan" makes no LLM call; the two "force" actions bypass the age/cadence gates (but never the
    /// protection rules) so a fold can be exercised without waiting two in-game days.
    /// </summary>
    public static class DebugActions_Compaction
    {
        private const string Cat = "core";

        [DebugAction("RimSynapse", "Compaction: dump plan (Log)",
            actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DumpPlan(Pawn p)
        {
            var comp = p?.TryGetComp<SynapseCorePawnComp>();
            if (comp == null) { SynapseLogger.Message($"[RimSynapse] {p?.LabelShort} has no SynapseCorePawnComp.", Cat); return; }
            long now = Find.TickManager.TicksAbs;

            var night = comp.SelectNightSources(now, force: false);
            var nightForced = comp.SelectNightSources(now, force: true);
            var pentad = comp.SelectPentadSources(now, force: false);
            int protectedCount = comp.memories.Count(m => comp.IsCompactionProtected(m));

            var sb = new StringBuilder();
            sb.AppendLine($"--- Compaction plan for {p.LabelShort}: {comp.memories.Count} memories, {protectedCount} protected ---");
            sb.AppendLine($"Night (aged-out, ready now): {night.Count} — {string.Join(" | ", night.Select(m => Trunc(m.summary)))}");
            sb.AppendLine($"Night (ignoring age gate):   {nightForced.Count} — {string.Join(" | ", nightForced.Select(m => Trunc(m.summary)))}");
            sb.AppendLine($"Pentad (Compacted_Days ready): {pentad.Count} — {string.Join(" | ", pentad.Select(m => Trunc(m.summary)))}");
            SynapseLogger.Message(sb.ToString().TrimEnd(), Cat);
        }

        [DebugAction("RimSynapse", "Compaction: force night fold (Log)",
            actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ForceNight(Pawn p)
        {
            var comp = p?.TryGetComp<SynapseCorePawnComp>();
            if (comp == null) return;
            bool fired = comp.TryFoldNight(Find.TickManager.TicksAbs, force: true);
            SynapseLogger.Message($"[RimSynapse] {p.LabelShort}: night fold {(fired ? "queued (prose lands async — re-dump memories in a few seconds)" : "nothing eligible")}.", Cat);
        }

        [DebugAction("RimSynapse", "Compaction: force pentad fold (Log)",
            actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ForcePentad(Pawn p)
        {
            var comp = p?.TryGetComp<SynapseCorePawnComp>();
            if (comp == null) return;
            bool fired = comp.TryFoldPentad(Find.TickManager.TicksAbs, force: true);
            SynapseLogger.Message($"[RimSynapse] {p.LabelShort}: pentad fold {(fired ? "queued (needs ≥2 Compacted_Days)" : "nothing eligible")}.", Cat);
        }

        [DebugAction("RimSynapse", "Compaction: dump compacted memories (Log)",
            actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DumpCompacted(Pawn p)
        {
            var comp = p?.TryGetComp<SynapseCorePawnComp>();
            if (comp == null) return;
            var folds = comp.memories.Where(m => m.memoryType != null && m.memoryType.StartsWith("Compacted_")).ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"--- {folds.Count} compacted memories on {p.LabelShort} ---");
            foreach (var m in folds.OrderBy(m => m.absTick))
                sb.AppendLine($"  [{m.memoryType}] w={m.weight:F2} links={m.linkedMemoryIds?.Count ?? 0}: {m.summary}");
            SynapseLogger.Message(sb.ToString().TrimEnd(), Cat);
        }

        private static string Trunc(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length <= 40 ? s : s.Substring(0, 40) + "…");
    }
}
