using System;
using System.Collections.Generic;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Covers the demand forecast (learning which in-game hours are quiet) and the
    /// deadline batch planner (shrink context, then cut items, never overrun quietly).
    /// Both are Verse-free by design, so cases drive them with synthetic days and ticks.
    /// </summary>
    [SynapseTestSet]
    public static class DeadlineForecastCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_ForecastLearnsQuietHours", () =>
            {
                try
                {
                    SynapseDemandForecast.Reset();

                    // Two synthetic days of colony rhythm: busy 06:00-20:00, silent night.
                    for (int day = 1; day <= 2; day++)
                        for (int hour = 6; hour <= 20; hour++)
                            for (int i = 0; i < 5; i++)
                                SynapseDemandForecast.RecordForeground(day, hour);

                    Assert.True(SynapseDemandForecast.IsLikelyQuiet(3, 2),
                        "02:00 must be learned as quiet after two busy-day folds");
                    Assert.False(SynapseDemandForecast.IsLikelyQuiet(3, 12),
                        "12:00 must be learned as busy");
                    Assert.True(SynapseDemandForecast.Rate(12) > SynapseDemandForecast.Rate(2),
                        "learned rates must reflect the recorded rhythm");
                    return $"night quiet, midday busy (rates {SynapseDemandForecast.Rate(2):F1} vs {SynapseDemandForecast.Rate(12):F1})";
                }
                finally { SynapseDemandForecast.Reset(); }
            });

            yield return new SynapseTestCase("Core_ForecastUnknownIsNotQuiet", () =>
            {
                try
                {
                    SynapseDemandForecast.Reset();
                    Assert.False(SynapseDemandForecast.IsLikelyQuiet(1, 3),
                        "with no folded history, no hour may claim to be quiet");

                    // A dead-flat profile has no quiet hours either.
                    for (int hour = 0; hour < 24; hour++)
                        SynapseDemandForecast.RecordForeground(1, hour);
                    Assert.False(SynapseDemandForecast.IsLikelyQuiet(2, 3),
                        "a flat profile must not mark any hour quiet");
                    return "unknown and flat profiles never claim quiet";
                }
                finally { SynapseDemandForecast.Reset(); }
            });

            yield return new SynapseTestCase("Core_BatchPlanOnTrack", () =>
            {
                // 10 items, 1800 ticks at 1x (30s real) = 3000 ms/item; 1000 ms items fit.
                var plan = SynapseBatchPlanner.Plan(10, 1800, 60f, 1000, 500);
                Assert.True(plan.OnTrack, "a fitting batch must be on track");
                Assert.Equal(1f, plan.ContextScale, "no shrink when on track");
                Assert.Equal(0, plan.ItemsToCut, "no cuts when on track");
                return $"on track at {plan.PerItemBudgetMs:F0} ms/item";
            });

            yield return new SynapseTestCase("Core_BatchPlanShrinksBeforeCutting", () =>
            {
                // Moderate deficit: 4000 ms items into a 3000 ms budget -> shrink only.
                var shrink = SynapseBatchPlanner.Plan(10, 1800, 60f, 4000, 500);
                Assert.False(shrink.OnTrack, "a deficit must not be on track");
                Assert.True(shrink.ContextScale < 1f && shrink.ItemsToCut == 0,
                    $"moderate deficit shrinks without cutting, got scale {shrink.ContextScale:F2}, cut {shrink.ItemsToCut}");

                // Severe deficit: 500 ms/item budget -> floor the context, then cut.
                var cut = SynapseBatchPlanner.Plan(10, 300, 60f, 4000, 500);
                Assert.Equal(SynapseBatchPlanner.MinContextScale, cut.ContextScale,
                    "cutting only happens with context already at the floor");
                Assert.True(cut.ItemsToCut > 0,
                    $"a severe deficit must cut items, got {cut.ItemsToCut}");
                return $"shrink to {shrink.ContextScale:P0} first; floor + cut {cut.ItemsToCut} when severe";
            });

            yield return new SynapseTestCase("Core_BatchPlanRecomputesOnSpeedChange", () =>
            {
                // Same window, 1x vs 3x: tripled speed slashes real time, forcing harder degradation.
                var normal = SynapseBatchPlanner.Plan(10, 1800, 60f, 4000, 500);
                var fast = SynapseBatchPlanner.Plan(10, 1800, 180f, 4000, 500);
                Assert.True(fast.PerItemBudgetMs < normal.PerItemBudgetMs,
                    "higher game speed must mean less real time per item");
                Assert.True(fast.ContextScale <= normal.ContextScale,
                    $"faster game must degrade at least as hard ({fast.ContextScale:F2} vs {normal.ContextScale:F2})");
                return $"1x budget {normal.PerItemBudgetMs:F0}ms -> 3x budget {fast.PerItemBudgetMs:F0}ms";
            });

            yield return new SynapseTestCase("Core_DeadlineBatchExpiryPolicies", () =>
            {
                var drop = new SynapseDeadlineBatch("zz-nightly", windowStartTick: 0, windowLengthTicks: 100, itemsTotal: 5, dropOnExpiry: true);
                drop.MarkItemDone();
                Assert.True(drop.Expired(150), "past the window end the batch is expired");
                Assert.Equal(0, drop.ExpireNow(), "drop policy carries nothing");
                Assert.Equal(0, drop.ItemsRemaining, "dropped items are closed out");

                var carry = new SynapseDeadlineBatch("zz-carry", 0, 100, 5, dropOnExpiry: false);
                carry.MarkItemDone();
                carry.MarkItemDone();
                Assert.Equal(3, carry.ExpireNow(), "carry policy returns the unfinished count");
                return "drop closes out, carry hands back 3";
            });
        }
    }
}
