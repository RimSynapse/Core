using System;
using UnityEngine;

namespace RimSynapse
{
    /// <summary>
    /// The Harmony + reflection surface over <see cref="SynapseDeferredEventPipeline"/> (Core #59).
    /// Owns the single pipeline instance and drives its deadlines from the main-thread update; the
    /// pure state machine stays game-free while this layer holds the RimWorld/Unity coupling.
    ///
    /// <para><b>Real-seconds clock.</b> The deadline uses <see cref="Time.realtimeSinceStartup"/>, which
    /// advances while the game is paused — so a hold cannot outlive its deadline just because the player
    /// paused. Every call here runs on the main thread (the Harmony prefix, the update tick, and the
    /// stage's <c>done</c> callback which the integration marshals via <c>SynapseGameComponent.Enqueue</c>).</para>
    ///
    /// <para><b>Reflection registration.</b> <see cref="RegisterStage"/> / <see cref="RegisterClassification"/>
    /// take only primitives and a delegate with no Core type in its signature, so a companion registers a
    /// deferred stage by reflection and builds with Core absent — the same rule as the provider registry.</para>
    /// </summary>
    public static class SynapseDeferredEvents
    {
        /// <summary>The built-in event class for letters routed through <c>LetterStack.ReceiveLetter</c>.</summary>
        public const string LetterClass = "letter";

        // A held event releases within this many real seconds no matter what a stage does — the timeout
        // that guarantees a suppressed letter still arrives (defect #1).
        private const double TimeoutSeconds = 30.0;

        // Beyond this many concurrent holds, further events fire unheld rather than piling up.
        private const int MaxConcurrentHolds = 8;

        private static readonly SynapseDeferredEventPipeline _pipeline =
            new SynapseDeferredEventPipeline(NowSeconds, TimeoutSeconds, MaxConcurrentHolds, LogLine);

        private static double NowSeconds() => Time.realtimeSinceStartup;

        private static void LogLine(string s) => SynapseLogger.Message(s, "deferred");

        /// <summary>Holds currently running.</summary>
        public static int ActiveHoldCount => _pipeline.ActiveHoldCount;

        /// <summary>Classify an event class as holdable. Unclassified classes are never held (MustNotDelay).</summary>
        public static void RegisterClassification(string eventClass, bool holdable)
            => _pipeline.RegisterClassification(eventClass, holdable);

        /// <summary>
        /// Register an ordered deferred stage for an event class. Lower <paramref name="order"/> runs
        /// first; each stage must call its <c>done</c> action before the next begins. The stage receives
        /// the event payload as <see cref="object"/> and a <see cref="Action"/> to signal completion —
        /// no Core type appears, so this is reflection-registrable from a Core-less consumer.
        /// </summary>
        public static void RegisterStage(string eventClass, int order, Action<object, Action> stage)
            => _pipeline.RegisterStage(eventClass, order, stage);

        /// <summary>
        /// Attempt to hold an event through its registered stages, releasing via <paramref name="release"/>
        /// once every stage completes or the deadline fires (whichever first). <paramref name="isValid"/>
        /// is re-checked immediately before release. Returns true when held (the caller must suppress the
        /// vanilla event); false when it should fire immediately (unclassified, no stages, or at the cap).
        /// </summary>
        public static bool TryHold(string eventClass, object payload, Action release, Func<bool> isValid)
            => _pipeline.TryHold(eventClass, payload, release, isValid);

        /// <summary>Drive the deadlines. Called once per frame from <c>SynapseGameComponent.GameComponentUpdate</c>.</summary>
        public static void Tick() => _pipeline.Tick();
    }
}
