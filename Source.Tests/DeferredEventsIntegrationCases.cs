using System;
using System.Collections.Generic;
using System.Reflection;
using Verse;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// The deferred-event integration surface (Core #59): SynapseDeferredEvents, the Harmony+reflection
    /// layer over the pure pipeline. Validates the reflection-registration contract — a consumer registers
    /// a classification + ordered stage and holds an event with no Core type in any signature — and the
    /// held → stage → release path. The pure state machine's timeout/invalidation/cap live in the
    /// game-free Core/Tests sandbox (real-seconds deadlines can't be fast-forwarded in-game).
    /// </summary>
    [SynapseTestSet]
    public static class DeferredEventsIntegrationCases
    {
        private const string TestClass = "zz_test_deferred";

        public static IEnumerable<SynapseTestCase> All()
        {
            // Register a classification + stage and hold an event — all by reflection, no Core type used.
            yield return new SynapseTestCase("Core_DeferredEvents_ReflectionStageReleases", () =>
            {
                Type t = GenTypes.GetTypeInAnyAssembly("RimSynapse.SynapseDeferredEvents");
                Assert.NotNull(t, "SynapseDeferredEvents resolves by fully-qualified name");

                MethodInfo regClass = t.GetMethod("RegisterClassification", BindingFlags.Public | BindingFlags.Static);
                MethodInfo regStage = t.GetMethod("RegisterStage", BindingFlags.Public | BindingFlags.Static);
                MethodInfo tryHold = t.GetMethod("TryHold", BindingFlags.Public | BindingFlags.Static);
                Assert.NotNull(regClass, "RegisterClassification is reflection-invokable");
                Assert.NotNull(regStage, "RegisterStage is reflection-invokable");
                Assert.NotNull(tryHold, "TryHold is reflection-invokable");

                regClass.Invoke(null, new object[] { TestClass, true });

                bool stageRan = false, released = false;
                Action<object, Action> stage = (payload, done) => { stageRan = true; done(); };
                regStage.Invoke(null, new object[] { TestClass, 100, stage });

                Action release = () => released = true;
                Func<bool> isValid = () => true;
                object held = tryHold.Invoke(null, new object[] { TestClass, "payload", release, isValid });

                Assert.True((bool)held, "the event was held (a stage is registered and under the cap)");
                Assert.True(stageRan, "the reflection-registered stage ran");
                Assert.True(released, "the event was released after the stage completed");
                return "reflection register + hold + ordered stage + release";
            });

            // An unclassified event class is never held — the fail-safe (MustNotDelay) default.
            yield return new SynapseTestCase("Core_DeferredEvents_UnclassifiedNotHeld", () =>
            {
                bool notHeld = !SynapseDeferredEvents.TryHold(
                    "zz_unclassified_event", "payload", () => { }, () => true);
                Assert.True(notHeld, "an unclassified class is not held (fires immediately)");
                return "unclassified fires immediately";
            });
        }
    }
}
