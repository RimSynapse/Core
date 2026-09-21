using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Verse;

namespace RimSynapse
{
    /// <summary>One world-event notification an external source pushed to Core (#135). Core-internal;
    /// producers never see this type — they publish via the primitives-only surface on
    /// <see cref="SynapseCoreContext.PublishWorldEvent"/>.</summary>
    public class SynapseWorldEvent
    {
        public string kind;       // e.g. "plague", "raid", "SolarFlare" — matched against incident defNames
        public string region;     // coarse where, for flavour
        public float magnitude;   // severity / scale, for weighting
        public string origin;     // faction or source name, or ""
        public string summary;    // one-line human description for the selection prompt
        public int expiryTick;    // absolute tick after which this event no longer reaches the colony
    }

    /// <summary>
    /// The additive, outbound world-event model (Core #135): external sources (WorldNews, World
    /// Domination, …) publish world-event notifications; the LLM storyteller consumes the pending set
    /// as incident-selection context and weight, and may manifest a qualifying one AT the colony. It
    /// never suppresses or rewrites vanilla — with no source publishing, the inbox is empty and the
    /// storyteller behaves exactly as stock (source-gated).
    ///
    /// <para>Publishers reach this only through the primitives-only broadcast surface
    /// <see cref="SynapseCoreContext.PublishWorldEvent"/>, so a producer builds and runs with Core
    /// absent. Everything here is Core-internal.</para>
    /// </summary>
    public static class SynapseWorldEventInbox
    {
        private const int DefaultTtlTicks = 120000;  // ~2 in-game days
        private const int MaxBuffered = 64;
        private static readonly List<SynapseWorldEvent> _events = new List<SynapseWorldEvent>();

        private static int CurrentTick() => Find.TickManager?.TicksAbs ?? 0;

        /// <summary>Publish from the reflection surface (now/ttl resolved from the live game).</summary>
        internal static void Publish(string kind, string region, float magnitude, string origin, string summary, int ttlTicks)
            => Publish(kind, region, magnitude, origin, summary, ttlTicks, CurrentTick());

        /// <summary>Publish with an explicit clock — the test seam. A blank kind is ignored (nothing to
        /// match on). ttlTicks &lt;= 0 falls back to the default horizon.</summary>
        internal static void Publish(string kind, string region, float magnitude, string origin, string summary, int ttlTicks, int nowTick)
        {
            if (kind.NullOrEmpty()) return;
            int ttl = ttlTicks > 0 ? ttlTicks : DefaultTtlTicks;
            _events.Add(new SynapseWorldEvent
            {
                kind = kind, region = region ?? "", magnitude = magnitude,
                origin = origin ?? "", summary = summary ?? "", expiryTick = nowTick + ttl,
            });
            Prune(nowTick);
            // Cap the buffer (drop oldest by expiry) so a spammy source can't grow it unbounded.
            while (_events.Count > MaxBuffered)
                _events.RemoveAt(0);
        }

        /// <summary>The world events still reaching the colony at <paramref name="nowTick"/>. Prunes
        /// expired entries as a side effect. Empty when no source has published (the gate).</summary>
        public static List<SynapseWorldEvent> Pending(int nowTick)
        {
            Prune(nowTick);
            return _events.ToList();
        }

        private static void Prune(int nowTick) => _events.RemoveAll(e => nowTick >= e.expiryTick);

        /// <summary>The additive selection-context block, or empty when nothing is pending (source-gated,
        /// so the storyteller stays vanilla). Appended to the incident-selection system prompt.</summary>
        public static string SelectionContextNote(int nowTick)
        {
            var pending = Pending(nowTick);
            if (pending.Count == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine("World events reaching the colony (an external source is active). You MAY manifest one AT the colony as an incident if it plausibly fits — this is additive; never suppress vanilla events:");
            foreach (var e in pending)
            {
                string where = e.region.NullOrEmpty() ? "" : $" near {e.region}";
                string who = e.origin.NullOrEmpty() ? "" : $" ({e.origin})";
                string desc = e.summary.NullOrEmpty() ? e.kind : e.summary;
                sb.AppendLine($"- [{e.kind}]{where}{who}: {desc}");
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Weight multiplier for an incident def, boosted when a pending world event's kind
        /// matches it (either name contains the other, case-insensitive). 1.0 when nothing matches, so
        /// the storyteller's normal weighting is untouched with no source. Magnitude nudges the boost.</summary>
        public static float WeightBoostFor(string incidentDefName, int nowTick)
        {
            if (incidentDefName.NullOrEmpty()) return 1f;
            float boost = 1f;
            foreach (var e in Pending(nowTick))
            {
                if (KindMatches(e.kind, incidentDefName))
                    boost *= 1.5f + Mathf.Clamp(e.magnitude, 0f, 4f) * 0.25f;
            }
            return boost;
        }

        private static bool KindMatches(string kind, string defName)
        {
            if (kind.NullOrEmpty() || defName.NullOrEmpty()) return false;
            string k = kind.ToLowerInvariant();
            string d = defName.ToLowerInvariant();
            return d.Contains(k) || k.Contains(d);
        }

        /// <summary>Drop everything. Save-scoped reset hook and test seam.</summary>
        public static void Clear() => _events.Clear();
    }
}
