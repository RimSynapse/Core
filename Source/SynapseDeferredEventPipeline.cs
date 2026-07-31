using System;
using System.Collections.Generic;

namespace RimSynapse
{
    /// <summary>
    /// The game-free hold state machine behind Core's deferred-event pipeline (Core#59).
    ///
    /// <para><b>The third extension kind.</b> Core already brokers <i>providers</i> (one authority,
    /// pulled, returns a value) and <i>broadcast hooks</i> (many subscribers, pushed, return
    /// nothing). This is neither: many subscribers, <b>ordered</b>, each able to <b>defer</b> the
    /// release of an event until it has done asynchronous work. A newspaper release and its spoken
    /// audio must arrive together; the letter rewriter changes the text and TTS then speaks the
    /// final wording, so the two are ordered rather than parallel.</para>
    ///
    /// <para><b>Pure on purpose.</b> No RimWorld, Harmony or Unity types appear here, so the whole
    /// state machine — ordering, timeout, invalidation, cap, once-only release — is unit-testable in
    /// <c>Tests/</c> without a game. The Harmony prefixes, the real <c>Letter</c>/<c>Message</c>
    /// payloads, the real-seconds clock and the main-thread re-issue live in
    /// <c>SynapseDeferredEvents</c>. This class is <b>not thread-safe</b>; the integration marshals
    /// every call onto the main thread (the same queue <c>SynapseGameComponent</c> already drains).</para>
    ///
    /// <para><b>Every hold terminates.</b> A hold is released exactly once — by its last stage
    /// completing, or by its deadline, whichever comes first. A stage that throws, or never calls
    /// back, cannot wedge it: the deadline still fires and the event is still delivered with whatever
    /// stages finished. Silent-but-delivered beats never-arrives.</para>
    /// </summary>
    public sealed class SynapseDeferredEventPipeline
    {
        private sealed class Stage
        {
            public int Order;
            public Action<object, Action> Run;
        }

        private sealed class Hold
        {
            public long Id;
            public object Payload;
            public Action Release;      // integration re-issues the event on the main thread
            public Func<bool> IsValid;  // re-checked immediately before release
            public List<Stage> Stages;
            public int StageIndex;
            public double DeadlineSeconds;
            public bool Released;        // once-only guard
        }

        private readonly Func<double> _now;
        private readonly double _timeoutSeconds;
        private readonly int _maxConcurrentHolds;
        private readonly Action<string> _log;

        // eventClass -> holdable? Absence means MustNotDelay: the fail-safe direction, because a
        // raid warning held until some slow stage finishes arrives after the raiders.
        private readonly Dictionary<string, bool> _holdable = new Dictionary<string, bool>();
        private readonly Dictionary<string, List<Stage>> _stages = new Dictionary<string, List<Stage>>();
        private readonly Dictionary<long, Hold> _active = new Dictionary<long, Hold>();
        private long _nextId = 1;

        public SynapseDeferredEventPipeline(Func<double> nowSeconds, double timeoutSeconds, int maxConcurrentHolds, Action<string> log = null)
        {
            if (nowSeconds == null) throw new ArgumentNullException(nameof(nowSeconds));
            _now = nowSeconds;
            _timeoutSeconds = timeoutSeconds;
            _maxConcurrentHolds = maxConcurrentHolds < 1 ? 1 : maxConcurrentHolds;
            _log = log ?? (_ => { });
        }

        public int ActiveHoldCount { get { return _active.Count; } }

        /// <summary>Classify an event class as holdable or not. Unclassified classes are never held.</summary>
        public void RegisterClassification(string eventClass, bool holdable)
        {
            if (string.IsNullOrEmpty(eventClass)) return;
            _holdable[eventClass] = holdable;
        }

        /// <summary>
        /// Register an ordered stage for an event class. Lower <paramref name="order"/> runs first,
        /// and each stage must finish before the next begins. The stage signature carries no Core
        /// type, so a consumer registers it by reflection and builds with Core absent.
        /// </summary>
        public void RegisterStage(string eventClass, int order, Action<object, Action> stage)
        {
            if (string.IsNullOrEmpty(eventClass) || stage == null) return;
            List<Stage> list;
            if (!_stages.TryGetValue(eventClass, out list)) { list = new List<Stage>(); _stages[eventClass] = list; }
            list.Add(new Stage { Order = order, Run = stage });
            list.Sort((a, b) => a.Order.CompareTo(b.Order));
        }

