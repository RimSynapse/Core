using System;
using System.Collections.Generic;
using System.Linq;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Covers the agent run controls: the configurable turn limit, the per-run request
    /// budget, cancellation, script abort, and the mutating-tool gate.
    /// </summary>
    [SynapseTestSet]
    public static class AgentControlCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_TurnLimitStopsRun", () => WithSettings(s =>
            {
                s.agentMaxTurns = 1;
                var (planner, outcomes, log) = NewPlanner();

                // Drive the limit check directly: the second entry exceeds a limit of one.
                planner.RunAgentLoop(Options());
                planner.RunAgentLoop(Options());

                Assert.True(outcomes.Any(o => !o.ok && o.msg.Contains("Turn limit")),
                    "the second turn must stop on the turn limit, got: " + Join(outcomes));
                Assert.True(log.Any(l => l.Contains("turn limit reached (1/1)")),
                    "the stop must name the limit and the count, got: " + string.Join(" | ", log.Take(4)));
                return "turn limit stops the run and names the count";
            }));

            yield return new SynapseTestCase("Core_RequestBudgetStopsRun", () => WithSettings(s =>
            {
                s.agentMaxTurns = 20;
                s.agentMaxRequestsPerRun = 1;
                var (planner, outcomes, log) = NewPlanner();

                planner.RunAgentLoop(Options()); // issues request 1
                planner.RunAgentLoop(Options()); // budget exhausted

                Assert.True(outcomes.Any(o => !o.ok && o.msg.Contains("Request budget")),
                    "the run must stop on the request budget, got: " + Join(outcomes));
                Assert.True(log.Any(l => l.Contains("request budget exhausted (1/1)")),
                    "the stop must name the budget and the count");
                return "request budget stops the run independently of turns";
            }));

            yield return new SynapseTestCase("Core_CancellationStopsRun", () => WithSettings(s =>
            {
                var (planner, outcomes, log) = NewPlanner();
                planner.Cancel();
                planner.RunAgentLoop(Options());

                Assert.True(outcomes.Any(o => !o.ok && o.msg.Contains("cancelled")),
                    "a cancelled run must report cancellation, got: " + Join(outcomes));
                Assert.True(log.Any(l => l.Contains("cancelled")),
                    "cancellation must be logged");
                return "cancel flag stops the next turn before any LLM call";
            }));

            yield return new SynapseTestCase("Core_MutatingToolGate", () => WithSettings(s =>
            {
                bool executed = false;
                SynapseToolRegistry.RegisterTool(
                    "zz_test_mutator", "test tool that mutates state",
                    new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object>() },
                    args => { executed = true; return "{\"success\": true}"; },
                    isDebug: false, keywords: null, isMutating: true);

                string refused = SynapseToolRegistry.ExecuteTool("zz_test_mutator", "{}", allowMutating: false);
                Assert.Contains(refused, "error", "a gated run must get a refusal payload");
                Assert.Contains(refused, "not permitted to mutate", "the refusal must say why");
                Assert.False(executed, "the handler must not run when refused");

                string allowed = SynapseToolRegistry.ExecuteTool("zz_test_mutator", "{}", allowMutating: true);
                Assert.Contains(allowed, "success", "an allowed call must execute normally");
                Assert.True(executed, "the handler must run when allowed");
                return "mutating tool refused when gated, runs when allowed";
            }));

            yield return new SynapseTestCase("Core_CoreMutatorsAreFlagged", () => WithSettings(s =>
            {
                // The COMPLETE manifest (Core#101) — execute_game_tool matters most:
                // unflagged, it would launder any mutation through the gate.
                var flagged = SynapseToolRegistry.AllTools
                    .Where(t => t.isMutating).Select(t => t.name).ToList();
                foreach (var name in new[]
                {
                    "possess_colonist", "damage_self_with_equipped", "modify_pawn_state",
                    "execute_game_tool", "fire_incident", "send_notification_letter",
                    "modify_object_state", "control_turret", "fire_weapon_at_cell",
                    "trigger_colonist_break", "attempt_remote_hack", "spawn_hacker_base",
                    "set_game_volume",
                })
                {
                    Assert.True(flagged.Contains(name), $"'{name}' must be flagged as mutating");
                }
                Assert.False(flagged.Contains("get_stored_result"),
                    "read-only tools must not be flagged");
                Assert.False(flagged.Contains("write_debugger_log"),
                    "write_debugger_log is deliberately exempt (diagnostic-only)");
                return $"{flagged.Count} tools flagged, launder path closed, manifest complete";
            }));

            yield return new SynapseTestCase("Core_ScriptAbort", () => WithSettings(s =>
            {
                var log = new List<string>();
                bool finished = false;
                var script = new SynapseScript
                {
                    scriptName = "zz-abort-me",
                    steps = new List<SynapseScriptStep>
                    {
                        new SynapseScriptStep
                        {
                            type = "wait_until",
                            arguments = new Dictionary<string, object>
                            {
                                ["condition"] = "never_true_condition",
                                ["pawnName"] = "nobody",
                                ["timeoutTicks"] = 999999
                            }
                        }
                    }
                };

                SynapseScriptRunner.StartScript(script, log.Add, () => finished = true);
                Assert.True(SynapseScriptRunner.GetActiveScriptNames().Contains("zz-abort-me"),
                    "the script should be active and waiting");

                bool aborted = SynapseScriptRunner.AbortScript("zz-abort-me");
                Assert.True(aborted, "AbortScript must report success for an active script");
                Assert.False(SynapseScriptRunner.GetActiveScriptNames().Contains("zz-abort-me"),
                    "the script must be gone after abort");
                Assert.True(finished, "onFinished must still run so an agent chain resolves");
                Assert.True(log.Any(l => l.Contains("aborted")), "the abort must be logged");

                Assert.False(SynapseScriptRunner.AbortScript("zz-abort-me"),
                    "aborting a script that is not active must return false");
                return "waiting script aborted; onFinished ran; second abort refused";
            }));
        }

        private static (SynapseLlmPlanner planner, List<(bool ok, string msg)> outcomes, List<string> log) NewPlanner()
        {
            var outcomes = new List<(bool, string)>();
            var log = new List<string>();
            var planner = new SynapseLlmPlanner("test command",
                l => { lock (log) log.Add(l ?? string.Empty); },
                (ok, msg) => { lock (outcomes) outcomes.Add((ok, msg ?? string.Empty)); });
            return (planner, outcomes, log);
        }

        private static ChatOptions Options()
        {
            return new ChatOptions { priority = 1, requestName = "TestRunner AgentControl" };
        }

        private static string Join(List<(bool ok, string msg)> outcomes)
        {
            lock (outcomes)
            {
                return outcomes.Count == 0 ? "<none>" :
                    string.Join(" | ", outcomes.Select(o => $"{o.ok}:{o.msg}"));
            }
        }

        private static string WithSettings(Func<RimSynapseSettings, string> body)
        {
            var s = RimSynapseMod.Instance?.Settings;
            Assert.NotNull(s, "settings unavailable");
            var savedTurns = s.agentMaxTurns;
            var savedRequests = s.agentMaxRequestsPerRun;
            var savedMutations = s.allowAutonomousMutations;
            try
            {
                return body(s);
            }
            finally
            {
                s.agentMaxTurns = savedTurns;
                s.agentMaxRequestsPerRun = savedRequests;
                s.allowAutonomousMutations = savedMutations;
            }
        }
    }
}
