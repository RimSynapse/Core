using LudeonTK;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for the async break-solver fix (Core #120), grouped under "RimSynapse".
    /// The <c>trigger_colonist_break</c> tool used to make a blocking LLM HTTP call on the main
    /// thread, freezing the game for the whole call. It now dispatches the isolated solver to a
    /// background thread and applies the action from a main-thread callback. This probe calls the
    /// dispatch path with a null pawn (so nothing is applied — non-destructive) and measures how
    /// long the SYNCHRONOUS call takes: a couple of ms proves the main thread is no longer blocked.
    /// Headlessly runnable via run_debug_action.
    /// </summary>
    public static class DebugActions_BreakSolver
    {
        [DebugAction("RimSynapse", "Break solver: dispatch is non-blocking (Core #120)",
            allowedGameStates = AllowedGameStates.Playing)]
        private static void ProbeBreakSolverNonBlocking()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Null pawn: the async callback logs "skipped" and applies no action — this probe never
            // possesses or harms a colonist. What it proves is that the dispatch returns immediately
            // rather than blocking the main thread on the solver's LLM call.
            SynapseToolRegistry.DispatchBreakResolution(
                pawn: null,
                abstractReason: "Node under high tension; conflict with Square.",
                targetPawnName: null,
                targetX: null,
                targetZ: null,
                disableStripping: false,
                pawnName: "[debug-probe]",
                filePathToDelete: null);

            sw.Stop();

            bool nonBlocking = sw.ElapsedMilliseconds < 100;
            SynapseLogger.Message(
                $"[RimSynapse] Break solver dispatch (Core #120): returned in {sw.ElapsedMilliseconds}ms on the main thread. " +
                $"Non-blocking: {(nonBlocking ? "YES" : "NO (bug — main thread blocked on the LLM call)")}. " +
                "The isolated solver now runs on a background thread; a follow-up " +
                "'[RimSynapse] Break resolution for [debug-probe]' line appears when it returns.");
        }
    }
}
