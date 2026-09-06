using LudeonTK;
using RimSynapse.UI;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Debug helpers for the LLM Queue Monitor, grouped under "RimSynapse". Opening it in the
    /// Advanced view is the headless hook for validating the GPU/VRAM panel (Core #128) — it forces
    /// the setting on and pops the window so a screenshot or a person can confirm it renders.
    /// </summary>
    public static class DebugActions_QueueMonitor
    {
        [DebugAction("RimSynapse", "Queue monitor: open (Advanced view)",
            allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.Playing)]
        private static void OpenAdvanced()
        {
            var settings = RimSynapseMod.Instance?.Settings;
            if (settings != null) settings.qmAdvancedView = true;

            if (!Find.WindowStack.IsOpen<Dialog_QueueMonitor>())
                Find.WindowStack.Add(new Dialog_QueueMonitor());

            SynapseLogger.Message("[RimSynapse] Queue monitor opened in Advanced view (#128).");
        }
    }
}
