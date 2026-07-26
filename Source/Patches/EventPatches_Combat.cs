using HarmonyLib;
using RimWorld;
using Verse;
using RimSynapse.Models;
using System.Linq;
using System.Collections.Generic;

namespace RimSynapse.Patches
{
    /// <summary>
    /// Combat-related event patches: pawn kills, downed, raids, panic fleeing, kidnapping.
    /// </summary>

    [HarmonyPatch(typeof(Pawn), "Kill")]
    internal static class Patch_Pawn_Kill
    {
        static void Prefix(Pawn __instance, DamageInfo? dinfo, Hediff exactCulprit = null)
        {
            if (__instance == null) return;
            if (Current.ProgramState != ProgramState.Playing || Find.World == null) return;

            var coreComp = Find.World.GetComponent<SynapseCoreWorldComponent>();
            if (coreComp == null) return;

            // Check for resident pawn death
            var pComp = __instance.TryGetComp<RimSynapse.Comps.SynapseCorePawnComp>();
            if (pComp != null && pComp.isResident && __instance.Map != null && __instance.Faction != null)
            {
                var faction = __instance.Faction;
                var map = __instance.Map;

                bool otherResidentAlive = map.mapPawns.AllPawns
                    .Any(p => p != __instance && p.Faction == faction && p.RaceProps.Humanlike && !p.Dead && p.TryGetComp<RimSynapse.Comps.SynapseCorePawnComp>()?.isResident == true);

                if (!otherResidentAlive)
                {
                    var thingsToClear = map.listerThings.AllThings
                        .Where(t => t.Faction == faction)
                        .ToList();
                    
                    foreach (var t in thingsToClear)
                    {
                        t.SetFaction(null);
                    }
                    
                    Messages.Message($"The residents of {faction.Name} on this map have all died or been recruited. Their property is now unclaimed.", MessageTypeDefOf.NeutralEvent, true);
                }
            }

            bool isColonist = __instance.IsColonist;
            bool isPrisoner = __instance.IsPrisonerOfColony;
            bool isHostile = __instance.HostileTo(Faction.OfPlayer);

            if (!isColonist && !isPrisoner && !isHostile) return;

            string killerInfo = "";
            if (dinfo.HasValue && dinfo.Value.Instigator is Pawn killer)
            {
                killerInfo = $" by {killer.Name?.ToStringShort ?? killer.KindLabel}";
            }

            string hediffInfo = "";
            if (exactCulprit != null)
            {
                hediffInfo = $" (from {exactCulprit.Label})";
            }

            string category = isColonist ? "ColonistDeath" : (isPrisoner ? "PrisonerDeath" : "EnemyKilled");

            coreComp.EnqueuePastEvent(new PastEvent
            {
                gameTick = GenTicks.TicksGame,
                category = category,
                eventDescription = $"{__instance.Name?.ToStringShort ?? __instance.KindLabel} was killed{killerInfo}{hediffInfo}."
            });

            // Track raid kills
            if (isHostile && coreComp.activeRaidTracker != null)
            {
                coreComp.activeRaidTracker.enemiesKilled++;
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), "MakeDowned")]
    internal static class Patch_Pawn_MakeDowned
    {
        static void Postfix(Pawn_HealthTracker __instance)
        {
            Pawn pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
            if (pawn == null || !pawn.IsColonist) return;
            if (Current.ProgramState != ProgramState.Playing || Find.World == null) return;

            var coreComp = Find.World.GetComponent<SynapseCoreWorldComponent>();
            if (coreComp == null) return;

            string reason = "";
            var latestHediff = pawn.health?.hediffSet?.hediffs?
                .OrderByDescending(h => h.ageTicks)
                .FirstOrDefault(h => h.Visible);
            if (latestHediff != null)
            {
                reason = $" due to {latestHediff.Label}";
                if (latestHediff.Part != null)
                    reason += $" ({latestHediff.Part.Label})";
            }

            coreComp.EnqueuePastEvent(new PastEvent
            {
                gameTick = GenTicks.TicksGame,
                category = "ColonistDowned",
                eventDescription = $"{pawn.Name?.ToStringShort ?? pawn.KindLabel} has been downed{reason}."
            });
        }
    }

