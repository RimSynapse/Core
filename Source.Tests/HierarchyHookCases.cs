using System.Collections.Generic;
using System.Linq;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// The open-ended leadership hierarchy hook (Core, for Psychology #21 / Factions). Verifies the seam is
    /// dormant with no provider, that a registered provider answers reports-to / direct-reports / role /
    /// chain-of-command over arbitrary (untyped) nodes, and that the chain walk is cycle-guarded. Uses plain
    /// string nodes to prove the surface is genuinely open-ended (not pawn-bound). Restores the global
    /// provider afterwards so no other case sees a stray org chart.
    /// </summary>
    [SynapseTestSet]
    public static class HierarchyHookCases
    {
        /// <summary>A tiny in-memory org chart over string nodes: faction leader ← two settlement leaders.</summary>
        private sealed class FakeProvider : SynapseCoreHierarchy.IHierarchyProvider
        {
            public readonly Dictionary<object, object> superior = new Dictionary<object, object>();
            public readonly Dictionary<object, string> title = new Dictionary<object, string>();
            public object ReportsTo(object node) => node != null && superior.TryGetValue(node, out var s) ? s : null;
            public IEnumerable<object> DirectReports(object node) => superior.Where(kv => Equals(kv.Value, node)).Select(kv => kv.Key);
            public string RoleTitle(object node) => node != null && title.TryGetValue(node, out var t) ? t : null;
        }

        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_Hierarchy_DormantWithoutProvider", () =>
            {
                var saved = SynapseCoreHierarchy.Provider;
                SynapseCoreHierarchy.Provider = null;
                try
                {
                    Assert.True(!SynapseCoreHierarchy.HasProvider, "no provider registered by default");
                    Assert.True(SynapseCoreHierarchy.ReportsTo("anything") == null, "reports-to is null when dormant");
                    Assert.True(SynapseCoreHierarchy.RoleTitle("anything") == null, "role is null when dormant");
                    Assert.Equal(0, SynapseCoreHierarchy.DirectReports("anything").Count(), "direct-reports is empty when dormant");
                    Assert.Equal(0, SynapseCoreHierarchy.ChainOfCommand("anything").Count, "chain is empty when dormant");
                    return "hook present but dormant";
                }
                finally { SynapseCoreHierarchy.Provider = saved; }
            },
            tier: "Execution", polarity: "negative",
            scenario: "The hierarchy hook is queried with no mod providing a structure",
            expectation: "Every query is null/empty — the hook is inert until a provider registers");

            yield return new SynapseTestCase("Core_Hierarchy_ProviderAnswersQueries", () =>
            {
                var saved = SynapseCoreHierarchy.Provider;
                var f = new FakeProvider();
                // faction leader "FL"; settlement leaders "A" and "B" report to it.
                f.superior["A"] = "FL"; f.superior["B"] = "FL";
                f.title["FL"] = "Faction Leader"; f.title["A"] = "Leader of Alpha"; f.title["B"] = "Leader of Beta";
                SynapseCoreHierarchy.Provider = f;
                try
                {
                    Assert.True(SynapseCoreHierarchy.HasProvider, "provider registered");
                    Assert.Equal("FL", (string)SynapseCoreHierarchy.ReportsTo("A"), "A reports to the faction leader");
                    Assert.Equal("Leader of Alpha", SynapseCoreHierarchy.RoleTitle("A"), "role title flows through");
                    var reports = SynapseCoreHierarchy.DirectReports("FL").Cast<string>().OrderBy(s => s).ToList();
                    Assert.Equal(2, reports.Count, "the faction leader has two direct reports");
                    Assert.True(reports[0] == "A" && reports[1] == "B", "both settlement leaders report up");
                    var chain = SynapseCoreHierarchy.ChainOfCommand("A").Cast<string>().ToList();
                    Assert.Equal(1, chain.Count, "A's chain of command is just the faction leader");
                    Assert.Equal("FL", chain[0], "the top of A's chain is the faction leader");
                    Assert.True(SynapseCoreHierarchy.ReportsTo("FL") == null, "the faction leader reports to no one");
                    return $"FL <- [{string.Join(",", reports)}]";
                }
                finally { SynapseCoreHierarchy.Provider = saved; }
            },
            tier: "Execution", polarity: "positive",
            scenario: "A mod registers an org chart of untyped nodes",
            expectation: "Reports-to, direct-reports, role, and chain-of-command all resolve through the provider");

            yield return new SynapseTestCase("Core_Hierarchy_ChainIsCycleGuarded", () =>
            {
                var saved = SynapseCoreHierarchy.Provider;
                var f = new FakeProvider();
                f.superior["X"] = "Y"; f.superior["Y"] = "X"; // a malformed cycle
                SynapseCoreHierarchy.Provider = f;
                try
                {
                    var chain = SynapseCoreHierarchy.ChainOfCommand("X").Cast<string>().ToList();
                    Assert.True(chain.Count <= 2, $"a cyclic chart terminates instead of looping (len {chain.Count})");
                    return $"cycle terminated at {chain.Count}";
                }
                finally { SynapseCoreHierarchy.Provider = saved; }
            },
            tier: "Execution", polarity: "negative",
            scenario: "A provider returns a cyclic reports-to relationship",
            expectation: "ChainOfCommand terminates rather than looping forever");
        }
    }
}
