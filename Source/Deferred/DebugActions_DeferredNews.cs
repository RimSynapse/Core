using System.Text;
using LudeonTK;
using RimWorld;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Debug actions for the deferred-news pipeline (WorldNews#19), grouped under "RimSynapse". They
    /// exercise the intercept → hold → release path headlessly, without waiting in-game days.
    /// </summary>
    public static class DebugActions_DeferredNews
    {
        [DebugAction("RimSynapse", "Deferred news: report pending",
            actionType = DebugActionType.Action,
            allowedGameStates = AllowedGameStates.PlayingOnMap | AllowedGameStates.PlayingOnWorld)]
        private static void ReportPending()
        {
            var mgr = SynapseDeferredNewsComponent.Instance;
            if (mgr == null) { SynapseLogger.Message("[RimSynapse] No deferred-news component."); return; }

            int now = Find.TickManager?.TicksGame ?? 0;
            var sb = new StringBuilder();
            sb.AppendLine($"[RimSynapse] Deferred news: {mgr.Pending.Count} pending (now tick {now}).");
            foreach (DeferredNewsEvent ev in mgr.Pending)
            {
                float daysLeft = (ev.releaseTick - now) / (float)GenDate.TicksPerDay;
                sb.AppendLine($"  [{ev.category}] \"{ev.title}\" — releases in {daysLeft:F2}d (tick {ev.releaseTick})");
            }
            SynapseLogger.Message(sb.ToString());
        }

        [DebugAction("RimSynapse", "Deferred news: inject test letter (held)",
            actionType = DebugActionType.Action,
            allowedGameStates = AllowedGameStates.PlayingOnMap | AllowedGameStates.PlayingOnWorld)]
        private static void InjectTestLetter()
        {
            var letter = LetterMaker.MakeLetter(
                "Rumour from the Trade Roads",
                "A passing caravan brings word of stirrings beyond the ridge. Details are thin, but the "
                + "frontier press is already sniffing at the story.",
                LetterDefOf.NeutralEvent);
            letter.ID = Find.UniqueIDsManager.GetNextLetterID();

            // Goes through the intercept prefix like any real letter → held (unless deferral is off /
            // the Other delay is 0), which the report action then shows.
            Find.LetterStack.ReceiveLetter(letter);
            SynapseLogger.Message("[RimSynapse] Injected test letter through ReceiveLetter.");
        }

        [DebugAction("RimSynapse", "Deferred news: release all now",
            actionType = DebugActionType.Action,
            allowedGameStates = AllowedGameStates.PlayingOnMap | AllowedGameStates.PlayingOnWorld)]
        private static void ReleaseAllNow()
        {
            var mgr = SynapseDeferredNewsComponent.Instance;
            if (mgr == null) { SynapseLogger.Message("[RimSynapse] No deferred-news component."); return; }
            int n = mgr.Pending.Count;
            mgr.DebugReleaseAllNow();
            SynapseLogger.Message($"[RimSynapse] Released {n} deferred event(s) now.");
        }
    }
}
