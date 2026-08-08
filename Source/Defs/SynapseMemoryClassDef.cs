using System.Collections.Generic;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Data-driven per-memoryType tuning: base weight, decay, whether the class is born long-term,
    /// and how much a memory of this class contributes to a neighbour's relational salience.
    /// Loaded from XML (Defs/MemoryClasses/…), mirroring the <see cref="SynapseWeightDef"/> pattern,
    /// so weights/decay can be retuned by XPath without recompiling. Unknown memoryTypes fall back to
    /// <see cref="Fallback"/>.
    ///
    /// <para>The canonical memory weight scale is 0.0–1.0.</para>
    /// </summary>
    public class SynapseMemoryClassDef : Def
    {
        /// <summary>The memoryType this class tunes (e.g. "social", "EventReflection", "BackstoryAdulthood").
        /// Matching is case-insensitive. If omitted, defName is used.</summary>
        public string memoryType;

        /// <summary>Suggested base weight for freshly created memories of this class (0–1).</summary>
        public float baseWeight = 0.3f;

        /// <summary>Per-day decay applied in the daily maintenance pass (before the global multiplier).</summary>
        public float decayRate = 0.1f;

        /// <summary>If true, memories of this class are long-term from creation and never decay.</summary>
        public bool bornLongTerm = false;

        /// <summary>How strongly a memory of this class boosts a neighbour's salience (0–1 multiplier on its weight).</summary>
        public float consolidationContribution = 0.5f;

        // ── Static lookup ────────────────────────────────────────────────
        private static Dictionary<string, SynapseMemoryClassDef> _byType;

        /// <summary>Shared fallback for unknown/legacy memoryTypes. Never null.</summary>
        public static readonly SynapseMemoryClassDef Fallback = new SynapseMemoryClassDef
        {
            defName = "SynapseMemoryClass_Fallback",
            memoryType = null,
            baseWeight = 0.3f,
            decayRate = 0.1f,
            bornLongTerm = false,
            consolidationContribution = 0.5f
        };

        private static void EnsureIndex()
        {
            if (_byType != null) return;
            _byType = new Dictionary<string, SynapseMemoryClassDef>();
            foreach (var def in DefDatabase<SynapseMemoryClassDef>.AllDefsListForReading)
            {
                string key = (def.memoryType ?? def.defName ?? "").ToLowerInvariant();
                if (key.Length == 0) continue;
                _byType[key] = def; // last wins; XPath patches can override
            }
        }

        /// <summary>Resolve the class for a memoryType, or the shared fallback. Never returns null.</summary>
        public static SynapseMemoryClassDef For(string memoryType)
        {
            if (string.IsNullOrEmpty(memoryType)) return Fallback;
            EnsureIndex();
            return _byType.TryGetValue(memoryType.ToLowerInvariant(), out var def) ? def : Fallback;
        }

        /// <summary>Drop the cached index (test hook; also safe after hot XML reload).</summary>
        public static void ResetForTesting() => _byType = null;
    }
}
