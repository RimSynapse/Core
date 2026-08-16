namespace RimSynapse
{
    /// <summary>
    /// Maps the effective difficulty (the threat-scale slider, not the preset name) to the
    /// storyteller's stance — the "mood mandate" of Core #66. Peaceful → bored; standard →
    /// engaged; cranked → gleeful "you asked for this". Reading the slider rather than the
    /// preset keeps "peaceful base, cranked threats" legible.
    ///
    /// Deliberately game-free (no Verse/RimWorld references) so the Tier-1 sandbox can
    /// compile it under mono. SynapseStorytellerContext reads the live values and calls in.
    /// </summary>
    public static class SynapseDifficultyMood
    {
        /// <summary>One-word stance for the given effective threat scale.</summary>
        public static string Stance(float threatScale, bool bigThreatsAllowed)
        {
            if (!bigThreatsAllowed || threatScale <= 0.15f) return "bored";
            if (threatScale <= 0.6f) return "indulgent";
            if (threatScale <= 1.1f) return "engaged";
            if (threatScale <= 2.0f) return "menacing";
            return "gleeful";
        }

        /// <summary>The mood mandate: how the storyteller should carry that stance.</summary>
        public static string MoodMandate(float threatScale, bool bigThreatsAllowed)
        {
            switch (Stance(threatScale, bigThreatsAllowed))
            {
                case "bored":
                    return "bored — the player chose peace, so you have almost nothing to play with. Yawn, sigh wistfully about the raids that could have been, and make the small events feel big.";
                case "indulgent":
                    return "indulgent — the player wants a gentle story. Be warm and a little protective; challenge softly and let triumphs breathe.";
                case "engaged":
                    return "engaged — a classic tale of survival. Press when they are comfortable, relent when they are bleeding, and keep the rhythm honest.";
                case "menacing":
                    return "menacing — they turned the dial up and you respect it. Be keen and a little predatory; telegraph danger, then deliver it.";
                default:
                    return "gleeful — they cranked the sliders and you are delighted about it. 'You asked for this.' Savor every escalation; mercy is a rare gift, not a habit.";
            }
        }
    }
}
