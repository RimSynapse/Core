using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimSynapse.Patches;
using Verse;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Guards the Log.Error classifier fix (Repo-MCP#17).
    ///
    /// <para>RimWorld's <c>Log.Error</c> writes to Player.log with no level marker — indistinguishable
    /// in form from a <c>Log.Message</c> line — so the harness classifier could only recognise an
    /// error by the words it used, and a failure reported in plain language read as a clean run.
    /// <see cref="Patch_Log_Error"/> stamps <c>[SYNAPSE-LOGERROR]</c> onto every <c>Log.Error</c> at
    /// the one funnel they all pass through, and <c>readlog.ps1</c> keys on that token.</para>
    ///
    /// <para><b>Neither case emits a real error.</b> A probe that actually called <c>Log.Error</c>
    /// would, correctly, make the whole run blocking — the probe would fail the suite it lives in.
    /// So the marking contract is asserted as a pure function and the interception is asserted
    /// structurally through Harmony, both without writing anything to the log. The classifier's own
    /// half is proven offline against captured logs in the Repo-MCP harness.</para>
    /// </summary>
    [SynapseTestSet]
    public static class LogClassifierCases
    {
        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_LogErrorMarkerContract", () =>
            {
                // The marking is a pure function precisely so it can be checked here without
                // emitting a blocking error into the live run.
                string marked = Patch_Log_Error.Mark("MOD LOAD ORDER IS WRONG");
                Assert.True(marked.StartsWith(Patch_Log_Error.Marker),
                    $"a plain-language error must be marked; got '{marked}'");

                // Idempotent: Log.Error can re-enter and ErrorOnce delegates to it.
                Assert.Equal(marked, Patch_Log_Error.Mark(marked),
                    "marking an already-marked message must be a no-op");

                // The TestRunner's own output goes through Log.Message and must never be counted;
                // guard the one path that could ever route it near an error marker.
                string test = "[SYNAPSE-TEST] PASS Something | detail";
                Assert.Equal(test, Patch_Log_Error.Mark(test),
                    "test output must never be marked as an error");

                return "Log.Error marking is applied once, idempotent, and exempts test output";
            });

            yield return new SynapseTestCase("Core_LogErrorPatchApplied", () =>
            {
                // Structural, not log-scraped: ask Harmony whether the interception is wired, so a
                // noisy startup that rolls the Log.Messages buffer cannot give a false pass.
                var method = typeof(Log).GetMethod("Error", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(string) }, null);
                Assert.NotNull(method, "Verse.Log.Error(string) should resolve");

                var info = Harmony.GetPatchInfo(method);
                Assert.NotNull(info, "Log.Error(string) has no Harmony patch info — the marker patch is not applied");

                bool marked = info.Prefixes != null && info.Prefixes.Any(p =>
                    !string.IsNullOrEmpty(p.owner) && p.owner.IndexOf("RimSynapse", StringComparison.OrdinalIgnoreCase) >= 0);

                Assert.True(marked,
                    "no RimSynapse prefix is attached to Log.Error(string); a mod's plain-language error would not be marked for the classifier");

                return "Log.Error(string) carries the RimSynapse marker prefix";
            });
        }
    }
}
