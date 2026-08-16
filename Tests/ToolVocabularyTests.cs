using System;
using System.Linq;

// Tier-1 sandbox for the storyteller tool vocabulary (Core #63): the allowlist itself and
// the difficulty clamp, compiled game-free under mono. The executor-side enforcement
// (registry refusal, script rejection, ambient scope) needs the game and is covered by the
// Tier-2 TestRunner cases Core_StorytellerVocabularyRejectsUnlisted and
// Core_ConsequenceToolClampsToBudget.
public static class ToolVocabularyProgram
{
    static int fails = 0;

    static void Section(string title) => Console.WriteLine($"\n== {title}");

    static void Check(string name, bool pass)
    {
        if (!pass) fails++;
        Console.WriteLine($"  {(pass ? "PASS" : "FAIL")} {name}");
    }

    static int Main()
    {
        Section("The storyteller scope exists and carries the curated allowlist");
        {
            Check("storyteller scope is defined", RimSynapse.SynapseToolVocabulary.IsDefined(RimSynapse.SynapseToolVocabulary.StorytellerScope));
            Check("fire_incident is in (the core surface)", RimSynapse.SynapseToolVocabulary.IsAllowed("storyteller", "fire_incident"));
            Check("trigger_colonist_break is in (a mood beat)", RimSynapse.SynapseToolVocabulary.IsAllowed("storyteller", "trigger_colonist_break"));
            Check("get_colony_moods is in (read-only context)", RimSynapse.SynapseToolVocabulary.IsAllowed("storyteller", "get_colony_moods"));
            Check("possess_colonist is out (fiat control)", !RimSynapse.SynapseToolVocabulary.IsAllowed("storyteller", "possess_colonist"));
            Check("modify_pawn_state is out (sheet editing)", !RimSynapse.SynapseToolVocabulary.IsAllowed("storyteller", "modify_pawn_state"));
            Check("execute_game_tool is out (launder path)", !RimSynapse.SynapseToolVocabulary.IsAllowed("storyteller", "execute_game_tool"));
            Check("damage_self_with_equipped is out (death by fiat)", !RimSynapse.SynapseToolVocabulary.IsAllowed("storyteller", "damage_self_with_equipped"));
        }

        Section("Scope semantics: unscoped allows, undefined scope fails closed");
        {
            Check("null scope allows anything", RimSynapse.SynapseToolVocabulary.IsAllowed(null, "possess_colonist"));
            Check("empty scope allows anything", RimSynapse.SynapseToolVocabulary.IsAllowed("", "possess_colonist"));
            Check("undefined scope denies everything", !RimSynapse.SynapseToolVocabulary.IsAllowed("no_such_scope", "get_colony_moods"));
            Check("null tool name denied under a scope", !RimSynapse.SynapseToolVocabulary.IsAllowed("storyteller", null));
            Check("unknown verb denied under the scope", !RimSynapse.SynapseToolVocabulary.IsAllowed("storyteller", "zz_not_a_tool"));
        }

        Section("Case-insensitivity: the runner compares OrdinalIgnoreCase, so must the scope");
        {
            Check("Fire_Incident matches", RimSynapse.SynapseToolVocabulary.IsAllowed("storyteller", "Fire_Incident"));
            Check("STORYTELLER scope name matches", RimSynapse.SynapseToolVocabulary.IsAllowed("STORYTELLER", "fire_incident"));
        }

        Section("Companion contribution: Add extends a scope, Define replaces it");
        {
            RimSynapse.SynapseToolVocabulary.Add("storyteller", "zz_companion_verb");
            Check("added verb is allowed", RimSynapse.SynapseToolVocabulary.IsAllowed("storyteller", "zz_companion_verb"));

            RimSynapse.SynapseToolVocabulary.Define("zz_scope", "alpha", "beta");
            Check("defined scope allows its names", RimSynapse.SynapseToolVocabulary.IsAllowed("zz_scope", "beta"));
            RimSynapse.SynapseToolVocabulary.Define("zz_scope", "gamma");
            Check("redefine replaces, not extends", !RimSynapse.SynapseToolVocabulary.IsAllowed("zz_scope", "alpha")
                && RimSynapse.SynapseToolVocabulary.IsAllowed("zz_scope", "gamma"));

            Check("Tools() lists the scope", RimSynapse.SynapseToolVocabulary.Tools("zz_scope").SequenceEqual(new[] { "gamma" }));
            Check("Tools() of undefined scope is empty", !RimSynapse.SynapseToolVocabulary.Tools("no_such_scope").Any());
        }

        Section("ClampToBudget: over-budget is clamped, in-budget passes, no ceiling passes");
        {
            Check("over the ceiling clamps", RimSynapse.SynapseToolVocabulary.ClampToBudget(5000f, 800f) == 800f);
            Check("under the ceiling passes", RimSynapse.SynapseToolVocabulary.ClampToBudget(300f, 800f) == 300f);
            Check("at the ceiling passes", RimSynapse.SynapseToolVocabulary.ClampToBudget(800f, 800f) == 800f);
            Check("no known ceiling passes through", RimSynapse.SynapseToolVocabulary.ClampToBudget(5000f, 0f) == 5000f);
        }

        Section("ResolvePoints: the consequence-tool contract");
        {
            Check("no override yields the ceiling", RimSynapse.SynapseToolVocabulary.ResolvePoints(0f, 800f, "storyteller") == 800f);
            Check("scoped override is clamped", RimSynapse.SynapseToolVocabulary.ResolvePoints(99999f, 800f, "storyteller") == 800f);
            Check("scoped in-budget override passes", RimSynapse.SynapseToolVocabulary.ResolvePoints(400f, 800f, "storyteller") == 400f);
            Check("unscoped override is untouched (operator god-mode)", RimSynapse.SynapseToolVocabulary.ResolvePoints(99999f, 800f, null) == 99999f);
            Check("negative override treated as no override", RimSynapse.SynapseToolVocabulary.ResolvePoints(-5f, 800f, "storyteller") == 800f);
        }

        Console.WriteLine(fails == 0 ? "\nToolVocabulary: ALL PASSED" : $"\nToolVocabulary: {fails} FAILED");
        return fails;
    }
}
