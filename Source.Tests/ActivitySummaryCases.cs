using System.Collections.Generic;
using RimWorld;
using Verse;
using RimSynapse.Comps;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Stage 0 of the 0.7.1 memory/eval redesign (Core #72): a pawn's activity summary must
    /// distinguish what was acted on. Repeatedly bashing an object must surface as one deduped,
    /// object-targeted, explicitly non-lethal line — not as overwhelming violent signal — so the
    /// psychology evaluator cannot read it as bloodlust (the canonical pod-car bug).
    /// </summary>
    [SynapseTestSet]
    public static class ActivitySummaryCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            // The canonical case: ~100 short combat intervals against an object over the last day.
            yield return new SynapseTestCase("Core_ObjectBashingReadsAsNonLethal", () =>
            {
                var comp = new SynapseCorePawnComp { lastJobStartedTick = -1 }; // no ongoing job
                int now = Find.TickManager.TicksGame;
                for (int i = 0; i < 100; i++)
                {
                    comp.recentJobs.Add(new JobInterval("AttackStatic", now - 5000 + (i * 10), 40, "object"));
                }

                string summary = comp.GetRecentJobsSummary();
                Assert.Contains(summary, "non-lethal", "object-bashing must be labelled non-lethal");
                Assert.Contains(summary, "object", "object-bashing must name the object target");
                // One deduped group — all 100 identical intervals collapse into a single activity
                // line at 100%. Count groups by their trailing "%)" rather than commas, since the
                // line's own text contains a comma.
                int groups = summary.Split(new[] { "%)" }, System.StringSplitOptions.None).Length - 1;
                Assert.Equal(1, groups, "identical object attacks must dedup to one activity line");
                return $"summary='{summary}'";
            });

            // A living target must NOT be labelled non-lethal, so real violence still reads as real.
            yield return new SynapseTestCase("Core_LivingTargetIsNotNonLethal", () =>
            {
                var comp = new SynapseCorePawnComp { lastJobStartedTick = -1 };
                int now = Find.TickManager.TicksGame;
                comp.recentJobs.Add(new JobInterval("AttackMelee", now - 5000, 4000, "humanlike"));
                comp.recentJobs.Add(new JobInterval("AttackMelee", now - 1000, 4000, "animal"));

                string summary = comp.GetRecentJobsSummary();
                Assert.DoesNotContain(summary, "non-lethal", "attacking the living is not non-lethal");
                Assert.Contains(summary, "person", "humanlike combat must name a person target");
                Assert.Contains(summary, "animal", "animal combat must name an animal target");
                return $"summary='{summary}'";
            });

            // A labelless JobDef must never leak its raw defName into the summary. GotoWander has no
            // <label> but a clean <reportString> ("wandering."), so it must humanize to that phrase —
            // NOT the raw "gotowander" that was surfacing in ambient dialogue.
            yield return new SynapseTestCase("Core_LabellessJobHumanizesNotRawDefName", () =>
            {
                var comp = new SynapseCorePawnComp { lastJobStartedTick = -1 };
                int now = Find.TickManager.TicksGame;
                comp.recentJobs.Add(new JobInterval("GotoWander", now - 5000, 4000));

                string summary = comp.GetRecentJobsSummary();
                Assert.DoesNotContain(summary.ToLowerInvariant(), "gotowander",
                    "a labelless job must not leak its raw defName");
                Assert.Contains(summary, "wandering",
                    "GotoWander must humanize via its reportString to 'wandering'");
                return $"summary='{summary}'";
            });

            // The classifier itself: a non-pawn thing is an object; an invalid target is null.
            yield return new SynapseTestCase("Core_ClassifyTargetKind", () =>
            {
                var steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                string objKind = SynapseCorePawnComp.ClassifyTargetKind(steel, null);
                Assert.Equal("object", objKind, "a steel stack must classify as an object");

                string invalid = SynapseCorePawnComp.ClassifyTargetKind(LocalTargetInfo.Invalid, null);
                Assert.True(invalid == null, "an invalid target must classify as null");

                Assert.True(SynapseCorePawnComp.IsCombatJob("AttackStatic"), "AttackStatic is a combat job");
                Assert.False(SynapseCorePawnComp.IsCombatJob("Sowing"), "Sowing is not a combat job");
                return $"steel={objKind}, invalid=null";
            });
        }
    }
}
