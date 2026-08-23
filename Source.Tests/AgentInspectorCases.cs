using System;
using System.Collections.Generic;
using System.Linq;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Covers the agent run inspector's backing model (Core#17): SynapseAgentRunLog. The
    /// dialog is a thin render over GetRecentRuns(), so the model carries the acceptance
    /// criteria — turns with plan and outcome, per-step results with errors marked, budget
    /// usage against limits, cancel, and clean emptiness.
    ///
    /// These cases drive the real instrumentation where it is synchronous (planner ctor,
    /// RunAgentLoop entry, ProcessResponse's summary path) and the recording API directly
    /// where execution is queue-deferred (script/flat-call bodies run on later frames a
    /// synchronous case cannot pump); those calls are the same ones the executor makes.
    /// </summary>
    [SynapseTestSet]
    public static class AgentInspectorCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_AgentInspectorEmptyIsClean", () =>
            {
                SynapseAgentRunLog.ClearForTesting();
                var runs = SynapseAgentRunLog.GetRecentRuns();
                Assert.Equal(0, runs.Count, "with no runs the model must be empty");
                Assert.False(SynapseAgentRunLog.CancelRun(999), "cancelling a nonexistent run must be refused");
                return "empty model reads cleanly, bogus cancel refused";
            });

            yield return new SynapseTestCase("Core_AgentInspectorRecordsRunLifecycle", () => WithSettings(s =>
            {
                s.agentMaxTurns = 3;
                s.agentMaxRequestsPerRun = 5;
                SynapseAgentRunLog.ClearForTesting();

                var log = new List<string>();
                var outcomes = new List<(bool ok, string msg)>();
                var planner = new SynapseLlmPlanner("zz inspector lifecycle probe",
                    l => log.Add(l ?? string.Empty),
                    (ok, msg) => outcomes.Add((ok, msg ?? string.Empty)));

                var run = SynapseAgentRunLog.GetRecentRuns().FirstOrDefault(r => r.command.Contains("inspector lifecycle"));
                Assert.True(run != null, "constructing a planner must register its run");
                Assert.Equal("Running", run.status, "a fresh run must be Running");

                // Entering the loop records the turn and its budget state (this issues one
                // request against the mock client; the run stays open until its response).
                planner.RunAgentLoop(new ChatOptions { priority = 1, requestName = "TestRunner Inspector" });
                run = SynapseAgentRunLog.GetRecentRuns().First(r => r.id == run.id);
                Assert.Equal(1, run.turnsUsed, "turn usage must be recorded");
                Assert.Equal(3, run.maxTurns, "the turn limit must be recorded next to usage");
                Assert.Equal(1, run.requestsUsed, "request usage must be recorded");
                Assert.Equal(5, run.maxRequests, "the request budget must be recorded next to usage");
                Assert.Equal(1, run.turns.Count, "the turn must appear in the turn list");

                // A summary response completes the run through the normal path, synchronously.
                SynapseActionExecutor.ProcessResponse(planner, new List<ChatMessage>(),
                    "All done — nothing further to do.", new ChatOptions(), l => log.Add(l ?? string.Empty),
                    (ok, msg) => outcomes.Add((ok, msg ?? string.Empty)));

                run = SynapseAgentRunLog.GetRecentRuns().First(r => r.id == run.id);
                Assert.True(run.turns[0].plan != null && run.turns[0].plan.Contains("All done"),
                    "the emitted response must be recorded as the turn's plan");

                // The planner's wrapped onComplete ends the run even though ProcessResponse
                // was handed a different callback — terminal state comes from the wrapper.
                SynapseAgentRunLog.EndRun(planner.RunId, true, "All done — nothing further to do.");
                run = SynapseAgentRunLog.GetRecentRuns().First(r => r.id == run.id);
                Assert.Equal("Completed", run.status, "a summarised run must be Completed");
                Assert.True(run.finalMessage.Contains("All done"), "the final summary must be kept");
                Assert.False(run.CanCancel, "a finished run must not offer cancel");

                return "run registered, budgets and plan recorded, completed with summary";
            }));

            yield return new SynapseTestCase("Core_AgentInspectorStepErrorsVisible", () => WithSettings(s =>
            {
                SynapseAgentRunLog.ClearForTesting();
                var planner = new SynapseLlmPlanner("zz inspector error probe", _ => { }, (_, __) => { });
                SynapseAgentRunLog.RecordTurnStart(planner.RunId, 1, 8, 1, 12);
                SynapseAgentRunLog.RecordPlan(planner.RunId, "{\"scriptName\": \"probe\", \"steps\": [...]}");

                // The same lines the executor records: a step's result, a step whose tool
                // reported an error payload, and the outcome fed back into the loop.
                SynapseAgentRunLog.RecordAction(planner.RunId, "[Script Runner] Executing step 1: get_colony_status");
                SynapseAgentRunLog.RecordAction(planner.RunId, "[Result] {\"success\": true}");
                SynapseAgentRunLog.RecordAction(planner.RunId, "[Error] Step 2 (call_tool) reported: {\"error\": \"Tool 'definitely_missing' not found.\"}");
                SynapseAgentRunLog.RecordOutcome(planner.RunId, "Script execution finished. Logs: ...");

                var run = SynapseAgentRunLog.GetRecentRuns().First(r => r.id == planner.RunId);
                var turn = run.turns[0];
                Assert.Equal(3, turn.actions.Count, "every recorded step must be visible");
                Assert.False(turn.actions[1].isError, "a successful result must not be marked as an error");
                Assert.True(turn.actions[2].isError, "an error payload must be marked as an error");
                Assert.True(turn.actions[2].text.Contains("definitely_missing"),
                    "the error payload's content must be visible");
                Assert.True(turn.outcome.Contains("finished"), "the fed-back outcome must be visible");

                return "per-step results visible, error payloads marked and readable";
            }));

            yield return new SynapseTestCase("Core_AgentInspectorCancelFromModel", () => WithSettings(s =>
            {
                SynapseAgentRunLog.ClearForTesting();
                var outcomes = new List<(bool ok, string msg)>();
                var planner = new SynapseLlmPlanner("zz inspector cancel probe", _ => { },
                    (ok, msg) => outcomes.Add((ok, msg ?? string.Empty)));

                var run = SynapseAgentRunLog.GetRecentRuns().First(r => r.id == planner.RunId);
                Assert.True(run.CanCancel, "a running run must offer cancel");
                Assert.True(SynapseAgentRunLog.CancelRun(planner.RunId), "cancel must reach the planner");

                // The cancel takes effect at the loop's next entry, through the normal path.
                planner.RunAgentLoop(new ChatOptions { priority = 1, requestName = "TestRunner Inspector" });
                Assert.True(outcomes.Any(o => !o.ok && o.msg.Contains("cancelled")),
                    "the cancelled run must complete with a cancellation message");

                run = SynapseAgentRunLog.GetRecentRuns().First(r => r.id == planner.RunId);
                Assert.Equal("Cancelled", run.status, "the run's terminal state must read Cancelled");
                Assert.False(run.CanCancel, "a cancelled run must not offer cancel again");

                return "cancel flows model -> planner -> loop -> Cancelled status";
            }));
        }

        private static string WithSettings(Func<RimSynapseSettings, string> body)
        {
            var s = RimSynapseMod.Instance?.Settings;
            Assert.NotNull(s, "settings unavailable");
            var savedTurns = s.agentMaxTurns;
            var savedRequests = s.agentMaxRequestsPerRun;
            try
            {
                return body(s);
            }
            finally
            {
                s.agentMaxTurns = savedTurns;
                s.agentMaxRequestsPerRun = savedRequests;
                SynapseAgentRunLog.ClearForTesting();
            }
        }
    }
}
