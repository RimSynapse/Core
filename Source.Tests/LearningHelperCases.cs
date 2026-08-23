using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Regression cases for the wiki guides injected into RimWorld's Learning Helper
    /// (Core/Source/Internal/WikiLearningHelperLoader.cs).
    ///
    /// Background: the loader adds ConceptDefs via DefDatabase.Add() from a
    /// [StaticConstructorOnStartup]. PlayerKnowledgeDatabase builds its ConceptDef lookup
    /// when first touched, so defs added afterwards were missing from it. Because
    /// LessonAutoActivator calls IsComplete() on every ConceptDef each frame, that threw
    /// KeyNotFoundException in UIRootUpdate continuously. The loader now calls
    /// PlayerKnowledgeDatabase.ReloadAndRebind() after injecting.
    /// </summary>
    [SynapseTestSet]
    public static class LearningHelperCases
    {
        private const string WikiPrefix = "RimSynapse_Wiki_";

        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_WikiConceptsAreKnown", () =>
            {
                var wikiConcepts = WikiConcepts();
                Assert.True(wikiConcepts.Count > 0,
                    "expected at least one injected wiki ConceptDef (none found — did injection run?)");

                // The exact failure that used to spam UIRootUpdate every frame.
                var missing = new List<string>();
                foreach (var c in wikiConcepts)
                {
                    try { PlayerKnowledgeDatabase.IsComplete(c); }
                    catch (KeyNotFoundException) { missing.Add(c.defName); }
                }

                Assert.True(missing.Count == 0,
                    $"{missing.Count} wiki concept(s) missing from PlayerKnowledgeDatabase: " +
                    string.Join(", ", missing.Take(5)));

                return $"{wikiConcepts.Count} wiki concepts all present in PlayerKnowledgeDatabase";
            });

            yield return new SynapseTestCase("Core_AllConceptsSurviveIsComplete", () =>
            {
                // LessonAutoActivator walks every ConceptDef, not just ours, so a def
                // injected by any mod can wedge the UI. Assert the whole database is safe.
                var all = DefDatabase<ConceptDef>.AllDefsListForReading;
                var broken = new List<string>();

                foreach (var c in all)
                {
                    try { PlayerKnowledgeDatabase.IsComplete(c); }
                    catch (KeyNotFoundException) { broken.Add(c.defName); }
                }

                Assert.True(broken.Count == 0,
                    $"{broken.Count}/{all.Count} ConceptDefs throw KeyNotFoundException in IsComplete: " +
                    string.Join(", ", broken.Take(5)));

                return $"all {all.Count} ConceptDefs safe for LessonAutoActivator";
            });

            yield return new SynapseTestCase("Core_ModLoadedWithoutPatchFailure", () =>
            {
                // A Harmony prefix binds its arguments by parameter name, so a mismatch throws
                // while patching. That propagates out of the Mod constructor and stops the whole
                // mod loading, which is far worse than the bug being patched around. If Core's
                // Mod instance is missing, its patches did not apply.
                var mod = LoadedModManager.GetMod<RimSynapseMod>();
                Assert.NotNull(mod, "RimSynapse.Core Mod instance missing — it likely threw while patching");
                return "RimSynapse.Core instantiated and patched cleanly";
            });

            yield return new SynapseTestCase("Core_WikiDefNamesAreValidIdentifiers", () =>
            {
                var offenders = WikiConcepts()
                    .Where(c => !c.defName.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
                    .Select(c => c.defName)
                    .ToList();

                Assert.True(offenders.Count == 0,
                    "wiki defNames must be alphanumeric/underscore only: " + string.Join(", ", offenders.Take(5)));

                return "wiki defNames are valid identifiers";
            });
        }

        private static List<ConceptDef> WikiConcepts()
        {
            return DefDatabase<ConceptDef>.AllDefsListForReading
                .Where(c => c.defName != null && c.defName.StartsWith(WikiPrefix))
                .ToList();
        }
    }
}
