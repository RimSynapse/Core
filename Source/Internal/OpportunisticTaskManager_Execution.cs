using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RimSynapse.Internal
{
    /// <summary>
    /// Task execution logic: idle-time firing, pause-time firing, throttle mode resolution.
    /// </summary>
    public static partial class OpportunisticTaskManager
    {
        /// <summary>
        /// Called by RequestQueue.WorkerLoop when the queue is completely empty.
        /// Selects and fires eligible tasks based on throttle mode, priority, and weight.
        /// </summary>
        internal static void TryRunOpportunisticTask()
        {
            var settings = RimSynapseMod.Instance?.Settings;
            var mode = ResolveThrottleMode(settings);

            TimeSpan idleDelay;
            int burstSize;
            float cooldownMultiplier;

            switch (mode)
            {
                case OpportunisticThrottleMode.Aggressive:
                    idleDelay = TimeSpan.FromMilliseconds(500);
                    burstSize = settings?.opportunisticBurstSize ?? 3;
                    cooldownMultiplier = 1.0f;
                    break;
                case OpportunisticThrottleMode.Balanced:
                    idleDelay = TimeSpan.FromSeconds(3);
                    burstSize = 1;
                    cooldownMultiplier = 1.0f;
                    break;
                case OpportunisticThrottleMode.Conservative:
                    idleDelay = TimeSpan.FromSeconds(15);
                    burstSize = 1;
                    cooldownMultiplier = 2.0f;
                    break;
                default:
                    idleDelay = TimeSpan.FromSeconds(5);
                    burstSize = 1;
                    cooldownMultiplier = 1.0f;
                    break;
            }

            // Foreground always wins: never dispatch on the heels of a live request,
            // whatever the throttle mode says.
            if (DateTime.UtcNow - RequestQueue.LastForegroundEnqueueUtc < TimeSpan.FromSeconds(3))
            {
                return;
            }

            // Forecast-aware dispatch: during in-game hours the demand history says are
            // quiet (colonists asleep, little social traffic), fill the window
            // aggressively regardless of the configured mode — this is when background
            // batches like nightly reviews are meant to run.
            try
            {
                if (Current.ProgramState == ProgramState.Playing && Find.TickManager != null)
                {
                    int abs = Find.TickManager.TicksAbs;
                    if (SynapseDemandForecast.IsLikelyQuiet(abs / 60000, abs / 2500 % 24))
                    {
                        idleDelay = TimeSpan.FromMilliseconds(500);
                        burstSize = Math.Max(burstSize, settings?.opportunisticBurstSize ?? 3);
                    }
                }
            }
            catch { }

            if (DateTime.UtcNow - _lastIdleCheck < idleDelay)
            {
                return;
            }
            _lastIdleCheck = DateTime.UtcNow;

            if (Current.ProgramState != ProgramState.Playing || Find.TickManager == null)
            {
                return;
            }

            int currentTick = Find.TickManager.TicksGame;
            int tasksFired = 0;

            lock (_lock)
            {
                UpdateEffectiveWeights(currentTick, cooldownMultiplier);

                var eligible = _tasks
                    .Where(t => t.Enabled && (t.Callback != null || t.CallbackBool != null) &&
                           currentTick - t.LastRunTick >= (int)(t.CooldownTicks * cooldownMultiplier))
                    .OrderByDescending(t => t.Priority)
                    .ToList();

                if (eligible.Count == 0) return;

                while (tasksFired < burstSize && eligible.Count > 0)
                {
                    var selected = SelectByWeightedRandom(eligible);
                    // Ambient marker so requests the task enqueues are classified as
                    // background and excluded from the demand forecast.
                    IsFiringTask = true;
                    try
                    {
                        FireTask(selected, currentTick, cooldownMultiplier, mode);
                    }
                    finally
                    {
                        IsFiringTask = false;
                    }
                    eligible.Remove(selected);
                    tasksFired++;
                }
            }
        }

        private static void UpdateEffectiveWeights(int currentTick, float cooldownMultiplier)
        {
            foreach (var task in _tasks)
            {
                if (!task.Enabled) continue;

                int ticksSinceRun = currentTick - task.LastRunTick;
                int cooldown = (int)(task.CooldownTicks * cooldownMultiplier);

                if (ticksSinceRun >= cooldown)
                {
                    task.EffectiveWeight = task.BaseWeight;
                }
                else
                {
                    float recovery = (float)ticksSinceRun / Math.Max(1, cooldown);
                    task.EffectiveWeight = task.BaseWeight * recovery;
                }

                float avgTokens = 0f;
                lock (RequestQueue.AvgTokensPerType)
                {
                    if (RequestQueue.AvgTokensPerType.TryGetValue(task.Label, out float tokens))
                    {
                        avgTokens = tokens;
                    }
                }
                float tokenPenalty = Math.Max(1f, avgTokens / 100f);
                task.EffectiveWeight /= tokenPenalty;
            }
        }

        private static TaskEntry SelectByWeightedRandom(List<TaskEntry> eligible)
        {
            int topPriority = eligible[0].Priority;
            var topGroup = eligible.Where(t => t.Priority == topPriority).ToList();

            float totalWeight = topGroup.Sum(t => Math.Max(0.01f, t.EffectiveWeight));
            float roll = Rand.Range(0f, totalWeight);
            float cumulative = 0f;
            TaskEntry selected = topGroup.Last();

            foreach (var task in topGroup)
            {
                cumulative += Math.Max(0.01f, task.EffectiveWeight);
                if (roll <= cumulative)
                {
                    selected = task;
                    break;
                }
            }

            return selected;
        }

        private static void FireTask(TaskEntry selected, int currentTick, float cooldownMultiplier, OpportunisticThrottleMode mode)
        {
            selected.LastRunTick = currentTick;
            selected.EffectiveWeight = 0f;
            selected.TimesRun++;

            SynapseLogger.Debug("queue", $"Opportunistic [{mode}]: Firing '{selected.Label}' " +
                $"(P{selected.Priority}, W{selected.BaseWeight:F1}, #{selected.TimesRun})");

            var callback = selected.Callback;
            var callbackBool = selected.CallbackBool;
            
            SynapseGameComponent.Enqueue(() =>
            {
                try
                {
                    bool didWork = false;
                    if (callbackBool != null)
                    {
                        didWork = callbackBool();
                    }
                    else if (callback != null)
                    {
                        callback();
                        didWork = true;
                    }
                    
                    if (didWork)
                    {
                        lock (_lock)
                        {
                            int cooldown = (int)(selected.CooldownTicks * cooldownMultiplier);
                            selected.LastRunTick = currentTick - cooldown + 250;
                        }
                    }
                }
                catch (Exception ex)
                {
                    SynapseLogger.Error($"Error executing opportunistic task '{selected.TaskId}': {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Resolves the effective throttle mode, handling Auto-detect.
        /// </summary>
        private static OpportunisticThrottleMode ResolveThrottleMode(RimSynapseSettings settings)
        {
            int modeInt = settings?.opportunisticThrottleMode ?? -1;

            if (modeInt >= 0 && modeInt <= 2)
            {
                return (OpportunisticThrottleMode)modeInt;
            }

            if (settings != null && settings.IsRemoteUrl)
            {
                return OpportunisticThrottleMode.Conservative;
            }

            return OpportunisticThrottleMode.Aggressive;
        }

        // ── Pause-time opportunistic task processing ──

        private static readonly Dictionary<string, DateTime> _realTimeCooldowns = new Dictionary<string, DateTime>();

        /// <summary>
        /// Called by SynapseGameComponent during extended pauses.
        /// Uses real-time cooldowns instead of game ticks so tasks can fire while the game is paused.
        /// </summary>
        internal static void TryRunPauseOpportunisticTask()
        {
            if (Current.ProgramState != ProgramState.Playing) return;
            if (RequestQueue.QueueDepth > 0) return;

            var settings = RimSynapseMod.Instance?.Settings;
            var mode = ResolveThrottleMode(settings);

            lock (_lock)
            {
                var now = DateTime.UtcNow;

                var eligible = _tasks
                    .Where(t => t.Enabled && (t.Callback != null || t.CallbackBool != null))
                    .Where(t =>
                    {
                        if (_realTimeCooldowns.TryGetValue(t.TaskId, out DateTime lastRun))
                        {
                            float cooldownSeconds = Math.Max(3f, t.CooldownTicks / 1000f);
                            return (now - lastRun).TotalSeconds >= cooldownSeconds;
                        }
                        return true;
                    })
                    .OrderByDescending(t => t.Priority)
                    .ToList();

                if (eligible.Count == 0) return;

                var selected = SelectPauseTask(eligible);

                _realTimeCooldowns[selected.TaskId] = now;
                selected.TimesRun++;

                SynapseLogger.Debug("queue", $"Pause-opportunistic [{mode}]: Firing '{selected.Label}' " +
                    $"(P{selected.Priority}, W{selected.BaseWeight:F1}, #{selected.TimesRun})");

                try
                {
                    bool didWork = false;
                    if (selected.CallbackBool != null)
                    {
                        didWork = selected.CallbackBool();
                    }
                    else if (selected.Callback != null)
                    {
                        selected.Callback();
                        didWork = true;
                    }

                    if (didWork)
                    {
                        _realTimeCooldowns[selected.TaskId] = now.AddSeconds(-Math.Max(3f, selected.CooldownTicks / 1000f) + 1.0);
                    }
                }
                catch (Exception ex)
                {
                    SynapseLogger.Error($"Error executing pause-opportunistic task '{selected.TaskId}': {ex.Message}");
                }
            }
        }

        private static TaskEntry SelectPauseTask(List<TaskEntry> eligible)
        {
            int topPriority = eligible[0].Priority;
            var topGroup = eligible.Where(t => t.Priority == topPriority).ToList();

            var pauseWeights = new Dictionary<TaskEntry, float>();
            foreach (var task in topGroup)
            {
                float avgTokens = 0f;
                lock (RequestQueue.AvgTokensPerType)
                {
                    if (RequestQueue.AvgTokensPerType.TryGetValue(task.Label, out float tokens))
                    {
                        avgTokens = tokens;
                    }
                }
                float tokenPenalty = Math.Max(1f, avgTokens / 100f);
                pauseWeights[task] = task.BaseWeight / tokenPenalty;
            }

            float totalWeight = topGroup.Sum(t => Math.Max(0.01f, pauseWeights[t]));
            float roll = Rand.Range(0f, totalWeight);
            float cumulative = 0f;
            TaskEntry selected = topGroup.Last();

            foreach (var task in topGroup)
            {
                cumulative += Math.Max(0.01f, pauseWeights[task]);
                if (roll <= cumulative)
                {
                    selected = task;
                    break;
                }
            }

            return selected;
        }

        /// <summary>
        /// Clears real-time cooldowns. Called when the game is unpaused to reset pause-session state.
        /// </summary>
        internal static void ResetPauseCooldowns()
        {
            lock (_lock)
            {
                _realTimeCooldowns.Clear();
            }
        }
    }
}
