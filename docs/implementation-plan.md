# Implementation plan

The plan is deliberately test- and contract-first. Do not begin with a generic reflection CRUD
engine.

## Phase 0 — repository and compatibility spike

1. Create solution, central build properties, analyzers, formatting, test projects, and CI.
2. Pin a current LibLCM NuGet version and document local-package override.
3. Prove builds for:
   - contract on `netstandard2.0`;
   - LibLCM libraries on `netstandard2.0`, `net462`, and `net8.0`;
   - a small `net48` FieldWorks-compatible consumer;
   - a `net8.0` CLI/test host.
4. Prove an already-loaded `LcmCache` can be passed into the runner without lifecycle ownership.
5. Prove whole-change-set commit and rollback with `UndoableUnitOfWorkHelper.Do`, including
   footprint-scoped snapshot read-back before commit.
6. Prove effect capture by before/after semantic-snapshot diff scoped via the public
   `ICmObject.AllOwnedObjects` and `ICmObject.ReferringObjects`, confirming no dependence on LibLCM's
   internal, consumer-invisible undo records ([ADR 0003](adr/0003-feasibility-findings.md)).
7. Record exact supported LibLCM/model versions.
8. Verify `LexDb.Resources` is safe for the [applied-change log](applied-log.md): FieldWorks tolerates
   unknown `CmResource` entries inertly, `CmResource.Name` accepts the capped length, and — confirmed
   at the LibChorus level, to be re-confirmed in FLExBridge — Send/Receive unions distinct-GUID
   additions rather than conflicting them, with `CmResource` registered GUID-keyed and order-irrelevant.
9. Adapt the FwData/LibLCM host plumbing (project load, cache lifecycle, UOW helper, headless
   UI/progress shims, rich-text mapping) by copy-and-adapt from `FwDataMiniLcmBridge` under its MIT
   license, not a shared package; see [ADR 0003](adr/0003-feasibility-findings.md).
10. Verify the custom-field schema pattern from
    [ADR 0005](adr/0005-schema-operations-non-undoable-uow.md): metadata operations run in their own
    non-undoable unit of work before the data unit of work, no save occurs while a task is open, a
    metadata-only change still persists, and a failed data phase leaves a defined-but-empty field
    rather than orphaned data referencing an unpersisted field.
11. Warm LibLCM's incoming-reference index at project load, off the interactive path, and confirm
    pre-flight is interactive-fast only after warm-up
    ([ADR 0006](adr/0006-engine-reality-apply-readback-preflight.md)).
12. Ship and version the normalization-data artifact (`nfc_fw`) and generate canonical-JSON/digest
    conformance vectors from C#, Python, and Rust to prove byte-for-byte agreement
    ([ADR 0007](adr/0007-cross-language-digest-determinism.md)).
13. Spike exclusive-write coordination and external-collision detection, and confirm no semantic side
    effect is task-close-only ([ADR 0006](adr/0006-engine-reality-apply-readback-preflight.md)).

Exit: target matrix and transaction boundary are demonstrated by executable tests.

## Phase 1 — contract kernel

1. Define immutable Change Set, operation envelope, placement, dependency, rationale, confidence,
   provenance, and extension types.
2. Implement strict closed JSON parsing and version validation.
3. Publish JSON Schemas and representative valid/invalid fixtures.
4. Implement 128-bit base64url ID parsing and canonical GUID mapping.
5. Add the fixed byte/suffix/GUID vector from the contract plus randomized round trips.
6. Implement RFC 8785 canonicalization and SHA-256 intent digests.
7. Prove pretty-printing and excluded assessment/extensions do not change intent digest.
8. Define stable diagnostic, Assessment, failure-report, and Receipt contracts.
9. Define the exact intent projection, including operation IDs but excluding Change Set ID,
   rationale, confidence, provenance, assessments, and extensions; render hashes as lowercase
   `sha256:<hex>`.
10. Define the identity-mapping input port required to preserve canonical lineage after explicit
    storage-GUID overrides.

Exit: a LibLCM-free package gives identical parsing and hashes on every target.

## Phase 2 — model inventory and semantic snapshot

1. Generate a raw inventory from LibLCM metadata.
2. Establish the reviewed coverage-manifest format and initial classifications.
3. Fail CI on unclassified/changed members.
4. Implement canonical representations for:
   - identities and runtime types;
   - ownership and references;
   - scalars and dates;
   - Unicode/MultiUnicode;
   - rich String/MultiString with runs and semantic properties;
   - ordered and unordered collections;
   - custom-field definitions and values.
5. Implement LibLCM NFD/NFSC normalization adapters.
6. Produce inspectable canonical snapshots and semantic digests.
7. Add exact `.fwdata` byte digest as a separate host utility.
8. Import LibLCM normalization edge cases as conformance fixtures.

Exit: save/reopen and canonically equivalent models have stable semantic digests; classifying a
newly shipped LibLCM member leaves the digest of a model that does not populate it unchanged;
meaningful rich text differences remain visible.

