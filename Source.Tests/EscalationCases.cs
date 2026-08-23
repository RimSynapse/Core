using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Covers escalation: the seam that lets programmed paths hand unexpected outcomes to
    /// the agent, and — mostly — its guardrails, since a broken backend must never convert
    /// every failing hook into an agent run.
    /// </summary>
    [SynapseTestSet]
    public static class EscalationCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_EscalationDisabledIsNoOp", () => WithSettings(s =>
            {
                s.enableEscalation = false;
                bool started = SynapseAgentEscalation.Escalate(Ctx("Test.Disabled"));
                Assert.False(started, "escalation must be a no-op while disabled");
                var rec = SynapseAgentEscalation.RecentSnapshot().LastOrDefault();
                Assert.True(rec != null && !rec.Started && rec.Reason.Contains("disabled"),
                    "the refusal must be recorded with its reason");
                return "disabled setting refuses cheaply, recorded";
            }));

            yield return new SynapseTestCase("Core_EscalationStartsLabelledRun", () => WithSettings(s =>
            {
                s.enableEscalation = true;
                s.escalationCooldownSeconds = 0;
                s.agentTierMode = 2;   // Standard: clear the tier gate deterministically
                s.agentMaxTurns = 1;   // keep the mock-backed run short

                bool started = SynapseAgentEscalation.Escalate(Ctx("Test.Origin"));
                Assert.True(started, "an enabled, in-budget escalation must start a run");
                Assert.True(RecentLogLines().Any(l => l.Contains("[Escalation] Started from Test.Origin")),
                    "the start must be logged with its origin");
                var rec = SynapseAgentEscalation.RecentSnapshot().LastOrDefault();
                Assert.True(rec != null && rec.Started, "the start must be recorded");
                return "escalation started and labelled with its origin";
            }));

            yield return new SynapseTestCase("Core_EscalationCooldownRefuses", () => WithSettings(s =>
            {
                s.enableEscalation = true;
                s.escalationCooldownSeconds = 3600;
                s.agentTierMode = 2;
                s.agentMaxTurns = 1;

                Assert.True(SynapseAgentEscalation.Escalate(Ctx("Test.First")), "first must start");
                Assert.False(SynapseAgentEscalation.Escalate(Ctx("Test.Second")),
                    "a second escalation inside the cooldown must be refused");
                var rec = SynapseAgentEscalation.RecentSnapshot().LastOrDefault();
                Assert.True(rec != null && rec.Reason.Contains("cooldown"),
                    "the refusal reason must name the cooldown, got: " + rec?.Reason);
                return "cooldown refused the second attempt";
            }));

            yield return new SynapseTestCase("Core_EscalationSessionCapRefuses", () => WithSettings(s =>
            {
                s.enableEscalation = true;
                s.escalationCooldownSeconds = 0;
                s.escalationSessionCap = 1;
                s.agentTierMode = 2;
                s.agentMaxTurns = 1;

                Assert.True(SynapseAgentEscalation.Escalate(Ctx("Test.CapFirst")), "first must start");
                Assert.False(SynapseAgentEscalation.Escalate(Ctx("Test.CapSecond")),
                    "the session cap must refuse past the limit");
                var rec = SynapseAgentEscalation.RecentSnapshot().LastOrDefault();
                Assert.True(rec != null && rec.Reason.Contains("session cap"),
                    "the refusal reason must name the cap, got: " + rec?.Reason);
                return "session cap enforced after one run";
            }));

            yield return new SynapseTestCase("Core_EscalationTierGateRefuses", () => WithSettings(s =>
            {
                s.enableEscalation = true;
                s.escalationCooldownSeconds = 0;
                s.agentTierMode = 0; // Auto
                SynapseTierController.ResetForTesting(); // Auto with no evidence => Minimal

                Assert.False(SynapseAgentEscalation.Escalate(Ctx("Test.Tier")),
                    "Auto + Minimal must refuse — recovery turns cost more than the skip");
                var rec = SynapseAgentEscalation.RecentSnapshot().LastOrDefault();
                Assert.True(rec != null && rec.Reason.Contains("Minimal"),
                    "the refusal must name the tier, got: " + rec?.Reason);

                s.agentTierMode = 3; // manual Rich overrides the gate
                s.agentMaxTurns = 1;
                Assert.True(SynapseAgentEscalation.Escalate(Ctx("Test.TierManual")),
                    "a manual tier override must win over the auto gate");
                return "Minimal(auto) refused; manual override allowed";
            }));

            yield return new SynapseTestCase("Core_EscalationObservationAbbreviated", () => WithSettings(s =>
            {
                var ctx = Ctx("Test.BigObservation");
                ctx.Observation = new string('x', 6000);
                string command = SynapseAgentEscalation.BuildEscalationCommand(ctx);

                Assert.True(command.Length < 2500,
                    $"a huge observation must not inflate the seeded command, got {command.Length} chars");
                Assert.Contains(command, "res_", "the full observation must be retrievable by handle");
                foreach (var part in new[] { "Origin:", "Expected:", "Observed:", "Goal:" })
                {
                    Assert.Contains(command, part, $"the seeded command must carry {part}");
                }
                return $"6000-char observation -> {command.Length}-char command with handle";
            }));
        }

        private static SynapseEscalationContext Ctx(string origin)
        {
            return new SynapseEscalationContext
            {
                Origin = origin,
                Expectation = "a well-formed test outcome",
                Observation = "the test outcome was missing",
                SuggestedGoal = "conclude the test"
            };
        }

        private static string WithSettings(Func<RimSynapseSettings, string> body)
        {
            var s = RimSynapseMod.Instance?.Settings;
            Assert.NotNull(s, "settings unavailable");
            var savedEnable = s.enableEscalation;
            var savedCooldown = s.escalationCooldownSeconds;
            var savedCap = s.escalationSessionCap;
            var savedTier = s.agentTierMode;
            var savedTurns = s.agentMaxTurns;
            SynapseAgentEscalation.ResetForTesting();
            try
            {
                return body(s);
            }
            finally
            {
                s.enableEscalation = savedEnable;
                s.escalationCooldownSeconds = savedCooldown;
                s.escalationSessionCap = savedCap;
                s.agentTierMode = savedTier;
                s.agentMaxTurns = savedTurns;
                SynapseAgentEscalation.ResetForTesting();
                SynapseTierController.ResetForTesting();
            }
        }

        private static IEnumerable<string> RecentLogLines()
        {
            var messages = Log.Messages;
            if (messages == null) return Enumerable.Empty<string>();
            return messages
                .Select(m => m.text ?? string.Empty)
                .Where(t => t.IndexOf(SynapseTestReporter.Tag, StringComparison.Ordinal) < 0)
                .ToList();
        }
    }
}
