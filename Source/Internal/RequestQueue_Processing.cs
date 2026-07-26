using System;
using System.Collections.Generic;
using System.Threading;

namespace RimSynapse.Internal
{
    /// <summary>
    /// Request processing logic: routing, context injection, tool call loops,
    /// telemetry logging, callback dispatch, and model error handling.
    /// </summary>
    public static partial class RequestQueue
    {
        private static void ProcessRequest(QueuedRequest requestToProcess, int currentSession)
        {
            try
            {
                SynapseLogger.Info("queue",
                    $"Processing request for {requestToProcess.Mod?.DisplayName ?? "unknown"}. Score: {requestToProcess.CurrentScore:F0}",
                    requestToProcess.Mod?.ModId);

                ResolveRouting(requestToProcess);
                InjectContext(requestToProcess);
                LogPromptIfTrace(requestToProcess);

                // Synchronous HTTP call inside thread pool
                var resultObj = HttpEngine.RouteRequestSync(
                    requestToProcess.Mod, requestToProcess.Payload, requestToProcess.CapabilityType, requestToProcess.Options);

                // Handle tool call loop
                resultObj = HandleToolCallLoop(requestToProcess, resultObj);

                if (SessionId != currentSession)
                {
                    SynapseLogger.Message($"Session changed during request. Discarding response for {requestToProcess.Mod?.DisplayName}.");
                    return;
                }

                ModRegistry.RecordRequest(requestToProcess.Mod);
                DispatchResult(requestToProcess, resultObj);
            }
            catch (Exception ex)
            {
                requestToProcess.CompletedAt = DateTime.UtcNow;
                if (requestToProcess.DispatchedAt.HasValue)
                {
                    requestToProcess.LlmLatencyMs = (long)(DateTime.UtcNow - requestToProcess.DispatchedAt.Value).TotalMilliseconds;
                }
                SynapseLogger.Error($"Queue worker error: {ex.Message}");
                if (SessionId == currentSession)
                {
                    var cb = requestToProcess.Callback;
                    SynapseGameComponent.Enqueue(() =>
                        cb?.DynamicInvoke(ChatResult.Failure($"Queue error: {ex.Message}")));
                }
            }
        }

        private static void ResolveRouting(QueuedRequest req)
        {
            string routingId = RoutingId.LocalOnly;
            string queryKey = $"{req.Mod?.ModId}:{req.Options?.queryId}";
            var settings = RimSynapseMod.Instance?.Settings;

            if (settings != null && req.Mod != null && !string.IsNullOrEmpty(req.Options?.queryId) && settings.queryRoutingIds.TryGetValue(queryKey, out var savedRouting))
            {
                routingId = savedRouting;
            }
            else if (settings != null)
            {
                LlmCapabilities reqCaps = LlmCapabilities.Text;
                if (req.Mod != null && !string.IsNullOrEmpty(req.Options?.queryId) && req.Mod.RegisteredQueries.TryGetValue(req.Options.queryId, out var queryDef))
                {
                    reqCaps = queryDef.requiredCaps;
                }
                if ((reqCaps & LlmCapabilities.Image) == LlmCapabilities.Image) routingId = settings.defaultRoutingImage;
                else if ((reqCaps & LlmCapabilities.Vision) == LlmCapabilities.Vision) routingId = settings.defaultRoutingVision;
                else if ((reqCaps & LlmCapabilities.Audio) == LlmCapabilities.Audio) routingId = settings.defaultRoutingAudio;
                else routingId = settings.defaultRoutingText;
            }

            if (routingId == RoutingId.LocalOnly)
            {
                string model = ModelManager.ResolveModel(req.Options?.model);
                if (!string.IsNullOrEmpty(model) && req.Options != null)
                {
                    req.Options.model = model;
                }
            }
        }

        private static void InjectContext(QueuedRequest req)
        {
            if (!SynapseCoreContext.IsEnabled() || string.IsNullOrEmpty(req.Options?.eventType))
                return;

            try
            {
                var contextText = SynapseCoreContext.GetContextText(
                    req.Options.eventType,
                    req.Options.sourcePawn,
                    req.Options.targetPawn,
                    req.Options.contextTiers ?? req.Mod?.DefaultTiers,
                    req.Options.weightOverrides);

                if (string.IsNullOrEmpty(contextText)) return;

                string systemPrompt = req.Mod?.SystemPrompt
                    ?? SynapseCoreContext.ResolvePrompt(
                        req.Options.eventType,
                        req.Mod?.ModId);

                string fullSystemMessage = string.IsNullOrEmpty(systemPrompt)
                    ? contextText
                    : $"{systemPrompt}\n---\n{contextText}";

                if (req.Payload is LlmTextRequest txtReq)
                {
                    var sysMsg = txtReq.Messages.Find(m => m.role == "system");
                    if (sysMsg != null)
                    {
                        sysMsg.content = fullSystemMessage;
                    }
                    else
                    {
                        txtReq.Messages.Insert(0, new ChatMessage
                        {
                            role = "system",
                            content = fullSystemMessage,
                        });
                    }
                }
            }
            catch (Exception ctxEx)
            {
                SynapseLogger.Warning($"Context assembly failed: {ctxEx.Message}");
            }
        }

