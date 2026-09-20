using System;
using System.Collections.Generic;
using System.Linq;
using RimSynapse.Comps;
using RimSynapse.Models;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimSynapse.UI
{
    /// <summary>
    /// Manual memory curator (Core #138): inspect, hand-remove, and hand-add a pawn's memories
    /// (<see cref="SynapseCorePawnComp.memories"/>). Built for correcting a live save whose memory
    /// banks have accumulated noise, and for exercising the memory feature set interactively. Removal
    /// is a deliberate manual override — the protected/long-term flag is shown so the curator can
    /// judge, but nothing is off-limits. Adds go through the canonical <see cref="SynapseCorePawnComp.AddMemory"/>
    /// path, so coalescing (#129) and indexing behave exactly as in normal play.
    /// </summary>
    public class Dialog_MemoryCurator : Window
    {
        private readonly Pawn pawn;
        private readonly SynapseCorePawnComp comp;
        private Vector2 scroll;

        // Add-memory buffers.
        private string addSummary = "";
        private string addType = "manual";
        private string addWeightBuf = "0.5";
        private bool addLongTerm;

        // Bulk-prune buffer.
        private string pruneBelowBuf = "0.1";

        public Dialog_MemoryCurator(Pawn pawn)
        {
            this.pawn = pawn;
            this.comp = pawn?.GetComp<SynapseCorePawnComp>();
            forcePause = true;
            doCloseX = true;
            doCloseButton = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
            draggable = true;
        }

        public override Vector2 InitialSize => new Vector2(780f, 740f);

        public override void DoWindowContents(Rect inRect)
        {
            if (comp == null)
            {
                Widgets.Label(inRect, "This pawn has no RimSynapse memory component.");
                return;
            }

            float y = 0f;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, y, inRect.width, 34f), $"Memory Curator — {pawn.LabelShortCap}");
            Text.Font = GameFont.Small;
            y += 34f;
            Widgets.Label(new Rect(0, y, inRect.width, 22f),
                $"{comp.memories.Count} memories  ·  {comp.memories.Count(m => m.isLongTerm)} long-term  ·  " +
                $"{comp.memories.Count(m => comp.IsCompactionProtected(m))} protected");
            y += 26f;

            // ── Add / bulk-prune controls ──────────────────────────────────
            var toolRect = new Rect(0, y, inRect.width, 58f);
            Widgets.DrawMenuSection(toolRect);
            var t = toolRect.ContractedBy(6f);
            // Row 1: add.
            float rx = t.x;
            Widgets.Label(new Rect(rx, t.y, 70f, 24f), "Add:"); rx += 40f;
            addSummary = Widgets.TextField(new Rect(rx, t.y, 300f, 24f), addSummary); rx += 306f;
            Widgets.Label(new Rect(rx, t.y, 36f, 24f), "type"); rx += 32f;
            addType = Widgets.TextField(new Rect(rx, t.y, 90f, 24f), addType); rx += 96f;
            Widgets.Label(new Rect(rx, t.y, 26f, 24f), "wt"); rx += 24f;
            addWeightBuf = Widgets.TextField(new Rect(rx, t.y, 44f, 24f), addWeightBuf); rx += 50f;
            Widgets.CheckboxLabeled(new Rect(rx, t.y, 74f, 24f), "LT", ref addLongTerm); rx += 78f;
            if (Widgets.ButtonText(new Rect(t.xMax - 70f, t.y, 70f, 24f), "Add"))
                DoAdd();
            // Row 2: bulk prune.
            float py = t.y + 30f;
            Widgets.Label(new Rect(t.x, py, 150f, 24f), "Prune all below weight");
            pruneBelowBuf = Widgets.TextField(new Rect(t.x + 150f, py, 50f, 24f), pruneBelowBuf);
            if (Widgets.ButtonText(new Rect(t.x + 210f, py, 130f, 24f), "Prune (unprotected)"))
                DoBulkPrune();
            y = toolRect.yMax + 8f;

            // ── Memory list ────────────────────────────────────────────────
            var ordered = comp.memories.OrderByDescending(m => m.weight).ToList();
            const float rowH = 46f;
            var outRect = new Rect(0, y, inRect.width, inRect.height - y - 40f);
            var viewRect = new Rect(0, 0, outRect.width - 16f, Math.Max(ordered.Count * rowH, outRect.height));
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);

            WeightedMemory toRemove = null;
            long nowAbs = Find.TickManager?.TicksAbs ?? 0;
            for (int i = 0; i < ordered.Count; i++)
            {
                var m = ordered[i];
                var row = new Rect(0, i * rowH, viewRect.width, rowH - 2f);
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                if (i % 2 == 1) Widgets.DrawLightHighlight(row);

                // Weight chip (colour by magnitude).
                var wRect = new Rect(row.x + 4f, row.y + 6f, 46f, row.height - 12f);
                GUI.color = Color.Lerp(new Color(0.6f, 0.3f, 0.3f), new Color(0.3f, 0.7f, 0.4f), Mathf.Clamp01(m.weight));
                Widgets.DrawBoxSolid(wRect, GUI.color * 0.35f);
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(wRect, m.weight.ToString("0.00"));
                Text.Anchor = TextAnchor.UpperLeft;

                // Summary + meta.
                var textRect = new Rect(wRect.xMax + 6f, row.y + 2f, row.width - wRect.width - 44f, row.height - 4f);
                string flags = (m.isLongTerm ? " [LT]" : "") + (comp.IsCompactionProtected(m) ? " [prot]" : "");
                float ageDays = (nowAbs - m.absTick) / 60000f;
                string tags = (m.tags != null && m.tags.Count > 0) ? "  #" + string.Join(" #", m.tags.Take(4)) : "";
                Widgets.Label(new Rect(textRect.x, textRect.y, textRect.width, 22f),
                    (m.summary ?? "(no summary)").Truncate(textRect.width));
                GUI.color = new Color(0.7f, 0.7f, 0.7f);
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(textRect.x, textRect.y + 22f, textRect.width, 20f),
                    $"{m.memoryType}{flags}  ·  {ageDays:0.0}d  ·  refs {m.timesReferenced}  ·  sal {m.salience:0.0}{tags}");
                Text.Font = GameFont.Small;
                GUI.color = Color.white;

                // Remove button.
                var xRect = new Rect(row.xMax - 40f, row.y + 8f, 34f, row.height - 16f);
                if (Widgets.ButtonText(xRect, "✕"))
                    toRemove = m;
            }
            Widgets.EndScrollView();

            if (toRemove != null)
            {
                comp.RemoveMemory(toRemove);
                Messages.Message($"Removed memory: \"{toRemove.summary?.Truncate(50f)}\"", MessageTypeDefOf.TaskCompletion, false);
            }
        }

        private void DoAdd()
        {
            if (addSummary.NullOrEmpty()) { Messages.Message("Enter a summary to add a memory.", MessageTypeDefOf.RejectInput, false); return; }
            if (!float.TryParse(addWeightBuf, out float w)) w = 0.5f;
            w = Mathf.Clamp(w, 0f, 1f);
            var mem = new WeightedMemory
            {
                summary = addSummary.Trim(),
                memoryType = addType.NullOrEmpty() ? "manual" : addType.Trim(),
                weight = w,
                baseWeight = w,
                isLongTerm = addLongTerm,
                absTick = Find.TickManager?.TicksAbs ?? 0,
                gameTick = Find.TickManager?.TicksGame ?? 0,
            };
            int before = comp.memories.Count;
            comp.AddMemory(mem);
            bool coalesced = comp.memories.Count == before;
            Messages.Message(coalesced
                    ? "Memory coalesced into an existing near-duplicate."
                    : $"Added memory (weight {w:0.00}).",
                MessageTypeDefOf.TaskCompletion, false);
            addSummary = "";
        }

        private void DoBulkPrune()
        {
            if (!float.TryParse(pruneBelowBuf, out float threshold)) { Messages.Message("Enter a numeric weight.", MessageTypeDefOf.RejectInput, false); return; }
            var victims = comp.memories.Where(m => m.weight < threshold && !comp.IsCompactionProtected(m)).ToList();
            if (victims.Count == 0) { Messages.Message($"No unprotected memories below weight {threshold:0.00}.", MessageTypeDefOf.RejectInput, false); return; }
            foreach (var v in victims) comp.RemoveMemory(v);
            Messages.Message($"Pruned {victims.Count} unprotected memories below weight {threshold:0.00}.", MessageTypeDefOf.TaskCompletion, false);
        }
    }
}
