using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// Covers script persistence (Core#20): a mid-wait script survives a snapshot/restore
    /// round trip at the same step, re-anchors its timeout instead of instantly expiring,
    /// logs that its agent chain was interrupted, and still completes. Empty and malformed
    /// persisted data are no-ops — which is also the pre-feature-save path (the scribe
    /// label is absent, so restore receives null).
    /// </summary>
    [SynapseTestSet]
    public static class ScriptPersistenceCases
    {
        private static bool _gateOpen;

        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_ScriptPersistenceRoundTrip", () =>
            {
                string pawnName = AnyPawnName();
                Assert.NotEmpty(pawnName, "no pawn on the map to wait on");

                _gateOpen = false;
                SynapseScriptRunner.RegisterWaitCondition("zz_persist_gate", (p, a) => _gateOpen);

                string tool = FirstReadOnlyTool();
                Assert.NotEmpty(tool, "no non-debug tool available to exercise");

                var script = new SynapseScript
                {
                    scriptName = "zz-persist",
                    steps = new List<SynapseScriptStep>
                    {
                        Step(tool, Args(("resultKey", "persist_probe"))),
                        Step("wait_until", Args(("condition", "zz_persist_gate"), ("pawnName", pawnName), ("timeoutTicks", 600000))),
                    }
                };

                try
                {
                    var log = new List<string>();
                    SynapseScriptRunner.StartScript(script, l => log.Add(l ?? string.Empty));
                    Assert.True(SynapseScriptRunner.GetActiveScriptNames().Contains("zz-persist"),
                        "the script must be mid-wait before snapshotting, got log: " + Join(log));

                    string snapshot = SynapseScriptRunner.SnapshotForSave();
                    Assert.NotEmpty(snapshot, "a snapshot with an active script must not be null");

                    // The snapshot covers EVERY active script, and other mods keep their own
                    // running (a real load clears them first via ClearForLoad, which a test
                    // must not do to a live game). Restore only ours, or the others duplicate.
                    var all = Newtonsoft.Json.Linq.JArray.Parse(snapshot);
                    var ours = new Newtonsoft.Json.Linq.JArray(
                        all.Where(t => (string)t["script"]?["scriptName"] == "zz-persist"));
                    Assert.Equal(1, ours.Count, "the snapshot must contain our waiting script exactly once");

                    // Simulate the process ending: the run is gone, only the snapshot remains.
                    SynapseScriptRunner.AbortScript("zz-persist");
                    Assert.False(SynapseScriptRunner.GetActiveScriptNames().Contains("zz-persist"),
                        "abort must clear the script before restore");

                    int restored = SynapseScriptRunner.RestoreFromSave(ours.ToString(Newtonsoft.Json.Formatting.None));
                    Assert.Equal(1, restored, "exactly one script must be restored");

                    var state = SynapseScriptRunner.GetActiveScriptStates()
                        .FirstOrDefault(s => s.name == "zz-persist");
                    Assert.True(state != null, "the restored script must be active");
                    Assert.Equal(2, state.currentStep, "the restored script must be at its wait step");
                    Assert.True(state.isWaiting, "the restored script must still be waiting");

                    Assert.True(RecentLogLines().Any(l => l.Contains("zz-persist") && l.Contains("agent chain was interrupted")),
                        "the restore must log that the agent chain will not resume");

                    // First tick re-anchors the timeout against the loaded clock.
                    SynapseScriptRunner.Tick();
                    state = SynapseScriptRunner.GetActiveScriptStates()
                        .FirstOrDefault(s => s.name == "zz-persist");
                    Assert.True(state != null, "re-anchoring must not expire the wait");
                    Assert.True(state.remainingWaitTicks > 0 && state.remainingWaitTicks <= 600000,
                        $"remaining wait must be re-anchored within the original budget, got {state?.remainingWaitTicks}");

                    // Meet the condition: the restored script must run to completion.
                    _gateOpen = true;
                    SynapseScriptRunner.Tick();
                    Assert.False(SynapseScriptRunner.GetActiveScriptNames().Contains("zz-persist"),
                        "the restored script must complete once its condition is met");
                    Assert.True(RecentLogLines().Any(l => l.Contains("persist_probe = ")),
                        "the persisted resultKey store must surface in the completion log");

                    return "restored at the wait step, re-anchored, interruption logged, completed";
                }
                finally
                {
                    _gateOpen = false;
                    SynapseScriptRunner.AbortScript("zz-persist");
                }
            });

            yield return new SynapseTestCase("Core_ScriptPersistenceEmptyIsNoop", () =>
            {
                int before = SynapseScriptRunner.ActiveScriptsCount;

                Assert.Equal(0, SynapseScriptRunner.RestoreFromSave(null), "null (pre-feature save) must restore nothing");
                Assert.Equal(0, SynapseScriptRunner.RestoreFromSave(""), "empty string must restore nothing");
                Assert.Equal(0, SynapseScriptRunner.RestoreFromSave("[]"), "an empty list must restore nothing");
                Assert.Equal(before, SynapseScriptRunner.ActiveScriptsCount, "no-op restores must not change active scripts");

                if (before == 0)
                {
                    Assert.True(SynapseScriptRunner.SnapshotForSave() == null,
                        "a snapshot with no active scripts must be null so nothing is written to the save");
                }

                return "null, empty and [] all restore nothing without error";
            });

            yield return new SynapseTestCase("Core_ScriptPersistenceMalformedIsHandled", () =>
            {
                int before = SynapseScriptRunner.ActiveScriptsCount;
                int restored = SynapseScriptRunner.RestoreFromSave("{this is not json");

                Assert.Equal(0, restored, "malformed persisted data must restore nothing");
                Assert.Equal(before, SynapseScriptRunner.ActiveScriptsCount, "malformed data must not change active scripts");
                Assert.True(RecentLogLines().Any(l => l.Contains("could not be read")),
                    "the unreadable data must be reported as a handled warning");

                return "malformed persisted data warned about, nothing restored";
            });
        }

        private static SynapseScriptStep Step(string type, Dictionary<string, object> args)
            => new SynapseScriptStep { type = type, arguments = args };

        private static Dictionary<string, object> Args(params (string key, object value)[] pairs)
        {
            var d = new Dictionary<string, object>();
            foreach (var (key, value) in pairs) d[key] = value;
            return d;
        }

        private static string AnyPawnName()
        {
            var pawn = Find.CurrentMap?.mapPawns?.FreeColonists?.FirstOrDefault()
                       ?? Find.CurrentMap?.mapPawns?.AllPawns?.FirstOrDefault();
            return pawn?.LabelShort;
        }

        private static string FirstReadOnlyTool()
        {
            var tool = SynapseToolRegistry.NonDebugTools
                .Select(t => t.name)
                .Where(n => !string.IsNullOrEmpty(n) && n.StartsWith("get_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n)
                .FirstOrDefault();
            return tool ?? SynapseToolRegistry.NonDebugTools.Select(t => t.name).FirstOrDefault();
        }

        private static IEnumerable<string> RecentLogLines()
        {
            var messages = Log.Messages;
            if (messages == null) return Enumerable.Empty<string>();
            return messages
                .Select(m => m.text ?? string.Empty)
                .Where(t => t.IndexOf(SynapseTestReporter.Tag, StringComparison.Ordinal) < 0)
                .ToList();
        }

        private static string Join(IEnumerable<string> lines)
        {
            var list = lines.ToList();
            return list.Count == 0 ? "<no output>"
                : string.Join(" | ", list.Take(5).Select(l => l.Length > 110 ? l.Substring(0, 110) + "..." : l));
        }
    }
}
