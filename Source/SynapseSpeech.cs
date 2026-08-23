using System;
using System.Threading;

namespace RimSynapse
{
    /// <summary>
    /// The consumer surface for spoken audio (Core #111). Core owns the question — "speak this" —
    /// and the Local Text to Speech companion owns the synthesis, registered on
    /// <see cref="SynapseCoreProviders.TextToSpeech"/>. Consumers call <see cref="TrySpeak(string)"/>
    /// and never touch the slot: unregistered means false and silence, the documented no-op.
    ///
    /// <para><b>Threading:</b> callable from any thread. The provider is invoked on the thread
    /// pool, so a provider that blocks or throws cannot stall or crash the caller; delivered PCM
    /// lands on <see cref="Utils.AudioPlaybackManager"/>, which marshals playback to the main
    /// thread itself. The returned bool means "a registered provider was handed the request", not
    /// "audio played" — refusal and synthesis failure are asynchronous and land in the log and
    /// <see cref="DescribeState"/>.</para>
    /// </summary>
    public static class SynapseSpeech
    {
        private const string LogCategory = "tts";

        // Debug-dump state (Core #111 acceptance: registered / provider / last result). Written
        // from pool threads, read from the debug action — volatile string swaps are enough.
        private static volatile string lastText;
        private static volatile string lastResult = "(no request yet)";

        /// <summary>Speak <paramref name="text"/> with no voice preference.</summary>
        public static bool TrySpeak(string text) => TrySpeak(text, null);

        /// <summary>
        /// Speak <paramref name="text"/>, preferring <paramref name="voiceHint"/> (a Kokoro voice
        /// id or file path; advisory — the provider may ignore it). Returns false when the text is
        /// empty or nobody owns synthesis; true means the registered provider was handed the
        /// request off-thread.
        /// </summary>
        public static bool TrySpeak(string text, string voiceHint)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            var provider = SynapseCoreProviders.TextToSpeechOrNote();
            if (provider == null) return false;

            lastText = text;
            lastResult = "dispatched";

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    bool accepted = provider(text, voiceHint, OnPcmDelivered);
                    if (!accepted)
                    {
                        lastResult = "refused by provider";
                        SynapseLogger.Message("[RimSynapse-Core] TTS provider refused a speech request.", LogCategory);
                    }
                    else if (lastResult == "dispatched")
                    {
                        // A provider that delivered synchronously already advanced the state to
                        // "delivered N bytes" — don't roll it back to the earlier stage.
                        lastResult = "accepted by provider";
                    }
                }
                catch (Exception ex)
                {
                    // A throwing provider must not take anything down; the request just goes
                    // silent. Contained, so logged as a handled warning (bracketed type per the
                    // log conventions), not as an error.
                    lastResult = $"provider threw [{ex.GetType().Name}]";
                    SynapseLogger.Warning(
                        $"[RimSynapse-Core] Provider 'TextToSpeech' threw [{ex.GetType().Name}] {ex.Message} — contained; the line goes unspoken.",
                        LogCategory);
                }
            });
            return true;
        }

        private static void OnPcmDelivered(byte[] pcm)
        {
            if (pcm == null || pcm.Length == 0)
            {
                lastResult = "provider delivered no audio";
                return;
            }
            lastResult = $"delivered {pcm.Length} bytes";
            // Delivery arrives on the provider's worker; AudioClip creation inside PlayPcm is
            // main-thread-only, so hop first.
            SynapseGameComponent.Enqueue(() => Utils.AudioPlaybackManager.PlayPcm(pcm));
        }

        /// <summary>One-line slot/state summary for the debug dump.</summary>
        public static string DescribeState()
        {
            var provider = SynapseCoreProviders.TextToSpeech;
            string registered = provider == null
                ? "unregistered (speech is a silent no-op)"
                : $"registered ({provider.Method.DeclaringType?.FullName}.{provider.Method.Name})";
            string last = lastText == null ? lastResult : $"{lastResult} | last text: \"{lastText}\"";
            return $"TextToSpeech slot: {registered}; last request: {last}";
        }
    }
}
