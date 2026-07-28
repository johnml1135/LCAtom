# FWLite grammar + PanGloss decision loop — verification synthesis

Status: research synthesis for owner decisions, not an ADR.

Date: 2026-07-28.

## Goal being tested

Add grammar authoring to FWLite, evaluate a proposed grammar change with PanGloss, produce:

1. an evidence report;
2. the words whose analyses differ between baseline and candidate;
3. a review surface for a native speaker, language specialist, or permitted AI reviewer; and
4. an explicit decision that can authorize, reject, defer, or revise the proposed change.

The required product loop is:

```text
proposal
  -> isolated baseline/candidate evaluation
  -> structured analysis delta + report
  -> human/AI review
  -> explicit decision
  -> stale-base check
  -> canonical apply
  -> sync
```

PanGloss output is evidence. It is never authority to mutate the project.

## Evidence baseline

Local source was inspected at these exact revisions:

| Repository | Revision |
| --- | --- |
| `LCAtom` | `2d47289` plus the untracked research documents listed below |
| `harmony` | `c858cb429231298aef564354b8ec2d5c87507287` |
| `languageforge-lexbox` | `da284fa8e628a7acfa76a080dabfc324272ce64e` |
| `PanGloss` | `cc1e392c6fc8f6eac36a3a48ea044c7308509095` |
| `machine` | `4c79ed0e055bb553e68359bcb81a8ad711134944` |
| `FieldWorks` | `b8a2dd123aa6a5d0b95774ae74daa50e852932f8` |
| `liblcm` | `d564a719b1cce16c25ebea53a537393cb757f5d1` |

The current untracked research inputs were:

- `docs/grill-decisions.md`;
- `docs/inventory-harmony.md`;
- `docs/inventory-lexbox.md`; and
- `docs/inventory-local-first.md`.

Eight read-only Luna audits were first requested against the local repositories. Their filesystem
sessions could not launch because `codex-windows-sandbox-setup.exe` is missing. Public-upstream
audits were useful as adversarial readers, but could not expose the local PanGloss checkout or
LCAtom documents.

The decisive source passages were therefore packaged directly into eight line-numbered, commit-pinned
evidence packets and sent to fresh Luna sessions through standard input. The packets covered Harmony
transactions and merge primitives, FWLite sync and FieldWorks interop, PanGloss diagnostics and CLI,
the manifest counts/rows, and selected HCLoader paths. Consequently:

- no Luna claim is accepted solely because an agent stated it;
- public-source uncertainty is not treated as evidence that local code is absent; and
- the conclusions below are based on local source where it was available.

## Bottom line

The desired loop is coherent, but the existing documents are too confident about the solution.

Three foundations are real:

1. Harmony is an existing generic synchronization and intent-log substrate for FWLite entities.
2. FWLite already reconciles a Harmony-backed MiniLcm projection with LibLCM/`.fwdata`.
3. PanGloss can read `.fwdata`, analyze words, emit diagnostic artifacts, and write a readiness
   report.

The complete loop nevertheless does **not** exist. The largest missing product capability is not
grammar CRUD. It is a paired, reproducible **baseline-versus-candidate assessment** with a stable
analysis identity and a reviewable changed-word artifact.

The current decisions D1–D3 should therefore be read as hypotheses:

| Decision | Current confidence | Reason |
| --- | --- | --- |
| D1: grammar entities live in the CRDT store | Plausible, not proven | Harmony can carry typed entities, but merge safety and LibLCM round-trip have not been demonstrated for a representative grammar slice. |
| D2: `IMiniLcmGrammarApi` in the existing repo | Sensible organizational default, not proven | It fits current project boundaries, but no interface or grammar implementation exists. |
| D3: generate the mechanical layer from the manifest | High-risk hypothesis | Shape-derived scaffolding appears feasible; the manifest does not prove create semantics, inverse/reference policy, validation, ordering, or multi-construct expansion. |

ADR 0013's narrow conclusion survives: do not build a third general change-log mechanism. It does
**not** prove that Harmony already provides the grammar domain model, proposal workflow, PanGloss
comparison, or LibLCM conformance.

## What was confirmed

### Harmony

