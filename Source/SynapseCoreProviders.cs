using System;
using System.Collections.Generic;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// The registration surface for capabilities Core does not own.
    ///
    /// <para><b>The rule this exists to serve:</b> the mod that introduces a mechanic owns its
    /// state and its logic. Core does not store other mods' data — it brokers access to it. A mod
    /// that owns a mechanic registers a provider here; a mod that wants to consult the mechanic
    /// asks Core and gets a documented answer whether or not anybody registered.</para>
    ///
    /// <para><b>This is the "provider" half of Core's extension surface.</b> A provider has exactly
    /// one authoritative answerer, is pulled rather than pushed, and returns a value. That is a
    /// different thing from the broadcast hooks on <see cref="SynapseCoreContext"/>
    /// (<c>OnInjectGenericContext</c>, <c>OnGlobalKnowledgeBroadcast</c>) and
    /// <c>SynapseLetterContextHook</c>, which have many subscribers, push, and return nothing. Use
    /// an event when several mods may each want to contribute; use a provider slot when exactly one
    /// mod is the authority on a question.</para>
    ///
    /// <para><b>Registering without depending on Core.</b> Producers must be able to build and run
    /// with Core absent, so every slot is a public static property that can be set by reflection
    /// and nothing else is required:</para>
    /// <code>
    /// var t = GenTypes.GetTypeInAnyAssembly("RimSynapse.SynapseCoreProviders");
    /// if (t != null)
    /// {
    ///     var slot = t.GetProperty("Residency", BindingFlags.Public | BindingFlags.Static);
    ///     if (slot != null) slot.SetValue(null, (Func&lt;Pawn, bool&gt;)MyResidency.IsResident);
    /// }
    /// </code>
    /// <para>A producer that finds no type, or no slot, logs and carries on. Never add a slot whose
    /// registration needs a Core type in its signature — that would defeat the point.</para>
    ///
    /// <para><b>Every slot must document its unregistered value</b>, and consumers must go through
    /// the accessor rather than null-checking the slot. Consumers inventing their own fallbacks is
    /// how two callers end up disagreeing about what "nobody answered" means.</para>
    /// </summary>
    public static class SynapseCoreProviders
    {
        private const string LogCategory = "providers";

        // Asking for a provider nobody registered is legitimate — it is what happens when the
        // owning mod is not installed. It is also indistinguishable from a provider that answered
        // "nothing", which is the failure mode worth surfacing, so it is said once per slot and
        // then never again.
        private static readonly HashSet<string> warnedUnregistered = new HashSet<string>();

        private static void NoteRegistration(string slot, object provider)
        {
            if (provider == null)
            {
                SynapseLogger.Message($"[RimSynapse-Core] Provider '{slot}' cleared.", LogCategory);
                return;
            }

            // Re-registration is not an error — a mod reloading its own provider is normal — but it
            // is worth seeing, because two mods claiming the same slot means one of them silently
            // stopped answering.
            SynapseLogger.Message($"[RimSynapse-Core] Registered provider '{slot}'.", LogCategory);
            warnedUnregistered.Remove(slot);
        }

        private static void NoteUnregistered(string slot, string consequence)
        {
            if (!warnedUnregistered.Add(slot)) return;
            SynapseLogger.Message(
                $"[RimSynapse-Core] No provider registered for '{slot}'; {consequence}. " +
                "This is expected when the mod that owns it is not installed.", LogCategory);
        }

        // ---------------------------------------------------------------------------------------
        // Population density — owned by RimSynapse - Regions and Territories.
        // ---------------------------------------------------------------------------------------

        private static Func<int, int> populationDensity;

        /// <summary>
        /// How many pawn dwellings stand on a world tile. Owned by Regions and Territories, which
        /// is the only mod that generates them.
        /// <para><b>Unregistered value: 0.</b> No territory mod means no known dwellings, which is
        /// the same answer as an empty tile — correct here, because every consumer uses it as a
        /// weighting input rather than as evidence of absence.</para>
        /// </summary>
        public static Func<int, int> PopulationDensity
        {
            get { return populationDensity ?? LegacyPopulationDensity; }
            set { populationDensity = value; NoteRegistration("PopulationDensity", value); }
        }

        /// <summary>
        /// Dwellings on <paramref name="tile"/>, or 0 if nobody owns the question.
        /// Consumers call this, not the slot.
        /// </summary>
        public static int PopulationDensityAt(int tile)
        {
            if (tile < 0) return 0;

            var provider = PopulationDensity;
            if (provider == null)
            {
                NoteUnregistered("PopulationDensity", "population density reads as 0");
                return 0;
            }

            try
            {
                return provider(tile);
            }
            catch (Exception ex)
            {
                // A throwing provider must not take a storyteller weighting pass with it.
                SynapseLogger.Error($"[RimSynapse-Core] Provider 'PopulationDensity' threw: {ex}", LogCategory);
                return 0;
            }
        }

        // ---------------------------------------------------------------------------------------
        // Residency — owned by RimSynapse - Regions and Territories.
        // ---------------------------------------------------------------------------------------

        private static Func<Pawn, bool> residency;

        /// <summary>
        /// Whether a pawn lives in a generated dwelling rather than merely standing in one. Owned
        /// by Regions and Territories, which generates the dwellings and their occupants and is the
        /// only writer of residency anywhere in the org.
        /// <para><b>Unregistered value: false.</b> With no territory mod nothing generates residents,
        /// so nobody is one. This matches behaviour before the slot existed.</para>
        /// </summary>
        public static Func<Pawn, bool> Residency
        {
            get { return residency; }
            set { residency = value; NoteRegistration("Residency", value); }
        }

        /// <summary>
        /// Whether <paramref name="pawn"/> is a resident, or false if nobody owns the question.
        /// Consumers call this, not the slot.
        /// </summary>
        public static bool IsResident(Pawn pawn)
        {
            if (pawn == null) return false;

            var provider = residency;
            if (provider == null)
            {
                NoteUnregistered("Residency", "no pawn is treated as a resident");
                return false;
            }

            try
            {
                return provider(pawn);
            }
            catch (Exception ex)
            {
                SynapseLogger.Error($"[RimSynapse-Core] Provider 'Residency' threw: {ex}", LogCategory);
                return false;
            }
        }

        // ---------------------------------------------------------------------------------------
        // Legacy
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Reads the pre-registry field on <see cref="SynapseCoreWorldComponent"/> so a copy of
        /// Regions and Territories built before this registry existed keeps working. Remove with
        /// that field once the shim's release has passed.
        /// </summary>
        private static Func<int, int> LegacyPopulationDensity
        {
            get
            {
#pragma warning disable 618
                return SynapseCoreWorldComponent.GetPopulationDensityDelegate;
#pragma warning restore 618
            }
        }

        /// <summary>Test seam: forget which slots have already warned.</summary>
        public static void ResetWarningsForTesting()
        {
            warnedUnregistered.Clear();
        }
    }
}
