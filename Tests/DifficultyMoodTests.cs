using System;

// Tier-1 sandbox for the difficulty mood mandate (Core #66): the pure mapping from the
// threat-scale slider to the storyteller's stance, compiled game-free under mono. The
// live gating and slider reads need the game and are covered by the Tier-2 cases
// Core_DifficultyContextInjected and Core_CustomDifficultySliderRead.
public static class DifficultyMoodProgram
{
    static int fails = 0;

    static void Section(string title) => Console.WriteLine($"\n== {title}");

    static void Check(string name, bool pass)
    {
        if (!pass) fails++;
        Console.WriteLine($"  {(pass ? "PASS" : "FAIL")} {name}");
    }

    static int Main()
    {
        Section("Stance maps from the slider, not a preset name");
        {
            Check("peaceful scale is bored", RimSynapse.SynapseDifficultyMood.Stance(0.1f, true) == "bored");
            Check("big threats off is bored regardless of scale", RimSynapse.SynapseDifficultyMood.Stance(1.5f, false) == "bored");
            Check("low scale is indulgent", RimSynapse.SynapseDifficultyMood.Stance(0.4f, true) == "indulgent");
            Check("standard scale is engaged", RimSynapse.SynapseDifficultyMood.Stance(1.0f, true) == "engaged");
            Check("raised scale is menacing", RimSynapse.SynapseDifficultyMood.Stance(1.6f, true) == "menacing");
            Check("cranked scale is gleeful", RimSynapse.SynapseDifficultyMood.Stance(3.0f, true) == "gleeful");
        }

        Section("Boundaries land on the softer side");
        {
            Check("0.15 still bored", RimSynapse.SynapseDifficultyMood.Stance(0.15f, true) == "bored");
            Check("0.6 still indulgent", RimSynapse.SynapseDifficultyMood.Stance(0.6f, true) == "indulgent");
            Check("1.1 still engaged", RimSynapse.SynapseDifficultyMood.Stance(1.1f, true) == "engaged");
            Check("2.0 still menacing", RimSynapse.SynapseDifficultyMood.Stance(2.0f, true) == "menacing");
        }

        Section("The mandate carries the stance and reads as direction, not data");
        {
            Check("bored mandate mentions yawning", RimSynapse.SynapseDifficultyMood.MoodMandate(0.1f, true).Contains("Yawn"));
            Check("gleeful mandate quotes 'you asked for this'", RimSynapse.SynapseDifficultyMood.MoodMandate(3.0f, true).Contains("You asked for this"));
            Check("every stance yields a non-empty mandate",
                RimSynapse.SynapseDifficultyMood.MoodMandate(0.1f, true).Length > 20
                && RimSynapse.SynapseDifficultyMood.MoodMandate(0.4f, true).Length > 20
                && RimSynapse.SynapseDifficultyMood.MoodMandate(1.0f, true).Length > 20
                && RimSynapse.SynapseDifficultyMood.MoodMandate(1.6f, true).Length > 20
                && RimSynapse.SynapseDifficultyMood.MoodMandate(3.0f, true).Length > 20);
            Check("mandate starts with its stance word",
                RimSynapse.SynapseDifficultyMood.MoodMandate(1.0f, true).StartsWith(RimSynapse.SynapseDifficultyMood.Stance(1.0f, true)));
        }

        Console.WriteLine(fails == 0 ? "\nDifficultyMood: ALL PASSED" : $"\nDifficultyMood: {fails} FAILED");
        return fails;
    }
}
