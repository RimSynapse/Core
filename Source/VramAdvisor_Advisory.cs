using System;
using System.Text.RegularExpressions;
using UnityEngine;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// VRAM advisory system: model estimation, GPU detection, user warnings.
    /// Advisory display and model estimation methods.
    /// </summary>
    public static partial class VramAdvisor
    {
        private static void ShowNoModelWarning(float totalGpuGb)
        {
            string gpuName = SystemInfo.graphicsDeviceName ?? "Unknown GPU";

            string msg =
                "RimSynapse — No LLM Model Detected\n\n" +
                "RimSynapse could not connect to LM Studio or no model is loaded.\n" +
                "AI features (chat, psychology, storytelling) will not function.\n\n" +
                "To fix this:\n" +
                "  1. Download LM Studio: https://lmstudio.ai\n" +
                "  2. Install and open LM Studio\n" +
                "  3. Search for and download a model (recommended: gemma-4-12b)\n" +
                "  4. Go to the Local Server tab (left sidebar)\n" +
                "  5. Click \"Start Server\" — it should say Ready on port 1234\n\n" +
                "Quickstart guide:\n" +
                "  https://lmstudio.ai/docs/basics/server\n\n" +
                $"GPU: {gpuName}  •  VRAM: {totalGpuGb:F0} GB total\n\n" +
                "You can disable this popup in Mod Settings -> RimSynapse Core.";

            SynapseLogger.Warning("No LLM model detected. LM Studio may not be running or has no model loaded.");

            LongEventHandler.QueueLongEvent(() =>
            {
                Find.WindowStack?.Add(new Dialog_MessageBox(
                    msg,
                    "OK",
                    null,
                    null,
                    null,
                    null,
                    false,
                    null,
                    null));
            }, null, false, null);
        }

        /// <summary>
        /// Show the VRAM status dialog. Adapts messaging based on headroom
        /// and only suggests NVIDIA Tool for compatible GPU series.
        /// </summary>
        private static void ShowAdvisory(float totalGb, float lmGb,
            float estFreeGb, string modelName, bool measured, float usedGb)
        {
            string gpuName = UnityEngine.SystemInfo.graphicsDeviceName ?? "Unknown GPU";
            // A measured free figure is trustworthy on its own; an estimated one only warns when we
            // actually have a model to blame (otherwise the estimate is mostly noise).
            bool isCritical = estFreeGb < 2.0f && (measured || lmGb > 0f);

            // ── Status line ──
            string freeWord = measured ? "Measured" : "Estimated";
            string status;
            if (!measured && lmGb <= 0f && string.IsNullOrEmpty(modelName))
                status = "No LLM model detected — VRAM estimate unavailable.";
            else if (isCritical)
                status =
                    $"⚠  {freeWord} {estFreeGb:F1} GB free — below recommended 2 GB.\n" +
                    "Consider adjusting your system settings before loading a late-game save.";
            else
                status =
                    $"✓  {freeWord} {estFreeGb:F1} GB free — your system should be stable.\n" +
                    "See suggestions below if you experience VRAM-related issues.";

            // ── Suggestions ──
            string suggestions =
                "\n\nSuggestions if you experience issues:\n" +
                "  • Use a smaller model in LM Studio (e.g., 7B instead of 12B)\n" +
                "  • Reduce the context window size in LM Studio\n" +
                "  • Close GPU-heavy background apps (Chrome, Discord)";

            // ── NVIDIA Tool recommendation — only for compatible GPUs ──
            string nvToolLine = "";
            bool isRtx40or50 = IsRtx40or50Series(gpuName);

            if (isRtx40or50 && !ModsConfig.IsActive("RimSynapse.NvidiaTool"))
            {
                nvToolLine =
                    "\n\nYour " + gpuName + " supports the RimSynapse NVIDIA Tool\n" +
                    "companion mod for real-time GPU monitoring and detailed VRAM breakdown.";
            }
            else if (!isRtx40or50)
            {
                // Non-NVIDIA or older GPU — don't mention the tool at all
            }

            // Measured: show real used/total and skip the per-component estimate lines (the meter's
            // used figure is system-wide and already includes them). Estimate: keep the old breakdown.
            string vramLines = measured
                ? $"VRAM: {usedGb:F1} / {totalGb:F1} GB used (measured)\n\n" +
                  (lmGb > 0f ? $"  • LM Studio model ({modelName}):  ~{lmGb:F1} GB of that\n\n" : "")
                : $"VRAM: {totalGb:F0} GB total\n\n" +
                  (lmGb > 0f ? $"  • LM Studio model ({modelName}):  ~{lmGb:F1} GB\n" : "") +
                  "  • RimWorld (estimate):  ~1.0 GB\n" +
                  "  • System (estimate):    ~2.5 GB\n\n";

            string msg =
                "RimSynapse — GPU Memory Status\n\n" +
                $"GPU: {gpuName}\n" +
                vramLines +
                status +
                suggestions +
                nvToolLine +
                "\n\nDisable this notification in Mod Settings → RimSynapse Core.";

            LongEventHandler.QueueLongEvent(() =>
            {
                Find.WindowStack?.Add(new Dialog_MessageBox(
                    msg,
                    "OK",
                    null,
                    null,
                    null,
                    null,
                    false,
                    null,
                    null));
            }, null, false, null);

            if (isCritical)
            {
                SynapseLogger.Warn("core",
                    $"VRAM advisory: ~{estFreeGb:F1} GB estimated free " +
                    $"with {modelName ?? "unknown"} loaded on {totalGb:F0} GB GPU.");
            }
        }

        /// <summary>
        /// Detect if the GPU is an NVIDIA RTX 4000 or 5000 series
        /// (the supported cards for RimSynapse NVIDIA Tool).
        /// Uses Unity's SystemInfo.graphicsDeviceName which returns
        /// strings like "NVIDIA GeForce RTX 5070 Ti".
        /// </summary>
        private static bool IsRtx40or50Series(string gpuName)
        {
            if (string.IsNullOrEmpty(gpuName)) return false;

            string upper = gpuName.ToUpperInvariant();

            // Check for RTX 40xx series: 4060, 4070, 4080, 4090, etc.
            // Check for RTX 50xx series: 5060, 5070, 5080, 5090, etc.
            // Also covers Ti/Super variants since we just check the model number prefix
            if (!upper.Contains("NVIDIA") && !upper.Contains("GEFORCE"))
                return false;

            return Regex.IsMatch(upper, @"RTX\s*[45]0[5-9]0");
        }
        /// <summary>
        /// Estimate VRAM usage for an LLM model based on its name.
        /// Parses parameter count (e.g., "12b", "7b") and applies
        /// Q4_K_M quantization estimate.
        /// Returns 0 if no model is selected or name can't be parsed.
        /// </summary>
        internal static float EstimateModelVramGb(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return 0f;

            float billions = ParseBillionParams(modelName.ToLowerInvariant());
            if (billions <= 0f) return 0f;

            // Q4_K_M quantization: ~0.65 GB per billion params
            // + ~0.5 GB KV cache / runtime overhead
            return (billions * 0.65f) + 0.5f;
        }

        /// <summary>
        /// Parse billion parameter count from model name string.
        /// </summary>
        private static float ParseBillionParams(string name)
        {
            // Match standard patterns: "12b", "7b", "70b", "3.8b", "0.5b"
            // Skip quantization markers: "q4b", "q8b"
            var match = Regex.Match(name, @"(?<![a-z])(\d+\.?\d*)b(?!\w)");
            if (match.Success)
            {
                float val;
                if (float.TryParse(match.Groups[1].Value, out val) && val > 0f)
                    return val;
            }

            // Handle MoE/expert notation: "e4b" = expert 4B, "a4b" = active 4B
            // These indicate the active parameter count for mixture-of-experts models
            // (e.g., gemma-4-e4b = 4B active expert parameters)
            var moeMatch = Regex.Match(name, @"[ea](\d+\.?\d*)b(?!\w)");
            if (moeMatch.Success)
            {
                float val;
                if (float.TryParse(moeMatch.Groups[1].Value, out val) && val > 0f)
                    return val;
            }

            // Fallback by name keywords
            if (name.Contains("mini")) return 3.8f;
            if (name.Contains("small")) return 7f;
            if (name.Contains("medium")) return 13f;
            if (name.Contains("large")) return 34f;

            return 0f;
        }
    }
}
