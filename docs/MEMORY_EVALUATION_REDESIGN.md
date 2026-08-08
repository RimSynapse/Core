# Memory & Psychology Evaluation Redesign

**Status:** Design — approved for staged implementation
**Scope:** RimSynapse **Core** (memory model, decay, consolidation, context assembly) and **Psychology** (evaluation prompt, trait engine). Cross-repo.
**Save compatibility:** Required. All changes must load pre-existing saves without data loss and migrate silently.

---

## 1. Motivation

The psychology engine produces incorrect and jarring personality changes because it lacks the *dimensions* to reason about what actually happened to a pawn. The memory layer that feeds it cannot distinguish important events from noise, recent from lifetime, or reinforced from stale.

### Observed failure (the canonical case)
A colonist attacked a **wrecked pod car** (a map object, not a creature) ~100 times over a day. The result:
1. The daily evaluation read the repetitive `AttackStatic` activity as overwhelming violence and **added `Bloodlust`**.
2. It **removed `Iron-Willed`** — a trait that should have *resisted* personality change, not been a casualty of it.
3. The evaluation's own prose said personality shifts were **"unlikely at this time"**, yet it applied both changes anyway.

### Root causes (verified in code)
| # | Problem | Location |
|---|---------|----------|
| R1 | The evaluator is handed the pawn's **entire** memory bank, labelled "Recent Memories." Cannot tell today from a year ago. | `Psychology/…/SynapsePsychologyOpportunistic.cs:210` passes `coreComp.memories`; consumed at `SynapsePsychologyEvaluation.cs:105-107` |
| R2 | No salience/aggregation. Repetitive identical actions aren't collapsed; **object targets aren't distinguished from living targets**. | `Core/…/SynapseCorePawnComp.cs` job tracking (`TrackJobRare`, job-activity summary ~L470) |
| R3 | No **trait resistance** dimension. Existing traits are never fed as inertia, and no trait is protected from removal. | `Psychology/…/SynapsePsychologyEvaluation.cs` prompt; `…_Parsing.cs:112 ApplyTraitChanges` |
| R4 | Structured `TraitChanges` output is **not gated** by the model's own likelihood assessment; changes are **instant one-day flips**. | `…_Parsing.cs:112-139` |
| R5 | Weight lifecycle broken: scale mismatch (Core model = 0–1, Psychology prompt = 0.1–5.0), `BumpMemory` **clamps to 1.0 so reinforcing a strong memory crushes it**, no weight-based short→long promotion, UI splits by `memoryType` string not weight. | `Core WeightedMemory.cs`; `Psychology SynapsePsychologyMemory.cs:62`; `Psychology Dialog_PawnPsychology_Tabs.cs:200` |
| R6 | Decay too slow for assigned weights (0.05/day on weights up to 1.5 ⇒ ~30 in-game days to prune a minor event). | `Core SynapseCorePawnComp.cs DoMemoryDecay ~L189` |

---

## 2. Design principles

1. **Weight is the single source of truth.** Short-term vs long-term is an *emergent property of weight*, not a hardcoded `memoryType` list.
2. **Relational salience ("graph reference").** A memory's importance is boosted by its connections to other significant memories/entities. Idle chit-chat decays within a day — **unless** it links to something significant (e.g. the other pawn later dies → "the last thing I said to them" is boosted over the consolidation line).
3. **Gradual pressure, not instant flips.** Personality changes accumulate over multiple days and are moderated by trait resistance, producing narrative like *"over the last 4 days, Josema is trending toward Bloodlust unless morale recovers."*
4. **Reason about the right signal.** Violence against objects ≠ violence against the living. The engine must be given target-type and deduplicated activity.
5. **Data-driven & tunable.** Base weights, decay, and thresholds are exposed via XML defs (extending the existing `SynapseWeightDef` pattern) and mod settings — no recompile to rebalance.
6. **Save-safe & additive.** New fields use `Scribe_Values.Look(ref x, "name", default)`; migrations run in `PostLoadInit`, following the existing `MigrateTickIfNeeded()` precedent.

