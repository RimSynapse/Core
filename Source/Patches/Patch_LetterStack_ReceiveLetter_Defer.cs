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
    /// <para>Delay is per-category and player-configurable. A category whose delay is 0 passes straight
    /// through — that is how combat threats stay immediate by default, so raiders are never silent.</para>
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

            string category = Categorize(let, cl);
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

        private static string Categorize(Letter let, ChoiceLetter cl)
        {
            if (let.def == LetterDefOf.ThreatBig || let.def == LetterDefOf.ThreatSmall) return "Threat";
            if (cl.quest != null) return "Quest";
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
