using System;
using System.Collections.Generic;
using RimSynapse.Internal;

namespace RimSynapse
{
    /// <summary>
    /// Main LLM client API. Consumer mods use this to send chat completions
    /// to LM Studio. All calls are queued, budget-enforced, and async with
    /// callbacks delivered on the main thread.
    /// </summary>
    public static class SynapseClient
    {
        /// <summary>Whether LM Studio was reachable on the last check.</summary>
        public static bool IsOnline => ModelManager.IsOnline;

        /// <summary>
        /// The currently active model ID as reported by LM Studio's API.
        /// Updated automatically via keep-alive polling (~30s cache).
        /// Returns null if no model is loaded or LM Studio is offline.
        /// </summary>
        public static string ActiveModelName => ModelManager.ActiveModel;

        /// <summary>
        /// The active model's context window size as reported by LM Studio's API,
        /// or null if unknown / offline.
        /// </summary>
        public static int? ActiveModelContextLength => ModelManager.ContextLength;

        /// <summary>Total queue depth across all mods.</summary>
        public static int TotalQueueDepth => RequestQueue.QueueDepth;

        /// <summary>
        /// Current dynamic throttle level.
        /// 1.0 = full speed, 0.0 = paused.
        /// </summary>
        public static float ThrottleLevel => RequestQueue.ThrottleLevel;

        /// <summary>
        /// GPU stats framework. Populated by an external GPU monitor mod.
        /// Set this property from your GPU monitor mod to push stats into Core.
        /// </summary>
        public static GpuStats Gpu { get; set; } = new GpuStats();

        /// <summary>
        /// Send a chat completion request to LM Studio.
        /// The request is queued and budget-enforced. The callback is invoked
        /// on the main thread when the response arrives.
        /// </summary>
        /// <param name="mod">Handle from <see cref="SynapseCore.Register"/></param>
        /// <param name="messages">OpenAI-format message list</param>
        /// <param name="options">Optional: model, max_tokens, temperature, etc.</param>
        /// <param name="callback">Called on the main thread with the result</param>
        public static void ChatAsync(
            SynapseModHandle mod,
            List<ChatMessage> messages,
            ChatOptions options,
            Action<ChatResult> callback)
        {
            if (mod == null)
            {
                SynapseLogger.Error("ChatAsync called with null mod handle. Register first via SynapseCore.Register().");
                callback?.Invoke(ChatResult.Failure("Mod not registered. Call SynapseCore.Register() first."));
                return;
            }

            // Quicktest mock bypass to avoid slow LLM generation during developer tests
            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-quicktest") >= 0)
            {
                string mockContent = "{\"success\": true}";
                string userMsgLower = messages.FindLast(m => m.role == "user")?.content?.ToLower() ?? "";
                string sysMsgLower = messages.Find(m => m.role == "system")?.content?.ToLower() ?? "";

                if (sysMsgLower.Contains("childhood") || userMsgLower.Contains("childhood"))
                {
                    mockContent = "{\n  \"Memory\": \"I spent my childhood digging trenches and learning the names of wild plants. My hands were always calloused, but I found peace in the quiet woods.\",\n  \"Hometown\": \"Kharstead\",\n  \"Tags\": [\"Origin\", \"Childhood\", \"Plants\"],\n  \"EmotionalTone\": \"neutral\"\n}";
                }
                else if (sysMsgLower.Contains("adulthood") || userMsgLower.Contains("adulthood"))
                {
                    mockContent = "{\n  \"Memory\": \"As an adult, I worked the heavy machinery in the logging camps. One winter, the heating failed, and we survived by felling wood.\",\n  \"Tags\": [\"Adulthood\", \"Plants\", \"Survival\"],\n  \"EmotionalTone\": \"determined\"\n}";
                }
                else if (sysMsgLower.Contains("psychology") || sysMsgLower.Contains("profile") || userMsgLower.Contains("profile") || userMsgLower.Contains("synthesize"))
                {
                    mockContent = "{\n  \"Personality\": \"A quiet and pragmatic individual who values survival. They are driven by practical results.\",\n  \"JungianType\": \"ISTJ\",\n  \"CoreArchetype\": \"Explorer\",\n  \"Temperament\": \"Phlegmatic\",\n  \"FirstImpression\": \"I've arrived. Let's get to work.\",\n  \"LeadershipStyle\": \"Rules through pragmatism and a focus on survival resources.\"\n}";
                }
                else if (sysMsgLower.Contains("narrative ai") || sysMsgLower.Contains("faction") || userMsgLower.Contains("faction"))
                {
                    mockContent = "{\n  \"Description\": \"An ancient faction of survivors who have weathered the harsh rimworld for generations. They value tradition and self-sufficiency, often trading with friendly neighbors while fiercely defending their borders from outlaws.\",\n  \"CurrentHiddenAgenda\": \"They seek to establish dynamic dominance by acquiring advanced tech resources through covert operations.\"\n}";
                }

                callback?.Invoke(new ChatResult { success = true, content = mockContent, model = "QuicktestMockModel" });
                return;
            }

            // Validate messages
            string validationError = InputConverter.Validate(messages);
            if (validationError != null)
            {
                SynapseLogger.Warning($"Input validation failed: {validationError}", mod.ModId);
                callback?.Invoke(ChatResult.Failure(validationError));
                return;
            }

            var req = new LlmTextRequest 
            { 
                Messages = messages, 
                SystemPrompt = "", 
                EnforceJson = false,
                MaxTokens = options?.maxTokens,
                Temperature = options?.temperature,
                DisableThinking = options?.thinking.HasValue == true 
                    ? !options.thinking.Value 
                    : RimSynapseMod.Instance.Settings.disableThinking
            };
            RequestQueue.Enqueue(mod, req, LlmCapabilities.Text, options ?? ChatOptions.Default, callback);
        }

