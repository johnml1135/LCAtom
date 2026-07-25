# HermitCrab projection

Grammar-editing consumers (Linguistic Assistant, PanGloss handoffs) reason in **HermitCrab (HC)
constructs** — phonological rewrite rules, natural classes, affix templates, inflectional affixes.
LCAtom does not. Its canonical contract is expressed against **LibLCM objects** (`Ph*`, `Mo*`,
`Fs*`), and the HC grammar those consumers run is *derived* from those objects by HCLoader, not
stored. This document fixes how HC-shaped intent relates to the canonical contract.

## Two layers

- **Canonical layer (normative).** Operations target LibLCM objects and owning/reference structure,
  exactly as in [the change-set contract](change-set-contract.md). This layer is engine-neutral: it
  can express any supported LibLCM grammar mutation whether or not HC has a matching construct, and
  it does not depend on HCLoader.
- **HC projection layer (non-normative).** A first-class authoring surface, its own C# project and
  CLI verbs, that accepts HC-shaped intent and lowers it to canonical operations. It is a versioned
  *reverse-HCLoader* owned alongside this runner, deliberately kept **out of the canonical contract,
  the intent digest, and the conformance surface** so that HCLoader drift across FieldWorks/HC
  versions changes only the adapter, never the immutable contract.

## Expansion is baseline-dependent

HC intent lowers through an explicit step:

```text
Expand(hcIntent, baseline) -> canonical change set
```

Expansion needs the model in hand: every referenced phoneme, natural class, environment, part of
speech, feature, slot, morph type, and every ordering anchor is resolved against `baseline`. HC
intent therefore cannot be statically desugared; it is an assessment-time activity that yields both
the proposed canonical operations and the diagnostics about their resolution.

## The hashed unit is the expanded operations

The canonical, hashed, applied, diffed, rebased, and conformance-gated unit is always the **expanded
LibLCM-object operations**. The HC one-liner rides along as **provenance/rationale** on the operations
it generated — retained, displayable, re-emittable for review — but is never itself hashed. The HC
one-liner is not a stable identity (it expands to different operations against different baselines);
the resolved operation bundle is.

## Fill, never frame

`Expand`, and any auto-completion, may **populate existing structure** but may never **create,
redefine, or delete the structure that others reference.**

- **Fill (expansion-eligible).** The *owned interior* of an explicitly-authored root — a rule's
  `PhSegRuleRHS` and contexts, an affix's allomorph / MSA / owned `InflectionFeatures`, the rule
  itself dropped into the existing `PhonRules` sequence. `Expand` furnishes these because they are
  mechanically determined by the authored intent.
- **Frame (explicit-only).** Any object owned by a shared pool and merely *referenced* — a
  `PhPhoneme`, `PhNaturalClass`, `PhEnvironment`, `MoStratum`, `MoInflAffixSlot`,
  `MoInflAffixTemplate`, feature-system object, `PartOfSpeech`, `MoMorphType`. These must already
  exist or be explicitly authored.

**Ownership is the test.** Owned under the authored root → fill. Owned by a shared pool and
referenced → frame. No referenceability heuristic is required.

The prohibition is symmetric across all three structural verbs:

