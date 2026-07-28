using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;

namespace RimSynapse.Comps
{
    /// <summary>
    /// Colony metrics gathering for storyteller pacing and event selection queries.
    /// Computes wealth, combat readiness, livestock analysis, greenhouse capacity,
    /// legendary art counts, and growth rates.
    /// </summary>
    public static partial class SynapseStorytellerOpportunistic
    {
        private static string GetColonyDetailedMetrics(Map map)
        {
            if (map == null) return "None.";

            float totalWealth = map.wealthWatcher?.WealthTotal ?? 0f;
            int freeColonists = map.mapPawns?.FreeColonistsCount ?? 0;
            float avgMood = map.mapPawns?.FreeColonists?.Any() == true
                ? map.mapPawns.FreeColonists.Average(p => p.needs?.mood?.CurLevelPercentage ?? 0.5f)
                : 0.5f;

            // Combat capability
            int capablePawns = 0;
            int armedPawns = 0;
            int downedPawns = 0;
            foreach (var pawn in map.mapPawns.FreeColonists)
            {
                if (pawn.Downed)
                {
                    downedPawns++;
                }
                else if (!pawn.Dead && !pawn.WorkTagIsDisabled(WorkTags.Violent))
                {
                    capablePawns++;
                    if (pawn.equipment?.Primary != null && pawn.equipment.Primary.def.IsWeapon)
                    {
                        armedPawns++;
                    }
                }
            }

            // Silver count
            int silverCount = 0;
            foreach (var thing in map.listerThings.ThingsOfDef(ThingDefOf.Silver))
            {
                silverCount += thing.stackCount;
            }

            // Food reserves
            float totalNutrition = CalculateNutrition(map);

            // Growing season statistics
            float growablePercent = CalculateGrowablePercent(map);
            float burdenMult = 1f / Math.Max(0.08f, growablePercent);

            // Livestock analysis
            string animalReport = BuildLivestockReport(map, burdenMult);
            int tameAnimalsCount = 0;
            float tameAnimalsWealth = 0f;
            CountTameAnimals(map, out tameAnimalsCount, out tameAnimalsWealth);

            // Greenhouse and Hydroponics capacity
            int activeHydroponics = 0;
            int activeSunLamps = 0;
            int activeSkylightsCount = 0;
            int greenhouseCells = 0;
            string trendText = "Stable";
            GatherGreenhouseMetrics(map, out activeHydroponics, out activeSunLamps, out activeSkylightsCount, out greenhouseCells, out trendText);

            // Legendary art
            int legendaryArtCount = 0;
            float legendaryArtValue = 0f;
            CountLegendaryArt(map, out legendaryArtCount, out legendaryArtValue);

            // Population density
            int popDensity = SynapseCoreProviders.PopulationDensityAt(map.Tile);

            // Format output using templates
            return FormatMetricsOutput(map, freeColonists, totalWealth, silverCount, totalNutrition,
                growablePercent, burdenMult, greenhouseCells, activeHydroponics, activeSunLamps,
                activeSkylightsCount, trendText, tameAnimalsCount, tameAnimalsWealth, animalReport,
                legendaryArtCount, legendaryArtValue, popDensity, capablePawns, armedPawns,
                downedPawns, avgMood);
        }

        private static float CalculateNutrition(Map map)
        {
            float totalNutrition = 0f;
            try
            {
                foreach (var thing in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSource))
                {
                    if (thing.def.IsNutritionGivingIngestible && thing.def.ingestible != null)
                    {
                        totalNutrition += thing.stackCount * thing.def.ingestible.CachedNutrition;
                    }
                }
            }
            catch { }
            return totalNutrition;
        }

        private static float CalculateGrowablePercent(Map map)
        {
            if (map.Tile < 0) return 1f;
            int growableTwelfths = 0;
            for (int i = 0; i < 12; i++)
            {
                Twelfth twelfth = (Twelfth)i;
                if (GenTemperature.AverageTemperatureAtTileForTwelfth(map.Tile, twelfth) >= 6f)
                {
                    growableTwelfths++;
                }
            }
            return (float)growableTwelfths / 12f;
        }

