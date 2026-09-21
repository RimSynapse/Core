using System.Collections.Generic;
using System.Linq;
using LudeonTK;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for the leadership hierarchy hook (Psychology #21). Temporarily registers a throwaway
    /// provider over real colonists — a "faction leader" the others report to — and asserts the pawn-centric
    /// surface (ReportsToPawn / DirectReportPawns / ChainOfCommandPawns / RoleTitle) resolves through it, then
    /// restores whatever provider was there. Headlessly runnable via run_debug_action.
    /// </summary>
    public static class DebugActions_Hierarchy
    {
        private sealed class FakeProvider : SynapseCoreHierarchy.IHierarchyProvider
        {
            public Pawn leader;
            public readonly List<Pawn> reports = new List<Pawn>();
            public object ReportsTo(object node) => (node is Pawn p && reports.Contains(p)) ? leader : null;
            public IEnumerable<object> DirectReports(object node) => ReferenceEquals(node, leader) ? reports.Cast<object>() : Enumerable.Empty<object>();
            public string RoleTitle(object node) => ReferenceEquals(node, leader) ? "Faction Leader" : (node is Pawn p && reports.Contains(p) ? "Settlement Leader" : null);
        }

        [DebugAction("RimSynapse", "Hierarchy: probe reports-to hook (#21)",
            allowedGameStates = AllowedGameStates.Playing)]
        private static void ProbeHierarchy()
        {
            var pawns = Find.CurrentMap?.mapPawns?.FreeColonists?.ToList();
            if (pawns == null || pawns.Count < 2)
            {
                SynapseLogger.Warning("[RimSynapse #21] Hierarchy probe needs >=2 colonists on the map.");
                return;
            }

            var saved = SynapseCoreHierarchy.Provider;
            var f = new FakeProvider { leader = pawns[0] };
            f.reports.AddRange(pawns.Skip(1).Take(2));
            SynapseCoreHierarchy.Provider = f;
            try
            {
                Pawn leader = f.leader, report = f.reports[0];
                bool reportsToOk = SynapseCoreHierarchy.ReportsToPawn(report) == leader;
                bool leaderTopOk = SynapseCoreHierarchy.ReportsToPawn(leader) == null;
                int directReports = SynapseCoreHierarchy.DirectReportPawns(leader).Count();
                var chain = SynapseCoreHierarchy.ChainOfCommandPawns(report);
                bool chainOk = chain.Count == 1 && chain[0] == leader;
                string role = SynapseCoreHierarchy.RoleTitle(report);

                bool pass = reportsToOk && leaderTopOk && directReports == f.reports.Count && chainOk && role == "Settlement Leader";
                SynapseLogger.Message(
                    $"[RimSynapse #21] Hierarchy hook: {(pass ? "PASS" : "FAIL")}\n" +
                    $"  {report.LabelShort} reports to {leader.LabelShort}: {reportsToOk}; leader is top: {leaderTopOk}\n" +
                    $"  direct reports of {leader.LabelShort}: {directReports} (expect {f.reports.Count})\n" +
                    $"  chain-of-command length {chain.Count} (expect 1); role '{role}'");
            }
            finally { SynapseCoreHierarchy.Provider = saved; }
        }
    }
}
