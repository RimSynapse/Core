using System;

namespace RimSynapse
{
    /// <summary>
    /// Learns when the game goes quiet, by in-game hour.
    ///
    /// Foreground LLM demand follows colony rhythm — colonists sleep, socialise and fight
    /// on a daily cycle — so the hours when the queue will likely be idle are predictable.
    /// Each foreground request arrival is bucketed by in-game hour; at day rollover the
    /// day's counts fold into a per-hour EWMA. An hour is "likely quiet" when its learned
    /// rate sits well below the daily mean, which lets the opportunistic scheduler dispatch
    /// background work aggressively *into* those windows instead of merely reacting to
    /// momentary idleness.
    ///
    /// Background/opportunistic requests are excluded by the caller: they only fire when
    /// the queue is already idle, so counting them would make quiet hours look busy and
    /// suppress the very scheduling they enable.
    ///
    /// Verse-free on purpose (callers supply day and hour), so it is directly testable.
    /// </summary>
    public static class SynapseDemandForecast
    {
        /// <summary>Blend factor when folding a finished day into the learned rates.</summary>
        public const float Blend = 0.4f;

        /// <summary>An hour is quiet when its rate is at or below this fraction of the daily mean.</summary>
        public const float QuietFraction = 0.5f;

        private static readonly object _lock = new object();
        private static readonly float[] _learned = new float[24];
        private static readonly int[] _today = new int[24];
        private static int _currentDay = -1;
        private static int _daysFolded;

        public static void RecordForeground(int day, int hour)
        {
            if (hour < 0 || hour > 23) return;
            lock (_lock)
            {
                FoldIfNewDayLocked(day);
                _today[hour]++;
            }
        }

        /// <summary>Learned foreground requests per in-game hour. 0 until a full day has folded.</summary>
        public static float Rate(int hour)
        {
            if (hour < 0 || hour > 23) return 0f;
            lock (_lock)
            {
                return _learned[hour];
            }
        }

        /// <summary>
        /// Whether an hour is historically quiet. Unknown is NOT quiet: with no folded
        /// days (or a dead-flat profile) this returns false, so the scheduler falls back
        /// to its configured throttle instead of guessing.
        /// </summary>
        public static bool IsLikelyQuiet(int day, int hour)
        {
            if (hour < 0 || hour > 23) return false;
            lock (_lock)
            {
                FoldIfNewDayLocked(day);
                if (_daysFolded < 1) return false;

                float mean = 0f;
                for (int h = 0; h < 24; h++) mean += _learned[h];
                mean /= 24f;
                if (mean <= 0f) return false;

                return _learned[hour] <= mean * QuietFraction;
            }
        }

        public static int DaysFolded
        {
            get { lock (_lock) return _daysFolded; }
        }

        public static void Reset()
        {
            lock (_lock)
            {
                Array.Clear(_learned, 0, 24);
                Array.Clear(_today, 0, 24);
                _currentDay = -1;
                _daysFolded = 0;
            }
        }

        private static void FoldIfNewDayLocked(int day)
        {
            if (_currentDay == -1)
            {
                _currentDay = day;
                return;
            }
            if (day == _currentDay) return;

            for (int h = 0; h < 24; h++)
            {
                _learned[h] = _daysFolded == 0
                    ? _today[h]
                    : Blend * _today[h] + (1f - Blend) * _learned[h];
                _today[h] = 0;
            }
            _daysFolded++;
            _currentDay = day;
        }
    }
}
