using System;
using System.Collections.Concurrent;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// GameComponent that bridges async background work back to Unity's main thread.
    /// Processes queued callbacks every Unity frame (including while paused) via GameComponentUpdate.
    /// Also detects pause state and triggers opportunistic tasks during extended pauses.
    /// </summary>
    public class SynapseGameComponent : GameComponent
    {
        private static readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();

        /// <summary>Max callbacks to process per frame to avoid frame drops.</summary>
        private const int MaxCallbacksPerFrame = 5;

        /// <summary>Real-time tracking for pause-based opportunistic task firing.</summary>
        private static DateTime _pauseStartTime = DateTime.MinValue;
        private static bool _wasPaused = false;
        private static bool _pauseOpportunisticFired = false;
        private static DateTime _lastPauseOpportunisticCheck = DateTime.MinValue;

        /// <summary>How long the game must be paused before opportunistic tasks start firing (seconds).</summary>
        private const float PauseIdleThreshold = 5.0f;

        /// <summary>Interval between opportunistic task checks during pause (seconds).</summary>
        private const float PauseCheckInterval = 2.0f;
        private static int _fileCheckCooldown = 0;
        private static int _tierCheckCooldown = 0;

        public SynapseGameComponent(Game game) { }

        /// <summary>
        /// Scripts mid-run travel with the save as one JSON string (issue #20). Saves from
        /// before this feature have no entry, so the string loads as null and restore is a
        /// no-op; loading always discards whatever scripts the previous session left running
        /// before restoring the save's own.
        /// </summary>
        public override void ExposeData()
        {
            base.ExposeData();

            string persistedScripts = null;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                persistedScripts = SynapseScriptRunner.SnapshotForSave();
            }

            Scribe_Values.Look(ref persistedScripts, "synapseActiveScripts");

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                SynapseScriptRunner.ClearForLoad();
                SynapseScriptRunner.RestoreFromSave(persistedScripts);
            }
        }

        /// <summary>
        /// Enqueue an action to run on the main thread during the next frame.
        /// Thread-safe — can be called from any background thread.
        /// </summary>
        public static void Enqueue(Action action)
        {
            if (action == null) return;
            _mainThreadQueue.Enqueue(action);
        }

        /// <summary>
        /// Clears the main thread callback queue.
        /// </summary>
        public static void ClearMainThreadQueue()
        {
            while (_mainThreadQueue.TryDequeue(out _)) { }
        }

        /// <summary>
        /// Clears both the background LLM queue and the main thread callback queue.
        /// </summary>
        public static void ClearAllQueues()
        {
            ClearMainThreadQueue();
            Internal.RequestQueue.Clear();
        }

        public static void ProcessMainThreadQueue()
        {
            int processed = 0;
            while (processed < MaxCallbacksPerFrame && _mainThreadQueue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    RimSynapse.SynapseLogger.Error($"[RimSynapse] Callback error: {ex}");
                }
                processed++;
            }
        }

        /// <summary>
        /// Called every Unity frame on the main thread — even while paused.
        /// Processes queued callbacks and handles pause-time opportunistic task firing.
        /// </summary>
        public override void GameComponentUpdate()
        {
            // Process callbacks from the queue (LLM results, log dispatch, etc.)
            ProcessMainThreadQueue();

            // Drive deferred-event deadlines (Core #59). Cheap when idle; runs every frame — including
            // while paused and before a map exists — so a held letter always releases at its deadline.
            SynapseDeferredEvents.Tick();

            _fileCheckCooldown++;
            if (_fileCheckCooldown >= 60)
            {
                _fileCheckCooldown = 0;
                PollScriptInputFile();
            }

            // Re-evaluate the capability tier every ~5 seconds: detects model swaps,
            // promotes on sustained fast responses, demotes immediately on slow ones.
            _tierCheckCooldown++;
            if (_tierCheckCooldown >= 300)
            {
                _tierCheckCooldown = 0;
                SynapseTierController.Update();
            }

            // ── Pause-time opportunistic task handling ──
            if (Find.TickManager == null) return;

            bool isPaused = Find.TickManager.Paused;

            if (isPaused)
            {
                if (!_wasPaused)
                {
                    // Just entered pause — start the timer
                    _pauseStartTime = DateTime.UtcNow;
                    _pauseOpportunisticFired = false;
                    _wasPaused = true;
                }

                double pausedSeconds = (DateTime.UtcNow - _pauseStartTime).TotalSeconds;
                if (pausedSeconds >= PauseIdleThreshold)
                {
                    // We've been paused long enough — fire opportunistic tasks periodically
                    if ((DateTime.UtcNow - _lastPauseOpportunisticCheck).TotalSeconds >= PauseCheckInterval)
                    {
                        _lastPauseOpportunisticCheck = DateTime.UtcNow;

                        if (!_pauseOpportunisticFired)
                        {
                            SynapseLogger.Message("Pause detected for 5+ seconds — enabling pause-time opportunistic processing.");
                            _pauseOpportunisticFired = true;
                        }

                        // Tell the task manager to run using real-time cooldowns
                        Internal.OpportunisticTaskManager.TryRunPauseOpportunisticTask();
                    }
                }
            }
            else
            {
                if (_wasPaused && _pauseOpportunisticFired)
                {
                    SynapseLogger.Message("Game unpaused — resuming tick-based opportunistic scheduling.");
                }
                _wasPaused = false;
                _pauseOpportunisticFired = false;
            }
        }

        private bool _vramChecked = false;

        public override void GameComponentTick()
        {
            if (!_vramChecked)
            {
                _vramChecked = true;
                VramAdvisor.Check();
            }
            SynapsePossessionManager.Tick();
            SynapseObjectControlManager.TickingUpdateHacks();
            SynapseScriptRunner.Tick();
        }

        /// <summary>
        /// Called when a game is loaded.
        /// Background services are already running (started in mod constructor).
        /// </summary>
        public override void FinalizeInit()
        {
            base.FinalizeInit();
            SynapseLogger.Message("Game loaded. Main-thread dispatcher active (frame-based, pause-aware).");
        }

        public override void StartedNewGame()
        {
            base.StartedNewGame();
            ClearAllQueues();
            SynapseLogger.Message("Started new game. Queues cleared.");
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
            ClearAllQueues();
            SynapseLogger.Message("Loaded game. Queues cleared.");
        }

        /// <summary>
        /// Directory backing the developer file-drop channel.
        ///
        /// The other end of this channel is the MCP server's gameIpc tools, which resolve
        /// RIMSYNAPSE_ROOT/Core, so this honours the same variable first and only then falls
        /// back to the Core mod's own folder. Both ends must agree or the channel silently
        /// does nothing. These paths were previously hardcoded to a d:\ checkout, so every
        /// lookup missed on any other machine while still costing the File.Exists calls.
        /// </summary>
        private static string _scriptingDir;
        private static string ScriptingDir
        {
            get
            {
                if (_scriptingDir != null) return _scriptingDir;

                string root = System.Environment.GetEnvironmentVariable("RIMSYNAPSE_ROOT");
                if (!string.IsNullOrEmpty(root))
                {
                    string candidate = System.IO.Path.Combine(root, "Core");
                    if (System.IO.Directory.Exists(candidate))
                    {
                        _scriptingDir = candidate;
                        LogResolvedDir("RIMSYNAPSE_ROOT");
                        return _scriptingDir;
                    }
                }

                string modRoot = RimSynapseMod.Instance?.Content?.RootDir;
                _scriptingDir = !string.IsNullOrEmpty(modRoot) ? modRoot : GenFilePaths.ConfigFolderPath;
                LogResolvedDir(!string.IsNullOrEmpty(modRoot) ? "mod RootDir" : "ConfigFolderPath fallback");
                return _scriptingDir;
            }
        }

        /// <summary>
        /// Announce the directory the file bridge will poll, once, naming how it was resolved.
        ///
        /// <para>The MCP side writes its request to a directory it resolves independently, and when
        /// the two disagree the only symptom is a ten-second timeout whose message used to guess at
        /// the game's state. Three separate investigations went looking in the wrong place because
        /// nothing said where the game was actually watching. One line makes that a glance.</para>
        /// </summary>
        private static void LogResolvedDir(string via)
        {
            try
            {
                SynapseLogger.Message($"[RimSynapse] Tool bridge polling directory: {_scriptingDir} (resolved via {via}).");
            }
            catch { }
        }

        private static string ScriptingPath(string fileName)
        {
            return System.IO.Path.Combine(ScriptingDir, fileName);
        }

        private void PollScriptInputFile()
        {
            try
            {
                string inputPath = ScriptingPath("script_input.json");
                string outputPath = ScriptingPath("script_output.log");
                string requestPath = ScriptingPath("game_state_request.json");
                string statePath = ScriptingPath("game_state.json");
                string toolInputPath = ScriptingPath("tool_input.json");
                string toolOutputPath = ScriptingPath("tool_output.json");

                // Poll script execution
                if (System.IO.File.Exists(inputPath))
                {
                    string json = System.IO.File.ReadAllText(inputPath);
                    System.IO.File.Delete(inputPath);

                    if (System.IO.File.Exists(outputPath))
                    {
                        System.IO.File.Delete(outputPath);
                    }

                    RimSynapseAPI.ExecuteScript(json, msg =>
                    {
                        try
                        {
                            System.IO.File.AppendAllText(outputPath, msg + "\n");
                        }
                        catch {}
                    });
                }

                string storytellerInputPath = ScriptingPath("storyteller_input.txt");
                string storytellerOutputPath = ScriptingPath("storyteller_output.log");

                // Poll storyteller command execution
                if (System.IO.File.Exists(storytellerInputPath))
                {
                    string command = System.IO.File.ReadAllText(storytellerInputPath).Trim();
                    System.IO.File.Delete(storytellerInputPath);

                    if (System.IO.File.Exists(storytellerOutputPath))
                    {
                        System.IO.File.Delete(storytellerOutputPath);
                    }

                    System.IO.File.WriteAllText(storytellerOutputPath, $"[Storyteller Console] Processing command: {command}\n");

                    RimSynapseAPI.ExecuteNaturalLanguageCommand(command, msg =>
                    {
                        try
                        {
                            System.IO.File.AppendAllText(storytellerOutputPath, msg + "\n");
                        }
                        catch {}
                    }, (success, finalSummary) =>
                    {
                        try
                        {
                            System.IO.File.AppendAllText(storytellerOutputPath, $"[Storyteller Console] Complete. Success: {success}. Summary: {finalSummary}\n");
                        }
                        catch {}
                    });
                }

                // Poll game state request
                if (System.IO.File.Exists(requestPath))
                {
                    System.IO.File.Delete(requestPath);
                    string stateDump = GetGameStateDump();
                    System.IO.File.WriteAllText(statePath, stateDump);
                }

                // Poll tool execution
                if (System.IO.File.Exists(toolInputPath))
                {
                    string json = System.IO.File.ReadAllText(toolInputPath);
                    System.IO.File.Delete(toolInputPath);

                    if (System.IO.File.Exists(toolOutputPath))
                    {
                        System.IO.File.Delete(toolOutputPath);
                    }

                    Enqueue(() =>
                    {
                        try
                        {
                            var request = Newtonsoft.Json.JsonConvert.DeserializeObject<ToolRequest>(json);
                            if (request != null && !string.IsNullOrEmpty(request.name))
                            {
                                string argsStr = request.arguments != null ? Newtonsoft.Json.JsonConvert.SerializeObject(request.arguments) : "{}";
                                string result = SynapseToolRegistry.ExecuteTool(request.name, argsStr);
                                System.IO.File.WriteAllText(toolOutputPath, result);
                            }
                            else
                            {
                                System.IO.File.WriteAllText(toolOutputPath, "{\"error\": \"Invalid tool request format.\"}");
                            }
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                System.IO.File.WriteAllText(toolOutputPath, $"{{\"error\": {Newtonsoft.Json.JsonConvert.SerializeObject(ex.Message)}}}");
                            }
                            catch {}
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimSynapse] Failed to poll script or state files: {ex.Message}");
            }
        }

        private string GetGameStateDump()
        {
            try
            {
                var dump = new System.Collections.Generic.Dictionary<string, object>();
                
                // Storyteller
                if (Find.Storyteller != null)
                {
                    dump["storyteller"] = Find.Storyteller.def?.defName ?? "Unknown";
                }

                // Colonists
                var colonists = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>();
                if (Find.CurrentMap != null && Find.CurrentMap.mapPawns != null)
                {
                    foreach (var pawn in Find.CurrentMap.mapPawns.FreeColonists)
                    {
                        if (pawn == null) continue;

                        var pData = new System.Collections.Generic.Dictionary<string, object>();
                        pData["name"] = pawn.LabelShort;
                        pData["fullName"] = pawn.Name?.ToStringFull ?? pawn.Label;
                        pData["x"] = pawn.Position.x;
                        pData["z"] = pawn.Position.z;
                        pData["isDowned"] = pawn.Downed;
                        pData["isDrafted"] = pawn.Drafted;
                        pData["equippedWeapon"] = pawn.equipment?.Primary?.def?.defName ?? "None";

                        // Skills
                        var skills = new System.Collections.Generic.Dictionary<string, object>();
                        if (pawn.skills != null)
                        {
                            foreach (var skill in pawn.skills.skills)
                            {
                                if (skill?.def == null) continue;
                                var skData = new System.Collections.Generic.Dictionary<string, object>();
                                skData["level"] = skill.Level;
                                skData["passion"] = skill.passion.ToString();
                                skills[skill.def.defName] = skData;
                            }
                        }
                        pData["skills"] = skills;

                        // Traits
                        var traits = new System.Collections.Generic.List<string>();
                        if (pawn.story?.traits != null)
                        {
                            foreach (var trait in pawn.story.traits.allTraits)
                            {
                                if (trait?.def == null) continue;
                                traits.Add(trait.def.defName);
                            }
                        }
                        pData["traits"] = traits;

                        // Health Hediffs
                        var hediffs = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>();
                        if (pawn.health?.hediffSet != null)
                        {
                            foreach (var hediff in pawn.health.hediffSet.hediffs)
                            {
                                if (hediff?.def == null) continue;
                                var hData = new System.Collections.Generic.Dictionary<string, object>();
                                hData["defName"] = hediff.def.defName;
                                hData["label"] = hediff.Label;
                                hData["severity"] = hediff.Severity;
                                hediffs.Add(hData);
                            }
                        }
                        pData["hediffs"] = hediffs;

                        colonists.Add(pData);
                    }
                }
                dump["colonists"] = colonists;

                // Active Scripts
                var runnerData = new System.Collections.Generic.Dictionary<string, object>();
                runnerData["activeScriptsCount"] = SynapseScriptRunner.ActiveScriptsCount;
                runnerData["activeScripts"] = SynapseScriptRunner.GetActiveScriptNames();
                dump["scriptRunner"] = runnerData;

                return Newtonsoft.Json.JsonConvert.SerializeObject(dump, Newtonsoft.Json.Formatting.Indented);
            }
            catch (Exception ex)
            {
                return $"{{\"error\": \"Failed to serialize game state: {ex.Message}\"}}";
            }
        }
    }

    public class ToolRequest
    {
        public string name;
        public object arguments;
    }
}

