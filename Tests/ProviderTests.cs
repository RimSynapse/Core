// Behaviour tests for the capability provider surface (Core #49).
//
// What is being checked is the part of the contract that consumers rely on and that is easy to
// break silently: that an unregistered slot returns its documented value rather than throwing,
// that a throwing provider cannot take its caller down, that registration is announced, and that
// "nobody registered" is said once rather than every frame — this is read from storyteller
// weighting passes, so a per-call log line would be a performance bug as well as noise.
using System;
using System.Reflection;
using RimSynapse;
using Verse;

namespace ProviderTests
{
    public static class Program
    {
        private static int failures;

        public static int Main()
        {
            Section("an unregistered slot returns its documented value");
            Reset();
            Check("population density reads 0", SynapseCoreProviders.PopulationDensityAt(5) == 0);
            Check("nobody is a resident", SynapseCoreProviders.IsResident(new Pawn()) == false);

            Section("the unregistered notice is said once per slot, not once per call");
            Reset();
            for (int i = 0; i < 50; i++) SynapseCoreProviders.PopulationDensityAt(1);
            Check("fifty calls produce one notice",
                SynapseLogger.CountContaining("No provider registered for 'PopulationDensity'") == 1);
            SynapseCoreProviders.IsResident(new Pawn());
            Check("a different slot gets its own notice",
                SynapseLogger.CountContaining("No provider registered for 'Residency'") == 1);

            Section("a registered provider answers");
            Reset();
            SynapseCoreProviders.PopulationDensity = tile => tile * 10;
            Check("the provider's answer comes back", SynapseCoreProviders.PopulationDensityAt(4) == 40);
            Check("registration is announced",
                SynapseLogger.CountContaining("Registered provider 'PopulationDensity'") == 1);
            Check("no unregistered notice once registered",
                SynapseLogger.CountContaining("No provider registered") == 0);

            var resident = new Pawn("Ada");
            SynapseCoreProviders.Residency = p => p != null && p.name == "Ada";
            Check("residency answers for the resident", SynapseCoreProviders.IsResident(resident));
            Check("...and denies everyone else", !SynapseCoreProviders.IsResident(new Pawn("Bo")));

            Section("clearing a slot restores the documented value");
            Reset();
            SynapseCoreProviders.PopulationDensity = tile => 99;
            SynapseCoreProviders.PopulationDensity = null;
            Check("cleared slot reads 0 again", SynapseCoreProviders.PopulationDensityAt(1) == 0);
            Check("clearing is announced", SynapseLogger.CountContaining("Provider 'PopulationDensity' cleared") == 1);

            Section("a throwing provider cannot take its caller down");
            Reset();
            SynapseCoreProviders.PopulationDensity = tile => throw new InvalidOperationException("boom");
            Check("the documented value is returned instead", SynapseCoreProviders.PopulationDensityAt(3) == 0);
            Check("...and the throw is logged as an error", SynapseLogger.Errors.Count == 1);

            SynapseCoreProviders.Residency = p => throw new InvalidOperationException("boom");
            Check("residency degrades the same way", SynapseCoreProviders.IsResident(new Pawn()) == false);

            Section("null and negative inputs are answered, not thrown at");
            Reset();
            SynapseCoreProviders.Residency = p => true;
            Check("a null pawn is not a resident", SynapseCoreProviders.IsResident(null) == false);
            SynapseCoreProviders.PopulationDensity = tile => 7;
            Check("a negative tile reads 0", SynapseCoreProviders.PopulationDensityAt(-1) == 0);
            Check("...without consulting the provider", SynapseCoreProviders.PopulationDensityAt(0) == 7);

            Section("the text-to-speech slot follows the same contract (Core #111)");
            Reset();
            Check("unregistered reads null through the accessor-side getter",
                SynapseCoreProviders.TextToSpeechOrNote() == null);
            for (int i = 0; i < 50; i++) SynapseCoreProviders.TextToSpeechOrNote();
            Check("fifty unregistered reads produce one notice",
                SynapseLogger.CountContaining("No provider registered for 'TextToSpeech'") == 1);

            string spokenText = null, spokenHint = null;
            SynapseCoreProviders.TextToSpeech = (text, hint, onPcm) => { spokenText = text; spokenHint = hint; return true; };
            Check("registration is announced",
                SynapseLogger.CountContaining("Registered provider 'TextToSpeech'") == 1);
            var tts = SynapseCoreProviders.TextToSpeechOrNote();
            Check("a registered provider is handed back", tts != null);
            Check("...and receives the text and hint", tts("hello rim", "af_heart", null) && spokenText == "hello rim" && spokenHint == "af_heart");

            SynapseCoreProviders.TextToSpeech = null;
            Check("clearing is announced", SynapseLogger.CountContaining("Provider 'TextToSpeech' cleared") == 1);
            Check("cleared slot reads null again", SynapseCoreProviders.TextToSpeechOrNote() == null);

            var ttsSlot = Type.GetType("RimSynapse.SynapseCoreProviders")
                .GetProperty("TextToSpeech", BindingFlags.Public | BindingFlags.Static);
            Check("the slot resolves and registers by reflection", ttsSlot != null && ttsSlot.CanWrite);
            ttsSlot.SetValue(null, (Func<string, string, Action<byte[]>, bool>)((t, h, cb) => true));
            Check("a reflection-registered provider answers", SynapseCoreProviders.TextToSpeechOrNote()("x", null, null));

            Section("a producer can register by reflection, with no reference to Core");
            // This is the whole point of the slot being a public static property: Regions and
            // Territories must build and run with Core absent, so it can only reach this by name.
            Reset();
            var type = Type.GetType("RimSynapse.SynapseCoreProviders");
            Check("the type resolves by name", type != null);
            var slot = type.GetProperty("Residency", BindingFlags.Public | BindingFlags.Static);
            Check("the slot resolves by name", slot != null);
            Check("the slot is settable", slot.CanWrite);
            slot.SetValue(null, (Func<Pawn, bool>)(p => true));
            Check("a reflection-registered provider answers", SynapseCoreProviders.IsResident(new Pawn()));

            var popSlot = type.GetProperty("PopulationDensity", BindingFlags.Public | BindingFlags.Static);
            Check("the population slot resolves too", popSlot != null && popSlot.CanWrite);

            Section("the legacy field still works for a pre-registry build of R&T");
            Reset();
#pragma warning disable 618
            SynapseCoreWorldComponent.GetPopulationDensityDelegate = tile => 12;
#pragma warning restore 618
            Check("the old registration path is honoured", SynapseCoreProviders.PopulationDensityAt(1) == 12);
            SynapseCoreProviders.PopulationDensity = tile => 34;
            Check("a real registration wins over the legacy field",
                SynapseCoreProviders.PopulationDensityAt(1) == 34);
            SynapseCoreProviders.PopulationDensity = null;
            Check("clearing it falls back to the legacy field, not to 0",
                SynapseCoreProviders.PopulationDensityAt(1) == 12);
#pragma warning disable 618
            SynapseCoreWorldComponent.GetPopulationDensityDelegate = null;
#pragma warning restore 618

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL PROVIDER TESTS PASSED" : failures + " PROVIDER TEST(S) FAILED");
            return failures == 0 ? 0 : 1;
        }

        private static void Reset()
        {
            SynapseCoreProviders.PopulationDensity = null;
            SynapseCoreProviders.Residency = null;
            SynapseCoreProviders.TextToSpeech = null;
#pragma warning disable 618
            SynapseCoreWorldComponent.GetPopulationDensityDelegate = null;
#pragma warning restore 618
            SynapseCoreProviders.ResetWarningsForTesting();
            SynapseLogger.Clear();
        }

        private static void Section(string name)
        {
            Console.WriteLine();
            Console.WriteLine("-- " + name);
        }

        private static void Check(string label, bool ok)
        {
            if (!ok) failures++;
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + label);
        }
    }
}
