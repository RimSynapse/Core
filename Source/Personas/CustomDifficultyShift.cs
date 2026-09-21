using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using RimWorld;
using Verse;

namespace RimSynapse.Personas
{
    /// <summary>
    /// Custom-difficulty shift commentary (Core #90): when the player runs Custom, notice notable
    /// deviations in the difficulty sliders and turn them into character material Aura can needle —
    /// "would've sent you insectoids, but you turned those off." The quip templates live in the persona
    /// def (moddable, #89); this class only detects and classifies the shifts and hands the matching
    /// template text to the engine as regeneration context.
    ///
    /// <para>Live fields are read by reflection off <see cref="RimWorld.Difficulty"/> so a renamed or
    /// absent field in a future RimWorld simply doesn't fire, rather than failing the build. The pure
    /// <see cref="Classify"/> pass is what the Core_CustomShift* cases assert against.</para>
    /// </summary>
    public static class CustomDifficultyShift
    {
        /// <summary>A detected notable deviation.</summary>
        public class Shift
        {
            public string slider;   // canonical key, e.g. "insectoids"
            public string dir;      // "off" | "low" | "high"
            public float value;     // the read value (bools as 1/0)
        }

        private enum Kind { Toggle, Scale, Offset, Interval }

        private class Dial
        {
            public string key;        // canonical key used in the persona def's shiftQuips
            public string field;      // RimWorld.Difficulty field name (read by reflection)
            public Kind kind;
            public float baseline;
        }

        // Canonical dial table. Toggle: 1 = on (baseline), 0 = off → "off". Scale: baseline 1.0,
        // notable when far from it → "low"/"high". Offset: baseline 0, a positive mood offset softens
        // penalties → "off". Interval: baseline 1.0, a large factor means the thing rarely happens → "off".
        private static readonly Dial[] Dials =
        {
            new Dial { key = "threatScale",     field = "threatScale",           kind = Kind.Scale,    baseline = 1f },
            new Dial { key = "allowBigThreats", field = "allowBigThreats",       kind = Kind.Toggle,   baseline = 1f },
            new Dial { key = "insectoids",      field = "allowInfestations",     kind = Kind.Toggle,   baseline = 1f },
            new Dial { key = "mechanoids",      field = "allowMechClusters",     kind = Kind.Toggle,   baseline = 1f },
            new Dial { key = "adaptation",      field = "adaptationEffectFactor",kind = Kind.Scale,    baseline = 1f },
            new Dial { key = "moodPenalties",   field = "colonistMoodOffset",    kind = Kind.Offset,   baseline = 0f },
            new Dial { key = "diseases",        field = "diseaseIntervalFactor", kind = Kind.Interval, baseline = 1f },
            new Dial { key = "diseases",        field = "diseaseIntervalFactorPositiveOnly", kind = Kind.Interval, baseline = 1f },
        };

        // "Notable" thresholds.
        private const float ScaleLow = 0.5f;    // threatScale/adaptation at or below → low
        private const float ScaleHigh = 2.0f;   // at or above → high
        private const float AdaptationOff = 0.001f; // adaptation effect this low is effectively off
        private const float MoodOffsetNotable = 5f;  // a mood buffer this big reads as "penalties off"
        private const float IntervalRare = 2.0f;     // disease interval this stretched reads as "off"

        /// <summary>Classify slider readings (canonical key → value; bools as 1/0) into notable shifts.
        /// Near-baseline dials produce nothing — quiet is the correct behaviour for a default config.</summary>
        public static List<Shift> Classify(IDictionary<string, float> readings)
        {
            var shifts = new List<Shift>();
            if (readings == null) return shifts;
            var seen = new HashSet<string>();

            foreach (var dial in Dials)
            {
                if (seen.Contains(dial.key)) continue;
                if (!readings.TryGetValue(dial.key, out float v)) continue;

                string dir = null;
                switch (dial.kind)
                {
                    case Kind.Toggle:
                        if (v <= 0.5f) dir = "off";
                        break;
                    case Kind.Scale:
                        if (dial.key == "adaptation") { if (v <= AdaptationOff) dir = "off"; }
                        else if (v <= ScaleLow) dir = "low";
                        else if (v >= ScaleHigh) dir = "high";
                        break;
                    case Kind.Offset:
                        if (v >= MoodOffsetNotable) dir = "off";
                        break;
                    case Kind.Interval:
                        if (v >= IntervalRare) dir = "off";
                        break;
                }

                if (dir != null)
                {
                    shifts.Add(new Shift { slider = dial.key, dir = dir, value = v });
                    seen.Add(dial.key);
                }
            }
            return shifts;
        }

        /// <summary>Read the live Custom difficulty into canonical readings, then classify. Empty unless
        /// the active difficulty is custom. Reflection-safe: unknown fields are skipped.</summary>
        public static List<Shift> DetectLive()
        {
            var diff = Find.Storyteller?.difficulty;
            var def = Find.Storyteller?.difficultyDef;
            if (diff == null || def == null || !def.isCustom) return new List<Shift>();
            return Classify(ReadLive(diff));
        }

        private static Dictionary<string, float> ReadLive(Difficulty diff)
        {
            var readings = new Dictionary<string, float>();
            var t = diff.GetType();
            foreach (var dial in Dials)
            {
                if (readings.ContainsKey(dial.key)) continue;
                var f = t.GetField(dial.field, BindingFlags.Public | BindingFlags.Instance);
                if (f == null) continue;
                object raw = f.GetValue(diff);
                float val;
                if (raw is bool b) val = b ? 1f : 0f;
                else if (raw is float fl) val = fl;
                else if (raw is int i) val = i;
                else continue;
                readings[dial.key] = val;
            }
            return readings;
        }

        /// <summary>Turn detected shifts into regeneration context: the persona-def quip templates for
        /// each shift, joined. Null when there is nothing notable (so no quip fires on a default config).
        /// The template is both the LLM seed and, verbatim, the deterministic fallback material.</summary>
        public static string BuildShiftContext(PersonaDifficultyEntry entry, List<Shift> shifts)
        {
            if (entry == null || shifts == null || shifts.Count == 0) return null;
            var lines = new List<string>();
            foreach (var s in shifts)
            {
                var quip = entry.QuipFor(s.slider, s.dir) ?? entry.QuipFor(s.slider, null);
                if (quip != null && !quip.text.NullOrEmpty()) lines.Add(quip.text);
            }
            if (lines.Count == 0) return null;
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++) sb.Append(i == 0 ? "" : " ").Append(lines[i]);
            return sb.ToString();
        }

        /// <summary>Convenience for the engine: the live shift context for the current Custom entry, or null.</summary>
        public static string LiveContext(PersonaDifficultyEntry entry) => BuildShiftContext(entry, DetectLive());
    }
}
