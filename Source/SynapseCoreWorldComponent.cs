using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using RimSynapse.Models;

namespace RimSynapse
{
    public partial class SynapseCoreWorldComponent : WorldComponent
    {
        public List<NarrativeThread> narrativeThreads = new List<NarrativeThread>();
        public List<FactionRelationshipTracker> factionTrackers = new List<FactionRelationshipTracker>();
        public List<ShortTermEvent> shortTermEvents = new List<ShortTermEvent>();
        public List<PawnEventRecord> pawnEventRecords = new List<PawnEventRecord>();
        public Dictionary<string, string> steleEventLinks = new Dictionary<string, string>();
        public bool colonyStartGenerated = false;
        
        public List<PastEvent> backlogQueueList = new List<PastEvent>();
        // Episode-coalescing backlog (Core#88). A List (not a Queue) so we can merge repeats and dequeue
        // only settled episodes. All mutation is MAIN-THREAD ONLY (enqueue from Harmony postfixes; the
        // async narration callback marshals through SynapseGameComponent.Enqueue before touching this).
        private List<PastEvent> _backlog = new List<PastEvent>();
        // Open (unsettled, still-coalescing) episodes keyed by CoalesceKey. Transient — not saved;
        // rebuilt lazily from _backlog after load.
        private Dictionary<string, PastEvent> _openEpisodes = new Dictionary<string, PastEvent>();
        private bool _openEpisodesRebuilt = false;

        // An episode is narratable once it has been quiet for this long (accretes into one memory first).
        private const int EpisodeSettleTicks = 1250;   // ~30 in-game minutes
        // Hard bound so an open-ended event (idle mechanoids that are never engaged) can't stay open forever.
        private const int EpisodeMaxOpenTicks = 15000;  // ~6 in-game hours
        public List<FiredIncidentRecord> firedIncidentHistory = new List<FiredIncidentRecord>();
        public List<WealthRecord> wealthHistory = new List<WealthRecord>();
        // Player↔storyteller chat log for the Core-owned chat window (Core #99). Player and
        // Storyteller messages only — carries no colonist speaker. Migrated out of the Conversations
        // world component so the window works even when Conversations is not installed.
        public List<StorytellerChatMessage> storytellerChatHistory = new List<StorytellerChatMessage>();
        public RaidOutcomeRecord lastRaidOutcome;
        private int lastPurgeTick = -1;
        public string activeRaidEventId;
        public RaidTracker activeRaidTracker;
        public Dictionary<int, int> mapGreenhouseCells = new Dictionary<int, int>();
        public List<MapGreenhouseHistoryTracker> greenhouseHistory = new List<MapGreenhouseHistoryTracker>();
        public Dictionary<string, string> legendaryImagePaths = new Dictionary<string, string>();
        public Dictionary<string, string> pawnToRaidId = new Dictionary<string, string>();
        public Dictionary<string, List<string>> raidRecruitedPawns = new Dictionary<string, List<string>>();
        public Dictionary<string, int> visitorEntryTicks = new Dictionary<string, int>();
        /// <summary>
        /// Superseded by <see cref="SynapseCoreProviders.PopulationDensity"/>.
        ///
        /// <para>Kept for one release as a compatibility shim: a build of Regions and Territories
        /// made before the provider registry existed sets this field by reflection, and
        /// <c>SynapseCoreProviders.PopulationDensity</c> falls back to reading it when its own slot
        /// is empty. New registrations should target the slot, which logs and has a documented
        /// unregistered value; this field has neither.</para>
        /// </summary>
        [System.Obsolete("Register SynapseCoreProviders.PopulationDensity instead. Read it via SynapseCoreProviders.PopulationDensityAt(tile).")]
        public static System.Func<int, int> GetPopulationDensityDelegate = null;

