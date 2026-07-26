using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Persistent mod settings stored in RimWorld's config directory.
    /// Accessible in-game via Mod Settings -> RimSynapse Core.
    /// </summary>
    public class RimSynapseSettings : ModSettings
    {
        // --- Connection ---
        public ApiProvider apiProvider = ApiProvider.Local_LMStudio; // Default provider for backwards compatibility
        public string lmStudioUrl = "http://127.0.0.1:1234";
        public string lmStudioApiKey = "";
        
        public string janUrl = "http://127.0.0.1:1337/v1";
        public string janApiKey = "";
        
        public string openAiUrl = "https://api.openai.com/v1";
        public string openAiApiKey = "";
        
        public string geminiUrl = "https://generativelanguage.googleapis.com/v1beta/openai";
        public string geminiApiKey = "";
        
        public string claudeUrl = "https://api.anthropic.com/v1";
        public string claudeApiKey = "";
        
        public string customUrl = "";
        public string customApiKey = "";
        public string pollinationsUrl = "https://image.pollinations.ai/prompt";
        public string elevenLabsUrl = "https://api.elevenlabs.io";
        public string elevenLabsApiKey = "";
        public string voiceboxUrl = "http://127.0.0.1:23432";
        public string voiceboxApiKey = "";
        // --- Models ---
        public string modelLocal = "local-model";
        public string modelJan = "jan-model";
        public string modelOpenAi = "gpt-5-chat-latest";
        public string modelGemini = "gemini-flash-lite-latest";
        public string modelClaude = "claude-opus-4-6";
        public string modelCustom = "";
        public string modelPollinations = "flux";
        public string modelElevenLabs = "";
        public string modelVoicebox = "kokoro";

        // --- Capabilities ---
        public LlmCapabilities capsLocal = LlmCapabilities.Text;
        public LlmCapabilities capsJan = LlmCapabilities.Text | LlmCapabilities.Vision | LlmCapabilities.Audio;
        public LlmCapabilities capsOpenAi = LlmCapabilities.Text | LlmCapabilities.Image | LlmCapabilities.Vision | LlmCapabilities.Audio;
        public LlmCapabilities capsGemini = LlmCapabilities.Text | LlmCapabilities.Vision | LlmCapabilities.Audio;
        public LlmCapabilities capsClaude = LlmCapabilities.Text | LlmCapabilities.Vision;
        public LlmCapabilities capsCustom = LlmCapabilities.Text;
        public LlmCapabilities capsElevenLabs = LlmCapabilities.Audio;
        public LlmCapabilities capsVoicebox = LlmCapabilities.Audio;

        // --- Query Routing Ledger ---
        [System.Obsolete("Use queryRoutingIds instead.")]
        public System.Collections.Generic.Dictionary<string, ProviderRouting> queryRouting = new System.Collections.Generic.Dictionary<string, ProviderRouting>();
        
        public System.Collections.Generic.Dictionary<string, string> queryRoutingIds = new System.Collections.Generic.Dictionary<string, string>();
        public System.Collections.Generic.Dictionary<string, string> queryRoutingModels = new System.Collections.Generic.Dictionary<string, string>();

        public string defaultRoutingText = RoutingId.LocalOnly;
        public string defaultRoutingImage = RoutingId.LocalOnly;
        public string defaultRoutingVision = RoutingId.LocalOnly;
        public string defaultRoutingAudio = RoutingId.LocalOnly;
        
        // --- Custom Providers List ---
        public System.Collections.Generic.List<CustomProviderSettings> customProviders = new System.Collections.Generic.List<CustomProviderSettings>();

        // --- Token Tracking ---
        public int tokensPromptLocal = 0;
        public int tokensCompletionLocal = 0;
        public int tokensPromptJan = 0;
        public int tokensCompletionJan = 0;
        public int tokensPromptOpenAi = 0;
        public int tokensCompletionOpenAi = 0;
        public int tokensPromptGemini = 0;
        public int tokensCompletionGemini = 0;
        public int tokensPromptClaude = 0;
        public int tokensCompletionClaude = 0;
        public int tokensPromptCustom = 0;
        public int tokensCompletionCustom = 0;

        /// <summary>
        /// True if the default API provider is cloud-based.
        /// </summary>
        public bool IsRemoteUrl => apiProvider == ApiProvider.Google_Gemini || 
                                   apiProvider == ApiProvider.OpenAI || 
                                   apiProvider == ApiProvider.Anthropic_Claude ||
                                   (!string.IsNullOrEmpty(lmStudioUrl) && 
                                    !lmStudioUrl.Contains("localhost") && 
                                    !lmStudioUrl.Contains("127.0.0.1") && 
                                    !lmStudioUrl.Contains("::1"));

        // --- Behavior ---
        public bool autoMapModel = true;
        public bool enableCheatingActions = false;
        public string selectedModel = "";
        public bool sanitizeResponse = true;
        public bool enableKeepAlive = true;
        public bool disableThinking = true;
        public bool disableSafetyContextStripping = false;
        public float audioBoost = 2.5f;

        // --- Context Embedding ---
        public bool enableContextEmbedding = false;
        public bool enableStorytellerTools = false;
        public int maxPacingContextTokens = 4096;
        public int modelContextLimit = 8192;
        public float shortTermMemoryHours = 48f;

        // --- Performance ---
        public int timeoutSeconds = 240;
        public int maxRequestsPerMinute = 30;
        public int maxConcurrentRequests = 2;

        // --- Opportunistic Tasks ---
        /// <summary>-1 = Auto, 0 = Aggressive, 1 = Balanced, 2 = Conservative</summary>
        public int opportunisticThrottleMode = -1;
        /// <summary>Max tasks to fire per idle check in Aggressive mode (1-5).</summary>
        public int opportunisticBurstSize = 3;

        // --- Logging & Troubleshooting ---
        public bool traceDebugMode = false;
        public bool enableTrainingMode = false;
        public bool fastTelemetryMode = false;
        public string trainingDataDirectory = "";
        public bool testIdeologyActive = true;
        public bool testRoyaltyActive = true;
        public bool testBiotechActive = true;
        public bool testAnomalyActive = true;

        public string GetTrainingDirectory()
        {
            if (string.IsNullOrEmpty(trainingDataDirectory))
            {
                return System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RimSynapse");
            }
            return trainingDataDirectory;
        }

        // --- Notifications ---
        /// <summary>Show VRAM status on game load (default: true). Disable in settings.</summary>
        public bool showVramAdvisory = true;

        /// <summary>Show Queue Monitor icon in the bottom-right toolbar (default: true).</summary>
        public bool showQueueMonitorIcon = true;
        public bool showGodModeIcon = true;

        // --- Queue Monitor Columns ---
        public bool qmShowPrio = true;
        public bool qmShowMod = true;
        public bool qmShowTarget = true;
        public bool qmShowTask = true;
        public bool qmShowAge = true;
        public bool qmShowQueued = true;
        public bool qmShowLatency = true;
        
        public bool qmShowStatus = false;
        public bool qmShowScore = false;
        public bool qmShowTimeout = false;
        public bool qmShowTokens = false;
        public bool qmShowPrompt = false;
        public bool qmShowResponse = false;
        public bool qmShowProvider = true;
        public bool qmShowModel = true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref apiProvider, "apiProvider", ApiProvider.Local_LMStudio);
            
            Scribe_Values.Look(ref lmStudioUrl, "lmStudioUrl", "http://127.0.0.1:1234");
            Scribe_Values.Look(ref lmStudioApiKey, "lmStudioApiKey", "");
            Scribe_Values.Look(ref janUrl, "janUrl", "http://127.0.0.1:1337/v1");
            Scribe_Values.Look(ref janApiKey, "janApiKey", "");
            Scribe_Values.Look(ref openAiUrl, "openAiUrl", "https://api.openai.com/v1");
            Scribe_Values.Look(ref openAiApiKey, "openAiApiKey", "");
            Scribe_Values.Look(ref geminiUrl, "geminiUrl", "https://generativelanguage.googleapis.com/v1beta/openai");
            Scribe_Values.Look(ref geminiApiKey, "geminiApiKey", "");
            Scribe_Values.Look(ref claudeUrl, "claudeUrl", "https://api.anthropic.com/v1");
            Scribe_Values.Look(ref claudeApiKey, "claudeApiKey", "");
            Scribe_Values.Look(ref customUrl, "customUrl", "");
            Scribe_Values.Look(ref customApiKey, "customApiKey", "");
            Scribe_Values.Look(ref elevenLabsUrl, "elevenLabsUrl", "https://api.elevenlabs.io");
            Scribe_Values.Look(ref elevenLabsApiKey, "elevenLabsApiKey", "");
            Scribe_Values.Look(ref voiceboxUrl, "voiceboxUrl", "http://127.0.0.1:23432");
            Scribe_Values.Look(ref voiceboxApiKey, "voiceboxApiKey", "");

            Scribe_Values.Look(ref modelLocal, "modelLocal", "local-model");
            Scribe_Values.Look(ref modelJan, "modelJan", "jan-model");
            Scribe_Values.Look(ref modelOpenAi, "modelOpenAi", "gpt-5-chat-latest");
            Scribe_Values.Look(ref modelGemini, "modelGemini", "gemini-flash-lite-latest");
            Scribe_Values.Look(ref modelClaude, "modelClaude", "claude-opus-4-6");
            Scribe_Values.Look(ref modelCustom, "modelCustom", "");
            Scribe_Values.Look(ref modelPollinations, "modelPollinations", "flux");
            Scribe_Values.Look(ref modelVoicebox, "modelVoicebox", "kokoro");

            Scribe_Collections.Look(ref queryRoutingIds, "queryRoutingIds", LookMode.Value, LookMode.Value);
            if (queryRoutingIds == null) queryRoutingIds = new System.Collections.Generic.Dictionary<string, string>();
            
            Scribe_Collections.Look(ref queryRoutingModels, "queryRoutingModels", LookMode.Value, LookMode.Value);
            if (queryRoutingModels == null) queryRoutingModels = new System.Collections.Generic.Dictionary<string, string>();
            
            Scribe_Collections.Look(ref customProviders, "customProviders", LookMode.Deep);
            if (customProviders == null) customProviders = new System.Collections.Generic.List<CustomProviderSettings>();

            Scribe_Values.Look(ref defaultRoutingText, "defaultRoutingText", RoutingId.LocalOnly);
            Scribe_Values.Look(ref defaultRoutingImage, "defaultRoutingImage", RoutingId.LocalOnly);
            Scribe_Values.Look(ref defaultRoutingVision, "defaultRoutingVision", RoutingId.LocalOnly);
            Scribe_Values.Look(ref defaultRoutingAudio, "defaultRoutingAudio", RoutingId.LocalOnly);

            Scribe_Values.Look(ref capsLocal, "capsLocal", LlmCapabilities.Text);
            Scribe_Values.Look(ref capsJan, "capsJan", LlmCapabilities.Text | LlmCapabilities.Vision | LlmCapabilities.Audio);
            Scribe_Values.Look(ref capsOpenAi, "capsOpenAi", LlmCapabilities.Text | LlmCapabilities.Image | LlmCapabilities.Vision | LlmCapabilities.Audio);
            Scribe_Values.Look(ref capsGemini, "capsGemini", LlmCapabilities.Text | LlmCapabilities.Vision | LlmCapabilities.Audio);
            Scribe_Values.Look(ref capsClaude, "capsClaude", LlmCapabilities.Text | LlmCapabilities.Vision);
            Scribe_Values.Look(ref capsCustom, "capsCustom", LlmCapabilities.Text);
            Scribe_Values.Look(ref capsVoicebox, "capsVoicebox", LlmCapabilities.Audio);

            Scribe_Values.Look(ref tokensPromptLocal, "tokensPromptLocal", 0);
            Scribe_Values.Look(ref tokensCompletionLocal, "tokensCompletionLocal", 0);
            Scribe_Values.Look(ref tokensPromptJan, "tokensPromptJan", 0);
            Scribe_Values.Look(ref tokensCompletionJan, "tokensCompletionJan", 0);
            Scribe_Values.Look(ref tokensPromptOpenAi, "tokensPromptOpenAi", 0);
            Scribe_Values.Look(ref tokensCompletionOpenAi, "tokensCompletionOpenAi", 0);
            Scribe_Values.Look(ref tokensPromptGemini, "tokensPromptGemini", 0);
            Scribe_Values.Look(ref tokensCompletionGemini, "tokensCompletionGemini", 0);
            Scribe_Values.Look(ref tokensPromptClaude, "tokensPromptClaude", 0);
            Scribe_Values.Look(ref tokensCompletionClaude, "tokensCompletionClaude", 0);
            Scribe_Values.Look(ref tokensPromptCustom, "tokensPromptCustom", 0);
            Scribe_Values.Look(ref tokensCompletionCustom, "tokensCompletionCustom", 0);
            Scribe_Values.Look(ref autoMapModel, "autoMapModel", true);
            Scribe_Values.Look(ref enableCheatingActions, "enableCheatingActions", false);
            Scribe_Values.Look(ref selectedModel, "selectedModel", "");
            Scribe_Values.Look(ref sanitizeResponse, "sanitizeResponse", true);
            Scribe_Values.Look(ref enableKeepAlive, "enableKeepAlive", true);
            Scribe_Values.Look(ref disableThinking, "disableThinking", true);
            Scribe_Values.Look(ref disableSafetyContextStripping, "disableSafetyContextStripping", false);
            Scribe_Values.Look(ref audioBoost, "audioBoost", 2.5f);
            Scribe_Values.Look(ref timeoutSeconds, "timeoutSeconds", 240);
            Scribe_Values.Look(ref maxRequestsPerMinute, "maxRequestsPerMinute", 30);
            Scribe_Values.Look(ref maxConcurrentRequests, "maxConcurrentRequests", 2);
            Scribe_Values.Look(ref opportunisticThrottleMode, "opportunisticThrottleMode", -1);
            Scribe_Values.Look(ref opportunisticBurstSize, "opportunisticBurstSize", 3);
            Scribe_Values.Look(ref enableContextEmbedding, "enableContextEmbedding", false);
            Scribe_Values.Look(ref enableStorytellerTools, "enableStorytellerTools", false);
            Scribe_Values.Look(ref maxPacingContextTokens, "maxPacingContextTokens", 4096);
            Scribe_Values.Look(ref modelContextLimit, "modelContextLimit", 8192);
            Scribe_Values.Look(ref shortTermMemoryHours, "shortTermMemoryHours", 48f);
            Scribe_Values.Look(ref traceDebugMode, "traceDebugMode", false);
            Scribe_Values.Look(ref enableTrainingMode, "enableTrainingMode", false);
            Scribe_Values.Look(ref fastTelemetryMode, "fastTelemetryMode", false);
            Scribe_Values.Look(ref trainingDataDirectory, "trainingDataDirectory", "");
            Scribe_Values.Look(ref testIdeologyActive, "testIdeologyActive", true);
            Scribe_Values.Look(ref testRoyaltyActive, "testRoyaltyActive", true);
            Scribe_Values.Look(ref testBiotechActive, "testBiotechActive", true);
            Scribe_Values.Look(ref testAnomalyActive, "testAnomalyActive", true);
            Scribe_Values.Look(ref showVramAdvisory, "showVramAdvisory", true);
            Scribe_Values.Look(ref showQueueMonitorIcon, "showQueueMonitorIcon", true);
            Scribe_Values.Look(ref showGodModeIcon, "showGodModeIcon", true);

            Scribe_Values.Look(ref qmShowPrio, "qmShowPrio", true);
            Scribe_Values.Look(ref qmShowMod, "qmShowMod", true);
            Scribe_Values.Look(ref qmShowTarget, "qmShowTarget", true);

            // Migration from old enum routing to string routing
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (queryRouting != null && queryRouting.Count > 0)
                {
                    foreach (var kvp in queryRouting)
                    {
                        if (!queryRoutingIds.ContainsKey(kvp.Key))
                        {
                            switch (kvp.Value)
                            {
                                case ProviderRouting.LocalOnly: queryRoutingIds[kvp.Key] = RoutingId.LocalOnly; break;
                                case ProviderRouting.Specific_OpenAI: queryRoutingIds[kvp.Key] = RoutingId.OpenAI; break;
                                case ProviderRouting.Specific_Gemini: queryRoutingIds[kvp.Key] = RoutingId.Gemini; break;
                                case ProviderRouting.Specific_Claude: queryRoutingIds[kvp.Key] = RoutingId.Claude; break;
                                case ProviderRouting.Specific_Custom: queryRoutingIds[kvp.Key] = RoutingId.CustomPrefix + "0"; break;
                                default: queryRoutingIds[kvp.Key] = RoutingId.LocalOnly; break;
                            }
                        }
                    }
                    queryRouting.Clear();
                }

                // Migrate old custom settings into the new CustomProviders list
                if (customProviders.Count == 0 && !string.IsNullOrEmpty(customUrl))
                {
                    customProviders.Add(new CustomProviderSettings
                    {
                        id = "0",
                        name = "Legacy Custom/Proxy",
                        url = customUrl,
                        apiKey = customApiKey,
                        caps = capsCustom
                    });
                }
            }

            Scribe_Values.Look(ref qmShowTask, "qmShowTask", true);
            Scribe_Values.Look(ref qmShowAge, "qmShowAge", true);
            
            // Backward compatibility for timing columns
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                bool oldAge = true;
                Scribe_Values.Look(ref oldAge, "qmShowAge", true);
                qmShowQueued = oldAge;
                qmShowLatency = oldAge;
            }
            
            Scribe_Values.Look(ref qmShowQueued, "qmShowQueued", true);
            Scribe_Values.Look(ref qmShowLatency, "qmShowLatency", true);
            
            Scribe_Values.Look(ref qmShowStatus, "qmShowStatus", false);
            Scribe_Values.Look(ref qmShowScore, "qmShowScore", false);
            Scribe_Values.Look(ref qmShowTimeout, "qmShowTimeout", false);
            Scribe_Values.Look(ref qmShowTokens, "qmShowTokens", false);
            Scribe_Values.Look(ref qmShowPrompt, "qmShowPrompt", false);
            Scribe_Values.Look(ref qmShowResponse, "qmShowResponse", false);
            Scribe_Values.Look(ref qmShowProvider, "qmShowProvider", true);
            Scribe_Values.Look(ref qmShowModel, "qmShowModel", true);
        }
    }
}
