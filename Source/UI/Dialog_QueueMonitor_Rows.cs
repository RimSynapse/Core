using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using RimSynapse.Internal;

namespace RimSynapse.UI
{
    /// <summary>
    /// Queue Monitor row drawing and utility methods.
    /// </summary>
    public partial class Dialog_QueueMonitor
    {
        private void DrawRequestRow(Rect rect, RequestQueue.QueuedRequest req, string status)
        {
            float curX = rect.x;
            string queuedMs = GetQueuedTimeMs(req).ToString("F0");
            string latencyMs = GetLatencyTimeMs(req).ToString("F0");
            string score = req.CurrentScore.ToString("F0");
            string timeout = req.Options?.maxWaitMs?.ToString() ?? "INF";
            string modName = req.Mod?.DisplayName ?? "Unknown";
            string prio = req.Priority.ToString();
            string taskName = req.Options?.requestName ?? "—";
            string targetName = req.Options?.targetName ?? "—";
            string tokens = req.Result != null ? $"{req.Result.promptTokens} / {req.Result.completionTokens}" : "—";
            string provider = GetProviderForRequest(req);
            string model = GetModelForRequest(req);

            var s = RimSynapseMod.Instance.Settings;
            bool[] colsVisible = { s.qmShowPrio, s.qmShowMod, s.qmShowTarget, s.qmShowTask, s.qmShowQueued, s.qmShowLatency, s.qmShowStatus, s.qmShowScore, s.qmShowTimeout, s.qmShowTokens, s.qmShowProvider, s.qmShowModel };

            string[] vals = { prio, modName, targetName, taskName, queuedMs, latencyMs, status, score, timeout, tokens, provider, model };
            for (int i = 0; i < 12; i++)
            {
                if (!colsVisible[i]) continue;
                Rect r = new Rect(curX, rect.y, mainWidths[i] - 4f, rect.height);
                Widgets.Label(r, vals[i].Truncate(r.width));
                Widgets.DrawLineVertical(curX + mainWidths[i], rect.y, rect.height);
                curX += mainWidths[i];
            }

            if (s.qmShowPrompt)
            {
                Rect r = new Rect(curX, rect.y, mainWidths[12] - 4f, rect.height);
                string textDump = GetPayloadTextDump(req);
                Widgets.Label(r, textDump);
                Widgets.DrawLineVertical(curX + mainWidths[12], rect.y, rect.height);
                curX += mainWidths[12];
            }

            if (s.qmShowResponse)
            {
                Rect r = new Rect(curX, rect.y, mainWidths[13] - 4f, rect.height);
                string resultStr = req.Result != null ? (req.Result.success ? req.Result.content : req.Result.error) : "Pending...";
                Widgets.Label(r, resultStr);
                Widgets.DrawLineVertical(curX + mainWidths[13], rect.y, rect.height);
                curX += mainWidths[13];
            }
            Widgets.DrawLineHorizontal(rect.x, rect.yMax, rect.width);
        }

        private string GetPayloadTextDump(RequestQueue.QueuedRequest req)
        {
            if (req.Payload is LlmTextRequest txt) return string.Join("\n\n", txt.Messages.Select(m => $"[{m.role.ToUpper()}]\n{m.content}"));
            if (req.Payload is LlmVisionRequest vis) return string.Join("\n\n", vis.Messages.Select(m => $"[{m.role.ToUpper()}]\n{m.content}"));
            if (req.Payload is LlmImageRequest img) return img.Prompt;
            if (req.Payload is LlmAudioRequest aud) return aud.InputText;
            return "Unknown Payload";
        }

        private void DrawOpportunisticRow(Rect rect, string status, string task, string prio,
            string weight, string runs, string cooldown, string mod)
        {
            float curX = rect.x;
            string[] vals = { status, task, prio, weight, runs, cooldown, mod };
            for (int i = 0; i < 7; i++)
            {
                Rect r = new Rect(curX, rect.y, oppWidths[i] - 4f, 25f);
                Widgets.Label(r, vals[i].Truncate(r.width));
                Widgets.DrawLineVertical(curX + oppWidths[i], rect.y, rect.height);
                curX += oppWidths[i];
            }
            Widgets.DrawLineHorizontal(rect.x, rect.yMax, rect.width);
        }

