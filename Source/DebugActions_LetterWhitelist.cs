using System.Text;
using LudeonTK;
using RimWorld;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for the letter-enhancement whitelist (Core #134). The whitelist ships EMPTY and
    /// dormant — RimSynapse intercepts no vanilla letters (it conflicts with mods that own that logic,
    /// e.g. World Domination 2.0). This probe confirms nothing is enhanced: every letter, including
    /// ThreatBig, reports ShouldEnhance=false and passes through to vanilla. It also proves the registry
    /// still works for opt-in hooks by registering a throwaway predicate and seeing it take effect.
    /// Headlessly runnable via run_debug_action.
    /// </summary>
    public static class DebugActions_LetterWhitelist
    {
        [DebugAction("RimSynapse", "Letter whitelist: dormant-gate probe",
            allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.Playing)]
        private static void ProbeWhitelist()
        {
            var threat = LetterMaker.MakeLetter("probe threat", "probe", LetterDefOf.ThreatBig);
            var neutral = LetterMaker.MakeLetter("probe neutral", "probe", LetterDefOf.NeutralEvent);

            bool threatEnhanced = SynapseLetterEnhancement.ShouldEnhance(threat);
            bool neutralEnhanced = SynapseLetterEnhancement.ShouldEnhance(neutral);

            var sb = new StringBuilder();
            sb.AppendLine("[RimSynapse] Letter-enhancement whitelist (Core #134) — dormant:");
            sb.AppendLine($"  registered defs: [{string.Join(", ", SynapseLetterEnhancement.RegisteredDefNames())}]  " +
                          $"predicates: {SynapseLetterEnhancement.PredicateCount}  (expect empty)");
            sb.AppendLine($"  ThreatBig    -> ShouldEnhance={threatEnhanced}  [expect False -> vanilla]");
            sb.AppendLine($"  NeutralEvent -> ShouldEnhance={neutralEnhanced}  [expect False -> vanilla]");

            // Registry still functions for opt-in hooks: a temporary predicate flips one letter.
            SynapseLetterEnhancement.RegisterPredicate(l => l?.Label.Resolve() == "probe threat");
            bool afterHook = SynapseLetterEnhancement.ShouldEnhance(threat);
            sb.AppendLine($"  after registering a probe predicate -> ShouldEnhance={afterHook}  [expect True]");

            sb.AppendLine($"  RESULT: {(!threatEnhanced && !neutralEnhanced && afterHook ? "PASS" : "FAIL")}");
            sb.AppendLine("  (note: probe predicate persists for the session; harmless — matches only this exact label)");

            SynapseLogger.Message(sb.ToString().TrimEnd());
        }
    }
}
