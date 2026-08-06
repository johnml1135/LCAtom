# ADR 0016 — Dry runs use a scratch cache copy, never Undo or Rollback

Status: accepted (2026-08-01). **Amended twice** — 2026-08-05 (*Verified, and two hazards this ADR did not
anticipate*) and 2026-08-06 (*there is exactly one rollback, and the apparatus is deleted rather than kept*),
which is the one to read first: it shrinks this ADR's consequences to a deletion.

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

## Verified, and two hazards this ADR did not anticipate

**Amendment, 2026-08-05.** Research against the liblcm and FieldWorks checkouts —
[full note](../research/2026-08-05-createcachecopy-provenance-and-hazards.md).

**Confirmed from source:** the two-cost model above is exactly right (`RawXmlBytes` is nulled on fluffing
at `CmObjectSurrogate.cs:519`, so the copy constructor at `:176-192` falls to `sourceSurrogate.XML` →
`ToXmlString()` at `:441-455`); the work is O(n objects), not per-field; `kMemoryOnly` genuinely performs
zero disk I/O; two `LcmCache` instances coexist in one process (`BEPPortTests` holds both live), with no
unsafe statics — ICU init is idempotent by design and `CmObjectId` interning is per-cache.

**Provenance:** `CreateCacheCopy` was SDK-sample infrastructure for XML ↔ Db4o backend porting. Its
history predates both repos' truncated roots, so original intent is unattributable; Db4o was removed in
2015 and the sample deleted in 2017. Reassuringly, the primitive underneath it,
`RegisterInactiveSurrogate`, runs on every object of every normal project open
(`XMLBackendProvider.cs:840`) — only the **cache-to-cache port path** is untested outside one
blank-project test. **And the timing harness this ADR needs already exists**, recoverable at
`git -C ../FieldWorks show f0d837288^:Samples/ImportExport/ImportExport.cs`, average-of-N mode included.

**Two hazards, neither about speed. Both are silent, and both make a scratch *not equivalent* to the live
cache:**

1. **Writing systems degrade to bare-tag defaults.** For a `kMemoryOnly` target,
   `InitializeWritingSystemManager` returns early (`BackendProvider.cs:548-564`), so no
   `WritingSystemStore` is attached and `GetOrSet` always falls through to `Create(identifier)`
   (`WritingSystemManager.cs:301-321`), synthesizing a definition **from the language tag alone**. Custom
   collation, sort rules, valid characters, keyboards and fonts are absent from the scratch. Anything
   collation- or valid-char-sensitive can diverge from the live cache without any error.
2. **Custom-field `flid`s are re-derived, not preserved.** `AddCustomFields`
   (`LcmMetaDataCache.cs:1132-1142`) discards the source's flid and re-assigns
   (`:936-948`) while enumerating a `HashSet<MetaFieldRec>` (`:66`) whose order is not contractual — so
   with ≥2 custom fields on one class, the same field can land on a **different flid** in the scratch.

**Motif is safe on both today, and the reasons must become stated invariants rather than incidental good
practice.** Writing systems are resolved per cache by tag (`SetGlossLowering`), and
`LexSenseSnapshotter:45` converts handle → tag before storing rather than persisting a raw handle. There
is **no `flid` reference anywhere in `src/`**, and `AGENTS.md` rule 11 already forbids treating flids as
portable identity — a rule that now has a second, sharper reason: **two caches in one process can
disagree.** So, added to this ADR's decisions:

- **Never carry a writing-system handle or a custom-field `flid` across the live/scratch boundary.**
  Resolve writing systems by tag and custom fields by `(ownerClass, internalName)` against each cache's own
  metadata cache, every time.
- **A dry run whose correctness depends on project-specific writing-system behaviour is not yet supported
  on a scratch.** Today's `setGloss` writes and reads a `MultiUnicode` string and is collation-independent.
  The first collation- or valid-char-sensitive operation must either establish that the scratch's writing
  systems are sufficient or run against the live cache under a documented exception.

The remaining spike is therefore narrower and sharper than "how long does it take": it must also
round-trip a project with ≥2 custom fields on one class and a customized writing system, and compare. A
correctness failure there would invalidate this ADR's *design*, not merely its parameters.

## Amended 2026-08-06 — there is exactly one rollback, and the apparatus is deleted rather than kept

The owner's challenge: *"Why would we ever apply and then roll back? Once we create the XML from the live data,
we just duplicate it for every proposal we want to check. We always go from a fresh state, and if there is a
DAG, we just apply them on a fresh copy. I see no instance in which we would revert, ever."*

Almost exactly right. Walking every workflow:

