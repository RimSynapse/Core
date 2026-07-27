using System;
using System.Collections.Generic;
using System.Linq;

namespace RimSynapse
{
    /// <summary>
    /// The inspector's backing model (issue #17): what each agent run decided and what came
    /// back, kept so a run that goes wrong can be diagnosed from Dialog_ScriptDebugger
    /// without reading Player.log.
    ///
    /// Recording happens at the points the data already exists — the planner registers the
    /// run and its budget state per turn, SynapseActionExecutor records the plan it parsed,
    /// each executed action, and the outcome text it feeds back into RunAgentLoop — so this
    /// class never reconstructs anything. Read-only apart from CancelRun, which forwards to
    /// the planner's own cancel control.
    ///
    /// LLM callbacks arrive off the main thread, so every mutation locks; readers get
    /// snapshot copies. Text is truncated at record time: this is a diagnostic excerpt
    /// store, not a transcript (the full text is in the log).
    /// </summary>
    public static class SynapseAgentRunLog
    {
        public class AgentActionRecord
        {
            public string text;
            public bool isError;
        }

        public class AgentTurnRecord
        {
            public int index;
            public string plan;
            public string outcome;
            public List<AgentActionRecord> actions = new List<AgentActionRecord>();
        }

        public class AgentRunRecord
        {
            public int id;
            public string command;
            public bool isAutonomous;
            public string status = "Running"; // Running | Completed | Failed | Cancelled
            public string finalMessage;
            public int turnsUsed;
            public int maxTurns;
            public int requestsUsed;
            public int maxRequests;
            public List<AgentTurnRecord> turns = new List<AgentTurnRecord>();
            internal SynapseLlmPlanner planner; // nulled at EndRun so finished runs pin nothing
            public bool CanCancel => status == "Running" && planner != null;
        }

        private const int MaxRuns = 10;
        private const int PlanExcerptChars = 800;
        private const int OutcomeExcerptChars = 1200;
        private const int ActionExcerptChars = 400;

        private static readonly object _lock = new object();
        private static readonly List<AgentRunRecord> _runs = new List<AgentRunRecord>();
        private static int _nextId = 1;

        public static int BeginRun(string command, bool isAutonomous, SynapseLlmPlanner planner)
        {
            lock (_lock)
            {
                var run = new AgentRunRecord
                {
                    id = _nextId++,
                    command = Truncate(command, 300),
                    isAutonomous = isAutonomous,
                    planner = planner,
                };
                _runs.Add(run);
                while (_runs.Count > MaxRuns) _runs.RemoveAt(0);
                return run.id;
            }
        }

        /// <summary>Called by the planner as each turn starts, carrying the budget state.</summary>
        public static void RecordTurnStart(int runId, int turn, int maxTurns, int requests, int maxRequests)
        {
            lock (_lock)
            {
                var run = Find(runId);
                if (run == null) return;
                run.turnsUsed = turn;
                run.maxTurns = maxTurns;
                run.requestsUsed = requests;
                run.maxRequests = maxRequests;
                run.turns.Add(new AgentTurnRecord { index = turn });
            }
        }

        /// <summary>The response the model emitted this turn — its plan, script, or summary.</summary>
        public static void RecordPlan(int runId, string responseContent)
        {
            lock (_lock)
            {
                var turn = CurrentTurn(runId);
                if (turn != null) turn.plan = Truncate(responseContent, PlanExcerptChars);
            }
        }

        /// <summary>
        /// One executed action (a script-runner log line or a flat call's result). Error
        /// classification mirrors the executor's own: an [Error]-prefixed line or an error
        /// payload in the result.
        /// </summary>
        public static void RecordAction(int runId, string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (_lock)
            {
                var turn = CurrentTurn(runId);
                if (turn == null) return;
                turn.actions.Add(new AgentActionRecord
                {
                    text = Truncate(line, ActionExcerptChars),
                    isError = line.StartsWith("[Error]", StringComparison.Ordinal)
                           || line.IndexOf("\"error\"", StringComparison.OrdinalIgnoreCase) >= 0
                           || line.IndexOf("\"success\": false", StringComparison.OrdinalIgnoreCase) >= 0,
                });
            }
        }

        /// <summary>The outcome text fed back into RunAgentLoop at the end of a turn.</summary>
        public static void RecordOutcome(int runId, string outcome)
        {
            lock (_lock)
            {
                var turn = CurrentTurn(runId);
                if (turn != null) turn.outcome = Truncate(outcome, OutcomeExcerptChars);
            }
        }

        /// <summary>Terminal state, funnelled through the planner's onComplete wrapper.</summary>
        public static void EndRun(int runId, bool success, string message)
        {
            lock (_lock)
            {
                var run = Find(runId);
                if (run == null || run.status != "Running") return;
                run.status = success ? "Completed"
                    : (message != null && message.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0)
                        ? "Cancelled" : "Failed";
                run.finalMessage = Truncate(message, OutcomeExcerptChars);
                run.planner = null;
            }
        }

        /// <summary>Forward the inspector's cancel to the run's planner. The run reports its
        /// terminal state on the loop's next entry, through the normal completion path.</summary>
        public static bool CancelRun(int runId)
        {
            SynapseLlmPlanner planner;
            lock (_lock)
            {
                var run = Find(runId);
                planner = run?.planner;
            }
            if (planner == null) return false;
            planner.Cancel();
            return true;
        }

        /// <summary>Snapshot of recent runs, newest first. Turn lists are deep-copied so the
        /// UI can render while a background callback records.</summary>
        public static List<AgentRunRecord> GetRecentRuns()
        {
            lock (_lock)
            {
                var list = new List<AgentRunRecord>();
                for (int i = _runs.Count - 1; i >= 0; i--)
                {
                    var r = _runs[i];
                    list.Add(new AgentRunRecord
                    {
                        id = r.id,
                        command = r.command,
                        isAutonomous = r.isAutonomous,
                        status = r.status,
                        finalMessage = r.finalMessage,
                        turnsUsed = r.turnsUsed,
                        maxTurns = r.maxTurns,
                        requestsUsed = r.requestsUsed,
                        maxRequests = r.maxRequests,
                        planner = r.planner,
                        turns = r.turns.Select(t => new AgentTurnRecord
                        {
                            index = t.index,
                            plan = t.plan,
                            outcome = t.outcome,
                            actions = t.actions.Select(a => new AgentActionRecord { text = a.text, isError = a.isError }).ToList(),
                        }).ToList(),
                    });
                }
                return list;
            }
        }

        public static void ClearForTesting()
        {
            lock (_lock)
            {
                _runs.Clear();
            }
        }

        private static AgentRunRecord Find(int runId)
        {
            for (int i = _runs.Count - 1; i >= 0; i--)
            {
                if (_runs[i].id == runId) return _runs[i];
            }
            return null;
        }

        private static AgentTurnRecord CurrentTurn(int runId)
        {
            var run = Find(runId);
            if (run == null) return null;
            // Recording before any turn started (e.g. a response processed for a run whose
            // loop was never entered) still lands somewhere visible.
            if (run.turns.Count == 0) run.turns.Add(new AgentTurnRecord { index = Math.Max(1, run.turnsUsed) });
            return run.turns[run.turns.Count - 1];
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
