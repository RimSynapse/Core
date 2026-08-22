using System;
using RimSynapse;

// Tier-1 sandbox for the world-history store's eviction policy (Core #65), compiled game-free under
// mono. The store must stay bounded and must preserve open threads over settled, resolved history.
// Live persistence/query/context-surfacing is Tier-2.
public static class WorldHistoryCurationProgram
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
        Section("Within cap => nothing evicted");
        {
            var resolved = new[] { true, false, true };
            var ticks = new[] { 10, 20, 30 };
            Check("under cap returns -1", WorldHistoryCuration.SelectEvictionIndex(resolved, ticks, 3, 5) == -1);
            Check("exactly at cap returns -1", WorldHistoryCuration.SelectEvictionIndex(resolved, ticks, 3, 3) == -1);
        }

        Section("Over cap => evict the OLDEST RESOLVED entry (keep open threads)");
        {
            // index: 0 open@5, 1 resolved@10, 2 resolved@8, 3 open@2
            var resolved = new[] { false, true, true, false };
            var ticks = new[] { 5, 10, 8, 2 };
            int victim = WorldHistoryCuration.SelectEvictionIndex(resolved, ticks, 4, 3);
            Check("evicts oldest RESOLVED (index 2 @8), not the older open @2", victim == 2);
        }

        Section("All open + over cap => evict oldest open (hard bound)");
        {
            var resolved = new[] { false, false, false };
            var ticks = new[] { 30, 10, 20 };
            int victim = WorldHistoryCuration.SelectEvictionIndex(resolved, ticks, 3, 2);
            Check("with nothing resolved, evict oldest overall (index 1 @10)", victim == 1);
        }

        Section("Guards");
        {
            Check("null arrays => -1", WorldHistoryCuration.SelectEvictionIndex(null, null, 5, 2) == -1);
            Check("zero count => -1", WorldHistoryCuration.SelectEvictionIndex(new bool[0], new int[0], 0, 2) == -1);
        }

        Console.WriteLine(fails == 0 ? "\nWorldHistoryCuration: ALL PASSED" : $"\nWorldHistoryCuration: {fails} FAILED");
        return fails;
    }
}
