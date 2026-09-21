using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using RimSynapse.Models;

namespace RimSynapse.Comps
{
    /// <summary>
    /// Automatic memory compaction (Core #131). Folds a pawn's UNREMARKABLE memories into fewer, richer
    /// first-person memories on a short-horizon ladder, so the boring stuff becomes background texture
    /// instead of noise. Significant memories are never touched — only referenced.
    ///
    /// This partial holds the automatic tiers only: <b>night</b> (fold a day's compactable memories → a
    /// <c>Compacted_Day</c>) and <b>pentad</b> (every 5th day, fold the <c>Compacted_Day</c>s → a
    /// <c>Compacted_Pentad</c>). Immediate first-person prose, no dates, no player trigger. The long-horizon
    /// narrative tiers (quadrum/year, dated) and the ad-hoc button are tabled (Core #133).
    ///
    /// Ordering: Psychology's nightly review reads "today's" memories on its OWN independent daily tick, so
    /// compaction can't be sequenced after it within a comp. Instead the night fold only ever touches
    /// memories already older than the short-term window (<see cref="CompactionMinAgeTicks"/>) — the review
    /// has long since seen them, so it never reads a fold in place of the raw day. Selection is deterministic
    /// C# here; the LLM only writes the sentence, on the main-thread apply.
    /// </summary>
    public partial class SynapseCorePawnComp
    {
        private const int TicksPerCompactionDay = 60000;
        private const float PentadWeightFactor = 1.05f;

        /// <summary>Absolute game-day (GenDate.DaysPassed) the last pentad fold ran, so the 5-day cadence
        /// survives save/load. -1 until the first pass seeds it.</summary>
        private int lastPentadDay = -1;

        /// <summary>MemIds selected for an in-flight fold, so a second maintenance pass doesn't re-select the
        /// same sources while the LLM call is out. Not scribed — an in-flight fold dies with the session and
        /// its sources simply become eligible again next pass.</summary>
        private HashSet<string> pendingCompaction; // not scribed — in-flight only

        private HashSet<string> Pending => pendingCompaction ?? (pendingCompaction = new HashSet<string>());

        private static RimSynapseSettings CompSettings => RimSynapseMod.Instance?.Settings;

        /// <summary>Only fold memories older than the short-term window, so Psychology's review (a 1-day
        /// window) has always seen the raw memories before they are compacted.</summary>
        private static int CompactionMinAgeTicks
            => (int)((CompSettings?.shortTermMemoryHours ?? 48f) * 2500f);

        private static int MinMemoriesToFold => Math.Max(2, CompSettings?.compactionMinMemories ?? 3);

        // ── Called from RunMemoryMaintenance (the per-pawn daily tick) ───────────────────────────────
        internal void MaybeCompact()
        {
            var s = CompSettings;
            if (s != null && !s.enableMemoryCompaction) return;
            if (parent is not Pawn pawn || !pawn.Spawned || pawn.Dead) return;

            long nowAbs = Find.TickManager != null ? Find.TickManager.TicksAbs : 0L;
            int today = GenDate.DaysPassed;
            if (lastPentadDay < 0) lastPentadDay = today; // seed without firing on the very first pass

            // Pentad first: it consumes Compacted_Days, so running it before tonight's night fold keeps the
            // day's fresh Compacted_Day out of this pentad (it ages one cycle before it can be folded up).
            if ((s == null || s.compactPentad) && today - lastPentadDay >= 5)
            {
                if (TryFoldPentad(nowAbs, force: false)) lastPentadDay = today;
                else lastPentadDay = today; // nothing to fold this window; don't retry every day
            }

            if (s == null || s.compactNightly) TryFoldNight(nowAbs, force: false);
        }

