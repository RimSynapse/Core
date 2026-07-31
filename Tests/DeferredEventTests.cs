// Tier-1 sandbox for the deferred-event hold state machine (Core#59).
//
// The pipeline is deliberately game-free, so the whole of it — ordering, timeout, stage-failure
// containment, invalidation, cap, and the once-only release that the integration's re-entry
// avoidance rests on — is exercised here with a driven clock and captured callbacks, no RimWorld,
// Harmony or Unity in sight. The Harmony prefixes and real Letter/Message re-issue are in
// SynapseDeferredEvents and are not covered this way (they need the game).
using System;
using System.Collections.Generic;
using RimSynapse;

public static class DeferredEventProgram
{
    static int fails = 0;
    static double clock;
    static readonly List<string> log = new List<string>();

    static SynapseDeferredEventPipeline New(double timeout, int cap)
    {
        log.Clear();
        return new SynapseDeferredEventPipeline(() => clock, timeout, cap, s => log.Add(s));
    }

    public static int Main()
    {
        Section("Ordering: lower order first, each waits for the prior");
        {
            clock = 0;
            var p = New(10, 8);
            var seq = new List<string>();
            Action pend = null;
            p.RegisterClassification("letter", true);
            p.RegisterStage("letter", 200, (pl, done) => { seq.Add("b"); pend = done; });
            p.RegisterStage("letter", 100, (pl, done) => { seq.Add("a"); done(); });
            bool released = false;
            bool held = p.TryHold("letter", "L", () => released = true, () => true);
            Check("held", held);
            Check("ran 100 before 200", seq.Count == 2 && seq[0] == "a" && seq[1] == "b");
            Check("not released mid-chain", !released);
            pend();
            Check("released after last stage", released);
            Check("no active holds after release", p.ActiveHoldCount == 0);
        }

        Section("Timeout: a stage that never calls back still releases at the deadline");
        {
            clock = 0;
            var p = New(5, 8);
            p.RegisterClassification("letter", true);
            p.RegisterStage("letter", 100, (pl, done) => { });   // never completes
            bool released = false;
            p.TryHold("letter", "L", () => released = true, () => true);
            clock = 4.9; p.Tick();
            Check("not released before deadline", !released);
            clock = 5.0; p.Tick();
            Check("released at deadline", released);
        }

        Section("Once-only: a stage completing after timeout must not double-fire");
        {
            clock = 0;
            var p = New(5, 8);
            Action late = null;
            int count = 0;
            p.RegisterClassification("letter", true);
            p.RegisterStage("letter", 100, (pl, done) => late = done);
            p.TryHold("letter", "L", () => count++, () => true);
            clock = 5.0; p.Tick();
            late();
            Check("released exactly once", count == 1);
        }

        Section("Stage throws: contained, hold still releases");
        {
            clock = 0;
            var p = New(10, 8);
            bool released = false;
            p.RegisterClassification("letter", true);
            p.RegisterStage("letter", 100, (pl, done) => { throw new InvalidOperationException("boom"); });
            p.RegisterStage("letter", 200, (pl, done) => done());
            p.TryHold("letter", "L", () => released = true, () => true);
            Check("throwing stage did not wedge", released);
            Check("throw logged", log.Exists(s => s.Contains("threw") && s.Contains("boom")));
        }

        Section("Invalidation: invalid state discards rather than re-issuing");
        {
            clock = 0;
            var p = New(10, 8);
            bool released = false;
            Action done1 = null;
            p.RegisterClassification("letter", true);
            p.RegisterStage("letter", 100, (pl, done) => done1 = done);
            p.TryHold("letter", "L", () => released = true, () => false);
            done1();
            Check("invalid hold not re-issued", !released);
            Check("discard logged", log.Exists(s => s.Contains("no longer valid")));
        }

        Section("Cap: beyond max concurrent holds, fire unheld");
        {
            clock = 0;
            var p = New(100, 2);
            p.RegisterClassification("letter", true);
            p.RegisterStage("letter", 100, (pl, done) => { });
            Check("hold 1 held", p.TryHold("letter", "L1", () => { }, () => true));
            Check("hold 2 held", p.TryHold("letter", "L2", () => { }, () => true));
            Check("hold 3 refused at cap", !p.TryHold("letter", "L3", () => { }, () => true));
            Check("cap logged", log.Exists(s => s.Contains("at cap 2")));
        }

        Section("Fail-safe defaults: only an explicitly-holdable class with stages is ever held");
        {
            clock = 0;
            var p = New(10, 8);
            Check("unclassified not held", !p.TryHold("raid", "R", () => { }, () => true));
            p.RegisterClassification("letter", true);
            Check("holdable but no stages not held", !p.TryHold("letter", "L", () => { }, () => true));
            p.RegisterClassification("raid", false);
            p.RegisterStage("raid", 100, (pl, done) => { });
            Check("must-not-delay not held even with a stage", !p.TryHold("raid", "R", () => { }, () => true));
        }

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "ALL SUITES PASSED" : fails + " ASSERTION(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    static void Section(string name) { Console.WriteLine(); Console.WriteLine("== " + name + " =="); }
    static void Check(string label, bool ok) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + label); if (!ok) fails++; }
}
