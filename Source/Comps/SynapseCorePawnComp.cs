using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using RimSynapse.Models;

namespace RimSynapse.Comps
{
    public class SynapseCorePawnComp : ThingComp
    {
        public List<WeightedMemory> memories = new List<WeightedMemory>();
        
        // Runtime Hashtable Indexes for instant lookup
        private Dictionary<string, List<WeightedMemory>> memoriesByTag = new Dictionary<string, List<WeightedMemory>>();
        private Dictionary<string, List<WeightedMemory>> memoriesByPawnId = new Dictionary<string, List<WeightedMemory>>();
        public List<OpinionSample> opinionHistory = new List<OpinionSample>();
        public string personalitySummary;
        public string dynamicBackstory;
        public string clinicalAssessment;
        public string hometown;
        public List<string> llmTraits = new List<string>();
        
        // Active AI-driven modifiers shared across mods
        public Dictionary<string, float> thoughtSensitivities = new Dictionary<string, float>();
        public Dictionary<string, float> relationSensitivities = new Dictionary<string, float>();

        // Grounding history tracking fields
        public List<string> recentLocations = new List<string>();
        public List<JobInterval> recentJobs = new List<JobInterval>();
        public string lastJobDefName;
        public int lastJobStartedTick = -1;
        private int locationSampleCooldown = 5;
        public bool isResident = false;
        public int lastRecruitmentAttemptTick = -999999;

        private const int TickIntervalDay = 60000;
        private const int TickInterval6Hours = 15000;
        
        private int lastDecayTick = -1;
        private int lastOpinionTick = -1;

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Collections.Look(ref memories, "synapseMemories", LookMode.Deep);
            Scribe_Collections.Look(ref opinionHistory, "synapseOpinionHistory", LookMode.Deep);
            Scribe_Values.Look(ref personalitySummary, "synapsePersonality");
            Scribe_Values.Look(ref dynamicBackstory, "dynamicBackstory");
            Scribe_Values.Look(ref clinicalAssessment, "clinicalAssessment");
            Scribe_Values.Look(ref hometown, "hometown");
            Scribe_Collections.Look(ref llmTraits, "llmTraits", LookMode.Value);
            
            Scribe_Collections.Look(ref thoughtSensitivities, "thoughtSensitivities", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref relationSensitivities, "relationSensitivities", LookMode.Value, LookMode.Value);
            
            Scribe_Collections.Look(ref recentLocations, "recentLocations", LookMode.Value);
            Scribe_Collections.Look(ref recentJobs, "recentJobs", LookMode.Deep);
            Scribe_Values.Look(ref lastJobDefName, "lastJobDefName");
            Scribe_Values.Look(ref lastJobStartedTick, "lastJobStartedTick", -1);
            Scribe_Values.Look(ref locationSampleCooldown, "locationSampleCooldown", 5);
            Scribe_Values.Look(ref isResident, "isResident", false);
            Scribe_Values.Look(ref lastRecruitmentAttemptTick, "lastRecruitmentAttemptTick", -999999);

            Scribe_Values.Look(ref lastDecayTick, "lastDecayTick", -1);
            Scribe_Values.Look(ref lastOpinionTick, "lastOpinionTick", -1);
            
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (memories == null) memories = new List<WeightedMemory>();
                if (opinionHistory == null) opinionHistory = new List<OpinionSample>();
                if (llmTraits == null) llmTraits = new List<string>();
                if (thoughtSensitivities == null) thoughtSensitivities = new Dictionary<string, float>();
                if (relationSensitivities == null) relationSensitivities = new Dictionary<string, float>();
                if (recentLocations == null) recentLocations = new List<string>();
                if (recentJobs == null) recentJobs = new List<JobInterval>();
            }
            
