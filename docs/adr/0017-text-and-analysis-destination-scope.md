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

### 3. Reserve non-object targets in the canonical-id space **now**

`CanonicalId` is `<optional prefix><22-char base64url decoding to exactly 16 bytes>`, and the prefix is
*"preserved exactly with no normalization"* and *"carries no structural meaning"*
(`Contract/Ids/CanonicalId.cs:11-20`). A non-object target is therefore **already representable
syntactically** — e.g. an `occ_`-prefixed id whose 16 bytes are a derived anchor digest.

**Decide now that an operation's target need not be a LibLCM object, and reserve the prefix.** Cost
today is approximately zero. The alternative — discovering later that targets must become a tagged
union — changes the hashed operation shape and invalidates every stored digest.

**This solves addressing shape, not anchor durability.** What 16 bytes survive a re-segmentation is
still open under `H31`, and this ADR does not pretend otherwise.

### 4. The effect tuple's identity slot may hold a non-object target

Same reasoning, applied to the digest atom. State it now so the slot is not defined as "object id" by
implication and then widened later.

### 5. Do not settle change-class *naming* on lexicon and grammar alone

`G27`/`G28` may proceed on everything else, but the committed kind-namespace segments should either be
sketched with text's shape in view or deferred, because renaming is major-forcing.

### 6. Name the fifth drift class — anchor invalidation

Four drift classes exist and are enumerated for human review (`change-set-contract.md:554`). Text adds a
fifth: the target moved without any object changing. Adding it later is a review-model and UI change,
not a contract change, so this is **genuinely cheap** — but name it now so the enum is not designed
closed.

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
- Decisions 3 and 4 are cheap **only if taken before M3 freezes the canonical JSON form.** After that
  they are major bumps. This is the ADR's one time-sensitive element.
