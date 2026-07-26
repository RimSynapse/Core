using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;

namespace RimSynapse.Internal
{
    /// <summary>
    /// Dynamic request queue with per-mod budget enforcement, tick-aware throttling,
    /// dynamic priority scaling, and TTL dropping.
    /// Serializes LLM requests (one at a time) and scales back under load.
    /// </summary>
    public static partial class RequestQueue
    {
        /// <summary>A queued LLM request with mod attribution and scoring info.</summary>
        public class QueuedRequest
        {
            public SynapseModHandle Mod;
            
            /// <summary>LlmTextRequest, LlmImageRequest, etc.</summary>
            public object Payload;
            
            /// <summary>The type of request.</summary>
            public LlmCapabilities CapabilityType;
            
            /// <summary>Callback matching the payload type.</summary>
            public Delegate Callback;
            
            // Legacy for old mod compatibility
            public ChatOptions Options;
            public int Priority;
            public DateTime EnqueuedAt;
            public double CurrentScore;
            
            // History tracking
            public ChatResult Result;
            public DateTime? DispatchedAt;
            public DateTime? CompletedAt;
            public long? LlmLatencyMs;
        }

        private static readonly List<QueuedRequest> _queue = new List<QueuedRequest>();
        private static readonly List<QueuedRequest> _history = new List<QueuedRequest>();
        private static readonly object _queueLock = new object();
        
        public static float HistoryRetentionSeconds = 5f;

        private static readonly Thread _workerThread;
        private static readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private static volatile bool _shutdown;

        // Throttle state
        private static float _throttleLevel = 1.0f;
        private static readonly List<long> _recentDurations = new List<long>();
        private static DateTime _windowStart = DateTime.UtcNow;
        private static readonly TimeSpan WindowDuration = TimeSpan.FromSeconds(60);
        private static DateTime _lastModelWarning = DateTime.MinValue;

        // Token and TOPS tracking
        public static Dictionary<string, float> AvgTokensPerType = new Dictionary<string, float>();
        private static Queue<ChatResult> _recentResults = new Queue<ChatResult>();
        private static object _topsLock = new object();

        // Public stats
        public static int QueueDepth
        {
            get
            {
                lock (_queueLock)
                {
                    return _queue.Count;
                }
            }
        }
        
        public static float ThrottleLevel => _throttleLevel;
        public static bool IsProcessing { get; private set; }
        
        public static QueuedRequest ActiveRequest { get; private set; }
        public static System.Diagnostics.Stopwatch ActiveRequestStopwatch { get; private set; } = new System.Diagnostics.Stopwatch();

        /// <summary>
        /// Average response time in milliseconds across recent requests.
        /// </summary>
        public static float AverageResponseMs
        {
            get
            {
                lock (_recentDurations)
                {
                    if (_recentDurations.Count == 0) return 0f;
                    long sum = 0;
                    foreach (var d in _recentDurations) sum += d;
                    return sum / (float)_recentDurations.Count;
                }
            }
        }

        /// <summary>
        /// Global Token Operations Per Second (TOPS) over the last 60 seconds.
        /// </summary>
        public static float GlobalTops
        {
            get
            {
                lock (_topsLock)
                {
                    if (_recentResults.Count == 0) return 0f;
                    long totalTokens = 0;
                    long totalMs = 0;
                    foreach (var r in _recentResults)
                    {
                        totalTokens += r.completionTokens;
                        totalMs += r.durationMs;
                    }
                    if (totalMs == 0) return 0f;
                    return (totalTokens / (float)totalMs) * 1000f;
                }
            }
        }

        static RequestQueue()
        {
            _workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "RimSynapse-RequestQueue",
            };
            _workerThread.Start();
        }

        /// <summary>
        /// Expose a snapshot of the queue for the debug monitor.
        /// </summary>
        public static List<QueuedRequest> GetQueueSnapshot()
        {
            lock (_queueLock)
            {
                return _queue.ToList();
            }
        }

        public static List<QueuedRequest> GetHistorySnapshot()
        {
            lock (_queueLock)
            {
                return _history.ToList();
            }
        }

        public static int SessionId { get; private set; }

        /// <summary>
        /// Clears all pending requests from the queue.
        /// </summary>
        public static void Clear()
        {
            lock (_queueLock)
            {
                SessionId++;
                foreach (var req in _queue)
                {
                    if (req.Mod != null)
                    {
                        req.Mod.QueuedCount = Math.Max(0, req.Mod.QueuedCount - 1);
                    }
                }
                _queue.Clear();
            }
            SynapseLogger.Message("RequestQueue cleared.");
        }

        /// <summary>
        /// Enqueue a generic LLM request. Budget-checked and priority-ordered.
        /// </summary>
        internal static void Enqueue(SynapseModHandle mod, object payload, LlmCapabilities capability, 
            ChatOptions options, Delegate callback)
        {
            if (payload is LlmTextRequest textReq)
            {
                textReq.DisableThinking = options?.thinking.HasValue == true
                    ? !options.thinking.Value
                    : (RimSynapseMod.Instance?.Settings != null && RimSynapseMod.Instance.Settings.disableThinking);
            }

            var request = new QueuedRequest
            {
                Mod = mod,
                Payload = payload,
                CapabilityType = capability,
                Options = options,
                Callback = callback,
                Priority = options?.priority ?? 0,
                EnqueuedAt = DateTime.UtcNow,
                CurrentScore = 0
            };

            if (mod != null)
            {
                mod.QueuedCount++;
            }

            lock (_queueLock)
            {
                _queue.Add(request);
            }
            
            _signal.Set(); // Wake the worker

            SynapseLogger.Debug("queue",
                $"Request enqueued for {mod?.DisplayName ?? "unknown"}. Queue depth: {QueueDepth}.",
                mod?.ModId);
        }

