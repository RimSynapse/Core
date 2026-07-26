using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Verse;
using RimWorld;

namespace RimSynapse.Internal
{
    /// <summary>
    /// Static constructor loader that scans local mod wiki files and injects them
    /// as ConceptDefs into DefDatabase to expose them inside RimWorld's built-in Learning Helper.
    /// Uses reflection to ensure compile-safe compatibility across RimWorld versions.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class WikiLearningHelperLoader
    {
        static WikiLearningHelperLoader()
        {
            try
            {
                InjectWikiConcepts();
            }
            catch (Exception ex)
            {
                SynapseLogger.Error("Failed to inject Wiki guides into Learning Helper: " + ex.Message);
            }
        }

        private static void InjectWikiConcepts()
        {
            int count = 0;
            
            // Query fields dynamically to verify names
            FieldInfo helpTextField = typeof(ConceptDef).GetField("helpText", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (helpTextField == null)
            {
                // Print all fields and properties to debug log if helpText is missing
                SynapseLogger.Warning("ConceptDef does not contain a field named 'helpText'. Printing all fields/properties:");
                foreach (var f in typeof(ConceptDef).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    SynapseLogger.Message($"  Field: {f.Name} ({f.FieldType.Name})");
                }
                foreach (var p in typeof(ConceptDef).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    SynapseLogger.Message($"  Property: {p.Name} ({p.PropertyType.Name})");
                }
            }

            FieldInfo priorityField = typeof(ConceptDef).GetField("priority", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo opportunityField = typeof(ConceptDef).GetField("opportunity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var mod in LoadedModManager.RunningMods)
            {
                string wikiPath = Path.Combine(mod.RootDir, "Learning");
                if (Directory.Exists(wikiPath))
                {
                    foreach (var file in Directory.GetFiles(wikiPath, "*.md"))
                    {
                        try
                        {
                            string fileName = Path.GetFileNameWithoutExtension(file);
                            string title = fileName.Replace('_', ' ');
                            string rawText = File.ReadAllText(file);
                            string parsedText = ParseMarkdownToRichText(rawText);

                            // Construct a custom ConceptDef
                            ConceptDef concept = new ConceptDef();
                            concept.defName = SanitizeDefName(
                                "RimSynapse_Wiki_" + mod.PackageIdPlayerFacing + "_" + fileName);
                            concept.label = title + " (" + mod.Name + ")";

                            // Filenames can collide across mods once sanitized; a duplicate
                            // defName would shadow the earlier entry in DefDatabase.
                            if (DefDatabase<ConceptDef>.GetNamedSilentFail(concept.defName) != null)
                            {
                                SynapseLogger.Warning($"Skipping duplicate wiki concept '{concept.defName}' from '{file}'.");
                                continue;
                            }
                            
                            // Set fields dynamically via reflection
                            if (helpTextField != null)
                            {
                                helpTextField.SetValue(concept, parsedText);
                            }
                            
                            if (priorityField != null)
                            {
                                priorityField.SetValue(concept, 999f);
                            }

                            if (opportunityField != null)
                            {
                                opportunityField.SetValue(concept, null);
                            }

                            DefDatabase<ConceptDef>.Add(concept);
                            count++;
                        }
                        catch (Exception ex)
                        {
                            SynapseLogger.Warning($"Failed to inject wiki file '{file}': {ex.Message}");
                        }
                    }
                }
            }

            if (count > 0)
            {
                // PlayerKnowledgeDatabase builds its ConceptDef -> knowledge lookup when it is
                // first touched. Defs added afterwards are absent from that dictionary, and
                // LessonAutoActivator calls IsComplete() on every ConceptDef each frame, so
                // without this rebind the injected guides throw KeyNotFoundException in
                // UIRootUpdate continuously.
                try
                {
                    PlayerKnowledgeDatabase.ReloadAndRebind();
                }
                catch (Exception ex)
                {
                    SynapseLogger.Error("Failed to rebind PlayerKnowledgeDatabase after injecting wiki concepts: " + ex.Message);
                }

                SynapseLogger.Message($"[RimSynapse] Successfully injected {count} Wiki guides into the Learning Helper database.");
            }
        }

        /// <summary>
        /// DefNames must be safe identifiers. Wiki filenames can contain hyphens, spaces and
        /// dots (e.g. "cloud-llms"), which are not valid in a defName.
        /// </summary>
        private static string SanitizeDefName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "RimSynapse_Wiki_Unnamed";

            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            return sb.ToString();
        }

        private static string ParseMarkdownToRichText(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return "";

            string[] lines = markdown.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                // Headings
                if (line.StartsWith("# "))
                {
                    lines[i] = "\n<size=18><b>" + line.Substring(2) + "</b></size>\n";
                    continue;
                }
                if (line.StartsWith("## "))
                {
                    lines[i] = "\n<size=15><b>" + line.Substring(3) + "</b></size>\n";
                    continue;
                }
                if (line.StartsWith("### "))
                {
                    lines[i] = "\n<size=13><b>" + line.Substring(4) + "</b></size>\n";
                    continue;
                }

                // Horizontal Rules
                if (line == "---" || line == "___" || line == "***")
                {
                    lines[i] = "<color=grey>────────────────────────────────────────</color>";
                    continue;
                }

                // Bullet points
                if (line.StartsWith("- ") || line.StartsWith("* "))
                {
                    lines[i] = "  • " + line.Substring(2);
                }
                else if (line.StartsWith("  - ") || line.StartsWith("  * "))
                {
                    lines[i] = "    • " + line.Substring(4);
                }

                // Blockquotes
                if (line.StartsWith("> "))
                {
                    lines[i] = "  <i>" + line.Substring(2) + "</i>";
                }
            }

            string processed = string.Join("\n", lines);

            // Bold & Italic Inline formatting
            processed = System.Text.RegularExpressions.Regex.Replace(processed, @"\*\*(.*?)\*\*", "<b>$1</b>");
            processed = System.Text.RegularExpressions.Regex.Replace(processed, @"__(.*?)__", "<b>$1</b>");
            processed = System.Text.RegularExpressions.Regex.Replace(processed, @"\*(.*?)\*", "<i>$1</i>");
            processed = System.Text.RegularExpressions.Regex.Replace(processed, @"_(.*?)_", "<i>$1</i>");

            // Clean up links: [text](url) -> <b>text</b>
            processed = System.Text.RegularExpressions.Regex.Replace(processed, @"\[(.*?)\]\(.*?\)", "<b>$1</b>");

            // Strip image blocks completely
            processed = System.Text.RegularExpressions.Regex.Replace(processed, @"\!\[.*?\]\(.*?\)", "");

            return processed.Trim();
        }
    }
}
