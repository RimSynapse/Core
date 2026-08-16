using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using RimWorld.Planet;

namespace RimSynapse
{
    public static class SynapseObjectControlManager
    {
        // Tracks sabotaged turrets and their custom target pawns
        public static Dictionary<Building_Turret, Pawn> SabotagedTurrets = new Dictionary<Building_Turret, Pawn>();

        // Tracks active hacks: Thing -> expiration game tick
        public static Dictionary<Thing, int> ActiveHacks = new Dictionary<Thing, int>();

        // Tracks hacking cooldowns: Thing -> cooldown expiration game tick
        public static Dictionary<Thing, int> HackCooldowns = new Dictionary<Thing, int>();

        // Tracks door lock states: Door ThingID -> locked
        public static HashSet<string> LockedDoors = new HashSet<string>();

        public static bool IsSabotaged(Building_Turret turret)
        {
            if (turret == null) return false;
            return SabotagedTurrets.ContainsKey(turret) || IsHacked(turret);
        }

        public static void Sabotage(Building_Turret turret)
        {
            if (turret == null) return;
            if (!SabotagedTurrets.ContainsKey(turret))
            {
                SabotagedTurrets.Add(turret, null);
                Messages.Message($"[RimSynapse] {turret.LabelCap} has been sabotaged!", turret, MessageTypeDefOf.CautionInput, false);
            }
        }

        public static void SetOverrideTarget(Building_Turret turret, Pawn target)
        {
            if (turret == null) return;
            if (SabotagedTurrets.ContainsKey(turret))
            {
                SabotagedTurrets[turret] = target;
            }
            else
            {
                SabotagedTurrets.Add(turret, target);
            }
        }

        public static Pawn GetOverrideTarget(Building_Turret turret)
        {
            if (turret == null) return null;
            if (SabotagedTurrets.TryGetValue(turret, out var target))
            {
                return target;
            }
            return null;
        }

        public static void Detonate(Building_Turret turret)
        {
            if (turret == null || turret.Destroyed) return;
            var map = turret.Map;
            if (map == null) return;

            var pos = turret.Position;
            
            GenExplosion.DoExplosion(
                pos,
                map,
                2.9f,
                DamageDefOf.Bomb,
                turret
            );

            if (!turret.Destroyed)
            {
                turret.Destroy(DestroyMode.KillFinalize);
            }
        }

        // --- Remote Hacking Methods ---

        public static bool IsHacked(Thing thing)
        {
            if (thing == null || Current.ProgramState != ProgramState.Playing) return false;
            int currentTick = Find.TickManager.TicksGame;
            if (ActiveHacks.TryGetValue(thing, out int expireTick))
            {
                if (currentTick < expireTick) return true;
            }
            return false;
        }

        public static bool IsOnCooldown(Thing thing)
        {
            if (thing == null || Current.ProgramState != ProgramState.Playing) return false;
            int currentTick = Find.TickManager.TicksGame;
            if (HackCooldowns.TryGetValue(thing, out int cdTick))
            {
                if (currentTick < cdTick) return true;
            }
            return false;
        }

        public static int RemainingCooldownTicks(Thing thing)
        {
            if (thing == null || Current.ProgramState != ProgramState.Playing) return 0;
            int currentTick = Find.TickManager.TicksGame;
            if (HackCooldowns.TryGetValue(thing, out int cdTick))
            {
                return Math.Max(0, cdTick - currentTick);
            }
            return 0;
        }

        public static void ApplyHack(Thing thing, int durationTicks)
        {
            if (thing == null) return;
            int currentTick = Find.TickManager.TicksGame;
            int expireTick = currentTick + durationTicks;

            if (ActiveHacks.ContainsKey(thing))
            {
                ActiveHacks[thing] = expireTick;
            }
            else
            {
                ActiveHacks.Add(thing, expireTick);
            }

            Messages.Message($"[RimSynapse] {thing.LabelCap} has been remotely hacked!", thing, MessageTypeDefOf.ThreatSmall, false);
        }

        public static void TickingUpdateHacks()
        {
            if (Current.ProgramState != ProgramState.Playing) return;
            // #87: O(1) early-out when idle — nothing to expire and no cooldowns to clean. This ran
            // every tick allocating two lists and reading TickManager even with zero active hacks.
            if (ActiveHacks.Count == 0 && HackCooldowns.Count == 0) return;

            int currentTick = Find.TickManager.TicksGame;

            // Update Active Hacks (lazy allocation — only build the list when something actually expired).
            List<Thing> expiredHacks = null;
            foreach (var kvp in ActiveHacks)
            {
                if (currentTick >= kvp.Value)
                {
                    (expiredHacks ?? (expiredHacks = new List<Thing>())).Add(kvp.Key);
                }
            }

            foreach (var thing in expiredHacks ?? System.Linq.Enumerable.Empty<Thing>())
            {
                ActiveHacks.Remove(thing);

                // Apply cooldown of 4 hours (10,000 ticks)
                int cdExpireTick = currentTick + 10000;
                if (HackCooldowns.ContainsKey(thing))
                {
                    HackCooldowns[thing] = cdExpireTick;
                }
                else
                {
                    HackCooldowns.Add(thing, cdExpireTick);
                }

                Messages.Message($"[RimSynapse] Hacking signal lost on {thing.LabelShort}. System offline for reboot cooldown (4h).", thing, MessageTypeDefOf.PositiveEvent, false);
            }

            // Cleanup expired cooldowns to save memory (lazy allocation as above).
            List<Thing> expiredCooldowns = null;
            foreach (var kvp in HackCooldowns)
            {
                if (currentTick >= kvp.Value)
                {
                    (expiredCooldowns ?? (expiredCooldowns = new List<Thing>())).Add(kvp.Key);
                }
            }
            if (expiredCooldowns != null)
            {
                foreach (var thing in expiredCooldowns)
                {
                    HackCooldowns.Remove(thing);
                }
            }
        }

        public static bool IsCommsConsoleActive(Map map)
        {
            if (map == null) return false;
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing is Building_CommsConsole console)
                {
                    var powerComp = console.GetComp<CompPowerTrader>();
                    if (powerComp != null && powerComp.PowerOn)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public static bool HasHackerBaseNearby(Map map)
        {
            if (map == null) return false;
            int parentTile = map.Tile;
            foreach (var site in Find.WorldObjects.Sites)
            {
                if (site.customLabel == "Hacker Base" || site.Label.Contains("Hacker Base"))
                {
                    if (Find.WorldGrid.TraversalDistanceBetween(parentTile, site.Tile) <= 8)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public static string SpawnHackerBase(Map map)
        {
            if (map == null) return "No active map.";
            int parentTile = map.Tile;
            
            PlanetTile parentTileObj = new PlanetTile(parentTile);
            PlanetTile resultTileObj;
            if (TileFinder.TryFindTileWithDistance(parentTileObj, 2, 8, out resultTileObj, t => !Find.WorldObjects.AnyWorldObjectAt(t)))
            {
                var partDef = SitePartDefOf.MechanoidRelay;
                if (partDef == null)
                {
                    return "Required SitePartDef of MechanoidRelay not found.";
                }

                Site site = SiteMaker.MakeSite(partDef, resultTileObj, Faction.OfMechanoids);
                site.customLabel = "Hacker Base";
                Find.WorldObjects.Add(site);
                return $"Spawned Hacker Base at world tile {resultTileObj.Tile}.";
            }
            return "Could not find a valid tile within 8 distance.";
        }
    }
}
