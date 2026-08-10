using System;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimSynapse.Patches
{
    /// <summary>
    /// Intercepts major threat letters to rewrite their text via LLM,
    /// adding Aura's spunky personality and commenting on the difficulty.
    /// </summary>
    [HarmonyPatch(typeof(LetterStack), "ReceiveLetter", new Type[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) })]
    public static class Patch_LetterStack_ReceiveLetter
    {
        // Track letters we are currently processing or have already processed
        private static System.Collections.Generic.HashSet<Letter> _processedLetters = new System.Collections.Generic.HashSet<Letter>();

        public static bool Prefix(LetterStack __instance, Letter let, string debugInfo, int delayTicks, bool playSound)
        {
            if (let == null) return true;

            // If we've already processed this letter, let vanilla handle it
            if (_processedLetters.Contains(let)) return true;

            ChoiceLetter choiceLet = let as ChoiceLetter;
            if (choiceLet == null) return true; // Only intercept choice letters

            // Only intercept if Synapse storyteller is the active storyteller
            if (Find.Storyteller?.storytellerComps?.OfType<RimSynapse.Comps.StorytellerComp_Storyteller>().Any() != true) return true;

            // Determine if it's a major threat or a quest
            bool isThreat = let.def == LetterDefOf.ThreatBig;
            bool isQuest = choiceLet.quest != null;

            if (!isThreat && !isQuest) return true; // Not something we want to rewrite

            bool isNewQuest = isQuest && choiceLet.quest.State == QuestState.NotYetAccepted;

            // Attempt to find the Quest Asker (ONLY for new quest offers)
            Pawn asker = null;
            if (isNewQuest && choiceLet.quest.QuestLookTargets != null)
            {
                asker = choiceLet.quest.QuestLookTargets
                    .Select(t => t.Thing as Pawn)
                    .FirstOrDefault(p => p != null && p.Faction != Faction.OfPlayer && !p.Dead);
            }

             // Extract the fully resolved text
            string originalTitle = let.Label.Resolve();
            string originalText = choiceLet.Text.Resolve(); 

            if (isThreat)
            {
                var coreComp = Find.World?.GetComponent<SynapseCoreWorldComponent>();
                if (coreComp != null)
                {
                    string raidId = "Raid_" + Find.TickManager.TicksGame;
                    coreComp.activeRaidEventId = raidId;

                    float curWealth = Find.CurrentMap?.wealthWatcher?.WealthTotal ?? 0f;
                    int curColonists = Find.CurrentMap?.mapPawns?.FreeColonistsCount ?? 0;
                    coreComp.activeRaidTracker = new RimSynapse.Models.RaidTracker(raidId, curWealth, curColonists);

                    coreComp.EnqueuePastEvent(new RimSynapse.Models.PastEvent
                    {
                        eventId = raidId,
                        category = "Threat",
                        eventDescription = $"Threat: {originalTitle}"
                    });
                }
            }

            // Add to processed so we don't infinitely loop when we manually inject it later
            _processedLetters.Add(let);

            var props = RimSynapse.Comps.StorytellerComp_Storyteller.GetActiveStorytellerProps();
            string characterName = props?.characterName ?? Find.Storyteller?.def?.label ?? "AI Storyteller";
            string speakingStyle = props?.speakingStyle ?? "sassy, dramatic, or menacing";
            // Ask the LLM to rewrite it
            string systemPrompt = @"You are " + characterName + @", the AI Storyteller in RimWorld.
A new event or threat has occurred. Rewrite the notification letter to fit your " + speakingStyle + @" persona.
Use the provided vanilla text as the baseline. Maintain all critical gameplay information (who, what, where, rewards, threats).
Do NOT use bracket tags like [Asker_nameFull]. Just use the resolved names provided in the vanilla text.

You MUST respond strictly in valid JSON:
{
  ""Title"": ""Your new dramatic title"",
  ""Description"": ""Your rewritten multi-paragraph description. Mention the consequences.""
}";

            if (asker != null && isNewQuest)
            {
                string factionName = asker.Faction?.Name ?? "an unknown faction";
                string title = "representative";
                if (asker.royalty != null && asker.royalty.MainTitle() != null)
                {
                    title = asker.royalty.MainTitle().GetLabelCapFor(asker);
                }
                
                systemPrompt = $@"You are {asker.Name.ToStringShort}, a {title} of {factionName}.
You are formally contacting a RimWorld colony to offer them a quest or opportunity. 
Write the notification letter from YOUR first-person perspective ('I am {asker.Name.ToStringShort}...').
Use the provided vanilla text as the baseline. Maintain all critical gameplay information (who, what, where, rewards, threats).
Do NOT use bracket tags like [Asker_nameFull]. Just use the resolved names provided in the vanilla text.

You MUST respond strictly in valid JSON:
{{
  ""Title"": ""A formal or dramatic title for your request"",
  ""Description"": ""Your rewritten multi-paragraph letter. Speak directly to the colonists.""
}}";
            }

            string additionalContext = SynapseLetterContextHook.GetAdditionalContext(let, asker);
            if (!string.IsNullOrEmpty(additionalContext))
            {
                systemPrompt += "\n---\nAdditional Tone and Faction Context Guidelines:\n" + additionalContext;
            }

            string worldContext = BuildColonyWorldContext();
            string userMessage = $"Vanilla Title: {originalTitle}\nVanilla Text: {originalText}\nRewrite this event.";
            if (!string.IsNullOrEmpty(worldContext))
                userMessage += $"\n\nGround it in the real world where natural — {worldContext}";

            SynapseClient.PromptAsync(
                RimSynapseMod.ModHandle,
                systemPrompt,
                userMessage,
                result =>
                {
                    if (result.success)
                    {
                        try
                        {
                            string json = RimSynapse.Utils.JsonHelper.ExtractJson(result.content);
                            if (json != null)
                            {
                                var parsed = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, string>>(json);
                                if (parsed != null && parsed.ContainsKey("Title") && parsed.ContainsKey("Description"))
                                {
                                    let.Label = parsed["Title"];
                                    choiceLet.Text = parsed["Description"];

                                    // ONLY overwrite the quest log if this is the initial quest offer!
                                    if (isNewQuest && choiceLet.quest != null)
                                    {
                                        choiceLet.quest.name = parsed["Title"];
                                        choiceLet.quest.description = parsed["Description"];

                                        // Log the Quest Offer in the past events queue
                                        var coreComp = Find.World?.GetComponent<SynapseCoreWorldComponent>();
                                        if (coreComp != null)
                                        {
                                            coreComp.EnqueuePastEvent(new RimSynapse.Models.PastEvent
                                            {
                                                eventId = choiceLet.quest.GetUniqueLoadID(),
                                                category = "Quest",
                                                eventDescription = $"Quest offered: {parsed["Title"]}"
                                            });
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            RimSynapse.SynapseLogger.Warn("core", $"[RimSynapse-Core] Failed to parse letter rewrite: {ex.Message}");
                        }
                    }

                    // Push the letter to the UI on the main thread
                    RimSynapse.SynapseGameComponent.Enqueue(() =>
                    {
                        __instance.ReceiveLetter(let, debugInfo, delayTicks, playSound);
                        TriggerStorytellerAudioComment(let, isThreat);
                    });
                },
                new RimSynapse.ChatOptions { priority = 3, requestName = "Rewrite Letter", targetName = originalTitle } // High priority for UI events
            );

            // Block the vanilla ReceiveLetter call right now, because we will inject it later
            return false;
        }

        private static void TriggerStorytellerAudioComment(Letter let, bool isThreat)
        {
            try
            {
                ChoiceLetter choiceLet = let as ChoiceLetter;
                if (choiceLet == null) return;

                // 1. Get voice settings based on storyteller extension
                var extension = Find.Storyteller?.def?.GetModExtension<StorytellerVoiceExtension>();
                if (extension == null) return;

                string routeId = RimSynapseMod.Instance.Settings.defaultRoutingAudio;
                string resolvedVoice = "";
                if (routeId == RoutingId.OpenAI)
                    resolvedVoice = string.IsNullOrEmpty(extension.openAIVoiceId) ? "nova" : extension.openAIVoiceId;
                else if (routeId == RoutingId.ElevenLabs)
                    resolvedVoice = string.IsNullOrEmpty(extension.elevenLabsVoiceId) ? "pNInz6obpgDQGcFmaJgB" : extension.elevenLabsVoiceId;
                else
                    resolvedVoice = string.IsNullOrEmpty(extension.localVoicePath) ? "Sounds/Voicebox/AuraVoice.wav" : extension.localVoicePath;

                // 2. Draft prompt to get snarky commentary
                var props = RimSynapse.Comps.StorytellerComp_Storyteller.GetActiveStorytellerProps();
                string characterName = props?.characterName ?? Find.Storyteller?.def?.label ?? "AI Storyteller";
                string speakingStyle = props?.speakingStyle ?? "sassy, dramatic, or menacing";
                string systemPrompt = @"You are " + characterName + @", the AI Storyteller in RimWorld.
Generate a brief, 1-sentence reaction or comment to the event that just occurred.
Be " + speakingStyle + @". Keep it under 15 words. Do not use markdown or quotes.
Examples:
- 'Oof, sorry about this one...'
- 'More beggars? Really?'
- 'Well, you asked for it.'
- 'This might get messy.'";

                string userMsg = $"The event title is: {let.Label.Resolve()}\nThe event description is: {choiceLet.Text.Resolve()}";

                SynapseClient.PromptAsync(
                    RimSynapseMod.ModHandle,
                    systemPrompt,
                    userMsg,
                    result =>
                    {
                        if (result.success && !string.IsNullOrWhiteSpace(result.content))
                        {
                            string commentText = RimSynapse.Internal.Sanitizer.Clean(result.content).Trim();
                            
                            // Send to audio engine
                            var audioReq = new LlmAudioRequest
                            {
                                InputText = commentText,
                                Voice = resolvedVoice,
                                ResponseFormat = "pcm"
                            };

                            // Add appropriate instructions for Voicebox/Kokoro if we route to Voicebox
                            if (routeId == RoutingId.Voicebox)
                            {
                                // Aura is sassy/snarky
                                audioReq.Instruct = isThreat ? "sarcastic, warning tone, dry" : "sarcastic, playful, snappy";
                            }

                            SynapseClient.SendAudioAsync(
                                RimSynapseMod.ModHandle,
                                audioReq,
                                new RimSynapse.ChatOptions { priority = 3, requestName = "Storyteller Reaction Voice" },
                                audioResult =>
                                {
                                    if (audioResult.success && !string.IsNullOrEmpty(audioResult.base64Audio))
                                    {
                                        RimSynapse.Utils.AudioPlaybackManager.PlayBase64Pcm(audioResult.base64Audio);
                                    }
                                }
                            );
                        }
                    },
                    new RimSynapse.ChatOptions { priority = 3, requestName = "Storyteller Reaction Commentary", targetName = let.Label.Resolve() }
                );
            }
            catch (Exception ex)
            {
                SynapseLogger.Warning($"Failed to trigger storyteller audio comment: {ex.Message}");
            }
        }

        /// <summary>Compact colony + neighbouring-faction context so generated letters reference the real
        /// world (colony name, population, known factions and how they feel about you) instead of reading
        /// as generic filler (#62). Faction.OfPlayerSilentFail avoids a null-faction Log.Error.</summary>
        private static string BuildColonyWorldContext()
        {
            try
            {
                var player = Faction.OfPlayerSilentFail;
                if (player == null) return "";

                string colony = $"the colony {player.Name}";
                var homeMap = Find.AnyPlayerHomeMap;
                if (homeMap != null) colony += $" ({homeMap.mapPawns.FreeColonistsCount} colonists)";

                var facs = Find.FactionManager?.AllFactionsVisible?
                    .Where(f => f != player && !f.IsPlayer && !f.Hidden && !f.defeated && f.def != null && f.def.humanlikeFaction)
                    .Take(6)
                    .Select(f => $"{f.Name} ({f.RelationKindWith(player)})")
                    .ToList();
                string factions = (facs != null && facs.Count > 0) ? "; nearby factions: " + string.Join(", ", facs) : "";
                return colony + factions + ".";
            }
            catch { return ""; }
        }
    }
}
