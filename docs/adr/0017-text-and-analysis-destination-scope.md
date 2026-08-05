# ADR 0017 — Text and analysis are in the destination, staged out of v1

**Status:** accepted, 2026-08-05. Answers `H30`, the first gate of
[grill-plan-a.md](../grill-plan-a.md). Supersedes the flat "text is out" statements in
[plan-motif.md](../plan-motif.md) and the README.

## Context

The [2026-08-03 proposal](../proposal-2026-08-03-bidirectional-and-test-coverage.md) §5 argued for
text and manual analysis as change classes 3 and 4, with analyses as unit tests and coverage as code
coverage. Plan A had text `out`, matching the manifest's own classification of all 48 text rows as
`not-domain-reachable`.

The owner's answer to `H30` was **not** "in" or "out" but *"I want 3 before everything is said and
done — what is the cost of bringing it in now vs. later? Is it purely additive?"*, with the governing
argument:

> *The gaps in the covered words **ARE** the feeding ground for new and refined rules. They are the raw
> material… The linguistic assistant will upload texts and say "what coverage do we have", then apply a
> methodical process to increase coverage.*

**That argument is accepted, and it retires the strongest objection on the table.** The
"coverage reads 3% on day one" risk assumed coverage is a *score*. Under this framing it is a **work
queue**, and 3% coverage means 97% raw material. A backlog that starts large is not a broken metric.

The remaining question was therefore not *whether* but *what it costs to defer*, which is answerable
from the code.

## The finding: about 70% additive, and the 30% that is not is the hashed part

**Purely additive — genuinely cheap later:**

- **Manifest re-scoping.** Flipping 48 rows from `out` and writing rationale is mechanical.
- **Snapshot content.** `ObjectSnapshot` is documented additive-stable by construction: *"later stages
  can add more MultiUnicode fields, and later still other per-kind maps… without breaking existing
  snapshot consumers"* (`Snapshot/ObjectSnapshot.cs:13-18`).
- **New kinds.** Under the versioning policy drafted for `B9`, adding a `kind` to a group is
  **minor-safe**.
- **The verb vocabulary.** Analyses need no eleventh verb. `WfiAnalysis` is a real `CmObject`
  (`create`/`delete`); approval is an `Evaluations` reference to an agent singleton
  (`addRef`/`removeRef`); `Segment.Analyses` is a reference sequence (`addRef` at an index, `move`).
  The ten primitives cover it.

**Not additive — expensive later, because it is hashed:**

- **Addressing.** `CanonicalId` (`Contract/Ids/CanonicalId.cs`) is a 16-byte, GUID-derived identifier,
  and the contract states *"Canonical identity, never storage GUIDs. Fields and objects are keyed by
  canonical ID"* (`change-set-contract.md:483`). An occurrence has **no GUID** — `AnalysisOccurrence`
  is a plain C# class, not a `CmObject`. It is therefore **not addressable** in the current model.
- **The effect tuple.** `(canonicalId, field, before, after)` is the digest atom
  (`Effects/ExpectedEffect.cs:23`, `change-set-contract.md:493`). Widening it later moves every stored
  `intentDigest` and `effectDigest`.
- **Kind naming.** Kinds are versioned contract; renaming one is **major-forcing** under `B9`. Settling
  the change-class taxonomy on 473 lexicon-and-grammar rows risks a cut that text later contradicts —
  and `G27` already shows classes 3 and 4 have **zero manifest rows to test the cut against**.

## Decisions

### 1. Text and analysis are in the destination

Not a maybe, not "a later bounded context we may never build." Plan A and the README stop saying text
is *out* and start saying it is *staged*.

### 2. v1 does not deliver them; the split runs along the two-fact finding

A manual analysis is **two independent facts**: **A**, this `WfiAnalysis` is human-approved (durable
GUID, global to the project); **B**, this occurrence uses it (`Segment` + index, no durable identity).

- **Fact A is buildable now.** FieldWorks already ships the computation as
  `ParserReport.NumUserApprovedAnalysesMissing`.
- **Fact B is blocked on `H31`** — there is no durable occurrence identity anywhere in the model, and
  re-segmentation deletes `Segment` objects and re-attaches by a string-plus-position heuristic.

Tests before coverage. Coverage remains a research track until the anchor contract exists.

### ~~3. Reserve non-object targets in the canonical-id space now~~ — **WITHDRAWN 2026-08-05**

### ~~4. The effect tuple's identity slot may hold a non-object target~~ — **WITHDRAWN 2026-08-05**

