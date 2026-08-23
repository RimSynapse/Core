using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using RimSynapse.Comps;
using RimSynapse.Models;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Core's half of the skill-driven trait engine (Psychology #60): the trait-axis adjacency
    /// model, the candidate-id encoding, and the mood-baseline reinforcement axis. The
    /// skill→trait mapping table itself is Psychology's and is covered in that repo's suite.
    /// Deterministic — the axis math is pure.
    /// </summary>
    [SynapseTestSet]
    public static class SkillAxisCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            // The reachable-degree / adjacency math over a real spectrum def (NaturalMood).
            yield return new SynapseTestCase("Core_TraitAxis_SpectrumAdjacencyWalk", () =>
            {
                var mood = DefDatabase<TraitDef>.GetNamedSilentFail("NaturalMood");
                Assert.True(mood != null, "NaturalMood trait def exists");

                var reach = TraitAxis.ReachableDegrees(mood);
                Assert.True(reach.Contains(0), "reachable degrees always include the neutral 0");
                Assert.True(reach.Count >= 3, "a spectrum has several reachable degrees");

                int? plus = TraitAxis.AdjacentPlus(mood, 0);
                int? minus = TraitAxis.AdjacentMinus(mood, 0);
                Assert.True(plus.HasValue && plus.Value > 0, "from neutral, the + move is a positive degree (Optimist-side)");
                Assert.True(minus.HasValue && minus.Value < 0, "from neutral, the - move is a negative degree (Pessimist-side)");

                // From the pessimist side, - goes deeper (Depressive) and + returns to neutral.
                int? deeper = TraitAxis.AdjacentMinus(mood, minus.Value);
                Assert.True(deeper.HasValue && deeper.Value < minus.Value, "a further - step goes to the deeper negative degree");
                int? back = TraitAxis.AdjacentPlus(mood, minus.Value);
                // Degrees are ints; compare against an int 0, not a float 0f — a boxed float
                // never Equals a boxed int, and both render "0", which masks the mismatch.
                Assert.Equal(0, back ?? -99, "a + step from the first negative degree returns to neutral");
                return $"reach=[{string.Join(",", reach)}] plus={plus} minus={minus}";
            });

            // Candidate-id encoding round-trips for both spectrum and single traits.
            yield return new SynapseTestCase("Core_TraitAxis_CandidateIdRoundTrip", () =>
            {
                string spec = TraitAxis.SpectrumCandidate("NaturalMood", -1);
                Assert.True(spec == "NaturalMood#-1", $"spectrum id encodes the degree (was {spec})");
                Assert.True(TraitAxis.AxisIdOf(spec) == "NaturalMood", "axis id extracts before the '#'");
                Assert.True(TraitAxis.TryParse(spec, out var ax, out int deg, out var single)
                    && ax == "NaturalMood" && deg == -1 && !single.HasValue, "spectrum id parses to axis+degree, no single flag");

                string add = TraitAxis.SingleCandidate("Bloodlust", true);
                string rem = TraitAxis.SingleCandidate("Bloodlust", false);
                Assert.True(add == "Bloodlust#+" && rem == "Bloodlust#-", $"single ids use +/- sentinels (was {add}, {rem})");
                Assert.True(TraitAxis.TryParse(add, out _, out _, out var s2) && s2 == true, "single '#+' parses as add");
                Assert.True(TraitAxis.TryParse(rem, out _, out _, out var s3) && s3 == false, "single '#-' parses as remove");
                return $"{spec}, {add}, {rem}";
            });

            // Reinforcement (the multidimensional 2nd axis): mood vs a rolling baseline, clamped [-1,1].
            yield return new SynapseTestCase("Core_MoodBaselineReinforcement", () =>
            {
                var comp = new SynapseCorePawnComp();
                float seed = comp.UpdateMoodBaselineAndGetReinforcement(0.5f);
                Assert.Equal(0f, seed, "the first day only seeds the baseline (no reinforcement yet)");

                float up = comp.UpdateMoodBaselineAndGetReinforcement(0.65f);
                Assert.True(up > 0f, "a happier-than-baseline day yields positive reinforcement");
                float down = comp.UpdateMoodBaselineAndGetReinforcement(0.20f);
                Assert.True(down < 0f, "a worse-than-baseline day yields negative reinforcement");

                var comp2 = new SynapseCorePawnComp();
                comp2.UpdateMoodBaselineAndGetReinforcement(0.5f);
                float clamped = comp2.UpdateMoodBaselineAndGetReinforcement(1.0f);
                Assert.True(clamped <= 1.0f && clamped >= 0.99f, $"reinforcement clamps at +1 (was {clamped})");
                return $"up={up:0.00} down={down:0.00} clamped={clamped:0.00}";
            });
        }
    }
}
