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
