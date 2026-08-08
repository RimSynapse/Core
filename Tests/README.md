# Behaviour tests

25 assertions that run without RimWorld, Unity, Harmony, or a game install.

```
sudo apt-get install -y mono-mcs mono-runtime   # once, on WSL or Linux
Tests/run-tests.sh
```

Exit code is zero only if every suite builds and every assertion holds.

## What Core can and cannot test this way

Most of Core needs the game: it is an HTTP client, a Harmony patch set, and a large amount of
`WorldComponent` state. None of that is pretended to be covered here.

What *is* covered is the surface other mods compile and register against. That is worth pinning
precisely because breaking it is silent — a companion mod registers into a renamed member, gets no
error, and simply never answers. The same failure class as an unbound Harmony patch.

| Suite | Covers |
|---|---|
| `ProviderTests` | the capability provider registry: documented unregistered values, warn-once, registration logging, throwing-provider containment, reflection registration, and the legacy population-density shim |
| `DeferredEventTests` | the deferred-event hold state machine: stage ordering, real-seconds timeout, stage-failure containment, invalidation-on-release, concurrent-hold cap, and once-only release |

## The three extension surfaces

Core brokers three different kinds of extension, and they are not interchangeable — picking the
wrong one is how a fourth undocumented convention gets invented:

- **Broadcast hooks** — many subscribers, push, no return value. Events on `SynapseCoreContext`
  (`OnInjectGenericContext`, `OnGlobalKnowledgeBroadcast`), `SynapseLetterContextHook` and
  `ContextAssembler`. Use one when several mods may each want to contribute something.
- **Providers** — exactly one authoritative answerer, pull, returns a value.
  `SynapseCoreProviders`. Use one when a single mod owns a question and others need the answer.
- **Deferred stages** — many subscribers, **ordered**, each able to **defer** the release of an
  event until it has finished asynchronous work. `SynapseDeferredEventPipeline` (the game-free state
  machine) behind `SynapseDeferredEvents` (the Harmony + reflection surface). Neither push nor pull:
  a stage is handed the event, may hold it, and signals when done; the event is released once every
  stage completes or the deadline fires, whichever comes first. Use one when a game event must wait
  for slow work — an LLM letter rewrite, then the spoken audio of the rewritten text — and the
  waiters are **ordered** because a later one consumes an earlier one's output. Every hold has a
  real-seconds deadline that always fires, and events not classified as holdable are never held.

The rule behind both: **the mod that introduces a mechanic owns its state and its logic.** Core does
not store other mods' data; it brokers access to it. A consumer asks Core and gets a documented
answer whether or not anybody registered.

Registration must work by reflection with no assembly reference to Core, because producers have to
build and run with Core absent. That is why every provider slot is a public static property and
nothing more exotic, and `ProviderTests` asserts the reflection path directly rather than trusting
it.

## Layout note

`Tests/` is a sibling of `Source/`, and the csproj lives in `Source/`. SDK-style projects glob
`**/*.cs` relative to the project directory, so nothing here is picked up by the mod build. Do not
move this folder under `Source/`.

The stubs are not a mock framework. `Verse.Pawn` is the smallest type that makes the callers
compile. `SynapseLogger` captures rather than writes, with the same signatures as the real one, so
the tests can assert on logging — which is half of what the provider surface promises, and the half
that would otherwise be checked by squinting at a log file.