        public static void SendTextAsync(
            SynapseModHandle mod,
            LlmTextRequest request,
            ChatOptions options,
            Action<ChatResult> callback)
        {
            if (mod == null) { callback?.Invoke(ChatResult.Failure("Mod not registered.")); return; }
            RequestQueue.Enqueue(mod, request, LlmCapabilities.Text, options ?? ChatOptions.Default, callback);
        }

        public static void SendVisionAsync(
            SynapseModHandle mod,
            LlmVisionRequest request,
            ChatOptions options,
            Action<ChatResult> callback)
        {
            if (mod == null) { callback?.Invoke(ChatResult.Failure("Mod not registered.")); return; }
            RequestQueue.Enqueue(mod, request, LlmCapabilities.Vision, options ?? ChatOptions.Default, callback);
        }

        public static void SendImageAsync(
            SynapseModHandle mod,
            LlmImageRequest request,
            ChatOptions options,
            Action<ImageResult> callback)
        {
            if (mod == null) { callback?.Invoke(ImageResult.Failure("Mod not registered.")); return; }
            RequestQueue.Enqueue(mod, request, LlmCapabilities.Image, options ?? ChatOptions.Default, callback);
        }

        public static void SendAudioAsync(
            SynapseModHandle mod,
            LlmAudioRequest request,
            ChatOptions options,
            Action<AudioResult> callback)
        {
            if (mod == null) { callback?.Invoke(AudioResult.Failure("Mod not registered.")); return; }
            RequestQueue.Enqueue(mod, request, LlmCapabilities.Audio, options ?? ChatOptions.Default, callback);
        }

        /// <summary>
        /// Quick helper: send a system prompt + user message.
        /// Equivalent to ChatAsync with two messages.
        /// </summary>
        public static void PromptAsync(
            SynapseModHandle mod,
            string systemPrompt,
            string userMessage,
            Action<ChatResult> callback,
            ChatOptions options = null)
        {
            var messages = new List<ChatMessage>();
            if (!string.IsNullOrEmpty(systemPrompt))
                messages.Add(ChatMessage.System(systemPrompt));
            if (!string.IsNullOrEmpty(userMessage))
                messages.Add(ChatMessage.User(userMessage));

            ChatAsync(mod, messages, options, callback);
        }