        public List<int> GetHistoryForMap(int mapId)
        {
            if (greenhouseHistory == null) greenhouseHistory = new List<MapGreenhouseHistoryTracker>();
            var tracker = greenhouseHistory.FirstOrDefault(t => t.mapId == mapId);
            if (tracker == null)
            {
                tracker = new MapGreenhouseHistoryTracker(mapId);
                greenhouseHistory.Add(tracker);
            }
            return tracker.history;
        }

        // Storyteller properties
        public Dictionary<string, float> categoryMultipliers = new Dictionary<string, float>();
        public Dictionary<string, float> incidentMultipliers = new Dictionary<string, float>();
        public float GlobalPacingMultiplier = 1.0f;
        public float BasePacingMultiplier = 1.0f;
        public float TensionModifier = 1.0f;
        public int lastInvestigationHour = -1;

        // LLM-driven incident selection: single-decision-in-flight guard (Core #67). One async
        // selection may be pending at a time — a slow call must not overlap the next beat. Both
        // fields are scribed so a decision interrupted by save/quit does not wedge the storyteller:
        // the callback that would clear the flag lives in a process that is now gone, so on load a
        // flag older than DecisionStaleTicks is treated as stale and a fresh decision may begin.
        public bool storytellerDecisionInFlight = false;
        public int storytellerDecisionStartTick = -1;
        private const int DecisionStaleTicks = 2500; // ~1 in-game hour; a real async call resolves in seconds

        /// <summary>Whether a storyteller decision is currently pending and not yet stale (#67).</summary>
        public bool StorytellerDecisionInFlight
            => !RimSynapse.Comps.StorytellerDecisionGate.CanBegin(
                storytellerDecisionInFlight, storytellerDecisionStartTick, NowTick, DecisionStaleTicks);

        /// <summary>
        /// Claim the single in-flight slot for a new decision. Returns false if one is already
        /// pending (and not stale), in which case the caller must not start another.
        /// </summary>
        public bool TryBeginStorytellerDecision()
        {
            if (!RimSynapse.Comps.StorytellerDecisionGate.CanBegin(
                    storytellerDecisionInFlight, storytellerDecisionStartTick, NowTick, DecisionStaleTicks))
                return false;
            storytellerDecisionInFlight = true;
            storytellerDecisionStartTick = NowTick;
            return true;
        }

        /// <summary>Release the in-flight slot once a decision has settled (or failed).</summary>
        public void EndStorytellerDecision()
        {
            storytellerDecisionInFlight = false;
            storytellerDecisionStartTick = -1;
        }

        private static int NowTick => Find.TickManager != null ? Find.TickManager.TicksGame : GenTicks.TicksGame;

        public SynapseCoreWorldComponent(World world) : base(world)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            
            Scribe_Collections.Look(ref narrativeThreads, "narrativeThreads", LookMode.Deep);
            Scribe_Collections.Look(ref factionTrackers, "factionTrackers", LookMode.Deep);
            Scribe_Collections.Look(ref shortTermEvents, "shortTermEvents", LookMode.Deep);
            Scribe_Collections.Look(ref backlogQueueList, "backlogQueueList", LookMode.Deep);
            Scribe_Collections.Look(ref firedIncidentHistory, "firedIncidentHistory", LookMode.Deep);
            Scribe_Collections.Look(ref pawnEventRecords, "pawnEventRecords", LookMode.Deep);
            Scribe_Collections.Look(ref steleEventLinks, "steleEventLinks", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref colonyStartGenerated, "colonyStartGenerated", false);

