using System;

namespace RimSynapse
{
    public struct SynapseBatchPlan
    {
        /// <summary>The batch fits at full context without cuts.</summary>
        public bool OnTrack;
        /// <summary>Per-item context scale, 1.0 down to the floor. Shrink is the first lever.</summary>
        public float ContextScale;
        /// <summary>Items to drop after shrinking to the floor still does not fit. The last lever.</summary>
        public int ItemsToCut;
        /// <summary>Real milliseconds available per remaining item.</summary>
        public double PerItemBudgetMs;
    }

    /// <summary>
    /// Sizes a background batch to its remaining game-time window.
    ///
    /// A batch like the nightly psychological reviews declares a window in game ticks;
    /// real time available depends on game speed, and per-item cost comes from the
    /// measured latency history. When the batch will not fit, degradation is ordered:
    /// shrink per-item context first (smaller prompts are faster), cut items only when
    /// shrinking to the floor still is not enough. Coalescing multiple subjects into one
    /// request sits between the two levers but requires the task's cooperation, so it
    /// belongs to the adopting mod.
    ///
    /// Pure math, no Verse types: callers supply ticks, speed and estimates.
    /// </summary>
    public static class SynapseBatchPlanner
    {
        /// <summary>Context is never scaled below this: a review with no context is noise.</summary>
        public const float MinContextScale = 0.25f;

        /// <param name="itemsRemaining">Items still to process.</param>
        /// <param name="ticksRemaining">Game ticks left in the window.</param>
        /// <param name="ticksPerSecond">Current effective game speed (60 at 1x, 180 at 3x).</param>
        /// <param name="estPerItemMs">Measured per-item latency at full context.</param>
        /// <param name="minPerItemMs">Latency floor — what an item costs even at minimum context.</param>
        public static SynapseBatchPlan Plan(
            int itemsRemaining, int ticksRemaining, float ticksPerSecond,
            double estPerItemMs, double minPerItemMs)
        {
            var plan = new SynapseBatchPlan { ContextScale = 1f, ItemsToCut = 0 };

            if (itemsRemaining <= 0)
            {
                plan.OnTrack = true;
                return plan;
            }

            double realMsRemaining = ticksPerSecond > 0
                ? ticksRemaining / (double)ticksPerSecond * 1000.0
                : 0.0;
            plan.PerItemBudgetMs = realMsRemaining / itemsRemaining;

            if (estPerItemMs <= plan.PerItemBudgetMs)
            {
                plan.OnTrack = true;
                return plan;
            }

            // Lever 1 — shrink context. Latency on a prefill-bound local model scales
            // roughly with prompt size, floored at the unavoidable minimum.
            double floor = Math.Max(1.0, minPerItemMs);
            float scale = (float)(plan.PerItemBudgetMs / Math.Max(1.0, estPerItemMs));
            if (scale < MinContextScale) scale = MinContextScale;

            double scaledCost = Math.Max(floor, estPerItemMs * scale);
            if (scaledCost <= plan.PerItemBudgetMs)
            {
                plan.OnTrack = false;
                plan.ContextScale = scale;
                return plan;
            }

            // Lever 2 — cut items. At the floor cost, how many actually fit?
            plan.OnTrack = false;
            plan.ContextScale = MinContextScale;
            double floorCost = Math.Max(floor, estPerItemMs * MinContextScale);
            int affordable = floorCost > 0 ? (int)(realMsRemaining / floorCost) : 0;
            plan.ItemsToCut = Math.Max(0, itemsRemaining - Math.Max(0, affordable));
            return plan;
        }
    }

    /// <summary>
    /// A declared background batch: name, game-tick window, item count and expiry policy.
    /// Owning tasks call PlanNow each dispatch opportunity and honour its levers; the
    /// scheduler-side decisions (shrink, cut, expiry) are logged here so the degradation
    /// order is always visible.
    /// </summary>
    public class SynapseDeadlineBatch
    {
        public string Name { get; }
        public int WindowStartTick { get; }
        public int WindowLengthTicks { get; }
        public int ItemsTotal { get; }
        public int ItemsDone { get; private set; }

        /// <summary>True: unfinished items are dropped at expiry (a stale nightly review is
        /// worthless by noon). False: they carry to the next window.</summary>
        public bool DropOnExpiry { get; }

        private float _lastLoggedScale = 1f;
        private int _lastLoggedCut;

        public SynapseDeadlineBatch(string name, int windowStartTick, int windowLengthTicks, int itemsTotal, bool dropOnExpiry)
        {
            Name = name;
            WindowStartTick = windowStartTick;
            WindowLengthTicks = Math.Max(1, windowLengthTicks);
            ItemsTotal = Math.Max(0, itemsTotal);
            DropOnExpiry = dropOnExpiry;
        }

        public int ItemsRemaining => Math.Max(0, ItemsTotal - ItemsDone);

        public bool Expired(int currentTick) => currentTick >= WindowStartTick + WindowLengthTicks;

        public void MarkItemDone()
        {
            ItemsDone++;
        }

        public SynapseBatchPlan PlanNow(int currentTick, float ticksPerSecond, double estPerItemMs, double minPerItemMs)
        {
            int ticksRemaining = Math.Max(0, WindowStartTick + WindowLengthTicks - currentTick);
            var plan = SynapseBatchPlanner.Plan(ItemsRemaining, ticksRemaining, ticksPerSecond, estPerItemMs, minPerItemMs);

            // Log lever movements, not every call — the degradation order should be
            // visible without being noisy.
            if (Math.Abs(plan.ContextScale - _lastLoggedScale) > 0.05f || plan.ItemsToCut != _lastLoggedCut)
            {
                _lastLoggedScale = plan.ContextScale;
                _lastLoggedCut = plan.ItemsToCut;
                SynapseLogger.Message(
                    $"[Batch] {Name}: {ItemsRemaining} item(s), {ticksRemaining} ticks left -> " +
                    (plan.OnTrack
                        ? "on track at full context"
                        : plan.ItemsToCut > 0
                            ? $"context floored at {plan.ContextScale:P0}, cutting {plan.ItemsToCut} item(s)"
                            : $"shrinking context to {plan.ContextScale:P0}"),
                    "performance");
            }
            return plan;
        }

        /// <summary>Apply the expiry policy; returns items carried to the next window.</summary>
        public int ExpireNow()
        {
            int unfinished = ItemsRemaining;
            if (unfinished == 0) return 0;

            if (DropOnExpiry)
            {
                SynapseLogger.Message($"[Batch] {Name}: window expired, dropping {unfinished} unfinished item(s) (stale by policy).", "performance");
                ItemsDone = ItemsTotal;
                return 0;
            }

            SynapseLogger.Message($"[Batch] {Name}: window expired, carrying {unfinished} item(s) to the next window.", "performance");
            return unfinished;
        }
    }
}
