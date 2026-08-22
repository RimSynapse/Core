namespace RimSynapse
{
    /// <summary>
    /// Pure eviction policy for the bounded world-history store (Core #65), kept game-free so the
    /// Tier-1 sandbox can pin it. The store must never grow without limit, and it must preserve
    /// <b>open threads</b> (unresolved incidents the Storyteller may still call back to) in favour of
    /// dropping settled, resolved history.
    ///
    /// Policy: when over the cap, evict the oldest RESOLVED entry first; only if every entry is still
    /// open do we drop the oldest open one (a hard bound against pathological open-thread growth).
    /// </summary>
    public static class WorldHistoryCuration
    {
        /// <summary>
        /// Index to evict when <paramref name="count"/> exceeds <paramref name="cap"/>, or -1 if the
        /// store is within bounds. Prefers the oldest resolved entry; falls back to the oldest entry
        /// overall when nothing is resolved.
        /// </summary>
        public static int SelectEvictionIndex(bool[] resolved, int[] lastTick, int count, int cap)
        {
            if (resolved == null || lastTick == null) return -1;
            if (count <= cap) return -1;
            int n = count < resolved.Length ? count : resolved.Length;
            if (n <= 0) return -1;

            int oldestResolved = -1, oldestResolvedTick = int.MaxValue;
            int oldestAny = -1, oldestAnyTick = int.MaxValue;

            for (int i = 0; i < n; i++)
            {
                if (lastTick[i] < oldestAnyTick) { oldestAnyTick = lastTick[i]; oldestAny = i; }
                if (resolved[i] && lastTick[i] < oldestResolvedTick) { oldestResolvedTick = lastTick[i]; oldestResolved = i; }
            }

            return oldestResolved >= 0 ? oldestResolved : oldestAny;
        }
    }
}
