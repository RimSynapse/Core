using LudeonTK;
using Verse;

namespace RimSynapse.Compat
{
    /// <summary>
    /// Debug actions for the cross-mod compatibility tracker (Core #91), grouped under "RimSynapse".
    /// Exercises the manifest read + Core-floor check headlessly (via the toolkit bridge's
    /// run_debug_action) so the mechanic can be validated without a version mismatch on hand.
    /// </summary>
    public static class DebugActions_Compat
    {
        [DebugAction("RimSynapse", "Compat: check now + dump",
            actionType = DebugActionType.Action,
            allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.PlayingOnMap | AllowedGameStates.PlayingOnWorld)]
        private static void CompatCheckNow()
        {
            SynapseLogger.Message(SynapseCompatChecker.DumpReport());
        }
    }
}
