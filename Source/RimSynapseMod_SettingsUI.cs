using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// Settings window UI rendering: DoSettingsWindowContents and helpers.
    /// </summary>
    public partial class RimSynapseMod
    {
        public override void DoSettingsWindowContents(Rect inRect)
        {
            // Scrollable container for all settings
            var viewRect = new Rect(0, 0, inRect.width - 20f, _viewHeight);
            Widgets.BeginScrollView(inRect, ref _scrollPosition, viewRect);
            var listing = new Listing_Standard();
            listing.Begin(viewRect);

            // ── Main UI Navigation ──────────────────────────────────
            var prevColor = GUI.color;
            GUI.color = new Color(0.9f, 0.45f, 0.15f); // Orange

            if (listing.ButtonText("Customize LLM Providers"))
            {
                Find.WindowStack.Add(new RimSynapse.UI.Dialog_ProviderSettings());
            }
            listing.Gap(4f);
            if (listing.ButtonText("Map Context to Models"))
            {
                Find.WindowStack.Add(new RimSynapse.UI.Dialog_QueryRouting());
            }

            GUI.color = prevColor;
            listing.Gap(12f);


            // ── Context Embedding ───────────────────────────────────
            listing.Label("Context Embedding",
                tooltip: "Inject game state (pawn data, colony, factions) into LLM requests. " +
                    "Configure prompts and weights via XML files in Defs/.");
            listing.GapLine();

            listing.CheckboxLabeled("Enable context embedding",
                ref Settings.enableContextEmbedding,
                "When enabled, Core assembles game state into a structured context block " +
                "and injects it into the system message of LLM requests. " +
                "Configure prompts in Defs/SynapsePrompts/, weights in Defs/SynapseWeights/, " +
                "and profiles in Defs/SynapseProfiles/.");

            if (Settings.enableContextEmbedding)
            {
                listing.Gap(4f);
                listing.Label("  Context is active. Edit XML files in the mod's Defs/ folder " +
                    "to customize prompts, weights, and event profiles.");

                int detectedCtx = Internal.ModelManager.ContextLength ?? Settings.modelContextLimit;
                listing.Label($"  Token budget adapts to LM Studio context window " +
                    $"({detectedCtx} tokens).");

                listing.Gap(2f);
                Settings.modelContextLimit = (int)listing.SliderLabeled(
                    $"  Active Model Context Limit: {Settings.modelContextLimit} tokens",
                    Settings.modelContextLimit, 2048f, 131072f,
                    tooltip: "Manually specify your local LLM's context length (e.g. 32768, 16384, or 8192) if it cannot be dynamically detected from the provider API. Controls prompt size limits and paginators.");
            }

            // ── Deferred news & bulletins (0.8) ─────────────────────
            listing.Gap(10f);
            listing.Label("Deferred News & Bulletins",
                tooltip: "News travels slowly on the rim. Events are held for a delay and released later; " +
                    "with a powered comms console you get an advance breaking-news bulletin on screen, " +
                    "otherwise you only learn of an event when it finally arrives.");
            listing.GapLine();

            listing.CheckboxLabeled("Enable deferred news pipeline",
                ref Settings.deferNewsEnabled,
                "Hold news-worthy events and release them after a delay. Turn OFF for vanilla-timed events " +
                "— e.g. when you are not running an LLM storyteller or the WorldNews mod.");

            if (Settings.deferNewsEnabled)
            {
                Settings.deferDaysDefault = (float)Math.Round(listing.SliderLabeled(
                    $"  Default delay: {Settings.deferDaysDefault:0.0} days",
                    Settings.deferDaysDefault, 0f, 5f,
                    tooltip: "How long most events are held before they arrive. 0 = immediate."), 1);

                Settings.deferDaysQuest = (float)Math.Round(listing.SliderLabeled(
                    $"  Quest / opportunity delay: {Settings.deferDaysQuest:0.0} days",
                    Settings.deferDaysQuest, 0f, 5f,
                    tooltip: "Delay for quest and opportunity offers — this is the lead time you lose without comms."), 1);

                Settings.deferDaysThreat = (float)Math.Round(listing.SliderLabeled(
                    $"  Combat threat delay: {Settings.deferDaysThreat:0.0} days",
                    Settings.deferDaysThreat, 0f, 5f,
                    tooltip: "Delay for raids and sieges. Default 0 so an active attack is never announced late."), 1);

                listing.Label("  Finer per-event overrides are planned; for now these categories cover every letter.");
            }

            listing.Gap(4f);
            string tierModeLabel;
            switch (Settings.agentTierMode)
            {
                case 1: tierModeLabel = "Minimal"; break;
                case 2: tierModeLabel = "Standard"; break;
                case 3: tierModeLabel = "Rich"; break;
                default: tierModeLabel = "Auto"; break;
            }
            if (listing.ButtonTextLabeled(
                $"Capability tier (effective: {SynapseTierController.Current})",
                tierModeLabel))
            {
                Settings.agentTierMode = (Settings.agentTierMode + 1) % 4;
            }
            listing.Label("  Auto starts at Minimal and promotes from measured response latency. " +
                "Minimal fits ~2K context models; Rich spends more context on strong hardware.");

            listing.Gap(4f);
            listing.CheckboxLabeled("Ignore token costs (EXPERIMENTAL)",
                ref Settings.ignoreTokenCosts,
                "Treats metered cloud backends (OpenAI, Anthropic, Gemini, Pollinations) as unmetered: " +
                "context scales up within the latency headroom and the token caps below are bypassed. " +
                "This can significantly increase API spend. Logged when active.");

            if (SynapseTierController.IsBackendMetered() && !Settings.ignoreTokenCosts)
            {
                listing.Gap(2f);
                Settings.tokenCapPerRequest = (int)listing.SliderLabeled(
                    $"  Token cap per request: {Settings.tokenCapPerRequest}",
                    Settings.tokenCapPerRequest, 512f, 32768f,
                    tooltip: "Maximum prompt tokens a single request may spend on this metered backend.");
                Settings.tokenCapPerDay = (int)listing.SliderLabeled(
                    $"  Token cap per day: {Settings.tokenCapPerDay} (spent today: {SynapsePerformanceModel.TokensSpentToday})",
                    Settings.tokenCapPerDay, 10000f, 2000000f,
                    tooltip: "Total tokens per real day on this metered backend. Resets at UTC midnight; not persisted across restarts.");
            }

            listing.Gap(4f);
            Settings.agentMaxTurns = (int)listing.SliderLabeled(
                $"Agent turn limit: {Settings.agentMaxTurns}",
                Settings.agentMaxTurns, 1f, 20f,
                tooltip: "Maximum plan-execute-observe turns per agent run. Each turn is a full LLM round trip.");
            Settings.agentMaxRequestsPerRun = (int)listing.SliderLabeled(
                $"Agent request budget per run: {Settings.agentMaxRequestsPerRun}",
                Settings.agentMaxRequestsPerRun, 1f, 50f,
                tooltip: "Maximum LLM requests one agent run may issue, independent of turns.");
            listing.CheckboxLabeled("Allow autonomous runs to change game state",
                ref Settings.allowAutonomousMutations,
                "Autonomous agent runs (not started by you directly) are refused state-mutating tools unless this is on. Direct Action Console commands are always allowed.");

            listing.CheckboxLabeled("Enable escalation to the agent (EXPERIMENTAL)",
                ref Settings.enableEscalation,
                "When a programmed system hits an outcome it was not built for (e.g. a ceremony record the model failed to write), it may hand the situation to the agent instead of dropping it. Rate-limited and tier-gated; escalated runs cannot change game state unless the setting above is also on.");

            if (Settings.enableEscalation)
            {
                Settings.escalationCooldownSeconds = (int)listing.SliderLabeled(
                    $"  Escalation cooldown: {Settings.escalationCooldownSeconds}s",
                    Settings.escalationCooldownSeconds, 10f, 600f,
                    tooltip: "Minimum seconds between escalated runs, so a broken backend cannot convert every failing hook into an agent run.");
                Settings.escalationSessionCap = (int)listing.SliderLabeled(
                    $"  Escalations per session: {Settings.escalationSessionCap}",
                    Settings.escalationSessionCap, 1f, 50f,
                    tooltip: "Hard cap on escalated runs per game session.");
            }

            listing.CheckboxLabeled("Enable storyteller tool usage",
                ref Settings.enableStorytellerTools,
                "When enabled, allows the AI storyteller to invoke tools to query precise game data. " +
                "Disabling this reduces the prompt size significantly (fits in standard 8K context windows) and speeds up storytelling evaluation.");

            if (Settings.enableStorytellerTools)
            {
                listing.Gap(4f);
                int maxSliderLimit = Math.Max(16384, Settings.modelContextLimit);
                Settings.maxPacingContextTokens = (int)listing.SliderLabeled(
                    $"Storyteller Max Context Budget: {Settings.maxPacingContextTokens} tokens",
                    Settings.maxPacingContextTokens, 2048f, maxSliderLimit,
                    tooltip: "The target maximum prompt budget for storyteller checks. Lower values (like 2048) speed up generation and use less VRAM. Higher values allow including more detailed event histories.");
            }

            listing.Gap(4f);
            Settings.shortTermMemoryHours = listing.SliderLabeled(
                $"Short-Term Memory Window: {Settings.shortTermMemoryHours:F0} hours",
                Settings.shortTermMemoryHours, 24f, 168f,
                tooltip: "How long recent social interactions and events are kept in the LLM's context window.");

            listing.Gap(12f);

            // ── Advanced ─────────────────────────────────────────────
            listing.Label("Advanced", tooltip: "Sanitization, keep-alive, and logging.");
            listing.GapLine();

            if (listing.ButtonText("Open LLM Queue Monitor"))
            {
                Find.WindowStack.Add(new RimSynapse.UI.Dialog_QueueMonitor());
            }

            if (listing.ButtonText("Open Test Bench"))
            {
                Find.WindowStack.Add(new RimSynapse.UI.Dialog_TestBench());
            }

            if (listing.ButtonText("Open Storyteller Mode Window"))
            {
                Find.WindowStack.Add(new RimSynapse.UI.Dialog_StorytellerMode());
            }

            if (listing.ButtonText("Open Script Debugger Window"))
            {
                Find.WindowStack.Add(new RimSynapse.UI.Dialog_ScriptDebugger());
            }

            listing.Gap(6f);

            listing.CheckboxLabeled("Auto-map to active model",
                ref Settings.autoMapModel,
                "Automatically use the first loaded model in LM Studio.");

            // When auto-map is off, show model selector dropdown
            if (!Settings.autoMapModel)
            {
                string currentModel = string.IsNullOrEmpty(Settings.selectedModel)
                    ? "(none selected)" : Settings.selectedModel;

                if (listing.ButtonText($"Model: {currentModel}"))
                {
                    var modelIds = Internal.ModelManager.CachedModelIds;
                    if (modelIds.Count == 0)
                    {
                        // No cached models — trigger a refresh
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                Internal.HttpEngine.EnsureInitialized();
                                var result = Internal.HttpEngine.GetModelsSync();
                                if (result.online && result.modelIds.Count > 0)
                                {
                                    // Force cache update
                                    Internal.ModelManager.RefreshCache();
                                    Internal.ModelManager.GetModels(_ => { });
                                }
                                else
                                {
                                }
                            }
                            catch (Exception ex)
                            {
                            }
                        });
                    }
                    else
                    {
                        // Build FloatMenu with available models
                        var options = new List<FloatMenuOption>();
                        foreach (var id in modelIds)
                        {
                            string modelId = id; // capture for closure
                            options.Add(new FloatMenuOption(modelId, () =>
                            {
                                Settings.selectedModel = modelId;
                            }));
                        }
                        Find.WindowStack.Add(new FloatMenu(options));
                    }
                }
            }

            listing.CheckboxLabeled("Sanitize responses",
                ref Settings.sanitizeResponse,
                "Strip <think> blocks and repair broken JSON from LLM output.");

            listing.CheckboxLabeled("Enable keep-alive pings",
                ref Settings.enableKeepAlive,
                "Ping LM Studio every 4 minutes to prevent model unloading.");

            listing.CheckboxLabeled("Disable thinking/reasoning",
                ref Settings.disableThinking,
                "Prevent reasoning models from using chain-of-thought. Saves tokens and reduces latency.");
                
            listing.CheckboxLabeled("Disable Safety Context Stripping (Experimental)",
                ref Settings.disableSafetyContextStripping,
                "Disables the geometric abstraction wrapper used to bypass local LLM safety filters during violent mental breaks. Enable this to test your endpoint's raw uncensored capabilities.");
                
            listing.Gap(6f);
            
            Settings.audioBoost = listing.SliderLabeled(
                $"TTS Audio PCM Boost: {Settings.audioBoost:F1}x",
                Settings.audioBoost, 1.0f, 4.0f,
                tooltip: "Directly boosts the PCM waveform amplitude. Helpful for quiet AI-generated TTS voices.");

            listing.Gap(6f);

            listing.Gap(6f);
            listing.CheckboxLabeled("Enable LM Studio Trace Debug Mode",
                ref Settings.traceDebugMode,
                "Dumps the full JSON context sent to LM Studio into the standard developer console for troubleshooting.");

            listing.Gap(6f);
            listing.CheckboxLabeled("Enable Storyteller Fine-Tuning Curation",
                ref Settings.enableTrainingMode,
                "Automatically saves prompt and response data in JSONL format to standard save folder for Gemma 4 fine-tuning.");

            if (Settings.enableTrainingMode)
            {
                listing.Gap(2f);
                listing.CheckboxLabeled("  Enable Storyteller Fast-Telemetry Mode (Dev)",
                    ref Settings.fastTelemetryMode,
                    "Runs storyteller evaluations much more frequently (every 1000 ticks) to quickly generate large datasets. Use in Speed 4 (Dev) mode for optimal results.");

                listing.Gap(2f);
                listing.Label("  Dataset Output Directory (leave blank for default):");
                Settings.trainingDataDirectory = listing.TextEntry(Settings.trainingDataDirectory);

                listing.Gap(4f);
                Rect clearBtnRect = listing.GetRect(24f);
                clearBtnRect.xMin += 15f; // Indent slightly
                clearBtnRect.width = 220f;
                if (Widgets.ButtonText(clearBtnRect, "Clear Curation Datasets"))
                {
                    ClearTrainingDataFiles();
                }
            }
            listing.Label("DLC Context Testing", tooltip: "Simulate disabling DLCs for LLM context generation while they are physically loaded.");
            listing.GapLine();
            if (ModsConfig.IdeologyActive) listing.CheckboxLabeled("Include Ideology Context", ref Settings.testIdeologyActive);
            if (ModsConfig.RoyaltyActive) listing.CheckboxLabeled("Include Royalty Context", ref Settings.testRoyaltyActive);
            if (ModsConfig.BiotechActive) listing.CheckboxLabeled("Include Biotech Context", ref Settings.testBiotechActive);
            if (ModsConfig.AnomalyActive) listing.CheckboxLabeled("Include Anomaly Context", ref Settings.testAnomalyActive);

            listing.Gap(12f);

            // ── Opportunistic Tasks ─────────────────────────────────────
            listing.Label("Opportunistic Tasks",
                tooltip: "Controls how aggressively the mod fills idle GPU time with background AI tasks.\n" +
                    "Aggressive: Maximizes local LLM usage.\nConservative: Minimizes API costs.");
            listing.GapLine();

            // Throttle mode selector
            string[] modeLabels = { "Auto-Detect", "Aggressive (Local)", "Balanced", "Conservative (Paid API)" };
            int modeIndex = Settings.opportunisticThrottleMode + 1; // -1→0, 0→1, 1→2, 2→3
            listing.Label($"Throttle Mode: {modeLabels[Math.Max(0, Math.Min(modeIndex, 3))]}");
            if (listing.ButtonText("Cycle Throttle Mode"))
            {
                Settings.opportunisticThrottleMode++;
                if (Settings.opportunisticThrottleMode > 2) Settings.opportunisticThrottleMode = -1;
            }

            // Burst size (only relevant for Aggressive)
            listing.Label($"Burst Size (Aggressive mode): {Settings.opportunisticBurstSize}",
                tooltip: "How many background tasks can fire per idle check. Higher = more GPU usage.");
            Settings.opportunisticBurstSize = (int)listing.Slider(Settings.opportunisticBurstSize, 1, 5);

            // Per-task controls
            var tasks = Internal.OpportunisticTaskManager.GetTaskSnapshot();
            if (tasks.Count > 0)
            {
                listing.Gap(6f);
                listing.Label("Registered Tasks:");

                foreach (var task in tasks.OrderByDescending(t => t.Priority))
                {
                    listing.Gap(4f);
                    string enabledStr = task.Enabled ? "ON" : "OFF";
                    listing.Label($"  {task.Label}  [P{task.Priority}]  W:{task.BaseWeight:F1}  CD:{task.CooldownTicks}t  ({enabledStr})",
                        tooltip: task.Description);
                }
            }

            listing.Gap(12f);

            // ── Notifications ───────────────────────────────────────────
            listing.Label("Notifications", tooltip: "Control startup notifications.");
            listing.GapLine();

            listing.CheckboxLabeled("Show VRAM status on game load",
                ref Settings.showVramAdvisory,
                "Shows estimated GPU memory breakdown when the game starts.\n" +
                "Uncheck to disable (only shows if NVIDIA Tool is not installed).");

            listing.CheckboxLabeled("Show LLM Queue Monitor icon on toolbar",
                ref Settings.showQueueMonitorIcon,
                "Shows the AI queue monitor icon in the bottom right play settings toolbar.");

            listing.CheckboxLabeled("Show Storyteller Mode console icon on toolbar",
                ref Settings.showGodModeIcon,
                "Shows the Storyteller Mode LLM console button in the bottom right play settings toolbar.");

            listing.End();
            _viewHeight = listing.CurHeight;
            Widgets.EndScrollView();
        }

        private void ClearTrainingDataFiles()
        {
            try
            {
                string dir = Settings.GetTrainingDirectory();
                string path1 = System.IO.Path.Combine(dir, "training_data.jsonl");
                string path2 = System.IO.Path.Combine(dir, "debug_training_data.jsonl");

                if (System.IO.File.Exists(path1)) System.IO.File.Delete(path1);
                if (System.IO.File.Exists(path2)) System.IO.File.Delete(path2);

                Messages.Message("RimSynapse training dataset files cleared successfully.", RimWorld.MessageTypeDefOf.PositiveEvent, false);
            }
            catch (System.Exception ex)
            {
                Log.Error($"[RimSynapse] Failed to clear training data files: {ex.Message}");
            }
        }

    }
}