            // Storyteller properties
            Scribe_Collections.Look(ref categoryMultipliers, "categoryMultipliers", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref incidentMultipliers, "incidentMultipliers", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref GlobalPacingMultiplier, "globalPacingMultiplier", 1.0f);
            Scribe_Values.Look(ref BasePacingMultiplier, "basePacingMultiplier", 1.0f);
            Scribe_Values.Look(ref TensionModifier, "tensionModifier", 1.0f);
            Scribe_Values.Look(ref lastInvestigationHour, "lastInvestigationHour", -1);
            Scribe_Values.Look(ref storytellerDecisionInFlight, "storytellerDecisionInFlight", false);
            Scribe_Values.Look(ref storytellerDecisionStartTick, "storytellerDecisionStartTick", -1);
            Scribe_Values.Look(ref activeRaidEventId, "activeRaidEventId");
            Scribe_Deep.Look(ref activeRaidTracker, "activeRaidTracker");
            Scribe_Collections.Look(ref mapGreenhouseCells, "mapGreenhouseCells", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref greenhouseHistory, "greenhouseHistory", LookMode.Deep);
            Scribe_Collections.Look(ref legendaryImagePaths, "legendaryImagePaths", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref pawnToRaidId, "pawnToRaidId", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref raidRecruitedPawns, "raidRecruitedPawns", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref visitorEntryTicks, "visitorEntryTicks", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref wealthHistory, "wealthHistory", LookMode.Deep);
            Scribe_Collections.Look(ref storytellerChatHistory, "storytellerChatHistory", LookMode.Deep);
            Scribe_Deep.Look(ref lastRaidOutcome, "lastRaidOutcome");

            if (Scribe.mode == LoadSaveMode.Saving)
            {
                backlogQueueList.Clear();
                backlogQueueList.AddRange(_backlog);
            }

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (narrativeThreads == null) narrativeThreads = new List<NarrativeThread>();
                if (factionTrackers == null) factionTrackers = new List<FactionRelationshipTracker>();
                if (shortTermEvents == null) shortTermEvents = new List<ShortTermEvent>();
                if (backlogQueueList == null) backlogQueueList = new List<PastEvent>();
                if (firedIncidentHistory == null) firedIncidentHistory = new List<FiredIncidentRecord>();
                if (wealthHistory == null) wealthHistory = new List<WealthRecord>();
                if (storytellerChatHistory == null) storytellerChatHistory = new List<StorytellerChatMessage>();
                if (pawnEventRecords == null) pawnEventRecords = new List<PawnEventRecord>();
                if (steleEventLinks == null) steleEventLinks = new Dictionary<string, string>();
                
                if (categoryMultipliers == null) categoryMultipliers = new Dictionary<string, float>();
                if (incidentMultipliers == null) incidentMultipliers = new Dictionary<string, float>();
                if (mapGreenhouseCells == null) mapGreenhouseCells = new Dictionary<int, int>();
                if (greenhouseHistory == null) greenhouseHistory = new List<MapGreenhouseHistoryTracker>();
                if (legendaryImagePaths == null) legendaryImagePaths = new Dictionary<string, string>();
                if (pawnToRaidId == null) pawnToRaidId = new Dictionary<string, string>();
                if (raidRecruitedPawns == null) raidRecruitedPawns = new Dictionary<string, List<string>>();
                if (visitorEntryTicks == null) visitorEntryTicks = new Dictionary<string, int>();
                
