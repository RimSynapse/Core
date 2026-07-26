using System;
using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;

namespace RimSynapse.Utils
{
    public static class SynapseRecruitmentMath
    {
        public static float CalculateRecruitmentChance(Pawn recruiter, Pawn recruit, float baseChance)
        {
            // Start with base chance
            float chance = baseChance;

            // 1. Tri-state recruitment calculation if psychology is installed
            float psychModifier = GetPsychologyRecruitmentModifier(recruiter, recruit);
            chance += psychModifier;

            // 2. Bed and Room Wealth calculation (if psychology is installed)
            if (IsPsychologyInstalled())
            {
                float roomWealthModifier = GetRoomWealthModifier(recruit);
                chance += roomWealthModifier;
            }

            return Mathf.Clamp(chance, 0.01f, 0.99f);
        }

        public static bool IsPsychologyInstalled()
        {
            return Type.GetType("RimSynapse.Psychology.Comps.SynapsePawnComp, RimSynapsePsychology") != null;
        }

        public static float GetPsychologyRecruitmentModifier(Pawn recruiter, Pawn recruit)
        {
            try
            {
                var compType = Type.GetType("RimSynapse.Psychology.Comps.SynapsePawnComp, RimSynapsePsychology");
                if (compType == null) return 0f;

                var comp = recruit.AllComps.FirstOrDefault(c => c.GetType() == compType);
                if (comp == null) return 0f;

                var socialNetworkField = compType.GetField("socialNetwork");
                if (socialNetworkField == null) return 0f;

                var socialNetwork = socialNetworkField.GetValue(comp) as System.Collections.IDictionary;
                if (socialNetwork == null || !socialNetwork.Contains(recruiter.GetUniqueLoadID())) return 0f;

                var record = socialNetwork[recruiter.GetUniqueLoadID()];
                if (record == null) return 0f;

                var recordType = record.GetType();
                var trustField = recordType.GetField("trust");
                var familiarityField = recordType.GetField("familiarity");

                float trust = trustField != null ? (float)trustField.GetValue(record) : 0f; // -100 to 100
                float familiarity = familiarityField != null ? (float)familiarityField.GetValue(record) : 0f; // 0 to 100
                float opinion = recruit.relations != null ? recruit.relations.OpinionOf(recruiter) : 0f; // -100 to 100

                // Tri-state recruitment calculation:
                // Trust adds up to +20% / -20%
                // Familiarity adds up to +10%
                // Opinion adds up to +20% / -20%
                float modifier = (trust / 100f) * 0.20f + (familiarity / 100f) * 0.10f + (opinion / 100f) * 0.20f;
                
                // Subtract the vanilla opinion factor to avoid double-counting it
                float vanillaOpinionFactor = (opinion / 100f) * 0.20f;
                return modifier - vanillaOpinionFactor;
            }
            catch (Exception ex)
            {
                SynapseLogger.Warn("core", "Error in GetPsychologyRecruitmentModifier: " + ex.Message);
                return 0f;
            }
        }

        public static float GetRoomWealthModifier(Pawn recruit)
        {
            if (recruit.Map == null) return 0f;

            // 1. Find a free bed owned by player faction on this map
            Building_Bed freeBed = null;
            foreach (var building in recruit.Map.listerBuildings.allBuildingsColonist)
            {
                var bed = building as Building_Bed;
                if (bed != null && bed.def.building.bed_humanlike && !bed.Medical && !bed.OwnersForReading.Any() && bed.Faction == Faction.OfPlayer)
                {
                    freeBed = bed;
                    break;
                }
            }

            if (freeBed == null) return 0f;

            // 2. Get wealth of target room
            float targetRoomWealth = 0f;
            var targetRoom = freeBed.GetRoom();
            if (targetRoom != null)
            {
                targetRoomWealth = targetRoom.GetStat(RoomStatDefOf.Wealth);
            }

            // 3. Get wealth of current room (if they have an assigned bed)
            float currentRoomWealth = 0f;
            var currentBed = recruit.ownership?.OwnedBed;
            if (currentBed != null)
            {
                var currentRoom = currentBed.GetRoom();
                if (currentRoom != null)
                {
                    currentRoomWealth = currentRoom.GetStat(RoomStatDefOf.Wealth);
                }
            }

            // 4. Compare wealth improvement
            float wealthDifference = targetRoomWealth - currentRoomWealth;
            if (wealthDifference > 0f)
            {
                // Significant improvement adds up to +30% chance!
                // Scale: every 500 wealth adds 6%, maxing out at +30% (at 2500 wealth improvement).
                float roomBonus = Mathf.Min(0.30f, (wealthDifference / 2500f) * 0.30f);
                return roomBonus;
            }

            return 0f;
        }
    }
}
