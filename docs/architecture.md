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
6. Test and conformance projects
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
impact, and runtime/model versions.

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
- `unsupported`.

CI fails when an upgraded LibLCM package introduces or changes an unclassified member, when a
classification has no rationale, or when an operation family does not cover its declared model
surface. Humans approve classifications.

This is the guard against a supposedly complete diff silently ignoring data.

## Versioning

One `contractVersion` applies to the entire Change Set. There is no independent per-operation
version.

Released semantics are immutable within a major version. Breaking interpretation requires a new
major contract version and, where feasible, an explicit migration tool. The runner reports:

- contract version;
- runner version;
- LibLCM assembly/model version;
- coverage-manifest version;
- canonicalization/snapshot version.

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