- **frame** — create structure;
- **redraw** — modify a structural *definition* (a `PhNCFeatures.Features` spec, a slot's `Optional`
  flag, a feature's value set);
- **demolish** — delete structure (including cleaning up a now-empty room).

All three are explicit-only, always. Only fill/unfill of content within existing structure is
expansion-eligible. Emptiness is a diagnostic; demolition is the author's call.

Note that "explicit" means *present as a named, reviewable operation in the change set* — not
"typed by a human." An Linguistic-Assistant-proposed affix, class, or rule is explicit because it
lands as a reviewable operation, not a hidden side effect. Every affix, rule, class, slot, and
feature is added only because it was named; `Expand` frames nothing.

## Missing referents fail closed

A reference that resolves to neither (a) an object in the baseline, nor (b) an explicitly-authored
`create` earlier in the same change set is **always a hard error**. `Expand` never synthesizes a
referent. The proposer must rewrite the change set to include an explicit create, sequenced first;
the contract already permits later operations to reference earlier-created entity IDs, and the whole
change set still commits atomically.

The hard error must be **actionable**, not a bare rejection: it names the missing referent's kind,
the resolution key searched for (for example natural-class name `V`), and the reference site
(operation and field). That is what lets a proposer deterministically prepend the correct explicit
create and re-assess.

## Rebase versus re-projection

Two distinct operations move an authored edit onto a different baseline:

- **Rebase** operates on the hashed canonical operations, within one project lineage where resolved
  GUIDs remain valid. This is the existing mechanical behavior in
  [conflict and rebase semantics](conflicts-and-rebase.md).
- **Re-projection** re-runs `Expand(hcIntent, newBaseline)` from retained HC intent, producing a
  **new change set with a new digest**. It is required for applying an edit to an unrelated project,
  where "the same" phoneme or class has a different GUID and operation replay would correctly match
  nothing.

Re-projection is explicit and first-class; it is never a silent re-resolution of an existing
identity. It resolves by *authored linguistic key* — the HC intent names the class, phoneme, or
category — which is explicit authored intent supplied by the caller, not runner-side fuzzy matching.
It obeys fill-never-frame: re-projection fails closed when the target baseline lacks a referenced
structure.

Curated libraries of reusable intents, parameterized templates, and portable placement predicates
are **out of scope** for this repository. Storing and curating intents is caller-owned, consistent
with the [storage boundary](architecture.md#storage-boundary).

## Diagnostics and presentation

HC-level work gets **no bespoke error model**. It flows through the same typed diagnostics
(info / warning / conflict / hard error with stable codes) and the same caller-owned Application
Policy as the canonical layer.

Assessment groups the resulting operations for display along two axes, both **recomputable
metadata that stays out of the intent digest**:

- **provenance** — which HC one-liner produced each operation;
- **ownership role** — structure-establishing explicit creates ("these frame") versus additions to
  existing structure ("these fill").

## Coverage-manifest unification

Frame-vs-fill is a classification axis over the `Ph*`/`Mo*`/`Fs*` surface. Every creatable/mutable
member carries one tag, and that single tag drives both the fail-closed behavior above and the
[model coverage manifest](architecture.md#model-coverage). The two concerns share one classification.

("Projection" throughout this document is the HermitCrab authoring surface — the `Expand`/reverse-HCLoader
layer — and is unrelated to the semantic-snapshot `projectionVersion` in
[versioning](architecture.md#projection-stability), which versions the canonical projection of the
LibLCM model. The two are distinct concepts that happen to share the word.)

## Lockstep with HermitCrab and the FieldWorks grammar creator

**This is reverse engineering, not design.** LCAtom's grammar API must be **exactly the set of LibLCM
inputs that FieldWorks' `HCLoader` consumes** — nothing less, or the user cannot control the projected
grammar; nothing pointless, or we offer controls wired to nothing. Two normative requirements follow:

1. **100% lockstep with HermitCrab** (`../machine`, `src/SIL.Machine.Morphology.HermitCrab`). Every
   construct the HC `Language` model can represent must be authorable through LCAtom in a friendly way.
   HC's model is the coverage yardstick ([ADR 0010](adr/0010-hermitcrab-experimentation-is-the-primary-purpose.md)).
2. **100% lockstep with the grammar creator** (`FieldWorks/Src/LexText/ParserCore/HCLoader.cs`). The
   authoritative map is *HC construct ← the LibLCM fields HCLoader actually reads*. That map — not the
   LibLCM model's apparent shape — defines the grammar write-surface, its ordering dependencies, its
   reliance on virtual/synthetic properties, and its silent-skip failure modes. Both are versioned
   dependencies: when either changes, the map is re-derived and the API re-checked.

### Strata — what projects actually contain

Empirically, in every project sampled (including a dedicated HermitCrab project): **zero `MoStratum`
objects**, and `MoMorphologicalData.ParserParameters` contains only `<XAmple>` tuning
(`MaxNulls`, `MaxPrefixes`, …) with **no `<HC>` element and no `<Strata>` section**. So every real
project runs on the three strata `HCLoader` hardcodes — `Morphology`, `Clitics`, `Surface`.

`HCLoader` *can* build more, from `<HC><Strata>…</Strata></HC>` in that text field, binding rules to
strata by **name-string matching**; and the model's `MoStratum` / `StratumRA` members are read nowhere
in FieldWorks except three presence checks. But since nobody writes either channel, stratum
configuration is **not a v1 requirement** — the requirement is only that LCAtom never disturbs the
default three. If stratum authoring is ever wanted, the reachable channel is `ParserParameters`, and
its name-string binding makes a rule rename also a stratum edit.

## Authoring input and round-trip

The projection has two directions, both owned in `SIL.LCAtom.HermitCrab`:

- **Forward** — project a LibLCM state to HermitCrab grammar XML by wrapping FieldWorks' battle-tested
  `HCLoader` (harvest, not rebuild). It runs against the baseline *or* a hypothetical post-apply state
  (assess → project the would-be grammar) — the review-first thesis applied to grammar: produce the HC
  grammar for a proposed change, run an external tool (PanGloss/HermitCrab) over it, review the report,
  then apply. On-demand, whole-grammar, result cached as a provenance-stamped attachment.
- **Reverse (`Expand`)** — lower a single high-level HC-intent command into the LibLCM object-ops that
  would produce it. The input is a **structured command** an agent or UI emits directly, not textual
  notation — there is no rule-language parser to build; LCAtom is not a compiler. The mapping is the
  **inverse of `HCLoader`**: `HCLoader` says which LibLCM objects each HC construct corresponds to, and
  `Expand` emits the create/update/delete ops for them under fill-never-frame.

Forward projection is reverse `Expand`'s **round-trip oracle**: `Expand(intent)` → ops → apply →
project the result back to HC → compare against the intent. The forward direction is built first, as
the harness that validates the reverse.

## Worked decompositions

Each HC one-liner expands to a coherent bundle of canonical operations against a baseline. Property
names and cardinalities are from `MasterLCModel.xml`.

- **`add-natural-class V = {a e i o u}`** → create `PhNCSegments` under `PhPhonData.NaturalClasses`;
  set `Name`/`Abbreviation`; add-reference each of five existing `PhPhoneme`s to `Segments` (`col`).
  Fails closed if any phoneme is absent.
- **`add-rule k -> g / V _ V, after R7`** → `sequence/insert` a `PhRegularRule` into the ordered
  `PhPhonData.PhonRules` at `{after: R7}`; set `Direction`; create the owned `PhSegRuleRHS`,
  `StrucDesc`, `StrucChange`, and left/right `PhSimpleContext` interior; reference existing phonemes
  and the natural class `V`. Order is semantic; the insert is where the round-trip invariant and
  rebase are exercised.
- **`add-suffix -s [num:pl] to Noun 'Number' slot`** → create a `LexEntry`, `MoAffixAllomorph`
  (form, morph-type ref), and `MoInflAffMsa` (POS ref, owned `InflectionFeatures`, slot ref),
  spanning lexicon + morphosyntax + ordered morphotactics. The Noun POS, the `num`/`pl` feature, the
  suffix morph type, and the Number slot are frames: each must already exist or be explicitly created
  first.
