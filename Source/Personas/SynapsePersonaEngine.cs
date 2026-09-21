using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimSynapse.Comps;
using RimWorld;
using Verse;

namespace RimSynapse.Personas
{
    /// <summary>
    /// The persona engine (Core #69): gives the RimSynapse storyteller a voice. It reads whichever
    /// <see cref="StorytellerPersonaDef"/> the active storyteller names (Aura by default), selects the
    /// difficulty entry by the live difficulty, and regenerates the beat's notification text through
    /// the LLM using the persona prompt + prose exemplars + the event payload. When the LLM is
    /// unavailable the prose exemplar is the deterministic fallback, so a beat is never silent by
    /// accident.
    ///
    /// <para>Dormant unless a <see cref="StorytellerComp_Storyteller"/> is the selected storyteller —
    /// under Cassandra/Phoebe/Randy the engine says nothing.</para>
    ///
    /// <para>The persona is the ONLY fourth-wall voice (it addresses the player, knows it authors the
    /// story). WorldNews and pawn/faction dialogue stay diegetic; the tone law is carried in the
    /// persona prompt so a modder's own persona may opt into a diegetic voice.</para>
    /// </summary>
    public static class SynapsePersonaEngine
    {
        /// <summary>Where a produced line goes. Default posts an Aura message on the meta channel;
        /// tests swap it to capture the (beat, line) pair. Never null in normal play.</summary>
        public static Action<PersonaBeat, string> LineSink = DefaultSink;

        /// <summary>True only when a RimSynapse storyteller is the selected storyteller.</summary>
        public static bool IsActive()
            => Find.Storyteller?.storytellerComps?.OfType<StorytellerComp_Storyteller>().Any() == true;

        /// <summary>The persona the active storyteller names, or Aura. Never null while defs are loaded.</summary>
        public static StorytellerPersonaDef ActivePersona()
        {
            var props = StorytellerComp_Storyteller.GetActiveStorytellerProps();
            return ResolvePersonaByName(props?.personaDefName);
        }

        /// <summary>Resolve a persona by def name; unknown or empty falls back to the shipped Aura. The
        /// seam Core_SecondPersonaDefLoadsAlongside asserts against (a third-party persona resolves; a
        /// bad name does not blank the voice).</summary>
        internal static StorytellerPersonaDef ResolvePersonaByName(string name)
        {
            StorytellerPersonaDef def = null;
            if (!name.NullOrEmpty())
                def = DefDatabase<StorytellerPersonaDef>.GetNamedSilentFail(name);
            return def ?? StorytellerPersonaDefOf.Aura;
        }

        /// <summary>The persona entry for the live difficulty, or null when the persona has no entry.</summary>
        public static PersonaDifficultyEntry CurrentEntry(StorytellerPersonaDef persona)
        {
            var d = Find.Storyteller?.difficultyDef;
            return persona?.EntryFor(d?.defName, d?.isCustom ?? false);
        }

        /// <summary>Map a lifecycle-hook outcome string to the resolution beat. Anything reading as a
        /// loss/failure is negative; everything else (held, won, resolved, empty) is positive.</summary>
        public static PersonaBeat OutcomeToBeat(string outcome)
            => IsNegativeOutcome(outcome) ? PersonaBeat.ResolutionNegative : PersonaBeat.ResolutionPositive;

        private static readonly string[] NegativeMarkers =
            { "lost", "loss", "lose", "fail", "defeat", "died", "death", "destroyed", "overrun", "wiped", "negative", "bad" };

        internal static bool IsNegativeOutcome(string outcome)
        {
            if (string.IsNullOrEmpty(outcome)) return false;
            string o = outcome.ToLowerInvariant();
            return NegativeMarkers.Any(m => o.Contains(m));
        }

        /// <summary>The deterministic fallback line for a beat: a prose exemplar from the def. Null only
        /// when the entry has no prose for the beat (which the def-linter forbids for Aura).</summary>
        public static string FallbackLine(PersonaDifficultyEntry entry, PersonaBeat beat)
        {
            var lines = entry?.prose?.For(beat);
            if (lines == null || lines.Count == 0) return null;
            var usable = lines.Where(l => !l.NullOrEmpty()).ToList();
            return usable.Count == 0 ? null : usable.RandomElement();
        }

