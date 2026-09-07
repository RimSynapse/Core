using System.Text;
using LudeonTK;
using RimWorld;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for the letter-enhancement whitelist (Core #134). Confirms a whitelisted letter
    /// def (ThreatBig) is gated for enhancement while a non-whitelisted one (NeutralEvent — the class a
    /// crash-pod rescue rides on) is not, so the latter passes through to vanilla/immediate handling.
    /// Headlessly runnable via run_debug_action.
    /// </summary>
    public static class DebugActions_LetterWhitelist
    {
        [DebugAction("RimSynapse", "Letter whitelist: enhance-gate probe",
            allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.Playing)]
        private static void ProbeWhitelist()
        {
            var threat = LetterMaker.MakeLetter("probe threat", "probe", LetterDefOf.ThreatBig);
            var neutral = LetterMaker.MakeLetter("probe neutral", "probe", LetterDefOf.NeutralEvent);

            bool threatEnhanced = SynapseLetterEnhancement.ShouldEnhance(threat);
            bool neutralEnhanced = SynapseLetterEnhancement.ShouldEnhance(neutral);

            var sb = new StringBuilder();
            sb.AppendLine("[RimSynapse] Letter-enhancement whitelist (Core #134):");
            sb.AppendLine($"  registered defs: [{string.Join(", ", SynapseLetterEnhancement.RegisteredDefNames())}]  " +
                          $"predicates: {SynapseLetterEnhancement.PredicateCount}");
            sb.AppendLine($"  ThreatBig  -> ShouldEnhance={threatEnhanced}  [expect True -> held/enhanced]");
            sb.AppendLine($"  NeutralEvent -> ShouldEnhance={neutralEnhanced}  [expect False -> vanilla/immediate]");
            sb.AppendLine($"  RESULT: {(threatEnhanced && !neutralEnhanced ? "PASS" : "FAIL")}");

            SynapseLogger.Message(sb.ToString().TrimEnd());
        }
    }
}