        // ── Protection ───────────────────────────────────────────────────────────────────────────────
        /// <summary>A memory that must never be compacted: already long-term / consolidated, a pivotal life
        /// event, or carrying a significant tag. A fold references these (via linkedMemoryIds) but never
        /// absorbs them, so "I fell in love" can still point at the loss it grew out of.</summary>
        internal bool IsCompactionProtected(WeightedMemory m)
        {
            if (m == null) return true;
            if (m.isLongTerm) return true;
            if (m.salience >= ConsolidationThreshold) return true;
            if (m.tags != null)
            {
                foreach (var t in m.tags)
                {
                    if (string.IsNullOrEmpty(t)) continue;
                    if (SignificantTags.Contains(t)) return true;
                    if (t.StartsWith(SynapsePivotalMemory.PivotalTagPrefix, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        private static bool IsCompacted(WeightedMemory m)
            => m?.memoryType != null && m.memoryType.StartsWith("Compacted_", StringComparison.OrdinalIgnoreCase);

        // ── Night tier: fold one day's raw memories ────────────────────────────────────────────────────
        /// <summary>The oldest fully-elapsed day whose raw, unprotected, aged-out memories number at least
        /// the fold minimum. Empty list when nothing is ready. <paramref name="force"/> ignores the min-age
        /// gate (debug only).</summary>
        internal List<WeightedMemory> SelectNightSources(long nowAbs, bool force)
        {
            var eligible = memories.Where(m => m != null
                && !string.IsNullOrEmpty(m.summary)
                && !IsCompacted(m)
                && !IsCompactionProtected(m)
                && !Pending.Contains(m.memId ?? "")
                && (force || nowAbs - m.absTick > CompactionMinAgeTicks));

            var byDay = eligible.GroupBy(m => (int)(m.absTick / TicksPerCompactionDay))
                                .Where(g => g.Count() >= MinMemoriesToFold)
                                .OrderBy(g => g.Key)
                                .FirstOrDefault();
            return byDay?.OrderBy(m => m.absTick).ToList() ?? new List<WeightedMemory>();
        }

        internal bool TryFoldNight(long nowAbs, bool force)
        {
            var sources = SelectNightSources(nowAbs, force);
            if (sources.Count < MinMemoriesToFold) return false;
            QueueFold(sources, "Compacted_Day", CompactionTier.Day);
            return true;
        }

        // ── Pentad tier: fold the aged Compacted_Days ────────────────────────────────────────────────────
        internal List<WeightedMemory> SelectPentadSources(long nowAbs, bool force)
        {
            // A Compacted_Day must age one day before it can be folded up, so tonight's fresh one is spared.
            return memories.Where(m => m != null
                    && m.memoryType == "Compacted_Day"
                    && !string.IsNullOrEmpty(m.summary)
                    && !IsCompactionProtected(m)
                    && !Pending.Contains(m.memId ?? "")
                    && (force || nowAbs - m.absTick > TicksPerCompactionDay))
                .OrderBy(m => m.absTick).ToList();
        }

        internal bool TryFoldPentad(long nowAbs, bool force)
        {
            var sources = SelectPentadSources(nowAbs, force);
            if (sources.Count < 2) return false;
            QueueFold(sources, "Compacted_Pentad", CompactionTier.Pentad);
            return true;
        }

        private enum CompactionTier { Day, Pentad }

        // ── The fold: deterministic select → async prose → main-thread apply ─────────────────────────────
        private void QueueFold(List<WeightedMemory> sources, string tierType, CompactionTier tier)
        {
            foreach (var m in sources) { m.EnsureMemId(); Pending.Add(m.memId); }
            var sourceIds = sources.Select(m => m.memId).ToList();

            string system =
                "You compress several of one person's memories into ONE shorter first-person memory, in their " +
                "own voice. Keep what matters, drop the trivial, and NEVER invent events that aren't in the list. " +
                "Plain, everyday spoken language — this is how they remember it, not a report. " +
                "Return STRICTLY valid JSON and nothing else: {\"memory\": \"...\"}.";

            string voice = !string.IsNullOrEmpty(voiceProfile) ? $"They speak like this: {voiceProfile.Trim()}\n" : "";
            string list = string.Join("\n", sources.Select(m => "- " + m.summary.Trim()));
            string altitude = tier == CompactionTier.Day
                ? "These are from one day. Write it as they'd think of it that night — immediate, in the moment. One or two sentences."
                : "These are from the last several days. Write it as they'd sum it up looking back over those days — a step removed, no specific dates. One or two sentences.";
            string user = $"{voice}Their memories:\n{list}\n\n{altitude} Return the JSON now.";

            var owner = parent as Pawn;
            string ownerName = owner?.Name?.ToStringShort ?? "pawn";

            SynapseClient.PromptAsync(
                RimSynapseMod.ModHandle,
                system, user,
                result =>
                {
                    string prose = result != null && result.success ? ExtractMemoryProse(result.content) : null;
                    SynapseGameComponent.Enqueue(() => ApplyFold(sourceIds, prose, tierType));
                },
                new ChatOptions
                {
                    priority = 3,
                    thinking = false,
                    requestName = tier == CompactionTier.Day ? "Memory compaction (day)" : "Memory compaction (pentad)",
                    targetName = "Pawn: " + ownerName
                });
        }

        /// <summary>Main-thread apply. Re-resolves sources by memId (some may have decayed/coalesced since
        /// selection) and only proceeds with a real fold. On failure NOTHING is removed — the sources clear
        /// their pending flag and become eligible again next pass.</summary>
        internal void ApplyFold(List<string> sourceIds, string prose, string tierType)
        {
            foreach (var id in sourceIds) Pending.Remove(id);
            if (sourceIds == null || sourceIds.Count == 0) return;

            var byId = new Dictionary<string, WeightedMemory>();
            foreach (var m in memories) if (!string.IsNullOrEmpty(m.memId)) byId[m.memId] = m;

            var live = sourceIds.Where(byId.ContainsKey).Select(id => byId[id])
                                .Where(m => !IsCompactionProtected(m)).ToList();
            if (string.IsNullOrWhiteSpace(prose) || live.Count < 2)
            {
                if (string.IsNullOrWhiteSpace(prose))
                    SynapseLogger.Warning($"[RimSynapse] Compaction ({tierType}) produced no prose for {(parent as Pawn)?.LabelShort}; sources left intact.", "core");
                return;
            }

            var tags = new List<string>();
            var subjects = new List<string>();
            var involved = new List<string>();
            var witnesses = new List<string>();
            var links = new List<string>();
            long maxTick = 0L;
            float maxWeight = 0f;
            void AddAll(List<string> dst, List<string> src) { if (src != null) foreach (var x in src) if (!string.IsNullOrEmpty(x) && !dst.Contains(x)) dst.Add(x); }
            foreach (var m in live)
            {
                AddAll(tags, m.tags); AddAll(subjects, m.subjectPawnIds);
                AddAll(involved, m.involvedPawnIds); AddAll(witnesses, m.witnessPawnIds);
                AddAll(links, m.linkedMemoryIds);          // keep the sources' links to protected memories alive
                if (m.absTick > maxTick) maxTick = m.absTick;
                if (m.weight > maxWeight) maxWeight = m.weight;
            }

            float factor = tierType == "Compacted_Pentad" ? PentadWeightFactor : 1f;
            var folded = new WeightedMemory
            {
                summary = prose.Trim(),
                memoryType = tierType,
                absTick = maxTick,
                weight = Math.Min(1f, maxWeight * factor),
                baseWeight = Math.Min(1f, maxWeight * factor),
                tags = tags,
                subjectPawnIds = subjects,
                involvedPawnIds = involved,
                witnessPawnIds = witnesses,
                linkedMemoryIds = links
            };

            // Remove the folded sources (the compacted memory IS their new home), then add the fold. Remove
            // by descending index so the shifting list stays correct.
            var liveIds = new HashSet<string>(live.Select(m => m.memId));
            for (int i = memories.Count - 1; i >= 0; i--)
                if (!string.IsNullOrEmpty(memories[i].memId) && liveIds.Contains(memories[i].memId)) RemoveMemoryAt(i);

            AddMemory(folded);
            SynapseLogger.Message($"[RimSynapse] Compaction ({tierType}) folded {live.Count} memories on {(parent as Pawn)?.LabelShort}: \"{Trunc(folded.summary, 80)}\"", "core");
        }

        private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");

        /// <summary>Pull the memory string out of {"memory":"..."} — tolerant of the wrapping small models add.</summary>
        internal static string ExtractMemoryProse(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;
            try
            {
                int a = content.IndexOf('{'); int b = content.LastIndexOf('}');
                string json = (a >= 0 && b > a) ? content.Substring(a, b - a + 1) : content;
                var jo = Newtonsoft.Json.Linq.JObject.Parse(json);
                string v = jo.Value<string>("memory");
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            }
            catch { return null; }
        }
    }
}
