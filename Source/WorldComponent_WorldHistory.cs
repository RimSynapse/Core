using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// The save-backed world-history store (Core #65): the canonical record of regional incidents and
    /// their outcomes that the Storyteller reasons over and calls back to. Fed by the incident
    /// lifecycle hook (Core #64) — a start opens an entry, a first-level resolution closes it; an
    /// entry that never closes is an <b>open thread</b> resurfaced into agent context.
    ///
    /// Bounded (see <see cref="WorldHistoryCuration"/>) so save size stays sane, and inert under a
    /// non-RimSynapse storyteller: recording is gated, so a stock game keeps an empty store.
    /// </summary>
    public partial class SynapseCoreWorldComponent
    {
        public List<WorldHistoryEntry> worldHistory = new List<WorldHistoryEntry>();

        // Bounded so a long game never bloats the save; open threads are preserved over resolved
        // history by the curation policy.
        private const int MaxWorldHistoryEntries = 120;
        // How many open threads to surface into the Storyteller's context, newest first.
        private const int MaxSurfacedThreads = 5;

        /// <summary>Record the start of a regionalizable incident (from the lifecycle hook).</summary>
        public void RecordIncidentStart(string kind, string region, float magnitude, string origin, int gameTick)
        {
            if (!SynapseStorytellerContext.IsRimSynapseStorytellerActive) return; // inert under vanilla
            if (string.IsNullOrEmpty(kind)) return;
            if (worldHistory == null) worldHistory = new List<WorldHistoryEntry>();

            worldHistory.Add(new WorldHistoryEntry(kind, region, magnitude, origin, gameTick));
            CurateWorldHistory();
        }

        /// <summary>
        /// Record a first-level resolution: close the most recent still-open entry with the same
        /// kind+region. If none is open (e.g. a resolution we never saw start), a resolved entry is
        /// recorded so the history is still complete.
        /// </summary>
        public void RecordIncidentResolution(string kind, string region, string outcome, int gameTick)
        {
            if (!SynapseStorytellerContext.IsRimSynapseStorytellerActive) return;
            if (string.IsNullOrEmpty(kind)) return;
            if (worldHistory == null) worldHistory = new List<WorldHistoryEntry>();

            var open = worldHistory
                .Where(e => !e.resolved && e.kind == kind && e.region == region)
                .OrderByDescending(e => e.startTick)
                .FirstOrDefault();

            if (open != null)
            {
                open.resolved = true;
                open.resolvedTick = gameTick;
                open.outcome = outcome;
            }
            else
            {
                worldHistory.Add(new WorldHistoryEntry(kind, region, 0f, "", gameTick)
                {
                    resolved = true,
                    resolvedTick = gameTick,
                    outcome = outcome,
                });
            }
            CurateWorldHistory();
        }

        /// <summary>Query the store by region/kind/time. Null filters match anything.</summary>
        public IEnumerable<WorldHistoryEntry> QueryWorldHistory(string region = null, string kind = null, int sinceTick = 0)
        {
            if (worldHistory == null) return Enumerable.Empty<WorldHistoryEntry>();
            return worldHistory.Where(e =>
                (region == null || e.region == region) &&
                (kind == null || e.kind == kind) &&
                e.LastTick >= sinceTick);
        }

        /// <summary>Unresolved incidents — the open threads the Storyteller may still call back to.</summary>
        public IEnumerable<WorldHistoryEntry> OpenThreads()
            => worldHistory?.Where(e => !e.resolved) ?? Enumerable.Empty<WorldHistoryEntry>();

        /// <summary>
        /// A compact context block naming the most recent open threads for the Storyteller's prompt.
        /// Empty when there are none (or when dormant), so callers can append unconditionally.
        /// </summary>
        public string WorldHistoryContextBlock()
        {
            if (!SynapseStorytellerContext.IsRimSynapseStorytellerActive) return string.Empty;
            var open = OpenThreads().OrderByDescending(e => e.startTick).Take(MaxSurfacedThreads).ToList();
            if (open.Count == 0) return string.Empty;

            int now = Find.TickManager?.TicksGame ?? 0;
            var sb = new StringBuilder();
            sb.AppendLine("Open world threads (unresolved events the colony still remembers — you may call back to them):");
            foreach (var e in open)
            {
                float daysAgo = (now - e.startTick) / 60000f;
                sb.AppendLine($"- {e.kind} in {e.region}, {daysAgo:0.0} days ago, still unresolved.");
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Enforce the store bound, preferring to drop settled history over open threads.</summary>
        private void CurateWorldHistory()
        {
            if (worldHistory == null) return;
            while (worldHistory.Count > MaxWorldHistoryEntries)
            {
                var resolvedFlags = worldHistory.Select(e => e.resolved).ToArray();
                var ticks = worldHistory.Select(e => e.LastTick).ToArray();
                int victim = WorldHistoryCuration.SelectEvictionIndex(resolvedFlags, ticks, worldHistory.Count, MaxWorldHistoryEntries);
                if (victim < 0) break;
                worldHistory.RemoveAt(victim);
            }
        }
    }
}
