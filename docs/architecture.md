# Architecture and decided design

## Purpose

LibLCM is a mature object model with factories, repositories, ownership rules, references,
persistence, and battle-tested undo/redo. It does not provide a portable, reviewable language for
describing a coherent model change. Existing tools can manipulate LibLCM, but their APIs,
transaction guarantees, and merge semantics differ.

This repository supplies one canonical semantic change language and one C# implementation that
all other tools call.

## Placement

The runner starts in an independent C# repository and is published as versioned NuGet packages.
It may eventually move into the LibLCM repository as a separate package, but its namespaces,
contract, fixtures, and consumers must not depend on that future move.

It does not initially live in:

- `SIL.LCModel.dll`, because the contract is still being validated and has an independent release
  and compatibility surface;
- FieldWorks, because headless and non-FieldWorks clients must not depend on a desktop application;
- Flexicon, GramTrans, or FlexToolsMCP, because Python/tool-specific behavior must not become the
  normative executor.

Those projects are not competitors but the motivating clients, adapters, and sources of use cases.
This runner exists to give them one mechanical, repeatable, reviewable, sequenceable, and rebasable
way to update the LibLCM model for dictionaries and grammars, replacing the raw property mutation each
hand-rolls today:

- Linguistic Assistant — an AI QA/documentation assistant that emits canonical Change Sets (lexical,
  morphophonology, and bilingual tiers) and targets this runner as its conforming applier;
- PanGloss — a Rust HermitCrab/FST parser whose grammar-fix handoffs are realized as Change Sets
  against the LibLCM grammar model;