        private static void LogPromptIfTrace(QueuedRequest req)
        {
            if (RimSynapseMod.Instance?.Settings?.traceDebugMode != true) return;

            var promptLog = new System.Text.StringBuilder();
            promptLog.AppendLine($"── PROMPT → {req.Mod?.DisplayName ?? "unknown"} ──");
            
            if (req.Payload is LlmTextRequest txtReq)
            {
                foreach (var msg in txtReq.Messages)
                {
                    promptLog.AppendLine($"[{msg.role}]: {msg.content}");
                }
            }
            else if (req.Payload is LlmVisionRequest visReq)
            {
                foreach (var msg in visReq.Messages)
                {
                    promptLog.AppendLine($"[{msg.role}]: {msg.content}");
                }
            }
            else
            {
                promptLog.AppendLine($"Payload: {req.Payload.GetType().Name}");
            }
            
            SynapseLogger.Message(promptLog.ToString(), req.Mod?.ModId);
        }

        private static object HandleToolCallLoop(QueuedRequest req, object resultObj)
        {
            if (!(resultObj is ChatResult initialChatResult) || !initialChatResult.success || initialChatResult.toolCalls == null || initialChatResult.toolCalls.Count == 0)
                return resultObj;

            int maxLoops = 3;
            var textReq = req.Payload as LlmTextRequest;

            while (initialChatResult.success && initialChatResult.toolCalls != null && initialChatResult.toolCalls.Count > 0 && maxLoops > 0 && textReq != null)
            {
                maxLoops--;
                SynapseLogger.Message($"[RimSynapse-Core] Assistant requested {initialChatResult.toolCalls.Count} tool calls. Executing on main thread...", req.Mod?.ModId);

                var assistantMsg = new ChatMessage("assistant", initialChatResult.content)
                {
                    tool_calls = initialChatResult.toolCalls
                };
                textReq.Messages.Add(assistantMsg);

                foreach (var tc in initialChatResult.toolCalls)
                {
                    string toolOutput = ExecuteToolOnMainThread(tc.function.name, tc.function.arguments);
                    var toolResponseMsg = ChatMessage.Tool(toolOutput, tc.id);
                    toolResponseMsg.name = tc.function.name;
                    textReq.Messages.Add(toolResponseMsg);
                    SynapseLogger.Message($"[RimSynapse-Core] Tool '{tc.function.name}' response: {toolOutput}", req.Mod?.ModId);
                }

                long startFollowUp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var followUpResult = HttpEngine.RouteRequestSync(
                    req.Mod, textReq, req.CapabilityType, req.Options);

                if (followUpResult is ChatResult followUpChatResult)
                {
                    initialChatResult.promptTokens += followUpChatResult.promptTokens;
                    initialChatResult.completionTokens += followUpChatResult.completionTokens;
                    initialChatResult.durationMs += (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startFollowUp);
                    initialChatResult.content = followUpChatResult.content;
                    initialChatResult.toolCalls = followUpChatResult.toolCalls;
                    initialChatResult.success = followUpChatResult.success;
                    initialChatResult.error = followUpChatResult.error;
                }
                else
                {
                    initialChatResult.success = false;
                    initialChatResult.error = "Follow-up request failed or did not return text.";
                    initialChatResult.toolCalls = null;
                }
            }

            return resultObj;
        }

        private static void DispatchResult(QueuedRequest req, object resultObj)
        {
            var settings = RimSynapseMod.Instance?.Settings;
            bool success = false;
            long durationMs = 0;
            string errorMsg = "";
            string contentPreview = "";

            if (resultObj is ChatResult chatResult)
            {
                chatResult.wasThrottled = false;
                success = chatResult.success;
                durationMs = chatResult.durationMs;
                errorMsg = chatResult.error;
                contentPreview = chatResult.content;
                req.Result = chatResult;
            }
            else if (resultObj is ImageResult imgResult)
            {
                success = imgResult.success;
                durationMs = imgResult.durationMs;
                errorMsg = imgResult.error;
                contentPreview = "[Image Base64]";
            }
            else if (resultObj is AudioResult audResult)
            {
                success = audResult.success;
                durationMs = audResult.durationMs;
                errorMsg = audResult.error;
                contentPreview = "[Audio Base64]";
            }

            req.LlmLatencyMs = durationMs;
            req.CompletedAt = DateTime.UtcNow;

            SynapseLogger.Message($"Completed LLM {req.CapabilityType} request for {req.Mod?.DisplayName ?? "unknown"} in {durationMs}ms. (Success: {success})", req.Mod?.ModId);

            LogResponseIfTrace(req, success, durationMs, contentPreview, errorMsg);
            LogTrainingData(req, resultObj, success, settings);

            // Invoke callback
            var cb = req.Callback;
            if (cb != null)
            {
                SynapseGameComponent.Enqueue(() => cb.DynamicInvoke(resultObj));
            }

            HandleModelErrors(success, errorMsg, settings);
            TrackMetrics(req, resultObj, success, durationMs);
        }