- `DataModel.AddChanges` creates one commit and applies it inside one EF transaction
  (`harmony/src/SIL.Harmony/DataModel.cs:83-120`). Within that commit,
  `SnapshotWorker` applies changes by `ChangeEntity.Index`
  (`harmony/src/SIL.Harmony/SnapshotWorker.cs:63-116`).
- Before/at-commit state is queryable
  (`harmony/src/SIL.Harmony/DataModel.cs:300-390`).
- Unknown change JSON can round-trip as `OpaqueChange`
  (`harmony/src/SIL.Harmony/Changes/OpaqueChange.cs:5-27`).
- `OpaqueChange` is inert on a client that does not know the type. The supplied source does not prove
  how or when history is regenerated after the type becomes known, or how dependent later changes
  are handled while the earlier change is skipped
  (`harmony/src/SIL.Harmony/SnapshotWorker.cs:76-109`).
- The commit hash does **not** bind payload or metadata. It hashes only commit ID plus parent hash
  with XxHash64 (`harmony/src/SIL.Harmony.Core/CommitBase.cs:32-40`).
- `SetOrderChange<T>` stores a `double` and directly assigns it
  (`harmony/src/SIL.Harmony/Changes/SetOrderChange.cs:5-40`). It is not a sequence CRDT and, by
  itself, does not preserve concurrent insertion intent.
- Within a commit, snapshot application sorts by `ChangeEntity.Index`, but the supplied source does
  not show validation for duplicate, missing, negative, or non-contiguous indexes
  (`harmony/src/SIL.Harmony/SnapshotWorker.cs:63-71`).

Qualification: Harmony's database atomicity is not LibLCM atomicity. FWLite's Harmony state and an
`.fwdata` project's LibLCM unit of work are two different persistence boundaries.

### FWLite and FieldWorks interoperability

- FWLite has separate `MiniLcm`, `LcmCrdt`, `FwDataMiniLcmBridge`, and
  `FwLiteProjectSync` layers.
- `CrdtFwdataProjectSyncService` performs explicit snapshot reconciliation through `IMiniLcmApi`;
  Harmony changes are not replayed directly into `LcmCache`
  (`backend/FwLite/FwLiteProjectSync/CrdtFwdataProjectSyncService.cs:42-143`).
- Reconciliation is hard-coded by family—writing systems, publications, parts of speech, semantic
  domains, complex-form types, morph types, then entries—and performs the two directions
  sequentially. The source shows `fwdata.Save()` after the sequence, but does not establish one
  transaction spanning the CRDT and LibLCM stores
  (`CrdtFwdataProjectSyncService.cs:93-145`).
- The existing bridge covers a limited lexical/project subset. There is no
  `IMiniLcmGrammarApi` and no grammar entity/change/sync surface in the inspected revision.
- FieldWorks interoperability uses the existing Chorus/`.fwdata` route. That is a real precedent,
  not proof that grammar round-trips correctly.
- LibLCM can create a new project at library/test level, while current CRDT/`.fwdata`
  reconciliation ordinarily starts with existing project state. A complete, user-facing
  CRDT-to-new-`.fwdata` materialization path is not yet established.

### PanGloss

- The CLI accepts `.fwdata` directly and imports it in memory
  (`PanGloss/rust/crates/pg-cli/src/main.rs:255-257,281-320`).
- `pangloss diagnose` emits build/assessment structures and per-word outcomes.
- `pangloss make-report` produces a Markdown readiness report covering capability, trust, policy
  checks, coverage attestation, build time, latency, compilation plan, limitations, and pinned
  revisions (`PanGloss/rust/crates/pg-cli/src/make_report.rs:1-91,470-591`).
- That readiness report explicitly does **not** certify analysis correctness
  (`make_report.rs:84-91`).
- Most importantly, the diagnostics source explicitly defers:
  - a default-engine comparison pipeline;
  - canonical/golden analysis-identity diffing; and
  - build/assessment report-to-report comparison
  (`PanGloss/rust/crates/pg-cli/src/diagnostics.rs:38-59`).

Therefore `make-report` is not an answer to “did this grammar change make the language analysis
better?” It answers a different question: “is this compiled grammar/runtime artifact ready under a
declared technical policy?”

