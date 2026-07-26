# LCAtom

*Atomic operations on FieldWorks' LibLCM data.*

LCAtom is the canonical C# contract and reference runner for applying portable,
reviewable, semantic CRUD+ change sets to an already-loaded LibLCM model.

**Why it exists.** A person or an AI working on a language asks *"what if we change this — does the
text parse better?"* LCAtom makes that question safe to ask, cheap to repeat, and honest to answer:
author a change at a high level, review the exact state delta before anything is touched, project the
would-be grammar to HermitCrab, let [PanGloss](https://github.com/sillsdev/machine) parse a text and
report, compare against earlier runs, then keep it or throw it away. Everything HermitCrab supports
must be authorable here, in a friendly way — that is the primary completeness criterion. See
[ADR 0010](docs/adr/0010-hermitcrab-experimentation-is-the-primary-purpose.md).

The project is deliberately storage- and workflow-agnostic. A change set can come from a file,
Git repository, database, web service, AI agent, FieldWorks panel, or another application. The
runner gives that change set one meaning, assesses it against a specific model, and can apply it
atomically through LibLCM's unit-of-work machinery.

This repository is initially a specification and implementation plan. Start with:

- [Architecture and decisions](docs/architecture.md)
- [Rationale](docs/rationale.md)
- [Decision records](docs/adr/)
- [Normative change-set contract](docs/change-set-contract.md)
- [Custom fields](docs/custom-fields.md)
- [Applied-change log](docs/applied-log.md)
- [HermitCrab projection](docs/hermitcrab-projection.md)
- [Flexicon harvest](docs/flexicon-harvest.md)
- [HC grammar map — the normative grammar write-surface](docs/hc-grammar-map.md)
- [HC surface scope — three coverage tiers](docs/hc-surface-scope.md)
- [API surface layer 1 — the LibLCM primitive surface](docs/api-surface-layer1.md)
- [API surface — the HC grammar-facing surface](docs/api-surface-hc.md)
- [Issues register](docs/issues.md)
- [Stress-test findings](docs/stress-test-findings.md)
- [Stage-2 change management (vision)](docs/stage2-change-management.md)
- [Conflict and rebase semantics](docs/conflicts-and-rebase.md)
- [Implementation plan](docs/implementation-plan.md)
- [Operation-catalog plan (lexical & grammar)](docs/operation-catalog-plan.md)
- [Build stages](docs/build-stages.md)
- [Implementation-session handoff](HANDOFF.md)

## Product boundary

This repository owns:

- the versioned semantic operation vocabulary;
- JSON parsing, validation, and canonicalization;
- canonical entity-ID/GUID conversion;
- canonical semantic snapshots and digests;
- deterministic two-way and common-ancestor three-way mechanical diff;
- assessment, planning, conflict diagnostics, rebase, atomic apply, read-back, and receipts;
- a thin applied-change log written into the project for provenance and idempotence;
- conformance fixtures proving that all supported clients receive the same behavior.

It does not own:

- opening, saving, closing, locking, backing up, or disposing FieldWorks projects;
- review queues, permissions, approvals, hosting, Git history, or database storage;
- linguistic entity matching between unrelated projects;
- arbitrary C#, Python, reflection, or raw-property mutation;
- UI behavior;
- the Notebook, scripture, and interlinear-text analysis surface, which remains FieldWorks-centric;
- a competing implementation in Flexicon, GramTrans, FlexToolsMCP, or FieldWorks.

LibLCM remains the authority for model invariants, persistence, ownership, and undo/redo. This
runner is the semantic interoperability layer above it.

## Motivating consumers

This runner exists because several independent tools each need the same thing — a mechanical,
repeatable, reviewable, sequenceable, and rebasable way to update the LibLCM model for **dictionaries
and grammars** — and each is otherwise forced to hand-roll raw property mutation against LibLCM. It is
being built for them, and they are expected to be refactored to call this compiled engine rather than
each reimplementing apply, ordering, delete-closure, and conflict semantics:

- **Linguistic Assistant** — an AI quality-assurance and language-documentation assistant that
  *emits* canonical Change Sets (lexical, morphophonology, and bilingual sense-link tiers) from corpus
  and parallel-text evidence. It already speaks this contract's vocabulary and independently reached
  the same "no CRDT for ordered grammar" conclusion; this repository is the conforming runner it
  targets.
- **PanGloss** — a Rust HermitCrab/FST morphological-parsing engine that emits FieldWorks
  investigation handoffs when a grammar fails to parse a word. The grammar and morphophonology edits
  those handoffs motivate are realized as Change Sets against the LibLCM grammar model.
- **Flexicon** — [github.com/MattGyverLee/flexicon](https://github.com/MattGyverLee/flexicon) — a
  Python (`pyflexicon`) wrapper over LibLCM for reading and writing FLEx projects.
- **FlexToolsMCP** — [github.com/MattGyverLee/FlexToolsMCP](https://github.com/MattGyverLee/FlexToolsMCP)
  — an MCP server that lets AI assistants manipulate FLEx lexicon data in natural language over the
  LibLCM/flexlibs API surface.
- **GramTrans** — [github.com/MattGyverLee/GramTrans](https://github.com/MattGyverLee/GramTrans) — a
  FlexTools module that transfers grammar components (phonology, morphology, lexicon scaffolding,
  templates) between FLEx projects.

The shared scope is dictionaries and grammars, including the phonological and morphological model that
Hermit Crab and PanGloss consume. It deliberately excludes the Notebook, scripture, and
interlinear-text analysis surface, which stays FieldWorks-centric.

## Governing invariant

For every supported state:

```text
Normalize(Apply(A, Diff(A, B))) == Normalize(B)
```

Application is atomic at the whole-change-set boundary. The runner never silently reorders
operations, guesses linguistic identity, changes authored intent during rebase, or partially
applies a change set.