        private static void CountTameAnimals(Map map, out int count, out float wealth)
        {
            count = 0;
            wealth = 0f;
            try
            {
                if (map.mapPawns?.AllPawns != null)
                {
                    foreach (var p in map.mapPawns.AllPawns)
                    {
                        if (p.Faction == Faction.OfPlayer && p.RaceProps != null && p.RaceProps.Animal)
                        {
                            count++;
                            wealth += p.MarketValue;
                        }
                    }
                }
            }
            catch { }
        }

        private static string BuildLivestockReport(Map map, float burdenMult)
        {
            var animalGroups = new Dictionary<string, (int count, float wealth, int trainedSteps, float dailyHunger)>();
            try
            {
                if (map.mapPawns?.AllPawns != null)
                {
                    foreach (var p in map.mapPawns.AllPawns)
                    {
                        if (p.Faction == Faction.OfPlayer && p.RaceProps != null && p.RaceProps.Animal)
                        {
                            string label = p.def.label;
                            int trained = 0;
                            if (p.training != null)
                            {
                                foreach (var trainable in DefDatabase<TrainableDef>.AllDefs)
                                {
                                    if (p.training.HasLearned(trainable))
                                    {
                                        trained++;
                                    }
                                }
                            }

                            // Calculate daily hunger
                            float lifeStageHunger = p.ageTracker?.CurLifeStage?.hungerRateFactor ?? 1f;
                            float baseHunger = p.def?.race?.baseHungerRate ?? 1f;
                            float hungerMult = 1f;
                            var hungerStat = DefDatabase<StatDef>.GetNamed("HungerRateMultiplier", false);
                            if (hungerStat != null)
                            {
                                hungerMult = p.GetStatValue(hungerStat);
                            }
                            float dailyHunger = lifeStageHunger * baseHunger * hungerMult;

                            if (animalGroups.TryGetValue(label, out var tuple))
                            {
                                animalGroups[label] = (tuple.count + 1, tuple.wealth + p.MarketValue, tuple.trainedSteps + trained, tuple.dailyHunger + dailyHunger);
                            }
                            else
                            {
                                animalGroups[label] = (1, p.MarketValue, trained, dailyHunger);
                            }
                        }
                    }
                }
            }
            catch { }

            var groupLines = new List<string>();
            foreach (var kvp in animalGroups)
            {
                string species = kvp.Key;
                int count = kvp.Value.count;
                float wealth = kvp.Value.wealth;
                int steps = kvp.Value.trainedSteps;
                float hunger = kvp.Value.dailyHunger;

                float rawCostPerLevel = hunger / Math.Max(1, steps);
                float winterAdjustedCost = rawCostPerLevel * burdenMult;

                groupLines.Add($"{count}x {species} (Value: {wealth:F0} silver, Total Trained Steps: {steps}, Winter-Adjusted Nutrition Cost/Step: {winterAdjustedCost:F2})");
            }
            return groupLines.Any() ? string.Join(", ", groupLines) : "None";
        }

