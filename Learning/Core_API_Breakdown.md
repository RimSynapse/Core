# RimSynapse Core API Breakdown

RimSynapse-Core is the backbone of the RimSynapse mod family. It abstracts away all HTTP communication, asynchronous queueing, context embedding, and hardware monitoring, providing companion mods with a clean, heavily optimized C# API.

---

## 1. Mod Registration (`SynapseCore.cs`)
Before using the LLM engine, companion mods must register during startup.

- **`SynapseCore.Register(string modId, string displayName, string systemPrompt = null)`**
  Registers your mod and returns a `SynapseModHandle`. This handle must be passed to all subsequent API calls. You can define a default System Prompt here.
- **`SynapseCore.IsModLoaded(string modId)`**
  Safely checks if another companion mod (like `RimSynapsePsychology`) is loaded, allowing for cross-mod integrations without strict assembly dependencies.

---

## 2. LLM Dispatch Client (`SynapseClient.cs`)
All communication with local and cloud providers runs through `SynapseClient`. Requests are queued by priority, dynamically throttled, and processed asynchronously on a background thread. Callbacks are guaranteed to execute on Unity's main thread.

### High-Level API Methods
- **`ChatAsync(SynapseModHandle mod, List<ChatMessage> messages, ChatOptions options, Action<ChatResult> callback)`**
  The standard pipeline for sending OpenAI-formatted messages to the text model.
- **`PromptAsync(SynapseModHandle mod, string systemPrompt, string userMessage, Action<ChatResult> callback, ChatOptions options = null)`**
  A convenience wrapper for simple queries.
- **`ChatFromJsonAsync(...)` / `ChatFromXmlAsync(...)`**
  Convenience wrappers that automatically parse JSON or XML into message structures.

### Direct Request Dispatchers (Low-Level APIs)
For advanced modding scenarios, you can dispatch direct typed requests:
- **`SendTextAsync(SynapseModHandle mod, LlmTextRequest request, ChatOptions options, Action<ChatResult> callback)`**
  Dispatches a low-level text generation request.
- **`SendVisionAsync(SynapseModHandle mod, LlmVisionRequest request, ChatOptions options, Action<ChatResult> callback)`**
  Dispatches a vision model query (containing image URLs or base64 paths alongside text prompts).
- **`SendImageAsync(SynapseModHandle mod, LlmImageRequest request, ChatOptions options, Action<ImageResult> callback)`**
  Dispatches an image generation request.
- **`SendAudioAsync(SynapseModHandle mod, LlmAudioRequest request, ChatOptions options, Action<AudioResult> callback)`**
  Dispatches an audio/TTS generation request (routing to ElevenLabs or Voicebox).

### Backend Discovery
- **`GetModelsAsync(Action<ModelsResult> callback)`**
  Asynchronously queries active models and parameters from the local backend (e.g. LM Studio).

### Global Status Getters
- **`SynapseClient.IsOnline`**: Returns true if the LLM backend is actively responding.
- **`SynapseClient.ActiveModelName`**: The name of the currently loaded model.
- **`SynapseClient.TotalQueueDepth`**: The number of pending requests across all mods.
- **`SynapseClient.ThrottleLevel`**: The current dynamic game-speed throttle.
- **`SynapseClient.Gpu`**: Exposes real-time VRAM/GPU stats populated by the NVIDIA integration tool.

---

## 3. Data Structures and Models

### Request Payloads (`Source/Models/Requests/`)

#### `LlmTextRequest`
- `string SystemPrompt`: Injected system instructions.
- `List<ChatMessage> Messages`: The message history.
- `bool EnforceJson`: If true, requires the model response to be valid JSON.

#### `LlmVisionRequest`
- `string Prompt`: The instruction/question about the image.
- `string ImageBase64`: Base64 encoded image data.
- `string ImageUrl`: Or a direct URL to the target image.

#### `LlmImageRequest`
- `string Prompt`: Description of the image to generate.
- `int Width` / `int Height`: Image dimensions.

#### `LlmAudioRequest`
- `string InputText`: Text to convert to speech.
- `string Voice`: Target voice profile name or UUID.
- `string Instruct`: Optional style/character instruct prompt for the TTS engine.

### Response Result Callbacks (`Source/Models/`)

