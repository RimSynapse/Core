using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimSynapse.Core.Patches
{
    [HarmonyPatch(typeof(PlayerKnowledgeDatabase), nameof(PlayerKnowledgeDatabase.GetKnowledge))]
    public static class Patch_PlayerKnowledgeDatabase_GetKnowledge
    {
        private static readonly FieldInfo dataField = AccessTools.Field(typeof(PlayerKnowledgeDatabase), "data");
        private static FieldInfo knowledgeField;

        [HarmonyPrefix]
        public static void Prefix(ConceptDef def)
        {
            if (def == null || string.IsNullOrEmpty(def.defName)) return;
            EnsureKeyExists(def.defName);
        }

        public static void EnsureKeyExists(string defName)
        {
            try
            {
                if (dataField == null) return;
                object dataObj = dataField.GetValue(null);
                if (dataObj == null) return;

                if (knowledgeField == null)
                {
                    knowledgeField = AccessTools.Field(dataObj.GetType(), "knowledge");
                }

                if (knowledgeField != null)
                {
                    var dict = knowledgeField.GetValue(dataObj) as IDictionary;
                    if (dict != null && !dict.Contains(defName))
                    {
                        dict[defName] = 0f;
                    }
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(PlayerKnowledgeDatabase), nameof(PlayerKnowledgeDatabase.SetKnowledge))]
    public static class Patch_PlayerKnowledgeDatabase_SetKnowledge
    {
        [HarmonyPrefix]
        public static void Prefix(ConceptDef def)
        {
            if (def == null || string.IsNullOrEmpty(def.defName)) return;
            Patch_PlayerKnowledgeDatabase_GetKnowledge.EnsureKeyExists(def.defName);
        }
    }

    [HarmonyPatch(typeof(PlayerKnowledgeDatabase), nameof(PlayerKnowledgeDatabase.IsComplete))]
    public static class Patch_PlayerKnowledgeDatabase_IsComplete
    {
        [HarmonyPrefix]
        public static void Prefix(ConceptDef def)
        {
            if (def == null || string.IsNullOrEmpty(def.defName)) return;
            Patch_PlayerKnowledgeDatabase_GetKnowledge.EnsureKeyExists(def.defName);
        }
    }
}
