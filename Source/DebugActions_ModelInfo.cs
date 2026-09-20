using System.Text;
using LudeonTK;
using RimWorld;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for the context-window / tiering fix (Core #139), grouped under "RimSynapse".
    /// Re-polls LM Studio's model info (including the native /api/v0 loaded_context_length), forces a
    /// tier re-evaluation, and dumps the resolved window, tier and per-request budget — so the fix is
    /// confirmable, and so a user can force an immediate refresh after reloading a model (rather than
    /// waiting for the ~30 min periodic re-poll). No-arg, headlessly runnable via run_debug_action.
    /// </summary>
    public static class DebugActions_ModelInfo
    {
        [DebugAction("RimSynapse", "Perf: refresh model info + dump tier/budget (#139)",
            allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.Playing)]
        private static void RefreshAndDump()
        {
            // Synchronous re-poll so the dump reflects the fresh window (the periodic path is async).
            var result = Internal.HttpEngine.GetModelsSync();
            Internal.ModelManager.UpdateCache(result);
            SynapseTierController.Update(force: true);

            var sb = new StringBuilder();
            sb.AppendLine("[RimSynapse] Model info + tiering (Core #139):");
            sb.AppendLine($"  backend metered : {SynapseTierController.IsBackendMetered()}   (tier mode: {SynapseTierController.Mode})");
            sb.AppendLine($"  online          : {result.online}{(result.error != null ? "  error=" + result.error : "")}");
            sb.AppendLine($"  active model    : {Internal.ModelManager.ActiveModel ?? "<none>"}");
            sb.AppendLine($"  reported window : {(Internal.ModelManager.ContextLength.HasValue ? Internal.ModelManager.ContextLength.Value + " tokens (loaded_context_length)" : "<none reported — using setting>")}");
            sb.AppendLine($"  effective window: {SynapseTierController.EffectiveWindow} tokens");
            sb.AppendLine($"  resolved tier   : {SynapseTierController.Current}");
            foreach (var evt in new[] { "event", "dialogue" })
            {
                var op = SynapseTierController.GetOperatingPoint(evt);
                sb.AppendLine($"  budget[{evt,-8}] : {op.MaxPromptTokens} prompt tokens  (governed by {op.GovernedBy})");
            }
            Log.Message(sb.ToString().TrimEnd());
        }
    }
}
