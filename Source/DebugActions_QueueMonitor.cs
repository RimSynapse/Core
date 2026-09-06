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

        [DebugAction("RimSynapse", "LLM metrics: record sample call + dump",
            allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.Playing)]
        private static void RecordSampleMetric()
        {
            var r = ChatResult.Success("hello world", "google/gemma-4-e2b",
                promptTokens: 4096, completionTokens: 256, durationMs: 2000);
            SynapseCallMetrics.Record(ApiProvider.Local_LMStudio, "http://localhost:1234/v1", r);

            var ses = SynapseCallMetrics.Session;
            var sav = SynapseCallMetrics.Save;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[RimSynapse] LLM call metrics (Core #127) after sample record:");
            sb.AppendLine($"  session: {ses.calls} calls, {ses.Tops:F1} tok/s, " +
                          $"{ses.promptTokens}p/{ses.completionTokens}c");
            sb.AppendLine($"  save:    {sav.calls} calls, {sav.promptTokens}p/{sav.completionTokens}c");
            foreach (var m in ses.perModel)
                sb.AppendLine($"    model {m.Key}: {m.Value.Tops:F1} tok/s");
            int? reported = RimSynapse.Internal.ModelManager.ContextLength
                            ?? RimSynapseMod.Instance?.Settings?.modelContextLimit;
            sb.AppendLine($"  context: reported max {(reported.HasValue ? reported.Value.ToString() : "?")} tok, " +
                          $"proven max {sav.ProvenMaxContext()} tok");
            SynapseLogger.Message(sb.ToString().TrimEnd());
        }

        [DebugAction("RimSynapse", "Capture screenshot to repo (workshop)",
            allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.Playing)]
        private static void CaptureScreenshotToRepo()
        {
            try
            {
                string root = System.Environment.GetEnvironmentVariable("RIMSYNAPSE_ROOT");
                if (string.IsNullOrEmpty(root)) root = System.IO.Path.Combine(GenFilePaths.ConfigFolderPath, "RimSynapse");
                string dir = System.IO.Path.Combine(root, "About", "steam_screenshots");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, $"monitor_{System.DateTime.Now:yyyyMMdd_HHmmss}.png");

                // Unity's own capture — writes the full framebuffer to disk a frame later. Bypasses the
                // external screen-grab tooling entirely (headless-safe).
                UnityEngine.ScreenCapture.CaptureScreenshot(path);
                SynapseLogger.Message($"[RimSynapse] Screenshot requested -> {path} (written within a frame or two).");
            }
            catch (System.Exception ex)
            {
                SynapseLogger.Warning("Screenshot capture failed: " + ex.Message);
            }
        }
    }
}
