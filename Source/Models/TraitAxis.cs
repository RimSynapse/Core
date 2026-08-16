using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimSynapse.Models
{
    /// <summary>
    /// A runtime view of one "trait axis" for a specific pawn: the trait def, whether it is a
    /// multi-degree spectrum (NaturalMood, Industriousness, Nerves) or a single/aversion trait
    /// (absent &lt;-&gt; present), the pawn's current position, the reachable adjacent positions, and
    /// today's measured pressure in each direction.
    ///
    /// <para>Owned by Core (Core owns trait-pressure data). Construction and the adjacency math are
    /// pure — no ticks, no side effects — so the whole trait-shift decision is unit-testable without a
    /// running game or an LLM. The trait-shift engine (Psychology) builds these per eligible axis, feeds
    /// them to the LLM as candidates, and resolves a fired shift back through them.</para>
    /// </summary>
    public class TraitAxisCandidate
    {
        /// <summary>The trait def's name — the stable axis identity.</summary>
        public string axisId;
        public TraitDef def;
        /// <summary>True when the def authors more than one degree (a movable spectrum).</summary>
        public bool isSpectrum;
        /// <summary>Spectrum: current degree (0 == neutral/absent). Single/aversion: unused (see <see cref="isPresent"/>).</summary>
        public int currentDegree;
        /// <summary>Single/aversion traits: whether the pawn currently has the trait.</summary>
        public bool isPresent;
        /// <summary>Spectrum: the next reachable degree in the + direction, or null at the edge.</summary>
        public int? plusDegree;
        /// <summary>Spectrum: the next reachable degree in the - direction, or null at the edge.</summary>
        public int? minusDegree;
        public string currentLabel;   // "none" when neutral/absent
        public string plusLabel;      // label of the reachable + move, or null
        public string minusLabel;     // label of the reachable - move, or null
        /// <summary>Accumulated pressure currently pushing the + direction (0 if none).</summary>
        public float measuredPlus;
        /// <summary>Accumulated pressure currently pushing the - direction (0 if none).</summary>
        public float measuredMinus;
    }

    /// <summary>
    /// Pure helpers for the trait-axis model: reachable-degree math and the candidate-id encoding that
    /// lets opposite directions on one spectrum share the <see cref="TraitPressure"/> dictionary without
    /// touching the accumulator signature. A candidate id is <c>"{axisId}#{targetDegree}"</c> for a
    /// spectrum move, or <c>"{axisId}#+"</c> / <c>"{axisId}#-"</c> for a single-trait add / remove.
    /// </summary>
    public static class TraitAxis
    {
        /// <summary>All reachable degrees on a def: every authored degree plus the implicit neutral 0.
        /// Sorted ascending. (Spectra author non-zero degrees; single traits author one degree.)</summary>
        public static List<int> ReachableDegrees(TraitDef def)
        {
            var set = new SortedSet<int> { 0 };
            if (def?.degreeDatas != null)
                foreach (var d in def.degreeDatas) set.Add(d.degree);
            return set.ToList();
        }

        /// <summary>The smallest reachable degree strictly greater than <paramref name="currentDegree"/>, or null.</summary>
        public static int? AdjacentPlus(TraitDef def, int currentDegree)
        {
            foreach (var d in ReachableDegrees(def))
                if (d > currentDegree) return d;
            return null;
        }

        /// <summary>The largest reachable degree strictly less than <paramref name="currentDegree"/>, or null.</summary>
        public static int? AdjacentMinus(TraitDef def, int currentDegree)
        {
            int? result = null;
            foreach (var d in ReachableDegrees(def))
            {
                if (d < currentDegree) result = d;
                else break; // ReachableDegrees is ascending
            }
            return result;
        }

        /// <summary>A def is a movable spectrum when it authors more than one degree.</summary>
        public static bool IsSpectrum(TraitDef def)
            => def?.degreeDatas != null && def.degreeDatas.Count > 1;

        public static string SpectrumCandidate(string axisId, int targetDegree)
            => axisId + "#" + targetDegree.ToString();

        public static string SingleCandidate(string axisId, bool add)
            => axisId + (add ? "#+" : "#-");

        /// <summary>Extract the axis id (the part before '#') from a candidate id.</summary>
        public static string AxisIdOf(string candidateId)
        {
            if (string.IsNullOrEmpty(candidateId)) return candidateId;
            int i = candidateId.IndexOf('#');
            return i < 0 ? candidateId : candidateId.Substring(0, i);
        }

        /// <summary>
        /// Parse a candidate id back into an axis id and a resolution. Returns false on a malformed id.
        /// For a spectrum move <paramref name="targetDegree"/> is set and <paramref name="singleAdd"/> is
        /// null; for a single add/remove <paramref name="singleAdd"/> is true/false and targetDegree is 0.
        /// </summary>
        public static bool TryParse(string candidateId, out string axisId, out int targetDegree, out bool? singleAdd)
        {
            axisId = null; targetDegree = 0; singleAdd = null;
            if (string.IsNullOrEmpty(candidateId)) return false;
            int i = candidateId.IndexOf('#');
            if (i < 0) { axisId = candidateId; return true; }
            axisId = candidateId.Substring(0, i);
            string tail = candidateId.Substring(i + 1);
            if (tail == "+") { singleAdd = true; return true; }
            if (tail == "-") { singleAdd = false; return true; }
            return int.TryParse(tail, out targetDegree);
        }

        /// <summary>The gender-correct label for a degree on a def, or "none" for the neutral 0 degree.</summary>
        public static string LabelForDegree(TraitDef def, int degree, Pawn pawn)
        {
            if (degree == 0) return "none";
            var data = def?.degreeDatas?.FirstOrDefault(d => d.degree == degree);
            if (data == null) return "none";
            return pawn != null ? data.GetLabelFor(pawn) : data.label;
        }

        /// <summary>
        /// Build the axis view for a pawn: reads current degree / presence and the reachable moves.
        /// Measured pressures are left at 0 for the caller to fill from the pawn's TraitPressure store.
        /// </summary>
        public static TraitAxisCandidate Build(Pawn pawn, TraitDef def)
        {
            if (def == null) return null;
            var c = new TraitAxisCandidate { axisId = def.defName, def = def, isSpectrum = IsSpectrum(def) };

            if (c.isSpectrum)
            {
                c.currentDegree = pawn?.story?.traits?.DegreeOfTrait(def) ?? 0;
                c.currentLabel = LabelForDegree(def, c.currentDegree, pawn);
                c.plusDegree = AdjacentPlus(def, c.currentDegree);
                c.minusDegree = AdjacentMinus(def, c.currentDegree);
                if (c.plusDegree.HasValue) c.plusLabel = LabelForDegree(def, c.plusDegree.Value, pawn);
                if (c.minusDegree.HasValue) c.minusLabel = LabelForDegree(def, c.minusDegree.Value, pawn);
            }
            else
            {
                c.isPresent = pawn?.story?.traits?.HasTrait(def) ?? false;
                // A single trait's label comes straight from its one authored degree (degree may be 0).
                string presentLabel = (def.degreeDatas != null && def.degreeDatas.Count > 0)
                    ? (pawn != null ? def.degreeDatas[0].GetLabelFor(pawn) : def.degreeDatas[0].label)
                    : def.label;
                c.currentLabel = c.isPresent ? presentLabel : "none";
                // The only move for a single trait is to toggle presence.
                if (c.isPresent) c.minusLabel = presentLabel;   // "-" removes it
                else c.plusLabel = presentLabel;                 // "+" adds it
            }
            return c;
        }
    }
}
