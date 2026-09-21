using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LudeonTK;
using RimWorld;
using Verse;

namespace RimSynapse.Personas
{
    /// <summary>
    /// Debug validation for the storyteller persona engine (Core #69/#89/#90), grouped under
    /// "RimSynapse". Headlessly runnable via the toolkit's run_debug_action. Each action exercises the
    /// deterministic core (persona resolution, prompt composition, prose fallback, shift detection)
    /// without needing a live backend, and drives a live Speak so the display path is exercised too.
    /// </summary>
    public static class DebugActions_Persona
    {
        [DebugAction("RimSynapse", "Persona: dump def + current entry (#89)",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DumpPersona()
        {
            var sb = new StringBuilder();
            var persona = SynapsePersonaEngine.ActivePersona();
            sb.AppendLine($"[RimSynapse] Persona def (Core #89): {persona?.defName ?? "<none>"} fourthWall={persona?.fourthWall}");
            var errors = persona?.ConfigErrors()?.ToList() ?? new List<string>();
            sb.AppendLine($"  def-linter: {(errors.Count == 0 ? "clean" : errors.Count + " problem(s)")}");
            foreach (var e in errors) sb.AppendLine($"    - {e}");

            var def = Find.Storyteller?.difficultyDef;
            var entry = SynapsePersonaEngine.CurrentEntry(persona);
            sb.AppendLine($"  live difficulty: {def?.defName ?? "?"} (custom={def?.isCustom})  → entry: {(entry == null ? "<none>" : entry.difficulty)}");
            if (entry != null)
            {
                sb.AppendLine($"  prompt: {entry.personaPrompt}");
                foreach (PersonaBeat beat in Enum.GetValues(typeof(PersonaBeat)))
                    sb.AppendLine($"  {beat} fallback: {SynapsePersonaEngine.FallbackLine(entry, beat)}");
            }
            Log.Message(sb.ToString());
        }

        [DebugAction("RimSynapse", "Persona: speak kickoff (#69)",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SpeakKickoff() => CaptureAndSpeak("kickoff",
            () => SynapsePersonaEngine.SpeakKickoff("Raid", "the eastern ridge", 800f, "the Ferals"));

        [DebugAction("RimSynapse", "Persona: speak resolution +/- (#69)",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SpeakResolutions() => CaptureAndSpeak("resolution", () =>
        {
            SynapsePersonaEngine.SpeakResolution("Raid", "the eastern ridge", "held");
            SynapsePersonaEngine.SpeakResolution("Raid", "the eastern ridge", "colonists died, position overrun");
        });

        [DebugAction("RimSynapse", "Persona: callback from world history (#69)",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SpeakCallback() => CaptureAndSpeak("callback", () =>
        {
            var thread = SynapsePersonaEngine.SpeakCallbackFromHistory();
            Log.Message($"[RimSynapse] callback thread: {(thread == null ? "<no open thread — nothing to pay off>" : thread.kind + " / " + thread.region)}");
        });

        [DebugAction("RimSynapse", "Persona: custom-shift detect (#90)",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CustomShift()
        {
            var sb = new StringBuilder();
            var def = Find.Storyteller?.difficultyDef;
            sb.AppendLine($"[RimSynapse] Custom-shift detect (Core #90): difficulty={def?.defName} custom={def?.isCustom}");
            var shifts = CustomDifficultyShift.DetectLive();
            if (shifts.Count == 0) sb.AppendLine("  no notable shifts (or not on Custom) — she stays quiet, as intended.");
            foreach (var s in shifts) sb.AppendLine($"  shift: {s.slider} = {s.value} → dir '{s.dir}'");
            var entry = SynapsePersonaEngine.CurrentEntry(SynapsePersonaEngine.ActivePersona());
            var ctx = CustomDifficultyShift.BuildShiftContext(entry, shifts);
            sb.AppendLine($"  shift context: {(ctx.NullOrEmpty() ? "<none>" : ctx)}");
            Log.Message(sb.ToString());
        }

        /// <summary>Swap the LineSink to a log-collector for the duration of an action, so the produced
        /// lines are dumped whether or not the display path is active.</summary>
        private static void CaptureAndSpeak(string label, Action drive)
        {
            var captured = new List<string>();
            var original = SynapsePersonaEngine.LineSink;
            SynapsePersonaEngine.LineSink = (beat, line) => { captured.Add($"[{beat}] {line}"); original?.Invoke(beat, line); };
            try { drive(); }
            finally { SynapsePersonaEngine.LineSink = original; }

            var sb = new StringBuilder();
            sb.AppendLine($"[RimSynapse] Persona {label} (Core #69): active={SynapsePersonaEngine.IsActive()}  lines={captured.Count}");
            foreach (var l in captured) sb.AppendLine($"  {l}");
            if (captured.Count == 0) sb.AppendLine("  (no line — dormant under a vanilla storyteller, or the LLM callback is still in flight)");
            Log.Message(sb.ToString());
        }
    }
}