- Flexicon ([github.com/MattGyverLee/flexicon](https://github.com/MattGyverLee/flexicon)) — a Python
  (`pyflexicon`) LibLCM wrapper for FLEx projects;
- FlexToolsMCP ([github.com/MattGyverLee/FlexToolsMCP](https://github.com/MattGyverLee/FlexToolsMCP))
  — an MCP server over FLEx lexicon data;
- GramTrans ([github.com/MattGyverLee/GramTrans](https://github.com/MattGyverLee/GramTrans)) —
  grammar-component transfer between FLEx projects.

They are expected to be refactored to call this compiled runner rather than each reimplementing apply,
ordering, delete-closure, and conflict semantics. FieldWorks should present previews, collect
approval, coordinate its shell undo behavior, call this runner, and show receipts. No client
reinterprets the JSON independently.

## Package shape

The initial solution should separate:

1. `SIL.LCAtom.Contract`
   - immutable contract DTOs;
   - closed JSON serialization;
   - schema/version validation;
   - RFC 8785 canonical JSON;
   - intent digest;
   - canonical 128-bit ID utilities;
   - no LibLCM dependency.
2. `SIL.LCAtom.Model`
   - canonical semantic snapshot;
   - model-surface coverage manifest;
   - LibLCM normalization adapters;
   - semantic and artifact digests.
3. `SIL.LCAtom.Runner`
   - identity resolution;
   - assessment and expected effects;
   - semantic lowering into an output-only mutation plan;
   - preconditions and conflict diagnostics;
   - atomic apply, read-back, invariant validation, and receipt.
4. `SIL.LCAtom.Diff`
   - two-way mechanical diff;
   - common-ancestor three-way comparison;
   - deterministic ordered-sequence edit synthesis.
5. `SIL.LCAtom.Cli`
   - optional process/JSON adapter for Python and isolated evaluation;
   - owns project lifecycle only as a host, never as core semantics.
6. `SIL.LCAtom.HermitCrab`
   - the HermitCrab authoring-surface projection (`Expand`/reverse-HCLoader), a versioned projection
     rather than canonical intent, owned alongside the runner as its own package and CLI verbs per
     [ADR 0001](adr/0001-hermitcrab-projection-not-canonical.md).
7. Test and conformance projects
   - contract unit tests;
   - LibLCM integration tests;
   - normative JSON fixtures;
   - supported LibLCM/FieldWorks compatibility tests.

Exact assembly count may be reduced if dependency boundaries remain enforceable. In particular,
the contract package must remain LibLCM-free and the runner must remain UI-free.

## Three artifacts, not one mutable document

### Canonical Change Set

Portable ordered semantic intent. It is not intrinsically tied to one baseline and contains no
runner-filled mutable fields.

### Change Set Assessment

Read-only evaluation of one intent digest against one semantic baseline. It records applicability,
resolved identities, before-state evidence, mutation plan, expected effects, warnings, conflicts,
impact, and runtime/model versions. This summary is not exhaustive; the
[contract](change-set-contract.md#assessment) lists the normative fields, including ingestibility and
effect drift.

### Application Receipt

The realized atomic transition:

```text
baselineSemanticDigest --intentDigest--> resultSemanticDigest
```

It includes per-operation outcomes, accepted warnings, mappings such as canonical entity ID to
storage GUID, and runner/LibLCM versions.

Keeping these separate prevents previewing a Change Set from changing its identity. A convenience
API may return them together, but their hashes and schemas remain independent.

A later host save may produce a separate `ArtifactAttestation` linking a Receipt to an exact
`.fwdata` byte digest. It cannot be part of the core Receipt because core application deliberately
does not save the project.

## Public semantic layer and private lowering layer

The public operation vocabulary expresses meaningful actions such as create an entry, add a sense,
set an alternative, attach a reference, clear a field, delete an entity, or move an ordered item.

The runner lowers these into a `LibLCM Mutation Plan` containing exact factories, owners, fields,
references, sequence positions, and cleanup effects. The plan is deterministic output and may be
shown to reviewers. It is never executable input supplied by callers.

There is no operation for:

- arbitrary property assignment;
- invoking a method by name;
- embedding C#, Python, or expressions;
- reflection-based access;
- raw `.fwdata` XML patches.

If a legitimate model feature is missing, add and version a semantic operation.

## Cache and transaction ownership

Core APIs accept an already-loaded `LcmCache`. They do not:

- open a project;
- choose a project path;
- save or close it;
- dispose the cache;
- manage external file backups or locks.

Preview and assessment are non-mutating.

Apply uses one outer LibLCM `UndoableUnitOfWorkHelper` for the entire Change Set. Rollback remains
enabled until all operations execute, the model is read back, and postconditions and invariants
pass. Only then is the unit committed.

There is no partial-apply mode. Individual operation services construct or execute inside the
provided application scope; they never own a nested transaction.

**Open risk — schema mutation inside the outer unit of work.** Flexicon learned by data loss (1,392
stranded senses) that calling LibLCM's `AddCustomField` while a unit of work is already open creates
the field in memory only; the operation appears to succeed, then `SaveChanges` throws and the
`.fwdata` is left referencing a field whose schema addition never persisted. A `customField/define`
operation inside the single outer Change Set unit of work may reproduce this. Whether
schema-changing operations must instead commit in their own prior unit of work — breaking the
one-outer-UoW rule for that one family — is validated by a Phase 0 spike before v1 (see
[implementation plan](implementation-plan.md) and [Flexicon harvest](flexicon-harvest.md)).

LibLCM undo/redo is the v1 rollback mechanism. Do not build a shadow project, handwritten inverse
log, or filesystem transaction. The apply API returns either an Application Receipt or a typed
Application Failure; it never returns both. If rollback itself fails, throw a dedicated critical
exception after collecting all safely available diagnostics. The runner cannot mark LibLCM's cache
object as poisoned, so the exception contract requires the host to discard that cache instance.

FieldWorks integration may require an application-provided transaction/undo coordinator so the
runner participates correctly in the shell's undo stack. This changes host coordination, not the
one-change-set atomicity rule.

## Model coverage

Version 1.0 must classify 100% of the supported LibLCM model surface, even though it need not make
every member mutable.

Generate a raw inventory from LibLCM metadata and check in a reviewed classification manifest.
Every class and field is classified as one of:

- `semantic-operation`;
- `supporting-detail`;
- `custom-field`;
- `derived-read-only`;
- `internal`;
- `runner-bookkeeping`;
- `unsupported`.

`runner-bookkeeping` marks model surface this runner writes but deliberately omits from the semantic
projection and from expected effects — currently only the
[applied-change log](applied-log.md). It is distinct from `unsupported`: the runner does write here,
and the exclusion is what keeps timestamps and identities out of every digest.

Semantic properties additionally carry a **comparison class** — unordered, positionally ordered, or
semantically ordered (feeding) — declaring how far drift comparison reaches into neighbors. See
[comparison footprint](change-set-contract.md#comparison-footprint). It is reviewed and
reclassifiable like any other manifest attribute.

Members on the HermitCrab-projected `Ph*`/`Mo*`/`Fs*` surface additionally carry a **frame/fill tag**
(structure-establishing versus structure-filling) — the single classification that drives both
fail-closed expansion and coverage; see
[HermitCrab projection](hermitcrab-projection.md#coverage-manifest-unification).

CI fails when an upgraded LibLCM package introduces or changes an unclassified member, when a
classification has no rationale, or when an operation family does not cover its declared model
surface. Humans approve classifications.

This is the guard against a supposedly complete diff silently ignoring data.

## Versioning

Compatibility is **detected, not declared**. A version number is a human claim about behavior, and
the failure most worth catching is a change wrongly believed compatible — exactly what a claim cannot
catch. This design already observes behavior directly, so observation governs and version numbers
serve a much smaller role. See
[ADR 0002](adr/0002-effect-comparison-over-declared-compatibility.md).

Three mechanisms, each with one job:

1. **Strict closed parsing** decides whether a Change Set can be ingested at all. It is the only
   mechanism that can, because an operation the runner does not understand cannot be lowered, so no
   behavior exists to observe.
2. **Declared group versions** make that refusal actionable — naming what to upgrade to or rewrite.
   They are not inputs to a compatibility computation.
3. **Effect comparison** decides whether an ingestible Change Set still means what it meant, by
   re-assessing and diffing `expectedEffects` against the recorded Assessment.

The governing cut for digests is **interface versions may enter them; implementation versions never
do**:

| Axis | Kind | Where it appears |
| --- | --- | --- |
| Contract group versions | interface | intent digest |
| `projectionVersion` | interface | semantic digest preimage |
| Runner version | implementation | Assessment/Receipt provenance |
| LibLCM assembly/model version | implementation | Assessment/Receipt provenance |
| Coverage-manifest version | implementation | Assessment/Receipt provenance |

A runner patch release must never change an identity, so implementation versions stay out of every
preimage while remaining reported provenance.

### Effect comparison

Version drift and baseline drift are the same event: the world moved under a recorded Assessment. They
are handled by one rule. When a Change Set is re-assessed or applied against a prior Assessment, any
difference in `expectedEffects` is a typed diagnostic carrying the full delta, resolved by
application policy and never auto-accepted.

Comparison is over effects, not the Mutation Plan. An improved lowering yields a different plan and
identical effects; treating plan inequality as drift would warn on every upgrade and train operators
to click through the warnings that matter. A changed default, by contrast, moves the effects and is
therefore surfaced — which is the property being protected.

### Contract groups

Contract versions are **per endpoint group**. Operation `kind` is already group-namespaced
(`lexical/entry/create`); the leading segment is the group, aligned to the coverage manifest's
coherent domain families. A Change Set declares a `contractVersions` map naming exactly the groups
its operations use, validated as neither short nor padded, so the hashed map depends only on authored
content and never on the runner's own version table. There is no independent per-*operation* version.

Released semantics are immutable within a group's major version. A runner that cannot honor a
declared group version rejects the Change Set and names the group, the version required, and the
version it carries. This repository does not maintain a compatibility matrix or a governed
additive/non-additive classification; effect comparison supersedes both.

### Projection stability

The semantic projection is **additive-stable**: members semantically indistinguishable from absent
are omitted entirely, so classifying a newly shipped LibLCM member is digest-preserving for every
model that does not populate it. Where LibLCM distinguishes unset from set-to-default, the projection
must too.

`projectionVersion` bumps only on observable projection change — altered normalization, non-additive
reclassification, changed rich-text classification. It is folded into the semantic digest preimage,
so additive upgrades preserve stored digests while non-additive changes cannot produce colliding hex.

A digest mismatch across projection versions is not a broken lineage. It is the trigger to re-assess
and compare effects. Additive stability exists to keep that re-assessment rare; folding the version
into the preimage exists so two different projections cannot yield equal hex.

The one genuine coupling between projection and contract is caught by the same oracle: if the
projection stops distinguishing something an operation can set, that operation has become a semantic
no-op, and the effect delta makes it visible rather than leaving it to a version bump nobody
classified correctly.

Unknown operation kinds or semantic properties are rejected. Optional tool data lives only in an
explicit `extensions` object and is ignored by execution and excluded from semantic-state and
intent digests unless a later contract explicitly states otherwise.

## Storage boundary

The runner receives Change Sets from “somewhere” and returns Assessments and Receipts. It provides
content hashes and lineage facts so another system can use Git, a database, an object store, or a
combination.

This repository does not decide:

- whether Change Sets are Git commits;
- whether `.fwdata` checkpoints are versioned;
- how proposals are discussed or approved;
- how review queues and permissions work;
- how 1,000 projects are hosted.

An `.fwdata` file may be stored as an opaque checkpoint, but it is never automatically merged.

## Parallel candidate evaluation

Do not assume a live `LcmCache` is thread-safe or cheaply cloneable. Initial parallelism should use
isolated processes and independently opened temporary project snapshots behind the same runner
contract. In-memory cloning can be added later only after LibLCM behavior is proven.
