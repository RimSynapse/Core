using System.Collections.Generic;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// The Kokoro TTS voice catalog and per-pawn base-voice assignment (Conversations#33).
    /// Psychology assigns each pawn a base voice at "birth" by sex/gender; the 0.10+ TTS layer renders
    /// it (with the personality-driven speed/blend on <see cref="Comps.SynapseCorePawnComp"/>).
    ///
    /// Kokoro voice ids are fixed and named <c>&lt;lang&gt;&lt;gender&gt;_&lt;name&gt;</c>: the first
    /// letter is the accent (a = American English, b = British English; other languages exist but are
    /// out of the default colonist pool), the second is gender (f/m). Kokoro has no pitch knob — the
    /// render-time levers are voice id, speed, and voice blending.
    /// </summary>
    public static class KokoroVoices
    {
        /// <summary>English female voices (American af_* + British bf_*).</summary>
        public static readonly string[] EnglishFemale =
        {
            "af_alloy", "af_aoede", "af_bella", "af_heart", "af_jessica", "af_kore",
            "af_nicole", "af_nova", "af_river", "af_sarah", "af_sky",
            "bf_alice", "bf_emma", "bf_isabella", "bf_lily"
        };

        /// <summary>English male voices (American am_* + British bm_*).</summary>
        public static readonly string[] EnglishMale =
        {
            "am_adam", "am_echo", "am_eric", "am_fenrir", "am_liam", "am_michael",
            "am_onyx", "am_puck", "am_santa",
            "bm_daniel", "bm_fable", "bm_george", "bm_lewis"
        };

        /// <summary>Whether <paramref name="voiceId"/> is a known catalog voice.</summary>
        public static bool IsKnown(string voiceId)
        {
            if (string.IsNullOrEmpty(voiceId)) return false;
            for (int i = 0; i < EnglishFemale.Length; i++) if (EnglishFemale[i] == voiceId) return true;
            for (int i = 0; i < EnglishMale.Length; i++) if (EnglishMale[i] == voiceId) return true;
            return false;
        }

        /// <summary>The gender-matched English pool for a pawn (Male → male, Female → female,
        /// None/other → the combined pool).</summary>
        public static IReadOnlyList<string> PoolFor(Pawn pawn)
        {
            if (pawn?.gender == Gender.Male) return EnglishMale;
            if (pawn?.gender == Gender.Female) return EnglishFemale;
            var both = new List<string>(EnglishMale.Length + EnglishFemale.Length);
            both.AddRange(EnglishMale);
            both.AddRange(EnglishFemale);
            return both;
        }

        /// <summary>
        /// Pick a stable, gender-appropriate base voice for a pawn. Seeded by the pawn's
        /// <c>thingIDNumber</c> so the same pawn always resolves to the same voice across reloads even
        /// if this is called before the assignment is persisted.
        /// </summary>
        public static string RandomVoiceFor(Pawn pawn)
        {
            var pool = PoolFor(pawn);
            if (pool == null || pool.Count == 0) return null;
            int seed = (pawn?.thingIDNumber ?? 0) ^ 0x4B4F4B;  // "KOK" — stable per-pawn seed
            Rand.PushState(seed);
            try { return pool[Rand.Range(0, pool.Count)]; }
            finally { Rand.PopState(); }
        }
    }
}
