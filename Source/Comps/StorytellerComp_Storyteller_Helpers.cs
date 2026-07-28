using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimSynapse.Comps
{
    /// <summary>
    /// Helper methods for the storyteller component:
    /// Faction motivation checks, category selection, and LLM-weighted incident picking.
    /// </summary>
    public partial class StorytellerComp_Storyteller
    {


        /// <summary>
        /// Selects an incident category (ThreatBig, Misc, Disease, etc.) using
        /// base weights modified by the LLM's category multipliers.
        /// </summary>
        private IncidentCategoryDef ChooseCategory(IIncidentTarget target, SynapseCoreWorldComponent worldComp)
        {
            var weights = new Dictionary<IncidentCategoryDef, float>();
            
            weights[IncidentCategoryDefOf.ThreatBig] = Props.baseWeightThreatBig;
            weights[IncidentCategoryDefOf.ThreatSmall] = Props.baseWeightThreatSmall;
            weights[IncidentCategoryDefOf.DiseaseHuman] = Props.baseWeightDiseaseHuman;
            weights[IncidentCategoryDefOf.Misc] = Props.baseWeightMisc;
            
            var diseaseAnimal = DefDatabase<IncidentCategoryDef>.GetNamedSilentFail("DiseaseAnimal");
            if (diseaseAnimal != null) weights[diseaseAnimal] = Props.baseWeightDiseaseAnimal;

            var orbitalVisitor = DefDatabase<IncidentCategoryDef>.GetNamedSilentFail("OrbitalVisitor");
            if (orbitalVisitor != null) weights[orbitalVisitor] = Props.baseWeightOrbitalVisitor;

            var factionArrival = DefDatabase<IncidentCategoryDef>.GetNamedSilentFail("FactionArrival");
            if (factionArrival != null) weights[factionArrival] = Props.baseWeightFactionArrival;

            if (worldComp != null)
            {
                foreach (var category in weights.Keys.ToList())
                {
                    weights[category] *= worldComp.GetCategoryMultiplier(category.defName);
                }
            }

            if (target.Tile >= 0)
            {
                int pop = SynapseCoreProviders.PopulationDensityAt(target.Tile);
                float raidMult = 1f / (1f + Props.motivatedRaidPopulationDensityFactor * pop);
                float joinMult = Props.populationDensityJoinBase + (Props.populationDensityJoinFactor * pop);
                joinMult = UnityEngine.Mathf.Clamp(joinMult, 0.1f, 5.0f);

                if (weights.ContainsKey(IncidentCategoryDefOf.ThreatBig))
                {
                    weights[IncidentCategoryDefOf.ThreatBig] *= raidMult;
                }
                if (weights.ContainsKey(IncidentCategoryDefOf.ThreatSmall))
                {
                    weights[IncidentCategoryDefOf.ThreatSmall] *= raidMult;
                }
                if (weights.ContainsKey(IncidentCategoryDefOf.Misc))
                {
                    weights[IncidentCategoryDefOf.Misc] *= joinMult;
                }

                if (factionArrival != null && weights.ContainsKey(factionArrival))
                {
                    weights[factionArrival] *= joinMult;
                }
            }

            return weights.RandomElementByWeightWithFallback(kvp => kvp.Value, default).Key;
        }
    }
}
