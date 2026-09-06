using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RimSynapse.Internal
{
    /// <summary>
    /// Low-level HTTP client wrapper for LM Studio communication.
    /// All calls run on background threads — never blocks Unity's main thread.
    /// </summary>
    internal static partial class HttpEngine
    {
        private static HttpClient _client;
        private static readonly object _initLock = new object();

        /// <summary>
        /// Ensure the HttpClient is initialized with current settings.
        /// </summary>
        internal static void EnsureInitialized()
        {
            if (_client != null) return;

            lock (_initLock)
            {
                if (_client != null) return;

                var handler = new HttpClientHandler
                {
                    // Accept self-signed certs for local development
                    ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) => true,
                };

                _client = new HttpClient(handler)
                {
                    Timeout = Timeout.InfiniteTimeSpan
                };
                _client.DefaultRequestHeaders.Add("Accept", "application/json");

                SynapseLogger.Message("HttpClient initialized.");
            }
        }

        /// <summary>
        /// Dispose the HttpClient on shutdown.
        /// </summary>
        internal static void Shutdown()
        {
            _client?.Dispose();
            _client = null;
        }

        /// <summary>
        /// POST a chat completion request to LM Studio.
        /// SYNCHRONOUS — intended to be called from the queue worker thread.
        /// Returns the result directly (never dispatches to main thread).
        /// </summary>
        internal static object RouteRequestSync(
            SynapseModHandle mod,
            object payload,
            LlmCapabilities capability,
            ChatOptions options)
        {
            EnsureInitialized();
            var settings = RimSynapseMod.Instance?.Settings;
            if (settings == null)
            {
                return ChatResult.Failure("RimSynapse settings not loaded.");
            }

            long startMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            try
            {
                string routingId = RoutingId.LocalOnly;
                string queryKey = $"{mod?.ModId}:{options?.queryId}";
                
                if (!string.IsNullOrEmpty(options?.providerOverride))
                {
                    routingId = options.providerOverride;
                }
                else
                {
                    if ((capability & LlmCapabilities.Image) == LlmCapabilities.Image) routingId = settings.defaultRoutingImage;
                    else if ((capability & LlmCapabilities.Vision) == LlmCapabilities.Vision) routingId = settings.defaultRoutingVision;
                    else if ((capability & LlmCapabilities.Audio) == LlmCapabilities.Audio) routingId = settings.defaultRoutingAudio;
                    else routingId = settings.defaultRoutingText;
                }

                string baseUrl = settings.lmStudioUrl;
                string apiKey = settings.lmStudioApiKey;
                ApiProvider providerHit = ApiProvider.Local_LMStudio;
                string defaultProviderModel = settings.modelLocal;

                if (routingId == RoutingId.OpenAI)
                {
                    baseUrl = settings.openAiUrl;
                    apiKey = settings.openAiApiKey;
                    providerHit = ApiProvider.OpenAI;
                    defaultProviderModel = settings.modelOpenAi;
                }
                else if (routingId == RoutingId.Jan)
                {
                    baseUrl = settings.janUrl;
                    apiKey = settings.janApiKey;
                    providerHit = ApiProvider.Local_Jan;
                    defaultProviderModel = settings.modelJan;
                }
                else if (routingId == RoutingId.Gemini)
                {
                    baseUrl = settings.geminiUrl;
                    apiKey = settings.geminiApiKey;
                    providerHit = ApiProvider.Google_Gemini;
                    defaultProviderModel = settings.modelGemini;
                }
                else if (routingId == RoutingId.Claude)
                {
                    baseUrl = settings.claudeUrl;
                    apiKey = settings.claudeApiKey;
                    providerHit = ApiProvider.Anthropic_Claude;
                    defaultProviderModel = settings.modelClaude;
                }
                else if (routingId == RoutingId.Pollinations)
                {
                    baseUrl = settings.pollinationsUrl;
                    apiKey = "";
                    providerHit = ApiProvider.Pollinations;
                    defaultProviderModel = settings.modelPollinations;
                }
                else if (routingId == RoutingId.ElevenLabs)
                {
                    baseUrl = settings.elevenLabsUrl;
                    apiKey = settings.elevenLabsApiKey;
                    providerHit = ApiProvider.ElevenLabs;
                    defaultProviderModel = settings.modelElevenLabs;
                }
                else if (routingId == RoutingId.Voicebox)
                {
                    baseUrl = settings.voiceboxUrl;
                    apiKey = settings.voiceboxApiKey;
                    providerHit = ApiProvider.Voicebox;
                    defaultProviderModel = settings.modelVoicebox;
                }
                else if (routingId != null && routingId.StartsWith(RoutingId.CustomPrefix))
                {
                    string customId = routingId.Substring(RoutingId.CustomPrefix.Length);
                    var custom = settings.customProviders.Find(c => c.id == customId);
                    if (custom != null)
                    {
                        baseUrl = custom.url;
                        apiKey = custom.apiKey;
                        providerHit = ApiProvider.Custom;
                        defaultProviderModel = custom.model;
                    }
                }

                // Resolve model override hierarchy
                string targetModel = options?.model;
                if (string.IsNullOrEmpty(targetModel))
                {
                    // 1. Capability default override
                    if (string.IsNullOrEmpty(targetModel))
                    {
                        string capKey = "default_text";
                        if ((capability & LlmCapabilities.Image) == LlmCapabilities.Image) capKey = "default_image";
                        else if ((capability & LlmCapabilities.Vision) == LlmCapabilities.Vision) capKey = "default_vision";
                        else if ((capability & LlmCapabilities.Audio) == LlmCapabilities.Audio) capKey = "default_audio";
                        
                        if (settings.queryRoutingModels.TryGetValue(capKey, out var capModel) && !string.IsNullOrEmpty(capModel))
                        {
                            targetModel = capModel;
                        }
                    }
                    // 2. Fallback to provider default / active model
                    if (string.IsNullOrEmpty(targetModel))
                    {
                        if (providerHit == ApiProvider.Local_LMStudio) targetModel = ModelManager.ResolveModel(options?.model);
                        else if (providerHit == ApiProvider.Local_Jan) targetModel = string.IsNullOrEmpty(options?.model) ? settings.modelJan : options.model;
                        else targetModel = defaultProviderModel;
                    }
                }

                // Instantiate specific provider
                Providers.ILlmProvider provider;
                if (providerHit == ApiProvider.Anthropic_Claude) provider = new Providers.AnthropicProvider(_client);
                else if (providerHit == ApiProvider.Google_Gemini) provider = new Providers.GeminiProvider(_client);
                else if (providerHit == ApiProvider.Pollinations) provider = new Providers.PollinationsProvider(_client);
                else if (providerHit == ApiProvider.ElevenLabs) provider = new Providers.ElevenLabsProvider(_client);
                else if (providerHit == ApiProvider.Voicebox) provider = new Providers.VoiceboxProvider(_client);
                else provider = new Providers.OpenAiProvider(_client);

                // Route to the appropriate interface method based on Payload Type
                if (payload is LlmTextRequest textReq)
                {
                    if (capability == LlmCapabilities.Vision)
                    {
                        // Some legacy compatibility fallback if they passed TextRequest but marked as Vision.
                        return provider.SendTextRequestSync(textReq, baseUrl, apiKey, targetModel);
                    }
                    var chatResult = provider.SendTextRequestSync(textReq, baseUrl, apiKey, targetModel);
                    
                    // Sanitize if enabled
                    if (chatResult.success && options?.sanitize != false && settings.sanitizeResponse)
                    {
                        chatResult.content = Sanitizer.Clean(chatResult.content);
                    }
                    
                    // Record usage
                    if (chatResult.success)
                    {
                        if (providerHit == ApiProvider.Local_LMStudio) { settings.tokensPromptLocal += chatResult.promptTokens; settings.tokensCompletionLocal += chatResult.completionTokens; }
                        else if (providerHit == ApiProvider.Local_Jan) { settings.tokensPromptJan += chatResult.promptTokens; settings.tokensCompletionJan += chatResult.completionTokens; }
                        else if (providerHit == ApiProvider.OpenAI) { settings.tokensPromptOpenAi += chatResult.promptTokens; settings.tokensCompletionOpenAi += chatResult.completionTokens; }
                        else if (providerHit == ApiProvider.Google_Gemini) { settings.tokensPromptGemini += chatResult.promptTokens; settings.tokensCompletionGemini += chatResult.completionTokens; }
                        else if (providerHit == ApiProvider.Anthropic_Claude) { settings.tokensPromptClaude += chatResult.promptTokens; settings.tokensCompletionClaude += chatResult.completionTokens; }
                        else if (providerHit == ApiProvider.Custom) { settings.tokensPromptCustom += chatResult.promptTokens; settings.tokensCompletionCustom += chatResult.completionTokens; }
                    }

                    // Scoped session/save metrics — TOPS, per-model throughput, max call size (#127).
                    SynapseCallMetrics.Record(providerHit, baseUrl, chatResult);

                    return chatResult;
                }
                else if (payload is LlmVisionRequest visionReq)
                {
                    return provider.SendVisionRequestSync(visionReq, baseUrl, apiKey, targetModel);
                }
                else if (payload is LlmImageRequest imgReq)
                {
                    return provider.SendImageRequestSync(imgReq, baseUrl, apiKey, targetModel);
                }
                else if (payload is LlmAudioRequest audReq)
                {
                    return provider.SendAudioRequestSync(audReq, baseUrl, apiKey, targetModel);
                }
                else
                {
                    return ChatResult.Failure("Unknown payload type submitted to HttpEngine.");
                }
            }
            catch (Exception ex)
            {
                long dur = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startMs;
                string error = ex.InnerException?.Message ?? ex.Message;
                SynapseLogger.Error($"RouteRequestSync Exception: {error}");
                
                if ((capability & LlmCapabilities.Image) == LlmCapabilities.Image) return ImageResult.Failure(error, dur);
                if ((capability & LlmCapabilities.Audio) == LlmCapabilities.Audio) return AudioResult.Failure(error, dur);
                return ChatResult.Failure(error, dur);
            }
        }

        /// <summary>
        /// GET the loaded models list from LM Studio.
        /// SYNCHRONOUS — caller handles threading.
        /// </summary>
        internal static ModelsResult GetModelsSync()
        {
            EnsureInitialized();
            var settings = RimSynapseMod.Instance?.Settings;
            if (settings == null)
            {
                return new ModelsResult { online = false, error = "Settings not loaded." };
            }

            try
            {
                string baseUrl = settings.lmStudioUrl;
                string apiKey = settings.lmStudioApiKey;

                baseUrl = baseUrl.TrimEnd('/');
                string url;
                if (baseUrl.EndsWith("/v1") || baseUrl.EndsWith("/v1beta/openai") || baseUrl.EndsWith("/v1/messages"))
                {
                    url = $"{baseUrl}/models";
                }
                else
                {
                    url = $"{baseUrl}/v1/models";
                }

                var result = new ModelsResult();

                // OpenAI-compatible endpoint
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(apiKey))
                {
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue(
                            "Bearer", apiKey);
                }

                var response = _client.SendAsync(request).Result;
                if (!response.IsSuccessStatusCode)
                {
                    result.online = false;
                    result.error = $"HTTP {(int)response.StatusCode}";
                    return result;
                }

                string body = response.Content.ReadAsStringAsync().Result;
                var json = JObject.Parse(body);
                var data = json["data"] as JArray;

                result.online = true;
                if (data != null)
                {
                    foreach (var m in data)
                    {
                        string id = m["id"]?.ToString();
                        if (!string.IsNullOrEmpty(id))
                            result.modelIds.Add(id);

                        // Try to extract active model's context window size from any typical LM Studio / OpenAI extensions
                        if (result.contextLength == null)
                        {
                            try
                            {
                                if (m["context_length"] != null)
                                    result.contextLength = m["context_length"].Value<int>();
                                else if (m["max_context_length"] != null)
                                    result.contextLength = m["max_context_length"].Value<int>();
                                else if (m["context_window"] != null)
                                    result.contextLength = m["context_window"].Value<int>();
                                else if (m["meta"]?["context_length"] != null)
                                    result.contextLength = m["meta"]["context_length"].Value<int>();
                                else if (m["meta"]?["context_window"] != null)
                                    result.contextLength = m["meta"]["context_window"].Value<int>();
                                else if (m["properties"]?["context_length"] != null)
                                    result.contextLength = m["properties"]["context_length"].Value<int>();
                            }
                            catch { }
                        }
                    }
                }

                // Dummy request removed: /v1/models now correctly returns all loaded models in LM Studio.

                return result;
            }
            catch (Exception ex)
            {
                string error = ex.InnerException?.Message ?? ex.Message;
                return new ModelsResult { online = false, error = error };
            }
        }

        /// <summary>
        /// Send a minimal keep-alive ping to prevent model unloading.
        /// Fire-and-forget, errors silently ignored.
        /// </summary>
        internal static void SendKeepAlivePing(string model)
        {
            EnsureInitialized();
            var settings = RimSynapseMod.Instance?.Settings;
            if (settings == null) return;

            Task.Run(() =>
            {
                try
                {
                    string baseUrl = settings.lmStudioUrl.TrimEnd('/');
                    string url = $"{baseUrl}/v1/chat/completions";

                    var body = new JObject
                    {
                        ["model"] = model,
                        ["messages"] = new JArray
                        {
                            new JObject
                            {
                                ["role"] = "user",
                                ["content"] = "keep-alive ping",
                            }
                        },
                        ["max_tokens"] = 1,
                    };

                    var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(
                            body.ToString(Formatting.None), Encoding.UTF8, "application/json"),
                    };

                    if (!string.IsNullOrEmpty(settings.lmStudioApiKey))
                    {
                        request.Headers.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue(
                                "Bearer", settings.lmStudioApiKey);
                    }

                    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    _client.SendAsync(request, cts.Token).Wait(cts.Token);

                    SynapseLogger.Message($"Keep-alive ping sent to \"{model}\".");
                }
                catch
                {
                    // Best-effort — silently ignore
                }
            });
        }
    }
}