| Workflow | Mutates | Reverts | Poisons |
| --- | --- | --- | --- |
| Dry Run — copy, mutate, read back, **discard** | yes | **no** | no |
| DAG closure — apply prerequisites then the dependent to one fresh copy, discard | yes | **no** | no |
| Reads, reports, diff | no | no | no |
| Apply, succeeds — commits, forward hooks run normally | yes | **no** | no |
| **Apply, fails** | yes | **yes** | yes |

**The single survivor is forced by atomicity, not chosen.** `AGENTS.md` rule 4 — *one complete Change Set is
one atomic LibLCM unit of work* — means a mid-proposal failure on the live cache must unwind, because the
alternative is a half-applied Proposal, which is worse than a stale derived cache. `ProposalApplier.cs:144-155`
already does exactly this.

### The consequence: no classification is needed anywhere

Because the only rollback is *"an apply just failed"*, **nothing needs to know which fields poison.** The rule
is unconditional:

> If an apply throws, the live cache is suspect. Discard it and reload.

So the entire apparatus is deleted rather than maintained:

- **`DerivedCachePoisoningOperationKinds`** — the hand-maintained field list has no remaining purpose. This is
  the real saving: `MOT-4` slice A had to read `OverridesLing_Lex.cs` to establish *why* two fields poison, and
  that cost would have recurred for every new field in the catalog, forever.
- **`AssessPoisonsCache`** (manifest column) — retired. This answers `C10a`.
- **`RollbackCacheInvalidator`** — deleted.
- **`CacheReusability`** — reduces to nothing; the thrown exception already tells the host to reload.

**"Poisoning" leaves the vocabulary.** Not a hazard managed well — a concept removed, which is the stronger
outcome and the one the owner asked for.

### Why the survivor is acceptable rather than a gap

A failed apply is exactly the moment to stop, inspect, and restart from a known state. The cost coincides with
a pause you would want regardless. Contrast the routine case, where a Dry Run leaving the working cache unusable
was intolerable precisely because Dry Runs are constant.

**Rejected: pre-flighting the apply on a copy** to make live failures nearly impossible. It cannot reach zero —
the live cache may hold uncommitted edits the copy lacks — it doubles the cost of every apply, and the bound
Dry-Run anchor already performs the state check. A rare failure is better handled by a reload than by permanent
overhead.

### Save first, and recovery is reload — which is stronger than rollback

The owner's follow-up closed the remaining hole: *"Wouldn't we mandatorily apply the proposal on a LibLCM copy
to validate it, and then use the fingerprint to make sure nothing that affects it changed? Also, before we
apply, don't we save? Could we just reload? Because we cannot always unapply a change."*

Three things follow, and the third is the important one.

**The save belongs before the Dry Run, not before the apply.** The scratch is a copy of the *saved file*, so
uncommitted edits in the live cache are invisible to it: the Dry Run would validate a world the apply will not
meet, and the footprint digests would not match, so apply refuses — failing closed, but reporting "drift" when
nothing drifted. Saving first makes the file equal the live cache, so validation and apply see one world. It
also **buys back the only advantage the in-memory copy had** — seeing uncommitted edits — without the
writing-system loss that disqualified it.

**Saving is what makes reload free.** Reload is only frightening because of unsaved work. After a save there is
none, so reloading returns exactly the pre-apply state.

**And reload is sound where rollback is only usually sufficient.** [ADR 0005](0005-schema-operations-non-undoable-uow.md)
establishes that a custom-field definition runs in a **non-undoable** unit of work and that a failed data phase
leaves the field *defined but empty* — a leftover explicitly "not automatically idempotent". So rollback cannot
restore the prior state in all cases. Reload can:

| | Rollback | Reload from the saved file |
| --- | --- | --- |
| Undoable data changes | reversed | discarded |
| Non-undoable schema phase (ADR 0005) | **survives** | discarded — apply never calls `Save`, so it never reached disk |
| Stale derived caches | **left stale** | gone with the cache |

That resolves ADR 0005's known wart as a side effect rather than as its own problem.

### The full sequence

```
 1  SAVE                  file == live cache; nothing else works without this
 2  copy the file         ~50 ms
 3  open the copy         ~550 ms                    -- the scratch
 4  apply the Proposal    to the scratch, plus any DAG prerequisites
 5  read effects back     from the scratch (ADR 0006 decision 1)
 6  DISCARD the scratch   never reverted
 7  bind the anchor       footprint digest + engine version
    -- time passes --
 8  apply to live         re-check the anchor; refuse on drift
 9  on failure: RELOAD    from the file saved at step 1; lossless
```

