using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimSynapse.UI
{
    /// <summary>
    /// The Basic view's GPU/VRAM panel (Core #128) — the monitor's default, at-a-glance face; the
    /// Advanced view swaps to the full LLM-call tables. Draws the GPU name, a measured VRAM usage bar
    /// (from <see cref="VramMeter"/>), and a component breakdown (from <see cref="VramBreakdown"/>) —
    /// RimWorld, LM Studio, in-process consumers, and the system remainder. When the meter is
    /// unsupported (integrated GPU / non-Windows) it says so and shows the component estimates only.
    /// </summary>
    public partial class Dialog_QueueMonitor
    {
        private static readonly Color GpuBarBg = new Color(0.16f, 0.16f, 0.19f, 0.9f);
        private static readonly Color VramGreen = new Color(0.40f, 0.80f, 0.45f);
        private static readonly Color VramYellow = new Color(0.90f, 0.80f, 0.30f);
        private static readonly Color VramOrange = new Color(0.95f, 0.60f, 0.25f);
        private static readonly Color VramRed = new Color(0.90f, 0.35f, 0.35f);

        private static readonly Color CompRimWorld = new Color(0.45f, 0.65f, 0.95f);
        private static readonly Color CompLmStudio = new Color(0.65f, 0.55f, 0.90f);
        private static readonly Color CompConsumer = new Color(0.40f, 0.80f, 0.75f);
        private static readonly Color CompSystem = new Color(0.55f, 0.55f, 0.60f);

        /// <summary>Draw the GPU/VRAM panel starting at <paramref name="top"/>; returns its height.</summary>
        private float DrawGpuPanel(float width, float top)
        {
            VramBreakdown.Refresh();

            float x = 4f;
            float w = width - 8f;
            float y = top + 4f;

            // ── Title + GPU name ──
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(x, y, 220f, 28f), "GPU / VRAM");
            Text.Font = GameFont.Small;

            string gpuName = SystemInfo.graphicsDeviceName ?? "Unknown GPU";
            string gpuVendor = SystemInfo.graphicsDeviceVendor;
            string gpuLine = string.IsNullOrEmpty(gpuVendor) ? gpuName : $"{gpuName}  ·  {gpuVendor}";
            var prevAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = new Color(0.7f, 0.7f, 0.7f);
            Widgets.Label(new Rect(x + 224f, y, w - 228f, 28f), gpuLine);
            GUI.color = Color.white;
            Text.Anchor = prevAnchor;
            y += 30f;

            float totalMb = VramMeter.TotalMb > 0f ? VramMeter.TotalMb : SystemInfo.graphicsMemorySize;

            // ── Measured VRAM usage bar ──
            if (VramMeter.Supported && totalMb > 0f)
            {
                float usedMb = VramMeter.UsedMb;
                float pct = totalMb > 0f ? Mathf.Clamp01(usedMb / totalMb) : 0f;
                DrawBar(new Rect(x, y, w, 22f), pct, VramColor(pct),
                    $"VRAM  {usedMb / 1024f:F1} / {totalMb / 1024f:F1} GB  ({pct:P0})");
                y += 26f;
            }
            else
            {
                GUI.color = new Color(0.85f, 0.75f, 0.4f);
                Widgets.Label(new Rect(x, y, w, 22f),
                    totalMb > 0f
                        ? $"Measured VRAM unavailable (integrated GPU or non-Windows) — {totalMb / 1024f:F1} GB total, estimates only"
                        : "GPU VRAM not detected.");
                GUI.color = Color.white;
                y += 24f;
            }

            // ── Component breakdown ──
            var rows = new List<(string label, float mb, Color color)>
            {
                ("RimWorld", VramBreakdown.RimWorldMb, CompRimWorld),
                (VramBreakdown.LmStudioRemote ? "LM Studio (remote host)" : "LM Studio model",
                    VramBreakdown.LmStudioMb, CompLmStudio),
            };
            foreach (var c in VramBreakdown.Consumers)
                rows.Add((c.label ?? "in-process model", c.vramMb, CompConsumer));
            if (VramBreakdown.Measured)
                rows.Add(("System / desktop", VramBreakdown.SystemMb, CompSystem));

            float denom = totalMb > 0f ? totalMb : 1f;
            foreach (var row in rows)
            {
                float frac = Mathf.Clamp01(row.mb / denom);
                string val = row.mb > 0.5f ? $"{row.mb / 1024f:F1} GB" : "—";
                DrawBar(new Rect(x, y, w, 18f), frac, row.color, $"{row.label}", val);
                y += 20f;
            }

            return (y + 2f) - top;
        }

        /// <summary>A one-line GPU/VRAM summary for the Advanced view's header: a compact VRAM bar plus
        /// a component readout, so switching to the LLM-call view doesn't lose sight of GPU load.</summary>
        private void DrawGpuSummary(float x, float y, float width)
        {
            VramBreakdown.Refresh();

            float totalMb = VramMeter.TotalMb > 0f ? VramMeter.TotalMb : SystemInfo.graphicsMemorySize;
            float barW = 320f;

            if (VramMeter.Supported && totalMb > 0f)
            {
                float used = VramMeter.UsedMb;
                float pct = Mathf.Clamp01(used / totalMb);
                DrawBar(new Rect(x, y, barW, 20f), pct, VramColor(pct),
                    $"VRAM {used / 1024f:F1} / {totalMb / 1024f:F1} GB", $"{pct:P0}");
            }
            else
            {
                Widgets.DrawBoxSolid(new Rect(x, y, barW, 20f), GpuBarBg);
                var pa = Text.Anchor; var pf = Text.Font;
                Text.Font = GameFont.Tiny; Text.Anchor = TextAnchor.MiddleLeft;
                GUI.color = new Color(0.85f, 0.75f, 0.4f);
                Widgets.Label(new Rect(x + 6f, y, barW - 12f, 20f),
                    totalMb > 0f ? $"VRAM measured n/a · {totalMb / 1024f:F1} GB total (est)" : "VRAM n/a");
                GUI.color = Color.white; Text.Anchor = pa; Text.Font = pf;
            }

            // Component readout to the right of the bar.
            var parts = new List<string>
            {
                $"RW {VramBreakdown.RimWorldMb / 1024f:F1}",
                VramBreakdown.LmStudioRemote ? "LMS remote" : $"LMS {VramBreakdown.LmStudioMb / 1024f:F1}",
            };
            foreach (var c in VramBreakdown.Consumers)
                parts.Add($"{ShortLabel(c.label)} {c.vramMb / 1024f:F1}");
            if (VramBreakdown.Measured)
                parts.Add($"Sys {VramBreakdown.SystemMb / 1024f:F1}");

            string gpu = SystemInfo.graphicsDeviceName ?? "GPU";
            string right = $"{gpu}    {string.Join("  ·  ", parts)}  GB";

            var pa2 = Text.Anchor; var pf2 = Text.Font;
            Text.Font = GameFont.Tiny; Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = new Color(0.72f, 0.72f, 0.72f);
            Widgets.Label(new Rect(x + barW + 12f, y, width - barW - 24f, 20f), right);
            GUI.color = Color.white; Text.Anchor = pa2; Text.Font = pf2;
        }

        private static string ShortLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return "model";
            return label.Length <= 14 ? label : label.Substring(0, 13) + "…";
        }

        /// <summary>A labelled bar: background, colored fill, and a left label with optional right value.</summary>
        private static void DrawBar(Rect rect, float fill, Color fillColor, string leftLabel, string rightValue = null)
        {
            Widgets.DrawBoxSolid(rect, GpuBarBg);
            if (fill > 0f)
            {
                var fr = new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01(fill), rect.height);
                Widgets.DrawBoxSolid(fr, fillColor);
            }

            var prevAnchor = Text.Anchor;
            var prevFont = Text.Font;
            Text.Font = GameFont.Tiny;

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(rect.x + 6f, rect.y, rect.width - 12f, rect.height), leftLabel);

            if (rightValue != null)
            {
                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(new Rect(rect.x + 6f, rect.y, rect.width - 12f, rect.height), rightValue);
            }

            Text.Anchor = prevAnchor;
            Text.Font = prevFont;
        }

        private static Color VramColor(float pct)
        {
            if (pct < 0.5f) return VramGreen;
            if (pct < 0.7f) return VramYellow;
            if (pct < 0.85f) return VramOrange;
            return VramRed;
        }
    }
}
