using System.Linq;
using System.Text;
using LudeonTK;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Debug validation for the Core-owned player↔storyteller chat window (Core #99), grouped under
    /// "RimSynapse". Opens the window and round-trips a canned message end-to-end: appends the player
    /// line to the shared log and dispatches the Chat agent request (the async reply appends a
    /// "Storyteller" line when the provider responds). Headlessly runnable via run_debug_action.
    /// </summary>
    public static class DebugActions_StorytellerChat
    {
        [DebugAction("RimSynapse", "Storyteller chat: open window + send canned message",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void OpenAndSendCannedMessage()
        {
            var worldComp = Find.World?.GetComponent<SynapseCoreWorldComponent>();
            if (worldComp == null)
            {
                SynapseLogger.Warn("core", "[RimSynapse] Storyteller chat: no SynapseCoreWorldComponent — cannot run.");
                return;
            }

            bool gate = SynapseStorytellerContext.IsRimSynapseStorytellerActive;

            var window = Find.WindowStack.WindowOfType<StorytellerConversationWindow>();
            if (window == null)
            {
                window = new StorytellerConversationWindow();
                Find.WindowStack.Add(window);
            }

            int before = worldComp.storytellerChatHistory.Count;
            const string canned = "Aura, are you enjoying yourself up there?";
            window.SubmitMessage(worldComp, canned);
            int after = worldComp.storytellerChatHistory.Count;

            var sb = new StringBuilder();
            sb.AppendLine("[RimSynapse] Storyteller chat debug:");
            sb.AppendLine($"  active storyteller: {Find.Storyteller?.def?.defName ?? "(none)"} | RimSynapse gate: {gate}");
            sb.AppendLine($"  window open: {Find.WindowStack.IsOpen<StorytellerConversationWindow>()}");
            sb.AppendLine($"  chat log: {before} -> {after} message(s) (player line appended, Chat request dispatched)");
            var last = worldComp.storytellerChatHistory.LastOrDefault();
            if (last != null) sb.AppendLine($"  last entry: [{last.sender}] {last.message}");
            sb.AppendLine("  (the storyteller reply appends asynchronously when the LLM provider responds)");
            SynapseLogger.Message(sb.ToString().TrimEnd());
        }

        [DebugAction("RimSynapse", "Storyteller chat: dump chat log",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DumpChatLog()
        {
            var worldComp = Find.World?.GetComponent<SynapseCoreWorldComponent>();
            if (worldComp == null)
            {
                SynapseLogger.Warn("core", "[RimSynapse] Storyteller chat: no SynapseCoreWorldComponent — cannot run.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[RimSynapse] Storyteller chat log: {worldComp.storytellerChatHistory.Count} message(s)");
            foreach (var m in worldComp.storytellerChatHistory)
                sb.AppendLine($"  [{m.sender} @ {m.gameTick}] {m.message}");
            SynapseLogger.Message(sb.ToString().TrimEnd());
        }
    }
}
