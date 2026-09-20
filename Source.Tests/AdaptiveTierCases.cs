using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Covers the capability tier controller: start-low promotion on evidence, immediate
    /// demotion on slow responses, manual override, model-swap reset, and the per-class
    /// objective switch (latency vs latency-floor vs cost).
    ///
    /// Latency samples are injected directly into SynapsePerformanceModel — no LLM calls.
    /// Settings are mutated inside a save/restore wrapper so cases leave no trace.
    /// </summary>
    [SynapseTestSet]
    public static class AdaptiveTierCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_TierStartsMinimal", () => WithSettings(s =>
            {
                s.agentTierMode = 0;
                s.apiProvider = ApiProvider.OpenAI; // metered: the latency dance applies to cloud only now (#139)
                SynapseTierController.ResetForTesting();
                SynapseTierController.Update(force: true);
                Assert.Equal(SynapseCapabilityTier.Minimal, SynapseTierController.Current,
                    "a metered backend with no samples must start Minimal regardless of window");
                return "metered starts Minimal with no evidence";
            }));

            // Local backends (Core #139): full quality within the window immediately — no sample/latency
            // gating and never demoted, because latency is irrelevant for rare async one-shots and
            // demoting frees no VRAM.
            yield return new SynapseTestCase("Core_TierLocalFullQuality", () => WithSettings(s =>
            {
                s.agentTierMode = 0;
                s.apiProvider = ApiProvider.Local_LMStudio;
                s.modelContextLimit = 20224;
                SynapseTierController.ResetForTesting();
                SynapseTierController.Update(force: true);
                int window = SynapseTierController.EffectiveWindow;
                var expected = window >= SynapseTierController.RichMinWindow
                    ? SynapseCapabilityTier.Rich : SynapseCapabilityTier.Standard;
                Assert.Equal(expected, SynapseTierController.Current,
                    "local with no samples must be full quality, never Minimal");

                // Slow responses must NOT demote a local backend.
                for (int i = 0; i < 10; i++) SynapsePerformanceModel.Record("dialogue", 15000, 500);
                SynapseTierController.Update();
                Assert.Equal(expected, SynapseTierController.Current,
                    "sustained slow latency must not demote a local backend");
                Assert.True(SynapseTierController.Current != SynapseCapabilityTier.Minimal,
                    "local must never be Minimal on Auto");
                return $"local full ({SynapseTierController.Current}) and immune to latency demotion";
            }));

            yield return new SynapseTestCase("Core_TierPromotesOnEvidence", () => WithSettings(s =>
            {
                s.agentTierMode = 0;
                s.apiProvider = ApiProvider.OpenAI; // step-by-step promotion is a metered-path behaviour now
                s.modelContextLimit = 32768;
                SynapseTierController.ResetForTesting();
                SynapseTierController.Update(force: true); // absorb window change from reset

                for (int i = 0; i < 10; i++)
                    SynapsePerformanceModel.Record("dialogue", 300, 500 + i * 10);

                SynapseTierController.Update(force: true); // one step
                SynapseTierController.Update(force: true); // second step

                int window = SynapseTierController.EffectiveWindow;
                var expected = window >= SynapseTierController.RichMinWindow ? SynapseCapabilityTier.Rich
                             : window >= SynapseTierController.StandardMinWindow ? SynapseCapabilityTier.Standard
                             : SynapseCapabilityTier.Minimal;

                Assert.Equal(expected, SynapseTierController.Current,
                    $"fast samples + window {window} should reach {expected}");
                return $"promoted to {SynapseTierController.Current} (window {window})";
            }));

            yield return new SynapseTestCase("Core_TierDemotesImmediately", () => WithSettings(s =>
            {
                s.agentTierMode = 0;
                s.apiProvider = ApiProvider.OpenAI; // latency demotion is a metered-path behaviour now (#139)
                s.modelContextLimit = 32768;
                SynapseTierController.ResetForTesting();
                SynapseTierController.Update(force: true);

                for (int i = 0; i < 10; i++)
                    SynapsePerformanceModel.Record("dialogue", 300, 500 + i * 10);
                SynapseTierController.Update(force: true);
                SynapseTierController.Update(force: true);
                var promoted = SynapseTierController.Current;

                SynapsePerformanceModel.Reset("test: switch to slow samples");
                for (int i = 0; i < 10; i++)
                    SynapsePerformanceModel.Record("dialogue", 12000, 500 + i * 10);
                SynapseTierController.Update(); // demotion must not need force

                Assert.Equal(SynapseCapabilityTier.Minimal, SynapseTierController.Current,
                    $"sustained 12s responses must demote from {promoted} to Minimal without a cooldown");
                Assert.True(RecentLogLines().Any(l => l.Contains("[Tier]") && l.Contains("->")),
                    "the tier change must be logged with a transition line");
                return $"demoted {promoted} -> Minimal immediately";
            }));

            yield return new SynapseTestCase("Core_TierScalarOrdering", () => WithSettings(s =>
            {
                s.agentTierMode = 1;
                float minimal = SynapseTierController.PerformanceScalar;
                s.agentTierMode = 2;
                float standard = SynapseTierController.PerformanceScalar;
                s.agentTierMode = 3;
                float rich = SynapseTierController.PerformanceScalar;

                Assert.True(minimal < standard && standard < rich,
                    $"scalar must rise with tier, got {minimal}/{standard}/{rich}");
                return $"scalars {minimal}/{standard}/{rich}";
            }));

            yield return new SynapseTestCase("Core_ManualOverrideWins", () => WithSettings(s =>
            {
                s.agentTierMode = 3; // Rich, forced
                SynapseTierController.ResetForTesting();
                for (int i = 0; i < 10; i++)
                    SynapsePerformanceModel.Record("dialogue", 15000, 500);
                SynapseTierController.Update(force: true);

                Assert.Equal(SynapseCapabilityTier.Rich, SynapseTierController.Current,
                    "manual Rich must win even with terrible measured latency");
                return "manual override beats measurement";
            }));

            yield return new SynapseTestCase("Core_ModelSwapResets", () => WithSettings(s =>
            {
                s.agentTierMode = 0;
                s.apiProvider = ApiProvider.OpenAI; // metered: a window change clears history -> insufficient samples -> Minimal

                // The window is only manipulable when no live provider discovery pinned it.
                s.modelContextLimit = 32768;
                int w1 = SynapseTierController.EffectiveWindow;
                s.modelContextLimit = 8192;
                int w2 = SynapseTierController.EffectiveWindow;
                if (w1 == w2)
                    throw new SynapseTestFailure("SKIPMARKER: window pinned by live provider discovery");

                s.modelContextLimit = 32768;
                SynapseTierController.ResetForTesting();
                SynapseTierController.Update(force: true);
                for (int i = 0; i < 10; i++)
                    SynapsePerformanceModel.Record("dialogue", 300, 500 + i * 10);
                SynapseTierController.Update(force: true);

                s.modelContextLimit = 4096; // simulate loading a smaller model
                SynapseTierController.Update(force: true);

                Assert.Equal(SynapseCapabilityTier.Minimal, SynapseTierController.Current,
                    "a metered window change clears history, so insufficient samples drop it to Minimal");
                Assert.Equal(0, SynapsePerformanceModel.TotalSampleCount(),
                    "a window change must clear the latency history");
                return "metered model swap re-tiers and clears history";
            }, skippable: true));

            // Local window change (Core #139): a bigger reloaded window must RE-BUDGET to full, not
            // stick at Minimal — the "Minimal forever" trap. And a window change never demotes a local
            // backend below full; it just clears the stale latency history.
            yield return new SynapseTestCase("Core_TierLocalWindowChangeRebudgets", () => WithSettings(s =>
            {
                s.agentTierMode = 0;
                s.apiProvider = ApiProvider.Local_LMStudio;

                s.modelContextLimit = 20224;
                int wBig = SynapseTierController.EffectiveWindow;
                s.modelContextLimit = 8192;
                if (wBig == SynapseTierController.EffectiveWindow)
                    throw new SynapseTestFailure("SKIPMARKER: window pinned by live provider discovery");

                // Start small, then "reload" a bigger window — must promote, not stay stuck.
                s.modelContextLimit = 8192;
                SynapseTierController.ResetForTesting();
                SynapseTierController.Update(force: true);
                Assert.True(SynapseTierController.Current != SynapseCapabilityTier.Minimal,
                    "local at 8192 must be full, not Minimal");

                s.modelContextLimit = 20224; // user reloads a larger window mid-session
                SynapseTierController.Update(force: true);
                Assert.Equal(20224, SynapseTierController.EffectiveWindow, "the new window is picked up");
                Assert.Equal(SynapseCapabilityTier.Rich, SynapseTierController.Current,
                    "a bigger reloaded window must re-budget to full (Rich), not stay Minimal");
                Assert.Equal(0, SynapsePerformanceModel.TotalSampleCount(),
                    "a window change clears stale latency history");
                return "local re-budgets to Rich on a window bump (no Minimal-forever)";
            }, skippable: true));

            yield return new SynapseTestCase("Core_FloorObjectiveUnmetered", () => WithSettings(s =>
            {
                s.agentTierMode = 0;
                s.apiProvider = ApiProvider.Local_LMStudio;
                s.ignoreTokenCosts = false;
                SynapseTierController.ResetForTesting();

                // Flat curve above the 500 ms dialogue SLO: floor ~900 ms, slope ~0.004 ms/token.
                int[] tokens = { 200, 1200, 2400, 3600, 4800, 6000 };
                for (int i = 0; i < tokens.Length; i++)
                    SynapsePerformanceModel.Record("dialogue", 900 + i * 5, tokens[i]);

                SynapseTierController.Update(force: true); // also emits the operating-point log
                var p = SynapseTierController.GetOperatingPoint("dialogue");

                Assert.Equal(SynapseGoverningConstraint.LatencyFloor, p.GovernedBy,
                    $"floor {p.FloorMs:F0}ms above the 500ms SLO must switch to floor governance");
                Assert.True(p.LatencyTargetMs > 950 && p.LatencyTargetMs < 1150,
                    $"target should be ~floor×1.15, got {p.LatencyTargetMs}ms (floor {p.FloorMs:F0}ms)");
                Assert.True(p.MaxPromptTokens > 6000 || p.MaxPromptTokens >= WindowCap() - 1,
                    $"a near-flat slope should allow large prompts, got {p.MaxPromptTokens}");
                Assert.True(RecentLogLines().Any(l => l.Contains("governed=") && l.Contains("floor=")),
                    "floor and slope must be logged with the operating point");
                return $"floor {p.FloorMs:F0}ms -> target {p.LatencyTargetMs}ms, maxPrompt {p.MaxPromptTokens}";
            }));

            yield return new SynapseTestCase("Core_MeteredCapGoverns", () => WithSettings(s =>
            {
                s.apiProvider = ApiProvider.OpenAI;
                s.ignoreTokenCosts = false;
                s.tokenCapPerRequest = 4000;
                SynapseTierController.ResetForTesting();

                var p = SynapseTierController.GetOperatingPoint("dialogue");
                Assert.Equal(SynapseGoverningConstraint.Cost, p.GovernedBy,
                    "a cloud provider must be cost-governed by default");
                Assert.True(p.MaxPromptTokens <= 4000,
                    $"prompt cap must respect tokenCapPerRequest, got {p.MaxPromptTokens}");
                return "metered backend governed by token cap";
            }));

            yield return new SynapseTestCase("Core_IgnoreCostsToggle", () => WithSettings(s =>
            {
                s.apiProvider = ApiProvider.OpenAI;
                s.ignoreTokenCosts = true;
                SynapseTierController.ResetForTesting();

                var p = SynapseTierController.GetOperatingPoint("dialogue");
                Assert.False(p.GovernedBy == SynapseGoverningConstraint.Cost,
                    "with the experimental toggle on, cost must no longer govern");
                Assert.True(RecentLogLines().Any(l => l.Contains("ignore token costs")),
                    "the cost override must be logged");
                return "toggle flips metered backend to latency governance, logged";
            }));
        }

        private static int WindowCap()
        {
            int w = SynapseTierController.EffectiveWindow;
            return Math.Max(256, w - Math.Max(512, (int)(w * 0.25f)));
        }

        /// <summary>Runs a case body with settings snapshotted and restored, controller reset after.</summary>
        private static string WithSettings(Func<RimSynapseSettings, string> body, bool skippable = false)
        {
            var s = RimSynapseMod.Instance?.Settings;
            Assert.NotNull(s, "settings unavailable");

            var savedTier = s.agentTierMode;
            var savedIgnore = s.ignoreTokenCosts;
            var savedCapReq = s.tokenCapPerRequest;
            var savedCapDay = s.tokenCapPerDay;
            var savedProvider = s.apiProvider;
            var savedCtx = s.modelContextLimit;

            try
            {
                return body(s);
            }
            catch (SynapseTestFailure f) when (skippable && f.Message.StartsWith("SKIPMARKER:"))
            {
                return "skipped: " + f.Message.Substring("SKIPMARKER:".Length).Trim();
            }
            finally
            {
                s.agentTierMode = savedTier;
                s.ignoreTokenCosts = savedIgnore;
                s.tokenCapPerRequest = savedCapReq;
                s.tokenCapPerDay = savedCapDay;
                s.apiProvider = savedProvider;
                s.modelContextLimit = savedCtx;
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
