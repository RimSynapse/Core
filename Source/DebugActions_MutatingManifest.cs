using System.Linq;
using System.Text;
using LudeonTK;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for the mutating-tool manifest (Core #101), grouped under
    /// "RimSynapse". Dumps which tools are flagged, asserts every known state-changing
    /// tool is on the manifest (and the deliberate exemption is not), and probes the
    /// gate for real: a gated call to a newly flagged tool must be refused without
    /// executing. Headlessly runnable via the toolkit's run_debug_action.
    /// </summary>
    public static class DebugActions_MutatingManifest
    {
        // The complete manifest EnsureInitialized must flag. Kept in sync with the
        // MarkMutating call; Core_CoreMutatorsAreFlagged asserts the same set in CI.
        private static readonly string[] Expected =
        {
            "possess_colonist", "damage_self_with_equipped", "modify_pawn_state",
            "execute_game_tool", "fire_incident", "send_notification_letter",
            "modify_object_state", "control_turret", "fire_weapon_at_cell",
            "trigger_colonist_break", "attempt_remote_hack", "spawn_hacker_base",
            "set_game_volume",
        };

        [DebugAction("RimSynapse", "Mutating manifest: dump + probe gate",
            allowedGameStates = AllowedGameStates.Playing)]
        private static void ProbeMutatingManifest()
        {
            var sb = new StringBuilder();
            int failures = 0;

            void Check(string name, bool pass, string detail)
            {
                if (!pass) failures++;
                sb.AppendLine($"  {(pass ? "PASS" : "FAIL")} {name} | {detail}");
            }

            var flagged = SynapseToolRegistry.AllTools
                .Where(t => t.isMutating).Select(t => t.name).OrderBy(n => n).ToList();
            sb.AppendLine("[RimSynapse] Mutating manifest probe:");
            sb.AppendLine($"  flagged ({flagged.Count}): {string.Join(", ", flagged)}");

            foreach (var name in Expected)
                Check($"'{name}' flagged", flagged.Contains(name), "on the manifest");

            Check("'write_debugger_log' exempt", !flagged.Contains("write_debugger_log"),
                "diagnostic-only, deliberately unflagged");

            // The gate, exercised for real: gated fire_incident must refuse and not fire.
            string refused = SynapseToolRegistry.ExecuteTool("fire_incident", "{}", allowMutating: false);
            Check("gated fire_incident refused",
                refused != null && refused.Contains("\"error\"") && refused.Contains("not permitted to mutate"),
                refused != null && refused.Length > 120 ? refused.Substring(0, 120) : refused);

            sb.AppendLine(failures == 0
                ? "  RESULT: all checks passed"
                : $"  RESULT: {failures} check(s) FAILED");
            Log.Message(sb.ToString().TrimEnd());
        }
    }
}
