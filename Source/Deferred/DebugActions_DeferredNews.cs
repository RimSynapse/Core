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
            // World-target the rumour: the locality gate (Core#123) passes target-less letters through
            // as Local, and this action exists to demonstrate the HOLD path.
            letter.lookTargets = new LookTargets(WorldTarget());

            // Goes through the intercept prefix like any real letter → held (unless deferral is off /
            // the Other delay is 0), which the report action then shows.
            Find.LetterStack.ReceiveLetter(letter);
            SynapseLogger.Message("[RimSynapse] Injected test letter through ReceiveLetter.");
        }

        /// <summary>An off-map target for synthetic world letters: a non-player settlement when one
        /// exists, else a bare world tile.</summary>
        private static RimWorld.Planet.GlobalTargetInfo WorldTarget()
        {
            var settlements = Find.WorldObjects?.Settlements;
            if (settlements != null)
            {
                for (int i = 0; i < settlements.Count; i++)
                {
                    var s = settlements[i];
                    if (s?.Faction != null && !s.Faction.IsPlayer)
                        return new RimWorld.Planet.GlobalTargetInfo(s);
                }
            }
            return new RimWorld.Planet.GlobalTargetInfo(0);
        }

        /// <summary>
        /// Debug-validation deliverable for Core#123: proves the locality gate. Sends one letter with a
        /// LOCAL target (a cell on the player's home map — must pass straight through, pending count
        /// unchanged) and one with a WORLD target (must be held, pending +1), both through the real
        /// <c>LetterStack.ReceiveLetter</c> path. Logs PASS/FAIL per leg. Needs deferral enabled with a
        /// non-zero default delay for the world leg to hold; the action reports if it is off.
        /// </summary>
        [DebugAction("RimSynapse", "Deferred news: TEST locality gate (#123)",
            actionType = DebugActionType.Action,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void TestLocalityGate()
        {
            var mgr = SynapseDeferredNewsComponent.Instance;
            var settings = RimSynapseMod.Instance?.Settings;
            if (mgr == null || settings == null) { SynapseLogger.Message("[RimSynapse] #123 TEST aborted: no component/settings."); return; }
            if (!settings.deferNewsEnabled || settings.deferDaysDefault <= 0f)
            {
                SynapseLogger.Message("[RimSynapse] #123 TEST: deferral disabled or default delay 0 — enable it to test the hold leg.");
                return;
            }

            Map home = Find.CurrentMap;
            if (home == null || !home.IsPlayerHome) { SynapseLogger.Message("[RimSynapse] #123 TEST aborted: current map is not a player home."); return; }

            // Leg 1: LOCAL — a letter about a spot on the player's own map must not be held.
            int pending0 = mgr.Pending.Count;
            var localLetter = LetterMaker.MakeLetter(
                "Synapse #123 local test", "A letter about the colony's own ground.", LetterDefOf.NeutralEvent);
            localLetter.ID = Find.UniqueIDsManager.GetNextLetterID();
            localLetter.lookTargets = new LookTargets(new TargetInfo(home.Center, home));
            Find.LetterStack.ReceiveLetter(localLetter);
            bool localPassed = mgr.Pending.Count == pending0;

            // Leg 2: WORLD — a letter about somewhere else must be held.
            int pending1 = mgr.Pending.Count;
            var worldLetter = LetterMaker.MakeLetter(
                "Synapse #123 world test", "A letter about somewhere far away.", LetterDefOf.NeutralEvent);
            worldLetter.ID = Find.UniqueIDsManager.GetNextLetterID();
            worldLetter.lookTargets = new LookTargets(WorldTarget());
            Find.LetterStack.ReceiveLetter(worldLetter);
            bool worldHeld = mgr.Pending.Count == pending1 + 1;

            SynapseLogger.Message(
                $"[RimSynapse] #123 TEST locality gate: local letter {(localPassed ? "passed through" : "WAS HELD")} " +
                $"({(localPassed ? "PASS" : "FAIL")}); world letter {(worldHeld ? "held" : "WAS NOT HELD")} " +
                $"({(worldHeld ? "PASS" : "FAIL")}). Scopes: local={DeferredNewsUtility.ClassifyScope(localLetter)}, " +
                $"world={DeferredNewsUtility.ClassifyScope(worldLetter)}.");
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