    [HarmonyPatch(typeof(Pawn), "PreKidnapped")]
    internal static class Patch_Pawn_PreKidnapped
    {
        static void Postfix(Pawn __instance, Pawn kidnapper)
        {
            if (__instance == null || !__instance.IsColonist) return;
            if (Current.ProgramState != ProgramState.Playing || Find.World == null) return;

            var coreComp = Find.World.GetComponent<SynapseCoreWorldComponent>();
            if (coreComp == null) return;

            string kidnapperName = kidnapper?.Name?.ToStringShort ?? kidnapper?.KindLabel ?? "unknown";
            string factionName = kidnapper?.Faction?.Name ?? "unknown faction";

            coreComp.EnqueuePastEvent(new PastEvent
            {
                gameTick = GenTicks.TicksGame,
                category = "ColonistKidnapped",
                eventDescription = $"{__instance.Name?.ToStringShort ?? __instance.KindLabel} was kidnapped by {kidnapperName} of {factionName}."
            });

            // Track raid kidnapping
            if (coreComp.activeRaidTracker != null)
            {
                coreComp.activeRaidTracker.colonistsKidnapped++;
            }
        }
    }

    [HarmonyPatch(typeof(LordToil_PanicFlee), "Init")]
    internal static class Patch_LordToil_PanicFlee_Init
    {
        static void Postfix(LordToil_PanicFlee __instance)
        {
            if (Current.ProgramState != ProgramState.Playing || Find.World == null) return;

            var coreComp = Find.World.GetComponent<SynapseCoreWorldComponent>();
            if (coreComp == null) return;

            var lord = __instance.lord;
            if (lord == null) return;

            var faction = lord.faction;
            var pawns = lord.ownedPawns;
            if (faction == null || faction.IsPlayer || pawns == null) return;

            if (coreComp.activeRaidTracker != null)
            {
                coreComp.activeRaidTracker.enemiesDowned += pawns.Count(p => p.Downed && !p.Dead);
            }

            int totalLiving = pawns.Count(p => !p.Dead);
            int downed = pawns.Count(p => p.Downed && !p.Dead);
            int dead = pawns.Count(p => p.Dead);

            string fleeDesc = $"{faction.Name}'s raiding party is fleeing! " +
                $"({totalLiving} alive, {downed} downed, {dead} dead)";

            coreComp.EnqueuePastEvent(new PastEvent
            {
                gameTick = GenTicks.TicksGame,
                category = "RaidFleeing",
                eventDescription = fleeDesc
            });

            // Witness reactions from colonists near the fleeing raiders
            if (Find.CurrentMap != null)
            {
                var colonists = Find.CurrentMap.mapPawns?.FreeColonistsSpawned;
                if (colonists != null)
                {
                    foreach (var colonist in colonists)
                    {
                        bool isNearFleeing = pawns.Any(r => !r.Dead && colonist.Position.DistanceTo(r.Position) < 20f);
                        if (isNearFleeing)
                        {
                            coreComp.LogShortTermInteraction(
                                colonist, null,
                                DefDatabase<InteractionDef>.GetNamedSilentFail("RimSynapse_WitnessedRaidRetreat")
                                    ?? InteractionDefOf.Chitchat);
                        }
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), "SpawnSetup")]
    internal static class Patch_Pawn_SpawnSetup_Raider
    {
        static void Postfix(Pawn __instance, Map map, bool respawningAfterLoad)
        {
            if (__instance == null || map == null || respawningAfterLoad) return;
            if (Current.ProgramState != ProgramState.Playing || Find.World == null) return;
            if (__instance.Faction == null || __instance.Faction.IsPlayer) return;
            if (!__instance.HostileTo(Faction.OfPlayer)) return;

            var coreComp = Find.World.GetComponent<SynapseCoreWorldComponent>();
            if (coreComp == null) return;

            if (!string.IsNullOrEmpty(coreComp.activeRaidEventId))
            {
                coreComp.pawnToRaidId[__instance.ThingID] = coreComp.activeRaidEventId;
            }
        }
    }
}