Rollback still occurs at step 8 because atomicity demands it, but **nothing depends on it being complete.**
Recovery is step 9.

**Two costs, stated rather than buried.** Saving is a real side effect — in FieldWorks it commits the linguist's
in-flight edits at a moment Motif chose, which is defensible (FieldWorks saves routinely, and the alternative is
validating against a stale world) but must be **visible to the user, not silent**. And a reload is ~1.8 s on
Sena 3, paid only on a failed apply, which after a validated Dry Run and a matching anchor should be rare.

Build cost is nearly nil: `ProposalApplier` already never saves — documented as the host's job — so "save first"
is a **host precondition**, not new Runner code.

### Found while building it: "save" was not a save

The sequence above was written, then implemented, and twelve round-trip tests failed with
**footprint drift on projects nobody had touched.** The anchor's digest matched the project's *pristine*
state while the live cache had already moved on — so the scratch was reading a file that was one
operation behind.

`FwDataProjectLoader.Save` did what `FwDataMiniLcmBridge` does: `cache.ActionHandlerAccessor.Commit()`.
That call does **not** write the file. `XMLBackendProvider.PerformCommit`
(`Infrastructure/Impl/XMLBackendProvider.cs:458-467`) enqueues a `CommitWork` item on a background
`ConsumerThread` and returns; the write lands whenever that thread gets to it. `IUndoStackManager.Save()`
is no better — `UnitOfWorkService.SaveInternal` reaches the same `Commit`. **LibLCM has no synchronous
save.** Its own answer is a separate barrier, `CompleteAllCommits()`, which waits for that thread to go
idle, and both places liblcm needs the file to be authoritative pair the two calls:
`ProjectLockingService.UnlockCurrentProject` (`DomainServices/ProjectLockingService.cs:37-41`) and
`ProjectBackupService` (`:61`).

Three things follow.

**1. Save-first is not merely a good idea, it is load-bearing — and it had a hole in it.** The decision
above assumed "save" meant "the file is current". It did not, and nothing had ever noticed because until
now nothing read the file straight after saving. The Dry Run copying a file is what made an
eleven-year-old asynchrony visible, which is a fair argument that the copy-based design is *more*
observable than the rollback-based one it replaces, not merely safer.

**2. The barrier is not publicly reachable, and that is an upstream ask.** `CompleteAllCommits` is
declared on the `internal` `IDataStorer`. The only route from outside is the public
`ILcmServiceLocator.DataSetup` property — the very same backend-provider instance, as liblcm's own
`LcmCache.cs:631` shows by casting it — and then its `public virtual CompleteAllCommits` by reflection.
Motif does that, once, in `FwDataProjectLoader.Save`, and **throws rather than degrading** if the member
ever disappears: silently reverting to the asynchronous behaviour would restore a bug whose symptom
accuses the drift check instead of the save. **Making a synchronous save publicly reachable is a liblcm
PR worth filing** (`ProjectLockingService` already demonstrates the intent), and that reflection is the
line to delete when it lands.

**3. The diagnostic was misleading in a specific, worth-recording way.** "Footprint drift" is a true
statement about digests and a false accusation about causes. It named the mechanism that noticed rather
than the one that failed, and cost most of an afternoon. Anything reporting drift should say *what it
compared and where each side came from* — the live cache versus a file copy taken at a stated moment —
because with that in the message the answer is immediate.

### What `MOT-11` becomes

Smaller than written: point the Dry Run at `ScratchCacheFactory.CreateFromFileCopy` (which already exists, built
during the `A1` spike), make the scratch single-use, and delete the four items above. Net negative code.

**Built 2026-08-06, and it came out smaller still.** `Run` takes a `DryRunScratch` rather than an
`LcmCache`, so "a Dry Run does not touch the live project" is a compile-time fact instead of a comment; the
scratch refuses a second use; the unit of work is non-undoable with `RollBack` cleared *before* the first
mutation, so even a failing Dry Run ends its task rather than reverting. All four items are deleted, and the
operation round-trip tests lost their dispose-and-reload dances — the clearest evidence that what was special
about `MoForm.Form` was never the field, it was the rollback. See [ADR 0030](0030-one-writer-cli-locks-like-fieldworks.md)
for why the CLI still opens the live project despite mutating only a copy.

**One subtlety that keeps it simple:** the scratch must be *discarded*, never reverted. Rolling back inside a
reused scratch would recreate the same staleness inside the scratch, and the next Dry Run would read back
wrong — a smaller copy of the original bug. "Discard" is what makes this a deletion rather than a relocation.

## Measured, 2026-08-05 — and one decision changes

