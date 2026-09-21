using System.Reflection;
using System.Text;
using LudeonTK;
using RimWorld;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for the world-event publish surface (Core #135), grouped under "RimSynapse".
    /// Headlessly runnable via the toolkit's run_debug_action. Publishes a sample world event through
    /// the same primitives-only reflection path a companion mod would use, then dumps the pending set,
    /// the selection-context note, and the weight boost — so the additive, source-gated behaviour can
    /// be confirmed end to end.
    /// </summary>
    public static class DebugActions_WorldEvents
    {
        [DebugAction("RimSynapse", "World events: publish sample + dump (#135)",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void PublishAndDump()
        {
            var sb = new StringBuilder();
            int now = Find.TickManager?.TicksAbs ?? 0;

            // Publish exactly as a Core-absent producer would: reflect the primitives-only method.
            var m = typeof(SynapseCoreContext).GetMethod("PublishWorldEvent", BindingFlags.Public | BindingFlags.Static);
            sb.AppendLine($"[RimSynapse] World-event publish surface (Core #135): reflectable={(m != null)}");
            m?.Invoke(null, new object[] { "Flu", "the northern reaches", 2.5f, "the drifting caravans", "a flu is sweeping the northern settlements", 60000 });

            var pending = SynapseWorldEventInbox.Pending(now);
            sb.AppendLine($"  pending events: {pending.Count}");
            foreach (var e in pending) sb.AppendLine($"    [{e.kind}] {e.summary} (expires @{e.expiryTick})");

            sb.AppendLine("  selection-context note:");
            foreach (var line in SynapseWorldEventInbox.SelectionContextNote(now).Split('\n'))
                sb.AppendLine("    " + line);

            sb.AppendLine($"  weight boost for 'Flu': {SynapseWorldEventInbox.WeightBoostFor("Flu", now):0.##}");
            sb.AppendLine($"  weight boost for 'Eclipse' (unmatched): {SynapseWorldEventInbox.WeightBoostFor("Eclipse", now):0.##}");
            Log.Message(sb.ToString());
        }

        [DebugAction("RimSynapse", "World events: clear inbox (#135)",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ClearInbox()
        {
            SynapseWorldEventInbox.Clear();
            Log.Message("[RimSynapse] World-event inbox cleared — storyteller reverts to vanilla (source-gated).");
        }
    }
}
