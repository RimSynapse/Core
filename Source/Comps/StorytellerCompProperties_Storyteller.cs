using System.Collections.Generic;
using RimWorld;

namespace RimSynapse.Comps
{
    public class IncidentModifierRule
    {
        public string aspect;
        public string aspectKey;
        public float threshold;
        public string comparison;
        public float multiplier = 1.0f;
        public string contextNote;
    }

    public class IncidentWeightConfig
    {
        public string incidentDefName;
        public float baseWeight = 1.0f;
        public string description;
        public string thematicGuide;
        public List<IncidentModifierRule> rules = new List<IncidentModifierRule>();
    }

    public class StorytellerCompProperties_Storyteller : StorytellerCompProperties
    {
        public float incidentsTargetDays = 10f;
        public float threatsTargetDays = 10f;

        public string pacingSystemPrompt;
        public string selectionSystemPrompt;

        // Wealth Growth Pacing Formula: Factor * (DaysPassed ^ Exponent) + Base
        public float targetWealthGrowthBase = 1000f;
        public float targetWealthGrowthFactor = 0f;
        public float targetWealthGrowthExponent = 1.0f;

        // Pacing Flexibility: 0.0 (Strict absolute pacing target) to 1.0 (Complete flexibility, vanilla wealth-only)
        public float pacingFlexibility = 0.2f;

        // True Wealth Weights
        public float weightItemWealth = 1.0f;
        public float weightBuildingWealth = 1.0f;
        public float weightPawnWealth = 1.0f;
        public float weightCombatCompetence = 1.0f;
        public float weightSecurityPower = 1.0f;

        // Personality and Writing Style
        public string characterName = "AI Storyteller";
        public string speakingStyle = "sassy, dramatic, or menacing";

        // The persona prose injected into both Storyteller and Chat context (Core #66) —
        // who this storyteller is, beyond a name and a style line. Multi-line XML text;
        // null means only name/style are injected. The full voice bible is Core #89 (0.10).
        public string personalityProfile;

        // Base Category Weights
        public float baseWeightThreatBig = 2.0f;
        public float baseWeightThreatSmall = 1.0f;
        public float baseWeightDiseaseHuman = 0.5f;
        public float baseWeightMisc = 3.0f;
        public float baseWeightDiseaseAnimal = 0.2f;
        public float baseWeightOrbitalVisitor = 1.0f;
        public float baseWeightFactionArrival = 1.0f;

        // Hostile Faction Motivation Conditions
        public float motivatedRaidGreedRatioThreshold = 3.0f;
        public float motivatedRaidBaseChance = 0.2f;
        public float motivatedRaidStrengthIncrease = 500.0f;

        // Factions Mod - Population Density Integration (Graceful degradation if Factions is not loaded)
        public float motivatedRaidPopulationDensityFactor = 0.005f;
        public float populationDensityJoinBase = 0.5f;
        public float populationDensityJoinFactor = 0.005f;

        // List of specific incident weights and descriptions
        public List<IncidentWeightConfig> incidentWeights = new List<IncidentWeightConfig>();

        public string metricsTemplate = @"Colony General and Resource Metrics:
- Overall Wealth: {wealth} (Items, buildings, and pawns)
- Available Silver: {silver} (Stored or mined on map)
- Food Reserves: {food} nutrition points
- Growing Season: {growingSeason} of the year growable (Winter Resource Burden Multiplier: {winterBurden}x)
- Greenhouse Capacity: {greenhouse}
- Population: {population} colonists
- Livestock: {livestock}
- Legendary Art: {legendaryArt} pieces (Total Value: {legendaryArtValue} silver)
- Combat Capability: {combat}
- Medical Status: {medical}
- Average Mood: {mood}";

        public string livestockTemplate = "{tameCount} tamed animals (Total Value: {tameWealth} silver)\n  - Detail: {animalReport}";

        public string greenhouseTemplate = "{greenhouseCells} active growable cells at midday (Hydroponics: {activeHydroponics} active basins, Sun Lamps: {activeSunLamps} powered, Skylights/Solar Roofs: {activeSkylightsCount}, Trend: {trend})";

        public StorytellerCompProperties_Storyteller()
        {
            this.compClass = typeof(StorytellerComp_Storyteller);
        }
    }
}
