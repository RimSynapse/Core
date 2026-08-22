using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;
using RimSynapse.Utils;
using RimSynapse.Models;
using Newtonsoft.Json;

namespace RimSynapse.Comps
{
    public class PacingAdjustmentResult
    {
        public float PacingMultiplier = 1.0f;
        public Dictionary<string, float> CategoryMultipliers = new Dictionary<string, float>();
        public List<string> RequestTools = null;
    }

    public class EventSelectionResult
    {
        public string IncidentDefName;
        public List<string> RequestTools = null;
    }

    /// <summary>
    /// AI Storyteller opportunistic query system.
    /// Handles LLM-driven pacing adjustments and event selection.
    /// Split into partial class files for maintainability.
    /// </summary>
    public static partial class SynapseStorytellerOpportunistic
    {
        private static TimeSpeed _preQuerySpeed = TimeSpeed.Normal;
        private static int _activeQueriesCount = 0;
        private static readonly object _speedLock = new object();

        private static void PauseForTelemetry()
        {
            if (RimSynapseMod.Instance?.Settings?.enableTrainingMode != true || RimSynapseMod.Instance?.Settings?.fastTelemetryMode != true)
                return;

            lock (_speedLock)
            {
                if (_activeQueriesCount == 0)
                {
                    _preQuerySpeed = Find.TickManager.CurTimeSpeed;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                }
                _activeQueriesCount++;
            }
        }

        private static void ResumeAfterTelemetry()
        {
            if (RimSynapseMod.Instance?.Settings?.enableTrainingMode != true || RimSynapseMod.Instance?.Settings?.fastTelemetryMode != true)
                return;

            lock (_speedLock)
            {
                _activeQueriesCount = Math.Max(0, _activeQueriesCount - 1);
                if (_activeQueriesCount == 0)
                {
                    Find.TickManager.CurTimeSpeed = _preQuerySpeed;
                }
            }
        }

        private static string GetToolsTextList()
        {
            // The prompt's tool list IS the enforced list: the storyteller vocabulary
            // (Core #63). The old behaviour listed every registered tool once the token
            // budget was large enough, which advertised tools the executor now refuses.
            var list = new List<string>();
            foreach (var tool in SynapseToolRegistry.NonDebugTools)
            {
                if (SynapseToolVocabulary.IsAllowed(SynapseToolVocabulary.StorytellerScope, tool.name))
                {
                    list.Add($"- '{tool.name}': {tool.description}");
                }
            }
            return string.Join("\n", list);
        }

        private static void ApplyPacingAdjustment(PacingAdjustmentResult parsed)
        {
            if (parsed == null) return;
            var comp = Find.World.GetComponent<SynapseCoreWorldComponent>();
            if (comp != null)
            {
                comp.GlobalPacingMultiplier = UnityEngine.Mathf.Clamp(parsed.PacingMultiplier, 0.1f, 5.0f);
                if (parsed.CategoryMultipliers != null)
                {
                    foreach (var kvp in parsed.CategoryMultipliers)
                    {
                        comp.categoryMultipliers[kvp.Key] = UnityEngine.Mathf.Clamp(kvp.Value, 0.01f, 10.0f);
                    }
                }
                RimSynapse.SynapseLogger.Message($"[RimSynapse-Core] Storyteller pacing adjusted to {comp.GlobalPacingMultiplier} with category multipliers updated.");
            }
        }

        private static void ApplyEventSelection(EventSelectionResult parsed, IIncidentTarget target)
        {
            if (parsed != null && !string.IsNullOrEmpty(parsed.IncidentDefName))
            {
                IncidentDef def = DefDatabase<IncidentDef>.GetNamedSilentFail(parsed.IncidentDefName);
                if (def != null)
                {
                    IncidentParms parms = StorytellerUtility.DefaultParmsNow(def.category, target);
                    if (def.Worker.CanFireNow(parms))
                    {
                        Find.Storyteller.incidentQueue.Add(def, Find.TickManager.TicksGame, parms);
                        RimSynapse.SynapseLogger.Message($"[RimSynapse-Core] Storyteller Event Selection chose: {parsed.IncidentDefName}");
                    }
                }
            }
        }

