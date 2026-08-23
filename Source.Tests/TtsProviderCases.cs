using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Verse;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// The Core speech surface (Core #111): SynapseCoreProviders.TextToSpeech +
    /// SynapseSpeech.TrySpeak. Deterministic — no speech mod is loaded under -quicktest, so the
    /// unregistered case is the real environment, and the registered cases install a fake by
    /// reflection (the companion registration path) and restore the slot afterwards. The provider
    /// runs on the thread pool, so registered cases wait on an event with a bounded timeout.
    /// The contained-throw path logs a warning, never an error — a deliberate throw here must not
    /// leave a blocking log entry behind.
    /// </summary>
    [SynapseTestSet]
    public static class TtsProviderCases
    {
        private static readonly Type ProviderDelegateType = typeof(Func<string, string, Action<byte[]>, bool>);

        public static IEnumerable<SynapseTestCase> All()
        {
            // Nobody registered: the documented no-op — false, silence, state says unregistered.
            yield return new SynapseTestCase("Core_TtsSlotUnregisteredIsNoOp", () =>
            {
                var saved = SynapseCoreProviders.TextToSpeech;
                try
                {
                    SynapseCoreProviders.TextToSpeech = null;
                    SynapseCoreProviders.ResetWarningsForTesting();

                    Assert.False(SynapseSpeech.TrySpeak("nobody should hear this"),
                        "with no provider registered, TrySpeak returns false");
                    Assert.False(SynapseSpeech.TrySpeak(null),
                        "null text is answered with false, not a throw");
                    Assert.Contains(SynapseSpeech.DescribeState(), "unregistered",
                        "the debug state reports the slot as unregistered");
                    return "unregistered TrySpeak=false, state reports no-op";
                }
                finally { SynapseCoreProviders.TextToSpeech = saved; }
            });

            // A fake provider registered exactly as a companion would — by reflection, no Core
            // reference needed — receives the text and hint off-thread and delivers PCM.
            yield return new SynapseTestCase("Core_TtsFakeProviderRoutesText", () =>
            {
                var saved = SynapseCoreProviders.TextToSpeech;
                try
                {
                    var slot = ResolveSlotByReflection();
                    string gotText = null, gotHint = null;
                    var invoked = new ManualResetEventSlim(false);

                    slot.SetValue(null, (Func<string, string, Action<byte[]>, bool>)((text, hint, onPcm) =>
                    {
                        gotText = text;
                        gotHint = hint;
                        onPcm(new byte[64]); // 32 samples of silence — proves the delivery path
                        invoked.Set();
                        return true;
                    }));

                    Assert.True(SynapseSpeech.TrySpeak("routed line", "af_heart"),
                        "with a provider registered, TrySpeak dispatches and returns true");
                    Assert.True(invoked.Wait(2000),
                        "the provider is invoked off-thread within 2s");
                    Assert.True(gotText == "routed line" && gotHint == "af_heart",
                        $"text and voice hint arrive unchanged (got \"{gotText}\" / \"{gotHint}\")");
                    Assert.Contains(SynapseSpeech.DescribeState(), "delivered 64 bytes",
                        "delivered PCM is recorded and handed to playback");
                    return "reflection-registered provider got text+hint, delivered 64 bytes";
                }
                finally { SynapseCoreProviders.TextToSpeech = saved; }
            });

            // A throwing provider is contained: the caller still gets true (the hand-off
            // happened), nothing propagates, and the failure is visible in the slot state.
            yield return new SynapseTestCase("Core_TtsThrowingProviderIsContained", () =>
            {
                var saved = SynapseCoreProviders.TextToSpeech;
                try
                {
                    var slot = ResolveSlotByReflection();
                    slot.SetValue(null, (Func<string, string, Action<byte[]>, bool>)((text, hint, onPcm) =>
                        throw new InvalidOperationException("boom")));

                    Assert.True(SynapseSpeech.TrySpeak("will not be spoken"),
                        "the dispatch itself succeeds; the throw happens on the worker");

                    bool contained = false;
                    for (int i = 0; i < 80 && !contained; i++)
                    {
                        contained = SynapseSpeech.DescribeState().Contains("provider threw [InvalidOperationException]");
                        if (!contained) Thread.Sleep(25);
                    }
                    Assert.True(contained,
                        "the throw is contained and recorded in the slot state within 2s");
                    return "throw contained off-thread, recorded as provider threw [InvalidOperationException]";
                }
                finally { SynapseCoreProviders.TextToSpeech = saved; }
            });
        }

        /// <summary>The companion registration path: resolve the slot by name, never by reference.</summary>
        private static PropertyInfo ResolveSlotByReflection()
        {
            var t = GenTypes.GetTypeInAnyAssembly("RimSynapse.SynapseCoreProviders");
            Assert.NotNull(t, "the provider registry resolves by fully-qualified name");
            var slot = t.GetProperty("TextToSpeech", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(slot, "the TextToSpeech slot resolves by name and is public static");
            Assert.True(slot.PropertyType == ProviderDelegateType, "the slot's delegate type is the documented contract");
            return slot;
        }
    }
}
