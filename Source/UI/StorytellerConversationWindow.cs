using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;

namespace RimSynapse
{
    /// <summary>
    /// A floating, draggable window that lets players chat directly with the active storyteller
    /// in-game (Core #99, migrated from Conversations).
    ///
    /// The chat log lives on <see cref="SynapseCoreWorldComponent.storytellerChatHistory"/> so it
    /// works even when Conversations is not installed. Opening is gated by
    /// <see cref="SynapseStorytellerContext.IsRimSynapseStorytellerActive"/> (see the toolbar toggle),
    /// so the window stays inert under a vanilla storyteller.
    /// </summary>
    public class StorytellerConversationWindow : Window
    {
        private Vector2 scrollPosition;
        private string inputBuf = "";
        private bool scrollToBottom = true;
        private bool focusInput = true;

        public override Vector2 InitialSize => new Vector2(420f, 520f);

        public StorytellerConversationWindow()
        {
            doCloseX = true;
            closeOnClickedOutside = false;
            draggable = true;
            resizeable = true;
            absorbInputAroundWindow = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var worldComp = Find.World?.GetComponent<SynapseCoreWorldComponent>();
            if (worldComp == null) return;

            // Title Header
            Text.Font = GameFont.Medium;
            string storytellerLabel = Find.Storyteller?.def?.LabelCap ?? "Storyteller";
            Widgets.Label(new Rect(0f, 0f, 250f, 35f), storytellerLabel);
            Text.Font = GameFont.Small;

            // Clear Conversation History Button
            Rect clearRect = new Rect(inRect.width - 100f, 5f, 70f, 24f);
            if (Widgets.ButtonText(clearRect, "Clear"))
            {
                worldComp.storytellerChatHistory.Clear();
                scrollToBottom = true;
            }

            // Scrollable Dialog Feed
            float footerHeight = 50f;
            Rect viewRect = new Rect(0f, 40f, inRect.width, inRect.height - 40f - footerHeight - 10f);
            float scrollWidth = viewRect.width - 16f;
            float totalHeight = CalculateScrollHeight(worldComp.storytellerChatHistory, scrollWidth);
            Rect scrollRect = new Rect(0f, 0f, scrollWidth, totalHeight);

            if (scrollToBottom)
            {
                scrollPosition.y = totalHeight + 1000f;
                scrollToBottom = false;
            }

            Widgets.BeginScrollView(viewRect, ref scrollPosition, scrollRect);
            float curY = 0f;
            foreach (var msg in worldComp.storytellerChatHistory)
            {
                bool isPlayer = msg.sender == "Player";
                float textHeight = Text.CalcHeight(msg.message, scrollWidth - 24f);
                float cardHeight = textHeight + 28f;
                Rect cardRect = new Rect(0f, curY, scrollWidth, cardHeight);

                // Styling cards based on message sender
                Color cardColor = isPlayer ? new Color(0.12f, 0.18f, 0.24f, 0.6f) : new Color(0.18f, 0.14f, 0.22f, 0.6f);
                Widgets.DrawBoxSolid(cardRect, cardColor);
                Widgets.DrawBox(cardRect, 1);

                // Senders Label
                Rect headerRect = new Rect(12f, curY + 4f, scrollWidth - 24f, 18f);
                GUI.color = isPlayer ? new Color(0.6f, 0.8f, 1f) : new Color(0.8f, 0.6f, 1f);
                Widgets.Label(headerRect, isPlayer ? "You" : storytellerLabel);
                GUI.color = Color.white;

                // Message Text
                Rect messageRect = new Rect(12f, curY + 20f, scrollWidth - 24f, textHeight);
                Widgets.Label(messageRect, msg.message);

                curY += cardHeight + 6f;
            }
            Widgets.EndScrollView();

            // Typing area and Send controls
            float footerY = inRect.height - footerHeight;
            Rect inputRect = new Rect(0f, footerY + 10f, inRect.width - 80f, 30f);
            GUI.SetNextControlName("StorytellerChatInput");

            inputBuf = Widgets.TextField(inputRect, inputBuf);

            if (focusInput)
            {
                Verse.UI.FocusControl("StorytellerChatInput", this);
                focusInput = false;
            }

            Rect sendRect = new Rect(inRect.width - 70f, footerY + 10f, 70f, 30f);
            bool pressedEnter = Event.current.type == EventType.KeyDown &&
                               Event.current.keyCode == KeyCode.Return &&
                               GUI.GetNameOfFocusedControl() == "StorytellerChatInput";

            if (Widgets.ButtonText(sendRect, "Send") || pressedEnter)
            {
                if (!string.IsNullOrWhiteSpace(inputBuf))
                {
                    SubmitMessage(worldComp, inputBuf.Trim());
                    inputBuf = "";
                    scrollToBottom = true;
                    focusInput = true;
                    Event.current.Use();
                }
            }
        }