**Both were unnecessary, and they rested on an error.** The original reasoning was: an occurrence has
no GUID, so it is not addressable, so the contract needs a second target kind reserved before M3
freezes the canonical form.

**Motif never addresses an occurrence.** It addresses a `Segment` and edits a field on it:

- **`Segment` is a `CmObject` with a GUID** (`MasterLCModel.xml:259`, `base="CmObject"`).
- **`Segment.Analyses` is `rel` / `card="seq"`** — an ordinary reference sequence, structurally
  identical to in-scope rows like `LexEntryRef.ComponentLexemes`.
- **`WfiAnalysis.Evaluations` is `rel` / `card="col"`** — approval is an ordinary unordered reference
  collection.
- `WfiAnalysis`, `WfiWordform`, `Text`, `StText`, `StTxtPara` are all GUID-bearing `CmObject`s.

So the effect is `(SegmentCanonicalId, analysesField, before-seq, after-seq)`. **The index lives inside
the value, not in the target.** Every text and analysis row is structurally indistinguishable from the
473 already in scope, and the existing addressing model covers all of them unchanged.

The error was conflating *"`AnalysisOccurrence` has no GUID"* with *"the thing we address has no
GUID."* `AnalysisOccurrence` is a C# convenience wrapper over `(Segment, index)`; it is not a target.

**Consequence: there is no time-sensitive decision here.** Nothing had to be reserved before M3, and
the text-side work is additive at the addressing layer too — which raises the additive fraction well
above the 70% this ADR originally estimated.

### 5. Do not settle change-class *naming* on lexicon and grammar alone

`G27`/`G28` may proceed on everything else, but the committed kind-namespace segments should either be
sketched with text's shape in view or deferred, because renaming is major-forcing.

### 6. Name the fifth drift class — segment lifecycle

Four drift classes exist and are enumerated for human review (`change-set-contract.md:554`). Text adds
a fifth: **the addressed `Segment` was split, merged, or destroyed by an intervening text edit.**

**This is far better bounded than first assumed.** liblcm has a dedicated `AnalysisAdjuster`
(`DomainImpl/AnalysisAdjuster.cs:16-60`) whose stated contract is to preserve analysis across edits:

> *"Any segment whose text is unaffected by edits should be unmodified in every other way, except that
> its begin offset should be adjusted… If the text of a particular wordform has not changed then it
> should still have the same analysis."*

So a `Segment` whose text is untouched **survives with its identity intact**. Only segments overlapping
the edited range split, merge, or vanish, and the merge/split rules are specified. The drift class is
therefore narrow, detectable with the existing `FootprintProbe`, and refusable — not a systemic
identity failure.

Adding it later is a review-model and UI change, not a contract change, so this stays **cheap**. Name
it now so the enum is not designed closed.

### 7. Coverage is presented as a work queue, not a score

The owner's framing, recorded so the eventual UI does not ship a percentage that looks like a grade.
The product artifact is *"here are the words no rule explains yet"*, ordered by value.

## Consequences

- **Text import is cheap and separable from occurrence anchoring.** `Text`, `StText`, `StTxtPara`, and
  `Segment` are all real `CmObject`s with GUIDs, so *"the assistant uploads a text"* is ordinary object
  creation that fits the contract today. Only occurrence-level analysis attachment lacks durable
  identity. **This splits `H34`**: text import can land long before anchoring is solved.
- Ten grill items previously gated on `H30` are **admitted, not deferred**: `H32a`, `H33a`, `H34`,
  `I35a`, `I35b`, `I36`, `I37`, `I39`, `I39a`, `I40`. They are no longer hypothetical, though most are
  not v1.
- `I38` stands: branch coverage by grammar feature remains a from-scratch build. Rules have no retained
  GUID and the model has no sentence concept.
- The manifest's 48 `not-domain-reachable` rows are now **provisionally** out, pending re-scoping — a
  classification to revisit, not a boundary.
- ~~Decisions 3 and 4 are cheap only if taken before M3 freezes the canonical JSON form.~~
  **Withdrawn — there is no time-sensitive element.** See the amendment above: every text and analysis
  row is an ordinary field on a GUID-bearing object, so the addressing model needs no change at all and
  M3 can freeze the canonical form without text in view.
- **`H31` needs correcting in the same direction.** "No durable occurrence identity exists" is true of
  the `AnalysisOccurrence` *class* and false of what Motif actually addresses. The `Segment` is durable,
  and `AnalysisAdjuster` explicitly preserves it when its text is unaffected.
