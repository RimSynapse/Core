using System;

// Tier-1 sandbox for the storyteller decision gate (Core #67): the pure "is a beat due?" and
// "may a decision begin?" logic, compiled game-free under mono. The live cadence source
// (IncidentCycleUtility), the async selection, and the vanilla fallback need the game and are
// covered by the Tier-2 cases (Core_StorytellerSelectsEligibleOnly, Core_StorytellerFallsBackToVanilla,
// Core_StorytellerDecisionSaveRoundTrip).
public static class StorytellerDecisionGateProgram
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
        const int stale = 2500;

        Section("A beat is due only on a positive deterministic count");
        {
            Check("count 0 -> no consult", RimSynapse.Comps.StorytellerDecisionGate.ShouldConsult(0) == false);
            Check("negative -> no consult", RimSynapse.Comps.StorytellerDecisionGate.ShouldConsult(-1) == false);
            Check("count 1 -> consult", RimSynapse.Comps.StorytellerDecisionGate.ShouldConsult(1));
            Check("count 3 -> consult", RimSynapse.Comps.StorytellerDecisionGate.ShouldConsult(3));
        }

        Section("Single decision in flight at a time");
        {
            Check("nothing in flight -> may begin",
                RimSynapse.Comps.StorytellerDecisionGate.CanBegin(inFlight: false, startTick: 0, now: 100, staleTicks: stale));
            Check("fresh in-flight blocks a second decision",
                RimSynapse.Comps.StorytellerDecisionGate.CanBegin(inFlight: true, startTick: 1000, now: 1500, staleTicks: stale) == false);
            Check("in-flight one tick short of stale still blocks",
                RimSynapse.Comps.StorytellerDecisionGate.CanBegin(inFlight: true, startTick: 1000, now: 1000 + stale - 1, staleTicks: stale) == false);
        }

        Section("A stale in-flight flag (interrupted session) clears on the next beat");
        {
            Check("exactly stale -> may begin",
                RimSynapse.Comps.StorytellerDecisionGate.CanBegin(inFlight: true, startTick: 1000, now: 1000 + stale, staleTicks: stale));
            Check("well past stale -> may begin",
                RimSynapse.Comps.StorytellerDecisionGate.CanBegin(inFlight: true, startTick: 1000, now: 1000 + stale * 10, staleTicks: stale));
            Check("IsStale agrees with CanBegin at the boundary",
                RimSynapse.Comps.StorytellerDecisionGate.IsStale(1000, 1000 + stale, stale));
            Check("not-yet-stale is not stale",
                RimSynapse.Comps.StorytellerDecisionGate.IsStale(1000, 1000 + stale - 1, stale) == false);
        }

        Console.WriteLine(fails == 0 ? "\nStorytellerDecisionGate: ALL PASSED" : $"\nStorytellerDecisionGate: {fails} FAILED");
        return fails;
    }
}
