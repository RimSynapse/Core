# Game Tool Directory (Plain English Queries)

RimSynapse Core implements a **native tool-calling engine**. When you chat with your colonists, or when the AI storyteller evaluates the colony, the LLM does not need to guess or hoard pre-calculated data. Instead, it dynamically queries the game state using registered game tools.

The registry holds well over a hundred tools, contributed by Core and every companion mod. The model never sees the whole list at once — discovery is **two-stage**: the first prompt names only the tools a request is likely to need (chosen by a search index), and everything else is reachable from within the conversation.

---

## 0. Discovery and Utility Tools

### `list_available_tools`
- **What it does**: Searches the full tool directory by query and returns compact name-and-summary matches.
- **Plain English Prompt Examples**:
  - *"Search your tools for anything related to weather."*
  - *"What tools do you have for managing animals?"*

### `describe_tool`
- **What it does**: Returns a single tool's full description and argument schema by name.
- **Plain English Prompt Examples**:
  - *"Describe the fire_incident tool and its arguments."*

### `execute_game_tool`
- **What it does**: Runs any registered tool by name — the escape hatch for tools not listed in the current prompt. Counts as a mutating tool, so autonomous runs cannot use it to bypass the mutation gate.

### `get_stored_result`
- **What it does**: Retrieves the full text behind a `res_N` handle. Oversized tool results travel to the model as an excerpt plus a handle; this tool fetches the rest on demand.

---

## 1. Environment and Resource Queries

### `get_stockpile_details`
- **What it does**: Scans all stockpiles on the map and returns exact quantities of raw materials (steel, wood, components, silver, medicine, and food nutrition).
- **Plain English Prompt Examples**:
  - *"Check what resources we have in the stockpiles."*
  - *"Do we have enough food and medicine for winter?"*
  - *"List our current steel and component counts."*

### `get_map_environment`
- **What it does**: Returns the biome, current outdoor temperature, weather, overhead mountain cave cells count, geothermal steam vents count, active Comms Console gateway status, and a list of all turrets, doors, and generators (detailing load IDs, coordinates, and active hack/cooldown status).
- **Plain English Prompt Examples**:
  - *"Tell me about the map environment."*
  - *"Are there any geothermal vents or overhead mountain areas on our map?"*
  - *"Scan the map electronics and list all active turrets, doors, and power generators."*

---

## 2. Pawn Status Queries

### `get_colonists_profile`
- **What it does**: Returns a summary profile of all colonists, detailing their names, shooting/melee skills, traits, active weapons, and visible health/injury conditions.
- **Plain English Prompt Examples**:
  - *"Who are the colonists, and what are their primary traits?"*
  - *"Who is our best shooter, and what weapons are they holding?"*
  - *"List any colonists who are currently bleeding or injured."*

### `get_colony_moods`
- **What it does**: Scans the colony and lists mood percentages, mental break thresholds (Minor, Major, Extreme risk alerts), and active negative thoughts for all colonists.
- **Plain English Prompt Examples**:
  - *"How is everyone feeling right now?"*
  - *"Who is at risk of snapping, and what is bothering them?"*
  - *"Check if anyone has extremely low mood or traumatic thoughts."*

---

## 3. Threat and Incident Controls

### `get_active_threats`
- **What it does**: Counts active hostiles (raiders, mechanoids, mad animals), bug hives, crashed ship parts, and fires on the map.
- **Plain English Prompt Examples**:
  - *"Are there any active threats or fires on the map?"*
  - *"Scan the area for hostile raiders or mechanoids."*

### `get_available_incidents` / `fire_incident`
- **What it does**: Lists all available storyteller events and their base weights, along with modder-supplied thematic descriptions. `fire_incident` triggers the selected incident immediately (e.g. raids, cargo pods, infestations).
- **Plain English Prompt Examples**:
  - *"What storytelling events are available to fire?"*
  - *"Trigger a mechanoid raid on the colony."*
  - *"Spawn some cargo pods near the base."*

---

## 4. Colonist Possession and Breakdowns

### `possess_colonist`
- **What it does**: Directs a colonist to perform a specific action (move, attack, draft, undraft) and locks out player manual overrides. Control is automatically released on condition triggers (Damage, Downed, ExtremeMood, Hunger, Exhaustion, Bleeding, EnemyNearby, TargetReached, or Timer). Flagged as mutating.
- **Plain English Prompt Examples**:
  - *"Possess John and force him to draft and walk to the geyser at (12, 45). Release him if he takes damage."*
  - *"Order Sarah to attack the hostiles. Keep her possessed until the enemy is nearby or she gets downed."*

### `trigger_colonist_break`
- **What it does**: Invokes a context-stripped secondary LLM solver to resolve a colonist's breakdown (suicide, homicide, crisis of faith, or map-edge departure) without triggering LLM safety filters.
- **Plain English Prompt Examples**:
  - *"Initiate a dynamic breakdown on John due to his grief."*
  - *"Force a crisis of faith breakdown on Sarah to test her beliefs."*

---

## 5. Electronic Sabotage and Remote Hacking

### `control_turret`
- **What it does**: Intercepts a map turret to toggle power states, redirect targeting to attack friendly colonists (if sabotaged), or trigger a self-destruct detonation.
- **Plain English Prompt Examples**:
  - *"Shutdown Turret_MiniTurret123."*
  - *"Target the autocannon turret to fire at John."*
  - *"Activate the self-destruct sequence and detonate the heavy turret at (15, 80)!"*

### `attempt_remote_hack`
- **What it does**: Remotely breaches a target electronic object's firewall (turret, power generator, or door). Requires a powered Comms Console and a nearby Hacker Base within 8 world map tiles. Hacked turrets target friendlies for 5m; hacked generators shut down for 1h; hacked doors lock shut for 1h. Starts a 4h reboot cooldown.
- **Plain English Prompt Examples**:
  - *"Attempt a remote hack on the solar generator to shut down power."*
  - *"Breach the local firewall of Door_Autodoor456 to lock it shut."*
  - *"Remotely compromise the autocannon turret."*

### `spawn_hacker_base`
- **What it does**: Spawns an enemy transceiver outpost within 8 tiles of the colony on the world map. This base acts as the network uplink allowing remote hacks to be broadcast against your colony's defense grid.
- **Plain English Prompt Examples**:
  - *"Set up a remote hacking transceiver base nearby on the world map."*
  - *"Spawn the Hacker Base quest site within 8 tiles."*