---

## 3. Existing infrastructure we build on (do not reinvent)

- **`WeightedMemory`** (`Core/Source/Models/WeightedMemory.cs`): already has `weight`, `baseWeight`, `decayRate`, `timesReferenced`, `isLongTerm`, `tags`, `subjectPawnIds`, `absTick`, and a `MigrateTickIfNeeded()` PostLoad hook.
- **Memory indexes** (`memoriesByTag`, `memoriesByPawnId` in `SynapseCorePawnComp`): the substrate for relational queries — memories sharing a `subjectPawnId` or tag are already O(1) linkable.
- **`SynapseWeightDef`** + `Defs/SynapseWeights/Weights_Default.xml`: XML-driven slot weighting. We add a parallel `SynapseMemoryClassDef` for memory-type base weights/decay.
- **`NarrativeThread`** (`SynapseCoreWorldComponent`): precedent for keyword/category/weight aggregation with its own decay — model for how consolidation clusters read.
- **Migration precedent:** `absTick` was added with default `0` and back-filled in `PostExposeData`'s `PostLoadInit` branch. Every new field below follows this exact pattern.

---

## 4. Data model changes

### 4.1 `WeightedMemory` (Core) — new fields
```csharp
public long   lastReferencedTick = 0;   // when last bumped/surfaced; drives recency
public float  salience = 0f;             // cached relational score (recomputed, see §5.4)
public string targetKind = null;         // "humanlike" | "animal" | "object" | "self" | null
public List<string> linkedMemoryIds = new(); // explicit graph edges (optional; tags/pawnIds are implicit edges)
public string memId = null;              // stable id so links survive save/load
```
- All scribed with defaults; absent in old saves ⇒ default, then initialized in migration (§8).
- `memId`: assign on creation (`AddMemory`) if null; back-fill deterministically on load.

