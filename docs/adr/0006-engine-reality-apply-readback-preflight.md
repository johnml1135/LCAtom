# ADR 0006 — Engine-reality constraints on apply, read-back, and pre-flight

Status: accepted (2026-07-24)

## Context

Stress tests against `liblcm` (scale and transaction/concurrency) both validated the transaction
model and surfaced engine behaviors Motif must accommodate. This extends
[ADR 0003](0003-feasibility-findings.md). Evidence: [stress-test findings](../stress-test-findings.md).

## Decisions

1. **Read-back before commit is sound.** Reads are legal at any transaction state (no getter checks
   it), and side effects — ownership cascade, computed defaults — apply *synchronously* as each
   mutation happens, before `EndUndoTask` (`CmObject.cs:1695-1723`; PropChanged fires synchronously
   inside `EndUndoTask`, `UndoStack.cs:343`). So capturing the effect set by snapshot-diff inside the
   open unit of work, before commit, sees the true cascaded state. Caveat: a few behaviors run only at
   task close, after read-back (DateModified stamping, `UndoStack.cs:294-321`); a Phase 0 spike must
   confirm no *semantic* side effect is task-close-only, or the effect closure would under-report it.

2. **`ReferringObjects` has a first-touch whole-project cost.** `EnsureCompleteIncomingRefs` →
   `GetIncomingFields` walks the class hierarchy to `CmObject` (`clid 0`) and, for the populous
   generic `sig="CmObject"` fields, force-fluffs every instance of the owning classes
   (`LcmMetaDataCache.cs:1107-1122`, `RepositoryAdditions.cs:326-356`). Footprint scoping bounds which
   fields are *compared*, not the first-touch index construction. The host therefore **warms the
   incoming-reference index at project load, off the interactive path**; the "near-instantaneous
   pre-flight" promise is conditioned on that warm-up. Whole-project snapshot work (onboarding,
   two-way diff, first baseline digest) is inherently expensive and is stated separately from the
   interactive pre-flight promise, not folded into it.

3. **Rollback is not Undo.** `UndoableUnitOfWorkHelper` dispose-with-rollback calls
   `UndoStack.Rollback`, which skips `ClearCachesOnUndoRedo` and the forward-only setter hooks that
   `Undo`/`Redo` run (`UndoStack.cs:616,667`), leaving stale derived caches: `MoStemAllomorph`
   monomorphemic data, `LexEntry` headword (`RepositoryAdditions.cs:1184`) and homograph
   (`:1247`) indexes. The object graph and IdentityMap themselves revert correctly. On a rolled-back
   apply the runner must invalidate those caches (call `ClearCachesOnUndoRedo` and reset the headword
   and homograph indexes) or discard the cache instance per the existing rollback-failure contract.

4. **Apply requires exclusive write access; a colliding writer causes silent, misattributed loss.**
   "Single-writer" is not an enforced lock — `ReaderWriterLockSlim`'s "UI thread only" is a comment,
   and a second writer is rejected by the state check before it waits on the lock. A colliding
   `BeginUndoTask`/`Save` calls `Rollback(0)`, which destroys the *entire* open bundle
   (`UndoStack.cs:705-725`), and Motif's own `EndUndoTask` then throws "Cannot end task that has not
   been started" — indistinguishable from its own rollback. The periodic 1-second autosave is benign
   (it no-ops while a task is open, `UnitOfWorkService.cs:240-241`); the real threats are any *other*
   writer, including FieldWorks' shutdown `Save()` from a background thread, which has no skip guard
   (`FieldWorks.cs:3919`). Apply therefore requires a host-provided guarantee of exclusive write access
   for its duration (the coordinator architecture.md defers), and Motif must detect the collision —
   treating an unexpected transaction state at task end as an external-collision diagnostic, not its
   own decision.

5. **Never nest a unit of work; never call a bare `UndoableUnitOfWorkHelper.Do` from lowering.**
   Nesting throws and `Rollback(0)`s the whole change set (`UndoStack.cs:194`). Several LibLCM
   convenience methods open their own bare unit of work (`LexEntry.MoveSenseToCopy`,
   `Text.AssociateWithNotebook`); lowering uses `DoUsingNewOrCurrentUOW` (join-or-open) or avoids them,
   and code review greps `UndoableUnitOfWorkHelper.Do(` in any reused LibLCM path first.

## Consequences

- Effect capture stays inside the open unit of work (before commit) — validated, not merely assumed.
- Pre-flight documents a load-time incoming-reference warm-up; whole-project operations are budgeted
  separately from interactive checks.
- The rollback path gains explicit derived-cache invalidation or cache-discard.
- Apply gains a host-coordination precondition (exclusive write access) and a collision-detection
  requirement so external interference is not misread as Motif's own rollback.
- Lowering carries a no-nested-UoW discipline for reused LibLCM convenience methods.
