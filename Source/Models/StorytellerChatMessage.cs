using Verse;

namespace RimSynapse
{
    /// <summary>
    /// One message in the player↔storyteller chat log (Core #99).
    ///
    /// This is the storyteller-window slice of the old Conversations
    /// <c>SynapseConversationMessage</c>, moved into Core so the chat window can live
    /// under the two-agent architecture (#68) and work even when Conversations is not
    /// installed. Pawn-to-pawn dialogue keeps its own <c>SynapseConversationMessage</c>
    /// type in Conversations — the two are intentionally not shared.
    /// </summary>
    public class StorytellerChatMessage : IExposable
    {
        public string sender; // "Player" or "Storyteller"
        public string message;
        public int gameTick;

        public StorytellerChatMessage() {}

        public StorytellerChatMessage(string sender, string message, int gameTick)
        {
            this.sender = sender;
            this.message = message;
            this.gameTick = gameTick;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref sender, "sender");
            Scribe_Values.Look(ref message, "message");
            Scribe_Values.Look(ref gameTick, "gameTick");
        }
    }
}