        private static void GatherGreenhouseMetrics(Map map, out int activeHydroponics, out int activeSunLamps, out int activeSkylightsCount, out int greenhouseCells, out string trendText)
        {
            activeHydroponics = 0;
            activeSunLamps = 0;
            activeSkylightsCount = 0;
            greenhouseCells = 0;
            trendText = "Stable";

            try
            {
                if (Find.World != null)
                {
                    var coreComp = Find.World.GetComponent<SynapseCoreWorldComponent>();
                    if (coreComp != null)
                    {
                        if (coreComp.mapGreenhouseCells != null)
                        {
                            coreComp.mapGreenhouseCells.TryGetValue(map.uniqueID, out greenhouseCells);
                        }

                        var history = coreComp.GetHistoryForMap(map.uniqueID);
                        if (history != null && history.Count >= 2)
                        {
                            int first = history[0];
                            int last = history[history.Count - 1];
                            if (last > first)
                            {
                                trendText = $"Growing (from {first} to {last} cells)";
                            }
                            else if (last < first)
                            {
                                trendText = $"Collapsing (from {first} down to {last} cells)";
                            }
                            else
                            {
                                trendText = $"Stable at {last} cells";
                            }
                        }
                    }
                }

                if (map.listerBuildings != null)
                {
                    var hydroDef = ThingDefOf.HydroponicsBasin;
                    if (hydroDef != null)
                    {
                        foreach (var b in map.listerBuildings.AllBuildingsColonistOfDef(hydroDef))
                        {
                            var power = b.TryGetComp<CompPowerTrader>();
                            if (power == null || power.PowerOn)
                            {
                                activeHydroponics++;
                            }
                        }
                    }

                    if (map.listerBuildings.allBuildingsColonist != null)
                    {
                        foreach (var b in map.listerBuildings.allBuildingsColonist)
                        {
                            if (b?.def?.defName == null) continue;

                            string defNameLower = b.def.defName.ToLowerInvariant();
                            bool isSunLamp = defNameLower.Contains("sunlamp");
                            bool isSkylight = defNameLower.Contains("skylight") || defNameLower.Contains("glassroof");

                            if (isSunLamp || isSkylight)
                            {
                                var power = b.TryGetComp<CompPowerTrader>();
                                if (power != null && !power.PowerOn) continue;

                                if (isSunLamp)
                                {
                                    activeSunLamps++;
                                }
                                else
                                {
                                    activeSkylightsCount++;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static void CountLegendaryArt(Map map, out int count, out float value)
        {
            count = 0;
            value = 0f;

            try
            {
                if (map.listerBuildings?.allBuildingsColonist != null)
                {
                    foreach (var b in map.listerBuildings.allBuildingsColonist)
                    {
                        if (b != null && b.def.IsArt)
                        {
                            var quality = b.TryGetComp<CompQuality>();
                            if (quality != null && quality.Quality == QualityCategory.Legendary)
                            {
                                count++;
                                value += b.MarketValue;
                            }
                        }
                    }
                }

                var haulables = map.listerThings?.ThingsInGroup(ThingRequestGroup.HaulableAlways);
                if (haulables != null)
                {
                    foreach (var t in haulables)
                    {
                        if (t == null) continue;
                        Thing innerThing = t is MinifiedThing minified ? minified.InnerThing : t;

                        if (innerThing != null && innerThing.def.IsArt)
                        {
                            var quality = innerThing.TryGetComp<CompQuality>();
                            if (quality != null && quality.Quality == QualityCategory.Legendary)
                            {
                                count++;
                                value += innerThing.MarketValue;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static string FormatMetricsOutput(Map map, int freeColonists, float totalWealth, int silverCount,
            float totalNutrition, float growablePercent, float burdenMult, int greenhouseCells, int activeHydroponics,
            int activeSunLamps, int activeSkylightsCount, string trendText, int tameAnimalsCount, float tameAnimalsWealth,
            string animalReport, int legendaryArtCount, float legendaryArtValue, int popDensity,
            int capablePawns, int armedPawns, int downedPawns, float avgMood)
        {
            var storytellerComp = Find.Storyteller?.storytellerComps?.OfType<StorytellerComp_Storyteller>().FirstOrDefault();
            var props = storytellerComp?.props as StorytellerCompProperties_Storyteller;

            string metricsFormat = props?.metricsTemplate;
            if (string.IsNullOrEmpty(metricsFormat))
            {
                metricsFormat = @"Colony General Metrics:
- Wealth: {wealth} | Silver: {silver} | Food: {food} points
- Season: {growingSeason} growable (Winter Burden: {winterBurden}x)
- Greenhouse: {greenhouse}
- Population: {population} colonists (Local Density: {popDensity})
- Livestock: {livestock}
- Legendary Art: {legendaryArt} pieces ({legendaryArtValue} silver)
- Combat: {combat} | Downed: {medical} | Avg Mood: {mood}";
            }

            string livestockFormat = props?.livestockTemplate;
            if (string.IsNullOrEmpty(livestockFormat))
            {
                livestockFormat = "{tameCount} tamed animals (Total Value: {tameWealth} silver)";
            }

            string greenhouseFormat = props?.greenhouseTemplate;
            if (string.IsNullOrEmpty(greenhouseFormat))
            {
                greenhouseFormat = "{greenhouseCells} cells (Hydroponics: {activeHydroponics}, Sun Lamps: {activeSunLamps}, Trend: {trend})";
            }

            string greenhouseText = greenhouseFormat
                .Replace("{greenhouseCells}", greenhouseCells.ToString())
                .Replace("{activeHydroponics}", activeHydroponics.ToString())
                .Replace("{activeSunLamps}", activeSunLamps.ToString())
                .Replace("{activeSkylightsCount}", activeSkylightsCount.ToString())
                .Replace("{trend}", trendText);

            string livestockText = livestockFormat
                .Replace("{tameCount}", tameAnimalsCount.ToString())
                .Replace("{tameWealth}", tameAnimalsWealth.ToString("F0"))
                .Replace("{animalReport}", animalReport);

            var coreWorldComp = Find.World?.GetComponent<SynapseCoreWorldComponent>();
            string growthMetric = "";
            string lastRaidReport = "";

            if (coreWorldComp != null)
            {
                growthMetric = BuildGrowthMetric(map, freeColonists, coreWorldComp, props);
                lastRaidReport = BuildLastRaidReport(coreWorldComp);
            }

            return (metricsFormat
                .Replace("{wealth}", totalWealth.ToString("F0"))
                .Replace("{silver}", silverCount.ToString())
                .Replace("{food}", totalNutrition.ToString("F1"))
                .Replace("{growingSeason}", growablePercent.ToString("P0"))
                .Replace("{winterBurden}", burdenMult.ToString("F1"))
                .Replace("{greenhouse}", greenhouseText)
                .Replace("{population}", freeColonists.ToString())
                .Replace("{popDensity}", popDensity.ToString())
                .Replace("{livestock}", livestockText)
                .Replace("{legendaryArt}", legendaryArtCount.ToString())
                .Replace("{legendaryArtValue}", legendaryArtValue.ToString("F0"))
                .Replace("{combat}", $"{capablePawns} violent ({armedPawns} armed)")
                .Replace("{medical}", $"{downedPawns} downed")
                .Replace("{mood}", avgMood.ToString("P0")))
                + growthMetric + lastRaidReport;
        }

        private static string BuildGrowthMetric(Map map, int freeColonists, SynapseCoreWorldComponent coreWorldComp, StorytellerCompProperties_Storyteller props)
        {
            if (coreWorldComp.wealthHistory == null || coreWorldComp.wealthHistory.Count < 2) return "";

            var oldest = coreWorldComp.wealthHistory[0];
            int tickDiff = Find.TickManager.TicksGame - oldest.gameTick;
            float days = (float)tickDiff / 60000f;
            if (days < 0.5f) return "";

            float currentTrueWealth = coreWorldComp.CalculateTrueWealth(map, props);
            float wealthDiff = currentTrueWealth - oldest.wealth;
            float avgColonists = 0.5f * (freeColonists + oldest.pawnCount);
            if (avgColonists < 1) avgColonists = 1;
            float actualRate = wealthDiff / (avgColonists * days);
            
            string result = $"\n- Daily True Wealth Growth Rate (Rolling): {actualRate:F0} silver/colonist/day";
            if (props != null)
            {
                float daysPassed = Find.TickManager.TicksGame / 60000f;
                float targetRate = props.targetWealthGrowthFactor * (float)System.Math.Pow(daysPassed, props.targetWealthGrowthExponent) + props.targetWealthGrowthBase;
                float blendedTargetRate = UnityEngine.Mathf.Lerp(targetRate, actualRate, props.pacingFlexibility);
                if (blendedTargetRate > 0)
                {
                    result += $" (Blended Target: {blendedTargetRate:F0} silver/colonist/day, Flexibility: {props.pacingFlexibility:F2})";
                }
            }
            return result;
        }

        private static string BuildLastRaidReport(SynapseCoreWorldComponent coreWorldComp)
        {
            if (coreWorldComp.lastRaidOutcome == null) return "";

            float daysSinceRaid = (Find.TickManager.TicksGame - coreWorldComp.lastRaidOutcome.gameTick) / 60000f;
            int k = coreWorldComp.lastRaidOutcome.colonistsKilled;
            int inj = coreWorldComp.lastRaidOutcome.colonistsInjured;
            int kid = coreWorldComp.lastRaidOutcome.colonistsKidnapped;
            int ek = coreWorldComp.lastRaidOutcome.enemiesKilled;
            int ed = coreWorldComp.lastRaidOutcome.enemiesDowned;
            
            string report = $"\n- Last Raid Outcome ({daysSinceRaid:F1} days ago): ";
            if (k == 0 && kid == 0)
            {
                report += $"Successful defense. Enemies: {ek} killed, {ed} downed. Player loss: {inj} injured, 0 dead/kidnapped.";
            }
            else
            {
                report += $"Tragic defense (Colony Recovering). Player loss: {k} killed, {kid} kidnapped, {inj} injured. Enemies: {ek} killed, {ed} downed.";
            }
            return report;
        }
    }
}