The spike is built (`spikes/SIL.Motif.Spikes.ScratchCache`, plus equivalence assertions in
`tests/SIL.Motif.Tests/Runner/ScratchCacheEquivalenceTests.cs`) and run against real Sena 3 — 152,222
objects, 53.3 MB. Full results: [research note §10](../research/2026-08-05-createcachecopy-provenance-and-hazards.md#10-measured--the-spike-was-built-and-run).

| | Sena 3 |
| --- | ---: |
| Copy the project's files (control) | 49 ms |
| **In-memory copy from a cold live cache** | **209 ms** |
| **Derived copy from the pristine scratch** | **140 ms** |
| **In-memory copy from a fully hot live cache** | **4,445 ms** |
| **File copy + open (the XML path)** | **580 ms** |

**Decision 1's mechanism holds and is the reason to keep this ADR: 140 ms fan-out against 4,445 ms from a
hot cache — 31.8×.** That is what makes an interactive dry-run loop feel instant.

**But two premises were wrong, and both point the same way:**

1. **The in-memory copy is not unconditionally cheaper than the proven path.** Break-even is at roughly
   **9% of objects fluffed**; past that the XML path wins, and a linguist who has been browsing for an hour
   is well past it. In a live FieldWorks session the file path is likely the *cheaper* option, not the
   expensive one.
2. **A memory-only scratch is not equivalent to the live cache.** **0 of 4 writing systems** came back
   value-equal: every one lost its valid-character sets (2 → 0) and its font, and the vernacular `seh` lost
   its collation rules. The file path returned **4 of 4** value-equal and no findings at all. Hazard (a) is
   therefore confirmed at scale, not merely predicted.

Hazard (b) did **not** reproduce: both fixtures carry two custom fields on `LexEntry`, the condition the
flid-drift hazard needs, and flids matched exactly. The invariant stands as cheap insurance, not as a
response to an observed failure. Two other predicted risks also did not bite — skipping
`cache.Initialize()`/`PrepareCache` produced no object-count difference, and no default-writing-system
problem appeared.

### The follow-up measurement that removes the choice

A first pass at this amendment proposed a hybrid: build the pristine scratch from the XML path for real
writing systems, then fan out in memory for speed. **That was measured and it does not work.**

An in-memory copy taken *from the file-loaded scratch* — which has all four writing systems intact — came
back with **0 of 4 value-equal**, in 78 ms. The loss is a property of **the target being `kMemoryOnly`**,
not of the source: `useMemoryWsManager` is hardwired true for that backend type
(`BackendProvider.cs:263-265`), `InitializeWritingSystemManager` returns early, and the target's writing
systems are re-synthesized from `AnalysisWss`/`VernWss` tags no matter how good the source's were.

**So "cheap fan-out" and "lossless" cannot both be had from `CreateCacheCopy`.** There is no configuration
of sources that fixes it.

### Amended decision — one canonical path: the XML path

Decision 1 said "take one `CreateCacheCopy` from the live cache into a `kMemoryOnly` pristine scratch."
That is **withdrawn**. Every scratch is built by copying the project's files and opening the copy:

```
copy the project's files   ~50 ms
open the copy             ~550 ms   → a scratch that is equivalent to live on every axis measured
mutate it, read back, discard
```

**Why one path rather than two.** The hybrid asked every future operation's author to answer "does this
depend on writing-system behaviour?" correctly, forever, with a silent wrong answer as the failure mode.
That reasoning burden *is* the defect. `seh` — Sena 3's vernacular — loses its collation rules in a
memory-only scratch, and collation is exactly what ordering, homograph numbering, and "is this form already
present" comparisons rest on. We cannot enumerate everywhere LibLCM consults a writing system during a
write and read-back, so we do not build a design that requires us to.

**What this costs:** the fan-out. ~600 ms per scratch instead of ~120 ms. For an agent loop that is real but
not an interactivity problem, and it buys the removal of an entire class of "is this scratch equivalent
enough?" bug.

**What this gives up, and the mitigation:** a file copy reads the project as saved, so uncommitted edits in
the live cache are absent. Then a Dry Run's anchor will not match the live footprint at apply time and
**apply refuses** — the fail-closed direction, and the drift mechanism already handles it. The remedy is an
explicit precondition: **save before dry-running.** A CLI that owns the project can do that itself; a
FieldWorks host asks the user, which is an ordinary action rather than a new concept.

`ScratchCacheFactory.CreateInMemoryCopy` stays in the tree as the measured comparison point and is marked
non-canonical. It is not to be used for a Dry Run without a decision that reopens this ADR.

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
