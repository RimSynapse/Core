using System;
using RimWorld;
using Verse;

namespace RimSynapse.Utils
{
    /// <summary>
    /// Shared date/tick utility for converting between pawn ages and absolute ticks.
    /// Used by Psychology (pawn memories) and StoryTeller (faction history) to
    /// generate historically-accurate timestamps for pre-game events.
    /// 
    /// RimWorld tick system:
    ///   - 60,000 ticks per day
    ///   - 60 days per year (15 per quadrum × 4 quadrums)
    ///   - 3,600,000 ticks per year
    ///   - TicksAbs = absolute ticks since year 0
    ///   - TicksGame = ticks since game started (starts at 0)
    ///   - Default starting year: 5500
    /// </summary>
    public static class SynapseDateHelper
    {
        public const int TicksPerDay = 60000;
        public const int DaysPerYear = 60;
        public const int TicksPerYear = TicksPerDay * DaysPerYear; // 3,600,000

        /// <summary>
        /// Gets the adjustment offset to convert game ticks to absolute ticks.
        /// adjTick = TicksAbs - TicksGame (the absolute tick when the game started)
        /// </summary>
        public static long GetAdjustmentTick()
        {
            return (long)GenTicks.TicksAbs - (long)Find.TickManager.TicksGame;
        }

        /// <summary>
        /// Converts a game-relative tick to an absolute tick.
        /// </summary>
        public static long GameTickToAbsTick(int gameTick)
        {
            return gameTick + GetAdjustmentTick();
        }

        /// <summary>
        /// Gets the absolute tick for when a pawn was a specific biological age.
        /// Uses pawn.ageTracker.BirthAbsTicks as the anchor.
        /// </summary>
        public static long GetAbsTickAtAge(Pawn pawn, int age)
        {
            long birthAbsTick = pawn.ageTracker.BirthAbsTicks;
            return birthAbsTick + (long)age * TicksPerYear;
        }

        /// <summary>
        /// Gets a reasonable absolute tick for a childhood memory (age 7-12).
        /// The range gives variety — different pawns remember different ages.
        /// </summary>
        public static long GetChildhoodMemoryTick(Pawn pawn)
        {
            int age = Rand.RangeInclusive(7, 12);
            return GetAbsTickAtAge(pawn, age);
        }

        /// <summary>
        /// Gets a reasonable absolute tick for an adulthood memory (age 18-25).
        /// Capped at currentAge - 2 so the memory isn't too recent.
        /// </summary>
        public static long GetAdulthoodMemoryTick(Pawn pawn)
        {
            int currentAge = pawn.ageTracker.AgeBiologicalYears;
            int maxAge = Math.Max(18, currentAge - 2);
            int age = Rand.RangeInclusive(18, Math.Min(maxAge, 25));
            return GetAbsTickAtAge(pawn, age);
        }

        /// <summary>
        /// Gets the current absolute tick (for "now" events like arrival, first impression).
        /// </summary>
        public static long GetCurrentAbsTick()
        {
            return GenTicks.TicksAbs;
        }

        /// <summary>
        /// Formats an absolute tick as a RimWorld date string, correctly handling negative ticks (pre-5500).
        /// </summary>
        public static string FormatAbsTick(long absTick, float longitude = 0f)
        {
            long ticks = absTick;

            // Longitude adjustment: each 1 degree = 1/360th of a day (166.66 ticks).
            // RimWorld shifts time based on longitude.
            long tzOffset = (long)(longitude * ((float)TicksPerDay / 360f));
            ticks += tzOffset;

            long yearsFromAnchor = ticks / TicksPerYear;
            long ticksRemainder = ticks % TicksPerYear;

            // Handle negative remainders correctly
            if (ticksRemainder < 0)
            {
                yearsFromAnchor -= 1;
                ticksRemainder += TicksPerYear;
            }

            int year = 5500 + (int)yearsFromAnchor;
            int daysTotal = (int)(ticksRemainder / TicksPerDay);
            
            int quadrumIndex = daysTotal / 15;
            int dayOfQuadrum = (daysTotal % 15) + 1; // 1-indexed for display (1st to 15th)

            string quadrumLabel = quadrumIndex switch
            {
                0 => "Aprimay",
                1 => "Jugust",
                2 => "Septober",
                3 => "Decembary",
                _ => "Unknown"
            };

            // Add suffix for the day
            string daySuffix = "th";
            if (dayOfQuadrum % 10 == 1 && dayOfQuadrum != 11) daySuffix = "st";
            else if (dayOfQuadrum % 10 == 2 && dayOfQuadrum != 12) daySuffix = "nd";
            else if (dayOfQuadrum % 10 == 3 && dayOfQuadrum != 13) daySuffix = "rd";

            long dayTicks = ticksRemainder % TicksPerDay;
            if (dayTicks < 0) dayTicks += TicksPerDay;
            int hour = (int)(dayTicks / 2500);
            int minute = (int)((dayTicks % 2500) / 41.66667f);

            return $"{dayOfQuadrum}{daySuffix} of {quadrumLabel}, {year} {hour:D2}:{minute:D2}";
        }

        /// <summary>
        /// Gets approximate pawn age at a given absolute tick.
        /// Useful for display: "Age 8 — 3 of Aprimay, 5492"
        /// </summary>
        public static int GetAgeAtAbsTick(Pawn pawn, long absTick)
        {
            long birthAbsTick = pawn.ageTracker.BirthAbsTicks;
            long ticksSinceBirth = absTick - birthAbsTick;
            return (int)(ticksSinceBirth / TicksPerYear);
        }
    }
}
