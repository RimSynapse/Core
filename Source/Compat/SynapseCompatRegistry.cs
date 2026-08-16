using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RimSynapse.Compat
{
    /// <summary>
    /// The runtime compatibility registry (Core #91, slice 2). Version numbers document intent;
    /// this registry is what actually <i>ensures</i> compatibility, because it is tied to the API
    /// surface that really loaded rather than to a hand-maintained semver that can drift or lie.
    ///
    /// <para>Two things live here:</para>
    /// <list type="bullet">
    /// <item><see cref="CoreApiContract"/> — an integer that bumps ONLY on a breaking change to
    /// Core's public API. Independent of the marketing version. A companion baked against contract
    /// N and loaded on a Core exposing contract &lt; N knows it is incompatible and can degrade
    /// gracefully instead of dying with a <c>TypeLoadException</c> the moment it touches the changed
    /// API.</item>
    /// <item>A decentralized registry: every RimSynapse mod self-registers its facts at load. Core
    /// reads it to produce the authoritative report + letter, but any mod can self-check, so a
    /// Core-less setup still verifies itself.</item>
    /// </list>
    ///
    /// <para><b>Registration is reflection-friendly</b> — a primitive-only signature with no Core
    /// type required — so a companion builds and runs with Core absent, the same rule as
    /// <see cref="SynapseCoreProviders"/>. A consumer registers like this:</para>
    /// <code>
    /// var t = GenTypes.GetTypeInAnyAssembly("RimSynapse.Compat.SynapseCompatRegistry");
    /// int coreContract = (int)(t?.GetField("CoreApiContract")?.GetValue(null) ?? -1); // -1 = Core absent/too old
    /// t?.GetMethod("Register")?.Invoke(null, new object[] {
    ///     "rimsynapse.psychology", "RimSynapse Psychology", "0.7.1", 1, new[] { "memory" } });
    /// </code>
    /// </summary>
    public static class SynapseCompatRegistry
    {
        /// <summary>
        /// Core's current API contract. <b>Bump this — and only this — when a change breaks binary
        /// compatibility for companions</b> (a removed, renamed, or re-signatured public member; see
        /// the binary-compatibility rule in <c>Core/CLAUDE.md</c>). Never bump it for purely additive
        /// changes. Companions compare the contract they were built against to this value.
        /// </summary>
        public const int CoreApiContract = 1;

        /// <summary>A single mod's self-reported compatibility facts.</summary>
        public sealed class Registration
        {
            public string PackageId;
            public string ModName;
            public string Version;
            public int RequiredCoreContract;
            public string[] Capabilities;
        }

        private const string LogCategory = "compat";

        private static readonly Dictionary<string, Registration> registrations =
            new Dictionary<string, Registration>(StringComparer.OrdinalIgnoreCase);

        // Capabilities Core itself provides. A companion may probe for a named capability instead of
        // pinning a version, which is forward-compatible: a mod needing "deferred-events" works with
        // any Core that still has it, whatever the version digit says.
        private static readonly HashSet<string> coreCapabilities = new HashSet<string>(
            new[] { "compat-registry", "provider-slots", "context-hooks", "deferred-events", "memory" },
            StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Self-register a mod's compatibility facts. Reflection-friendly: primitive args only, no
        /// Core type in the signature. Re-registration overwrites — a mod reloading its own entry is
        /// normal. A null/empty <paramref name="packageId"/> is ignored rather than throwing, because
        /// this runs from other mods' constructors and must never take a caller down.
        /// </summary>
        public static void Register(string packageId, string modName, string version,
            int requiredCoreContract, string[] capabilities)
        {
            if (string.IsNullOrEmpty(packageId)) return;

            registrations[packageId] = new Registration
            {
                PackageId = packageId,
                ModName = string.IsNullOrEmpty(modName) ? packageId : modName,
                Version = version,
                RequiredCoreContract = requiredCoreContract,
                Capabilities = capabilities ?? Array.Empty<string>(),
            };

            string state = requiredCoreContract > CoreApiContract ? "INCOMPATIBLE" : "ok";
            SynapseLogger.Message(
                $"[RimSynapse-Core] Compat: registered {registrations[packageId].ModName} v{version} " +
                $"(needs Core API >= {requiredCoreContract}, Core API is {CoreApiContract}) — {state}.",
                LogCategory);
        }

        /// <summary>Remove a registration. Returns whether one was present. (Also a test seam.)</summary>
        public static bool Unregister(string packageId) =>
            !string.IsNullOrEmpty(packageId) && registrations.Remove(packageId);

        /// <summary>Whether a mod has registered under <paramref name="packageId"/>.</summary>
        public static bool IsRegistered(string packageId) =>
            !string.IsNullOrEmpty(packageId) && registrations.ContainsKey(packageId);

        /// <summary>The registration for <paramref name="packageId"/>, or null.</summary>
        public static Registration Get(string packageId)
        {
            if (string.IsNullOrEmpty(packageId)) return null;
            registrations.TryGetValue(packageId, out Registration r);
            return r;
        }

        /// <summary>Every mod that has registered, for the report / tests / debug dump.</summary>
        public static IEnumerable<Registration> All => registrations.Values;

        /// <summary>
        /// Whether Core provides a named capability. A companion probes this (by reflection) to pin a
        /// capability rather than a version. Unknown or null names read false.
        /// </summary>
        public static bool CoreProvides(string capability) =>
            !string.IsNullOrEmpty(capability) && coreCapabilities.Contains(capability);

        /// <summary>
        /// Registered mods whose required contract exceeds what this Core provides — i.e. the ones
        /// that will break, and should have degraded gracefully and be named in the warning letter.
        /// </summary>
        public static IEnumerable<Registration> ContractIncompatibilities() =>
            registrations.Values.Where(r => r.RequiredCoreContract > CoreApiContract);

        /// <summary>Human-readable dump for the debug action / headless validation.</summary>
        public static string Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[RimSynapse] Compat registry — Core API contract {CoreApiContract}, " +
                          $"{registrations.Count} mod(s) registered:");
            foreach (Registration r in registrations.Values.OrderBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase))
            {
                string state = r.RequiredCoreContract > CoreApiContract ? "  <<< INCOMPATIBLE" : "";
                string caps = (r.Capabilities != null && r.Capabilities.Length > 0)
                    ? " [" + string.Join(",", r.Capabilities) + "]" : "";
                sb.AppendLine($"    {r.ModName} ({r.PackageId}) v{r.Version} — needs Core API >= {r.RequiredCoreContract}{caps}{state}");
            }
            sb.AppendLine($"  Core capabilities: {string.Join(", ", coreCapabilities.OrderBy(c => c))}");
            return sb.ToString();
        }

        /// <summary>Test seam: forget every registration.</summary>
        public static void ClearForTesting() => registrations.Clear();
    }
}
