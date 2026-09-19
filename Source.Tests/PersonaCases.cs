using System;
using System.Collections.Generic;
using System.Linq;
using RimSynapse;
using RimSynapse.Personas;
using RimAgentic.Testing;
using Verse;

namespace RimSynapse.Tests
{
    /// <summary>
    /// The storyteller persona engine (Core #69/#89/#90). Tier-2 (defs are loaded): these assert the
    /// deterministic seams — persona/entry resolution, prompt composition, prose fallback, resolution
    /// split, callback provenance, moddable second persona, and custom-difficulty shift detection.
    /// Whether the regenerated voice is any *good* is a playtest concern, not CI.
    /// </summary>
    [SynapseTestSet]
    public static class PersonaCases
    {
        private static StorytellerPersonaDef Aura => StorytellerPersonaDefOf.Aura;

        public static IEnumerable<SynapseTestCase> All()
        {
            // The shipped Aura def loads and passes its own linter — every difficulty, every beat.
            yield return new SynapseTestCase("Core_AuraDefLoadsComplete", () =>
            {
                Assert.True(Aura != null, "Aura persona def must load");
                var errors = Aura.ConfigErrors().ToList();
                Assert.Equal(0, errors.Count, "Aura def-linter must be clean: " + string.Join("; ", errors));
                foreach (var req in StorytellerPersonaDef.RequiredDifficulties)
                    Assert.True(Aura.EntryFor(req, req == "Custom") != null, $"Aura must have an entry for {req}");
                return "aura complete";
            });

            // Regeneration prompt is built from persona prompt + prose exemplars + the event payload.
            yield return new SynapseTestCase("Core_PersonaDefRegeneratesFlyerText", () =>
            {
                var entry = Aura.EntryFor("Rough", false);
                Assert.True(entry != null, "Rough entry must exist");
                string payload = "a raid strikes the eastern ridge";
                string prompt = SynapsePersonaEngine.ComposePrompt(Aura, entry, PersonaBeat.Kickoff, payload, null);
                Assert.Contains(prompt, payload, "prompt must carry the event payload");
                Assert.Contains(prompt, "Aura", "prompt must name the persona");
                string exemplar = entry.prose.kickoff.First();
                Assert.Contains(prompt, exemplar, "prompt must seed the LLM with a prose exemplar");
                Assert.Contains(prompt, "fourth wall", "Aura's tone law (fourth wall) must be in the prompt");
                return "regenerates from def + payload";
            });

            // Prose is the deterministic fallback when the LLM is unavailable.
            yield return new SynapseTestCase("Core_PersonaFallbackUsesProseWhenLlmAbsent", () =>
            {
                var entry = Aura.EntryFor("Hard", false);
                foreach (PersonaBeat beat in Enum.GetValues(typeof(PersonaBeat)))
                {
                    string line = SynapsePersonaEngine.FallbackLine(entry, beat);
                    Assert.True(!line.NullOrEmpty(), $"fallback for {beat} must be non-empty");
                    Assert.True(entry.prose.For(beat).Contains(line), $"fallback for {beat} must be a def prose exemplar");
                }
                return "fallback is prose";
            });

            // Mood selects the def entry by difficulty; the prompts scale from bored to gleeful.
            yield return new SynapseTestCase("Core_AuraMoodScalesWithDifficulty", () =>
            {
                var peaceful = Aura.EntryFor("Peaceful", false);
                var extreme = Aura.EntryFor("Extreme", false);
                Assert.True(peaceful != null && extreme != null, "both entries must resolve");
                Assert.True(peaceful.personaPrompt != extreme.personaPrompt, "mood must change the prompt");
                Assert.Contains(peaceful.personaPrompt.ToLowerInvariant(), "bored", "Peaceful Aura is bored");
                Assert.Contains(extreme.personaPrompt.ToLowerInvariant(), "gleeful", "Extreme Aura is gleeful");
                // Custom difficulty resolves to the Custom entry via the isCustom fallback.
                Assert.Equal("Custom", Aura.EntryFor("SomeModdedCustomDef", true)?.difficulty,
                    "any custom difficulty resolves to the Custom entry");
                return "mood scales";
            });

            // She comments on kickoff and on resolution, split into positive and negative.
            yield return new SynapseTestCase("Core_AuraCommentsOnKickoffAndResolution", () =>
            {
                var entry = Aura.EntryFor("Rough", false);
                Assert.True(!SynapsePersonaEngine.FallbackLine(entry, PersonaBeat.Kickoff).NullOrEmpty(),
                    "kickoff beat must produce a line");
                Assert.Equal(PersonaBeat.ResolutionPositive, SynapsePersonaEngine.OutcomeToBeat("held"),
                    "a held/won outcome is a positive resolution");
                Assert.Equal(PersonaBeat.ResolutionNegative,
                    SynapsePersonaEngine.OutcomeToBeat("colonists died, position overrun"),
                    "a loss outcome is a negative resolution");
                Assert.False(SynapsePersonaEngine.IsNegativeOutcome("the colony held the line"),
                    "'held' must not read as negative");
                return "kickoff + split resolution";
            });

            // Every difficulty carries BOTH resolution variants — a resolution beat needs both.
            yield return new SynapseTestCase("Core_AuraResolutionHasPositiveAndNegative", () =>
            {
                foreach (var req in StorytellerPersonaDef.RequiredDifficulties)
                {
                    var entry = Aura.EntryFor(req, req == "Custom");
                    Assert.True(entry.prose.resolutionPositive.Any(l => !l.NullOrEmpty()),
                        $"{req} must have a positive resolution line");
                    Assert.True(entry.prose.resolutionNegative.Any(l => !l.NullOrEmpty()),
                        $"{req} must have a negative resolution line");
                }
                return "both variants everywhere";
            });

            // Callbacks reference real world-history entries, never invented ones.
            yield return new SynapseTestCase("Core_AuraCallbackReferencesRealHistory", () =>
            {
                var real = new WorldHistoryEntry { kind = "SolarFlare", region = "the northern basin", origin = "" };
                string payload = SynapsePersonaEngine.ComposeCallbackPayload(real);
                Assert.True(!payload.NullOrEmpty(), "a real entry yields a callback payload");
                Assert.Contains(payload, "SolarFlare", "callback must name the real entry's kind");
                Assert.Contains(payload, "the northern basin", "callback must name the real entry's region");
                Assert.Contains(payload.ToLowerInvariant(), "do not invent", "callback must forbid inventing events");
                Assert.True(SynapsePersonaEngine.ComposeCallbackPayload(null) == null,
                    "no entry → no callback (she never invents one)");
                return "callbacks are grounded";
            });

            // A second, third-party persona def loads alongside Aura and resolves by name.
            yield return new SynapseTestCase("Core_SecondPersonaDefLoadsAlongside", () =>
            {
                var second = MinimalPersona("RimSynapse_TestPersonaSecond");
                // Register the modder persona alongside Aura (DefDatabase has no public Remove; a
                // test-only def left registered is harmless and the Add is idempotent by name).
                if (DefDatabase<StorytellerPersonaDef>.GetNamedSilentFail(second.defName) == null)
                    DefDatabase<StorytellerPersonaDef>.Add(second);

                Assert.True(SynapsePersonaEngine.ResolvePersonaByName("Aura") == Aura, "Aura resolves");
                Assert.Equal(second.defName,
                    SynapsePersonaEngine.ResolvePersonaByName(second.defName)?.defName,
                    "a third-party persona resolves by name");
                Assert.True(SynapsePersonaEngine.ResolvePersonaByName(null) == Aura, "no name → Aura");
                Assert.True(SynapsePersonaEngine.ResolvePersonaByName("NoSuchPersona") == Aura,
                    "an unknown name falls back to Aura, never blank");
                Assert.False(second.fourthWall, "a modder persona may be diegetic (fourthWall false)");
                return "second persona loads";
            });

            // Custom-shift: a disabled adversary is detected as an "off" shift, and Aura has the line.
            yield return new SynapseTestCase("Core_CustomShiftDetectsDisabledThreat", () =>
            {
                var shifts = CustomDifficultyShift.Classify(new Dictionary<string, float> { { "insectoids", 0f } });
                Assert.Equal(1, shifts.Count, "one shift detected");
                Assert.Equal("insectoids", shifts[0].slider, "the insectoid slider");
                Assert.Equal("off", shifts[0].dir, "detected as off");
                var custom = Aura.EntryFor("Custom", true);
                string ctx = CustomDifficultyShift.BuildShiftContext(custom, shifts);
                Assert.True(!ctx.NullOrEmpty(), "the Custom entry supplies an insectoid quip");
                Assert.Contains(ctx.ToLowerInvariant(), "insectoids", "the insectoid case works end to end");
                // allowBigThreats off is likewise caught.
                var bt = CustomDifficultyShift.Classify(new Dictionary<string, float> { { "allowBigThreats", 0f } });
                Assert.Equal("off", bt.Single().dir, "big threats disabled is an off shift");
                return "disabled threat detected";
            });

            // Custom-shift: direction classification (low vs high) on a scale slider.
            yield return new SynapseTestCase("Core_CustomShiftClassifiesDirection", () =>
            {
                var high = CustomDifficultyShift.Classify(new Dictionary<string, float> { { "threatScale", 3.0f } });
                Assert.Equal("high", high.Single().dir, "3x threats is high");
                var low = CustomDifficultyShift.Classify(new Dictionary<string, float> { { "threatScale", 0.2f } });
                Assert.Equal("low", low.Single().dir, "0.2x threats is low");
                return "direction classified";
            });

            // Custom-shift: a near-baseline config stays quiet — no quip noise.
            yield return new SynapseTestCase("Core_NoQuipOnBaselineSliders", () =>
            {
                var baseline = new Dictionary<string, float>
                {
                    { "threatScale", 1f }, { "allowBigThreats", 1f }, { "insectoids", 1f },
                    { "mechanoids", 1f }, { "adaptation", 1f }, { "moodPenalties", 0f }, { "diseases", 1f },
                };
                var shifts = CustomDifficultyShift.Classify(baseline);
                Assert.Equal(0, shifts.Count, "a default config produces no shift");
                var custom = Aura.EntryFor("Custom", true);
                Assert.True(CustomDifficultyShift.BuildShiftContext(custom, shifts) == null,
                    "no shift → no quip context");
                return "baseline is quiet";
            });
        }

        private static StorytellerPersonaDef MinimalPersona(string defName)
        {
            var def = new StorytellerPersonaDef { defName = defName, label = "Test Persona", fourthWall = false };
            foreach (var req in StorytellerPersonaDef.RequiredDifficulties)
            {
                def.difficulties.Add(new PersonaDifficultyEntry
                {
                    difficulty = req,
                    personaPrompt = "test",
                    prose = new PersonaBeats
                    {
                        kickoff = new List<string> { "k" },
                        resolutionPositive = new List<string> { "rp" },
                        resolutionNegative = new List<string> { "rn" },
                        callback = new List<string> { "cb" },
                        idle = new List<string> { "id" },
                    }
                });
            }
            return def;
        }
    }
}