#### `ChatResult`
- `bool success`: Whether the query completed without errors.
- `string content`: The textual response from the model.
- `string error`: Error message if success is false.
- `int promptTokens` / `int completionTokens`: Token usage diagnostics.

#### `ImageResult`
- `bool success`: Success status.
- `string base64Image`: Base64 string of the generated image.
- `string error`: Error explanation.

#### `AudioResult`
- `bool success`: Success status.
- `string base64Audio`: Base64 encoded PCM/WAV audio bytes.
- `string error`: Error explanation.

### Event Tracking Data Structures

#### `PastEvent`
Tracks in-game incidents and their current resolutions:
- `string eventId`: Unique event identifier (automatically generated).
- `string parentEventId`: Link to a parent event (used to chain resolutions back to their triggers).
- `int gameTick`: In-game tick when the event occurred.
- `SynapseDate date`: In-game date representation.
- `string eventDescription`: Human-readable description of the event.
- `string category`: Event classification (e.g. `"Threat"`, `"Quest"`, `"Geopolitical"`).
- `string factionName` / `string settlementName`: Associated faction and settlement names.
- `string outcomeDescription`: Summary of how the event resolved.
- `EventOutcome outcome`: The outcome state enum.
- `bool isResolved`: True if the event has been resolved.
- `string sourceFactionId` / `string targetFactionId`: Geopolitical faction ID links.

#### `EventOutcome` (Enum)
Represents the structural outcome category:
- `Unknown`: Resolution state undetermined.
- `Success`: The event was successfully completed (e.g., beggars helped, quest completed).
- `Failed`: The event failed (e.g., failed quest, failed peace talks).
- `Ignored`: The event expired or was ignored by the colony.
- `Tragedy`: Tragic resolution (e.g., colonist deaths, base burned).
- `Triumph`: Successful defense or threat neutralization.
- `Conflict`: Open hostilities triggered (e.g., attacked visitors, declared war).

---

## 4. Mod Handles (`SynapseModHandle.cs`)
Returned during registration, the `SynapseModHandle` manages mod-specific configurations and budget parameters:

- **`ModId`**: Unique identifier string.
- **`DisplayName`**: Human-readable name.
- **`QueryBudgetPercent`**: User-allocated budget percentage (0-100) controlled via game settings.
- **`SystemPrompt`**: The base system prompt that can be read or modified at runtime.
- **`DefaultTiers`**: Default context tiers (`ContextTierMask`) used during prompt building.
- **`RegisterQueryType(string queryId, string displayName, LlmCapabilities requiredCaps)`**
  Registers a custom query type, allowing players to route specific tasks (like chat vs story generation) to different engines.

---

## 5. The Opportunistic Task Engine (`SynapseClient.cs`)
Rather than freezing the game or hoarding CPU cycles, non-critical background generations should be registered as Opportunistic Tasks.

- **`RegisterOpportunisticTask(SynapseModHandle mod, string taskId, Action callback, OpportunisticTaskConfig config)`**
  Registers a low-priority background function. The Core engine will automatically trigger the `callback` when the main LLM request queue is empty, respecting the defined `CooldownTicks`, `Priority`, and `Weight` limits.

---

## 6. Context Embedding (`SynapseCoreContext.cs`)
Mods can leverage the Core's dynamic context engine to inject colony state into their prompts without writing custom scrapers.

- **`GetContextText(string eventType, Pawn sourcePawn = null, Pawn targetPawn = null)`**
  Assembles a rich text string containing colony wealth, food reserves, and the specific pawns' health/mood statuses.
- **`ResolvePrompt(string eventType, string modId)`**
  Reads from `SynapsePromptDef` XMLs to resolve event-specific system prompts.

---

## 7. Event Tracking and Storytelling (`SynapseCoreWorldComponent.cs`)
Core maintains a rolling, serializable backlog of major colony events and natively intercepts standard storytelling events, raids, and quests for LLM processing.

- **`EnqueuePastEvent(PastEvent pastEvent)`**
  Pushes a new event to the backlog. Core automatically snapshots the colony's wealth, nutrition, and every colonist's mood/health at the exact tick the event occurred.
- **`GetRecentEvents(int count)`**
  Retrieves the most recent events, perfect for providing the LLM with chronological context of what just happened before a chat or mental break.