        /// <summary>
        /// Quick helper: send a JSON string as input. Automatically parses
        /// and converts to ChatMessage list.
        /// </summary>
        public static void ChatFromJsonAsync(
            SynapseModHandle mod,
            string json,
            Action<ChatResult> callback,
            ChatOptions options = null)
        {
            var messages = InputConverter.FromJson(json, out string error);
            if (messages == null)
            {
                SynapseLogger.Warning($"JSON input conversion failed: {error}", mod?.ModId);
                callback?.Invoke(ChatResult.Failure(error));
                return;
            }

            ChatAsync(mod, messages, options, callback);
        }

        /// <summary>
        /// Quick helper: send an XML string as input. Automatically parses
        /// and converts to ChatMessage list.
        /// </summary>
        public static void ChatFromXmlAsync(
            SynapseModHandle mod,
            string xml,
            Action<ChatResult> callback,
            ChatOptions options = null)
        {
            var messages = InputConverter.FromXml(xml, out string error);
            if (messages == null)
            {
                SynapseLogger.Warning($"XML input conversion failed: {error}", mod?.ModId);
                callback?.Invoke(ChatResult.Failure(error));
                return;
            }

            ChatAsync(mod, messages, options, callback);
        }

        /// <summary>
        /// Query loaded models from LM Studio.
        /// Callback runs on the main thread.
        /// </summary>
        public static void GetModelsAsync(Action<ModelsResult> callback)
        {
            ModelManager.GetModels(callback);
        }
        /// <summary>
        /// Registers a low-priority background task with scheduling metadata.
        /// This is the primary API for opportunistic tasks. No XML Defs or type dependencies required.
        ///
        /// Example usage from a companion mod:
        /// <code>
        /// SynapseClient.RegisterOpportunisticTask(ModHandle, "MyMod_BackstoryGen", MyCallback,
        ///     new OpportunisticTaskConfig {
        ///         Label = "Backstory Generation",
        ///         Description = "Generates backstories for NPCs during idle time.",
        ///         Priority = 5,
        ///         Weight = 2.0f,
        ///         CooldownTicks = 15000
        ///     });
        /// </code>
        /// </summary>
        /// <param name="mod">Your mod handle from SynapseCore.Register().</param>
        /// <param name="taskId">Unique string ID for this task.</param>
        /// <param name="callback">The function to call when the queue is idle.</param>
        /// <param name="config">Scheduling config. If null, sensible defaults are used.</param>
        public static void RegisterOpportunisticTask(SynapseModHandle mod, string taskId, Action callback, Internal.OpportunisticTaskConfig config)
        {
            Internal.OpportunisticTaskManager.RegisterTask(mod, taskId, callback, config);
        }

        /// <summary>
        /// Registers a dynamic low-priority background task. The callback returns true if it performed work.
        /// </summary>
        public static void RegisterOpportunisticTask(SynapseModHandle mod, string taskId, Func<bool> callback, Internal.OpportunisticTaskConfig config)
        {
            Internal.OpportunisticTaskManager.RegisterTask(mod, taskId, callback, config);
        }

        /// <summary>
        /// Registers a low-priority background task with default scheduling.
        /// Use the config overload to customize priority, weight, and cooldown.
        /// </summary>
        public static void RegisterOpportunisticTask(SynapseModHandle mod, string taskId, Action callback)
        {
            Internal.OpportunisticTaskManager.RegisterTask(mod, taskId, callback);
        }

        /// <summary>
        /// Legacy overload for backward compatibility. Prefer the config-based overload.
        /// </summary>
        [System.Obsolete("Use RegisterOpportunisticTask(mod, taskId, callback, config) with an OpportunisticTaskConfig.")]
        public static void RegisterOpportunisticTask(SynapseModHandle mod, string taskId, Action callback, int cooldownTicks)
        {
            Internal.OpportunisticTaskManager.RegisterTask(mod, taskId, callback, cooldownTicks);
        }
    }
}
