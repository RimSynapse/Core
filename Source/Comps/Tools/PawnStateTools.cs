using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Newtonsoft.Json;

namespace RimSynapse
{
    /// <summary>
    /// Tool handler: modify_pawn_state
    /// Applies direct modifications to colonist health, traits, skills, and ideology.
    /// </summary>
    public static partial class SynapseToolRegistry
    {
        private static void RegisterPawnStateTools()
        {
            RegisterTool(
                "modify_pawn_state",
                "Apply direct modifications to a target pawn or animal's health (hediffs, kill, damage), traits, skills, or conversion to an ideology.",
                new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["pawnName"] = new Dictionary<string, string>
                        {
                            ["type"] = "string",
                            ["description"] = "The name of the target pawn/animal."
                        },
                        ["thingId"] = new Dictionary<string, string>
                        {
                            ["type"] = "string",
                            ["description"] = "The unique load ID (ThingID) of the target pawn/animal (preferred over pawnName)."
                        },
                        ["action"] = new Dictionary<string, string>
                        {
                            ["type"] = "string",
                            ["description"] = "The modification type to perform: 'kill', 'damage', 'add_hediff', 'remove_body_part', 'convert', 'add_trait', 'remove_trait', 'set_skill'."
                        },
                        ["damageAmount"] = new Dictionary<string, string>
                        {
                            ["type"] = "integer",
                            ["description"] = "Optional damage points to deal for 'damage' action."
                        },
                        ["hediffName"] = new Dictionary<string, string>
                        {
                            ["type"] = "string",
                            ["description"] = "The Def name of the hediff to add (e.g. Cut, WoundInfection, Flu, Catatonic)."
                        },
                        ["bodyPart"] = new Dictionary<string, string>
                        {
                            ["type"] = "string",
                            ["description"] = "The Def name of the body part to modify or remove (e.g. LeftArm, RightEye, Brain)."
                        },
                        ["severity"] = new Dictionary<string, string>
                        {
                            ["type"] = "number",
                            ["description"] = "Optional severity for added hediffs (usually 0.0 to 1.0)."
                        },
                        ["ideoName"] = new Dictionary<string, string>
                        {
                            ["type"] = "string",
                            ["description"] = "The name of the target ideology for conversion."
                        },
                        ["traitName"] = new Dictionary<string, string>
                        {
                            ["type"] = "string",
                            ["description"] = "The Def name of the trait to add or remove."
                        },
                        ["degree"] = new Dictionary<string, string>
                        {
                            ["type"] = "integer",
                            ["description"] = "Optional degree for added traits."
                        },
                        ["skillName"] = new Dictionary<string, string>
                        {
                            ["type"] = "string",
                            ["description"] = "The Def name of the skill to set."
                        },
                        ["level"] = new Dictionary<string, string>
                        {
                            ["type"] = "integer",
                            ["description"] = "Optional level of the skill to set (0 to 20)."
                        },
                        ["passion"] = new Dictionary<string, string>
                        {
                            ["type"] = "string",
                            ["description"] = "Optional passion level to set for 'set_skill' action: 'None', 'Minor', 'Major' (Major is burning/major passion)."
                        }
                    },
                    ["required"] = new List<string> { "action" }
                },
                args =>
                {
                    if (Find.CurrentMap == null) return "{\"success\": false, \"reason\": \"No active map loaded.\"}";
                    try
                    {
                        var parsedArgs = JsonConvert.DeserializeObject<Dictionary<string, object>>(args);
                        if (parsedArgs == null || !parsedArgs.TryGetValue("action", out var actionVal))
                        {
                            return "{\"success\": false, \"reason\": \"Missing required argument 'action'.\"}";
                        }

                        string pawnName = parsedArgs.TryGetValue("pawnName", out var pawnVal) ? pawnVal?.ToString() : null;
                        string thingId = parsedArgs.TryGetValue("thingId", out var idVal) ? idVal?.ToString() : null;
                        string action = actionVal?.ToString();

                        Pawn pawn = null;
                        if (!string.IsNullOrEmpty(thingId))
                        {
                            pawn = Find.CurrentMap.mapPawns.AllPawns.FirstOrDefault(p => p.ThingID == thingId);
                        }
                        if (pawn == null && !string.IsNullOrEmpty(pawnName))
                        {
                            pawn = Find.CurrentMap.mapPawns.AllPawns.FirstOrDefault(p => p.LabelShort.Equals(pawnName, StringComparison.OrdinalIgnoreCase));
                        }

                        if (pawn == null)
                        {
                            return $"{{\"success\": false, \"reason\": \"Target pawn/animal '{thingId ?? pawnName}' not found on active map.\"}}";
                        }

                        return ExecutePawnStateAction(pawn, pawn.LabelShort, action, parsedArgs);
                    }
                    catch (Exception ex)
                    {
                        return $"{{\"success\": false, \"reason\": \"Modifying pawn state failed: {ex.Message}\"}}";
                    }
                },
                true
            );
        }

        private static string ExecutePawnStateAction(Pawn pawn, string pawnName, string action, Dictionary<string, object> parsedArgs)
        {
            string hediffName = parsedArgs.TryGetValue("hediffName", out var hn) ? hn?.ToString() : null;
            string bodyPart = parsedArgs.TryGetValue("bodyPart", out var bp) ? bp?.ToString() : null;
            float? severity = null;
            if (parsedArgs.TryGetValue("severity", out var sevVal) && sevVal != null && float.TryParse(sevVal.ToString(), out float fSev)) severity = fSev;
            
            string ideoName = parsedArgs.TryGetValue("ideoName", out var idn) ? idn?.ToString() : null;
            string traitName = parsedArgs.TryGetValue("traitName", out var trn) ? trn?.ToString() : null;
            int? degree = null;
            if (parsedArgs.TryGetValue("degree", out var degVal) && degVal != null && int.TryParse(degVal.ToString(), out int iDeg)) degree = iDeg;

            string skillName = parsedArgs.TryGetValue("skillName", out var skn) ? skn?.ToString() : null;
            int? level = null;
            if (parsedArgs.TryGetValue("level", out var lvlVal) && lvlVal != null && int.TryParse(lvlVal.ToString(), out int iLvl)) level = iLvl;

            if (action.Equals("kill", StringComparison.OrdinalIgnoreCase))
            {
                pawn.Kill(null, null);
                return $"{{\"success\": true, \"message\": \"Successfully killed pawn/animal '{pawnName}'.\"}}";
            }
            else if (action.Equals("damage", StringComparison.OrdinalIgnoreCase))
            {
                int dmg = 20;
                if (parsedArgs.TryGetValue("damageAmount", out var dVal) && dVal != null && int.TryParse(dVal.ToString(), out int iDmg)) dmg = iDmg;
                pawn.TakeDamage(new DamageInfo(DamageDefOf.Bomb, dmg));
                return $"{{\"success\": true, \"message\": \"Successfully dealt {dmg} damage to '{pawnName}'.\"}}";
            }
            else if (action.Equals("add_hediff", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(hediffName)) return "{\"success\": false, \"reason\": \"Missing 'hediffName' parameter.\"}";
                HediffDef hediffDef = DefDatabase<HediffDef>.GetNamedSilentFail(hediffName);
                if (hediffDef == null) return $"{{\"success\": false, \"reason\": \"HediffDef '{hediffName}' not found.\"}}";

                BodyPartRecord part = null;
                if (!string.IsNullOrEmpty(bodyPart))
                {
                    part = pawn.RaceProps.body.AllParts.FirstOrDefault(p => p.def.defName.Equals(bodyPart, StringComparison.OrdinalIgnoreCase));
                    if (part == null) return $"{{\"success\": false, \"reason\": \"BodyPartRecord '{bodyPart}' not found on pawn.\"}}";
                }

                Hediff hediff = HediffMaker.MakeHediff(hediffDef, pawn, part);
                if (severity.HasValue) hediff.Severity = severity.Value;
                pawn.health.AddHediff(hediff, part, null, null);

                return $"{{\"success\": true, \"message\": \"Successfully added hediff '{hediffName}' to {pawnName}.\"}}";
            }
            else if (action.Equals("remove_body_part", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(bodyPart)) return "{\"success\": false, \"reason\": \"Missing 'bodyPart' parameter.\"}";
                BodyPartRecord part = pawn.RaceProps.body.AllParts.FirstOrDefault(p => p.def.defName.Equals(bodyPart, StringComparison.OrdinalIgnoreCase));
                if (part == null) return $"{{\"success\": false, \"reason\": \"BodyPartRecord '{bodyPart}' not found on pawn.\"}}";

                pawn.health.AddHediff(HediffMaker.MakeHediff(HediffDefOf.MissingBodyPart, pawn, part), part, null, null);
                return $"{{\"success\": true, \"message\": \"Successfully amputated/removed body part '{bodyPart}' from {pawnName}.\"}}";
            }
            else if (action.Equals("convert", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(ideoName)) return "{\"success\": false, \"reason\": \"Missing 'ideoName' parameter.\"}";
                if (!ModsConfig.IdeologyActive || pawn.Ideo == null) return "{\"success\": false, \"reason\": \"Ideology DLC is not active or target has no ideology.\"}";

                Ideo targetIdeo = Find.IdeoManager.IdeosListForReading.FirstOrDefault(i => i.name.Equals(ideoName, StringComparison.OrdinalIgnoreCase));
                if (targetIdeo == null) return $"{{\"success\": false, \"reason\": \"Ideology '{ideoName}' not found.\"}}";

                pawn.ideo.SetIdeo(targetIdeo);
                return $"{{\"success\": true, \"message\": \"Successfully converted {pawnName} to {ideoName}.\"}}";
            }
            else if (action.Equals("add_trait", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(traitName)) return "{\"success\": false, \"reason\": \"Missing 'traitName' parameter.\"}";
                TraitDef traitDef = DefDatabase<TraitDef>.GetNamedSilentFail(traitName);
                if (traitDef == null) return $"{{\"success\": false, \"reason\": \"TraitDef '{traitName}' not found.\"}}";

                pawn.story.traits.GainTrait(new Trait(traitDef, degree ?? 0));
                return $"{{\"success\": true, \"message\": \"Successfully added trait '{traitName}' to {pawnName}.\"}}";
            }
            else if (action.Equals("remove_trait", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(traitName)) return "{\"success\": false, \"reason\": \"Missing 'traitName' parameter.\"}";
                TraitDef traitDef = DefDatabase<TraitDef>.GetNamedSilentFail(traitName);
                if (traitDef == null) return $"{{\"success\": false, \"reason\": \"TraitDef '{traitName}' not found.\"}}";

                if (pawn.story.traits.HasTrait(traitDef))
                {
                    var trait = pawn.story.traits.GetTrait(traitDef);
                    // Proper removal path: unsuppresses masked conflicts AND invalidates the cached
                    // disabled-work/skill sets, so removing a work-disabling trait restores capability.
                    pawn.story.traits.RemoveTrait(trait, true);
                    pawn.Notify_DisabledWorkTypesChanged();
                    pawn.skills?.Notify_SkillDisablesChanged();
                    return $"{{\"success\": true, \"message\": \"Successfully removed trait '{traitName}' from {pawnName}.\"}}";
                }
                return $"{{\"success\": false, \"reason\": \"{pawnName} does not have trait '{traitName}'.\"}}";
            }
            else if (action.Equals("set_skill", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(skillName)) return "{\"success\": false, \"reason\": \"Missing 'skillName' parameter.\"}";

                SkillDef skillDef = DefDatabase<SkillDef>.GetNamedSilentFail(skillName);
                if (skillDef == null) return $"{{\"success\": false, \"reason\": \"SkillDef '{skillName}' not found.\"}}";

                var record = pawn.skills.GetSkill(skillDef);
                if (record == null) return $"{{\"success\": false, \"reason\": \"Skill record '{skillName}' not found on {pawnName}.\"}}";

                string msg = "";
                if (level.HasValue)
                {
                    record.Level = level.Value;
                    msg += $"level to {level.Value}";
                }

                if (parsedArgs.TryGetValue("passion", out var passionVal) && passionVal != null)
                {
                    string passStr = passionVal.ToString();
                    if (Enum.TryParse<RimWorld.Passion>(passStr, true, out var passionEnum))
                    {
                        record.passion = passionEnum;
                        if (msg != "") msg += " and ";
                        msg += $"passion to {passionEnum}";
                    }
                    else
                    {
                        return $"{{\"success\": false, \"reason\": \"Invalid passion level '{passStr}'. Use 'None', 'Minor', or 'Major'.\"}}";
                    }
                }

                if (msg == "") return "{\"success\": false, \"reason\": \"Must specify at least 'level' or 'passion' to modify skill.\"}";

                return $"{{\"success\": true, \"message\": \"Successfully set skill '{skillName}' {msg} for {pawnName}.\"}}";
            }

            return $"{{\"success\": false, \"reason\": \"Unknown action '{action}'.\"}}";
        }
    }
}