## Assumptions that did not survive challenge

### “Harmony already supplies the whole change-review mechanism”

No. It supplies synchronized intent changes, transactions over its own database, snapshots, and
history. It does not supply:

- a pre-canonical proposal overlay;
- payload-bound approval integrity;
- LibLCM validation/read-back;
- PanGloss evaluation;
- a linguistic decision state machine; or
- a cross-store atomic transaction.

### “The remaining Harmony grammar problem is only five special fields”

The five `feeding`/`index-as-identity` fields are special merge hazards, but counting fields is not
the same as proving a grammar model. Reference policy, delete closure, cross-owner moves, required
object creation, normalization, and invariant enforcement apply across many otherwise “ordinary”
fields.

### “The manifest can generate grammar support”

It can plausibly generate DTO properties, registrations, repetitive references, and portions of
sync/diff code. It cannot decide:

- valid construction defaults and prerequisite closure;
- domain validation;
- add-wins versus remove-wins reference policy;
- inverse/ownership semantics;
- cross-owner move and cycle behavior;
- what an ordered field means linguistically; or
- how multiple constructs encoded by one model member should be exposed.

The manifest's `473` in-scope rows are not `473` independent construct fields. Some rows expand to
multiple pipe-separated constructs, while inherited model members may be represented once. Report
raw rows, expanded `(construct, field)` pairs, and deduplicated LibLCM members separately.

The “five special fields” claim is also unsafe. The manifest contains 2 `feeding`, 3
`index-as-identity`, **56 `positional`**, and 412 `unordered` rows. The first five name unusual
linguistic dependencies; they are not the only rows requiring nontrivial identity, placement,
reference, delete, or merge semantics.

Generated output needs per-construct conformance fixtures, not only compilation and inventory
coverage.

### “A native speaker can decide whether a formal analysis is better”

A native speaker is authoritative about forms, meanings, acceptability, and contrasts they
understand. They may not be equipped to compare formal morpheme IDs, feature structures, rule
traces, or ambiguity sets. The review surface must translate analysis deltas into language-facing
questions and must preserve “unsure / needs linguist” as a valid answer.

### “An internet AI can substitute for a native speaker”

No. For sufficiently documented languages, an AI can search for corroborating evidence, explain
candidate analyses, cluster changed words, and recommend review priorities. It is not linguistic
authority. External use also requires explicit project policy because unpublished examples,
community knowledge, and culturally sensitive data may leave the device.

## The missing assessment contract

PanGloss needs a versioned paired-run contract. At minimum:

### Inputs

- exact baseline project/grammar digest;
- exact candidate project/grammar digest;
- proposal/change identity;
- PanGloss revision and engine/configuration;
- corpus/word-set digest and tokenization policy;
- resource budgets and incomplete-result policy; and
- optional gold judgments or prior reviewer decisions;
- baseline and candidate importer warnings, unsupported/dropped constructs, and a policy that decides
  when asymmetric or material import loss invalidates the comparison; and
- canonical semantic serialization/normalization plus the digest algorithm. The contract must state
  whether import warnings and importer version are included in the digest.

### Per-word output

- stable word occurrence identity and display context;
- baseline analysis set;
- candidate analysis set;
- added and removed analysis identities;
- coverage transition: none→some, some→none, some→some;
- ambiguity transition;
- incomplete/error status for each side;
- a human-readable explanation;
- provenance linking the delta to the exact paired run; and
- review state and reviewer annotations, stored separately from the computed delta.

“Differently analyzed” must not mean only “the pretty string changed.” Analysis identity must be
defined and versioned. Ordering differences in an analysis set should not create false changes.

### Run-level output

- counts for improved, regressed, changed-but-unjudged, unchanged, and incomplete words;
- no single “better” scalar unless a policy explicitly defines its utility and trade-offs;
- links to the existing readiness report rather than conflating readiness with correctness;
- a machine-readable assessment artifact; and
- a derived Markdown/HTML report for people.

## Recommended proof sequence

Do not start by generating all grammar entities.

### Proof 0 — build paired PanGloss comparison on two existing `.fwdata` files

