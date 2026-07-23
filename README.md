# LibLCM Change Sets

LibLCM Change Sets is the canonical C# contract and reference runner for applying portable,
reviewable, semantic CRUD+ change sets to an already-loaded LibLCM model.

The project is deliberately storage- and workflow-agnostic. A change set can come from a file,
Git repository, database, web service, AI agent, FieldWorks panel, or another application. The
runner gives that change set one meaning, assesses it against a specific model, and can apply it
atomically through LibLCM's unit-of-work machinery.

This repository is initially a specification and implementation plan. Start with:

- [Architecture and decisions](docs/architecture.md)
- [Normative change-set contract](docs/change-set-contract.md)
- [Custom fields](docs/custom-fields.md)
- [Conflict and rebase semantics](docs/conflicts-and-rebase.md)
- [Implementation plan](docs/implementation-plan.md)
- [Implementation-session handoff](HANDOFF.md)

## Product boundary

This repository owns:

- the versioned semantic operation vocabulary;
- JSON parsing, validation, and canonicalization;
- canonical entity-ID/GUID conversion;
- canonical semantic snapshots and digests;
- deterministic two-way and common-ancestor three-way mechanical diff;
- assessment, planning, conflict diagnostics, rebase, atomic apply, read-back, and receipts;
- conformance fixtures proving that all supported clients receive the same behavior.

It does not own:

- opening, saving, closing, locking, backing up, or disposing FieldWorks projects;
- review queues, permissions, approvals, hosting, Git history, or database storage;
- linguistic entity matching between unrelated projects;
- arbitrary C#, Python, reflection, or raw-property mutation;
- UI behavior;
- a competing implementation in Flexicon, GramTrans, FlexToolsMCP, or FieldWorks.

LibLCM remains the authority for model invariants, persistence, ownership, and undo/redo. This
runner is the semantic interoperability layer above it.

## Governing invariant

For every supported state:

```text
Normalize(Apply(A, Diff(A, B))) == Normalize(B)
```

Application is atomic at the whole-change-set boundary. The runner never silently reorders
operations, guesses linguistic identity, changes authored intent during rebase, or partially
applies a change set.

