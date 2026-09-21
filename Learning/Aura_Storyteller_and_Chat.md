# Aura Storyteller and Chat

How the 0.9 storyteller engine behaves in play. Everything on this page is dormant unless a
RimSynapse storyteller (Aura) is selected — under Cassandra, Phoebe or Randy the vanilla
storyteller runs completely unchanged.

## LLM-driven incident selection

When Aura is your storyteller, the AI chooses **which** incident fires — not when. Timing
stays on the game's own deterministic schedule (the same cadence math vanilla uses), so the
pace of your game does not depend on an AI being fast or slow. When a story beat is due, Aura
looks at the incidents that could legitimately fire right now and picks the one that fits the
colony's story best.

- Only incidents the game itself says are eligible can ever be chosen — Aura cannot conjure
  an impossible event.
- Choices respect the difficulty ceiling: a peaceful-difficulty colony cannot be
  carpet-bombed because the model felt dramatic.
- If your LLM backend is offline, slow, or out of budget, that beat falls back to the normal
  vanilla weighted roll. The colony is always fully playable without a working backend.
- Decisions land through the game's incident queue, which re-checks validity when the moment
  arrives — a decision that stopped making sense is dropped, never forced.

## Difficulty as Aura's mood

Aura reads your difficulty settings — including a custom threat-scale slider — and treats
them as her stance, from bored (peaceful) through engaged (standard) to gleeful (cranked).
Difficulty is never overridden; it is a hard budget her choices must fit inside.

## Chatting with the storyteller

A speech-bubble toggle appears in the bottom-right play settings row when Aura is active.
It opens a floating, draggable chat window where you can talk to her directly; she answers
in character, aware of your colony's situation. With a voice backend configured (OpenAI,
ElevenLabs, or local TTS) her replies are also spoken aloud.

- The chat window lives in Core: it works with or without the Conversations mod installed.
- Chat history is saved with your game.

## Talking to Aura is not a control panel

The chat and the storyteller are deliberately **two separate agents**:

- The Chat agent you talk to holds **no game-changing tools at all**. However persuasively
  it is talked into misbehaving, the worst it can do is be rude.
- Your messages never trigger storyteller decisions. Aura reads the chat log on her own
  schedule, reduced to a coarse mood signal — did the player plead for mercy, taunt her,
  thank her — never your literal words. She may heed it, ignore it, or spite it.

Begging for mercy might help. It might not. That is the point.

## The world remembers

Regional events — solar flares, toxic fallout, disease outbreaks, weather extremes — are
recorded into a save-backed world history with their outcomes. Unresolved events stay open
as threads Aura can call back to later ("the fallout that never quite ended..."). The
history is bounded, so long games do not bloat their saves.

## The persona engine: Aura is data, not code (0.10)

Aura's voice is not hardcoded — it lives in a **storyteller persona def** (an XML file,
`Defs/StorytellerPersonas/Persona_Aura.xml`). Per difficulty, the def carries a *persona
prompt* (the personality the LLM is told to write as) and hand-written *prose exemplars*
for each beat: when she **kicks off** an incident, when it resolves **well** or **badly**,
when she **calls back** to an earlier thread, and **idle** ambient lines. The exemplars
both steer the LLM's style and are the exact lines shown when no model is available — so
Aura always has something to say, backend or not. On Peaceful she is bored and teasing; on
Losing-is-Fun she is a gleeful antagonist; the whole arc is authored in the def.

Because the voice is data, **you can build your own storyteller** without writing code:
copy the def, rewrite the prompts and prose, ship it, and it loads alongside Aura. A
custom persona can even drop the fourth wall and speak diegetically. Aura is only the
reference voice.

## World events can reach your colony (0.10)

When a world-event source is active (e.g. RimSynapse WorldNews, or other mods that emit
world news), the events it publishes become material the storyteller can act on: a plague
sweeping the world can actually arrive at your colony, a distant war can wash raiders your
way. This is purely additive — it never silences or rewrites vanilla events, and with no
such source installed the storyteller behaves exactly as stock.
