using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;
using RimSynapse.Compat;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// The runtime compatibility registry + API-contract probe (Core #91 slice 2). Deterministic —
    /// the registry is pure in-memory state. Tests use a unique <c>zz_test_</c> packageId and clean up
    /// with Unregister so the real registrations (Core, Psychology) are never disturbed; nothing here
    /// calls ClearForTesting, which would wipe them before Psychology's own registry case runs.
    /// </summary>
    [SynapseTestSet]
    public static class CompatRegistryCases
    {
        private const string TestId = "zz_test_compat_mod";

        public static IEnumerable<SynapseTestCase> All()
        {
            // Registering the same packageId twice keeps one entry, latest wins.
            yield return new SynapseTestCase("Core_CompatRegistry_RegisterAndDedup", () =>
            {
                try
                {
                    SynapseCompatRegistry.Register(TestId, "Test Mod", "0.1.0", 1, new[] { "test" });
                    SynapseCompatRegistry.Register(TestId, "Test Mod", "0.2.0", 1, new[] { "test" });

                    int count = SynapseCompatRegistry.All.Count(r => r.PackageId == TestId);
                    Assert.Equal(1, count, "a re-registered packageId is deduped to one entry");

                    var got = SynapseCompatRegistry.Get(TestId);
                    Assert.NotNull(got, "the registration is retrievable");
                    Assert.True(got.Version == "0.2.0", $"latest registration wins (was {got.Version})");
                    return $"deduped to 1, version={got.Version}";
                }
                finally { SynapseCompatRegistry.Unregister(TestId); }
            });

            // A mod requiring a contract higher than Core provides is flagged; one at/below is not.
            yield return new SynapseTestCase("Core_CompatRegistry_DetectsContractMismatch", () =>
            {
                try
                {
                    int core = SynapseCompatRegistry.CoreApiContract;

                    SynapseCompatRegistry.Register(TestId, "Future Mod", "9.9", core + 1, null);
                    bool flagged = SynapseCompatRegistry.ContractIncompatibilities().Any(r => r.PackageId == TestId);
                    Assert.True(flagged, "a mod needing a newer contract than Core is reported incompatible");

                    SynapseCompatRegistry.Register(TestId, "Current Mod", "1.0", core, null);
                    bool stillFlagged = SynapseCompatRegistry.ContractIncompatibilities().Any(r => r.PackageId == TestId);
                    Assert.False(stillFlagged, "a mod needing the current contract is compatible");
                    return $"coreContract={core}; future flagged, current ok";
                }
                finally { SynapseCompatRegistry.Unregister(TestId); }
            });

            // Capability probe: known true, unknown/null false.
            yield return new SynapseTestCase("Core_CompatRegistry_CoreProvidesCapability", () =>
            {
                Assert.True(SynapseCompatRegistry.CoreProvides("compat-registry"), "Core advertises its own registry capability");
                Assert.False(SynapseCompatRegistry.CoreProvides("no-such-capability"), "an unknown capability reads false");
                Assert.False(SynapseCompatRegistry.CoreProvides(null), "a null capability reads false, not a throw");
                return "known=true, unknown=false, null=false";
            });

            // The Core-less registration path: find the registry by name and call Register via
            // reflection, exactly as a companion with no Core reference does. Also read the contract.
            yield return new SynapseTestCase("Core_CompatRegistry_ReflectionRegistrationWorks", () =>
            {
                try
                {
                    var t = GenTypes.GetTypeInAnyAssembly("RimSynapse.Compat.SynapseCompatRegistry");
                    Assert.NotNull(t, "the registry type resolves by fully-qualified name (reflection surface)");

                    var contractField = t.GetField("CoreApiContract", BindingFlags.Public | BindingFlags.Static);
                    Assert.NotNull(contractField, "CoreApiContract is reflection-readable");
                    int contract = (int)contractField.GetValue(null);
                    Assert.Equal(SynapseCompatRegistry.CoreApiContract, contract, "reflected contract matches the typed const");

                    var register = t.GetMethod("Register", BindingFlags.Public | BindingFlags.Static);
                    Assert.NotNull(register, "Register is reflection-invokable");
                    register.Invoke(null, new object[] { TestId, "Reflected Mod", "0.5", contract, new[] { "test" } });

                    Assert.True(SynapseCompatRegistry.IsRegistered(TestId), "the reflection-registered mod landed in the registry");
                    return $"reflected register ok, contract={contract}";
                }
                finally { SynapseCompatRegistry.Unregister(TestId); }
            });
        }
    }
}