        private static float GetAspectValue(string aspect, string aspectKey, Map map, SynapseCoreWorldComponent worldComp)
        {
            switch (aspect)
            {
                case "ColonyWealth":
                    return map?.wealthWatcher?.WealthTotal ?? 0f;
                case "CombatReadiness":
                    if (map?.mapPawns?.FreeColonists == null || !map.mapPawns.FreeColonists.Any()) return 1.0f;
                    int capable = map.mapPawns.FreeColonists.Count(p => !p.Dead && !p.Downed && !p.WorkTagIsDisabled(WorkTags.Violent));
                    return (float)capable / map.mapPawns.FreeColonists.Count;
                case "ColonistCount":
                    return map?.mapPawns?.FreeColonistsCount ?? 0;
                case "AverageMood":
                    if (map?.mapPawns?.FreeColonists == null || !map.mapPawns.FreeColonists.Any()) return 0.5f;
                    return (float)map.mapPawns.FreeColonists.Average(p => p.needs?.mood?.CurLevelPercentage ?? 0.5f);
                case "PopulationDensity":
                    if (map == null) return 0f;
                    return SynapseCoreProviders.PopulationDensityAt(map.Tile);
                case "FoodReserves":
                    float totalNutrition = 0f;
                    if (map?.listerThings != null)
                    {
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
                    }
                    return totalNutrition;
                case "SilverReserves":
                    int silverCount = 0;
                    if (map?.listerThings != null)
                    {
                        foreach (var thing in map.listerThings.ThingsOfDef(ThingDefOf.Silver))
                        {
                            silverCount += thing.stackCount;
                        }
                    }
                    return silverCount;
                case "RecentIncidentCount":
                    if (worldComp == null || string.IsNullOrEmpty(aspectKey)) return 0f;
                    return worldComp.GetRecentIncidentCount(aspectKey, 900000); // 15 days lookback
                case "TimePassed":
                    return Find.TickManager.TicksGame / 60000f; // Days passed
                case "LastRaidColonistCasualties":
                    if (worldComp?.lastRaidOutcome != null)
                    {
                        return worldComp.lastRaidOutcome.colonistsKilled + worldComp.lastRaidOutcome.colonistsKidnapped;
                    }
                    return 0f;
                case "LastRaidEnemyCasualties":
                    if (worldComp?.lastRaidOutcome != null)
                    {
                        return worldComp.lastRaidOutcome.enemiesKilled + worldComp.lastRaidOutcome.enemiesDowned;
                    }
                    return 0f;
                case "LastRaidWasSuccess":
                    if (worldComp?.lastRaidOutcome != null)
                    {
                        bool playerLoss = (worldComp.lastRaidOutcome.colonistsKilled + worldComp.lastRaidOutcome.colonistsKidnapped) > 0;
                        bool enemyLoss = (worldComp.lastRaidOutcome.enemiesKilled + worldComp.lastRaidOutcome.enemiesDowned) > 0;
                        return (enemyLoss && !playerLoss) ? 1f : 0f;
                    }
                    return 0f;
                case "LastRaidWasFailure":
                    if (worldComp?.lastRaidOutcome != null)
                    {
                        bool playerLoss = (worldComp.lastRaidOutcome.colonistsKilled + worldComp.lastRaidOutcome.colonistsKidnapped) > 0;
                        return playerLoss ? 1f : 0f;
                    }
                    return 0f;
                case "RollingWealthGrowthRate":
                    if (worldComp?.wealthHistory != null && worldComp.wealthHistory.Count >= 2)
                    {
                        var oldest = worldComp.wealthHistory[0];
                        int tickDiff = Find.TickManager.TicksGame - oldest.gameTick;
                        float days = (float)tickDiff / 60000f;
                        if (days >= 0.5f)
                        {
                            var props = StorytellerComp_Storyteller.GetActiveStorytellerProps();
                            float currentTrueWealth = worldComp.CalculateTrueWealth(map, props);
                            float wealthDiff = currentTrueWealth - oldest.wealth;
                            float avgColonists = 0.5f * (map.mapPawns.FreeColonistsCount + oldest.pawnCount);
                            if (avgColonists < 1) avgColonists = 1;
                            return wealthDiff / (avgColonists * days);
                        }
                    }
                    return 0f;
                default:
                    return 0f;
            }
        }

        private static bool EvaluateRule(IncidentModifierRule rule, Map map, SynapseCoreWorldComponent worldComp)
        {
            if (rule == null) return false;
            float val = GetAspectValue(rule.aspect, rule.aspectKey, map, worldComp);
            switch (rule.comparison)
            {
                case "LessThan":
                    return val < rule.threshold;
                case "GreaterThan":
                    return val > rule.threshold;
                case "Equals":
                    return Math.Abs(val - rule.threshold) < 0.001f;
                default:
                    return false;
            }
        }
    }
}
