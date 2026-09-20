using System.Linq;
using System.Text;
using LudeonTK;
using RimSynapse.Comps;
using RimSynapse.Models;
using RimSynapse.UI;
using RimWorld;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for the manual memory curator (Core #138), grouped under "RimSynapse". The
    /// "open curator" action is the manual surface; the dump/add/prune actions exercise the add and
    /// remove paths headlessly (single-<see cref="Pawn"/>, runnable via the toolkit's run_debug_action
    /// with pawnName) so the mechanic is confirmable without the UI.
    /// </summary>
    public static class DebugActions_MemoryCurator
    {
        private const string Cat = "core";

        [DebugAction("RimSynapse", "Memory: open curator (pick pawn)",
            actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void OpenCurator(Pawn p)
        {
            if (p?.TryGetComp<SynapseCorePawnComp>() == null)
            {
                SynapseLogger.Message($"[RimSynapse] {p?.LabelShort} has no SynapseCorePawnComp.", Cat);
                return;
            }
            Find.WindowStack.Add(new Dialog_MemoryCurator(p));
        }

        [DebugAction("RimSynapse", "Memory: dump memories (pick pawn)",
            actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DumpMemories(Pawn p)
        {
            var comp = p?.TryGetComp<SynapseCorePawnComp>();
            if (comp == null) return;
            var sb = new StringBuilder();
            sb.AppendLine($"[RimSynapse] {p.LabelShort}: {comp.memories.Count} memories " +
                          $"({comp.memories.Count(m => m.isLongTerm)} LT, {comp.memories.Count(m => comp.IsCompactionProtected(m))} protected):");
            foreach (var m in comp.memories.OrderByDescending(m => m.weight))
                sb.AppendLine($"  [{m.weight:0.00}] {m.memoryType}{(m.isLongTerm ? " LT" : "")} — {Trunc(m.summary)}");
            SynapseLogger.Message(sb.ToString().TrimEnd(), Cat);
        }

        [DebugAction("RimSynapse", "Memory: add test memory (pick pawn)",
            actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void AddTestMemory(Pawn p)
        {
            var comp = p?.TryGetComp<SynapseCorePawnComp>();
            if (comp == null) return;
            int before = comp.memories.Count;
            comp.AddMemory(new WeightedMemory
            {
                summary = $"[debug] manually added test memory at tick {Find.TickManager.TicksAbs}",
                memoryType = "manual",
                weight = 0.5f,
                baseWeight = 0.5f,
                absTick = Find.TickManager.TicksAbs,
                gameTick = Find.TickManager.TicksGame,
            });
            int after = comp.memories.Count;
            SynapseLogger.Message($"[RimSynapse] {p.LabelShort}: add test memory — count {before} → {after} " +
                                  $"({(after > before ? "added" : "coalesced into existing")}).", Cat);
        }

        [DebugAction("RimSynapse", "Memory: prune lowest unprotected (pick pawn)",
            actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void PruneLowest(Pawn p)
        {
            var comp = p?.TryGetComp<SynapseCorePawnComp>();
            if (comp == null) return;
            var victim = comp.memories.Where(m => !comp.IsCompactionProtected(m))
                                      .OrderBy(m => m.weight).FirstOrDefault();
            if (victim == null) { SynapseLogger.Message($"[RimSynapse] {p.LabelShort}: nothing unprotected to prune.", Cat); return; }
            int before = comp.memories.Count;
            bool removed = comp.RemoveMemory(victim);
            SynapseLogger.Message($"[RimSynapse] {p.LabelShort}: prune lowest [{victim.weight:0.00}] \"{Trunc(victim.summary)}\" — " +
                                  $"removed={removed}, count {before} → {comp.memories.Count}.", Cat);
        }

        private static string Trunc(string s) => string.IsNullOrEmpty(s) ? "(none)" : (s.Length > 60 ? s.Substring(0, 57) + "..." : s);
    }
}