### 4.2 `SynapseCorePawnComp` (Core) — new field
```csharp
// Per candidate-trait accumulated evidence toward/away from a personality shift.
public Dictionary<string, TraitPressure> traitPressures = new();
```
`TraitPressure` (new `IExposable` model): `float pressure; string direction ("add"|"remove"); long lastUpdatedTick; float peak;`
- Decays toward 0 when no evidence arrives (so a bad day doesn't linger forever).

### 4.3 New Defs (Core, XML-tunable)
- **`SynapseMemoryClassDef`** — per `memoryType`: `baseWeight`, `decayRate`, `bornLongTerm` (bool), `consolidationContribution`. Ships a `MemoryClasses_Default.xml`. Falls back to sane defaults for unknown types.
- Extend settings (see §7) for global multipliers and thresholds.

### 4.4 Scale normalization (R5)
Canonical scale is **0.0–1.0** everywhere.
- Psychology's opportunistic prompt (`SynapsePsychologyOpportunistic.cs:44-45`) rewritten to instruct 0.0–1.0 tiers (minor 0.05–0.25, standard 0.25–0.5, major 0.5–0.8, defining 0.8–1.0).
- Creation sites currently using 2.0/3.0/4.0/10.0 (backstory, therapy, kills) are rescaled to ≤1.0 and instead set `isLongTerm=true` / `bornLongTerm` where "permanent" was the intent (decayRate 0 already).
- Legacy saves: memories with `weight > 1.0` are detected on load and divided down (see §8).

---

## 5. Algorithms

### 5.1 Ingestion: aggregate repetitive actions (R2)
Before an activity becomes memory/context:
- Collapse consecutive identical `jobDefName` intervals into one aggregated entry ("spent ~6h attacking a wrecked vehicle") rather than N entries. `recentJobs` accumulation already groups by def; extend the **summary** path to emit a single deduped line and a `count`.
- The job-activity summary fed to context must include **target kind** when the job is combat (`AttackStatic`/`AttackMelee`): resolve `job.targetA` → humanlike / animal / object. Object-only violence is explicitly labelled non-lethal.

### 5.2 Recent vs lifetime split (R1) — *Stage 0, highest leverage*
`QueueDailyPsychologyReview` receives two distinct inputs:
- **Today's events**: memories with `absTick` within the last day **plus** the aggregated activity summary. Labelled "Today."
- **Long-term burdens**: `GetTopMemoryBurdens(...)` over consolidated/long-term memories only. Labelled "Long-standing psychological burdens (weighted)."

Fix the caller (`SynapsePsychologyOpportunistic.cs:210`) to stop passing the full `coreComp.memories` as `dailyEvents`.

### 5.3 Decay & fast pruning (R6)
- `DoMemoryDecay` unchanged in shape but decay rates come from `SynapseMemoryClassDef` × `memoryDecayMultiplier` setting; short-term/idle classes get high decay (≥0.5/day ⇒ gone within ~2 days).
- Prune at `weight <= 0` (already present). `isLongTerm` still skips decay.

### 5.4 Relational salience & consolidation (R5, principle 2) — the "graph reference"
Once per day (in the existing daily decay pass), for each **non-long-term** memory compute:

```
salience = weight
         + Σ (linkWeight(other) )  for each other memory sharing a subjectPawnId or tag
         + entitySignificanceBonus (subject pawn died / is loved / is a rival / major event tag)
```
- `linkWeight(other)` uses the existing `memoriesByPawnId` / `memoriesByTag` indexes — O(neighbors), not O(n²).
- **Consolidation:** if `salience >= consolidationThreshold` (setting, default 1.0) **OR** `timesReferenced >= referenceThreshold` (default 3) ⇒ set `isLongTerm = true` (registered). This is exactly "once the collective graph of that memory exceeds a limit, it becomes registered."
- **Retroactive boost:** when a significant event is added (e.g. a death with that pawn's id), neighbors' salience is recomputed next pass, so a chit-chat linked to a now-dead pawn can cross the line the day the death lands. Otherwise unlinked chit-chat has salience ≈ its own small weight and decays out.

### 5.5 `BumpMemory` fix (R5)
Raise toward the memory's own ceiling instead of a flat 1.0:
```csharp
match.weight = Math.Min(1.0f, match.weight + bumpAmount); // ceiling is 1.0 in the new scale — correct
match.lastReferencedTick = now;
match.timesReferenced++;
```
Under the normalized 0–1 scale the old `Math.Min(1.0, …)` is no longer destructive. (The bug was purely the scale mismatch.) Also bump on **surfacing into context**, not only on LLM back-reference, so reinforcement reflects genuine salience.

### 5.6 Trait resistance (R3)
- A `SynapseTraitResistanceDef` (or reuse existing trait extension in Psychology `Source/Extensions/SynapseTraitExtension.cs`) maps traits → resistance factor and protected status:
  - `Iron-Willed`, `Steadfast`, `Nerves` (Nerves of steel), `Psychopath`, `Too smart`, `Sanguine`, `Undgrateful`, etc. get resistance > 0.
  - Resistance **raises the pressure threshold** required to change *any* trait and **protects the resisting trait itself** from removal unless overwhelming, sustained pressure.
- Current traits + their resistance are fed into the evaluation prompt as an explicit dimension, and enforced in code in the accumulator (§5.7) — belt and suspenders, so a hallucinated change still can't bypass resistance.

### 5.7 Multi-day TraitPressure accumulator (R4, principle 3)
The evaluation no longer applies trait changes directly. Instead:
1. The LLM returns a **`PersonalityShiftAssessment`**: per candidate trait `{trait, direction, dailyPressure (0–1), rationale}` — evidence for *today only*.
2. Code folds each into `comp.traitPressures[trait]`:
   `pressure += dailyPressure × (1 − resistanceFactor)`, decaying existing pressure toward 0 first.
3. A trait change fires **only** when `pressure >= shiftThreshold` (setting) sustained; protected traits require a higher bar. On fire: apply via existing `ApplyTraitDirective`, then reset that pressure entry.
4. **Consistency gate:** if the model's narrative likelihood is "unlikely" (low overall), `dailyPressure` is floored near 0 — the "unlikely-but-still-flipped" contradiction becomes impossible.
5. The accumulated state produces the desired UI/letter narrative: *"Behavior over the last N days suggests Josema is trending toward Bloodlust; a personality shift is likely if morale does not recover."*

### 5.8 Context selection by tier (R5)
- `ContextAssembler_DataBuilders` memory slot selects **long-term/high-salience first**, then fills remaining budget with recent high-weight short-term — instead of a flat top-5-by-weight (`:371-372`).
- UI (`Dialog_PawnPsychology_Tabs.cs:200`) splits Short-term vs Long-term by `isLongTerm`/salience tier, not the `memoryType` string blocklist.

---

## 6. Evaluation prompt & JSON schema changes (Psychology)

New/changed inputs to the daily eval user message:
- **Today's activity** (deduped, with target kinds) — separate from long-term burdens.
- **Current traits + resistance** and **which traits are protected**.
- **Lifetime violence against living creatures** (kills of humanlikes/animals) stated explicitly, so object-bashing cannot masquerade as bloodlust.
- **Accumulated trait pressures so far** (so the model sees the trajectory, not just today).

New/changed outputs (replaces raw `TraitChanges`):
```json
{
  "…9 clinical categories + Summary…": "…",
  "AbandonmentRiskScore": 0,
  "PersonalityShiftLikelihood": "none|low|moderate|high",
  "PersonalityShiftAssessment": [
    { "trait": "Bloodlust", "direction": "add", "dailyPressure": 0.15,
      "rationale": "Repeated combat, but only against an inert vehicle — weak signal." }
  ],
  "SocialAdjustments": { "Name": { "trustOffset": 2.5, "familiarityOffset": 1.0 } }
}
```
- Guardrails (also from earlier findings): validate trait defNames against a **whitelist**, clamp `dailyPressure`, clamp social offsets to prompted ranges *before* applying, per-eval caps, cooldown between actual trait changes.

---

## 7. Settings & data-driven knobs

Add to `RimSynapsePsychologySettings` (sliders in the mod settings UI) and/or Core defs:
- `shiftThreshold` (trait pressure needed to fire) · `shiftPressureDecay`
- `consolidationThreshold` · `referenceThreshold`
- `shortTermDecayRate` global multiplier (extends existing `memoryDecayMultiplier`)
- `abandonmentThreshold` (currently hardcoded `>90`)
- `suicideDamageMultiplier` (currently hardcoded `5.0f`)
- `opinionTrustBlend` (currently hardcoded 50/50)
- `evalCadence` (nightly vs every N days) — also trims token cost
- Memory-class base weights/decay via `MemoryClasses_Default.xml` (modder-overridable by XPath).

---

## 8. Save-game migration (mandatory)

All migration runs in `SynapseCorePawnComp.PostExposeData` under `LoadSaveMode.PostLoadInit`, alongside the existing `MigrateTickIfNeeded()` loop. Strategy per change:

| Change | Old save behavior | Migration |
|--------|-------------------|-----------|
| New `WeightedMemory` fields (`lastReferencedTick`, `salience`, `targetKind`, `linkedMemoryIds`, `memId`) | Node absent ⇒ Scribe default | Assign `memId` if null (deterministic from summary+absTick); set `lastReferencedTick = absTick`; recompute `salience` on first daily pass; `targetKind` left null (unknown, treated as non-combat) |
| **Weight rescale 0.1–5.0 → 0–1** | Weights may exceed 1.0 | On load, if any memory `weight > 1.0` OR `baseWeight > 1.0`, treat save as legacy and divide by the detected max tier (5.0) → clamp 0–1. Guard with a one-shot `memoryScaleVersion` int on the comp (default 0 = legacy, set to current after migrating) so it never double-applies |
| `traitPressures` dict (comp) | Absent ⇒ empty dict | Start empty; no existing trait changes are retroactively reconstructed |
| `SynapseMemoryClassDef` decay/weights | N/A (new defs) | Unknown/legacy `memoryType`s fall back to defaults; nothing breaks |
| `isLongTerm` promotion | Old memories all `false` unless born long-term | First consolidation pass promotes qualifying memories naturally |

**Versioning:** add `Scribe_Values.Look(ref memoryScaleVersion, "memoryScaleVersion", 0)` on the comp. All one-shot migrations key off it. This is the safe, idempotent pattern.

**Non-negotiables:** never drop the `synapseMemories` scribe key or rename existing memory field keys (would orphan old data). Only add.

---

## 9. Staged rollout

Each stage compiles, ships, and is testable independently. Later stages assume earlier ones.

- **Stage 0 — Stop feeding the whole bank + activity sanity (fixes the canonical bug).**
  §5.1 aggregation + target-kind, §5.2 recent/lifetime split, prompt relabel, lifetime-living-kills input. *Mostly Psychology + a Core helper for target kind & deduped summary.*
  **Test:** the pod-car scenario yields low/`none` shift likelihood and no trait change.

- **Stage 1 — Weight lifecycle & relational consolidation.** §4.1/4.4 model + scale normalization + migration (§8), §5.3 decay-by-class, §5.4 salience/consolidation, §5.5 BumpMemory, §5.8 tier selection, UI split. *Core-heavy.*
  **Test:** idle chit-chat prunes within ~1–2 days; a chit-chat linked to a subsequently-dead pawn is promoted to long-term; old save loads with weights rescaled once.

- **Stage 2 — Evaluation dimensions & trait engine.** §5.6 resistance, §5.7 pressure accumulator, §6 schema + guardrails.
  **Test:** Iron-Willed resists and is protected; a real multi-day violent streak against the living accumulates pressure and fires Bloodlust with an escalating narrative; "unlikely" never flips a trait same-day.

- **Stage 3 — Settings/knobs & cost trim.** §7 sliders + XML, trim 9-category prompt / cadence.
  **Test:** sliders move behavior; token/call volume drops.

---

## 10. Cross-repo touch points

| Area | Repo | Files (indicative) |
|------|------|--------------------|
| Memory model, new fields, migration, decay, salience, consolidation, indexes, BumpMemory, context tier selection, new Defs | **Core** | `Models/WeightedMemory.cs`, `Comps/SynapseCorePawnComp.cs`, `Internal/ContextAssembler_DataBuilders.cs`, `Defs/SynapseWeightDef.cs`(+new `SynapseMemoryClassDef`), new `Defs/MemoryClasses_Default.xml` |
| Eval input split, prompt/schema, trait resistance + pressure application, guardrails, settings, UI split | **Psychology** | `API/SynapsePsychologyEvaluation.cs`, `…_Parsing.cs`, `API/SynapsePsychologyOpportunistic.cs`, `API/SynapsePsychologyMemory.cs`, `Extensions/SynapseTraitExtension.cs`, `Settings/…`, `UI/Dialog_PawnPsychology_Tabs.cs` |

## 11. Open questions
- Exact resistance list & factors per trait (needs a first pass, then playtest tuning).
- Should consolidation also *demote* (long→short) if links resolve/decay? Proposal: no auto-demotion in v1; only prune at weight 0 for non-long-term. Revisit.
- Are the inert AI-break and discarded-therapy-summary features (found separately) in scope for this effort or tracked as separate issues? *Recommend separate.*
