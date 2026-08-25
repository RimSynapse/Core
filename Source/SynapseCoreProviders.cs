using System;
using System.Collections.Generic;
using Verse;
using RimWorld;

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
        // Population density — owned by the territory mod (Regions and Societies, maintained
        // outside RimSynapse).
        // ---------------------------------------------------------------------------------------

        private static Func<int, int> populationDensity;

        /// <summary>
        /// How many pawn dwellings stand on a world tile. Owned by the territory mod — the only
        /// mod that generates them; today that is Regions and Societies, maintained outside
        /// RimSynapse.
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
        // Residency — owned by the territory mod (Regions and Societies, maintained outside
        // RimSynapse).
        // ---------------------------------------------------------------------------------------

        private static Func<Pawn, bool> residency;

        /// <summary>
        /// Whether a pawn lives in a generated dwelling rather than merely standing in one. Owned
        /// by the territory mod, which generates the dwellings and their occupants and is the only
        /// writer of residency.
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
        // Conversation participation — who the colony has a standing relationship with, and so may
        // take part in RimSynapse conversations. Pure vanilla state, no provider slot: Core owns the
        // question so Conversations (#41) and its outsider passes (#52/#53) agree on one answer.
        // Deliberately a SUPERSET of Psychology's IsEligibleForReview — it also counts adopted
        // residents (LivingWorld), because an adopted resident belongs in colony chatter even though
        // the clinical review does not spend an LLM call on them. The two predicates answer different
        // questions and are allowed to differ.
        // ---------------------------------------------------------------------------------------

        /// <summary>Whether <paramref name="pawn"/> may take part in colony conversations: any spawned,
        /// living humanlike the colony has a standing relationship with — its colonists, its prisoners
        /// and slaves, quest lodgers staying with it, and adopted residents. NOT raiders, passing
        /// traders, or unaffiliated visitors (handled separately by the outsider line-bank pass).
        /// Cheap enough to call per-pawn per scan.</summary>
        public static bool MayConverse(Pawn pawn)
        {
            if (pawn == null || pawn.Dead || !pawn.Spawned) return false;
            if (pawn.RaceProps == null || !pawn.RaceProps.Humanlike) return false;
            return pawn.IsColonist
                || pawn.IsPrisonerOfColony
                || pawn.IsSlaveOfColony
                || pawn.IsQuestLodger()
                || IsResident(pawn);
        }

        /// <summary>One-word role for a conversing pawn, for gizmo labels, prompt framing and debug
        /// output: "colonist" | "prisoner" | "slave" | "guest" | "resident", or "outsider" if they may
        /// not converse. Colonist takes priority where a pawn matches more than one.</summary>
        public static string ConversationRole(Pawn pawn)
        {
            if (pawn == null) return "none";
            if (pawn.IsColonist) return "colonist";
            if (pawn.IsPrisonerOfColony) return "prisoner";
            if (pawn.IsSlaveOfColony) return "slave";
            if (pawn.IsQuestLodger()) return "guest";
            if (IsResident(pawn)) return "resident";
            return "outsider";
        }

        // ---------------------------------------------------------------------------------------
        // Text-to-speech — owned by RimSynapse - Local Text to Speech.
        // ---------------------------------------------------------------------------------------

        private static Func<string, string, Action<byte[]>, bool> textToSpeech;

        /// <summary>
        /// Synthesise a line of text as spoken audio. Owned by Local Text to Speech, which is the
        /// only mod that runs a synthesis engine (Kokoro/ONNX).
        ///
        /// <para><b>Contract:</b> <c>(text, voiceHint, onPcm) => accepted</c>. The provider must
        /// return quickly — synthesis happens on the provider's own worker, never on the calling
        /// thread — and deliver 16-bit mono 24 kHz PCM to <c>onPcm</c> when done (the same format
        /// <see cref="Utils.AudioPlaybackManager"/> plays; Core routes delivery to playback and
        /// <c>onPcm</c> is safe to call from any thread). <c>voiceHint</c> is advisory — a Kokoro
        /// voice id (<see cref="KokoroVoices"/>) or a file path — and the provider may ignore it.
        /// Returning false means the request was refused (engine not ready, text rejected); nothing
        /// will be delivered.</para>
        ///
        /// <para><b>Unregistered value: no-op.</b> <see cref="SynapseSpeech.TrySpeak(string)"/>
        /// returns false and no audio plays — silence, exactly as if the speech mod were not
        /// installed. Consumers call the accessor, never this slot.</para>
        /// </summary>
        public static Func<string, string, Action<byte[]>, bool> TextToSpeech
        {
            get { return textToSpeech; }
            set { textToSpeech = value; NoteRegistration("TextToSpeech", value); }
        }

        /// <summary>Accessor-side read with the once-only unregistered note. Used by
        /// <see cref="SynapseSpeech"/>; consumers go through that, not this.</summary>
        internal static Func<string, string, Action<byte[]>, bool> TextToSpeechOrNote()
        {
            var provider = textToSpeech;
            if (provider == null)
                NoteUnregistered("TextToSpeech", "speech requests are silent no-ops");
            return provider;
        }

        // ---------------------------------------------------------------------------------------
        // Legacy
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Reads the pre-registry field on <see cref="SynapseCoreWorldComponent"/> so a
        /// territory-mod build predating this registry keeps working. Remove with that field once
        /// the shim's release has passed.
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