- **`GetMostSignificantEvents(int count)`**
  Retrieves the most significant events of the colony's lifetime, filtered to exclude art description recursion loops (events of category `"LegendaryArtCreated"` or containing `"legendary"`).
- **`AllEvents` (Property)**
  A public `IEnumerable<PastEvent>` wrapper providing a read-only stream of the live `_backlogQueue` queue, safe for modders to query history logs dynamically during active play.
- **`visitorEntryTicks` (Dictionary)**
  A serializable dictionary mapping visitor `ThingID`s to the ticks they arrived on the map.
- **`GetPopulationDensityDelegate` (Static Field)**
  A public static `System.Func<int, int>` delegate that companion mods (like `RimSynapse-Factions`) can hook to provide world map tile population calculations dynamically back to Core. Defaults to returning `0` if Factions is not loaded.
- **`LogGlobalEvent(string category, string description, string factionName = null, string settlementName = null)`**
  Logs a global event to the backlog and returns the newly generated `eventId` string.
- **`LogWorldEvent(string category, string description, string sourceFactionId, string targetFactionId = null, string parentEventId = null)`**
  Logs a geopolitical or world event (such as vassalage, rebellion, war) and returns the generated `eventId` string. Resolves faction names automatically.
- **`ResolveEvent(string eventId, string outcomeDescription, EventOutcome outcome, string parentEventId = null)`**
  Locates an active event in the backlog queue by its ID and updates its resolution text, outcome category, and resolution state.

---

## 8. Image Generation Framework (`SynapseImageClient.cs`)
Core provides a unified framework for fetching and caching dynamically generated images via Pollinations.ai without stalling Unity.

- **`GenerateAndSaveImageAsync(SynapseModHandle mod, string queryId, string subjectContext, string artStyle, Action<Texture2D, string> callback)`**
  Requests a descriptive image prompt from the LLM, appends the art style, downloads the image from Pollinations.ai in the background, saves it safely to disk tied to the active save game, and returns a Unity `Texture2D` back to the main thread. Orphaned assets are automatically cleaned up when a save file is deleted.

---

## 9. Game Tools, Scripts and the Agent (0.6 Foundation)

The agent and tool foundation has its own dedicated guide — see **Agent Scripting and Tools** in this wiki (mirrored on GitHub as `docs/COMPANION_MODS.md`). The short map:

- **`SynapseToolRegistry`** — register game tools the LLM can call (`RegisterTool`, with `keywords` for the search index and an `isMutating` flag for the autonomous-mutation gate). Handlers return JSON and never throw; failures are `{"error": ...}` payloads.
- **`SynapseScriptRunner`** — executes the model's multi-step scripts with `wait_until` conditions, validates them against the declared step schema before running, persists them across save/load, and accepts custom wait conditions via `RegisterWaitCondition`.
- **`SynapseLlmPlanner`** — the agent loop: turn and request budgets from settings, `Cancel()`, autonomous-run gating.
- **`SynapseAgentEscalation`** — programmed paths hand unexpected outcomes to the agent via `Escalate(context)`; default-off, rate-limited, never throws.
- **`SynapseAgentRunLog`** — the run inspector's backing model; every run's turns, actions, and budget state, rendered in the Script Debugger's *Agent Runs* view.
- **`SynapseTierController` / `SynapseResultStore`** — measured capability tiers with per-class operating points, and excerpt-plus-handle transport for oversized payloads.

---

## 10. Factions Mod Population Density Integration

The population density mechanics reside in `RimSynapse-Factions` and link to Core dynamically:

*   **Registration**: During mod construction, `RimSynapseFactionsMod` registers its calculator `PopulationDensityUtility.GetPopulationAtTile` with Core's `SynapseCoreWorldComponent.GetPopulationDensityDelegate`.
*   **Storyteller Impact**: Core storyteller algorithms query this delegate dynamically to scale raid frequencies (suppressed in high density) and wanderer join frequencies (boosted in high density) assembly-safely.
*   **Geodesic BFS Algorithm**: Calculates population density using step-wise biome and road link multipliers.
*   **Homestead Generator**: Procedurally spawns pre-built cabins, crops, or pasture fences if a player starts or settles on a tile containing dwellings.
*   **Tile Inspector**: Draws `"Pawn dwellings: <pop>"` in the world selection panel.