            // Migrate old gameTick-only memories to absTick
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                foreach (var memory in memories)
                {
                    memory.MigrateTickIfNeeded();
                }
                RebuildMemoryIndexes();
            }
        }

        public override void CompTickRare()
        {
            base.CompTickRare();
            
            if (parent is Pawn pawn && pawn.Spawned && !pawn.Dead)
            {
                int currentTick = Find.TickManager.TicksGame;

                // Track job activity/durations rare-tick (every 250 ticks)
                TrackJobRare(pawn, currentTick);

                // Sample location every 1250 ticks (5 rare ticks)
                locationSampleCooldown--;
                if (locationSampleCooldown <= 0)
                {
                    locationSampleCooldown = 5;
                    SampleLocationRare(pawn);
                }

                // Memory decay once per day
                if (lastDecayTick == -1)
                {
                    lastDecayTick = currentTick;
                }
                else if (currentTick - lastDecayTick >= TickIntervalDay)
                {
                    lastDecayTick = currentTick;
                    DoMemoryDecay();
                }

                // Sample opinions periodically (e.g. every 6 in-game hours)
                if (lastOpinionTick == -1)
                {
                    lastOpinionTick = currentTick;
                }
                else if (currentTick - lastOpinionTick >= TickInterval6Hours)
                {
                    lastOpinionTick = currentTick;
                    SampleOpinions(pawn);
                }

                // Simulated cooking for resident NPC pawns
                if (isResident && pawn.inventory != null)
                {
                    bool hasMeals = false;
                    foreach (var item in pawn.inventory.innerContainer)
                    {
                        if (item.def.IsNutritionGivingIngestible && item.def.ingestible.preferability >= FoodPreferability.MealSimple)
                        {
                            hasMeals = true;
                            break;
                        }
                    }

                    if (!hasMeals)
                    {
                        // Look for a campfire or stove nearby (radial 12 cells)
                        bool hasCookingFacility = false;
                        foreach (var c in GenRadial.RadialCellsAround(pawn.Position, 12f, true))
                        {
                            if (c.InBounds(pawn.Map))
                            {
                                var things = c.GetThingList(pawn.Map);
                                foreach (var t in things)
                                {
                                    if (t.def == ThingDefOf.Campfire || t.def.defName.Contains("Stove") || t.def.defName.Contains("Cooker"))
                                    {
                                        hasCookingFacility = true;
                                        break;
                                    }
                                }
                            }
                            if (hasCookingFacility) break;
                        }

                        if (hasCookingFacility)
                        {
                            // Spawn simple meal in inventory
                            Thing meal = ThingMaker.MakeThing(ThingDefOf.MealSimple);
                            meal.stackCount = 1;
                            pawn.inventory.innerContainer.TryAdd(meal);
                            RimSynapse.SynapseLogger.Info("core", $"[Resident Cooking] {pawn.Name?.ToStringShort ?? pawn.Label} simulated cooking a Simple Meal near a cooking facility.");
                        }
                    }
                }
            }
        }

        private void DoMemoryDecay()
        {
            for (int i = memories.Count - 1; i >= 0; i--)
            {
                var mem = memories[i];
                if (mem.isLongTerm) continue; // Long term memories never decay

                mem.weight -= mem.decayRate;
                
                if (mem.weight <= 0f)
                {
                    RemoveMemoryAt(i);
                }
            }
        }

        private void SampleOpinions(Pawn pawn)
        {
            if (pawn.relations == null || pawn.Map == null) return;

            var colonists = pawn.Map.mapPawns?.FreeColonists;
            if (colonists != null)
            {
                int currentTick = GenTicks.TicksGame;
                
                foreach (var other in colonists)
                {
                    if (other == pawn) continue;

                    int opinion = pawn.relations.OpinionOf(other);
                    
                    opinionHistory.Add(new OpinionSample
                    {
                        targetPawnId = other.ThingID,
                        opinion = opinion,
                        gameTick = currentTick
                    });
                }
                
                TrimOpinionHistory();
            }
        }

        private void TrimOpinionHistory()
        {
            // Group by targetPawnId and keep only the latest 20 samples per pawn
            var grouped = opinionHistory.GroupBy(o => o.targetPawnId);
            var newHistory = new List<OpinionSample>();
            
            foreach (var group in grouped)
            {
                var recentSamples = group.OrderByDescending(o => o.gameTick).Take(20);
                newHistory.AddRange(recentSamples);
            }
            
            opinionHistory = newHistory;
        }

        /// <summary>
        /// Aggregates all memory weights by their tags and returns the top N burdens.
        /// This creates a summarized 'Sensitivity' profile for the LLM without sending every raw memory.
        /// </summary>
        public string GetTopMemoryBurdens(int topN = 5, float minWeightThreshold = 0f, string optionalTagFilter = null, string optionalPawnIdFilter = null)
        {
            if (memories == null || memories.Count == 0) return "None";

            var sourceList = memories;
            if (!string.IsNullOrEmpty(optionalTagFilter))
            {
                sourceList = GetMemoriesByTag(optionalTagFilter);
            }
            else if (!string.IsNullOrEmpty(optionalPawnIdFilter))
            {
                sourceList = GetMemoriesByPawnId(optionalPawnIdFilter);
            }

            if (sourceList.Count == 0) return "None";

            var tagWeights = new Dictionary<string, float>();
            var tagCounts = new Dictionary<string, int>();

            foreach (var memory in sourceList)
            {
                if (memory.tags == null) continue;
                foreach (string tag in memory.tags)
                {
                    if (!tagWeights.ContainsKey(tag))
                    {
                        tagWeights[tag] = 0f;
                        tagCounts[tag] = 0;
                    }
                    tagWeights[tag] += memory.weight;
                    tagCounts[tag]++;
                }
            }

            var sorted = tagWeights
                .Where(kvp => kvp.Value >= minWeightThreshold)
                .OrderByDescending(kvp => kvp.Value)
                .Take(topN)
                .ToList();

            if (sorted.Count == 0) return "None";

            return string.Join(", ", sorted.Select(kvp => $"{kvp.Key} ({kvp.Value:F1})"));
        }

        // ────────────────────────────────────────────────────────
        //  Hashtable Memory Logic
        // ────────────────────────────────────────────────────────

        public void AddMemory(WeightedMemory memory)
        {
            memories.Add(memory);
            IndexMemory(memory);
        }

        private void RemoveMemoryAt(int index)
        {
            var memory = memories[index];
            memories.RemoveAt(index);
            UnindexMemory(memory);
        }

        private void RebuildMemoryIndexes()
        {
            memoriesByTag.Clear();
            memoriesByPawnId.Clear();
            foreach (var mem in memories)
            {
                IndexMemory(mem);
            }
        }

        private void IndexMemory(WeightedMemory memory)
        {
            if (memory.tags != null)
            {
                foreach (var tag in memory.tags)
                {
                    if (!memoriesByTag.TryGetValue(tag, out var list))
                    {
                        list = new List<WeightedMemory>();
                        memoriesByTag[tag] = list;
                    }
                    list.Add(memory);
                }
            }

            if (memory.subjectPawnIds != null)
            {
                foreach (var pawnId in memory.subjectPawnIds)
                {
                    if (!memoriesByPawnId.TryGetValue(pawnId, out var list))
                    {
                        list = new List<WeightedMemory>();
                        memoriesByPawnId[pawnId] = list;
                    }
                    list.Add(memory);
                }
            }
        }

        private void UnindexMemory(WeightedMemory memory)
        {
            if (memory.tags != null)
            {
                foreach (var tag in memory.tags)
                {
                    if (memoriesByTag.TryGetValue(tag, out var list))
                    {
                        list.Remove(memory);
                    }
                }
            }

            if (memory.subjectPawnIds != null)
            {
                foreach (var pawnId in memory.subjectPawnIds)
                {
                    if (memoriesByPawnId.TryGetValue(pawnId, out var list))
                    {
                        list.Remove(memory);
                    }
                }
            }
        }

        public List<WeightedMemory> GetMemoriesByTag(string tag)
        {
            if (memoriesByTag.TryGetValue(tag, out var list))
            {
                return list;
            }
            return new List<WeightedMemory>();
        }

        public List<WeightedMemory> GetMemoriesByPawnId(string pawnId)
        {
            if (memoriesByPawnId.TryGetValue(pawnId, out var list))
            {
                return list;
            }
            return new List<WeightedMemory>();
        }

        private void TrackJobRare(Pawn pawn, int currentTick)
        {
            string currentJobDefName = "idle";
            if (pawn.CurJob != null && pawn.CurJob.def != null)
            {
                currentJobDefName = pawn.CurJob.def.defName;
            }

            if (lastJobStartedTick == -1)
            {
                lastJobStartedTick = currentTick;
                lastJobDefName = currentJobDefName;
                return;
            }

            if (lastJobDefName != currentJobDefName)
            {
                int duration = currentTick - lastJobStartedTick;
                if (duration > 0 && lastJobDefName != null)
                {
                    recentJobs.Add(new JobInterval(lastJobDefName, lastJobStartedTick, duration));
                }

                lastJobDefName = currentJobDefName;
                lastJobStartedTick = currentTick;

                // Clean up intervals older than 24 hours (60,000 ticks)
                recentJobs.RemoveAll(j => (currentTick - (j.startTick + j.durationTicks)) > 60000);
            }
        }

        private void SampleLocationRare(Pawn pawn)
        {
            string loc = "outdoors";
            var room = pawn.Position.GetRoom(pawn.Map);
            if (room != null && !room.PsychologicallyOutdoors)
            {
                loc = room.Role?.label ?? room.Role?.defName ?? "indoors";
            }

            recentLocations.Add(loc);
            if (recentLocations.Count > 48) // Keep 24 hours of samples
            {
                recentLocations.RemoveAt(0);
            }
        }

        public string GetRecentLocationsSummary()
        {
            if (recentLocations == null || recentLocations.Count == 0)
            {
                return "outdoors (100%)";
            }

            var counts = new Dictionary<string, int>();
            foreach (var loc in recentLocations)
            {
                if (!counts.ContainsKey(loc)) counts[loc] = 0;
                counts[loc]++;
            }

            var sorted = counts.OrderByDescending(kvp => kvp.Value).ToList();
            var parts = new List<string>();
            foreach (var kvp in sorted)
            {
                float pct = (float)kvp.Value / recentLocations.Count;
                parts.Add($"{kvp.Key} ({pct.ToStringPercent()})");
            }

            return string.Join(", ", parts);
        }

        public string GetRecentJobsSummary()
        {
            int currentTick = Find.TickManager.TicksGame;
            // Clean up intervals older than 24 hours (60,000 ticks)
            recentJobs.RemoveAll(j => (currentTick - (j.startTick + j.durationTicks)) > 60000);

            // Accumulate durations
            var accumulated = new Dictionary<string, int>();
            int totalDuration = 0;

            foreach (var interval in recentJobs)
            {
                if (!accumulated.ContainsKey(interval.jobDefName)) accumulated[interval.jobDefName] = 0;
                accumulated[interval.jobDefName] += interval.durationTicks;
                totalDuration += interval.durationTicks;
            }

            // Include current job's ongoing duration
            if (lastJobDefName != null && lastJobStartedTick != -1)
            {
                int ongoingDuration = currentTick - lastJobStartedTick;
                if (ongoingDuration > 0)
                {
                    if (!accumulated.ContainsKey(lastJobDefName)) accumulated[lastJobDefName] = 0;
                    accumulated[lastJobDefName] += ongoingDuration;
                    totalDuration += ongoingDuration;
                }
            }

            if (totalDuration == 0)
            {
                return "idle (100%)";
            }

            // Group by human-readable JobDef labels where possible
            var labelAccumulated = new Dictionary<string, int>();
            foreach (var kvp in accumulated)
            {
                var jobDef = DefDatabase<JobDef>.GetNamed(kvp.Key, false);
                string label = jobDef?.label ?? kvp.Key;
                if (!labelAccumulated.ContainsKey(label)) labelAccumulated[label] = 0;
                labelAccumulated[label] += kvp.Value;
            }

            var sorted = labelAccumulated.OrderByDescending(kvp => kvp.Value).ToList();
            var parts = new List<string>();
            foreach (var kvp in sorted)
            {
                float pct = (float)kvp.Value / totalDuration;
                parts.Add($"{kvp.Key} ({pct.ToStringPercent()})");
            }

            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Stores a duration segment spent on a specific job.
    /// </summary>
    public class JobInterval : IExposable
    {
        public string jobDefName;
        public int startTick;
        public int durationTicks;

        public JobInterval() {}

        public JobInterval(string jobDefName, int startTick, int durationTicks)
        {
            this.jobDefName = jobDefName;
            this.startTick = startTick;
            this.durationTicks = durationTicks;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref jobDefName, "jobDefName");
            Scribe_Values.Look(ref startTick, "startTick");
            Scribe_Values.Look(ref durationTicks, "durationTicks");
        }
    }
}
