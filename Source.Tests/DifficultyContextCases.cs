using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Covers difficulty + personality context injection (Core #66).
    ///
    /// The difficulty must reach the agents as both a hard points ceiling and a mood
    /// mandate derived from the custom threat-scale slider (not the preset name), the
    /// personality profile must ride the same block, and under a non-RimSynapse
    /// storyteller every builder must return empty — mechanisms dormant.
    /// </summary>
    [SynapseTestSet]
    public static class DifficultyContextCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_DifficultyContextInjected", () =>
            {
                // The formatting seam: everything the agents receive is in the block.
                string block = SynapseStorytellerContext.FormatDifficultyBlock(
                    "Strive to survive", isCustom: false, threatScale: 1.0f, bigThreatsAllowed: true, ceiling: 780f);
                Assert.Contains(block, "780", "the hard points ceiling must be in the block");
                Assert.Contains(block, "Threat scale slider: 1", "the slider value must be in the block");
                Assert.Contains(block, "mood mandate", "the mood mandate must be in the block");
                Assert.Contains(block, "engaged", "scale 1.0 must read as the engaged stance");

                string persona = SynapseStorytellerContext.FormatPersonalityBlock(
                    "Aura", "sassy, dramatic, and snarky", "- Treats the colony as her serial drama.");
                Assert.Contains(persona, "Aura", "the character name must be in the personality block");
                Assert.Contains(persona, "sassy", "the speaking style must be in the personality block");
                Assert.Contains(persona, "serial drama", "the personality profile prose must be in the block");

                // The gate: live builders must agree with the live storyteller. Under the
                // quicktest's vanilla storyteller they are empty (dormant); under a
                // RimSynapse one they must be non-empty and carry the mandate.
                bool active = SynapseStorytellerContext.IsRimSynapseStorytellerActive;
                string liveDifficulty = SynapseStorytellerContext.BuildDifficultyContext(Find.CurrentMap);
                string livePersona = SynapseStorytellerContext.BuildPersonalityContext();
                if (active)
                {
                    Assert.True(liveDifficulty.Contains("mood mandate"), "active gate must inject the difficulty block");
                    Assert.True(livePersona.Length > 0, "active gate must inject the personality block");
                }
                else
                {
                    Assert.True(liveDifficulty == string.Empty, "dormant gate must inject nothing (difficulty)");
                    Assert.True(livePersona == string.Empty, "dormant gate must inject nothing (personality)");
                    Assert.True(SynapseStorytellerContext.BuildAgentContext(Find.CurrentMap) == string.Empty,
                        "dormant gate must inject nothing (combined)");
                }

                return $"blocks carry ceiling+mandate+persona; gate={(active ? "active, injected" : "dormant, empty")}";
            });

            yield return new SynapseTestCase("Core_StorytellerContextActiveGate", () =>
            {
                // Prove the ACTIVE branch of the gate: swap the live storyteller to one that
                // carries our comp (Aura), assert injection happens, then restore. Self-skips
                // when no RimSynapse storyteller def is loaded.
                var rimSynapseDef = DefDatabase<StorytellerDef>.AllDefsListForReading
                    .FirstOrDefault(d => d.comps != null && d.comps.Any(c => c is RimSynapse.Comps.StorytellerCompProperties_Storyteller));
                if (rimSynapseDef == null)
                    return "SKIP: no RimSynapse storyteller def loaded";

                var storyteller = Find.Storyteller;
                var originalDef = storyteller.def;
                try
                {
                    storyteller.def = rimSynapseDef;
                    storyteller.Notify_DefChanged();

                    Assert.True(SynapseStorytellerContext.IsRimSynapseStorytellerActive,
                        "the gate must open under a RimSynapse storyteller");
                    string context = SynapseStorytellerContext.BuildAgentContext(Find.CurrentMap);
                    Assert.Contains(context, "mood mandate", "the active gate must inject the difficulty block");
                    Assert.Contains(context, "Personality Profile", "the active gate must inject the personality block");
                    Assert.True(SynapseStorytellerContext.ThreatPointsCeiling(Find.CurrentMap) > 0f,
                        "the ceiling must be a positive points figure");
                }
                finally
                {
                    storyteller.def = originalDef;
                    storyteller.Notify_DefChanged();
                }

                Assert.True(!SynapseStorytellerContext.IsRimSynapseStorytellerActive
                    || originalDef.comps.Any(c => c is RimSynapse.Comps.StorytellerCompProperties_Storyteller),
                    "the gate must close again after restore");

                return $"gate opened under '{rimSynapseDef.defName}', injected, and closed on restore";
            });

            yield return new SynapseTestCase("Core_CustomDifficultySliderRead", () =>
            {
                var difficulty = Find.Storyteller?.difficulty;
                Assert.NotNull(difficulty, "no Difficulty settings on the active storyteller");

                float saved = difficulty.threatScale;
                try
                {
                    difficulty.threatScale = 1.77f;
                    float read = SynapseStorytellerContext.ReadThreatScale();
                    Assert.True(Math.Abs(read - 1.77f) < 0.001f,
                        $"ReadThreatScale must read the live slider, got {read}");

                    // The block renders the slider it read — same format call, so culture-safe.
                    string block = SynapseStorytellerContext.FormatDifficultyBlock(
                        "whatever", isCustom: true, threatScale: read, bigThreatsAllowed: true, ceiling: 500f);
                    Assert.Contains(block, read.ToString("0.00"), "the block must carry the slider value");
                    Assert.Contains(block, "custom settings", "a custom preset must tell the agent to trust the sliders");
                    Assert.Contains(block, "menacing", "scale 1.77 must read as the menacing stance");
                }
                finally
                {
                    difficulty.threatScale = saved;
                }

                return "threat-scale slider read live and rendered into the block";
            });
        }
    }
}