## Phase 3 — assessment and mutation-plan spine

1. Define resolver interfaces for canonical IDs, existing GUIDs, owners, references, fields,
   writing systems, and sequence anchors.
2. Implement GUID collision preflight before any mutation.
3. Define the output-only LibLCM Mutation Plan.
4. Implement before-state and expected-effects capture in Assessment, never by mutating Change Set
   intent. Define effects as read-back snapshot deltas honoring the four obligations —
   read-back-not-replay, canonical identity, identity-aware structural delta, transition hashing —
   per [expected effects](change-set-contract.md#expected-effects). Capture is a footprint-scoped
   before/after snapshot diff, not consumption of a LibLCM change feed (none is exposed); scope the
   snapshot to the footprint plus the delete/reference closure enumerated via the public
   `ICmObject.AllOwnedObjects` and `ICmObject.ReferringObjects`.
5. Implement conflict taxonomy and stable diagnostics.
6. Implement read-only `Assess(changeSet, cache)`.
7. Add resource/depth/count limits for untrusted declarative input.
8. Distinguish reassessment (same intent) from explicit rebase output (new intent when authored
   anchors must change).

Exit: assessments are deterministic and preview all modeled consequences without mutation; effect
digests are computed over read-back snapshot deltas in canonical identity, are stable under a
lowering that changes the plan but not the resulting state, and move when a `before` or `after` on
a touched field moves.

## Phase 4 — minimal vertical operation slice

Implement a coherent end-to-end subset rather than all creates first:

1. one common entity create with explicit canonical ID;
2. scalar set and explicit clear;
3. multilingual alternative set and clear;
4. atomic reference attach and detach;
5. collection add/remove;
6. sequence insert/move/remove using anchors;
7. entity delete with full cascade;
8. whole-change-set atomic apply and rollback;
9. read-back and Receipt.

For each family add:

- schema;
- validation;
- lowering;
- effects;
- apply;
- read-back;
- snapshot;
- diff;
- conflicts/rebase;
- conformance tests.

Write the [applied-change log](applied-log.md) entry inside the same unit of work, classified
`runner-bookkeeping` so it reaches neither the snapshot nor expected effects, and expose the
GUID-matched "already applied?" query over it.

Exit: a mixed multi-operation Change Set applies atomically and rollback restores the exact semantic
baseline after failures injected at every operation boundary, leaving no log entry.

## Phase 5 — custom fields

1. Inventory `FieldDescription`, metadata-cache, custom-property, serialization, and undo behavior.
2. Implement enumeration and resolution by `(ownerClass, internalName)` plus expected structural
   signature.
3. Keep `flid` inside cache-scoped resolved handles and mutation plans.
4. Implement ensure-style definition.
5. Implement supported display metadata update only.
6. Implement typed value operations for all classified Cellar property types.
7. Implement high-level definition deletion and complete owned-object/value effects.
8. Preserve optional client logical keys in non-semantic `extensions`; do not interpret them in v1
   unless the open decision is explicitly approved.
9. Add cross-project fixtures where identical definitions receive different `flid` values.
10. Add incompatible same-name, migration-required, cascade-change, and rollback fixtures.

Exit: custom-field definitions and values round-trip mechanically without serializing `flid` as
identity.

## Phase 6 — complete semantic coverage

Work through the coverage manifest by coherent domain families. Each pull request must close a
visible portion of the manifest and include full operation-family completion criteria from
`AGENTS.md`.

Prioritize:

1. lexical entries, senses, examples, forms, writing-system alternatives;
2. POS, MSAs, inflection classes, features, and references;
3. possibility lists and list items;
4. phonological/morphological model objects used by Hermit Crab/FieldWorks;
5. remaining supported semantic surface;
6. explicit unsupported and derived/internal classifications.

Exit: 100% model-surface classification and every `semantic-operation` member covered.

## Phase 7 — mechanical diff

1. Implement scalar, map, set, reference, ownership, and entity create/delete comparison.
2. Implement deterministic minimum ordered-sequence edit synthesis using LIS.
3. Freeze tie-breaking, operation emission, and before/after anchor fixtures.
4. Add exhaustive small-permutation BFS oracle and property tests.
5. Implement two-way `Diff(A, B)`.
6. Prove `Normalize(Apply(A, Diff(A,B))) == Normalize(B)` over generated supported models.
7. Implement common-ancestor three-way analysis.
8. Ensure unrelated projects use exact-ID delete/create with no fuzzy matching.

Exit: round-trip invariant passes for all supported generated and curated fixtures.

## Phase 8 — rebase and conflict conformance

1. Implement re-assessment against a new baseline.
2. Refresh only allowed baseline evidence and unique anchors.
3. Emit changed delete/reference effects.
4. Add fixtures for every deterministic, warning, conflict, and hard-error point.
5. Prove semantic intent is unchanged by pure rebase.
6. Prove ambiguous cases cannot be applied without amended intent.

Exit: conflict behavior is fully documented by executable fixtures.

## Phase 9 — adapters and release

1. Implement a minimal `net8.0` process/JSON host for isolated and Python-driven use.
2. Keep host project opening/saving outside core interfaces.
3. Build a `net48` compatibility/conformance adapter suitable for FieldWorks integration.
4. Document pythonnet as possible but prefer process isolation initially.
5. Publish package, schema, fixtures, supported LibLCM/FieldWorks versions, and the drift and
   migration policy.
6. Run conformance against pinned LibLCM and representative real FieldWorks projects.

Exit: Linguistic Assistant, PanGloss, Flexicon, GramTrans, FlexToolsMCP, FieldWorks, and other tools
can call the same compiled semantics without reimplementing them.

## Test strategy

Required test classes:

- strict schema and malformed-input tests;
- fixed and randomized canonical-ID tests;
- canonical JSON/digest vectors;
- model inventory drift tests;
- additive-stability tests proving an additive manifest or LibLCM change preserves semantic digests,
  and that a `projectionVersion` bump changes them;
- effect-drift tests: a changed default surfaces a delta, and an equivalent but improved lowering
  produces a different Mutation Plan with identical effect digests and no drift diagnostic;
- cross-version ingestibility tests, including an older runner refusing an unknown operation kind
  with an actionable required-version message;
- applied-log tests: entries never move the semantic digest or any effect digest, foreign
  `CmResource` entries survive untouched, rollback leaves none, malformed input is rejected rather
  than truncated, and the description may contain the delimiter;
- comparison-footprint tests: an unrelated edit elsewhere in the project produces no drift; a change
  to a positionally ordered neighbor's identity does, while that neighbor's internal edit does not;
  a feeding phonological neighbor's content edit does; and a membership the operation authors is in
  the footprint while the referenced container's own churn is not;
- pre-flight tests: an unchanged engine and footprint skip the re-check, a clean pre-flight advances
  the anchor to ready-to-apply without re-review, and an effect delta stops with the delta;
- prerequisite tests: a `requires` entry absent from the applied history is a hard error that cannot
  be forced, a present prerequisite permits apply, a cyclic prerequisite graph is rejected, a diamond
  (two independent prerequisites of one dependent) resolves, and a dependent is assessed and tested
  against the state with its full prerequisite closure applied in topological order;
- identity-stability tests: editing or rebasing a Change Set moves its intent digest but never its
  frozen `changeSetId`, and an applied-log entry whose stored intent digest differs from the Change
  Set now presented is surfaced rather than reported as already-applied;
- apply-binding tests: apply refuses with a hard error when given no prior Assessment, and stops with
  a drift diagnostic when the bound Assessment's footprint has moved;
- Flexicon-derived regression fixtures for LibLCM ordering/cascade gotchas (see
  [Flexicon harvest](flexicon-harvest.md)): schema-mutation-in-UoW, dangling stratum refs on delete,
  orphan-on-dereference, first-component-becomes-primary, and attach-owned-child-before-set;
- transaction-hazard tests: a rolled-back apply invalidates the headword/homograph/monomorphemic
  caches (or discards the cache), an external writer colliding mid-apply is reported as a collision
  rather than a self-rollback, and lowering never opens a nested unit of work;
- reparent and compound-op tests: move-between-owners is one operation with a correct effect set, and
  merge / subclass-convert / GUID-change (create-target-then-merge) carry a read-back-derived
  footprint and force full re-assessment;
- cross-language conformance: identical canonical JSON and digests from C#/Python/Rust over shared
  vectors, including the pinned sort comparator and the shipped normalization data;
- normalization and rich-text property tests;
- per-operation positive and negative tests;
- collision and wrong-type tests;
- dependency/order tests;
- delete closure/reference cleanup tests;
- custom-field type and lifecycle tests;
- injected-failure atomic rollback tests;
- save/reopen semantic stability tests;
- diff/apply round-trip properties;
- minimal-sequence exhaustive oracle tests;
- three-way conflict matrix;
- rebase intent-preservation tests;
- cross-target and supported-LibLCM conformance tests.

## Explicitly deferred

- Git/database/object-store selection;
- proposal discussion, approval, permissions, and review UI;
- server hosting and multi-tenant project management;
- fuzzy/cross-origin linguistic entity matching;
- in-memory parallel cache cloning;
- automatic custom-field structural migration;
- upstreaming into LibLCM;
- a second language/runtime implementation of operation semantics.

## Open decision carried into implementation

One design question was deliberately not claimed as approved: whether v1 should define a named,
namespaced logical contract key for custom-field conventions shared across projects. LibLCM cannot
persist or enforce such a key. The safe v1 default is to permit it only inside non-semantic
`extensions`, resolve fields by `(ownerClass, internalName)` plus structural signature, and revisit
the key only when a registry or persisted LibLCM representation has a concrete use case.
