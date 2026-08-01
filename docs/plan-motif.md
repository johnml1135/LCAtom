# Plan — work in this repository (`motif`)

*Ten items. Milestones are defined in [plan-cross-repo.md](plan-cross-repo.md); this file owns
`MOT-*` item status and evidence.*

This repo owns, per D6: **the manifest, its classification columns, the MiniLcm ↔ LibLCM name map, the
generator, and the semantic + lowering layers.** It does not own the generated output's home — that is
`LcmCrdt` in lexbox ([plan-lcmcrdt.md](plan-lcmcrdt.md)) — and it does not own CRDT primitives — that is
harmony ([plan-harmony.md](plan-harmony.md)).

**Relationship to the older plans in this directory.** [implementation-plan.md](implementation-plan.md)
and [operation-catalog-plan.md](operation-catalog-plan.md) plan the change-set contract and runner that
[ADR 0013](adr/0013-harmony-is-the-change-mechanism.md) **withdrew**. They are retained as a record and
for their per-phase status detail; they are not the live plan. This file is. Product-level scope,
phases, and the FieldWorks/PanGloss/review surface are in
[motif-overall-plan.md](motif-overall-plan.md) — CI and quarantining the retired runner are tracked
there (its Phase 0), not duplicated here.

## Status summary

| Item | Milestone | Size | Status |
| --- | --- | --- | --- |
| `MOT-1` — the MiniLcm ↔ LibLCM name and shape map | **M0** | Small, and hand-authored | Not started — **the artifact does not exist** |
| `MOT-2` — the `(Class, Field)` join, failing the build on any unmatched key | **M1** | Small | Not started |
| `MOT-3` — generator skeleton: read `MasterLCModel.xml`, emit nothing yet | **M1** | Medium | Not started |
| `MOT-4` — emit possibility-list CRUD for the three reachable entities | **M2** | Medium | Not started |
| `MOT-5` — map ordered and reference kinds onto the Harmony primitives | **M3** | Medium | Not started |
| `MOT-9` — Baseline Token, Dry Run binding, apply authorization, Receipt/recovery contract | **M4** | Medium, correctness-critical | Not started |
| `MOT-10` — Proposal revisions, Check Runs, Reviews, Decisions, semantic owner policy | **M4** | Medium, the PR-like product core | Not started |
| `MOT-6` — semantic + lowering layer for grammar construct 1 | **M5** | Medium — **the first product family** | Not started |
| `MOT-7` — the remaining 29 constructs | **M6** | Large | Not started |
| `MOT-8` — the ordered-grammar proof | **M6** | Medium, and the highest-risk item here | Not started |

**What already exists and is not re-planned.** `manifest/liblcm-inventory.tsv` — 899 lines, 898 rows,
19 columns (`Class`, `Base`, `Abstract`, `Scope`, `ScopeReason`, `Field`, `Kind`, `Sig`, `Card`,
`HcReferenced`, `Construct`, `Group`, `Classification`, `ComparisonClass`, `Verbs`, `HcReachable`,
`AssessPoisonsCache`, `EnumValues`, `Rationale`), 473 in-scope rows across 95 in-scope classes, **100%
classified for every in-scope row.** The HCLoader-derived grammar map and the coverage research are
likewise done. The retired runner also still builds and passes 82/82 tests; that is a fact about the
repo, not a plan item.

---

## `MOT-1` — the name and shape map — M0

**The one artifact ADR 0014 named as required and non-existent.** The manifest is keyed on *LibLCM*
class names; the generation target uses *MiniLcm* type names, and they do not correspond:

| MiniLcm | LibLCM |
| --- | --- |
| `MorphType` | `MoMorphType` |
| `ComplexFormType` | `LexEntryType` |
| `SemanticDomain` | `CmSemanticDomain` |

A MiniLcm type is also not always exactly one LibLCM class — hence *shape* map, not just *name* map.
This is hand-maintained, is **not derivable from either source**, and is a prerequisite for everything
that follows.

**Deliverable.** A checked-in, reviewable file in `manifest/`, in the same TSV shape as the existing
inventory so the same tooling reads it. Scope it to what M2 needs — the three reachable entities plus
`CmPossibility` — and grow it per construct, not speculatively.

**Acceptance**

- Every MiniLcm type the generator targets resolves to its LibLCM class(es), and the reverse lookup is
  unambiguous.
- A MiniLcm type whose shape is *not* one LibLCM class is representable rather than approximated.
- The map is reviewable by a human who knows the domain and has not read the generator.

---

## `MOT-2` — the join, failing the build — M1

Structure comes from `MasterLCModel.xml` so it tracks LibLCM upgrades; policy (`Scope`, `Construct`,
`ComparisonClass`, `Verbs`) comes from the manifest, which is human judgement and exists nowhere else.
They join on `(Class, Field)`, and **a key present in one and absent from the other fails the build.**

The join key already exists and has been checked, not assumed: 445 `<basic>` + 235 `<owning>` + 218
`<rel>` = **898** field declarations in `MasterLCModel.xml` (424,797 bytes, 5,368 lines, model version
`7000072`, 193 classes), against **898** manifest rows. Diffing the actual key sets yields **zero keys
present in one and absent from the other, and no duplicates in either.** A matching count alone would
not have shown that.

