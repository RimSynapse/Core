using System.Text;
using LudeonTK;
using Verse;
using RimSynapse.Comps;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for job-name humanization in <see cref="SynapseCorePawnComp.GetRecentJobsSummary"/>,
    /// grouped under "RimSynapse". A labelless JobDef (the canonical case: GotoWander, which has no &lt;label&gt;
    /// but a clean &lt;reportString&gt; "wandering.") must surface as a readable phrase, never the raw defName —
    /// which was leaking into ambient dialogue as "the gotowander they've been busy with". Uses a fresh comp
    /// with injected intervals so it is deterministic and needs no pawn to actually be wandering. Headlessly
    /// runnable via run_debug_action.
    /// </summary>
    public static class DebugActions_ActivitySummary
    {
        [DebugAction("RimSynapse", "Activity summary: labelless job humanizes (no raw defName)",
            allowedGameStates = AllowedGameStates.Playing)]
        private static void ProbeJobHumanization()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[RimSynapse] Job-name humanization (GetRecentJobsSummary):");

            int now = Find.TickManager.TicksGame;

            // GotoWander: no label, clean reportString -> should read "wandering".
            var viaReport = new SynapseCorePawnComp { lastJobStartedTick = -1 };
            viaReport.recentJobs.Add(new JobInterval("GotoWander", now - 5000, 4000));
            string reportSummary = viaReport.GetRecentJobsSummary();
            bool leaks = reportSummary.ToLowerInvariant().Contains("gotowander");
            sb.AppendLine($"  GotoWander -> '{reportSummary}'");
            sb.AppendLine($"    leaks raw defName? {leaks}  (expected False)");
            sb.AppendLine($"    reads as 'wandering'? {reportSummary.Contains("wandering")}  (expected True)");

            // A defName with neither label nor a resolvable JobDef falls back to spaced camelCase.
            var viaCamel = new SynapseCorePawnComp { lastJobStartedTick = -1 };
            viaCamel.recentJobs.Add(new JobInterval("SomeMadeUpJobDef", now - 5000, 4000));
            string camelSummary = viaCamel.GetRecentJobsSummary();
            sb.AppendLine($"  SomeMadeUpJobDef -> '{camelSummary}'  (expected spaced/lowercase, not the raw token)");

            SynapseLogger.Message(sb.ToString().TrimEnd());
        }
    }
}
