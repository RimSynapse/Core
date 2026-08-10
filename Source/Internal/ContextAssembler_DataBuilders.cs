using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace RimSynapse.Internal
{
    /// <summary>
    /// Context data builders: pawn identity, pawn state, colony, world, synthetic data.
    /// Reads vanilla RimWorld APIs and optional companion mod data via reflection.
    /// </summary>
    internal static partial class ContextAssembler
    {
        private static void FillShortTermHistory(ContextPacket packet, Pawn sourcePawn, List<ContextSlot> slots)
        {
            var comp = Find.World?.GetComponent<SynapseCoreWorldComponent>();
            if (comp == null || comp.shortTermEvents == null || comp.shortTermEvents.Count == 0) return;

            var relevantEvents = new List<Models.ShortTermEvent>();
            foreach (var ev in comp.shortTermEvents)
            {
                if (sourcePawn == null)
                {
                    relevantEvents.Add(ev);
                }
                else if (ev.involvedPawnIds.Contains(sourcePawn.ThingID) || ev.eventType == Models.ShortTermEventType.GlobalEvent)
                {
                    relevantEvents.Add(ev);
                }
            }

            if (relevantEvents.Count > 0)
            {
                packet.recentEvents = relevantEvents.OrderBy(e => e.gameTick).ToList();
                
                var sb = new StringBuilder();
                foreach (var ev in packet.recentEvents)
                {
                    sb.AppendLine($"- [{ev.date.ToString()}] {ev.description}");
                }
                
                slots.Add(MakeSlot("shortTermHistory", sb.ToString()));
            }
        }

        private static PawnPacket BuildPawnIdentity(Pawn pawn)
        {
            return new PawnPacket
            {
                pawnId = pawn.ThingID,
                name = pawn.Name?.ToStringShort ?? "Unknown",
                gender = pawn.gender.ToString(),
                age = pawn.ageTracker?.AgeBiologicalYears ?? 0,
                backstoryChildhood = pawn.story?.Childhood?.title,
                backstoryAdulthood = pawn.story?.Adulthood?.title,
                traits = pawn.story?.traits?.allTraits?
                    .Select(t => t.Label).ToList() ?? new List<string>(),
            };
        }

        private static void FillPawnState(PawnPacket packet, Pawn pawn, List<ContextSlot> slots)
        {
            FillPawnMood(packet, pawn, slots);
            FillPawnSkills(packet, pawn, slots);
            FillPawnHealth(packet, pawn, slots);
            FillPawnRelationships(packet, pawn, slots);
            FillPawnOpinions(packet, pawn, slots);
            FillPawnJob(packet, pawn);
            FillPawnEquipment(packet, pawn, slots);
            FillPawnProductivity(packet, pawn, slots);

            // Let expansion integrations inject their data
            OnAssemblePawnPacket?.Invoke(pawn, packet, slots);

            // Weather / Season / Biome
            if (pawn.Map != null)
            {
                string weather = $"[Environment] {GenLocalDate.Season(pawn.Map)}, " +
                    $"{pawn.Map.weatherManager?.curWeather?.label ?? "clear"}, " +
                    $"{pawn.Map.Biome?.label ?? "unknown biome"}";
                slots.Add(MakeSlot("weather", weather));
            }
        }

        private static void FillPawnMood(PawnPacket packet, Pawn pawn, List<ContextSlot> slots)
        {
            if (pawn.needs?.mood == null) return;
            packet.moodLevel = pawn.needs.mood.CurLevel;
            packet.thoughts = ThoughtFilter.FilterThoughts(pawn);
            slots.Add(MakeSlot("mood", FormatMood(packet)));
        }

        private static void FillPawnSkills(PawnPacket packet, Pawn pawn, List<ContextSlot> slots)
        {
            if (pawn.skills?.skills == null) return;
            packet.skills = new Dictionary<string, int>();
            packet.passions = new List<string>();
            foreach (var skill in pawn.skills.skills)
            {
                packet.skills[skill.def.label] = skill.Level;
                if (skill.passion != Passion.None)
                    packet.passions.Add($"{skill.def.label} ({skill.passion})");
            }
            slots.Add(MakeSlot("skills", FormatSkills(packet)));
        }

        private static void FillPawnHealth(PawnPacket packet, Pawn pawn, List<ContextSlot> slots)
        {
            if (pawn.health?.hediffSet?.hediffs == null) return;
            packet.healthConditions = pawn.health.hediffSet.hediffs
                .Where(h => h.Visible)
                .Select(h =>
                {
                    string part = h.Part?.Label;
                    string label = h.Label;
                    return part != null ? $"{label} ({part})" : label;
                })
                .ToList();

            if (packet.healthConditions.Count > 0)
                slots.Add(MakeSlot("health", FormatHealth(packet)));
        }

        private static void FillPawnRelationships(PawnPacket packet, Pawn pawn, List<ContextSlot> slots)
        {
            if (pawn.relations?.DirectRelations == null) return;
            packet.relationships = pawn.relations.DirectRelations
                .Select(r => new RelationshipEntry
                {
                    relationLabel = r.def.label,
                    otherPawnName = r.otherPawn?.Name?.ToStringShort ?? "Unknown",
                    otherPawnId = r.otherPawn?.ThingID,
                })
                .ToList();

            if (packet.relationships.Count > 0)
                slots.Add(MakeSlot("relationships", FormatRelationships(packet)));
        }

        private static void FillPawnOpinions(PawnPacket packet, Pawn pawn, List<ContextSlot> slots)
        {
            try
            {
                var colonists = pawn.Map?.mapPawns?.FreeColonists;
                if (colonists == null) return;

                packet.opinions = colonists
                    .Where(c => c != pawn)
                    .Select(c => new OpinionEntry
                    {
                        pawnName = c.Name?.ToStringShort ?? "Unknown",
                        opinion = pawn.relations?.OpinionOf(c) ?? 0,
                    })
                    .OrderByDescending(o => Math.Abs(o.opinion))
                    .Take(5)
                    .ToList();

                if (packet.opinions.Count > 0)
                    slots.Add(MakeSlot("opinions", FormatOpinions(packet)));
            }
            catch { /* Opinion access can fail in edge cases */ }
        }

        private static void FillPawnJob(PawnPacket packet, Pawn pawn)
        {
            if (pawn.CurJob != null)
            {
                packet.currentJob = pawn.CurJob.def?.label ?? "unknown activity";
            }
        }

        private static void FillPawnEquipment(PawnPacket packet, Pawn pawn, List<ContextSlot> slots)
        {
            var equipList = new List<string>();
            if (pawn.equipment?.Primary != null)
                equipList.Add(pawn.equipment.Primary.Label);
            if (pawn.apparel?.WornApparel != null)
            {
                foreach (var a in pawn.apparel.WornApparel)
                    equipList.Add(a.Label);
            }
            if (equipList.Count > 0)
            {
                packet.equipment = equipList;
                slots.Add(MakeSlot("equipment", FormatEquipment(packet)));
            }
        }

        private static void FillPawnProductivity(PawnPacket packet, Pawn pawn, List<ContextSlot> slots)
        {
            if (!pawn.RaceProps.Humanlike) return;

            float lifeStageHunger = pawn.ageTracker?.CurLifeStage?.hungerRateFactor ?? 1f;
            float baseHunger = pawn.def?.race?.baseHungerRate ?? 1f;
            float hungerMult = 1f;
            var hungerStat = DefDatabase<StatDef>.GetNamed("HungerRateMultiplier", false);
            if (hungerStat != null)
            {
                hungerMult = pawn.GetStatValue(hungerStat);
            }
            packet.hungerRatePerDay = lifeStageHunger * baseHunger * hungerMult;

            float prod = 0f;
            if (!pawn.WorkTagIsDisabled(WorkTags.Violent))
            {
                float meleeSpeed = pawn.GetStatValue(StatDefOf.MeleeDodgeChance) * 5f;
                float shootAccuracy = pawn.GetStatValue(StatDefOf.ShootingAccuracyPawn);
                prod += 1f + meleeSpeed + (shootAccuracy * 2f);
            }

            prod += pawn.GetStatValue(StatDefOf.ConstructionSpeed);
            prod += pawn.GetStatValue(StatDefOf.MiningSpeed) * pawn.GetStatValue(StatDefOf.MiningYield);
            prod += pawn.GetStatValue(StatDefOf.PlantWorkSpeed) * pawn.GetStatValue(StatDefOf.PlantHarvestYield);
            prod += pawn.GetStatValue(StatDefOf.GeneralLaborSpeed);
            prod += pawn.GetStatValue(StatDefOf.ResearchSpeed);
            prod += pawn.GetStatValue(StatDefOf.TradePriceImprovement) * 5f;

            packet.productivityScore = prod;
            packet.netEconomicValue = packet.hungerRatePerDay > 0.01f ? (packet.productivityScore / packet.hungerRatePerDay) : packet.productivityScore;

            string productivityText = $"[Productivity] Daily Hunger: {packet.hungerRatePerDay:F2}, Work Capability: {packet.productivityScore:F2}, Net Economic Value: {packet.netEconomicValue:F2}";
            slots.Add(MakeSlot("productivity", productivityText));
        }

        // ────────────────────────────────────────────────────────
        //  Colony data builder (Tier 3)
        // ────────────────────────────────────────────────────────

        private static ColonyPacket BuildColonyPacket()
        {
            var map = Find.CurrentMap;
            if (map == null) return null;

            var packet = new ColonyPacket
            {
                colonistCount = map.mapPawns?.FreeColonistsCount ?? 0,
                wealthTotal = map.wealthWatcher?.WealthTotal ?? 0f,
                season = GenLocalDate.Season(map).ToString(),
                weather = map.weatherManager?.curWeather?.label ?? "unknown",
                biome = map.Biome?.label ?? "unknown",
                dangerLevel = map.dangerWatcher?.DangerRating.ToString() ?? "unknown",
                recentEvents = new List<string>(),
            };

            try
            {
                var currentProject = Find.ResearchManager?.GetProject();
                if (currentProject != null)
                {
                    packet.currentResearch = currentProject.label;
                    packet.researchProgress = currentProject.ProgressPercent;
                }
            }
            catch { }

            try
            {
                var letters = Find.LetterStack?.LettersListForReading;
                if (letters != null)
                {
                    int start = Math.Max(0, letters.Count - 5);
                    for (int i = start; i < letters.Count; i++)
                    {
                        packet.recentEvents.Add(letters[i].Label);
                    }
                }
            }
            catch { }

            return packet;
        }

        // ────────────────────────────────────────────────────────
        //  World / Faction builder (Tier 4)
        // ────────────────────────────────────────────────────────

        private static WorldPacket BuildWorldPacket()
        {
            var packet = new WorldPacket
            {
                factions = new List<FactionEntry>(),
                activeQuestNames = new List<string>(),
            };

            try
            {
                var allFactions = Find.FactionManager?.AllFactionsVisible;
                if (allFactions != null)
                {
                    foreach (var faction in allFactions)
                    {
                        if (faction.IsPlayer) continue;

                        packet.factions.Add(new FactionEntry
                        {
                            factionName = faction.Name,
                            factionType = faction.def?.label ?? "unknown",
                            relationKind = faction.PlayerRelationKind.ToString(),
                            goodwill = faction.PlayerGoodwill,
                            leaderName = faction.leader?.Name?.ToStringShort,
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                SynapseLogger.Message($"Faction enumeration error: {ex.Message}");
            }

            try
            {
                var quests = Find.QuestManager?.QuestsListForReading;
                if (quests != null)
                {
                    foreach (var quest in quests.Where(q => !q.Historical && !q.dismissed))
                    {
                        packet.activeQuestNames.Add(quest.name);
                    }
                }
            }
            catch { }

            return packet;
        }

        // ────────────────────────────────────────────────────────
        //  Tier 5: Synthetic data (companion mod, null-safe)
        // ────────────────────────────────────────────────────────

        private static void FillSyntheticData(ContextPacket packet, Pawn pawn, List<ContextSlot> slots)
        {
            if (pawn == null) return;
            FillPsychologyData(packet, pawn, slots);
            FillNarrativeThreads(packet, slots);
        }

        private static void FillPsychologyData(ContextPacket packet, Pawn pawn, List<ContextSlot> slots)
        {
            try
            {
                var coreComp = pawn.GetComp<RimSynapse.Comps.SynapseCorePawnComp>();
                if (coreComp == null) return;

                // Grab filtered memories. If there's a target pawn, pull memories specific to them FIRST.
                string targetPawnId = packet.targetPawn?.pawnId;
                
                // Fetch the top 5 memory burdens
                string topBurdens = coreComp.GetTopMemoryBurdens(5, 0f, null, targetPawnId);
                
                if (topBurdens != "None")
                {
                    slots.Add(MakeSlot("memories", $"[Memories] {topBurdens}"));
                    
                    // We also populate packet.sourcePawn.memories for raw data injection
                    var filteredMemories = targetPawnId != null
                        ? coreComp.GetMemoriesByPawnId(targetPawnId)
                        : coreComp.memories;

                    // Tier selection: long-term / high-salience first, then recent high-weight short-term
                    // (design §5.8). Also records the surface as a reference (recency + count).
                    var selected = coreComp.SelectMemoriesForContext(filteredMemories, 5);
                    var entries = new List<MemoryEntry>();
                    foreach (var mem in selected)
                    {
                        entries.Add(new MemoryEntry
                        {
                            summary = mem.summary,
                            weight = mem.weight,
                            memoryType = mem.memoryType
                        });
                    }
                    packet.sourcePawn.memories = entries;
                }

                // Read personality summary natively — LLM prose if a producer filled it, else the Core
                // deterministic baseline (backstory + traits), so consumers work without Psychology too.
                string personality = coreComp.EffectivePersonalitySummary();
                if (!string.IsNullOrEmpty(personality))
                {
                    packet.sourcePawn.personalitySummary = personality;
                    slots.Add(MakeSlot("personalitySummary", $"[Personality] {personality}"));
                }
            }
            catch (Exception ex)
            {
                SynapseLogger.Message($"Could not read Core comp memory data: {ex.Message}");
            }
        }

        private static void FillNarrativeThreads(ContextPacket packet, List<ContextSlot> slots)
        {
            try
            {
                if (Find.World?.components == null) return;

                foreach (var comp in Find.World.components)
                {
                    if (comp.GetType().Name != "SynapseWorldComponent") continue;

                    var threadsField = comp.GetType().GetField("narrativeThreads");
                    if (!(threadsField?.GetValue(comp) is System.Collections.IList threadList)) break;

                    var entries = new List<NarrativeThreadEntry>();
                    foreach (var thread in threadList)
                    {
                        var tType = thread.GetType();
                        var keyword = tType.GetField("keyword")?.GetValue(thread) as string;
                        var category = tType.GetField("category")?.GetValue(thread) as string;
                        var desc = tType.GetField("description")?.GetValue(thread) as string;
                        var weight = tType.GetField("weight")?.GetValue(thread);
                        var resolved = tType.GetField("isResolved")?.GetValue(thread);

                        if (keyword != null)
                        {
                            entries.Add(new NarrativeThreadEntry
                            {
                                keyword = keyword,
                                category = category,
                                description = desc,
                                weight = weight != null ? (float)weight : 0f,
                                isResolved = resolved != null && (bool)resolved,
                            });
                        }
                    }

                    var active = entries.Where(t => !t.isResolved).ToList();
                    if (active.Count > 0)
                    {
                        packet.narrativeThreads = active;
                        slots.Add(MakeSlot("narrativeThreads", FormatThreads(active)));
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                SynapseLogger.Message($"Could not read Storyteller world data: {ex.Message}");
            }
        }
    }
}