        private float CalculateScrollHeight(List<StorytellerChatMessage> history, float width)
        {
            float total = 0f;
            foreach (var msg in history)
            {
                total += Text.CalcHeight(msg.message, width - 24f) + 34f;
            }
            return total;
        }

        /// <summary>
        /// Appends the player's message to the shared log and dispatches the Chat agent request.
        /// This is the player-facing Chat agent: it speaks in Aura's voice but holds no consequence
        /// tools, and it never triggers a Storyteller turn (#68) — the storyteller reads this log as
        /// read-only sentiment on its own cadence beat.
        /// </summary>
        public void SubmitMessage(SynapseCoreWorldComponent worldComp, string messageText)
        {
            worldComp.storytellerChatHistory.Add(new StorytellerChatMessage("Player", messageText, Find.TickManager.TicksGame));

            string storytellerLabel = Find.Storyteller?.def?.LabelCap ?? "Storyteller";

            var apiMessages = new List<ChatMessage>
            {
                new ChatMessage
                {
                    role = "system",
                    content = $"You are {storytellerLabel}, the AI Storyteller in RimWorld. You are conversing directly with the player. Speak in your established storyteller persona. Keep your responses immersive, engaging, and relatively concise (under 100 words)."
                }
            };

            // Personality profile + difficulty (ceiling and mood mandate) from Core (#66).
            // Empty under a non-RimSynapse storyteller, so nothing is injected there.
            string agentContext = SynapseStorytellerContext.BuildAgentContext(Find.CurrentMap);
            if (!string.IsNullOrEmpty(agentContext))
            {
                apiMessages.Add(new ChatMessage
                {
                    role = "system",
                    content = agentContext
                });
            }

            string colonyContext = SynapseCoreContext.GetContextText("StorytellerChat");
            if (!string.IsNullOrEmpty(colonyContext))
            {
                apiMessages.Add(new ChatMessage
                {
                    role = "system",
                    content = $"Current Colony Context:\n{colonyContext}"
                });
            }

            int startIdx = Mathf.Max(0, worldComp.storytellerChatHistory.Count - 10);
            for (int i = startIdx; i < worldComp.storytellerChatHistory.Count; i++)
            {
                var msg = worldComp.storytellerChatHistory[i];
                apiMessages.Add(new ChatMessage
                {
                    role = msg.sender == "Player" ? "user" : "assistant",
                    content = msg.message
                });
            }

            SynapseClient.ChatAsync(
                RimSynapseMod.ModHandle,
                apiMessages,
                new ChatOptions { priority = 2, requestName = "Storyteller Direct Chat" },
                result =>
                {
                    if (result.success && !string.IsNullOrEmpty(result.content))
                    {
                        string reply = result.content.Trim();

                        SynapseGameComponent.Enqueue(() =>
                        {
                            worldComp.storytellerChatHistory.Add(new StorytellerChatMessage("Storyteller", reply, Find.TickManager.TicksGame));
                            scrollToBottom = true;

                            var extension = Find.Storyteller?.def?.GetModExtension<StorytellerVoiceExtension>();
                            if (extension != null)
                            {
                                TriggerStorytellerVoice(reply, extension);
                            }
                        });
                    }
                }
            );
        }

        private void TriggerStorytellerVoice(string text, StorytellerVoiceExtension extension)
        {
            string routeId = RimSynapseMod.Instance.Settings.defaultRoutingAudio;
            string voice = "";

            if (routeId == RoutingId.OpenAI)
                voice = string.IsNullOrEmpty(extension.openAIVoiceId) ? "nova" : extension.openAIVoiceId;
            else if (routeId == RoutingId.ElevenLabs)
                voice = string.IsNullOrEmpty(extension.elevenLabsVoiceId) ? "pNInz6obpgDQGcFmaJgB" : extension.elevenLabsVoiceId;
            else
                voice = string.IsNullOrEmpty(extension.localVoicePath) ? "Sounds/Voicebox/AuraVoice.wav" : extension.localVoicePath;

            var request = new LlmAudioRequest
            {
                InputText = text,
                Voice = voice,
                ResponseFormat = "pcm"
            };

            if (routeId == RoutingId.Voicebox)
            {
                request.Instruct = "conversational, natural, character voice";
            }

            SynapseClient.SendAudioAsync(
                RimSynapseMod.ModHandle,
                request,
                new ChatOptions { priority = 2, requestName = "Storyteller Response Voice" },
                audioResult =>
                {
                    if (audioResult.success && !string.IsNullOrEmpty(audioResult.base64Audio))
                    {
                        Utils.AudioPlaybackManager.PlayBase64Pcm(audioResult.base64Audio);
                    }
                }
            );
        }
    }
}
