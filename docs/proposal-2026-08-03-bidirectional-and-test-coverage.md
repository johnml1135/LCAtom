# Proposal — bidirectional change encoding, change classes, and text as test coverage

**Status: owner proposal, recorded 2026-08-03 to be challenged. Not a decision, not a plan.**
Challenges and the research needed to settle them are queued as `F*`–`J*` in
[grill-plan-a.md](grill-plan-a.md). Where this proposal contradicts a committed decision, that is
called out below rather than quietly reconciled.

---

## 1. Bidirectional encoding is first-class

The design must not only turn JSON into LibLCM changes. It must also **compare two LibLCM projects and
emit the list of JSON operations that transforms one into the other.**

That single capability is what makes four things possible:

- two projects can be merged;
- someone can take a change set, edit it, and re-encode it;
- **someone can draft a change set from inside FieldWorks, by editing normally**;
- a reviewer can edit a proposal's live model and have the result re-encoded.

Some things cannot be recovered from a state delta, and the covered surface may have to be scoped
accordingly — but the capability is foundational, not an add-on.

**Partly already specified.** `change-set-contract.md` §"Mechanical diff" already defines exact-identity
two-way diff, common-ancestor three-way comparison producing a `ThreeWayAssessment`, and an
O(n log n) LIS algorithm for minimum ordered-sequence edits with frozen tie-breaking. **None of it is
implemented.** What this proposal changes is its *priority*: from a downstream feature to a
foundation.

## 2. Change classes

Proposed taxonomy, offered to be challenged:

| Class | Content | Manifest correspondence |
| --- | --- | --- |
| **1** | Lexical entries — add, edit, delete | `set\|clear` (220 rows), `create\|delete` (99) |
| **2** | Grammar rules — add, edit, delete | same verbs, grammar constructs |
| **3** | **Texts** — *"yes, we need to add them"* | **currently `out` / `not-domain-reachable`** |
| **4** | Manual text analysis of individual words in context | **currently `out`** |
| **5** | Links and relationships between items | `addRef\|removeRef` (34 rows) |
| **6** | *Open* — changing references, moving a parent? | `create\|delete\|move\|reparent` (32), `addRef\|removeRef\|move` (27) |

The 473 in-scope manifest rows break down as 412 `unordered`, 56 `positional`, 3
`index-as-identity`, 2 `feeding`.

**Candidates for class 6 and beyond**, from the ten primitive verbs and the manifest, offered as the
answer to *"what am I missing"*:

- **Ordering** — `move` within a sequence. 56 `positional` plus 2 `feeding` rows. Distinct from
  reference edits because placement is identity-relative and, for phonological rules, order *is*
  meaning.
- **Reparenting** — `reparent`, cross-owner move. 32 rows. The proposal's own "moving a parent?".
- **Schema and metadata** — custom field definition, writing systems. These are **not data changes**:
  per [ADR 0005](adr/0005-schema-operations-non-undoable-uow.md) they run in a separate non-undoable
  unit of work and are one-way. A failed data phase leaves a defined-but-empty field.
- **Shared vocabulary** — possibility lists (parts of speech, semantic domains, morph types). Edited
  rarely, referenced everywhere; a change here has project-wide blast radius unlike a lexical edit.
- **Compound graph operations** — `merge` two entities, `replace` (subclass convert with reference
  redirect, or GUID change). Always discovered-footprint, and — see §7 — the operations least likely
  to survive a round trip through diff.

## 3. Editing a change set

Both Avalonia and the CLI need the ability to **duplicate and remove operations from a change set**.
Expected to be trivial; recorded because it is required and currently absent.

Consequence to check: removing an operation moves the **intent digest** while the `proposalId` stays
frozen, and may orphan a dependent operation or break a `requires` edge.

## 4. Two authoring paths, one contract

**The higher-level semantic layer is not needed when comparing two LibLCM models.** A human creating
or reviewing a change works directly in the model; the diff emits primitives.

The higher layer is meaningful **only for AI and the CLI** — batch reads, batch updates, creating
multiple rules at once. Those need both:

- an **API command** form for creating or editing a change, and
- an **at-rest JSON** form — noting that *a batch update is a different thing once the data has
  changed*, so what is stored must be the resolved operations, not the unresolved query.

This matches the Layer 0 / Layer 1 split already accepted in
[ADR 0009](adr/0009-layered-api-primitives-and-composers.md): ten primitive verbs are the permanent
contract, and composers author change sets built entirely from them, adding zero permanent contract
surface. **The proposal supplies the missing rationale for why the split exists** — Layer 0 is the
diff's output vocabulary, Layer 1 is the agent's input vocabulary.

## 5. Texts and manual analysis as tests and coverage

The model:

| Software | Motif |
| --- | --- |
| Failing test | A manual analysis that is **not** in the set of valid analyses PanGloss produces |
| Test suite | Manual analyses of words in context, contributed by users |
| Coverage — statement | Every word in a text has one authoritative analysis: either manual, or a single analysis produced by PanGloss |
| Coverage — branch | Every grammar feature has at least one word that parses in a sentence |

