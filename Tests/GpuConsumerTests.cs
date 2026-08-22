using System;
using RimSynapse;

// Tier-1 sandbox for the GpuStats in-process consumers channel (Core #104): the pure
// upsert-by-modId + resident→vram logic, compiled game-free under mono. GpuStats has no Verse
// dependencies, so the whole channel is testable here; the live registration by Local TTS and the
// NVIDIA Tool's breakdown read are exercised in-game / by their own repos.
public static class GpuConsumerProgram
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
        Section("Upsert inserts a new consumer, keyed by modId");
        {
            var g = new GpuStats();
            g.UpsertConsumer("rimsynapse.localtts", "Local TTS (Kokoro)", 400f, true);
            var snap = g.ConsumersSnapshot();
            Check("one consumer registered", snap.Count == 1);
            Check("modId stored", snap[0].modId == "rimsynapse.localtts");
            Check("label stored", snap[0].label == "Local TTS (Kokoro)");
            Check("resident vram kept", Math.Abs(snap[0].vramMb - 400f) < 0.01f);
            Check("resident flag true", snap[0].resident);
        }

        Section("Upsert updates in place by modId (no duplicate)");
        {
            var g = new GpuStats();
            g.UpsertConsumer("rimsynapse.localtts", "Local TTS", 400f, true);
            g.UpsertConsumer("rimsynapse.localtts", "Local TTS (Kokoro)", 512f, true);
            var snap = g.ConsumersSnapshot();
            Check("still one consumer (updated, not appended)", snap.Count == 1);
            Check("label updated", snap[0].label == "Local TTS (Kokoro)");
            Check("vram updated", Math.Abs(snap[0].vramMb - 512f) < 0.01f);
        }

        Section("Non-resident reports zero VRAM (model on CPU)");
        {
            var g = new GpuStats();
            g.UpsertConsumer("rimsynapse.localtts", "Local TTS (Kokoro)", 512f, resident: false);
            var snap = g.ConsumersSnapshot();
            Check("registered but zero vram", Math.Abs(snap[0].vramMb) < 0.01f);
            Check("resident flag false", !snap[0].resident);
        }

        Section("Distinct modIds coexist; snapshot is a copy");
        {
            var g = new GpuStats();
            g.UpsertConsumer("rimsynapse.localtts", "Local TTS", 400f, true);
            g.UpsertConsumer("some.other.mod", "Other Model", 128f, true);
            var snap = g.ConsumersSnapshot();
            Check("two distinct consumers", snap.Count == 2);
            // Mutating the snapshot must not affect the live list.
            snap.Clear();
            Check("snapshot is a defensive copy", g.ConsumersSnapshot().Count == 2);
        }

        Section("Empty/null modId is ignored (no crash, no phantom row)");
        {
            var g = new GpuStats();
            g.UpsertConsumer(null, "x", 10f, true);
            g.UpsertConsumer("", "y", 10f, true);
            Check("no consumers registered for null/empty modId", g.ConsumersSnapshot().Count == 0);
        }

        Section("RemoveConsumer drops a row entirely; missing/empty is a no-op");
        {
            var g = new GpuStats();
            g.UpsertConsumer("a", "A", 10f, true);
            g.UpsertConsumer("b", "B", 20f, true);
            g.RemoveConsumer("a");
            var snap = g.ConsumersSnapshot();
            Check("removed the named consumer", snap.Count == 1 && snap[0].modId == "b");
            g.RemoveConsumer("nope");   // absent
            g.RemoveConsumer(null);     // null
            Check("removing absent/null is a no-op", g.ConsumersSnapshot().Count == 1);
        }

        Console.WriteLine(fails == 0 ? "\nGpuConsumer: ALL PASSED" : $"\nGpuConsumer: {fails} FAILED");
        return fails;
    }
}
