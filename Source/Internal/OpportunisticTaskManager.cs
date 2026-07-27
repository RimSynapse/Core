using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RimSynapse.Internal
{
    /// <summary>
    /// Throttle mode controls how aggressively the system fills idle GPU time.
    /// </summary>
    public enum OpportunisticThrottleMode
    {
        /// <summary>Auto-detect: Aggressive for localhost, Conservative for remote.</summary>
        Auto = -1,
        /// <summary>Local LLM: 0.5s idle delay, burst up to N tasks per check.</summary>
        Aggressive = 0,
        /// <summary>Middle ground: 3s idle delay, 1 task per check.</summary>
        Balanced = 1,
        /// <summary>Paid API: 15s idle delay, 1 task per check, doubled cooldowns.</summary>
        Conservative = 2
    }

    /// <summary>
    /// Registration metadata for an opportunistic task.
    /// Companion mods pass this when registering to provide Core with all scheduling info.
    /// </summary>
    public class OpportunisticTaskConfig
    {
        /// <summary>Human-readable label shown in the Queue Monitor and Settings.</summary>
        public string Label = "Unknown Task";

        /// <summary>Tooltip description shown in Settings.</summary>
        public string Description = "";

        /// <summary>Higher priority tasks are checked first. Scale: 0 (disabled) to 10 (critical). Default: 5.</summary>
        public int Priority = 5;

        /// <summary>Base weight for weighted random selection among tasks at the same priority. Default: 1.0.</summary>
        public float Weight = 1.0f;

        /// <summary>Minimum in-game ticks between invocations. 60000 ≈ 1 day. Default: 15000 ≈ 6 hours.</summary>
        public int CooldownTicks = 15000;

        /// <summary>Whether this task starts enabled. Can be toggled in-game.</summary>
        public bool Enabled = true;
    }

    /// <summary>
    /// Centralized scheduler for low-priority background AI tasks.
    /// Companion mods register callbacks with metadata via C# — no Def system required.
    /// Core handles weighted selection, cooldown tracking, priority ordering,
    /// and throttle-aware scheduling.
    ///
    /// Third-party mods: just call SynapseClient.RegisterOpportunisticTask() with your
    /// callback and an OpportunisticTaskConfig. No XML or type dependencies needed.
    /// </summary>
    public static partial class OpportunisticTaskManager
    {
        /// <summary>
        /// True while a task's callback is being fired. Requests enqueued during a fire
        /// are classified as background and excluded from the demand forecast — they only
        /// run when the queue is already idle, so counting them would make quiet hours
        /// look busy.
        /// </summary>
        internal static bool IsFiringTask;

        /// <summary>
        /// Runtime state for a registered opportunistic task.
        /// Exposed publicly so the Queue Monitor can display it.
        /// </summary>
        public class TaskEntry
        {
            public SynapseModHandle Mod;
            public string TaskId;
            public Action Callback;
            public Func<bool> CallbackBool;
            public OpportunisticTaskConfig Config;
            public int LastRunTick = -999999;
            public float EffectiveWeight;
            public int TimesRun;

            // Accessors through config
            public int Priority => Config?.Priority ?? 5;
            public float BaseWeight => Config?.Weight ?? 1.0f;
            public int CooldownTicks => Config?.CooldownTicks ?? 15000;
            public bool Enabled
            {
                get => Config?.Enabled ?? true;
                set { if (Config != null) Config.Enabled = value; }
            }
            public string Label => Config?.Label ?? TaskId;
            public string Description => Config?.Description ?? "";
        }

        private static readonly List<TaskEntry> _tasks = new List<TaskEntry>();
        private static readonly object _lock = new object();
        private static DateTime _lastIdleCheck = DateTime.MinValue;

        /// <summary>
        /// Read-only snapshot of all registered tasks for the Queue Monitor and Settings UI.
        /// </summary>
        public static List<TaskEntry> GetTaskSnapshot()
        {
            lock (_lock)
            {
                return _tasks.ToList();
            }
        }

        /// <summary>
        /// Registers an opportunistic task with full metadata that can return whether it performed work.
        /// If it returns true, it triggers a short burst cooldown to rapidly process remaining targets.
        /// If it returns false, it falls back to the full polling cooldown.
        /// </summary>
        public static void RegisterTask(SynapseModHandle mod, string taskId, Func<bool> callback, OpportunisticTaskConfig config = null)
        {
            lock (_lock)
            {
                var existing = _tasks.Find(t => t.TaskId == taskId);
                if (existing != null)
                {
                    existing.CallbackBool = callback;
                    existing.Callback = null;
                    existing.Mod = mod;
                    if (config != null) existing.Config = config;
                    return;
                }

                var entry = new TaskEntry
                {
                    Mod = mod,
                    TaskId = taskId,
                    CallbackBool = callback,
                    Config = config ?? new OpportunisticTaskConfig { Label = taskId },
                    EffectiveWeight = config?.Weight ?? 1.0f
                };

                _tasks.Add(entry);
                SynapseLogger.Info("core", $"Registered Opportunistic Task '{entry.Label}' for {mod.DisplayName} " +
                    $"(P{entry.Priority}, W{entry.BaseWeight:F1}, CD{entry.CooldownTicks}t, Dynamic)");
            }
        }

        /// <summary>
        /// Registers an opportunistic task with full metadata.
        /// This is the primary API — any mod can call this without needing XML Defs or type references.
        /// </summary>
        /// <param name="mod">Your mod handle from SynapseCore.Register().</param>
        /// <param name="taskId">Unique string ID for this task (e.g., "MyMod_BackstoryGen").</param>
        /// <param name="callback">The function to call when the queue is idle.</param>
        /// <param name="config">Scheduling metadata (priority, weight, cooldown, label). If null, sensible defaults are used.</param>
        public static void RegisterTask(SynapseModHandle mod, string taskId, Action callback, OpportunisticTaskConfig config = null)
        {
            lock (_lock)
            {
                var existing = _tasks.Find(t => t.TaskId == taskId);
                if (existing != null)
                {
                    existing.Callback = callback;
                    existing.CallbackBool = null;
                    existing.Mod = mod;
                    if (config != null) existing.Config = config;
                    return;
                }

                var entry = new TaskEntry
                {
                    Mod = mod,
                    TaskId = taskId,
                    Callback = callback,
                    Config = config ?? new OpportunisticTaskConfig { Label = taskId },
                    EffectiveWeight = config?.Weight ?? 1.0f
                };

                _tasks.Add(entry);
                SynapseLogger.Info("core", $"Registered Opportunistic Task '{entry.Label}' for {mod.DisplayName} " +
                    $"(P{entry.Priority}, W{entry.BaseWeight:F1}, CD{entry.CooldownTicks}t)");
            }
        }

        /// <summary>
        /// Legacy overload for backward compatibility.
        /// </summary>
        [Obsolete("Use RegisterTask(mod, taskId, callback, config) with an OpportunisticTaskConfig.")]
        public static void RegisterTask(SynapseModHandle mod, string taskId, Action callback, int cooldownTicks)
        {
            RegisterTask(mod, taskId, callback, new OpportunisticTaskConfig
            {
                Label = taskId,
                CooldownTicks = cooldownTicks
            });
        }

    }
}

