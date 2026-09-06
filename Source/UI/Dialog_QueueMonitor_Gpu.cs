using UnityEngine;
using Verse;

namespace RimSynapse.UI
{
    /// <summary>
    /// The GPU/VRAM column (Core #128) — the monitor's always-present left column. Shows the GPU name,
    /// a measured VRAM usage bar (from <see cref="VramMeter"/>), and a textual component breakdown
    /// (from <see cref="VramBreakdown"/>): RimWorld, LM Studio, in-process consumers, system, free.
    /// In Basic view it is the whole window; in Advanced the LLM-call view is drawn to its right.
    /// When the meter is unsupported (integrated GPU / non-Windows) it says so and shows the component
    /// estimates only.
    /// </summary>
    public partial class Dialog_QueueMonitor
    {
        internal const float VramColWidth = 360f;

        private static readonly Color GpuBarBg = new Color(0.16f, 0.16f, 0.19f, 0.9f);
        private static readonly Color VramGreen = new Color(0.40f, 0.80f, 0.45f);
        private static readonly Color VramYellow = new Color(0.90f, 0.80f, 0.30f);
        private static readonly Color VramOrange = new Color(0.95f, 0.60f, 0.25f);
        private static readonly Color VramRed = new Color(0.90f, 0.35f, 0.35f);
        private static readonly Color RowDim = new Color(0.72f, 0.72f, 0.72f);

        /// <summary>Number of textual breakdown rows the column will draw — used to size the Basic window.</summary>
        internal static int VramRowCount()
        {
            VramBreakdown.Refresh();
            return 2 + VramBreakdown.Consumers.Count + (VramBreakdown.Measured ? 2 : 1);
        }

        private void DrawVramColumn(Rect area)
        {
            VramBreakdown.Refresh();

            float x = area.x + 6f;
            float w = area.width - 12f;
            float y = area.y + 6f;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(x, y, w, 28f), "GPU / VRAM");
            Text.Font = GameFont.Small;
            y += 30f;

            GUI.color = RowDim;
            Widgets.Label(new Rect(x, y, w, 20f), SystemInfo.graphicsDeviceName ?? "Unknown GPU");
            GUI.color = Color.white;
            y += 22f;

            float totalMb = VramMeter.TotalMb > 0f ? VramMeter.TotalMb : SystemInfo.graphicsMemorySize;

            if (VramMeter.Supported && totalMb > 0f)
            {
                float used = VramMeter.UsedMb;
                float pct = Mathf.Clamp01(used / totalMb);
                DrawBar(new Rect(x, y, w, 22f), pct, VramColor(pct),
                    $"VRAM {used / 1024f:F1} / {totalMb / 1024f:F1} GB", $"{pct:P0}");
                y += 28f;
            }
            else
            {
                GUI.color = new Color(0.85f, 0.75f, 0.4f);
                Widgets.Label(new Rect(x, y, w, 20f),
                    totalMb > 0f ? $"VRAM {totalMb / 1024f:F1} GB total · measured n/a (iGPU)"
                                 : "VRAM not detected");
                GUI.color = Color.white;
                y += 24f;
            }

            y += 2f;
            DrawTextRow(x, ref y, w, "RimWorld", GbStr(VramBreakdown.RimWorldMb));
            DrawTextRow(x, ref y, w,
                VramBreakdown.LmStudioRemote ? "LM Studio (remote)" : "LM Studio model",
                VramBreakdown.LmStudioRemote ? "—" : GbStr(VramBreakdown.LmStudioMb));
            foreach (var c in VramBreakdown.Consumers)
                DrawTextRow(x, ref y, w, c.label ?? "in-process model", GbStr(c.vramMb));

            if (VramBreakdown.Measured)
            {
                DrawTextRow(x, ref y, w, "System / desktop", GbStr(VramBreakdown.SystemMb));
                y += 2f;
                float freeMb = VramMeter.FreeMb;
                float freePctUsed = totalMb > 0f ? 1f - Mathf.Clamp01(freeMb / totalMb) : 0f;
                DrawTextRow(x, ref y, w, "Free", GbStr(freeMb), VramColor(freePctUsed));
            }
            else
            {
                GUI.color = RowDim;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(x, y, w, 18f), "(estimates — no measured GPU counter)");
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                y += 18f;
            }
        }

        private static string GbStr(float mb) => mb > 0.5f ? $"{mb / 1024f:F1} GB" : "—";

        private static void DrawTextRow(float x, ref float y, float w, string label, string value, Color? valueColor = null)
        {
            var prevAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(x, y, w * 0.55f, 20f), label);

            Text.Anchor = TextAnchor.MiddleRight;
            if (valueColor.HasValue) GUI.color = valueColor.Value;
            Widgets.Label(new Rect(x + w * 0.35f, y, w * 0.65f, 20f), value);
            GUI.color = Color.white;

            Text.Anchor = prevAnchor;
            y += 20f;
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