        private static int _activeRequests = 0;

        /// <summary>
        /// Background worker loop. Processes requests in parallel based on dynamic scoring.
        /// </summary>
        private static void WorkerLoop()
        {
            while (!_shutdown)
            {
                var settings = RimSynapseMod.Instance?.Settings;
                int maxConcurrent = settings?.maxConcurrentRequests ?? 2;

                if (_activeRequests >= maxConcurrent)
                {
                    _signal.WaitOne(TimeSpan.FromMilliseconds(100));
                    continue;
                }

                // Wait for a request or periodic check if queue is empty
                bool hasItems = false;
                lock (_queueLock) { hasItems = _queue.Count > 0; }
                if (!hasItems)
                {
                    _signal.WaitOne(TimeSpan.FromSeconds(1));
                    if (_shutdown) break;
                }

                // Reset window counters if window expired
                if (DateTime.UtcNow - _windowStart > WindowDuration)
                {
                    _windowStart = DateTime.UtcNow;
                    ModRegistry.ResetWindowCounters();
                }

                QueuedRequest requestToProcess = null;
                var now = DateTime.UtcNow;

                lock (_queueLock)
                {
                    // Prune history
                    for (int i = _history.Count - 1; i >= 0; i--)
                    {
                        var req = _history[i];
                        if (req.CompletedAt.HasValue && (now - req.CompletedAt.Value).TotalSeconds > HistoryRetentionSeconds)
                        {
                            _history.RemoveAt(i);
                        }
                    }

                    if (_queue.Count == 0)
                    {
                        // Queue is empty, attempt opportunistic background work
                        if (_activeRequests == 0)
                        {
                            OpportunisticTaskManager.TryRunOpportunisticTask();
                        }
                        continue;
                    }

                    int maxPerWindow = settings?.maxRequestsPerMinute ?? 30;

                    // Evaluate queue: Drop stale requests and calculate dynamic scores
                    for (int i = _queue.Count - 1; i >= 0; i--)
                    {
                        var req = _queue[i];
                        double ageMs = (now - req.EnqueuedAt).TotalMilliseconds;

                        // TTL Check
                        if (req.Options?.maxWaitMs.HasValue == true && ageMs > req.Options.maxWaitMs.Value)
                        {
                            SynapseLogger.Warning($"Request for {req.Mod?.DisplayName} dropped (exceeded maxWaitMs of {req.Options.maxWaitMs.Value}).");
                            if (req.Mod != null) req.Mod.QueuedCount = Math.Max(0, req.Mod.QueuedCount - 1);
                            
                            var cb = req.Callback;
                            SynapseGameComponent.Enqueue(() => cb?.DynamicInvoke(ChatResult.Failure("Request timed out in queue.")));
                            
                            _queue.RemoveAt(i);
                            continue;
                        }

                        // Calculate Token Penalty
                        int totalChars = 0;
                        if (req.Payload is LlmTextRequest textReq && textReq.Messages != null)
                        {
                            foreach (var msg in textReq.Messages)
                            {
                                totalChars += msg.content?.Length ?? 0;
                            }
                        }
                        double estimatedTokens = totalChars / 4.0;
                        double tokenPenalty = estimatedTokens * 50.0;

                        // Calculate Dynamic Score: (Priority * 100,000) + Capped Age - Token Penalty
                        double cappedAge = Math.Min(ageMs, 100000.0);
                        req.CurrentScore = (req.Priority * 100000.0) + cappedAge - tokenPenalty;

                        // Budget Penalty: If over budget, apply massive penalty
                        if (req.Mod != null && !ModRegistry.IsWithinBudget(req.Mod, maxPerWindow))
                        {
                            req.CurrentScore -= 10000000.0;
                        }
                    }

                    if (_queue.Count == 0)
                        continue;

                    // Sort descending by score and pick the top
                    _queue.Sort((a, b) => b.CurrentScore.CompareTo(a.CurrentScore));
                    
                    requestToProcess = _queue[0];
                    _queue.RemoveAt(0);
                }

                if (requestToProcess == null) continue;

                int currentSession = SessionId;

                // ── Prevent firing if game is not active ──
                if (Verse.Current.ProgramState != Verse.ProgramState.Playing)
                {
                    SynapseLogger.Message($"Discarding request for {requestToProcess.Mod?.DisplayName} because the game is not playing.");
                    if (requestToProcess.Mod != null)
                    {
                        requestToProcess.Mod.QueuedCount = Math.Max(0, requestToProcess.Mod.QueuedCount - 1);
                    }
                    continue;
                }

                if (requestToProcess.Mod != null)
                {
                    requestToProcess.Mod.QueuedCount = Math.Max(0, requestToProcess.Mod.QueuedCount - 1);
                }

                // Execute in parallel
                IsProcessing = true;
                ActiveRequest = requestToProcess;
                requestToProcess.DispatchedAt = DateTime.UtcNow;
                ActiveRequestStopwatch.Restart();

                Interlocked.Increment(ref _activeRequests);
                var capturedReq = requestToProcess;
                var capturedSessionId = currentSession;

                System.Threading.Tasks.Task.Run(() => 
                {
                    try
                    {
                        ProcessRequest(capturedReq, capturedSessionId);
                    }
                    finally
                    {
                        ActiveRequest = null;
                        ActiveRequestStopwatch.Stop();
                        Interlocked.Decrement(ref _activeRequests);
                        _signal.Set();
                        
                        if (_activeRequests == 0)
                        {
                            IsProcessing = false;
                        }
                    }
                });
            }
        }

    }
}

