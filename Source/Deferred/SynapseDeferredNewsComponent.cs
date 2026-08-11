using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// One event held by the deferred-news pipeline: the real letter, when it was caught, and when it
    /// is due to be released. <see cref="title"/> and <see cref="category"/> are cached so the banner
    /// can read them without resolving the letter every frame.
    /// </summary>
    public class DeferredNewsEvent : IExposable
    {
        public Letter letter;
        public int interceptTick;
        public int releaseTick;
        public string category;   // "Threat" | "Quest" | "Other"
        public string title;

        public void ExposeData()
        {
            Scribe_Deep.Look(ref letter, "letter");
            Scribe_Values.Look(ref interceptTick, "interceptTick", 0);
            Scribe_Values.Look(ref releaseTick, "releaseTick", 0);
            Scribe_Values.Look(ref category, "category", "Other");
            Scribe_Values.Look(ref title, "title", "");
        }
    }

    /// <summary>
    /// The deferred-event pipeline (WorldNews#19 mechanic; owned by Core per its 0.8 charter). Holds
    /// news-worthy letters caught by <see cref="Patches.Patch_LetterStack_ReceiveLetter_Defer"/> for a
    /// configurable delay, then re-injects them so they fire "late" — the way word travels slowly on
    /// the rim. While an event is held it is visible to consumers (the WorldNews breaking-news banner)
    /// as an advance bulletin; when it releases, whatever normally happens on the letter happens then,
    /// and the newspaper records it.
    ///
    /// <para>Auto-registered by RimWorld because it has a <c>(Game)</c> constructor.</para>
    /// </summary>
    public class SynapseDeferredNewsComponent : GameComponent
    {
        public static SynapseDeferredNewsComponent Instance =>
            Verse.Current.Game?.GetComponent<SynapseDeferredNewsComponent>();

        private List<DeferredNewsEvent> pending = new List<DeferredNewsEvent>();

        // Letters we have just re-injected: the intercept prefix must let these through rather than
        // re-holding them. Not persisted — a loaded game's already-shown letters never re-enter the
        // patch, and anything still pending is re-held from the save.
        private static readonly HashSet<Letter> Released = new HashSet<Letter>();

        public SynapseDeferredNewsComponent(Game game) { }

        /// <summary>Events currently held, in intercept order. Read-only view for the banner.</summary>
        public IReadOnlyList<DeferredNewsEvent> Pending => pending;

        public bool WasReleased(Letter let) => let != null && Released.Contains(let);

        /// <summary>
        /// A sentence for LLM prompts that <em>decide</em> events, warning that what they schedule will
        /// not reach the colony immediately (WorldNews#19). Empty when deferral is off or the default
        /// delay is 0, so callers can append it unconditionally.
        /// </summary>
        public static string PromptNote()
        {
            var s = RimSynapseMod.Instance?.Settings;
            if (s == null || !s.deferNewsEnabled || s.deferDaysDefault <= 0f) return "";
            return $"Timing note: events you introduce will not reach the colony for about "
                + $"{s.deferDaysDefault:0.#} in-game day(s) — word travels slowly on the rim. A colony "
                + "with a working comms console receives an advance bulletin; without one, the colonists "
                + "only find out when the event finally arrives.";
        }

        /// <summary>Catch a letter and schedule its release.</summary>
        public void Hold(Letter let, int releaseTick, string category, string title)
        {
            if (let == null) return;
            pending.Add(new DeferredNewsEvent
            {
                letter = let,
                interceptTick = Find.TickManager?.TicksGame ?? 0,
                releaseTick = releaseTick,
                category = category,
                title = title,
            });
            SynapseLogger.Message(
                $"[RimSynapse] Deferred news held ({category}) until tick {releaseTick}: {title}");
        }

        public override void GameComponentTick()
        {
            if (pending.Count == 0) return;
            int now = Find.TickManager?.TicksGame ?? 0;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (pending[i].releaseTick <= now) Release(i);
            }
        }

        private void Release(int index)
        {
            DeferredNewsEvent ev = pending[index];
            pending.RemoveAt(index);

            Letter let = ev?.letter;
            if (let == null) return;

            Released.Add(let);
            try
            {
                // Re-injected: the defer prefix sees WasReleased and passes it straight through, so the
                // letter shows now and the normal downstream (rewrite, WorldNews event recording) runs.
                Find.LetterStack.ReceiveLetter(let);
                SynapseLogger.Message($"[RimSynapse] Deferred news released: {ev.title}");
            }
            catch (Exception e)
            {
                SynapseLogger.Warning($"[RimSynapse] Failed to release deferred news '{ev.title}': {e.Message}");
            }
        }

        public override void StartedNewGame()
        {
            base.StartedNewGame();
            SeedInitialBulletin();
        }

        /// <summary>
        /// Prime the pipeline on a fresh colony (WorldNews#19): hold one welcome dispatch so the
        /// mechanic is live from turn one — it shows as a bulletin if comms are up, or simply releases
        /// after the delay otherwise.
        /// </summary>
        private void SeedInitialBulletin()
        {
            try
            {
                if (!(RimSynapseMod.Instance?.Settings?.deferNewsEnabled ?? false)) return;

                int now = Find.TickManager?.TicksGame ?? 0;
                float days = RimSynapseMod.Instance.Settings.deferDaysDefault;
                int releaseTick = now + UnityEngine.Mathf.RoundToInt(Math.Max(0.1f, days) * GenDate.TicksPerDay);

                Letter let = LetterMaker.MakeLetter(
                    "Frontier Dispatches Online",
                    "Long-range comms chatter reaches the new settlement. The frontier press has taken "
                    + "note of your arrival, and word of your doings will now travel the rim — though it "
                    + "travels slowly without a comms console of your own.",
                    LetterDefOf.NeutralEvent);
                let.ID = Find.UniqueIDsManager.GetNextLetterID();

                Hold(let, releaseTick, "Other", "Frontier Dispatches Online");
            }
            catch (Exception e)
            {
                SynapseLogger.Warning($"[RimSynapse] Failed to seed initial bulletin: {e.Message}");
            }
        }

        /// <summary>Force every held event to release right now — debug validation of the release path.</summary>
        public void DebugReleaseAllNow()
        {
            for (int i = pending.Count - 1; i >= 0; i--) Release(i);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref pending, "synapsePendingNews", LookMode.Deep);
            if (pending == null) pending = new List<DeferredNewsEvent>();
        }
    }
}
