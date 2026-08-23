using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Reports mods that are loaded before something they declare they must follow.
    ///
    /// <para><b>Why this exists.</b> `loadAfter` in About.xml is advisory: RimWorld loads mods in
    /// the order `ModsConfig.xml` lists them and only uses `loadAfter` to nudge the in-game mod
    /// manager. Any tool that writes the modlist alphabetically produces a broken order — and the
    /// failure is silent. When an assembly cannot resolve a reference, RimWorld logs
    /// `ReflectionTypeLoadException getting types in assembly X` and then skips that assembly in
    /// `GenTypes.AllTypes`. The mod's `Mod` subclass is never discovered, so it never *fails* to
    /// instantiate: no Harmony patches bind, no defs resolve, and nothing says the mod is dead.
    /// Observed with Factions ordered before its territory-mod dependency — the whole mod vanished
    /// and the test suite stayed green (RimSynapse/Factions#42, RimSynapse/TestRunner#6).</para>
    ///
    /// <para>This check reads the <i>cause</i> — declared order versus actual order — rather than
    /// the symptom. It therefore fires before def loading, so the explanation appears above the
    /// wall of red rather than buried under it.</para>
    ///
    /// <para>It is deliberately not limited to RimSynapse mods. Any mod pair with a real binary
    /// dependency has this problem, and as more companion mods bind to each other it will recur.</para>
    /// </summary>
    public static class SynapseLoadOrderCheck
    {
        private static bool alreadyRun;

        /// <summary>
        /// Logs one error naming every violation, or nothing at all when the order is sound.
        /// Never throws: a diagnostic that takes the mod down is worse than no diagnostic.
        /// </summary>
        public static void Verify()
        {
            if (alreadyRun) return;
            alreadyRun = true;

            try
            {
                var violations = FindViolations();
                if (violations.Count == 0) return;

                var sb = new StringBuilder();
                // "ERROR" is in the text on purpose. Log.Error leaves no level marker in
                // Player.log — verified: a Log.Error message is byte-identical in form to a
                // Log.Message one, with no stack trace and no Verse.Log line — so the harness
                // classifier can only recognise an error by reading the words (Repo-MCP#17).
                // Without this token a wrong load order is reported to the player and still
                // classified as a clean run.
                sb.Append("[RimSynapse] ERROR - MOD LOAD ORDER IS WRONG. ");
                sb.Append(violations.Count == 1 ? "One mod is" : violations.Count + " mods are");
                sb.AppendLine(" loaded before something it declares it must load after.");
                sb.AppendLine();

                foreach (var v in violations)
                {
                    sb.AppendLine($"  \"{v.ModName}\" must load AFTER \"{v.RequiredName}\", but is currently at " +
                                  $"position {v.ModPosition + 1} while \"{v.RequiredName}\" is at position {v.RequiredPosition + 1}.");
                }

                sb.AppendLine();
                sb.AppendLine("This is not cosmetic. A mod whose dependency loads later cannot resolve its types: it");
                sb.AppendLine("will appear active in the mod list while doing nothing at all — no patches, no defs, no");
                sb.AppendLine("errors of its own. Expect 'Could not find a type named ...' entries below this line.");
                sb.AppendLine();
                sb.AppendLine("Fix it by dragging the mods into the order above in the in-game mod list, or by editing");
                sb.AppendLine("ModsConfig.xml directly. Note that RimWorld does NOT reorder mods for you — loadAfter in");
                sb.AppendLine("About.xml only advises the mod manager; the file's order is what actually loads.");

                Log.Error(sb.ToString());
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimSynapse] Load-order check could not run ([{ex.GetType().Name}] {ex.Message}). " +
                            "This is a diagnostic only; it does not affect the game.");
            }
        }

        /// <summary>A mod ordered before something it declared it must follow.</summary>
        public struct Violation
        {
            public string ModPackageId;
            public string ModName;
            public string RequiredPackageId;
            public string RequiredName;
            public int ModPosition;
            public int RequiredPosition;
        }

        /// <summary>
        /// Exposed so the TestRunner can assert on the same data this reports, rather than
        /// scraping the log for the message above.
        /// </summary>
        public static List<Violation> FindViolations()
        {
            var result = new List<Violation>();

            var active = ModsConfig.ActiveModsInLoadOrder?.ToList();
            if (active == null || active.Count == 0) return result;

            // packageIds are compared lower-invariant throughout: ModsConfig.xml writes them
            // lowercased, while About.xml's loadAfter entries keep their author's casing
            // ("RimSynapse.Psychology"). A case-sensitive compare finds nothing.
            var position = new Dictionary<string, int>();
            for (int i = 0; i < active.Count; i++)
            {
                string id = Normalize(active[i]?.PackageId);
                if (id != null && !position.ContainsKey(id)) position[id] = i;
            }

            for (int i = 0; i < active.Count; i++)
            {
                var meta = active[i];
                if (meta == null) continue;

                foreach (string required in DeclaredPredecessors(meta))
                {
                    string id = Normalize(required);
                    if (id == null) continue;

                    // A loadAfter naming a mod that is not installed is normal and correct —
                    // that is how optional integrations are declared. Only an active mod in the
                    // wrong place is a problem.
                    int requiredPos;
                    if (!position.TryGetValue(id, out requiredPos)) continue;
                    if (requiredPos < i) continue;

                    result.Add(new Violation
                    {
                        ModPackageId = meta.PackageId,
                        ModName = DisplayName(meta),
                        RequiredPackageId = id,
                        RequiredName = DisplayName(active[requiredPos]),
                        ModPosition = i,
                        RequiredPosition = requiredPos,
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// Everything <paramref name="meta"/> declares it must load after, from both `loadAfter`
        /// and `forceLoadAfter`.
        ///
        /// <para>Read by reflection rather than directly. These members have changed shape across
        /// RimWorld versions and a hard reference would make Core fail to build against a version
        /// where one of them is absent — for a diagnostic, that trade is the wrong way round.</para>
        /// </summary>
        private static IEnumerable<string> DeclaredPredecessors(ModMetaData meta)
        {
            // Deduplicated because a property and its backing field are both probed: if
            // ModMetaData exposes both `LoadAfter` and `loadAfter`, the same packageId would
            // otherwise be reported twice.
            //
            // `loadBefore` is deliberately not checked. It exists so a mod can push itself ahead
            // of somebody else's, and treating the mirror case as a violation would report pairs
            // where nothing is actually broken. Assembly resolution only cares about loadAfter.
            var seen = new HashSet<string>();
            foreach (string member in new[] { "LoadAfter", "ForceLoadAfter", "loadAfter", "forceLoadAfter" })
            {
                foreach (string id in ReadStringList(meta, member))
                {
                    string norm = Normalize(id);
                    if (norm != null && seen.Add(norm)) yield return id;
                }
            }
        }

        private static IEnumerable<string> ReadStringList(ModMetaData meta, string member)
        {
            object value = null;

            var prop = typeof(ModMetaData).GetProperty(member,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null)
            {
                value = prop.GetValue(meta, null);
            }
            else
            {
                var field = typeof(ModMetaData).GetField(member,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null) value = field.GetValue(meta);
            }

            var list = value as IEnumerable;
            if (list == null) yield break;

            foreach (object o in list)
            {
                var s = o as string;
                if (!string.IsNullOrEmpty(s)) yield return s;
            }
        }

        private static string DisplayName(ModMetaData meta)
        {
            if (meta == null) return "(unknown)";
            return string.IsNullOrEmpty(meta.Name) ? meta.PackageId : meta.Name;
        }

        private static string Normalize(string packageId)
        {
            if (string.IsNullOrEmpty(packageId)) return null;
            return packageId.Trim().ToLowerInvariant();
        }
    }
}
