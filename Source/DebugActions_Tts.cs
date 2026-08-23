using LudeonTK;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for the Core speech surface (Core #111), grouped under "RimSynapse".
    /// Speaks a canned line through <see cref="SynapseSpeech.TrySpeak(string, string)"/> — exactly
    /// the path the chat and letter call sites use — and dumps the slot state. Headlessly runnable
    /// via run_debug_action; with no speech mod installed the speak action logs the documented
    /// no-op (returns false, silence) rather than failing.
    /// </summary>
    public static class DebugActions_Tts
    {
        [DebugAction("RimSynapse", "TTS: speak canned line",
            allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.Playing)]
        private static void SpeakCannedLine()
        {
            const string canned = "The rim remembers every colonist you lose. So do I.";
            bool dispatched = SynapseSpeech.TrySpeak(canned, null);
            SynapseLogger.Message(
                $"[RimSynapse] TTS debug: TrySpeak returned {dispatched} " +
                $"({(dispatched ? "request handed to the registered provider" : "no provider registered — silent no-op")}). " +
                SynapseSpeech.DescribeState());
        }

        [DebugAction("RimSynapse", "TTS: dump slot state",
            allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.Playing)]
        private static void DumpSlotState()
        {
            SynapseLogger.Message("[RimSynapse] " + SynapseSpeech.DescribeState());
        }
    }
}