        /// <summary>
        /// Build the regeneration prompt: persona identity + tone law + mood + prose exemplars (style
        /// seed) + the event payload (and any custom-shift context). Pure — this is the seam the
        /// Core_PersonaDefRegeneratesFlyerText case asserts against.
        /// </summary>
        public static string ComposePrompt(StorytellerPersonaDef persona, PersonaDifficultyEntry entry,
            PersonaBeat beat, string payload, string shiftContext)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"You are {persona?.label ?? persona?.defName ?? "the storyteller"}, the storyteller narrating this colony.");
            if (persona != null && persona.fourthWall)
                sb.AppendLine("You break the fourth wall: address the player directly and never hide that you are the one writing this story. Do not speak as a colonist or a newsreader — that is a different, diegetic voice.");
            if (!entry.personaPrompt.NullOrEmpty())
                sb.AppendLine($"Right now your character is: {entry.personaPrompt}");
            sb.AppendLine($"This is your {BeatLabel(beat)} line.");

            var exemplars = entry.prose?.For(beat)?.Where(l => !l.NullOrEmpty()).ToList();
            if (exemplars != null && exemplars.Count > 0)
            {
                sb.AppendLine("Match the voice of these examples (do not copy them verbatim):");
                foreach (var ex in exemplars) sb.AppendLine($"  - {ex}");
            }
            sb.AppendLine("Write ONE short line, in character. No quotation marks, no preamble.");

            var user = new StringBuilder();
            user.AppendLine($"Event: {(payload.NullOrEmpty() ? "(unspecified)" : payload)}");
            if (!shiftContext.NullOrEmpty())
                user.AppendLine($"The player's own difficulty settings to needle: {shiftContext}");

