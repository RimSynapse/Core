using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimSynapse.Patches
{
    /// <summary>
    /// Intercepts major threat / quest letters and rewrites their text via the LLM in the storyteller's
    /// persona. Migrated onto the general deferred-event pipeline (Core #59): instead of hand-rolling a
    /// hold (an unbounded processed-set, no timeout, no re-validation), the rewrite is a single
    /// <b>stage</b> registered on the <c>letter</c> class, and the pipeline guarantees the letter always
    /// arrives — at the stage's completion or the deadline, whichever comes first — re-validating before
    /// it does. The re-issued letter is recognised by <see cref="_releasing"/> and passes straight through.
    /// </summary>
    [HarmonyPatch(typeof(LetterStack), "ReceiveLetter", new Type[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) })]
    public static class Patch_LetterStack_ReceiveLetter
    {
        // The only retained state: letters currently being re-issued on release. Bounded to in-flight
        // releases (added before the re-issue, removed after), so there is no ever-growing set (defect #2).
        private static readonly HashSet<Letter> _releasing = new HashSet<Letter>();

        /// <summary>Everything the rewrite stage and the release need, carried as the hold payload.</summary>
        private sealed class LetterHold
        {
            public LetterStack instance;
            public Letter let;
            public ChoiceLetter choiceLet;
            public string debugInfo;
            public int delayTicks;
            public bool playSound;
            public bool isThreat;
            public bool isNewQuest;
            public string systemPrompt;
            public string userMessage;
        }

        [StaticConstructorOnStartup]
        private static class Registration
        {
            static Registration()
            {
                SynapseDeferredEvents.RegisterClassification(SynapseDeferredEvents.LetterClass, true);
                SynapseDeferredEvents.RegisterStage(SynapseDeferredEvents.LetterClass, 100,
                    (payload, done) => RunRewriteStage(payload as LetterHold, done));
            }
        }

        public static bool Prefix(LetterStack __instance, Letter let, string debugInfo, int delayTicks, bool playSound)
        {
            if (let == null) return true;
            if (_releasing.Contains(let)) return true;   // our own re-issue on release → let it show

            ChoiceLetter choiceLet = let as ChoiceLetter;
            if (choiceLet == null) return true;          // only intercept choice letters

            // Only when the Synapse storyteller is active.
            if (Find.Storyteller?.storytellerComps?.OfType<RimSynapse.Comps.StorytellerComp_Storyteller>().Any() != true) return true;

            bool isThreat = let.def == LetterDefOf.ThreatBig;
            bool isQuest = choiceLet.quest != null;
            if (!isThreat && !isQuest) return true;      // not something we rewrite

            bool isNewQuest = isQuest && choiceLet.quest.State == QuestState.NotYetAccepted;

            Pawn asker = null;
            if (isNewQuest && choiceLet.quest.QuestLookTargets != null)
            {
                asker = choiceLet.quest.QuestLookTargets
                    .Select(t => t.Thing as Pawn)
                    .FirstOrDefault(p => p != null && p.Faction != Faction.OfPlayer && !p.Dead);
            }

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

            var props = RimSynapse.Comps.StorytellerComp_Storyteller.GetActiveStorytellerProps();
            string characterName = props?.characterName ?? Find.Storyteller?.def?.label ?? "AI Storyteller";
            string speakingStyle = props?.speakingStyle ?? "sassy, dramatic, or menacing";

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
                systemPrompt += "\n---\nAdditional Tone and Faction Context Guidelines:\n" + additionalContext;

            string worldContext = BuildColonyWorldContext();
            string userMessage = $"Vanilla Title: {originalTitle}\nVanilla Text: {originalText}\nRewrite this event.";
            if (!string.IsNullOrEmpty(worldContext))
                userMessage += $"\n\nGround it in the real world where natural — {worldContext}";

            var payload = new LetterHold
            {
                instance = __instance,
                let = let,
                choiceLet = choiceLet,
                debugInfo = debugInfo,
                delayTicks = delayTicks,
                playSound = playSound,
                isThreat = isThreat,
                isNewQuest = isNewQuest,
                systemPrompt = systemPrompt,
                userMessage = userMessage,
            };

            // Hand the letter to the pipeline. If it is held, suppress the vanilla letter now; the pipeline
            // re-issues it via Release() on completion or timeout. If it declines (no storyteller stage, at
            // the concurrency cap, …) the letter fires immediately — the fail-safe direction.
            bool held = SynapseDeferredEvents.TryHold(
                SynapseDeferredEvents.LetterClass,
                payload,
                () => Release(payload),
                () => IsValid(payload));

            return !held;
        }

        /// <summary>The rewrite stage: ask the LLM, apply the result on the main thread, then signal done.</summary>
        private static void RunRewriteStage(LetterHold p, Action done)
        {
            if (p == null) { done(); return; }

            SynapseClient.PromptAsync(
                RimSynapseMod.ModHandle,
                p.systemPrompt,
                p.userMessage,
                result =>
                {
                    // Apply the rewrite and advance the hold on the main thread — never touch the letter
                    // (or signal the pipeline) from the background LLM thread.
                    RimSynapse.SynapseGameComponent.Enqueue(() =>
                    {
                        ApplyRewrite(p, result);
                        done();
                    });
                },
                new RimSynapse.ChatOptions { priority = 3, requestName = "Rewrite Letter", targetName = p.let.Label.Resolve() });
        }

        private static void ApplyRewrite(LetterHold p, ChatResult result)
        {
            if (!result.success) return;
            try
            {
                string json = RimSynapse.Utils.JsonHelper.ExtractJson(result.content);
                if (json == null) return;

                var parsed = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                if (parsed == null || !parsed.ContainsKey("Title") || !parsed.ContainsKey("Description")) return;

                p.let.Label = parsed["Title"];
                p.choiceLet.Text = parsed["Description"];

                if (p.isNewQuest && p.choiceLet.quest != null)
                {
                    p.choiceLet.quest.name = parsed["Title"];
                    p.choiceLet.quest.description = parsed["Description"];

                    var coreComp = Find.World?.GetComponent<SynapseCoreWorldComponent>();
                    coreComp?.EnqueuePastEvent(new RimSynapse.Models.PastEvent
                    {
                        eventId = p.choiceLet.quest.GetUniqueLoadID(),
                        category = "Quest",
                        eventDescription = $"Quest offered: {parsed["Title"]}"
                    });
                }
            }
            catch (Exception ex)
            {
                RimSynapse.SynapseLogger.Warn("core", $"[RimSynapse-Core] Failed to parse letter rewrite: {ex.Message}");
            }
        }

        /// <summary>Re-checked immediately before release: a quest offer that closed during the hold is discarded.</summary>
        private static bool IsValid(LetterHold p)
        {
            if (p?.let == null) return false;
            if (p.isNewQuest)
            {
                var q = p.choiceLet?.quest;
                if (q == null || q.State != QuestState.NotYetAccepted) return false; // offer no longer open
            }
            return true;
        }

        /// <summary>Release: re-issue the (possibly rewritten) letter to the UI and speak the reaction. Runs
        /// on the main thread from the pipeline; the <see cref="_releasing"/> guard lets the re-issue through.</summary>
        private static void Release(LetterHold p)
        {
            if (p?.let == null || p.instance == null) return;
            _releasing.Add(p.let);
            try
            {
                p.instance.ReceiveLetter(p.let, p.debugInfo, p.delayTicks, p.playSound);
                TriggerStorytellerAudioComment(p.let, p.isThreat);
            }
            finally
            {
                _releasing.Remove(p.let);
            }
        }

        private static void TriggerStorytellerAudioComment(Letter let, bool isThreat)
        {
            try
            {
                ChoiceLetter choiceLet = let as ChoiceLetter;
                if (choiceLet == null) return;

                var extension = Find.Storyteller?.def?.GetModExtension<StorytellerVoiceExtension>();
                if (extension == null) return;

                string routeId = RimSynapseMod.Instance.Settings.defaultRoutingAudio;
                string resolvedVoice;
                if (routeId == RoutingId.OpenAI)
                    resolvedVoice = string.IsNullOrEmpty(extension.openAIVoiceId) ? "nova" : extension.openAIVoiceId;
                else if (routeId == RoutingId.ElevenLabs)
                    resolvedVoice = string.IsNullOrEmpty(extension.elevenLabsVoiceId) ? "pNInz6obpgDQGcFmaJgB" : extension.elevenLabsVoiceId;
                else
                    resolvedVoice = string.IsNullOrEmpty(extension.localVoicePath) ? "Sounds/Voicebox/AuraVoice.wav" : extension.localVoicePath;

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

                            // Local TTS gets first claim when its toggle is on (Core #111); the
                            // provider route below stays the fallback so an absent speech mod
                            // changes nothing.
                            if (RimSynapseMod.Instance.Settings.localTtsSpeakLetters
                                && SynapseSpeech.TrySpeak(commentText, extension.localVoicePath))
                                return;

                            var audioReq = new LlmAudioRequest
                            {
                                InputText = commentText,
                                Voice = resolvedVoice,
                                ResponseFormat = "pcm"
                            };
                            if (routeId == RoutingId.Voicebox)
                                audioReq.Instruct = isThreat ? "sarcastic, warning tone, dry" : "sarcastic, playful, snappy";

                            SynapseClient.SendAudioAsync(
                                RimSynapseMod.ModHandle,
                                audioReq,
                                new RimSynapse.ChatOptions { priority = 3, requestName = "Storyteller Reaction Voice" },
                                audioResult =>
                                {
                                    if (audioResult.success && !string.IsNullOrEmpty(audioResult.base64Audio))
                                        RimSynapse.Utils.AudioPlaybackManager.PlayBase64Pcm(audioResult.base64Audio);
                                });
                        }
                    },
                    new RimSynapse.ChatOptions { priority = 3, requestName = "Storyteller Reaction Commentary", targetName = let.Label.Resolve() });
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