        private float GetRowHeight(RequestQueue.QueuedRequest req)
        {
            float height = 25f;
            var s = RimSynapseMod.Instance.Settings;
            if (s.qmShowPrompt)
            {
                string textDump = GetPayloadTextDump(req);
                float promptH = Text.CalcHeight(textDump, mainWidths[11] - 4f);
                if (promptH + 10f > height) height = promptH + 10f;
            }
            if (s.qmShowResponse && req.Result != null)
            {
                string resultStr = req.Result.success ? req.Result.content : req.Result.error;
                float resH = Text.CalcHeight(resultStr, mainWidths[12] - 4f);
                if (resH + 10f > height) height = resH + 10f;
            }
            return height;
        }

        private string GetThrottleModeLabel()
        {
            var settings = RimSynapseMod.Instance?.Settings;
            int mode = settings?.opportunisticThrottleMode ?? -1;
            switch (mode)
            {
                case 0: return "Aggressive";
                case 1: return "Balanced";
                case 2: return "Conservative";
                default:
                    bool isRemote = settings?.IsRemoteUrl ?? false;
                    return isRemote ? "Auto → Conservative" : "Auto → Aggressive";
            }
        }

        private string GetProviderForRequest(RequestQueue.QueuedRequest req)
        {
            var settings = RimSynapseMod.Instance?.Settings;
            string routingId = RoutingId.LocalOnly;
            string queryKey = $"{req.Mod?.ModId}:{req.Options?.queryId}";

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

            if (routingId == RoutingId.LocalOnly) return "Local";
            if (routingId == RoutingId.OpenAI) return "OpenAI";
            if (routingId == RoutingId.Gemini) return "Gemini";
            if (routingId == RoutingId.Claude) return "Claude";
            if (routingId == RoutingId.Pollinations) return "Pollinations.ai";
            if (routingId.StartsWith(RoutingId.CustomPrefix))
            {
                string customId = routingId.Substring(RoutingId.CustomPrefix.Length);
                var custom = settings?.customProviders.Find(c => c.id == customId);
                if (custom != null) return $"Custom: {custom.name}";
                return "Custom";
            }
            return routingId;
        }

        private string GetModelForRequest(RequestQueue.QueuedRequest req)
        {
            if (req.Result != null && !string.IsNullOrEmpty(req.Result.model))
                return req.Result.model;
                
            var settings = RimSynapseMod.Instance?.Settings;
            string routingId = RoutingId.LocalOnly;
            string queryKey = $"{req.Mod?.ModId}:{req.Options?.queryId}";

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
                if (!string.IsNullOrEmpty(req.Options?.model)) return req.Options.model;
                if (settings != null && settings.autoMapModel && !string.IsNullOrEmpty(ModelManager.ActiveModel))
                    return ModelManager.ActiveModel;
                return settings?.modelLocal ?? "Unknown";
            }
            
            string capKey = "default_text";
            LlmCapabilities cap = LlmCapabilities.Text;
            if (req.Mod != null && !string.IsNullOrEmpty(req.Options?.queryId) && req.Mod.RegisteredQueries.TryGetValue(req.Options.queryId, out var qDef2))
            {
                cap = qDef2.requiredCaps;
            }
            if ((cap & LlmCapabilities.Image) == LlmCapabilities.Image) capKey = "default_image";
            else if ((cap & LlmCapabilities.Vision) == LlmCapabilities.Vision) capKey = "default_vision";
            else if ((cap & LlmCapabilities.Audio) == LlmCapabilities.Audio) capKey = "default_audio";

            if (settings != null)
            {
                if (settings.queryRoutingModels.TryGetValue(queryKey, out var mod1) && !string.IsNullOrEmpty(mod1))
                    return mod1;
                
                if (settings.queryRoutingModels.TryGetValue(capKey, out var capModel) && !string.IsNullOrEmpty(capModel))
                    return capModel;
                
                if (routingId == RoutingId.OpenAI) return settings.modelOpenAi;
                if (routingId == RoutingId.Gemini) return settings.modelGemini;
                if (routingId == RoutingId.Claude) return settings.modelClaude;
                if (routingId == RoutingId.Pollinations) return settings.modelPollinations;
                
                if (routingId.StartsWith(RoutingId.CustomPrefix))
                {
                    string customId = routingId.Substring(RoutingId.CustomPrefix.Length);
                    var custom = settings?.customProviders.Find(c => c.id == customId);
                    if (custom != null) return custom.model;
                }
            }

            return "Unknown";
        }
    }
    }