Before FWLite grammar CRUD, add and prove the missing ability for PanGloss to compare a known
baseline and candidate project and emit a deterministic changed-word artifact. Existing PanGloss
commands analyze one grammar at a time; this is new product work, not wiring. Manually make one
controlled grammar edit in FieldWorks for the fixture.

Exit gate: against a predefined language-facing oracle, a native speaker or linguist can inspect the
report, judge the relevant word-level contrasts, and detect at least one deliberate regression
fixture without needing to interpret internal parser structures.

### Proof 1 — FWLite edits one existing grammar value

Candidate slice: use an existing affix-template slot with referring irregular inflection types and
change its `Optional` value. `HCLoader` directly uses `!slot.Optional` to decide whether to create
null-affix rules, then writes `Optional` into the HermitCrab template
(`FieldWorks/Src/LexText/ParserCore/HCLoader.cs:1687-1732`). This is a stronger causal candidate than
the parser-parameter `Strata` string, but it is not selected until a manual baseline/candidate
`.fwdata` experiment proves that PanGloss imports the field and produces the intended nonempty delta.

Implement the path:

```text
FWLite edit
  -> Harmony commit
  -> MiniLcm grammar projection
  -> scratch `.fwdata`
  -> PanGloss paired assessment
  -> changed-word review
```

Exit gate: identity and value survive CRDT restart, `.fwdata` materialization, LibLCM reopen, and
PanGloss import.

### Proof 2 — rejection and acceptance

Keep the proposal isolated until review. Prove reject/discard, stale-base detection, re-evaluation,
explicit approval, canonical apply, and second-client synchronization.

Exit gate: no report or preview can authorize mutation, and an approval is bound to exact proposal
and evaluation digests.

### Proof 3 — representative hard grammar shapes

Add fixtures for:

- create/delete with prerequisite closure;
- reference add/remove under concurrent edits;
- cross-owner move;
- ordered feeding/bleeding rules; and
- index-as-identity structures.

Only after these pass should the generator expand the surface.

## Provisional architecture

The smallest architecture consistent with the evidence is:

```text
candidate accepted-state projection (Harmony/MiniLcm if D1 passes)
           |
           | draft proposal, not yet canonical
           v
proposal + exact baseline
           |
           v
isolated candidate materialization
           |
           +--> baseline `.fwdata` --> PanGloss
           |
           +--> candidate `.fwdata` --> PanGloss
                                      |
                                      v
                         structured analysis delta
                                      |
                         +------------+------------+
                         |                         |
                    human report              AI evidence
                         |                         |
                         +------------+------------+
                                      |
                               explicit decision
                                      |
                              stale-base validation
                                      |
                           accepted Harmony change (conditional on D1)
                                      |
                     existing FWLite/FieldWorks sync
```

Open question: whether a draft proposal is itself a Harmony entity/commit or a local/application
artifact outside the canonical grammar projection. Do not settle this by assuming “all durable
state belongs in Harmony.” Test rejection, offline review, sync, and history semantics first.

## Risks requiring explicit decisions

1. What “better” means and who defines the policy.
2. Which actor may authorize canonical application.
3. Whether AI review is allowed, what data may leave the device, and how consent is recorded.
4. Whether native-speaker judgments apply to words, analyses, or the grammar change itself.
5. What happens when metrics improve while reviewed words regress.
6. Whether proposals and reports sync to collaborators before acceptance.
7. How a stale evaluation is invalidated.
8. Whether `.fwdata` is generated on demand, continuously reconciled, or required as a project
   authority.
9. Whether grammar change schemas become a durable cross-version Harmony protocol.
10. Who maintains PanGloss comparison, FWLite grammar, Harmony additions, and LibLCM conformance.

## Conclusion

Continue toward grammar in FWLite, but move the next proof point upstream:

> First build and validate the paired PanGloss assessment contract on two pinned `.fwdata` files,
> using an existing template-slot `Optional` change as the first controlled candidate.

Without that, grammar CRUD produces changes but cannot answer the product's central question.
Once the delta artifact is real, implement one FWLite-to-`.fwdata` grammar edit that visibly changes
that artifact. That experiment will produce much stronger evidence for or against D1–D3 than another
architecture document.




