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

        // Voice (Conversations#33): Psychology authors these; Conversations consumes voiceProfile now,
        // the 0.10+ Kokoro TTS layer consumes the kokoro* fields. See Models/KokoroVoices.cs.
        public string voiceProfile;              // prose speaking-style directive (how they talk)
        public string kokoroVoice;               // base Kokoro voice id, RNG by sex/gender at birth
        public float kokoroSpeed = 1f;           // personality pace -> Kokoro speed (~0.8..1.3)
        public string kokoroBlendVoice;          // optional secondary voice to blend toward (timbre)
        public float kokoroBlendWeight = 0f;     // blend weight 0..1 (0 = no blend)
        public bool voiceGenerated = false;      // guard: derive/backfill runs once

        // Active AI-driven modifiers shared across mods
        public Dictionary<string, float> thoughtSensitivities = new Dictionary<string, float>();
        public Dictionary<string, float> relationSensitivities = new Dictionary<string, float>();

        // Per-candidate-trait accumulated evidence toward/away from a personality shift (Stage 2, #79).
        // Core owns the persisted data; Psychology folds evidence in and reads the threshold.
        public Dictionary<string, TraitPressure> traitPressures = new Dictionary<string, TraitPressure>();

        // Once-a-day skill snapshot for the skill-driven trait engine — the prior day's Level and
        // xpSinceLastLevel per skill, so rust (an expert skill declining from disuse) is detectable as a
        // delta without a per-tick patch. Refreshed by UpdateAndGetRustedSkills at the rest edge.
        public Dictionary<string, int> skillLevelSnapshot = new Dictionary<string, int>();
        public Dictionary<string, float> skillXpSinceLevelSnapshot = new Dictionary<string, float>();
        public long lastSkillSnapshotTick = 0L;

        // Rolling mood baseline (EMA of daily average mood) for the reinforcement dimension of the trait
        // engine — a trait needs the behaviour AND a positive mood response to it. -1 = uninitialised.
        public float moodBaseline = -1f;

        // Grounding history tracking fields
        public List<string> recentLocations = new List<string>();
        public List<JobInterval> recentJobs = new List<JobInterval>();
        public string lastJobDefName;
        public int lastJobStartedTick = -1;
        // Target kind of the currently-running job (combat jobs only); folded into the interval
        // when the job ends. See ClassifyTargetKind. Null for non-combat / unclassifiable.
        public string lastJobTargetKind;
        private int locationSampleCooldown = 5;
        /// <summary>
        /// Migration source only. Residency moved to Regions and Territories in 0.7 — it generates
        /// the dwellings and was always the only writer of this field — and is read through
        /// <see cref="SynapseCoreProviders.IsResident"/>.
        ///
        /// <para>Still scribed so a pre-0.7 save keeps its residents: R&amp;T reads this once per
        /// pawn and adopts it. Dwelling generation runs only at map generation, so a dropped flag
        /// could never be re-derived. Remove this field, and its Scribe line, once that migration
        /// window has passed.</para>
        /// </summary>
        public bool isResident = false;
        public int lastRecruitmentAttemptTick = -999999;

        private const int TickIntervalDay = 60000;
        private const int TickInterval6Hours = 15000;

        private int lastDecayTick = -1;
        private int lastOpinionTick = -1;

        // ── Stage 1 (0.7.1) weight lifecycle ────────────────────────────
        /// <summary>One-shot legacy-migration guard. 0 = pre-0.7.1 (weights on the old 0.1–5.0 scale);
        /// bumped to CurrentMemoryScaleVersion after the one-time rescale so it never double-applies.</summary>
        private int memoryScaleVersion = 0;
        private const int CurrentMemoryScaleVersion = 1;
        private const float LegacyMaxWeightTier = 5.0f; // old scale ceiling; legacy weights divide by this

        /// <summary>One-shot guard for the trait-pressure key migration. 0 = pre-skill-engine (keys were
        /// bare trait defNames); bumped after rewriting them to candidate-id form ("{axisId}#{degree}").</summary>
        private int traitPressureKeyVersion = 0;
        private const int CurrentTraitPressureKeyVersion = 1;

        /// <summary>Global decay-speed multiplier. Owned here (Core owns memory); Psychology mirrors its
        /// "Memory Decay Speed" setting into this static (Psychology→Core is the allowed direction).</summary>
        public static float MemoryDecayMultiplier = 1.0f;

        // Consolidation / pressure knobs — Core owns the data; Psychology mirrors its settings into
        // these statics (Psychology→Core is the allowed direction). Defaults preserve prior behavior.
        public static float ConsolidationThreshold = 1.0f;
        public static int ReferenceThreshold = 3;
        public static float TraitPressureDecayPerDay = 0.2f;

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

            Scribe_Values.Look(ref voiceProfile, "voiceProfile");
            Scribe_Values.Look(ref kokoroVoice, "kokoroVoice");
            Scribe_Values.Look(ref kokoroSpeed, "kokoroSpeed", 1f);
            Scribe_Values.Look(ref kokoroBlendVoice, "kokoroBlendVoice");
            Scribe_Values.Look(ref kokoroBlendWeight, "kokoroBlendWeight", 0f);
            Scribe_Values.Look(ref voiceGenerated, "voiceGenerated", false);

            Scribe_Collections.Look(ref thoughtSensitivities, "thoughtSensitivities", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref relationSensitivities, "relationSensitivities", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref traitPressures, "traitPressures", LookMode.Value, LookMode.Deep);
            Scribe_Collections.Look(ref skillLevelSnapshot, "skillLevelSnapshot", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref skillXpSinceLevelSnapshot, "skillXpSinceLevelSnapshot", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref lastSkillSnapshotTick, "lastSkillSnapshotTick", 0L);
            Scribe_Values.Look(ref moodBaseline, "moodBaseline", -1f);
            
            Scribe_Collections.Look(ref recentLocations, "recentLocations", LookMode.Value);
            Scribe_Collections.Look(ref recentJobs, "recentJobs", LookMode.Deep);
            Scribe_Values.Look(ref lastJobDefName, "lastJobDefName");
            Scribe_Values.Look(ref lastJobStartedTick, "lastJobStartedTick", -1);
            Scribe_Values.Look(ref lastJobTargetKind, "lastJobTargetKind", null);
            Scribe_Values.Look(ref locationSampleCooldown, "locationSampleCooldown", 5);
            Scribe_Values.Look(ref isResident, "isResident", false);
            Scribe_Values.Look(ref lastRecruitmentAttemptTick, "lastRecruitmentAttemptTick", -999999);

            Scribe_Values.Look(ref lastDecayTick, "lastDecayTick", -1);
            Scribe_Values.Look(ref lastOpinionTick, "lastOpinionTick", -1);
            Scribe_Values.Look(ref memoryScaleVersion, "memoryScaleVersion", 0);
            Scribe_Values.Look(ref traitPressureKeyVersion, "traitPressureKeyVersion", 0);
            
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (memories == null) memories = new List<WeightedMemory>();
                if (opinionHistory == null) opinionHistory = new List<OpinionSample>();
                if (llmTraits == null) llmTraits = new List<string>();
                if (thoughtSensitivities == null) thoughtSensitivities = new Dictionary<string, float>();
                if (relationSensitivities == null) relationSensitivities = new Dictionary<string, float>();
                if (traitPressures == null) traitPressures = new Dictionary<string, TraitPressure>();
                if (skillLevelSnapshot == null) skillLevelSnapshot = new Dictionary<string, int>();
                if (skillXpSinceLevelSnapshot == null) skillXpSinceLevelSnapshot = new Dictionary<string, float>();
                if (recentLocations == null) recentLocations = new List<string>();
                if (recentJobs == null) recentJobs = new List<JobInterval>();
            }
            
            // Migrate old gameTick-only memories to absTick, then run Stage 1 (0.7.1) migrations.
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                foreach (var memory in memories)
                {
                    memory.MigrateTickIfNeeded();
                }
                MigrateMemoryScaleIfNeeded();
                MigrateTraitPressureKeysIfNeeded();
                foreach (var memory in memories)
                {
                    memory.EnsureMemId();
                    if (memory.lastReferencedTick == 0) memory.lastReferencedTick = memory.absTick;
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
                    RunMemoryMaintenance();
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

                // Simulated cooking for resident NPC pawns. Asks whoever owns residency rather than
                // the local field, which as of 0.7 is only a migration source for R&T.
                if (SynapseCoreProviders.IsResident(pawn) && pawn.inventory != null)
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

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        // ── Trait pressure (Stage 2, #79) — data + helpers; Psychology owns the threshold logic ──
        private const int TicksPerDayF = 60000;

        /// <summary>
        /// Fold a day's evidence into a trait's accumulated pressure (design §5.7): decay the existing
        /// entry toward 0 by elapsed days first, then add <c>dailyPressure × (1 − resistanceFactor)</c>.
        /// Returns the new accumulated pressure.
        /// </summary>
        public float AccumulateTraitPressure(string trait, float dailyPressure, string direction,
            float resistanceFactor, long nowTick, float pressureDecayPerDay = 0.2f)
        {
            if (string.IsNullOrEmpty(trait)) return 0f;
            if (!traitPressures.TryGetValue(trait, out var tp))
            {
                tp = new TraitPressure();
                traitPressures[trait] = tp;
            }
            if (tp.lastUpdatedTick > 0)
            {
                float days = System.Math.Max(0f, (nowTick - tp.lastUpdatedTick) / (float)TicksPerDayF);
                tp.pressure = System.Math.Max(0f, tp.pressure - pressureDecayPerDay * days);
            }
            tp.pressure += dailyPressure * (1f - Clamp01(resistanceFactor));
            tp.direction = direction;
            // Stamp the axis/degree from the candidate-id key (e.g. "NaturalMood#-1" or "Bloodlust#+"),
            // so readers can render "toward Pessimist" without re-parsing. No signature change.
            if (RimSynapse.Models.TraitAxis.TryParse(trait, out var axisId, out var targetDegree, out _))
            {
                tp.axisId = axisId;
                tp.targetDegree = targetDegree;
            }
            tp.lastUpdatedTick = nowTick;
            if (tp.pressure > tp.peak) tp.peak = tp.pressure;
            return tp.pressure;
        }

        public bool TryGetTraitPressure(string trait, out TraitPressure tp) => traitPressures.TryGetValue(trait, out tp);

        /// <summary>Clear a trait's accumulated pressure (call after a change actually fires).</summary>
        public void ResetTraitPressure(string trait)
        {
            if (trait != null) traitPressures.Remove(trait);
        }

        /// <summary>
        /// Decay every trait pressure toward 0 by elapsed days and drop entries that reach ~0, so a
        /// single bad day does not linger. Called from the daily maintenance pass.
        /// </summary>
        public void DecayTraitPressuresToZero(long nowTick, float pressureDecayPerDay = 0.2f)
        {
            if (traitPressures.Count == 0) return;
            var stale = new List<string>();
            foreach (var kvp in traitPressures)
            {
                var tp = kvp.Value;
                float days = tp.lastUpdatedTick > 0 ? System.Math.Max(0f, (nowTick - tp.lastUpdatedTick) / (float)TicksPerDayF) : 0f;
                tp.pressure = System.Math.Max(0f, tp.pressure - pressureDecayPerDay * days);
                tp.lastUpdatedTick = nowTick;
                if (tp.pressure <= 0.001f) stale.Add(kvp.Key);
            }
            foreach (var k in stale) traitPressures.Remove(k);
        }

        /// <summary>
        /// One-shot legacy weight rescale (design §8). Old saves stored weights on a 0.1–5.0 scale;
        /// the canonical scale is now 0–1. Runs exactly once per comp, guarded by memoryScaleVersion.
        /// </summary>
        private void MigrateMemoryScaleIfNeeded()
        {
            if (memoryScaleVersion >= CurrentMemoryScaleVersion) return;

            bool looksLegacy = false;
            foreach (var mem in memories)
            {
                if (mem.weight > 1.0f || mem.baseWeight > 1.0f) { looksLegacy = true; break; }
            }
            if (looksLegacy)
            {
                foreach (var mem in memories)
                {
                    mem.weight = Clamp01(mem.weight / LegacyMaxWeightTier);
                    mem.baseWeight = Clamp01(mem.baseWeight / LegacyMaxWeightTier);
                }
                SynapseLogger.Message($"[RimSynapse] Migrated {memories.Count} memories from legacy weight scale (÷{LegacyMaxWeightTier}).", "performance");
            }
            memoryScaleVersion = CurrentMemoryScaleVersion;
        }

        /// <summary>
        /// One-shot rewrite of legacy trait-pressure keys. The old (largely inert) whitelist system keyed
        /// pressure by bare trait defName ("Bloodlust"); the skill engine keys by candidate id
        /// ("Bloodlust#+", "NaturalMood#-1"). Old entries were all single-trait add/remove, so we resolve
        /// the new key from the stored <see cref="TraitPressure.direction"/>. Runs once, guarded by version.
        /// </summary>
        private void MigrateTraitPressureKeysIfNeeded()
        {
            if (traitPressureKeyVersion >= CurrentTraitPressureKeyVersion) return;
            if (traitPressures != null && traitPressures.Count > 0)
            {
                var rekeyed = new Dictionary<string, TraitPressure>();
                foreach (var kvp in traitPressures)
                {
                    string key = kvp.Key;
                    if (key != null && key.IndexOf('#') < 0)
                    {
                        bool add = !string.Equals(kvp.Value?.direction, "remove", System.StringComparison.OrdinalIgnoreCase);
                        key = RimSynapse.Models.TraitAxis.SingleCandidate(key, add);
                    }
                    rekeyed[key] = kvp.Value; // last-writer-wins on the rare collision
                }
                traitPressures = rekeyed;
                SynapseLogger.Message($"[RimSynapse] Migrated {rekeyed.Count} trait-pressure keys to candidate-id form.", "performance");
            }
            traitPressureKeyVersion = CurrentTraitPressureKeyVersion;
        }

        /// <summary>
        /// Daily maintenance: salience &amp; consolidation FIRST (so a same-window significant event can
        /// promote a linked memory before it would decay out), then class-driven decay and pruning.
        /// Public + parameterless so tests can drive it deterministically after populating memories.
        /// </summary>
        public void RunMemoryMaintenance()
        {
            // Pick up any memories added by direct list mutation (some companion paths bypass AddMemory)
            // and ensure every memory has a stable id before we reason about links.
            RebuildMemoryIndexes();
            foreach (var mem in memories) mem.EnsureMemId();

            RecomputeSalienceAndConsolidate();
            DoMemoryDecay();

            // Trait pressures ebb toward 0 on days without fresh evidence (design §4.2/§5.7).
            long now = Find.TickManager != null ? Find.TickManager.TicksAbs : 0L;
            DecayTraitPressuresToZero(now, TraitPressureDecayPerDay);
        }

        private void DoMemoryDecay()
        {
            for (int i = memories.Count - 1; i >= 0; i--)
            {
                var mem = memories[i];
                if (mem.isLongTerm) continue; // Long term memories never decay

                float decay = SynapseMemoryClassDef.For(mem.memoryType).decayRate * MemoryDecayMultiplier;
                mem.weight -= decay;

                if (mem.weight <= 0f)
                {
                    RemoveMemoryAt(i);
                }
            }
        }

        /// <summary>
        /// Relational salience &amp; consolidation (design §5.4). A memory's salience is its own weight plus
        /// the class-weighted contribution of neighbours sharing a subjectPawnId or tag, plus a small
        /// bonus for defining tags. Crossing the threshold (or enough references) promotes it to long-term.
        /// The "graph reference" retroactive boost is emergent: a newly added death memory is a heavy
        /// neighbour of everything sharing that pawn's id, so linked chit-chat crosses the line next pass.
        /// </summary>
        private void RecomputeSalienceAndConsolidate()
        {
            foreach (var mem in memories)
            {
                if (mem.isLongTerm)
                {
                    mem.salience = 1f; // already registered
                    continue;
                }

                float salience = mem.weight + EntitySignificanceBonus(mem);

                foreach (var neighbor in Neighbors(mem))
                {
                    var cls = SynapseMemoryClassDef.For(neighbor.memoryType);
                    salience += neighbor.weight * cls.consolidationContribution;
                }

                mem.salience = salience;

                if (salience >= ConsolidationThreshold || mem.timesReferenced >= ReferenceThreshold)
                {
                    mem.isLongTerm = true;
                }
            }
        }

        private static readonly HashSet<string> SignificantTags = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            "Death", "Died", "Grief", "Betrayal", "Bond", "Love", "TraitShift"
        };

        private static float EntitySignificanceBonus(WeightedMemory mem)
        {
            if (mem.tags == null) return 0f;
            foreach (var tag in mem.tags)
            {
                if (SignificantTags.Contains(tag)) return 0.3f;
            }
            return 0f;
        }

        /// <summary>Distinct memories that share a subjectPawnId or tag with <paramref name="mem"/> (excluding itself).</summary>
        private IEnumerable<WeightedMemory> Neighbors(WeightedMemory mem)
        {
            var seen = new HashSet<WeightedMemory> { mem };
            var result = new List<WeightedMemory>();
            if (mem.subjectPawnIds != null)
            {
                foreach (var pid in mem.subjectPawnIds)
                {
                    if (memoriesByPawnId.TryGetValue(pid, out var list))
                    {
                        foreach (var other in list) if (seen.Add(other)) result.Add(other);
                    }
                }
            }
            if (mem.tags != null)
            {
                foreach (var tag in mem.tags)
                {
                    if (memoriesByTag.TryGetValue(tag, out var list))
                    {
                        foreach (var other in list) if (seen.Add(other)) result.Add(other);
                    }
                }
            }
            return result;
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
            if (memory == null) return;
            NormalizeMemory(memory);
            memory.EnsureMemId();
            memories.Add(memory);
            IndexMemory(memory);
        }

        /// <summary>
        /// Select memories for context by tier (design §5.8): long-term / high-salience first, then the
        /// remaining budget filled with recent high-weight short-term — instead of a flat top-N-by-weight,
        /// which favours stale minor memories that merely haven't decayed yet. Surfacing counts as a
        /// reference (design §5.5): recency and reference count are bumped, feeding consolidation. Weight
        /// is intentionally NOT raised here — only genuine LLM back-reference (BumpMemory) does that.
        /// </summary>
        public List<WeightedMemory> SelectMemoriesForContext(List<WeightedMemory> source, int budget)
        {
            if (source == null || source.Count == 0 || budget <= 0) return new List<WeightedMemory>();

            var longTerm = source.Where(m => m.isLongTerm)
                                  .OrderByDescending(m => m.salience)
                                  .ThenByDescending(m => m.weight);
            var shortTerm = source.Where(m => !m.isLongTerm)
                                  .OrderByDescending(m => m.weight)
                                  .ThenByDescending(m => m.lastReferencedTick);

            var ordered = longTerm.Concat(shortTerm).Take(budget).ToList();

            long now = Find.TickManager != null ? Find.TickManager.TicksAbs : 0L;
            foreach (var m in ordered)
            {
                m.timesReferenced++;
                m.lastReferencedTick = now;
            }
            return ordered;
        }

        /// <summary>
        /// Bring a memory onto the canonical 0–1 scale and apply its class's born-long-term flag.
        /// Defensive: callers on the old 0.1–5.0 scale (or other mods) are clamped rather than trusted.
        /// </summary>
        private static void NormalizeMemory(WeightedMemory memory)
        {
            if (memory.weight > 1.0f) memory.weight = memory.weight / LegacyMaxWeightTier;
            if (memory.baseWeight > 1.0f) memory.baseWeight = memory.baseWeight / LegacyMaxWeightTier;
            memory.weight = Clamp01(memory.weight);
            memory.baseWeight = Clamp01(memory.baseWeight);

            var cls = SynapseMemoryClassDef.For(memory.memoryType);
            if (cls.bornLongTerm) memory.isLongTerm = true;
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

        // Combat job defNames whose target we classify (living vs object) so repetitive violence
        // can be reasoned about correctly — object-bashing must not read as violence against the living.
        private static readonly HashSet<string> CombatJobDefNames = new HashSet<string>
        {
            "AttackMelee", "AttackStatic", "Hunt"
        };

        public static bool IsCombatJob(string jobDefName)
        {
            return jobDefName != null && CombatJobDefNames.Contains(jobDefName);
        }

        /// <summary>
        /// Classify what a combat job acted on: "humanlike" | "animal" | "object" | "self",
        /// or null when the target is invalid/unknown. Mechanoids and other non-living pawns
        /// classify as "object" — they are constructs, not living creatures, so harming them is
        /// not evidence of bloodlust toward the living.
        /// </summary>
        public static string ClassifyTargetKind(LocalTargetInfo target, Pawn actor)
        {
            if (!target.IsValid) return null;
            Thing t = target.Thing;
            if (t == null) return null; // a cell target with no thing
            if (t is Pawn p)
            {
                if (p == actor) return "self";
                if (p.RaceProps != null)
                {
                    if (p.RaceProps.Humanlike) return "humanlike";
                    if (p.RaceProps.Animal) return "animal";
                }
                return "object"; // mechanoids / entities: non-living
            }
            return "object";
        }

        private string ClassifyCurrentJobTarget(Pawn pawn, string jobDefName)
        {
            if (!IsCombatJob(jobDefName) || pawn.CurJob == null) return null;
            return ClassifyTargetKind(pawn.CurJob.targetA, pawn);
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
                lastJobTargetKind = ClassifyCurrentJobTarget(pawn, currentJobDefName);
                return;
            }

            if (lastJobDefName != currentJobDefName)
            {
                int duration = currentTick - lastJobStartedTick;
                if (duration > 0 && lastJobDefName != null)
                {
                    recentJobs.Add(new JobInterval(lastJobDefName, lastJobStartedTick, duration, lastJobTargetKind));
                }

                lastJobDefName = currentJobDefName;
                lastJobStartedTick = currentTick;
                // Capture the new job's target now, at its start — targetA is stable for the job's life.
                lastJobTargetKind = ClassifyCurrentJobTarget(pawn, currentJobDefName);

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

            // Accumulate durations keyed by job def AND target kind, so repetitive violence against
            // an object groups apart from violence against the living rather than reading as one
            // undifferentiated "attacking" blob.
            var accumulated = new Dictionary<string, int>();
            var keyDef = new Dictionary<string, string>();
            var keyKind = new Dictionary<string, string>();
            int totalDuration = 0;

            void Accumulate(string defName, string targetKind, int ticks)
            {
                if (defName == null || ticks <= 0) return;
                string tk = IsCombatJob(defName) ? targetKind : null;
                string key = defName + "|" + (tk ?? "");
                if (!accumulated.ContainsKey(key))
                {
                    accumulated[key] = 0;
                    keyDef[key] = defName;
                    keyKind[key] = tk;
                }
                accumulated[key] += ticks;
                totalDuration += ticks;
            }

            foreach (var interval in recentJobs)
            {
                Accumulate(interval.jobDefName, interval.targetKind, interval.durationTicks);
            }

            // Include current job's ongoing duration
            if (lastJobDefName != null && lastJobStartedTick != -1)
            {
                int ongoingDuration = currentTick - lastJobStartedTick;
                if (ongoingDuration > 0)
                {
                    Accumulate(lastJobDefName, lastJobTargetKind, ongoingDuration);
                }
            }

            if (totalDuration == 0)
            {
                return "idle (100%)";
            }

            var sorted = accumulated.OrderByDescending(kvp => kvp.Value).ToList();
            var parts = new List<string>();
            foreach (var kvp in sorted)
            {
                float pct = (float)kvp.Value / totalDuration;
                parts.Add(DescribeJobActivity(keyDef[kvp.Key], keyKind[kvp.Key], pct));
            }

            return string.Join(", ", parts);
        }

        /// <summary>
        /// Numeric activity facts over the rolling 24h job window, for the skill-driven trait engine.
        /// <paramref name="idleFraction"/> is the share of recorded time spent idle (0..1);
        /// <paramref name="livingViolenceFraction"/> is the share spent in combat against a living target
        /// (humanlike or animal — object/mechanoid bashing is excluded). Raw facts only: the mapping into
        /// trait axes lives in Psychology. Mirrors <see cref="GetRecentJobsSummary"/>'s accumulation.
        /// </summary>
        public void GetActivityMetrics(out float idleFraction, out float livingViolenceFraction)
        {
            idleFraction = 0f;
            livingViolenceFraction = 0f;
            int currentTick = Find.TickManager.TicksGame;
            long total = 0, idle = 0, livingViolence = 0;

            void Tally(string defName, string targetKind, int ticks)
            {
                if (defName == null || ticks <= 0) return;
                total += ticks;
                if (defName == "idle") idle += ticks;
                if (IsCombatJob(defName) && (targetKind == "humanlike" || targetKind == "animal"))
                    livingViolence += ticks;
            }

            if (recentJobs != null)
                foreach (var interval in recentJobs)
                    Tally(interval.jobDefName, interval.targetKind, interval.durationTicks);
            if (lastJobDefName != null && lastJobStartedTick != -1)
                Tally(lastJobDefName, lastJobTargetKind, currentTick - lastJobStartedTick);

            if (total <= 0) { idleFraction = 1f; return; }
            idleFraction = (float)idle / total;
            livingViolenceFraction = (float)livingViolence / total;
        }

        /// <summary>
        /// Detect skill rust and refresh the daily snapshot. A skill is "rusting" when it is expert-level
        /// (>= <paramref name="expertLevel"/>) and has either dropped a level since the last snapshot, or
        /// lost xp-toward-next-level while going unpractised today (xpSinceMidnight ~ 0). Returns the rusting
        /// skills. The first call on a fresh comp only seeds the snapshot (returns none). Core owns the
        /// snapshot state; Psychology calls this once per day at the rest edge.
        /// </summary>
        public List<SkillDef> UpdateAndGetRustedSkills(Pawn pawn, int expertLevel = 10, float idleXpToday = 50f)
        {
            var rusted = new List<SkillDef>();
            if (pawn?.skills?.skills == null) return rusted;
            if (skillLevelSnapshot == null) skillLevelSnapshot = new Dictionary<string, int>();
            if (skillXpSinceLevelSnapshot == null) skillXpSinceLevelSnapshot = new Dictionary<string, float>();

            bool hadSnapshot = skillLevelSnapshot.Count > 0;
            foreach (var rec in pawn.skills.skills)
            {
                if (rec?.def == null) continue;
                string key = rec.def.defName;
                if (hadSnapshot && rec.Level >= expertLevel)
                {
                    bool droppedLevel = skillLevelSnapshot.TryGetValue(key, out int prevLevel) && rec.Level < prevLevel;
                    bool xpEroding = skillXpSinceLevelSnapshot.TryGetValue(key, out float prevXp)
                        && rec.xpSinceLastLevel < prevXp - 1f && rec.xpSinceMidnight < idleXpToday;
                    if (droppedLevel || xpEroding) rusted.Add(rec.def);
                }
                skillLevelSnapshot[key] = rec.Level;
                skillXpSinceLevelSnapshot[key] = rec.xpSinceLastLevel;
            }
            lastSkillSnapshotTick = Find.TickManager != null ? Find.TickManager.TicksAbs : 0L;
            return rusted;
        }

        /// <summary>
        /// The reinforcement dimension of the trait engine: fold today's average mood into a rolling
        /// baseline (EMA) and return how today compares to it, in [-1, +1]. Positive = the pawn felt better
        /// than usual (behaviours done today are reinforced); negative = worse (they're aversive). The first
        /// call only seeds the baseline and returns 0. A trait needs its behaviour AND a matching-sign
        /// reinforcement, so this is what makes every trait multidimensional rather than a raw behaviour count.
        /// </summary>
        public float UpdateMoodBaselineAndGetReinforcement(float todayMood, float scale = 0.15f)
        {
            todayMood = Clamp01(todayMood);
            if (moodBaseline < 0f)
            {
                moodBaseline = todayMood; // first day: seed only, no reinforcement yet
                return 0f;
            }
            float delta = (todayMood - moodBaseline) / (scale <= 0f ? 0.15f : scale);
            if (delta > 1f) delta = 1f; else if (delta < -1f) delta = -1f;
            moodBaseline = 0.9f * moodBaseline + 0.1f * todayMood;
            return delta;
        }

        private static RoomStatDef _wealthRoomStat;

        /// <summary>
        /// A pawn's individual share of colony wealth (design: "no trait is one-dimensional" — wealth grounds
        /// Greedy/Jealous/Ascetic). = personally owned assets (equipment/apparel/inventory + owned room) plus a
        /// heavily-dampened slice of communal wealth (WealthTotal / (10 x free-colonist count)), because colony
        /// wealth dwarfs any individual's — the damping keeps personal possessions the real differentiator.
        /// Pawns/slaves are already counted in WealthTotal at the game's slave factor.
        /// </summary>
        public static float ComputeIndividualWealth(Pawn pawn)
        {
            if (pawn?.Map == null) return 0f;
            float personal = WealthWatcher.GetEquipmentApparelAndInventoryWealth(pawn);
            var room = pawn.ownership?.OwnedRoom;
            if (room != null)
            {
                if (_wealthRoomStat == null) _wealthRoomStat = DefDatabase<RoomStatDef>.GetNamedSilentFail("Wealth");
                if (_wealthRoomStat != null) personal += room.GetStat(_wealthRoomStat);
            }
            var ww = pawn.Map.wealthWatcher;
            int n = pawn.Map.mapPawns.FreeColonistsCount;
            if (n < 1) n = 1;
            float communalShare = ww != null ? ww.WealthTotal / (10f * n) : 0f;
            return personal + communalShare;
        }

        /// <summary>Average individual wealth across the map's free colonists (peer baseline for envy/greed).</summary>
        public static float ColonyAverageIndividualWealth(Map map)
        {
            if (map == null) return 0f;
            // Snapshot the roster: ComputeIndividualWealth reads mapPawns.FreeColonistsCount, which can
            // rebuild the cached FreeColonists list mid-enumeration ("collection was modified" otherwise).
            var colonists = map.mapPawns.FreeColonists.ToList();
            if (colonists.Count == 0) return 0f;
            float sum = 0f;
            foreach (var c in colonists) sum += ComputeIndividualWealth(c);
            return sum / colonists.Count;
        }

        /// <summary>
        /// The personality prose consumers (Conversations, the trait-engine judge, tools) should read: the
        /// LLM-synthesised summary when a producer (Psychology) has filled it, else a deterministic baseline
        /// assembled from the pawn's own vanilla data. So a game WITHOUT Psychology still gets grounded
        /// character prose — no RNG, no empty context. Psychology enriches; it never gates.
        /// </summary>
        public string EffectivePersonalitySummary()
        {
            if (!string.IsNullOrWhiteSpace(personalitySummary)) return personalitySummary;
            return ComputeBaselinePersonality();
        }

        /// <summary>
        /// The no-LLM baseline: the pawn's adult backstory narrative — that IS their settled-identity prose.
        /// Falls back to the childhood narrative only for pawns too young to have an adulthood (traits are
        /// fed to consumers separately, so they aren't repeated here).
        /// </summary>
        public string ComputeBaselinePersonality()
        {
            var pawn = parent as Pawn;
            if (pawn?.story == null) return null;
            return BackstoryNarrative(pawn.story.Adulthood, pawn)
                ?? BackstoryNarrative(pawn.story.Childhood, pawn);
        }

        private static string BackstoryNarrative(RimWorld.BackstoryDef b, Pawn pawn)
        {
            if (b == null) return null;
            string full = b.FullDescriptionFor(pawn).Resolve();
            if (string.IsNullOrEmpty(full)) return null;
            // Strip rich-text tags (e.g. <color=...>name</color>) so the prose is clean for the LLM/consumers.
            full = System.Text.RegularExpressions.Regex.Replace(full, "<[^>]+>", "");
            // Drop the appended skill/work-modifier block — cut at the first "Skill: +N" token (the disabled
            // lines follow the skill gains, so this removes them too). Keep just the narrative paragraph.
            var m = System.Text.RegularExpressions.Regex.Match(full, @"[A-Za-z]+:\s*[+\-]\d");
            if (m.Success && m.Index > 0) full = full.Substring(0, m.Index);
            return System.Text.RegularExpressions.Regex.Replace(full, @"\s+", " ").Trim();
        }

        /// <summary>
        /// Render one accumulated activity group. Combat groups name their target kind, and
        /// object-only violence is explicitly marked non-lethal so it cannot masquerade as
        /// bloodthirsty violence against the living.
        /// </summary>
        private static string DescribeJobActivity(string jobDefName, string targetKind, float pct)
        {
            var jobDef = DefDatabase<JobDef>.GetNamed(jobDefName, false);
            string label = jobDef?.label ?? jobDefName;
            string pctStr = pct.ToStringPercent();

            if (IsCombatJob(jobDefName) && targetKind != null)
            {
                switch (targetKind)
                {
                    case "humanlike": return $"{label} a person ({pctStr})";
                    case "animal":    return $"{label} an animal ({pctStr})";
                    case "self":      return $"{label} themselves ({pctStr})";
                    default:          return $"{label} an inanimate object — non-lethal, not violence against the living ({pctStr})";
                }
            }

            return $"{label} ({pctStr})";
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

        /// <summary>
        /// For combat jobs, the classified kind of what was acted on:
        /// "humanlike" | "animal" | "object" | "self". Null for non-combat jobs or when
        /// the target could not be classified (including legacy saves written before this field).
        /// </summary>
        public string targetKind;

        public JobInterval() {}

        public JobInterval(string jobDefName, int startTick, int durationTicks)
        {
            this.jobDefName = jobDefName;
            this.startTick = startTick;
            this.durationTicks = durationTicks;
        }

        // Separate overload rather than an optional parameter, to preserve the existing
        // signature for anything already bound to it (binary-compatibility rule).
        public JobInterval(string jobDefName, int startTick, int durationTicks, string targetKind)
        {
            this.jobDefName = jobDefName;
            this.startTick = startTick;
            this.durationTicks = durationTicks;
            this.targetKind = targetKind;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref jobDefName, "jobDefName");
            Scribe_Values.Look(ref startTick, "startTick");
            Scribe_Values.Look(ref durationTicks, "durationTicks");
            Scribe_Values.Look(ref targetKind, "targetKind", null);
        }
    }
}