**Deliverable.** The join, plus the build failure, plus a test that the build failure actually fires.

**Acceptance**

- An injected extra `(Class, Field)` key on either side fails the build with a message naming the key.
- A LibLCM upgrade that adds a field produces a row with structure and no policy, and the build stays
  red until a human classifies it. **For a system where a wrong merge policy corrupts a language
  project quietly, visible churn beats minimal churn** — that is the intent, not a side effect.
- `MasterLCModel.xml` is obtained without requiring a liblcm source checkout. `SIL.LCModel.csproj:125`
  packs `MasterLCModel.*` into the NuGet package under `contentFiles/` — but *not* in the conventional
  `contentFiles/{lang}/{tfm}/` layout, so it may not flow automatically into a `PackageReference`
  consumer. Reading it out of the package or the global package cache is the fallback, and which path is
  used must be recorded rather than left to whoever runs the build.

---

## `MOT-3` — generator skeleton — M1

Read the joined model, emit nothing. Separating "can we read and join this" from "is the emitted C#
right" keeps M2's gate about the output.

Precedent that this is ordinary rather than novel: **LibLCM already generates the majority of itself
from this file** — NVelocity templates (the 33 `LcmGenerate/*.vm.cs` files, explicitly `<Compile Remove>`'d
at `SIL.LCModel.csproj:12`) driven by an MSBuild task in `SIL.LCModel.Build.Tasks`; the `GenerateModel`
target declares `Inputs="MasterLCModel.xml"` and shells to a standalone `GenerateModel.proj`
(`SIL.LCModel.csproj:111-119`). Output: 9 gitignored files, **~154,000 lines — more generated code than
the ~149,000 hand-written lines in the same project.** Model-driven generation of a LibLCM-shaped C#
layer is not speculative; it is how LibLCM exists.

**Acceptance:** the generator loads all 898 rows joined, reports its own coverage, and is runnable in CI
without a liblcm source tree.

---

## `MOT-4` — emit possibility-list CRUD — M2

The output side of the ADR 0014 acceptance gate. Emit, for `PartOfSpeech`, `MoMorphType`/`MorphType`, and
`LexEntryType`/`ComplexFormType`: model classes and properties; `GetReferences()`;
`RemoveReference(id, time)` under the three fixed shape rules (**owner → delete self**, **`rel/atomic` →
null it**, **`rel/col` → filter it**); sync helpers dispatched by `ComparisonClass`; `JsonPatchChange`-based
edit changes; and `DeleteChange` registrations.

**Not emitted, because the manifest cannot know it:** `CreateChange` bodies (they must construct a
*valid* entity), HCLoader validation rules, EF relationship configuration, enum members (they live
outside `MasterLCModel.xml` — only a type-name override file exists), and custom fields (a pure runtime
concept, `AddCustomField`, absent from the model).

**Acceptance** is `CRDT-1`'s, in someone else's repo: the generated code replaces the shipped
hand-written versions and `LcmCrdt`'s existing tests pass **unmodified**. Correctness here is not
established by the design being elegant — it is established by regenerating code that already passes its
tests.

**What passing does not license.** 37 in-scope rows: 34 `unordered`, 3 `positional`, zero `feeding`, zero
`index-as-identity`, zero `AssessPoisonsCache=yes`. It exercises `set|clear` (20), `create|delete` (8),
`addRef|removeRef` (4), and `create|delete|move|reparent` (3). It licenses the mechanical majority and
says nothing about the ordered-grammar minority — precisely the residue ADR 0013 flagged as the real
problem.

---

## `MOT-5` — map ordered and reference kinds onto the primitives — M3

Once `HAR-3`, `HAR-5`, and `HAR-6` exist, the generator needs to target them:

| Manifest kind | Target |
| --- | --- |
| `feeding` (2 fields) | `HAR-3` converging sequence |
| `positional` (3 in the gate; more later) | `HAR-3`, or an explicit decision to keep LWW |
| `index-as-identity` (3 fields) | `CRDT-3` keyed map — **not** an ordered collection |
| `addRef\|removeRef` (34 fields) | `HAR-5` reference-set policy, uniformly |
| `create\|delete\|move\|reparent` (32 fields) | `HAR-6` cross-owner move rule |

**Acceptance:** for each kind, the generated code names the primitive rather than reimplementing it, and
a generated field of that kind is indistinguishable in behaviour from a hand-written one using the same
primitive.

---

## `MOT-6` — semantic + lowering layer, construct 1 — M5

**This is the actual design work** that ADR 0013 left standing after it withdrew the runner. Everything
above is mechanical; this is not.

Scope: one grammar construct. The semantic vocabulary (a named, reusable unit of intent — the sense in
which the product is called *Motif*), and its lowering into the concrete changes `MOT-4`'s output can
apply.

Two constraints inherited from settled decisions:

