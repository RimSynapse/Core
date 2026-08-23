using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Guards Core's binary surface by reflection.
    ///
    /// Companion and third-party DLLs bind to EXACT method signatures. Appending an optional
    /// parameter to an existing public method or constructor removes the old signature from
    /// the assembly, so a mod compiled against an earlier Core dies with a
    /// MissingMethodException at the call site — and does so silently from the suite's point
    /// of view unless the mod happens to be one we ship and exercise.
    ///
    /// Core_AllModsInstantiated catches this only for OUR mods, and only for calls made
    /// during startup. These cases assert the published signatures still exist regardless of
    /// whether anything in this workspace happens to call them. When Core deliberately
    /// retires an API, delete its entry here in the same commit — never weaken the check to
    /// make a build pass.
    /// </summary>
    [SynapseTestSet]
    public static class PublicSurfaceCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_PublicMethodOverloadsIntact", () =>
            {
                var expected = new (Type owner, string method, Type[] args)[]
                {
                    // Tool registry: the incident that produced this rule.
                    (typeof(SynapseToolRegistry), "RegisterTool",
                        new[] { typeof(string), typeof(string), typeof(object), typeof(Func<string, string>), typeof(bool), typeof(List<string>) }),
                    (typeof(SynapseToolRegistry), "RegisterTool",
                        new[] { typeof(string), typeof(string), typeof(object), typeof(Func<string, string>), typeof(bool), typeof(List<string>), typeof(bool) }),
                    (typeof(SynapseToolRegistry), "ExecuteTool", new[] { typeof(string), typeof(string) }),
                    (typeof(SynapseToolRegistry), "ExecuteTool", new[] { typeof(string), typeof(string), typeof(bool) }),

                    // Script runner: the mutation-gate overload must not have replaced the original.
                    (typeof(SynapseScriptRunner), "StartScript",
                        new[] { typeof(SynapseScript), typeof(Action<string>), typeof(Action) }),
                    (typeof(SynapseScriptRunner), "StartScript",
                        new[] { typeof(SynapseScript), typeof(Action<string>), typeof(Action), typeof(bool) }),
                };

                var missing = expected
                    .Where(e => e.owner.GetMethod(e.method, BindingFlags.Public | BindingFlags.Static, null, e.args, null) == null)
                    .Select(e => $"{e.owner.Name}.{e.method}({string.Join(", ", e.args.Select(a => a.Name))})")
                    .ToList();

                Assert.True(missing.Count == 0,
                    $"{missing.Count} published signature(s) missing from the assembly — a mod bound to them would throw MissingMethodException: "
                    + string.Join(" | ", missing));

                return $"all {expected.Length} published method overloads intact";
            });

            yield return new SynapseTestCase("Core_PublicConstructorOverloadsIntact", () =>
            {
                var expected = new (Type owner, Type[] args)[]
                {
                    // The 3-arg planner ctor predates the autonomous-run flag; the flag must
                    // stay a separate overload so older callers still bind.
                    (typeof(SynapseLlmPlanner),
                        new[] { typeof(string), typeof(Action<string>), typeof(Action<bool, string>) }),
                    (typeof(SynapseLlmPlanner),
                        new[] { typeof(string), typeof(Action<string>), typeof(Action<bool, string>), typeof(bool) }),
                };

                var missing = expected
                    .Where(e => e.owner.GetConstructor(e.args) == null)
                    .Select(e => $"{e.owner.Name}({string.Join(", ", e.args.Select(a => a.Name))})")
                    .ToList();

                Assert.True(missing.Count == 0,
                    $"{missing.Count} published constructor(s) missing from the assembly: " + string.Join(" | ", missing));

                return $"all {expected.Length} published constructor overloads intact";
            });
        }
    }
}
