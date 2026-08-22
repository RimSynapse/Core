using System;
using RimSynapse;

// Tier-1 sandbox for the regionalizable-incident lifecycle hook (Core #64), compiled game-free
// under mono: classification, the resolution dedup (anti-oscillation), fan-out to reflection-style
// subscribers, and subscriber-exception containment. The live emit points (IncidentWorker.TryExecute
// start, GameCondition.End resolution) are Tier-2 / in-game.
public static class IncidentLifecycleProgram
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
        Section("Regionalizable classification (Tier A–D vs colony-local)");
        {
            Check("solar flare is regionalizable", SynapseIncidentLifecycle.IsRegionalizable("SolarFlare", null));
            Check("toxic fallout is regionalizable", SynapseIncidentLifecycle.IsRegionalizable("ToxicFallout", null));
            Check("psychic drone is regionalizable", SynapseIncidentLifecycle.IsRegionalizable("PsychicDrone", null));
            Check("disease by category is regionalizable", SynapseIncidentLifecycle.IsRegionalizable("Flu", "DiseaseHuman"));
            Check("animal disease by category is regionalizable", SynapseIncidentLifecycle.IsRegionalizable("Muffalo", "DiseaseAnimal"));
            Check("raid is NOT regionalizable", !SynapseIncidentLifecycle.IsRegionalizable("RaidEnemy", "ThreatBig"));
            Check("infestation is NOT regionalizable", !SynapseIncidentLifecycle.IsRegionalizable("Infestation", "ThreatBig"));
            Check("null/null is not regionalizable", !SynapseIncidentLifecycle.IsRegionalizable(null, null));
        }

        Section("Start fan-out reaches every subscriber");
        {
            int a = 0, b = 0;
            string gotKind = null; float gotMag = 0f;
            Action<string, string, float, string, int> h1 = (k, r, m, o, l) => { a++; gotKind = k; gotMag = m; };
            Action<string, string, float, string, int> h2 = (k, r, m, o, l) => { b++; };
            SynapseIncidentLifecycle.OnIncidentStarted += h1;
            SynapseIncidentLifecycle.OnIncidentStarted += h2;
            try
            {
                SynapseIncidentLifecycle.BroadcastStarted("SolarFlare", "BorealForest", 350f, "", 0);
                Check("both subscribers fired", a == 1 && b == 1);
                Check("payload delivered", gotKind == "SolarFlare" && Math.Abs(gotMag - 350f) < 0.01f);
            }
            finally { SynapseIncidentLifecycle.OnIncidentStarted -= h1; SynapseIncidentLifecycle.OnIncidentStarted -= h2; }
        }

        Section("Resolution dedups by key (anti-oscillation)");
        {
            SynapseIncidentLifecycle.ResetResolvedForTest();
            int fired = 0;
            Action<string, string, string> h = (k, r, o) => fired++;
            SynapseIncidentLifecycle.OnIncidentResolved += h;
            try
            {
                bool first = SynapseIncidentLifecycle.BroadcastResolved("SolarFlare", "BorealForest", "ended", "SolarFlare:100:BorealForest");
                bool second = SynapseIncidentLifecycle.BroadcastResolved("SolarFlare", "BorealForest", "ended", "SolarFlare:100:BorealForest");
                bool other = SynapseIncidentLifecycle.BroadcastResolved("ToxicFallout", "BorealForest", "ended", "ToxicFallout:100:BorealForest");
                Check("first resolution accepted", first);
                Check("duplicate key refused", !second);
                Check("distinct key accepted", other);
                Check("subscriber fired exactly twice (dup suppressed)", fired == 2);
            }
            finally { SynapseIncidentLifecycle.OnIncidentResolved -= h; }
        }

        Section("Empty dedup key always emits; zero subscribers is safe");
        {
            SynapseIncidentLifecycle.ResetResolvedForTest();
            Check("no subscribers, start does not throw", DoesNotThrow(() =>
                SynapseIncidentLifecycle.BroadcastStarted("Eclipse", "x", 0f, "", 0)));
            Check("empty key emits and returns true", SynapseIncidentLifecycle.BroadcastResolved("Eclipse", "x", "ended", ""));
            Check("empty key again still emits (no dedup)", SynapseIncidentLifecycle.BroadcastResolved("Eclipse", "x", "ended", ""));
        }

        Section("A throwing subscriber is contained, others still fire");
        {
            SynapseIncidentLifecycle.ResetResolvedForTest();
            int good = 0;
            Action<string, string, float, string, int> bad = (k, r, m, o, l) => throw new InvalidOperationException("boom");
            Action<string, string, float, string, int> ok = (k, r, m, o, l) => good++;
            SynapseIncidentLifecycle.OnIncidentStarted += bad;
            SynapseIncidentLifecycle.OnIncidentStarted += ok;
            try
            {
                SynapseIncidentLifecycle.BroadcastStarted("SolarFlare", "x", 1f, "", 0);
                Check("good subscriber still fired despite a throwing one", good == 1);
                Check("subscriber error recorded", SynapseIncidentLifecycle.LastSubscriberError != null
                    && SynapseIncidentLifecycle.LastSubscriberError.Contains("boom"));
            }
            finally { SynapseIncidentLifecycle.OnIncidentStarted -= bad; SynapseIncidentLifecycle.OnIncidentStarted -= ok; }
        }

        Console.WriteLine(fails == 0 ? "\nIncidentLifecycle: ALL PASSED" : $"\nIncidentLifecycle: {fails} FAILED");
        return fails;
    }

    static bool DoesNotThrow(Action a) { try { a(); return true; } catch { return false; } }
}
