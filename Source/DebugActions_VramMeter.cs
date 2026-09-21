using System.Text;
using LudeonTK;
using UnityEngine;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for the cross-vendor measured VRAM meter (Core #125), grouped under
    /// "RimSynapse". Forces an immediate sample (bypassing the throttle) and dumps what the advisory
    /// and the monitor's Advanced view will render, including whether the reading is measured (PDH) or
    /// the meter fell back. Headlessly runnable via run_debug_action.
    /// </summary>
    public static class DebugActions_VramMeter
    {
        [DebugAction("RimSynapse", "GPU: sample VRAM meter + dump",
            allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.Playing)]
        private static void SampleAndDump()
        {
            bool ok = VramMeter.SampleNow();

            var sb = new StringBuilder();
            sb.AppendLine("[RimSynapse] VRAM meter sample (Core #125):");
            sb.AppendLine($"  GPU (Unity):   {SystemInfo.graphicsDeviceName ?? "unknown"} " +
                          $"[{SystemInfo.graphicsDeviceVendor ?? "?"}]");
            sb.AppendLine($"  supported:     {VramMeter.Supported}  (measured={ok})");
            sb.AppendLine($"  total VRAM:    {VramMeter.TotalMb:F0} MB ({VramMeter.TotalMb / 1024f:F1} GB)");
            if (VramMeter.Supported)
            {
                sb.AppendLine($"  used (system): {VramMeter.UsedMb:F0} MB ({VramMeter.UsedMb / 1024f:F1} GB)");
                sb.AppendLine($"  free:          {VramMeter.FreeMb:F0} MB ({VramMeter.FreeMb / 1024f:F1} GB)");
            }
            else
            {
                sb.AppendLine($"  used/free:     unavailable — advisory falls back to estimate");
                sb.AppendLine($"  last error:    {VramMeter.LastError ?? "(none)"}");
            }

            // Show what landed in the Core GpuStats channel a consumer would read.
            var gpu = SynapseClient.Gpu;
            if (gpu != null)
                sb.AppendLine($"  GpuStats:      supported={gpu.supported} " +
                              $"used={gpu.usedVramGb:F1} GB total={gpu.totalVramGb:F1} GB " +
                              $"({gpu.VramUsagePercent:P0})");

            SynapseLogger.Message(sb.ToString().TrimEnd());
        }
    }
}
