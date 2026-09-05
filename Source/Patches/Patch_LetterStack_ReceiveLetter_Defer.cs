using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimSynapse.Patches
{
    /// <summary>
    /// The intercept end of the deferred-news pipeline (WorldNews#19). Runs before every other
    /// <c>ReceiveLetter</c> patch (<see cref="HarmonyPriority"/> First): a deferrable letter is caught
    /// and handed to <see cref="SynapseDeferredNewsComponent"/> to release later, and the vanilla call
    /// (and the rewrite prefix behind it) is blocked. When the component re-injects the letter on
    /// release, this prefix recognises it and lets it through so it shows and records normally.
    ///
    /// <para>Only world-scoped letters are candidates at all (Core#123): anything witnessed on the
    /// player's own maps is Local and always passes straight through — word only "travels slowly" from
    /// elsewhere. Beyond that, delay is per-category and player-configurable; a category whose delay is
    /// 0 passes through — that is how combat threats stay immediate by default, so raiders are never
    /// silent (and a threat on the player's own map is immediate regardless of the slider).</para>
    /// </summary>
    [HarmonyPatch(typeof(LetterStack), "ReceiveLetter",
        new Type[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) })]
    public static class Patch_LetterStack_ReceiveLetter_Defer
    {
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(Letter let)
        {
            if (let == null) return true;

            var mgr = SynapseDeferredNewsComponent.Instance;
            if (mgr == null) return true;                 // no game / component yet
            if (mgr.WasReleased(let)) return true;        // re-injection on release → let it show

            var settings = RimSynapseMod.Instance?.Settings;
            if (settings == null || !settings.deferNewsEnabled) return true;

            if (!(let is ChoiceLetter cl)) return true;   // only structured letters are news
            if (IsExcluded(let)) return true;

            // The locality gate (Core#123): "word travels slowly" only applies to news from
            // elsewhere. A colony-local letter — anything witnessed on the player's own maps —
            // passes through immediately, always; only world-scoped and quest letters can defer.
            NewsScope scope = DeferredNewsUtility.ClassifyScope(let);
            if (scope == NewsScope.Local) return true;

            string category = Categorize(let, scope);
            float days = DelayDaysFor(settings, category);
            if (days <= 0f) return true;                  // immediate category (e.g. Threats by default)

            int now = Find.TickManager?.TicksGame ?? 0;
            int releaseTick = now + Mathf.RoundToInt(days * GenDate.TicksPerDay);
            string title = SafeResolve(let.Label);

            mgr.Hold(let, releaseTick, category, title);
            return false;                                 // hold it; released later
        }

        /// <summary>Letters the pipeline must never swallow: our own published newspaper, and anything
        /// already flagged released (handled above). Extend this as new internal letters appear.</summary>
        private static bool IsExcluded(Letter let)
        {
            string label = SafeResolve(let.Label);
            if (!string.IsNullOrEmpty(label) && label.StartsWith("Newspaper Published:")) return true;
            return false;
        }

        /// <summary>Category for an already world-scoped letter (Local never reaches here — the gate
        /// passed it through). The threat slider therefore only governs *distant* threats; a threat on
        /// the player's own map is witnessed and always immediate, whatever the slider says.</summary>
        private static string Categorize(Letter let, NewsScope scope)
        {
            if (let.def == LetterDefOf.ThreatBig || let.def == LetterDefOf.ThreatSmall) return "Threat";
            if (scope == NewsScope.Quest) return "Quest";
            return "Other";
        }

        private static float DelayDaysFor(RimSynapseSettings s, string category)
        {
            switch (category)
            {
                case "Threat": return s.deferDaysThreat;
                case "Quest": return s.deferDaysQuest;
                default: return s.deferDaysDefault;
            }
        }

        private static string SafeResolve(TaggedString s)
        {
            try { return s.Resolve(); } catch { return s.RawText ?? ""; }
        }
    }
}
