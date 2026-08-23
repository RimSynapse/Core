using System.Collections.Generic;
using RimSynapse.Comps;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Core's half of the 0.7.1 trait engine (Core #79): multi-day pressure accumulation with
    /// decay and trait resistance. The consistency gate, whitelist, and the gate+pressure
    /// end-to-end case live in Psychology's suite — they exercise SynapseTraitPolicy.
    /// Deterministic — the pressure model is pure.
    /// </summary>
    [SynapseTestSet]
    public static class TraitEngineCases
    {
        private const long Day = 60000L;

        public static IEnumerable<SynapseTestCase> All()
        {
            // #79: pressure accumulates across days (minus daily decay) and ebbs to nothing when evidence stops.
            yield return new SynapseTestCase("Core_TraitPressureAccumulatesAndDecays", () =>
            {
                var comp = new SynapseCorePawnComp();
                // Ticks are nonzero (0 is the "never updated" sentinel; real game ticks are always large).
                float p0 = comp.AccumulateTraitPressure("Bloodlust", 0.5f, "add", 0f, Day);
                Assert.Equal(0.5f, p0, "first day's pressure is the raw contribution");
                // Next day: decay 0.2 (1 day) then +0.5 => 0.3 + 0.5 = 0.8
                float p1 = comp.AccumulateTraitPressure("Bloodlust", 0.5f, "add", 0f, 2 * Day);
                Assert.True(p1 > 0.79f && p1 < 0.81f, $"day-2 pressure should be ~0.8 (was {p1})");
                comp.TryGetTraitPressure("Bloodlust", out var tp);
                Assert.True(tp.peak >= p1, "peak tracks the highest pressure");

                // No evidence for 5 days -> decays to 0 and the entry is dropped.
                comp.DecayTraitPressuresToZero(2 * Day + 5 * Day);
                Assert.False(comp.TryGetTraitPressure("Bloodlust", out _), "stale pressure decays to zero and is removed");
                return $"p0={p0}, p1={p1:0.00}";
            });

            // #44: resistance slows accumulation (pressure += dailyPressure x (1 - resistance)).
            yield return new SynapseTestCase("Core_TraitResistanceSlowsAccumulation", () =>
            {
                var comp = new SynapseCorePawnComp();
                float unresisted = comp.AccumulateTraitPressure("Kind", 0.5f, "remove", 0f, 0L);
                float resisted = comp.AccumulateTraitPressure("Nerves", 0.5f, "remove", 0.8f, 0L);
                Assert.True(resisted < unresisted, "a resistant trait accumulates pressure more slowly");
                Assert.True(resisted > 0.09f && resisted < 0.11f, $"0.5 x (1-0.8) ~= 0.1 (was {resisted})");
                return $"unresisted={unresisted}, resisted={resisted:0.00}";
            });
        }
    }
}
