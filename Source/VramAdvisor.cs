using System;
using System.Text.RegularExpressions;
using UnityEngine;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Lightweight VRAM awareness check for the Core mod.
    /// Uses only Unity's built-in APIs — no NVIDIA dependency, fully vendor-agnostic.
    ///
    /// On first game load, estimates whether the player has enough GPU memory
    /// headroom for both RimWorld and their loaded LLM model. If headroom is
    /// tight, shows a non-blocking notification with suggestions.
    ///
    /// For detailed real-time GPU monitoring, points users to the companion
    /// mod RimSynapse NVIDIA Tool.
    ///
    /// NOT a GameComponent — zero save-file footprint. Safe to add/remove.
    /// </summary>
    public static partial class VramAdvisor
    {
        /// <summary>Minimum recommended free VRAM in GB (higher than NVIDIA Tool
        /// since our estimates are less precise than real NVML data).</summary>
        private const float MinFreeGb = 4.0f;

        /// <summary>
        /// Called once during mod startup (StaticConstructorOnStartup).
        /// Uses Unity's SystemInfo to estimate VRAM headroom.
        /// </summary>
        /// <summary>Max retries if LM Studio doesn't return a model.</summary>
        private const int MaxRetries = 3;
        private const int RetryDelayMs = 3000;
        private const int InitialDelayMs = 5000;

        /// <summary>
        /// Whether the NVIDIA Tool companion mod handles VRAM breakdown.
        /// The "no model" check still runs from Core regardless.
        /// </summary>
        private static bool _nvidiaToolHandlesVram;

        internal static void Check()
        {

            // Track whether NVIDIA Tool handles the VRAM advisory —
            // but we ALWAYS check for LM Studio connectivity (no model = mod broken)
            _nvidiaToolHandlesVram = ModsConfig.IsActive("RimSynapse.NvidiaTool");

            if (_nvidiaToolHandlesVram)
            {
                SynapseLogger.Info("core",
                    "NVIDIA Tool mod detected — Core will defer VRAM breakdown " +
                    "but still check LM Studio connectivity.");
            }

            // Run the model query on a background thread with delay.
            // At startup, HttpEngine and LM Studio need a few seconds to be ready.
            System.Threading.Tasks.Task.Run(() =>
            {
                // Wait for things to settle
                System.Threading.Thread.Sleep(InitialDelayMs);
                RunCheck(0);
            });
        }

        /// <summary>
        /// Query LM Studio for the loaded model, with retry logic.
        /// Runs on a background thread to avoid blocking the main thread.
        /// </summary>
        private static void RunCheck(int attempt)
        {
            // SystemInfo must be read on the main thread — cache it early
            // Actually SystemInfo.graphicsMemorySize is thread-safe in Unity
            int totalGpuMb = SystemInfo.graphicsMemorySize;
            if (totalGpuMb <= 0) return;

            float totalGpuGb = totalGpuMb / 1024f;

            // Query LM Studio for the actual loaded model
            string modelName = null;
            try
            {
                Internal.HttpEngine.EnsureInitialized();
                var modelsResult = Internal.HttpEngine.GetModelsSync();
                Internal.ModelManager.UpdateCache(modelsResult);

                SynapseLogger.Message($"VRAM advisor (attempt {attempt + 1}): " +
                    $"online={modelsResult.online}, " +
                    $"models={modelsResult.modelIds.Count}, " +
                    $"error={modelsResult.error ?? "none"}");

                if (modelsResult.online && modelsResult.modelIds.Count > 0)
                {
                    modelName = modelsResult.modelIds[0];
                    SynapseLogger.Message($"VRAM advisor: live model from LM Studio: \"{modelName}\"");
                }
            }
            catch (Exception ex)
            {
                SynapseLogger.Warning($"VRAM advisor: could not query LM Studio (attempt {attempt + 1}): {ex.Message}");
            }

            // If no model found and retries remain, wait and try again
            if (string.IsNullOrEmpty(modelName) && attempt < MaxRetries)
            {
                SynapseLogger.Message($"VRAM advisor: no model found, retrying in {RetryDelayMs}ms...");
                System.Threading.Thread.Sleep(RetryDelayMs);
                RunCheck(attempt + 1);
                return;
            }

            bool showNotify = RimSynapseMod.Instance?.Settings?.showVramAdvisory ?? true;

            if (string.IsNullOrEmpty(modelName))
            {
                bool isRemote = RimSynapseMod.Instance?.Settings?.IsRemoteUrl ?? false;
                SynapseLogger.Warning("VRAM Advisor (Logging): No LLM model detected. LM Studio may not be running or has no model loaded.");
                
                if (!isRemote && showNotify)
                {
                    ShowNoModelWarning(totalGpuGb);
                }
                else if (isRemote)
                {
                    SynapseLogger.Warning("Remote LM Studio host is unreachable or has no model loaded. Popup suppressed.");
                }
                return;
            }
            // ── Model found — LM Studio is alive ──
            // Now decide whether to show the VRAM breakdown.
            // If NVIDIA Tool handles VRAM, or user disabled notifications, skip it.
            bool isRemoteHost = RimSynapseMod.Instance?.Settings?.IsRemoteUrl ?? false;

            float lmEstimateGb = EstimateModelVramGb(modelName);

            // Prefer a MEASURED free-VRAM figure (Core #125) over the old estimate. The meter reports
            // system-wide dedicated usage — RimWorld, an on-machine LM Studio, and the desktop are all
            // already counted — so when it answers we use total − measuredUsed directly and never add
            // the per-component estimates on top (that would double-count the model).
            bool measured = VramMeter.Sample();
            float usedGb, freeGb;
            if (measured)
            {
                if (VramMeter.TotalMb > 0f) totalGpuGb = VramMeter.TotalMb / 1024f;
                usedGb = VramMeter.UsedMb / 1024f;
                freeGb = VramMeter.FreeMb / 1024f;

                SynapseLogger.Warning(
                    $"VRAM Advisor (measured): {totalGpuGb:F1} GB total, " +
                    $"{usedGb:F1} GB used system-wide, {freeGb:F1} GB free " +
                    $"(model {modelName ?? "none"} ~{lmEstimateGb:F1} GB).");
            }
            else
            {
                // Fallback estimate: model + a nominal RimWorld slice + desktop/background overhead.
                const float rwEstimateGb = 1.0f;     // RimWorld itself typically uses 0.5-1.5 GB
                const float systemEstimateGb = 2.5f; // DWM/compositor/background apps (Win11 + Chrome/Discord)
                usedGb = lmEstimateGb + rwEstimateGb + systemEstimateGb;
                freeGb = totalGpuGb - usedGb;

                SynapseLogger.Warning(
                    $"VRAM Advisor (estimate): {totalGpuGb:F1} GB total, " +
                    $"~{lmEstimateGb:F1} GB model ({modelName ?? "none"}), " +
                    $"~{rwEstimateGb:F1} GB RimWorld, ~{systemEstimateGb:F1} GB system. " +
                    $"Est. free: ~{freeGb:F1} GB.");
            }

            if (!showNotify || _nvidiaToolHandlesVram || isRemoteHost)
            {
                return;
            }

            ShowAdvisory(totalGpuGb, lmEstimateGb, freeGb, modelName, measured, usedGb);
        }

    }
}