        /// <summary>
        /// Attempt to hold an event. Returns true when the event is now held and the caller must
        /// suppress the vanilla event; false when the caller should let it fire immediately —
        /// because the class is not holdable, has no registered stages, or the concurrent-hold cap
        /// is reached. <paramref name="release"/> re-issues the event (the integration wraps it so
        /// it runs on the main thread); <paramref name="isValid"/> is re-checked at release time.
        /// </summary>
        public bool TryHold(string eventClass, object payload, Action release, Func<bool> isValid)
        {
            if (release == null) return false;

            bool holdable;
            if (!_holdable.TryGetValue(eventClass, out holdable) || !holdable) return false;

            List<Stage> stages;
            if (!_stages.TryGetValue(eventClass, out stages) || stages.Count == 0) return false;

            if (_active.Count >= _maxConcurrentHolds)
            {
                _log(string.Format("[RimSynapse] Deferred event '{0}' fired unheld: {1} hold(s) already at cap {2}.",
                    eventClass, _active.Count, _maxConcurrentHolds));
                return false;
            }

            var hold = new Hold
            {
                Id = _nextId++,
                Payload = payload,
                Release = release,
                IsValid = isValid ?? (() => true),
                Stages = new List<Stage>(stages),   // snapshot: later registrations do not alter this hold
                StageIndex = 0,
                DeadlineSeconds = _now() + _timeoutSeconds,
                Released = false,
            };
            _active[hold.Id] = hold;
            StartStage(hold);
            return true;
        }

        private void StartStage(Hold hold)
        {
            if (hold.Released) return;
            if (hold.StageIndex >= hold.Stages.Count) { Release(hold); return; }

            Stage stage = hold.Stages[hold.StageIndex];
            bool advanced = false;
            Action done = () =>
            {
                // A stage that signals completion more than once, or after the deadline already
                // released the hold, is ignored — the once-only guarantee holds at the stage seam too.
                if (advanced || hold.Released) return;
                advanced = true;
                hold.StageIndex++;
                StartStage(hold);
            };

            try
            {
                stage.Run(hold.Payload, done);
            }
            catch (Exception ex)
            {
                _log(string.Format("[RimSynapse] Deferred stage {0} threw ([{1}] {2}); advancing past it.",
                    hold.StageIndex, ex.GetType().Name, ex.Message));
                if (!advanced && !hold.Released) { advanced = true; hold.StageIndex++; StartStage(hold); }
            }
        }

        /// <summary>
        /// Drive the deadlines. Called every frame — including while the game is paused, which is
        /// why the deadline is measured in real seconds rather than game ticks. Any hold past its
        /// deadline is released with whatever stages completed.
        /// </summary>
        public void Tick()
        {
            if (_active.Count == 0) return;
            double now = _now();

            List<Hold> expired = null;
            foreach (var kv in _active)
            {
                Hold h = kv.Value;
                if (!h.Released && now >= h.DeadlineSeconds)
                {
                    if (expired == null) expired = new List<Hold>();
                    expired.Add(h);
                }
            }
            if (expired == null) return;

            foreach (Hold hold in expired)
            {
                _log(string.Format("[RimSynapse] Deferred hold {0} timed out after {1:0.#}s; releasing with {2}/{3} stage(s) done.",
                    hold.Id, _timeoutSeconds, hold.StageIndex, hold.Stages.Count));
                Release(hold);
            }
        }

        private void Release(Hold hold)
        {
            if (hold.Released) return;
            hold.Released = true;
            _active.Remove(hold.Id);

            bool valid;
            try { valid = hold.IsValid(); }
            catch (Exception ex)
            {
                _log(string.Format("[RimSynapse] Deferred hold {0} validity check threw ([{1}] {2}); discarding.",
                    hold.Id, ex.GetType().Name, ex.Message));
                valid = false;
            }

            if (!valid)
            {
                _log(string.Format("[RimSynapse] Deferred hold {0} discarded: referenced state is no longer valid.", hold.Id));
                hold.Payload = null;    // never retain a discarded payload
                return;
            }

            try { hold.Release(); }
            catch (Exception ex)
            {
                _log(string.Format("[RimSynapse] Deferred hold {0} release threw ([{1}] {2}).",
                    hold.Id, ex.GetType().Name, ex.Message));
            }
            hold.Payload = null;        // release done — drop the reference (defect #2: no unbounded retention)
        }
    }
}
