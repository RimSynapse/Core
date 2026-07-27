using System;
using System.Collections.Generic;

namespace RimSynapse
{
    /// <summary>What a programmed path hands the agent when reality diverged.</summary>
    public class SynapseEscalationContext
    {
        /// <summary>Which system and call site is escalating, e.g. "Psychology.CeremonyRecord".</summary>
        public string Origin;
        /// <summary>What the programmed path was trying to produce.</summary>
        public string Expectation;
        /// <summary>What actually happened. Large payloads are abbreviated to excerpt + handle.</summary>
        public string Observation;
        /// <summary>Optional: what the agent should try to achieve. Defaults to conservative assessment.</summary>
        public string SuggestedGoal;
    }

    public class SynapseEscalationRecord
    {
        public DateTime Utc;
        public string Origin;
        public bool Started;
        public string Reason;
    }

    /// <summary>
    /// Lets a programmed path hand what it knows to the agent instead of logging a warning
    /// and dropping the interaction — the "nothing went as expected" moments the agent
    /// exists for.
    ///
    /// The guardrails are the bulk of the design, because a broken backend must not
    /// convert every failing hook into an agent run:
    ///  - opt-in: default-off setting; a refused call is a cheap no-op and the call site's
    ///    original warning still logs,
    ///  - rate-limited: a global cooldown plus a per-session cap,
    ///  - tier-aware: on Auto, the Minimal tier refuses — a 2k model burning turns on
    ///    recovery is worse than the skip; a manual tier override wins either way,
    ///  - labelled: every escalated run carries its origin in the logs, and recent
    ///    attempts (started or refused, with reasons) are kept for the run inspector.
    ///
    /// Escalated runs are autonomous: they cannot call mutating tools unless the player
    /// has separately enabled allowAutonomousMutations.
    /// </summary>
    public static class SynapseAgentEscalation
    {
        private static readonly object _lock = new object();
        private static DateTime _lastStartUtc = DateTime.MinValue;
        private static int _sessionCount;
        private static readonly List<SynapseEscalationRecord> _recent = new List<SynapseEscalationRecord>();
        private const int RecentCap = 20;

        /// <summary>Recent attempts, newest last — surfaced by the run inspector.</summary>
        public static List<SynapseEscalationRecord> RecentSnapshot()
        {
            lock (_lock) return new List<SynapseEscalationRecord>(_recent);
        }

        /// <summary>
        /// Attempt an escalation. Returns true when an agent run was started; false means
        /// refused, with the reason logged and recorded. Never throws.
        /// </summary>
        public static bool Escalate(SynapseEscalationContext ctx)
        {
            if (ctx == null || string.IsNullOrEmpty(ctx.Origin)) return false;

            var settings = RimSynapseMod.Instance?.Settings;

            if (settings == null || !settings.enableEscalation)
                return Refuse(ctx.Origin, "disabled in settings");

            if (SynapseTierController.Mode == SynapseTierMode.Auto
                && SynapseTierController.Current == SynapseCapabilityTier.Minimal)
                return Refuse(ctx.Origin, "tier is Minimal (auto) — recovery turns cost more than the skip");

            lock (_lock)
            {
                int cooldown = Math.Max(0, settings.escalationCooldownSeconds);
                if ((DateTime.UtcNow - _lastStartUtc).TotalSeconds < cooldown)
                    return RefuseLocked(ctx.Origin, $"cooldown ({cooldown}s)");

                if (_sessionCount >= Math.Max(1, settings.escalationSessionCap))
                    return RefuseLocked(ctx.Origin, $"session cap ({settings.escalationSessionCap}) reached");

                _lastStartUtc = DateTime.UtcNow;
                _sessionCount++;
                Record(ctx.Origin, true, "started");
            }

            string origin = ctx.Origin;
            string command = BuildEscalationCommand(ctx);
            SynapseLogger.Message($"[Escalation] Started from {origin}.", "performance");

            // The planner touches game state; make sure it starts on the main thread
            // regardless of which thread the failing path noticed the divergence on.
            SynapseGameComponent.Enqueue(() =>
            {
                try
                {
                    var planner = new SynapseLlmPlanner(
                        command,
                        line => SynapseLogger.Message($"[Escalation:{origin}] {line}", "performance"),
                        (ok, msg) => SynapseLogger.Message(
                            $"[Escalation:{origin}] Finished (success={ok}): {Truncate(msg)}", "performance"),
                        isAutonomous: true);
                    planner.Start();
                }
                catch (Exception ex)
                {
                    SynapseLogger.Warning($"[Escalation:{origin}] Failed to start: {ex.Message}", "performance");
                }
            });

            return true;
        }

        /// <summary>
        /// The seeded instruction for an escalated run. Public so the format is testable;
        /// the observation is abbreviated so a huge payload becomes excerpt + handle.
        /// </summary>
        public static string BuildEscalationCommand(SynapseEscalationContext ctx)
        {
            string observation = SynapseResultStore.AbbreviateIfLarge(ctx.Observation ?? "(none)", 800);
            string goal = string.IsNullOrEmpty(ctx.SuggestedGoal)
                ? "Assess whether anything should be done, and do the minimum that restores a sensible state. Doing nothing is a valid outcome."
                : ctx.SuggestedGoal;

            return "ESCALATION from a programmed system that hit an outcome it was not built for.\n"
                 + $"Origin: {ctx.Origin}\n"
                 + $"Expected: {ctx.Expectation ?? "(unspecified)"}\n"
                 + $"Observed: {observation}\n"
                 + $"Goal: {goal}\n"
                 + "Investigate with read-only tools before acting, and be conservative.";
        }

        public static void ResetForTesting()
        {
            lock (_lock)
            {
                _lastStartUtc = DateTime.MinValue;
                _sessionCount = 0;
                _recent.Clear();
            }
        }

        private static bool Refuse(string origin, string reason)
        {
            lock (_lock) return RefuseLocked(origin, reason);
        }

        private static bool RefuseLocked(string origin, string reason)
        {
            Record(origin, false, reason);
            // Debug-level on the disabled path: an off feature should not chatter.
            if (reason == "disabled in settings")
                SynapseLogger.Debug($"[Escalation] Refused from {origin}: {reason}", "performance");
            else
                SynapseLogger.Message($"[Escalation] Refused from {origin}: {reason}", "performance");
            return false;
        }

        private static void Record(string origin, bool started, string reason)
        {
            _recent.Add(new SynapseEscalationRecord
            {
                Utc = DateTime.UtcNow,
                Origin = origin,
                Started = started,
                Reason = reason
            });
            while (_recent.Count > RecentCap) _recent.RemoveAt(0);
        }

        private static string Truncate(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(no message)";
            return s.Length <= 160 ? s : s.Substring(0, 160) + "...";
        }
    }
}
