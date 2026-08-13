using System.Collections.Generic;
using System.Linq;
using Verse;
using RimSynapse.Comps;
using RimSynapse.Models;

namespace RimSynapse
{
    /// <summary>
    /// Records a pivotal life-event (recruited, arrested, captured, enslaved, converted, freed,
    /// escape attempt, …) as a <b>secure long-term memory</b> — the kind a pawn would never lose
    /// (Core #92).
    ///
    /// <para><b>Data-driven, not hardcoded.</b> The set of pivotal classes is exactly the
    /// <see cref="SynapseMemoryClassDef"/>s marked <c>bornLongTerm=true</c>. Securing is automatic:
    /// <see cref="SynapseCorePawnComp.AddMemory"/> normalizes a memory of a bornLongTerm class to
    /// <c>isLongTerm=true</c>, and the daily maintenance pass never decays or consolidates away a
    /// long-term memory. Adding a new pivotal class is a def edit; only a new <i>trigger</i> needs code.</para>
    ///
    /// <para><b>Idempotent</b> per (pawn, class): each pivotal memory carries a
    /// <c>pivotal:&lt;memoryType&gt;</c> tag, and a repeat of the same event returns the existing one
    /// rather than duplicating. Identity events are recorded once.</para>
    /// </summary>
    public static class SynapsePivotalMemory
    {
        /// <summary>Tag prefix marking a memory as a recorded pivotal life-event; also the dedup key.</summary>
        public const string PivotalTagPrefix = "pivotal:";

        // The canonical pivotal memoryTypes. Kept as constants so triggers and tests share one spelling;
        // the authoritative "is this secured" answer is still the def's bornLongTerm flag, not this list.
        public const string Recruited = "LifeEvent_Recruited";
        public const string Arrested = "LifeEvent_Arrested";
        public const string Captured = "LifeEvent_Captured";
        public const string Enslaved = "LifeEvent_Enslaved";
        public const string Converted = "LifeEvent_Converted";
        public const string Freed = "LifeEvent_Freed";
        public const string EscapeAttempt = "LifeEvent_EscapeAttempt";

        /// <summary>
        /// Record a pivotal memory on <paramref name="pawn"/> (game entry point). Resolves the pawn's
        /// core comp; no-ops safely if it has none. Returns the memory's stable id, or null.
        /// </summary>
        public static string Record(Pawn pawn, string memoryType, string description)
        {
            if (pawn == null) return null;
            var comp = pawn.TryGetComp<SynapseCorePawnComp>();
            if (comp == null) return null;
            return RecordOn(comp, memoryType, description, pawn.ThingID);
        }

        /// <summary>
        /// Comp-level core (also the test seam). No-ops if the comp is null, the memoryType is empty,
        /// or — deliberately — the memoryType is not a <c>bornLongTerm</c> class, so a typo can never
        /// silently create a decaying "pivotal" memory. Idempotent per (comp, memoryType).
        /// </summary>
        public static string RecordOn(SynapseCorePawnComp comp, string memoryType, string description, string aboutPawnId = null)
        {
            if (comp == null || string.IsNullOrEmpty(memoryType)) return null;

            var cls = SynapseMemoryClassDef.For(memoryType);
            if (!cls.bornLongTerm)
            {
                SynapseLogger.Warning(
                    $"[RimSynapse-Core] SynapsePivotalMemory: '{memoryType}' is not a bornLongTerm class — " +
                    "a pivotal memory must be secured. Skipping (add a bornLongTerm SynapseMemoryClassDef for it).");
                return null;
            }

            string tag = PivotalTagPrefix + memoryType;

            // Idempotent: one secured memory per pivotal class per pawn.
            var existing = comp.GetMemoriesByTag(tag);
            if (existing != null && existing.Count > 0) return existing[0].memId;

            var mem = new WeightedMemory
            {
                summary = description,
                memoryType = memoryType,
                weight = cls.baseWeight,
                baseWeight = cls.baseWeight,
                absTick = Find.TickManager != null ? Find.TickManager.TicksAbs : 0L,
                tags = new List<string> { tag },
            };
            if (!string.IsNullOrEmpty(aboutPawnId)) mem.subjectPawnIds.Add(aboutPawnId);

            comp.AddMemory(mem); // NormalizeMemory secures it: bornLongTerm -> isLongTerm
            return mem.memId;
        }

        /// <summary>Whether the pawn already carries a recorded pivotal memory of this class.</summary>
        public static bool Has(SynapseCorePawnComp comp, string memoryType)
        {
            if (comp == null || string.IsNullOrEmpty(memoryType)) return false;
            var m = comp.GetMemoriesByTag(PivotalTagPrefix + memoryType);
            return m != null && m.Count > 0;
        }
    }
}
