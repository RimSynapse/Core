using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RimSynapse.Internal.Providers
{
    public class OpenAiProvider : ILlmProvider
    {
        private readonly HttpClient _client;
        private static bool _globalOmitResponseFormat = false;

        public OpenAiProvider(HttpClient client)
        {
            _client = client;
        }

        public ChatResult SendTextRequestSync(LlmTextRequest request, string baseUrl, string apiKey, string model)
        {
            try
            {
                baseUrl = baseUrl.TrimEnd('/');
                string url;
                if (baseUrl.EndsWith("/v1") || baseUrl.EndsWith("/v1beta/openai") || baseUrl.EndsWith("/v1/messages"))
                    url = $"{baseUrl.Replace("/v1/messages", "/v1")}/chat/completions";
                else
                    url = $"{baseUrl}/v1/chat/completions";

                var messagesArray = new JArray();
                if (!string.IsNullOrEmpty(request.SystemPrompt))
                {
                    messagesArray.Add(new JObject { ["role"] = "system", ["content"] = request.SystemPrompt });
                }
                foreach (var msg in request.Messages)
                {
                    var msgObj = new JObject { ["role"] = msg.role };
                    if (msg.content != null)
                    {
                        msgObj["content"] = msg.content;
                    }
                    if (msg.role == "tool" && !string.IsNullOrEmpty(msg.tool_call_id))
                    {
                        msgObj["tool_call_id"] = msg.tool_call_id;
                    }
                    if (msg.role == "tool" && !string.IsNullOrEmpty(msg.name))
                    {
                        msgObj["name"] = msg.name;
                    }
                    if (msg.tool_calls != null && msg.tool_calls.Count > 0)
                    {
                        var tcArray = new JArray();
                        foreach (var tc in msg.tool_calls)
                        {
                            tcArray.Add(new JObject
                            {
                                ["id"] = tc.id,
                                ["type"] = tc.type,
                                ["function"] = new JObject
                                {
                                    ["name"] = tc.function.name,
                                    ["arguments"] = tc.function.arguments
                                }
                            });
                        }
                        msgObj["tool_calls"] = tcArray;
                    }
                    messagesArray.Add(msgObj);
                }

                bool omitResponseFormat = _globalOmitResponseFormat;
                int maxRetries = 2;
                int currentAttempt = 0;
                
                while (currentAttempt < maxRetries)
                {
                    currentAttempt++;

                    var body = new JObject
                    {
                        ["model"] = model,
                        ["messages"] = messagesArray
                    };

                    if (request.MaxTokens.HasValue) body["max_tokens"] = request.MaxTokens.Value;
                    if (request.Temperature.HasValue) body["temperature"] = request.Temperature.Value;
                    
                    if (request.DisableThinking)
                    {
                        // Standard parameters for disabling thinking across newer engines
                        body["thinking"] = false; // vLLM / Llama.cpp
                        body["think"] = false;    // Ollama
                        
                        // Instruct standard prompt templates to skip thinking via Jinja chat template arguments
                        var chatTemplateKwargs = new JObject
                        {
                            ["enable_thinking"] = false
                        };
                        body["chat_template_kwargs"] = chatTemplateKwargs;
                    }

                    if (request.EnforceJson && !omitResponseFormat)
                    {
                        body["response_format"] = new JObject { ["type"] = "json_object" };
                    }

                    if (request.Tools != null && request.Tools.Count > 0)
                    {
                        var toolsArray = new JArray();
                        foreach (var tool in request.Tools)
                        {
                            toolsArray.Add(new JObject
                            {
                                ["type"] = "function",
                                ["function"] = new JObject
                                {
                                    ["name"] = tool.name,
                                    ["description"] = tool.description,
                                    ["parameters"] = JToken.FromObject(tool.parameters)
                                }
                            });
                        }
                        body["tools"] = toolsArray;
                    }

                    string jsonBody = body.ToString(Formatting.None);
                    SynapseLogger.TraceContext(jsonBody, url);

                    var req = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
                    };

                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                    }

                    long startMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    int timeoutSec = RimSynapseMod.Instance?.Settings != null ? RimSynapseMod.Instance.Settings.timeoutSeconds : 120;
                    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
                    
                    var response = _client.SendAsync(req, cts.Token).Result;
                    string responseBody = response.Content.ReadAsStringAsync().Result;

                    if (!response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && responseBody.Contains("response_format") && !omitResponseFormat)
                        {
                            _globalOmitResponseFormat = true;
                            omitResponseFormat = true;
                            SynapseLogger.Warning("Endpoint rejected 'json_object' response_format. Retrying without it and disabling it for future requests...");
                            continue;
                        }

                        long dur = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startMs;
                        var error = $"OpenAI-compatible endpoint returned {(int)response.StatusCode}: {responseBody}";
                        SynapseLogger.Error(error);
                        return ChatResult.Failure(error, dur);
                    }
                    
                    // Break out of the retry loop on success
                    return ProcessSuccessfulResponse(responseBody, model, startMs);
                }
                
                return ChatResult.Failure("Exceeded max retries.");
            }
            catch (Exception ex)
            {
                string error = ex.InnerException?.Message ?? ex.Message;
                SynapseLogger.Error($"OpenAI Request failed: {error}");
                return ChatResult.Failure(error);
            }
        }
        
        private ChatResult ProcessSuccessfulResponse(string responseBody, string model, long startMs)
        {
            var result = JObject.Parse(responseBody);
            string content = null;
            int promptTokens = 0;
            int completionTokens = 0;
            JToken toolCallsToken = null;

            var choices = result["choices"] as JArray;
            if (choices != null && choices.Count > 0)
            {
                var message = choices[0]?["message"];
                content = message?["content"]?.ToString();
                toolCallsToken = message?["tool_calls"];
            }

            var usage = result["usage"];
            if (usage != null)
            {
                promptTokens = usage["prompt_tokens"]?.Value<int>() ?? 0;
                completionTokens = usage["completion_tokens"]?.Value<int>() ?? 0;
            }

            long durationMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startMs;
            var chatRes = ChatResult.Success(content, model, promptTokens, completionTokens, durationMs);
            if (toolCallsToken != null)
            {
                chatRes.toolCalls = toolCallsToken.ToObject<System.Collections.Generic.List<ChatToolCall>>();
            }
            return chatRes;
        }

        public ChatResult SendVisionRequestSync(LlmVisionRequest request, string baseUrl, string apiKey, string model)
        {
            // Similar to text, but with vision payload formatting.
            return ChatResult.Failure("Vision not fully implemented in OpenAiProvider yet.");
        }

        public ImageResult SendImageRequestSync(LlmImageRequest request, string baseUrl, string apiKey, string model)
        {
            return ImageResult.Failure("Image generation not fully implemented in OpenAiProvider yet.");
        }

        public AudioResult SendAudioRequestSync(LlmAudioRequest request, string baseUrl, string apiKey, string model)
        {
            try
            {
                baseUrl = baseUrl.TrimEnd('/');
                string url;
                if (baseUrl.EndsWith("/v1") || baseUrl.EndsWith("/v1beta/openai") || baseUrl.EndsWith("/v1/messages"))
                    url = $"{baseUrl.Replace("/v1/messages", "/v1")}/audio/speech";
                else
                    url = $"{baseUrl}/v1/audio/speech";

                var body = new JObject
                {
                    ["model"] = model,
                    ["input"] = request.InputText,
                    ["voice"] = string.IsNullOrEmpty(request.Voice) ? "alloy" : request.Voice,
                    ["response_format"] = string.IsNullOrEmpty(request.ResponseFormat) ? "pcm" : request.ResponseFormat
                };

                string jsonBody = body.ToString(Formatting.None);
                SynapseLogger.TraceContext(jsonBody, url);

                var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
                };

                if (!string.IsNullOrEmpty(apiKey))
                {
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                }

                long startMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                int timeoutSec = RimSynapseMod.Instance?.Settings != null ? RimSynapseMod.Instance.Settings.timeoutSeconds : 120;
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
                
                var response = _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).Result;
                
                if (!response.IsSuccessStatusCode)
                {
                    long dur = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startMs;
                    string responseBody = response.Content.ReadAsStringAsync().Result;
                    var error = $"OpenAI-compatible audio endpoint returned {(int)response.StatusCode}: {responseBody}";
                    SynapseLogger.Error(error);
                    return AudioResult.Failure(error, dur);
                }

                // Many local backends erroneously return 200 OK with a JSON error when the endpoint isn't supported.
                string mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType != null && (mediaType.Contains("json") || mediaType.Contains("text")))
                {
                    long dur = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startMs;
                    string responseBody = response.Content.ReadAsStringAsync().Result;
                    var error = $"Endpoint returned JSON/Text instead of audio bytes: {responseBody}";
                    SynapseLogger.Error(error);
                    return AudioResult.Failure(error, dur);
                }

                byte[] audioBytes;
                using (var stream = response.Content.ReadAsStreamAsync().Result)
                using (var ms = new System.IO.MemoryStream())
                {
                    stream.CopyTo(ms);
                    audioBytes = ms.ToArray();
                }
                
                string base64Audio = Convert.ToBase64String(audioBytes);
                
                long durationMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startMs;
                return AudioResult.Success(base64Audio, model, durationMs);
            }
            catch (Exception ex)
            {
                string error = ex.InnerException?.Message ?? ex.Message;
                SynapseLogger.Error($"OpenAI Audio Request failed: {error}");
                return AudioResult.Failure(error);
            }
        }
    }
}