            // The composed prompt is system + a delimiter + user, so a single string carries both for
            // the linter/test; SpeakAsync splits them back to system/user for the LLM call.
            return sb.ToString().TrimEnd() + "\n\n---\n" + user.ToString().TrimEnd();
        }

        private static string BeatLabel(PersonaBeat beat)
        {
            switch (beat)
            {
                case PersonaBeat.Kickoff: return "incident-kickoff";
                case PersonaBeat.ResolutionPositive: return "resolution (the player held/won)";
                case PersonaBeat.ResolutionNegative: return "resolution (the player suffered/lost)";
                case PersonaBeat.Callback: return "callback (paying off an earlier thread)";
                default: return "idle ambient";
            }
        }

        // ── Beat entry points (the wiring calls these) ───────────────────────────

        /// <summary>Kickoff beat — she fired an incident (from the lifecycle hook / fire path).</summary>
        public static void SpeakKickoff(string kind, string region, float magnitude, string origin)
        {
            string payload = $"You are firing a {Describe(kind)}{RegionClause(region)}{OriginClause(origin)}.";
            Speak(PersonaBeat.Kickoff, payload);
        }

        /// <summary>Resolution beat — an incident just resolved (from the lifecycle hook).</summary>
        public static void SpeakResolution(string kind, string region, string outcome)
        {
            var beat = OutcomeToBeat(outcome);
            string held = beat == PersonaBeat.ResolutionNegative ? "went badly for the colony" : "was weathered by the colony";
            string payload = $"The {Describe(kind)}{RegionClause(region)} {held} (outcome: {(outcome.NullOrEmpty() ? "resolved" : outcome)}).";
            Speak(beat, payload);
        }

        /// <summary>Callback beat — pay off a real open thread from world history (#65). No-op when
        /// there is no open thread, so she never invents a callback to an event that did not happen.
        /// Returns the entry she paid off (for the debug action / test), or null.</summary>
        public static WorldHistoryEntry SpeakCallbackFromHistory()
        {
            if (!IsActive()) return null;
            var thread = PickOpenThread();
            if (thread == null) return null;
            Speak(PersonaBeat.Callback, ComposeCallbackPayload(thread));
            return thread;
        }

        /// <summary>An open thread to call back to, or null. Oldest-open first so old debts pay off.</summary>
        public static WorldHistoryEntry PickOpenThread()
        {
            var hist = Find.World?.GetComponent<SynapseCoreWorldComponent>();
            var open = hist?.OpenThreads();
            if (open == null) return null;
            return open.OrderBy(e => e.startTick).FirstOrDefault();
        }

        /// <summary>Payload for a callback, built only from the real history entry's own fields — the
        /// seam Core_AuraCallbackReferencesRealHistory asserts references a real entry, not an invention.</summary>
        public static string ComposeCallbackPayload(WorldHistoryEntry entry)
        {
            if (entry == null) return null;
            var sb = new StringBuilder();
            sb.Append($"Call back to an earlier still-open thread: a {Describe(entry.kind)}");
            if (!entry.region.NullOrEmpty()) sb.Append($" near {entry.region}");
            if (!entry.origin.NullOrEmpty()) sb.Append($" from {entry.origin}");
            sb.Append(". Pay it off now — reference it as a thread you planted, and do not invent new events.");
            return sb.ToString();
        }

        /// <summary>
        /// The core flow: dormancy-gate, resolve persona + difficulty entry, compose the prompt, then
        /// regenerate through the LLM with the prose exemplar as the deterministic fallback. The
        /// produced line goes to <see cref="LineSink"/>. <paramref name="shiftContext"/> carries the
        /// Custom-difficulty shift material (Core #90).
        /// </summary>
        public static void Speak(PersonaBeat beat, string payload, string shiftContext = null)
        {
            if (!IsActive()) return; // dormant under a vanilla storyteller
            var persona = ActivePersona();
            var entry = CurrentEntry(persona);
            if (entry == null) return;

            // On Custom difficulty, needle the player's own dials (Core #90) unless a caller supplied
            // its own shift material. Cheap no-op on non-Custom (DetectLive returns empty).
            if (shiftContext == null && (Find.Storyteller?.difficultyDef?.isCustom ?? false))
                shiftContext = CustomDifficultyShift.LiveContext(entry);

            string prompt = ComposePrompt(persona, entry, beat, payload, shiftContext);
            string fallback = FallbackLine(entry, beat);
            RegenerateAsync(prompt, fallback, line =>
            {
                if (!line.NullOrEmpty()) LineSink?.Invoke(beat, line);
            });
        }

        /// <summary>Regenerate the line through the LLM; on any failure or empty content, use the
        /// deterministic prose fallback. The prompt is split back into system/user at the delimiter.</summary>
        private static void RegenerateAsync(string composedPrompt, string fallback, Action<string> onLine)
        {
            SplitPrompt(composedPrompt, out string system, out string user);
            var options = new ChatOptions { priority = 3, requestName = "persona-line", targetName = "storyteller" };
            try
            {
                SynapseClient.PromptAsync(RimSynapseMod.ModHandle, system, user, result =>
                {
                    string text = (result != null && result.success && !result.content.NullOrEmpty())
                        ? CleanLine(result.content)
                        : fallback;
                    onLine(text.NullOrEmpty() ? fallback : text);
                }, options);
            }
            catch (Exception ex)
            {
                SynapseLogger.Message($"[Persona] regeneration failed, using fallback: {ex.Message}", "performance");
                onLine(fallback);
            }
        }

        internal static void SplitPrompt(string composed, out string system, out string user)
        {
            int i = composed?.IndexOf("\n\n---\n") ?? -1;
            if (i < 0) { system = composed ?? ""; user = ""; return; }
            system = composed.Substring(0, i);
            user = composed.Substring(i + "\n\n---\n".Length);
        }

        /// <summary>Trim an LLM reply to one clean line: first non-empty line, stripped of wrapping quotes.</summary>
        internal static string CleanLine(string content)
        {
            if (content.NullOrEmpty()) return content;
            string first = content.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? content.Trim();
            first = first.Trim();
            if (first.Length >= 2 && ((first[0] == '"' && first[first.Length - 1] == '"') ||
                                      (first[0] == '\'' && first[first.Length - 1] == '\'')))
                first = first.Substring(1, first.Length - 2).Trim();
            return first;
        }

        private static string Describe(string kind) => kind.NullOrEmpty() ? "event" : kind;
        private static string RegionClause(string region) => region.NullOrEmpty() ? "" : $" near {region}";
        private static string OriginClause(string origin) => origin.NullOrEmpty() ? "" : $" from {origin}";

        private static void DefaultSink(PersonaBeat beat, string line)
        {
            if (line.NullOrEmpty()) return;
            var persona = ActivePersona();
            string speaker = persona?.label ?? persona?.defName ?? "Storyteller";
            Messages.Message($"{speaker}: {line}", MessageTypeDefOf.NeutralEvent, historical: false);
        }
    }
}
