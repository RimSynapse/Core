using System.Diagnostics;
using System.Collections.Generic;
using RimSynapse;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Guards the async break-solver fix (Core #120). The <c>trigger_colonist_break</c> tool handler
    /// runs on the main thread and used to make a blocking LLM HTTP call inline, freezing the game
    /// for the whole call. <see cref="SynapseToolRegistry.DispatchBreakResolution"/> now hands the
    /// solver off to a background thread and applies the action from a main-thread callback, so the
    /// dispatch returns immediately.
    ///
    /// The case calls the dispatch with a null pawn: the completion callback then applies nothing
    /// (non-destructive — it never possesses or harms a colonist), while the background solver still
    /// runs. The assertion is that the SYNCHRONOUS dispatch returns effectively instantly rather than
    /// blocking for the solver's duration. (If a regression reintroduced an inline blocking call, a
    /// reachable-but-slow backend — the reported failure mode — would push this well past the
    /// threshold.)
    /// </summary>
    [SynapseTestSet]
    public static class BreakSolverCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_BreakSolverDispatchIsNonBlocking", () =>
            {
                var sw = Stopwatch.StartNew();
                SynapseToolRegistry.DispatchBreakResolution(
                    pawn: null,
                    abstractReason: "Node under high tension; conflict with Square.",
                    targetPawnName: null,
                    targetX: null,
                    targetZ: null,
                    disableStripping: false,
                    pawnName: "[test-probe]",
                    filePathToDelete: null);
                sw.Stop();

                Assert.True(sw.ElapsedMilliseconds < 200,
                    $"break-resolution dispatch must return without blocking on the solver LLM call " +
                    $"(returned in {sw.ElapsedMilliseconds}ms; the whole game freezes here if it blocks)");

                return $"dispatch returned in {sw.ElapsedMilliseconds}ms without blocking the main thread";
            },
                tier: "Execution", polarity: "positive",
                scenario: "A colonist break is resolved while the backend is slow to respond",
                expectation: "The tool dispatches the solver asynchronously; the main thread never blocks on the LLM call");
        }
    }
}
