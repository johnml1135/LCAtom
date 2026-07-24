# ADR 0005 — Custom-field (schema) operations run in a separate non-undoable unit of work

Status: accepted (2026-07-24)

## Context

LCAtom applies a whole Change Set in one outer `UndoableUnitOfWorkHelper`. Flexicon lost 1,392 senses
(issue #21) by adding a custom field inside an open unit of work. Three repositories were read to
learn how the battle-hardened tools sequence a metadata (schema) change against data changes; the
project is new and should mirror them rather than invent a pattern. See
[Flexicon harvest](../flexicon-harvest.md).

## Findings

- **LibLCM.** `AddCustomField`/`DeleteCustomField`/`UpdateCustomField` are untracked in-memory
  metadata mutations (`LcmMetaDataCache.cs:920-1102`); the data `Rollback` cannot undo them. "Commit
  at wrong place" is thrown whenever `Save`/`Commit` runs while any task is open
  (`UndoStack.cs:239-246`, requiring the `ReadyForBeginTask` state). Custom-field persistence is
  diff-driven and independent of the undo stack (`BackendProvider.cs:482-524`), so a field added
  inside a task that is then rolled back survives in memory and is written on the next unrelated
  save — the corruption Flexicon saw.
- **FieldWorks (the reference).** `AddCustomFieldDlg.cs:487` wraps the metadata mutation in a
  `NonUndoableUnitOfWorkHelper.Do(...)` on a dedicated non-undoable stack (`FieldDescription.cs:336-417`),
  issues no separate save, adds a schema-specific dirty-check so a metadata-only change still commits
  (`BackendProvider.HaveAnyModifiedCustomProperties`), and forces a full cache/view rebuild
  (`MasterRefresh`) afterward. Custom-field changes never enter the data undo stack and are one-way
  ("cannot be undone").
- **Flexicon.** Refuses `AddCustomField` while a unit of work is open and documents the same
  corruption; the same lesson, enforced defensively.

## Decision — mirror FieldWorks

1. Metadata/schema operations — the custom-field family (`customField/define`, update, delete) —
   execute in their own **non-undoable** unit of work, distinct from the outer undoable unit of work
   used for data operations. This amends "one outer `UndoableUnitOfWorkHelper` for the entire Change
   Set": apply is two-phase, and the schema phase is non-undoable.
2. The schema phase runs **first**, so data operations can reference newly-defined flids.
3. `Save`/`Commit` is never called while any unit of work is open; a commit happens only after a
   phase's task has closed.
4. Schema changes are **one-way**. LibLCM's data rollback cannot revert them, and — like FieldWorks —
   LCAtom does not try. A Change Set whose schema phase succeeds but whose data phase then fails leaves
   the custom field defined but empty. That leftover is **not automatically idempotent**:
   `AddCustomField` throws on a duplicate name (`LcmMetaDataCache.cs:967-983`), so a retry must run the
   [custom-field](../custom-fields.md) ensure/resolve pre-check (absent → create; present and
   compatible → reuse) *before* `AddCustomField`, every time, including after a crash. Because the
   schema phase writes no applied-log entry of its own, a crash between the schema commit and the data
   phase leaves the idempotence check reporting "never applied"; the retry is safe only because that
   ensure pre-check treats the existing field as reuse. Where consistency cannot be assured, the cache
   is discarded per the rollback-failure contract.
5. The commit gate must treat a metadata-only change as dirty (mirroring
   `HaveAnyModifiedCustomProperties`), so a schema-only Change Set is not a silent no-op.
6. Read-back after a schema change rebuilds metadata-dependent projections against the new flid rather
   than trusting incremental notifications (mirroring `MasterRefresh`).

Scope: writing-system and part-of-speech creation are ordinary model-object operations in the normal
undoable unit of work — they are not `IFwMetaDataCacheManaged` schema mutations and do not use this
phase. This decision is specifically the custom-field metadata family.

## Consequences

- The transaction model becomes a two-phase apply: a non-undoable schema phase, then the undoable
  data phase.
- The atomicity guarantee is stated honestly: data operations are atomic; custom-field schema
  operations are one-way and survive a data-phase rollback as a benign empty definition.
- Conformance asserts the mirrored pattern: no save while a task is open, schema in its own
  non-undoable unit of work, a metadata-only change still persists, and a failed data phase leaves a
  defined-but-empty field, never orphaned data referencing an unpersisted field.