- **Preconditions live in the proposal, never in the change** (D5). Baseline evidence is an *observation
  carried by the proposal envelope*, evaluated at review/apply time and surfaced as drift. A precondition
  inside a merging change makes the outcome depend on evaluation position, and two replicas that resolve
  it differently diverge permanently. What crosses into history is unconditional.
- Construct naming is **not mechanical** (issue B19), and 17 manifest rows are multi-construct (B20).
  Both block this item and neither is resolved by the generator.

**Acceptance:** M4's gate, jointly with `CRDT-4` and `CRDT-5` — one construct merges across two replicas
**and** round-trips through Chorus Send/Receive.

---

## `MOT-7` — the remaining 29 constructs — M6

30 constructs, 75 reference fields, 38 classes. The point of the generator is that this is **30 reviewed
diffs rather than 30 hand-built constructs.**

Sequencing is already decided by [ADR 0012](adr/0012-build-order-hc-spine-first-kinds-generated.md): of
150 HermitCrab-reachable in-scope fields, **113 are grammar and only 32 lexical**, so grammar leads. L0
(the ~37 non-grammar fields `HCLoader` actually reads) then G0–G2, then the lexical backfill driven by
the non-HermitCrab consumers rather than by the parser.

**Known blockers that are not generator work:** L0's object-creation closure is uncomputed (B21), and
roughly 300 of 473 in-scope rows were classified by heuristic rather than by citation (B17, B18) —
which matters more than it did, because generation reads those classifications directly. Verify-lazily
versus dedicated-audit is undecided.

---

## `MOT-8` — the ordered-grammar proof — M6

**The highest-risk item in this repository, and the one the M2 gate deliberately does not cover.**

Two `feeding` fields — phonological rule order, where order encodes feeding and bleeding — and three
`index-as-identity` fields — alpha variables, where position is an identifier. ADR 0013's surviving
finding is that these **cannot ride on a last-writer-wins scalar order**. That was recorded as a defect
report against Harmony's `SetOrderChange`, and `HAR-3` is the answer to it.

**Acceptance**

- Two people concurrently reordering phonological rules converge to one order, and that order is
  linguistically defensible — not merely identical on both replicas. Convergence to a *wrong* grammar is
  a failure, and this is the case where the CRDT-correct answer and the linguistically-correct answer can
  differ.
- The three alpha-variable fields survive a concurrent edit under `CRDT-3`'s keyed representation.
- The residue is proven against real phonological rule order from a real project, not a synthetic
  fixture.


---

## `MOT-9` — reviewed world equals applied world — M4

Define the portable Baseline Token, immutable Dry Run binding, one-use Apply Authorization, Drift
Refusal, Receipt, and reconciliation states described in
[plan-product-architecture.md](plan-product-architecture.md). The authored Proposal remains the only
semantic input; a generated LibLCM Mutation Plan is output-only.

**Acceptance:** two agents begin at one Baseline Token; one Proposal applies in authored order and
the other refuses Drift before mutation. Injected failures at every UOW/save/history/Receipt boundary
produce rollback or `NeedsReconciliation`, never blind retry.

## `MOT-10` — Proposal review domain — M4

Define immutable Proposal revisions, typed Check Runs, human/AI Reviews, versioned policy Decisions,
semantic owner routing, and stale-binding rules. These are application records over Harmony history,
not a competing change transport.

**Acceptance**

- any change to Proposal, baseline, relevant artifact, tool contract, interpretation version, or
  policy revision invalidates the former Check Runs and Decision;
- static-analysis Check Runs are first-class immutable facts with the same exact-input and stale
  binding as Dry Run, Assessment-correlation, conformance, privacy/security, and policy checks;
- AI actors are labeled, may recommend or abstain, and cannot satisfy a human/native-speaker role by
  implication; permitted AI roles are declared per operation family, and any autonomous approval or
  apply policy is versioned, independently checked, provenance-bound, least-privileged, expiring,
  and audited;
- granular operation/effect comments coexist with Proposal-level atomic apply;
- payload and provenance digests bind the approved candidate through its Receipt;
- generated-output provenance binds the LibLCM model, manifest, crosswalk, generator, dependency
  lock, build environment, and output digests.
This item is why M5 is not simply "finish the volume". Open question feeding it, still unanswered in
[grill-decisions.md](grill-decisions.md): *what happens when two people concurrently reorder
phonological rules.*

---

## Cross-links

- Milestones, dependency edges, alignment rules: [plan-cross-repo.md](plan-cross-repo.md)
- Primitives this depends on: [plan-harmony.md](plan-harmony.md)
- Where the generated output lands: [plan-lcmcrdt.md](plan-lcmcrdt.md)
- Product scope and phases: [motif-overall-plan.md](motif-overall-plan.md)
- Decisions: [grill-decisions.md](grill-decisions.md) ·
  [ADR 0013](adr/0013-harmony-is-the-change-mechanism.md) ·
  [ADR 0014](adr/0014-generate-the-crdt-layer-from-masterlcmodel.md) ·
  [ADR 0012](adr/0012-build-order-hc-spine-first-kinds-generated.md)
- Open issues named above: [issues.md](issues.md) (B17, B18, B19, B20, B21)
