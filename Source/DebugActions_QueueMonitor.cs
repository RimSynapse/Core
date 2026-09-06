using LudeonTK;
using RimSynapse.UI;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Debug helpers for the monitor, grouped under "RimSynapse". Opening it in the Basic (VRAM) view
    /// is the headless hook for validating the GPU/VRAM panel (Core #128) — it forces the view to
    /// Basic and pops the window so a screenshot or a person can confirm the panel renders.
    /// </summary>
    public static class DebugActions_QueueMonitor
    {
        [DebugAction("RimSynapse", "Monitor: open VRAM (Basic) view",
            allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.Playing)]
        private static void OpenVramView()
        {
            var settings = RimSynapseMod.Instance?.Settings;
            if (settings != null) settings.qmAdvancedView = false;

            if (!Find.WindowStack.IsOpen<Dialog_QueueMonitor>())
                Find.WindowStack.Add(new Dialog_QueueMonitor());

            SynapseLogger.Message("[RimSynapse] Monitor opened in Basic (VRAM) view (#128).");
        }

        [DebugAction("RimSynapse", "Monitor: open LLM calls (Advanced) view",
            allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.Playing)]
        private static void OpenLlmView()
        {
            var settings = RimSynapseMod.Instance?.Settings;
            if (settings != null) settings.qmAdvancedView = true;

            if (!Find.WindowStack.IsOpen<Dialog_QueueMonitor>())
                Find.WindowStack.Add(new Dialog_QueueMonitor());

            SynapseLogger.Message("[RimSynapse] Monitor opened in Advanced (LLM calls) view (#128).");
        }
    }
}