        private static void LogResponseIfTrace(QueuedRequest req, bool success, long durationMs, string contentPreview, string errorMsg)
        {
            if (RimSynapseMod.Instance?.Settings?.traceDebugMode != true) return;

            var respLog = new System.Text.StringBuilder();
            respLog.AppendLine($"── RESPONSE ← {req.Mod?.DisplayName ?? "unknown"} ({durationMs}ms, success={success}) ──");
            if (success)
                respLog.AppendLine(contentPreview);
            else
                respLog.AppendLine($"ERROR: {errorMsg}");
            SynapseLogger.Message(respLog.ToString(), req.Mod?.ModId);
        }

        private static void LogTrainingData(QueuedRequest req, object resultObj, bool success, RimSynapseSettings settings)
        {
            if (!success || settings?.enableTrainingMode != true) return;
            if (!(req.Payload is LlmTextRequest textRequest) || !(resultObj is ChatResult cResult)) return;

            try
            {
                string sys = "";
                string usr = "";
                foreach (var m in textRequest.Messages)
                {
                    if (m.role == "system") sys = m.content;
                    else if (m.role == "user") usr = m.content;
                }

                if (!string.IsNullOrEmpty(usr) && !string.IsNullOrEmpty(cResult.content))
                {
                    var row = new Dictionary<string, string>
                    {
                        ["instruction"] = sys,
                        ["input"] = usr,
                        ["output"] = cResult.content
                    };
                    string jsonLine = Newtonsoft.Json.JsonConvert.SerializeObject(row) + "\n";
                    string saveDir = settings.GetTrainingDirectory();
                    if (!System.IO.Directory.Exists(saveDir))
                    {
                        System.IO.Directory.CreateDirectory(saveDir);
                    }
                    System.IO.File.AppendAllText(System.IO.Path.Combine(saveDir, "training_data.jsonl"), jsonLine);
                }
            }
            catch (Exception ex)
            {
                SynapseLogger.Warning($"Failed to write training data line: {ex.Message}");
            }
        }

        private static void HandleModelErrors(bool success, string errorMsg, RimSynapseSettings settings)
        {
            if (success || string.IsNullOrEmpty(errorMsg)) return;

            string errLower = errorMsg.ToLowerInvariant();
            if (errLower.Contains("model not found") || errLower.Contains("404") || (errLower.Contains("400") && errLower.Contains("model")))
            {
                ModelManager.RefreshCache();
                
                if (settings != null && !settings.autoMapModel)
                {
                    if ((DateTime.UtcNow - _lastModelWarning).TotalSeconds > 10)
                    {
                        _lastModelWarning = DateTime.UtcNow;
                        SynapseGameComponent.Enqueue(() => {
                            Verse.Messages.Message(
                                "RimSynapse LLM Error: Model not found. Did you swap models in LM Studio? Please open RimSynapse Core Mod Settings and reselect your model (or enable Auto-map).",
                                RimWorld.MessageTypeDefOf.RejectInput, false);
                        });
                    }
                }
            }
        }

        private static void TrackMetrics(QueuedRequest req, object resultObj, bool success, long durationMs)
        {
            lock (_recentDurations)
            {
                _recentDurations.Add(durationMs);
                if (_recentDurations.Count > 20)
                    _recentDurations.RemoveAt(0);
            }
            
            if (success && resultObj is ChatResult chatRes && chatRes.completionTokens > 0)
            {
                lock (_topsLock)
                {
                    _recentResults.Enqueue(chatRes);
                    if (_recentResults.Count > 50) _recentResults.Dequeue();
                }

                string rName = req.Options?.requestName ?? "Unknown";
                lock (AvgTokensPerType)
                {
                    if (!AvgTokensPerType.ContainsKey(rName))
                    {
                        AvgTokensPerType[rName] = chatRes.completionTokens;
                    }
                    else
                    {
                        AvgTokensPerType[rName] = (AvgTokensPerType[rName] * 0.8f) + (chatRes.completionTokens * 0.2f);
                    }
                }
            }

            req.CompletedAt = DateTime.UtcNow;
            lock (_queueLock)
            {
                _history.Add(req);
            }
        }

        private static string ExecuteToolOnMainThread(string name, string arguments)
        {
            string output = null;
            var resetEvent = new AutoResetEvent(false);

            SynapseGameComponent.Enqueue(() =>
            {
                try
                {
                    output = SynapseToolRegistry.ExecuteTool(name, arguments);
                }
                catch (Exception ex)
                {
                    output = $"{{\"error\": \"Tool execution crashed: {ex.Message}\"}}";
                }
                finally
                {
                    resetEvent.Set();
                }
            });

            resetEvent.WaitOne(TimeSpan.FromSeconds(10));
            return output ?? "{\"error\": \"Tool execution timed out.\"}";
        }

        private static void UpdateThrottle()
        {
            _throttleLevel = 1.0f;
        }

        internal static void Shutdown()
        {
            _shutdown = true;
            _signal.Set();
        }
    }
}