                _backlog.Clear();
                if (backlogQueueList != null) _backlog.AddRange(backlogQueueList);
                _openEpisodes.Clear();
                _openEpisodesRebuilt = false;
            }
        }

        public override void WorldComponentTick()
        {
            base.WorldComponentTick();
            int currentTick = Find.TickManager.TicksGame;
            
            // Purge short-term memory every 2500 ticks (1 in-game hour)
            if (currentTick - lastPurgeTick > 2500)
            {
                lastPurgeTick = currentTick;
                float hours = RimSynapseMod.Instance.Settings.shortTermMemoryHours;
                int maxAgeTicks = (int)(hours * 2500f);
                
                shortTermEvents.RemoveAll(e => currentTick - e.gameTick > maxAgeTicks);
                firedIncidentHistory.RemoveAll(e => currentTick - e.gameTick > 1800000); // Prune older than 30 days
            }

            // Update greenhouse cells daily at 12:00
            if (currentTick % 60000 == 30000)
            {
                UpdateGreenhouseCells();
            }

            // Daily wealth growth pacing check
            if (currentTick % 60000 == 45000)
            {
                CheckWealthGrowthPacing();
            }

            // Check for new game colony start generation
            if (!colonyStartGenerated && currentTick >= 200)
            {
                colonyStartGenerated = true;
                GenerateColonyStartEventRecord();
            }
        }

        private void UpdateGreenhouseCells()
        {
            if (Find.Maps == null) return;

            foreach (var map in Find.Maps)
            {
                int count = 0;
                try
                {
                    if (map.roofGrid != null && map.glowGrid != null)
                    {
                        var candidateCells = new HashSet<IntVec3>();

                        // 1. Growing zones cells
                        if (map.zoneManager != null)
                        {
                            foreach (var zone in map.zoneManager.AllZones)
                            {
                                if (zone is Zone_Growing growZone && growZone.cells != null)
                                {
                                    foreach (var cell in growZone.cells)
                                    {
                                        candidateCells.Add(cell);
                                    }
                                }
                            }
                        }

                        // 2. Planter cells (hydroponics/pots)
                        if (map.listerBuildings != null && map.listerBuildings.allBuildingsColonist != null)
                        {
                            foreach (var b in map.listerBuildings.allBuildingsColonist)
                            {
                                if (b is Building_PlantGrower grower)
                                {
                                    foreach (var cell in grower.OccupiedRect())
                                    {
                                        candidateCells.Add(cell);
                                    }
                                }
                            }
                        }

                        // 3. Scan candidate cells
                        foreach (var cell in candidateCells)
                        {
                            if (!map.roofGrid.Roofed(cell)) continue;
                            if (map.glowGrid.GroundGlowAt(cell) < 0.51f) continue;

                            Room room = cell.GetRoom(map);
                            if (room != null && !room.UsesOutdoorTemperature && !room.PsychologicallyOutdoors)
                            {
                                float temp = room.Temperature;
                                if (temp >= 10f && temp <= 42f)
                                {
                                    count++;
                                }
                            }
                        }
                    }
                }
                catch { }

                mapGreenhouseCells[map.uniqueID] = count;

                // Update history
                var history = GetHistoryForMap(map.uniqueID);
                history.Add(count);
                if (history.Count > 10)
                {
                    history.RemoveAt(0);
                }
            }
        }

        public void LogShortTermInteraction(Pawn initiator, Pawn recipient, InteractionDef intDef)
        {
            var stEvent = new ShortTermEvent
            {
                gameTick = Find.TickManager.TicksGame,
                date = SynapseDate.Now(),
                eventType = ShortTermEventType.Generic,
                involvedPawnIds = new List<string> { initiator.ThingID, recipient.ThingID }
            };

            // Filter generic chitchat vs deep talk
            if (intDef == InteractionDefOf.Chitchat)
            {
                // In RimWorld, Chitchat generally results in small positive opinion. 
                // A true negative outcome would usually be a slight or insult.
                stEvent.eventType = ShortTermEventType.PositiveInteraction;
                stEvent.description = $"{initiator.NameShortColored} and {recipient.NameShortColored} shared positive chitchat.";
            }
            else if (intDef == InteractionDefOf.DeepTalk)
            {
                stEvent.eventType = ShortTermEventType.DeepTalk;
                stEvent.description = $"{initiator.NameShortColored} and {recipient.NameShortColored} had a deep talk.";
            }
            else if (intDef.defName == "Slight" || intDef == InteractionDefOf.Insult)
            {
                stEvent.eventType = ShortTermEventType.NegativeInteraction;
                stEvent.description = $"{initiator.NameShortColored} insulted or slighted {recipient.NameShortColored}.";
            }
            else if (intDef == InteractionDefOf.RomanceAttempt)
            {
                stEvent.eventType = ShortTermEventType.DeepTalk;
                stEvent.description = $"{initiator.NameShortColored} flirted with {recipient.NameShortColored}.";
            }
            else
            {
                stEvent.description = $"{initiator.NameShortColored} interacted with {recipient.NameShortColored} ({intDef.label}).";
            }

            shortTermEvents.Add(stEvent);
        }

        public string LogGlobalEvent(string category, string description, string factionName = null, string settlementName = null)
        {
            var pastEvent = new PastEvent
            {
                gameTick = Find.TickManager.TicksGame,
                date = SynapseDate.Now(),
                category = category,
                eventDescription = description,
                factionName = factionName,
                settlementName = settlementName
            };
            EnqueuePastEvent(pastEvent);
            return pastEvent.eventId;
        }

        public string LogWorldEvent(string category, string description, string sourceFactionId, string targetFactionId = null, string parentEventId = null)
        {
            string factionName = null;
            if (!string.IsNullOrEmpty(sourceFactionId))
            {
                Faction f = Find.FactionManager.AllFactions.FirstOrDefault(x => x.GetUniqueLoadID() == sourceFactionId);
                if (f != null) factionName = f.Name;
            }

            var pastEvent = new PastEvent
            {
                gameTick = Find.TickManager.TicksGame,
                date = SynapseDate.Now(),
                category = category,
                eventDescription = description,
                factionName = factionName,
                sourceFactionId = sourceFactionId,
                targetFactionId = targetFactionId,
                parentEventId = parentEventId
            };
            EnqueuePastEvent(pastEvent);
            return pastEvent.eventId;
        }

        public void ResolveEvent(string eventId, string outcomeDescription, EventOutcome outcome, string parentEventId = null)
        {
            if (string.IsNullOrEmpty(eventId)) return;

            foreach (var ev in _backlog)
            {
                if (ev.eventId == eventId)
                {
                    ev.outcomeDescription = outcomeDescription;
                    ev.outcome = outcome;
                    ev.isResolved = true;
                    ev.resolvedTick = Find.TickManager.TicksGame;

                    if (Find.AnyPlayerHomeMap != null)
                    {
                        Map map = Find.AnyPlayerHomeMap;
                        ev.endWealth = map.wealthWatcher.WealthTotal;
                        ev.endFoodNutrition = map.resourceCounter.TotalHumanEdibleNutrition;

                        float wealthDiff = ev.endWealth - ev.startWealth;
                        float foodDiff = ev.endFoodNutrition - ev.startFoodNutrition;
                        int durationTicks = ev.resolvedTick - ev.gameTick;
                        float durationDays = (float)durationTicks / 60000f;

                        string resourceSummary = $" [Duration: {durationDays:F2} days, Wealth Change: {wealthDiff:+0;-0;0} silver, Food Change: {foodDiff:+0.0;-0.0;0.0} nutrition]";
                        ev.outcomeDescription += resourceSummary;
                    }

                    if (!string.IsNullOrEmpty(parentEventId))
                    {
                        ev.parentEventId = parentEventId;
                    }
                    return;
                }
            }
        }

        public void EnqueuePastEvent(PastEvent pastEvent)
        {
            if (pastEvent == null) return;
            pastEvent.EnsureMcpTag();

            int now = Find.TickManager != null ? Find.TickManager.TicksGame : GenTicks.TicksGame;
            if (pastEvent.firstTick == 0) pastEvent.firstTick = now;
            pastEvent.lastUpdateTick = now;

            EnsureOpenEpisodeIndex();

            // Coalesce trivial/standard repeats of the same still-open ordeal (e.g. 27 crow claws) into
            // one growing episode. Serious/Major events are always their own entry.
            if (pastEvent.severity <= EventSeverity.Standard)
            {
                string key = pastEvent.CoalesceKey;
                if (_openEpisodes.TryGetValue(key, out var open) && _backlog.Contains(open) && !IsEpisodeSettled(open, now))
                {
                    open.occurrenceCount++;
                    open.lastUpdateTick = now;
                    if (pastEvent.severity > open.severity) open.severity = pastEvent.severity;
                    MergePawnIds(open.involvedPawnIds, pastEvent.involvedPawnIds);
                    MergePawnIds(open.witnessPawnIds, pastEvent.witnessPawnIds);
                    MergePawnIds(open.afterEffectPawnIds, pastEvent.afterEffectPawnIds);
                    if (!string.IsNullOrEmpty(pastEvent.eventDescription)) open.eventDescription = pastEvent.eventDescription;
                    return; // merged — no new entry, no re-snapshot
                }
            }

            SnapshotColony(pastEvent);
            _backlog.Add(pastEvent);
            if (pastEvent.severity <= EventSeverity.Standard) _openEpisodes[pastEvent.CoalesceKey] = pastEvent;

            // Cap: evict the lowest-severity, oldest event first, so trivial spam never pushes out a
            // serious episode.
            while (_backlog.Count > 50)
            {
                var victim = _backlog.OrderBy(e => (int)e.severity).ThenBy(e => e.lastUpdateTick).First();
                RemoveBacklogEvent(victim);
            }
        }

        /// <summary>Take the colony/pawn snapshot for a fresh (non-merged) event.</summary>
        private void SnapshotColony(PastEvent pastEvent)
        {
            if (Find.AnyPlayerHomeMap == null) return;
            Map map = Find.AnyPlayerHomeMap;

            float nutrition = map.resourceCounter.TotalHumanEdibleNutrition;
            pastEvent.colonySnapshot = $"Wealth: {map.wealthWatcher.WealthTotal:F0}, Nutrition Available: {nutrition:F0}";
            pastEvent.startWealth = map.wealthWatcher.WealthTotal;
            pastEvent.startFoodNutrition = nutrition;

            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn.needs != null && pawn.needs.mood != null && pawn.needs.food != null && pawn.needs.rest != null)
                {
                    string moodStr = pawn.needs.mood.CurLevelPercentage.ToStringPercent();
                    string foodStr = pawn.needs.food.CurLevelPercentage.ToStringPercent();
                    string restStr = pawn.needs.rest.CurLevelPercentage.ToStringPercent();
                    pastEvent.pawnSnapshots[pawn.ThingID] = $"Mood: {moodStr}, Food: {foodStr}, Rest: {restStr}";
                }
            }
        }

        /// <summary>Register an already-enqueued event as the open episode for its subject (used by the
        /// injury/tend capture path to attach witnesses/after-effect pawns to the right ordeal).</summary>
        public PastEvent FindOpenEpisode(string coalesceKey)
        {
            if (string.IsNullOrEmpty(coalesceKey)) return null;
            EnsureOpenEpisodeIndex();
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : GenTicks.TicksGame;
            if (_openEpisodes.TryGetValue(coalesceKey, out var ev) && _backlog.Contains(ev) && !IsEpisodeSettled(ev, now))
                return ev;
            return null;
        }

        /// <summary>The most recent still-open episode a pawn is involved in — the target an after-effect
        /// participant (e.g. a doctor tending them) attaches to. Null if none is open.</summary>
        public PastEvent FindOpenEpisodeForPawn(Pawn pawn)
        {
            if (pawn == null) return null;
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : GenTicks.TicksGame;
            return _backlog
                .Where(e => !IsEpisodeSettled(e, now) && e.involvedPawnIds != null && e.involvedPawnIds.Contains(pawn.ThingID))
                .OrderByDescending(e => e.lastUpdateTick)
                .FirstOrDefault();
        }

        private bool IsEpisodeSettled(PastEvent ev, int now)
        {
            return (now - ev.lastUpdateTick) >= EpisodeSettleTicks || (now - ev.firstTick) >= EpisodeMaxOpenTicks;
        }

        private void EnsureOpenEpisodeIndex()
        {
            if (_openEpisodesRebuilt) return;
            _openEpisodesRebuilt = true;
            _openEpisodes.Clear();
            foreach (var ev in _backlog)
                if (ev.severity <= EventSeverity.Standard)
                    _openEpisodes[ev.CoalesceKey] = ev; // last-wins
        }

        private void RemoveBacklogEvent(PastEvent ev)
        {
            _backlog.Remove(ev);
            if (_openEpisodes.TryGetValue(ev.CoalesceKey, out var oe) && oe == ev) _openEpisodes.Remove(ev.CoalesceKey);
        }

        private static void MergePawnIds(List<string> dst, List<string> src)
        {
            if (dst == null || src == null) return;
            for (int i = 0; i < src.Count; i++)
                if (!string.IsNullOrEmpty(src[i]) && !dst.Contains(src[i])) dst.Add(src[i]);
        }

        public bool TryDequeuePastEvent(out PastEvent pastEvent)
        {
            pastEvent = null;
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : GenTicks.TicksGame;
            EnsureOpenEpisodeIndex();

            // Only settled episodes are narratable — an ongoing ordeal keeps accreting first. Oldest first.
            var settled = _backlog.Where(e => IsEpisodeSettled(e, now)).OrderBy(e => e.lastUpdateTick).ToList();
            foreach (var ev in settled)
            {
                // Significance floor: a lone trivial blip that never accreted is dropped, never narrated.
                if (ev.severity == EventSeverity.Trivial && ev.occurrenceCount <= 1)
                {
                    RemoveBacklogEvent(ev);
                    continue;
                }
                RemoveBacklogEvent(ev);
                pastEvent = ev;
                return true;
            }
            return false;
        }

        public int BacklogCount => _backlog.Count;

        public IEnumerable<PastEvent> GetRecentEvents(int count)
        {
            return System.Linq.Enumerable.Skip(_backlog, System.Math.Max(0, _backlog.Count - count));
        }

        public IEnumerable<PastEvent> AllEvents => _backlog;

        public List<PastEvent> GetMostSignificantEvents(int count)
        {
            var list = new List<PastEvent>();
            list.AddRange(_backlog);
            
            // Exclude legendary art creation events to prevent recursion/meta art loops
            list.RemoveAll(e => e.category == "LegendaryArtCreated" || 
                                (e.eventDescription != null && e.eventDescription.ToLower().Contains("legendary")));

            return list
                .OrderByDescending(e => CalculateSignificance(e))
                .Take(count)
                .ToList();
        }

        public float CalculateSignificance(PastEvent ev)
        {
            if (ev == null) return 0f;
            float score = 10f;

            if (!string.IsNullOrEmpty(ev.category))
            {
                string catLower = ev.category.ToLower();
                if (catLower.Contains("raid") || catLower.Contains("combat") || catLower.Contains("battle"))
                {
                    score += 50f;
                }
                else if (catLower.Contains("marriage") || catLower.Contains("wedding") || catLower.Contains("birth"))
                {
                    score += 100f;
                }
                else if (catLower.Contains("death") || catLower.Contains("tragedy") || catLower.Contains("murder"))
                {
                    score += 80f;
                }
                else if (catLower.Contains("legendary") || catLower.Contains("art"))
                {
                    score += 60f;
                }
                else if (catLower.Contains("restoration"))
                {
                    score += 75f;
                }
                else if (catLower.Contains("bionic") || catLower.Contains("surgery"))
                {
                    score += 60f;
                }
                else if (catLower.Contains("quest") || catLower.Contains("tribute"))
                {
                    score += 40f;
                }
            }

            if (!string.IsNullOrEmpty(ev.eventDescription))
            {
                string descLower = ev.eventDescription.ToLower();
                if (descLower.Contains("joined") || descLower.Contains("recruited") || descLower.Contains("recruit"))
                {
                    score += 50f;
                }
                if (descLower.Contains("died") || descLower.Contains("killed") || descLower.Contains("slain"))
                {
                    score += 40f;
                }
                if (descLower.Contains("triumph") || descLower.Contains("victory") || descLower.Contains("won"))
                {
                    score += 30f;
                }
            }

            if (ev.isResolved)
            {
                if (ev.outcome == EventOutcome.Triumph)
                {
                    score += 30f;
                }
                else if (ev.outcome == EventOutcome.Tragedy)
                {
                    score += 40f;
                }
                else if (ev.outcome == EventOutcome.Success)
                {
                    score += 15f;
                }
            }

            float wealthDiff = System.Math.Abs(ev.endWealth - ev.startWealth);
            if (wealthDiff > 100f)
            {
                score += System.Math.Min(50f, wealthDiff / 200f);
            }

            float foodDiff = System.Math.Abs(ev.endFoodNutrition - ev.startFoodNutrition);
            if (foodDiff > 5f)
            {
                score += System.Math.Min(25f, foodDiff * 2f);
            }

            if (Find.TickManager != null)
            {
                int ageTicks = Find.TickManager.TicksGame - ev.gameTick;
                if (ageTicks > 0)
                {
                    float dayAge = ageTicks / 60000f;
                    score -= System.Math.Min(20f, dayAge * 0.5f);
                }
            }

            return score;
        }



        private void GenerateColonyStartEventRecord()
        {
            try
            {
                var colonists = new List<Pawn>();
                if (Find.Maps != null)
                {
                    foreach (var map in Find.Maps)
                    {
                        if (map?.mapPawns?.FreeColonists != null)
                        {
                            colonists.AddRange(map.mapPawns.FreeColonists);
                        }
                    }
                }

                if (colonists.Count == 0) return;

                string scenarioName = Find.Scenario?.name ?? "Scenario Start";
                string dateStr = RimWorld.GenDate.DateFullStringAt(Find.TickManager.TicksAbs, Find.WorldGrid.LongLatOf(colonists[0].Tile));

                // Generate a description/log of the colony start
                string colonistListStr = string.Join("\n", colonists.Select(p => $" - {p.Name?.ToStringFull ?? p.Label}, the {p.story?.Title ?? "Colonist"}"));

                // Build a nice, formatted log text
                string startLog = $"The colony began on this historic day under the '{scenarioName}' scenario.\n\n" +
                                 $"The following pioneers crashlanded or arrived to establish the settlement:\n" +
                                 $"{colonistListStr}\n\n" +
                                 $"With nothing but a few basic supplies and their determination, they stepped onto the soil to face the unknown dangers and build a new home together.";

                // Generate a historical event record!
                string eventId = "ColonyStart_" + System.Guid.NewGuid().ToString();
                var record = new PawnEventRecord(
                    eventId,
                    "Colony Established",
                    dateStr,
                    "Colony",
                    startLog
                );

                pawnEventRecords.Add(record);
                RimSynapse.SynapseLogger.Info("core", $"[RimSynapse] Successfully generated Colony Start event record: {eventId}");

                // Send a letter to notify the player and save it into the game's historical letter history
                Find.LetterStack.ReceiveLetter(
                    "Colony Established",
                    startLog,
                    LetterDefOf.PositiveEvent,
                    colonists
                );
            }
            catch (System.Exception ex)
            {
                RimSynapse.SynapseLogger.Warn("core", $"Failed to generate Colony Start event record: {ex.Message}");
            }
        }

    }

    public class MapGreenhouseHistoryTracker : IExposable
    {
        public int mapId;
        public List<int> history = new List<int>();

        public MapGreenhouseHistoryTracker()
        {
        }

        public MapGreenhouseHistoryTracker(int mapId)
        {
            this.mapId = mapId;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref mapId, "mapId", 0);
            Scribe_Collections.Look(ref history, "history", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars && history == null)
            {
                history = new List<int>();
            }
        }
    }
}
