using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RimSynapse.Personas
{
    /// <summary>
    /// The beats a persona speaks on. The engine (Core #69) asks the persona for a line at one of
    /// these moments; the def (Core #89) supplies the prompt and prose per difficulty.
    /// </summary>
    public enum PersonaBeat
    {
        /// <summary>She fires an incident.</summary>
        Kickoff,
        /// <summary>First-level resolution — the player held / won.</summary>
        ResolutionPositive,
        /// <summary>First-level resolution — the player suffered / lost.</summary>
        ResolutionNegative,
        /// <summary>Paying off an earlier open thread later.</summary>
        Callback,
        /// <summary>Ambient commentary when nothing is happening.</summary>
        Idle,
    }

    /// <summary>Prose exemplars for one difficulty, one per beat. Hand-written lines that seed the
    /// LLM's style during regeneration and are the deterministic fallback when the LLM is
    /// unavailable. Each list holds one or more variants; the engine picks one.</summary>
    public class PersonaBeats
    {
        public List<string> kickoff = new List<string>();
        public List<string> resolutionPositive = new List<string>();
        public List<string> resolutionNegative = new List<string>();
        public List<string> callback = new List<string>();
        public List<string> idle = new List<string>();

        public List<string> For(PersonaBeat beat)
        {
            switch (beat)
            {
                case PersonaBeat.Kickoff: return kickoff;
                case PersonaBeat.ResolutionPositive: return resolutionPositive;
                case PersonaBeat.ResolutionNegative: return resolutionNegative;
                case PersonaBeat.Callback: return callback;
                default: return idle;
            }
        }
    }

    /// <summary>A slider-shift quip template (Core #90). Authored in the def, not code, so it is
    /// moddable. The LLM may elaborate the template; the deterministic fallback uses it verbatim.</summary>
    public class PersonaShiftQuip
    {
        /// <summary>Which CustomDifficulty dimension this reacts to, e.g. "insectoids", "threatScale",
        /// "adaptation", "moodPenalties". Matched case-insensitively by the shift detector (#90).</summary>
        public string slider;
        /// <summary>The direction that fires this quip: "off", "low", or "high".</summary>
        public string dir = "off";
        /// <summary>The line (fallback verbatim; LLM seed otherwise).</summary>
        public string text;
    }

    /// <summary>One difficulty's worth of persona: the personality prompt handed to the LLM plus the
    /// prose corpus. Keyed by <see cref="difficulty"/> = the vanilla <c>DifficultyDef.defName</c>
    /// (Peaceful, Easy, Medium, Rough, Hard, Extreme, Custom).</summary>
    public class PersonaDifficultyEntry
    {
        /// <summary>The DifficultyDef.defName this entry drives. "Custom" matches any custom difficulty.</summary>
        public string difficulty;
        /// <summary>The personality instruction handed to the LLM in this mood — the knob a modder turns.</summary>
        public string personaPrompt;
        /// <summary>Prose exemplars per beat.</summary>
        public PersonaBeats prose = new PersonaBeats();
        /// <summary>Custom-difficulty slider-shift quips (Core #90). Only meaningful on the Custom entry.</summary>
        public List<PersonaShiftQuip> shiftQuips = new List<PersonaShiftQuip>();

        /// <summary>Find the quip for a detected shift (slider + direction), case-insensitive, or null.</summary>
        public PersonaShiftQuip QuipFor(string sliderKey, string direction)
        {
            if (shiftQuips == null || string.IsNullOrEmpty(sliderKey)) return null;
            return shiftQuips.FirstOrDefault(q =>
                q.slider != null && q.slider.EqualsIgnoreCase(sliderKey) &&
                (direction == null || q.dir == null || q.dir.EqualsIgnoreCase(direction)));
        }
    }

    /// <summary>
    /// A storyteller's persona — the moddable voice bible. Aura ships as Core's reference persona
    /// (Defs/StorytellerPersonas/Persona_Aura.xml, Core #89); the engine (Core #69) reads whichever
    /// persona the active storyteller names, and third-party persona defs load alongside Aura.
    ///
    /// <para>The persona is data, the engine is Core: a modder copies this def, rewrites the prompts
    /// and prose, and ships a new storyteller with no code.</para>
    /// </summary>
    public class StorytellerPersonaDef : Def
    {
        /// <summary>Tone law: this persona breaks the fourth wall (addresses the player, knows she is
        /// the author). Aura is true; a diegetic modder persona sets it false. The engine passes this
        /// through to the prompt so the voice stays consistent.</summary>
        public bool fourthWall = true;

        /// <summary>Per-difficulty prompt + prose. One entry per difficulty the persona supports.</summary>
        public List<PersonaDifficultyEntry> difficulties = new List<PersonaDifficultyEntry>();

        /// <summary>The difficulties every complete persona must cover (the vanilla set + Custom).</summary>
        public static readonly string[] RequiredDifficulties =
            { "Peaceful", "Easy", "Medium", "Rough", "Hard", "Extreme", "Custom" };

        /// <summary>Resolve the entry for a difficulty defName. Exact match first; then, for a custom
        /// difficulty, the "Custom" entry; then null (the engine falls back to prose-less silence or a
        /// default the caller chooses). Case-insensitive on defName.</summary>
        public PersonaDifficultyEntry EntryFor(string difficultyDefName, bool isCustom)
        {
            if (difficulties == null || difficulties.Count == 0) return null;
            if (!string.IsNullOrEmpty(difficultyDefName))
            {
                var exact = difficulties.FirstOrDefault(e =>
                    e.difficulty != null && e.difficulty.EqualsIgnoreCase(difficultyDefName));
                if (exact != null) return exact;
            }
            if (isCustom)
            {
                var custom = difficulties.FirstOrDefault(e =>
                    e.difficulty != null && e.difficulty.EqualsIgnoreCase("Custom"));
                if (custom != null) return custom;
            }
            return null;
        }

        /// <summary>The def-linter (Core #89 acceptance): every required difficulty present, and every
        /// beat populated for each, so a half-authored persona is caught at load, not at a silent beat.</summary>
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (var e in base.ConfigErrors()) yield return e;

            if (difficulties == null || difficulties.Count == 0)
            {
                yield return "no <difficulties> entries — a persona with no voice";
                yield break;
            }

            foreach (var req in RequiredDifficulties)
            {
                var entry = difficulties.FirstOrDefault(e => e.difficulty.EqualsIgnoreCase(req));
                if (entry == null)
                {
                    yield return $"missing difficulty entry '{req}'";
                    continue;
                }
                if (string.IsNullOrEmpty(entry.personaPrompt))
                    yield return $"'{req}' has no <personaPrompt>";
                foreach (PersonaBeat beat in System.Enum.GetValues(typeof(PersonaBeat)))
                {
                    var lines = entry.prose?.For(beat);
                    if (lines == null || lines.Count == 0 || lines.All(string.IsNullOrEmpty))
                        yield return $"'{req}' has no prose for beat {beat}";
                }
            }
        }
    }
}
