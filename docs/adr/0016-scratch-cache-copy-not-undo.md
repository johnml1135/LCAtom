# ADR 0016 — Dry runs use a scratch cache copy, never Undo or Rollback

Status: accepted (2026-08-01)

Supersedes the mutate-then-rollback mechanism that
[ADR 0006](0006-engine-reality-apply-readback-preflight.md) decision 3 works around. ADR 0006's other
decisions stand unchanged — in particular decision 1 (read back, do not replay), which is *why* a dry
run must mutate something rather than compute effects analytically.

## Context

`ProposalDryRunner` mutates the live `LcmCache` inside an `UndoableUnitOfWorkHelper` that is never
committed, so `Dispose()` rolls back. Three facts make that unsafe as the interactive design:

1. **Rollback is not Undo.** `UndoStack.Rollback` skips the forward-only setter hooks `Undo`/`Redo`
   run, so `LexEntry` headword/homograph and `MoStemAllomorph` monomorphemic derived caches go stale
   relative to a correctly-reverted object graph. No non-committing invalidation of them is reachable
   through LibLCM's public surface — `RollbackCacheInvalidator`'s remarks record that
   `ILexEntryRepository.ResetHomographs`, the only candidate, is not safe to call there.
2. **Undo is not a safe substitute either.** `NonUndoableUnitOfWorkHelper`
   (`liblcm/src/SIL.LCModel/Infrastructure/NonUndoableUnitOfWorkHelper.cs`) makes non-undoable units
   a first-class LibLCM concept, and [ADR 0005](0005-schema-operations-non-undoable-uow.md) already
   documents one instance in scope. Building the interactive loop on "Undo restores it" would require
   auditing all 898 manifest rows for exceptions, and re-auditing on every LibLCM bump.
3. **Poisoning is terminal.** `CacheReusability` tracks poisoning per `LcmCache` instance with no
   un-poison path; recovery is a fresh cache, i.e. closing and reopening the project. Worse,
   `ProposalApplier` calls `RollbackCacheInvalidator.InvalidateReachableCaches` from its catch block
   on **any** exception for **any** operation kind, without consulting
   `DerivedCachePoisoningOperationKinds` — so one failed apply ends the session's Motif capability.

Meanwhile `LcmCache.CreateCacheCopy` (`liblcm/src/SIL.LCModel/LcmCache.cs:177`) is public and copies
at the **surrogate** level: `BackendProvider.DoPortWithoutBootstrapping` iterates
`sourceIdentityMap.AllObjectsOrSurrogates()`, creates a surrogate per object, and registers each as
*inactive* — objects reconstitute lazily on first access. `BackendProviderType.kMemoryOnly` gives a
target with no on-disk store. It reads the identity map, so it captures FieldWorks' unsaved edits, and
needs no file, no save, and no lock.

`CmObjectSurrogateFactory.Create(ICmObjectOrSurrogate)` has two costs: a reconstituted `ICmObject`
source pays `ToXmlString()`, a dormant surrogate source is a cheap copy-construct. So copying *from a
pristine, untouched scratch* is materially cheaper than copying from a hot live cache.

## Decision

1. **Serialize heavily once.** Take one `CreateCacheCopy` from the live FieldWorks cache into a
   `kMemoryOnly` **pristine scratch**. This is the expensive copy: every object the user has browsed
   in the live cache pays a `ToXmlString()`.
2. **Fan out cheaply, N times.** Derive per-use caches from the pristine scratch, not from the live
   cache. The scratch stays dormant, so each derived copy takes the surrogate-to-surrogate path and
   each reconstitutes only the objects its work actually touches. One pristine scratch serves N
   proposal dry runs and N PanGloss snapshot/parser runs.
3. **Footprint analysis on the live cache gates the re-copy, and Apply stays on the live cache.**
   `FootprintProbe.ComputeCurrentFootprintDigest` is a pure read, always correct and cheap. Compare it
   against the scratch's before reusing the scratch: equal means dry-run on the existing scratch,
   different means re-copy first. Apply itself continues to run against the live cache in one
   `UndoableUnitOfWorkHelper`, still bound to a prior DryRun anchor per
   [ADR 0004](0004-prerequisite-graph-stable-ids-bound-apply.md) decision 3. The user's unrelated
   edits do not force a re-copy; only edits inside a proposal's footprint do.
4. **A prerequisite DAG is evaluated by applying its closure to one scratch in topological order.**
   [change-set-contract.md](../change-set-contract.md) already requires assessment against "the state
   LibLCM would be in with its full prerequisite closure already applied, in topological order", but
   assumes that in a live project the prerequisites are *already in history*. The scratch extends this
   to a closure of **un-applied** Proposals: apply the topologically-sorted closure to one derived
   scratch, then dry-run the dependent Proposal on top. Effects are still read back, never replayed.
   The scratch is discarded afterwards regardless of outcome.
5. **Nothing depends on Undo or Rollback for correctness.** A derived scratch is discarded, never
   reverted. Whether a mutation left a derived cache stale stops mattering, because the cache that
   holds it is thrown away.

## Consequences

- **Three types retire once 1–3 ship**: `CacheReusability`, `RollbackCacheInvalidator`, and
  `DerivedCachePoisoningOperationKinds` — along with the hand-maintained kind list its own remarks
  admit "can drift out of sync with the real operation vocabulary". A correctness obligation that had
  to be permanently right becomes a performance question that can be measured.
- The manifest column `AssessPoisonsCache` loses its consumer. Do not delete it yet: it is still the
  honest answer to "does this operation touch a forward-only derived cache", which the LibLCM upstream
  fix below would want.
- **Still worth fixing upstream.** That `Rollback` skips hooks `Undo` runs, and that no public
  non-committing invalidation exists, remains a LibLCM gap. SIL owns LibLCM and is already in that
  codebase for the Avalonia/`net10.0` migration. This ADR routes around the gap; it does not close it.
- Dry runs no longer observe a live cache, so a *stale* scratch is possible where a poisoned live
  cache used to be. That is a safe failure: the decision-3 footprint probe detects it and Apply's own
  drift check hard-stops. Staleness costs a re-copy, not a session.
- PanGloss runs get a pinned baseline for free, which they need anyway: a grammar revision identity is
  meaningless if the baseline drifts mid-run.
- The Runner keeps its existing contract — it operates on an already-loaded cache it does not own
  (`SIL.Motif.Runner.csproj`), and is indifferent to whether that cache is live or scratch. No Runner
  API changes.

## Unverified — measure before building on this

- **`CreateCacheCopy` has zero callers** in `liblcm` or `FieldWorks` source (matches appear only in
  compiled assemblies). It is public, plausible, and untested in practice.
- The wall-clock cost of one `CreateCacheCopy` from a hot Sena-3-scale cache into `kMemoryOnly`, and
  of a derived copy from a pristine scratch. Decision 1 versus 2's whole value is the ratio between
  them; both are asserted from the code path, neither is measured.
- Whether a second `LcmCache` coexists cleanly inside the FieldWorks process. The service locator is
  per-cache and `InitializeWritingSystemManager` is called on the copy, so it looks sound; ICU
  initialization is more global and was not traced.
- Whether `IProjectIdentifier` is publicly constructible with `Type = kMemoryOnly` in the form
  `CreateCacheCopy` needs.