"Needed interactions" — tables, views, and similar — need spelling out, and some of those belong in
coverage too.

**Contributions are donated tests.** A new set of analyses from another user in a text is them
donating tests. If analysis is added or clarified *after* a rule is added — which users should be
encouraged to do, with thresholds or rules for coverage and passing — it must enter the change set.

**Required AI capabilities:**

- *"I found this valid word and analysis — I want to add it."*
- *"This rule change adds a second valid analysis for these 5 instances of this word — I need to
  disambiguate."*
- *"There is no sentence with a word exercising this rule — let's find one and add it, and make sure
  the unit tests come with it."*

## 6. The eight components

| Component | Position |
| --- | --- |
| Changes | lexemes, rules, relationships |
| Unit tests | manual analysis of words in context |
| Code coverage | every word in a text has one valid analysis |
| Data model | live LibLCM, warmed, manually editable |
| Creation and editing | AI through a higher-level CLI API; humans in FieldWorks as always |
| Visualization | summaries, but mostly through FieldWorks |
| Rebasing | fingerprints revalidate that nothing touched has changed |
| Portability | duplicate, zip, transport, split a change set |

Visualization in FieldWorks — diffs, and editing a proposal's live LibLCM so it can be re-encoded —
plus word-analysis interaction, is acknowledged as a large unspecified body of work.

---

## 7. Where this collides with committed decisions

Recorded plainly so the challenges are honest. Each has a grill item.

**a. Text is currently out of scope, and this reverses that.** The Manifest marks `Segment` (10 rows),
`WfiAnalysis` (9), `WfiMorphBundle` (5), `WfiWordform` (4), `Text` (6), `CmAgent` (7), and `StTxtPara`
(7) as `out` with reason `not-domain-reachable`. [Plan A](plan-motif.md) and the README both state
text is out. Classes 3 and 4 are therefore **a manifest re-scoping and a new bounded context**, not
additional volume in an existing one — and the accepted synthesis finding that "text mutation needs
its own bounded context" still stands.

**b. Occurrence identity is positional and this is the known hard problem.** Occurrence identity
derives from a segment plus an index, so text edits can move or invalidate it. A text-specific anchor
contract was already named as a prerequisite before text becomes collaboratively authoritative.
Classes 3 and 4 depend entirely on solving it.

**c. A diff-derived Proposal has different provenance from an authored one.** ADR 0006 decision 1 is
*read back, do not replay*: effects are captured by before/after snapshot diff because replaying
intended writes would omit the engine's own consequences. A diff-derived Proposal's operations are
*derived from* an observed delta, so its effects are equal to that delta by construction. That is not
a contradiction, but it is a second provenance class that the review model does not currently
distinguish.

**d. Diff must be canonical or digests will not match.** Many operation sequences produce the same
state. If two people make the same edit and the diff emits different-but-equivalent operation lists,
the intent digests differ and content-equality queries break. The contract already anticipates part of
this — "freeze LIS tie-breaking, operation emission order, and anchor choice in normative fixtures" —
but canonicality is currently specified only for ordered sequences, not for the whole diff.

**e. Some operations cannot survive the round trip.** `merge`, `replace`, and `reparent` are
discovered-footprint by design. From a state delta alone, "merged A into B" is indistinguishable from
"deleted A and edited B", and "reparented X" from "deleted X and created X elsewhere". The proposal
anticipates this — *"some things can't be easily undone"* — and the question is where the covered
surface is drawn, and whether an uncovered edit is refused loudly or silently degraded to
delete-plus-create.

**f. Coverage starts near zero.** "Every word in a text has one authoritative analysis" is a target,
not a baseline; most text in most projects is unanalyzed. A coverage metric that reads 3% on day one
needs a defined ramp, or it will be ignored.

**g. "One authoritative analysis" may be linguistically wrong.** Genuine ambiguity exists. Forcing one
analysis per occurrence may encode a false certainty, and the proposal's own disambiguation
requirement implies ambiguity is a real state rather than a failure.

## 8. What is already built and reusable

- Ten primitive verbs, closed and versioned; the Layer 0 / Layer 1 split.
- RFC 8785 intent digest, frozen `proposalId`, content-addressed proposal store.
- Effect model read back from the engine, effect digest, four drift classes.
- **Fingerprint rebasing already exists**: `FootprintProbe.ComputeCurrentFootprintDigest` plus
  `BoundDryRunAnchor`, and apply refuses on drift. The proposal's "rebasing using fingerprints" is
  built for one operation kind.
- **Portability is nearly free**: `ProposalStore` is already content-addressed objects plus manifests,
  and `proposalId` is frozen while the intent digest moves — so duplicate, zip, transport, and split
  are mostly packaging, with the `requires` DAG being the thing that constrains a split.
