using System;
using HarmonyLib;
using Verse;

namespace RimSynapse.Patches
{
    /// <summary>
    /// Prefixes every <see cref="Verse.Log.Error(string)"/> message with a recognizable marker so
    /// the harness log classifier can detect an error by <i>level</i> instead of by guessing at the
    /// author's wording.
    ///
    /// <para><b>Why.</b> RimWorld's <c>Log.Error</c> writes its message to Player.log with no level
    /// prefix, no stack trace and no <c>Verse.Log</c> frame — byte-identical in form to a
    /// <c>Log.Message</c> line. <c>readlog.ps1</c> therefore recognised an error only when the text
    /// happened to contain the word "error", so any mod — ours or third-party — that reported a real
    /// failure in plain language was classified as a clean run (Repo-MCP#17). Because
    /// <c>Log.Error</c> is the single funnel every error (including <c>Log.ErrorOnce</c>, which
    /// delegates to it) passes through, marking here catches all of them with one patch.</para>
    ///
    /// <para><b>Coverage boundary.</b> This is applied by <c>harmony.PatchAll()</c> in the mod
    /// constructor, so errors logged <i>before</i> that point are not marked. The one known
    /// pre-patch emitter is <see cref="SynapseLoadOrderCheck"/>, which runs first by design and
    /// carries its own literal "ERROR" token for exactly this reason.</para>
    ///
    /// <para>The classifier half of this fix is the <c>[SYNAPSE-LOGERROR]</c> alternation added to
    /// <c>readlog.ps1</c>'s error pattern.</para>
    /// </summary>
    [HarmonyPatch(typeof(Log), nameof(Log.Error), new Type[] { typeof(string) })]
    public static class Patch_Log_Error
    {
        /// <summary>The token the harness classifier keys on. Changing it requires the matching
        /// change in <c>readlog.ps1</c>.</summary>
        public const string Marker = "[SYNAPSE-LOGERROR]";

        /// <summary>
        /// The marking contract, pure and side-effect-free so it can be asserted directly without
        /// emitting a real (blocking) error into a live run. Idempotent, and never marks the
        /// TestRunner's own output — that goes through <c>Log.Message</c> and must never be counted.
        /// </summary>
        public static string Mark(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.StartsWith(Marker)) return text;             // already marked (re-entry / ErrorOnce)
            if (text.Contains("[SYNAPSE-TEST]")) return text;     // never touch test output
            return Marker + " " + text;
        }

        public static void Prefix(ref string text)
        {
            text = Mark(text);
        }
    }
}
