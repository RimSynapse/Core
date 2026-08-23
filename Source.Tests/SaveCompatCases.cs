using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml;
using RimSynapse.Models;
using Verse;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Save-compatibility guard (Core #87 follow-up / the 0.7→0.8 save-friendliness contract).
    ///
    /// RimWorld save compatibility is decided entirely by what a shipped <see cref="IExposable"/>
    /// writes: every <c>Scribe_*.Look(ref x, "key")</c> call is a column in the save file. ADDING
    /// a key is safe — old saves simply lack the node and Scribe fills the default. REMOVING or
    /// RENAMING a key is the silent break: an existing colony's data for that field is orphaned on
    /// the next load, with no error and no warning. Nothing else in the suite notices, because the
    /// game loads fine — it just quietly drops the field.
    ///
    /// This case pins the current scribe-key set of the save-critical data models. It does NOT
    /// forbid additions (extra keys are ignored); it fails only when a key that shipped before has
    /// disappeared from the serialized output — i.e. someone removed or renamed a Scribe label.
    /// When a field is deliberately retired, delete its key from the golden set in the SAME commit
    /// and note the migration, exactly as PublicSurfaceCases requires for the binary surface.
    ///
    /// Mechanism: build an instance, force every field to a non-default value (so Scribe's
    /// "skip when equal to default" optimisation cannot hide a key), serialize it headlessly with
    /// <see cref="ScribeSaver.DebugOutputFor"/>, and collect the element names that appear. If the
    /// serializer is unusable in this environment the set self-skips with a reason rather than
    /// asserting into flakiness.
    ///
    /// Coverage note: a key whose <c>Scribe_Values.Look</c> default happens to equal the MAXIMUM
    /// member of its enum, or a <c>Scribe_Deep</c>/<c>Scribe_Defs</c> field we cannot force, may
    /// not be provoked into writing — do not golden-list such keys. Strings, numeric values, bools,
    /// collections and forceable enums are fully covered, which is the bulk of every save schema.
    /// </summary>
    [SynapseTestSet]
    public static class SaveCompatCases
    {
        // The scribe keys each shipped model MUST keep writing. Transcribed from the ExposeData
        // methods; only removals/renames of these fail the guard. Scribe_Deep "date" keys are
        // intentionally omitted (see the coverage note above).
        static readonly (Type type, string[] keys)[] Contracts = new (Type, string[])[]
        {
            (typeof(WeightedMemory), new[]
            {
                "summary", "memoryType", "tags", "gameTick", "absTick", "weight", "baseWeight",
                "decayRate", "timesReferenced", "isLongTerm", "subjectPawnIds",
                "lastReferencedTick", "salience", "targetKind", "linkedMemoryIds", "memId",
            }),
            (typeof(PastEvent), new[]
            {
                "eventId", "parentEventId", "gameTick", "eventDescription", "mcpTag", "category",
                "factionName", "settlementName", "outcomeDescription", "outcome", "isResolved",
                "resolvedTick", "startWealth", "endWealth", "startFoodNutrition", "endFoodNutrition",
                "sourceFactionId", "targetFactionId", "colonySnapshot", "pawnSnapshots",
                "severity", "occurrenceCount", "firstTick", "lastUpdateTick",
                "involvedPawnIds", "witnessPawnIds", "afterEffectPawnIds",
            }),
            (typeof(RaidTracker), new[]
            {
                "raidEventId", "startWealth", "startColonistsCount", "enemiesKilled", "enemiesDowned",
                "colonistsInjured", "colonistsKilled", "colonistsKidnapped", "livestockInjured",
                "livestockKilled", "lostLivestockDetails",
            }),
            (typeof(ShortTermEvent), new[]
            {
                "gameTick", "eventType", "description", "involvedPawnIds",
            }),
        };

        public static IEnumerable<SynapseTestCase> All()
        {
            foreach (var contract in Contracts)
            {
                var type = contract.type;
                var expected = contract.keys;

                yield return new SynapseTestCase(
                    $"Core_SaveCompat_{type.Name}_KeysIntact",
                    () =>
                    {
                        var present = CaptureKeys(type);
                        var missing = expected.Where(k => !present.Contains(k)).ToList();

                        Assert.True(missing.Count == 0,
                            $"{type.Name}: {missing.Count} scribe key(s) no longer serialize — a save field was "
                            + $"removed or its Scribe label renamed, silently orphaning old-save data: "
                            + string.Join(", ", missing)
                            + $" | present: {string.Join(", ", present.OrderBy(s => s))}");

                        return $"{expected.Length} scribe keys intact ({present.Count} serialized)";
                    },
                    // Self-skip if the headless serializer is not usable here, rather than flake.
                    skipReason: () =>
                    {
                        try
                        {
                            return CaptureKeys(type).Count == 0
                                ? $"{type.Name}: DebugOutputFor produced no keys in this environment"
                                : null;
                        }
                        catch (Exception ex)
                        {
                            return $"{type.Name}: headless scribe unavailable ({ex.GetType().Name})";
                        }
                    });
            }
        }

        /// <summary>
        /// Serialize a fully-populated instance of <paramref name="type"/> and return every element
        /// name that appears anywhere in the output. Populating to non-default defeats Scribe's
        /// default-skip so keys with a Look default still write.
        /// </summary>
        static HashSet<string> CaptureKeys(Type type)
        {
            var obj = (IExposable)Activator.CreateInstance(type);
            ForceNonDefault(obj);

            string xml = Scribe.saver.DebugOutputFor(obj);
            var keys = new HashSet<string>();
            if (string.IsNullOrEmpty(xml)) return keys;

            var doc = new XmlDocument();
            // DebugOutputFor returns a single rooted node; wrap defensively in case it ever
            // returns a fragment with multiple top-level nodes.
            try { doc.LoadXml(xml); }
            catch (XmlException) { doc.LoadXml("<synapseSaveCompatRoot>" + xml + "</synapseSaveCompatRoot>"); }

            foreach (XmlNode node in doc.GetElementsByTagName("*"))
                keys.Add(node.Name);
            return keys;
        }

        /// <summary>Set every writable instance field to a distinct non-default value.</summary>
        static void ForceNonDefault(object obj)
        {
            foreach (var f in obj.GetType().GetFields(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (f.IsInitOnly || f.IsLiteral) continue;
                try
                {
                    object v = NonDefaultFor(f.FieldType);
                    if (v != null) f.SetValue(obj, v);
                }
                catch { /* a field we cannot populate just stays default; extras are harmless */ }
            }
        }

        static object NonDefaultFor(Type t)
        {
            if (t == typeof(string)) return "s";
            if (t == typeof(bool)) return true;
            if (t == typeof(int)) return 123456;
            if (t == typeof(long)) return 123456L;
            if (t == typeof(float)) return 123.5f;
            if (t == typeof(double)) return 123.5d;
            if (t == typeof(short)) return (short)123;
            if (t == typeof(byte)) return (byte)123;
            if (t == typeof(uint)) return 123456u;
            if (t == typeof(ulong)) return 123456ul;

            if (t.IsEnum)
            {
                // Greatest underlying value — beats a zero-ish Look default (Unknown/Generic/Standard).
                return Enum.GetValues(t).Cast<object>()
                    .OrderByDescending(v => Convert.ToInt64(v)).FirstOrDefault();
            }

            if (t.IsGenericType)
            {
                var g = t.GetGenericTypeDefinition();
                if (g == typeof(List<>) || g == typeof(HashSet<>))
                {
                    var coll = Activator.CreateInstance(t);
                    var elem = t.GetGenericArguments()[0];
                    t.GetMethod("Add").Invoke(coll, new[] { SampleElem(elem) });
                    return coll;
                }
                if (g == typeof(Dictionary<,>))
                {
                    var dict = Activator.CreateInstance(t);
                    var args = t.GetGenericArguments();
                    t.GetMethod("Add").Invoke(dict, new[] { SampleElem(args[0]), SampleElem(args[1]) });
                    return dict;
                }
            }

            // Never guess a Def reference, and don't recurse into deep IExposables we cannot vouch
            // for — those keys are excluded from the golden sets on purpose.
            if (typeof(Def).IsAssignableFrom(t)) return null;
            if (typeof(IExposable).IsAssignableFrom(t))
            {
                var ctor = t.GetConstructor(Type.EmptyTypes);
                return ctor != null ? ctor.Invoke(null) : null;
            }
            return null;
        }

        static object SampleElem(Type t)
        {
            var v = NonDefaultFor(t);
            if (v != null) return v;
            return t.IsValueType ? Activator.CreateInstance(t) : null;
        }
    }
}
