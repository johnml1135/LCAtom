# Stress-test findings

Adversarial stress tests of the plan against real source (`liblcm`, `FieldWorks`, `flexicon`,
`languageforge-lexbox`). Each dimension landed real hits; the decisions taken are recorded in the
ADRs cited. This file preserves the evidence.

## 1. Operation model — the family list fractures on graph edits (→ ADR 0008)

The vocabulary's implicit unit is "one field on one owner," but real LibLCM edits are graphs with
dynamic or unbounded scope:

- **Reparenting** is a generic core primitive: `ILcmOwningSequence.MoveTo(iStart, iEnd, seqDest,
  iDestStart)` (`liblcm Vectors.cs:2077`) re-owns a range onto a *different* owner. Real callers:
  `OverridesLing_Lex.cs:9789` (SubPossibilities between LexEntryTypes), `LexSenseOperations.MovePicture`,
  `FwDataMiniLcmApi.InsertSense` (dynamic destination owner). `insert`+`remove` cannot name it without
  double-counting in the effect model.
- **Retype-with-redirect**: `ConvertLexEntryType` (`OverridesLing_Lex.cs:9729-9827`) changes an
  object's subclass, preserves identity, reparents owned collections, and redirects every project-wide
  incoming reference.
- **Merge**: `MergeObject` (`flexicon LexEntryOperations.py:2835-2926`) touches an indeterminate set
  of other entries' references — footprint not knowable without running it.
- **Atomic multi-object creates**: `MoDerivAffMsa` needs two `FsFeatStruc` trees plus references
  (`MasterLCModel.xml:3142,3147`); affix-template slots have no create primitive at all yet.

Per-construct catalog traps (for the eventual v1 catalog, not the family model): `LexReference`
silently deletes the whole relation when a remove leaves one target (`OverridesLing_Lex.cs:8386-8425`);
five parallel slot sequences over one unordered pool with an unenforced partition
(`MasterLCModel.xml:3353-3413`); object-valued custom fields blur the schema/data family line;
`sig="CmObject"` collections are heterogeneous by schema (legal targets are context-dependent);
ordering-sensitive multi-writes that NRE if split across independent ops.

## 2. Scale — `ReferringObjects` is not the cheap primitive ADR 0003 assumed (→ ADR 0006)

`EnsureCompleteIncomingRefs` → `GetIncomingFields` walks the class hierarchy up to `CmObject`
(`clid 0`) (`LcmMetaDataCache.cs:1107-1122`). Because populous fields carry the generic
`sig="CmObject"` (`StTxtPara.AnalyzedTextObjects`, `LexEntryRef.ComponentLexemes`,
`CmAnnotation.InstanceOf`), the *first* `ReferringObjects` call in a session force-fluffs every
instance of the owning classes via `AllInstances` (`RepositoryAdditions.cs:326-356`) — an O(project)
deserialization. Cached per-flid-per-session afterward, but the first payment can land on an automatic
pre-flight. LibLCM's own `DeleteObject` pays the same fan-out, so it is an engine reality, not an
Motif mistake. Whole-project snapshots (onboarding, two-way diff, baseline digest) are inherently
multi-second on large text-heavy projects.

## 3. Cross-language determinism (→ ADR 0007)

- **Normalization is the crux.** NFSC/NFC resolve to SIL's *custom* ICU data `nfc_fw.nrm`
  (`CustomIcu.cs:398-437`), gated by `HaveCustomIcuLibrary` — a runtime environment fact, so even two
  .NET machines can disagree. Python/Rust normalization libraries have no access to the custom data at
  all. Must ship a versioned normalization-data artifact and bind all languages to it.
- **Unordered sort comparator** is unspecified; base64url is not order-preserving and decode-as-GUID
  already disagrees .NET vs Python/Rust. Pin to byte-ordinal over the UTF-8 of the canonical-id string.
- **Floats**: `confidence` is excluded from the intent digest and LibLCM's model has zero float
  fields, but `CellarPropertyType.Float/Numeric` exist and `AddCustomField` accepts them unguarded.
- **GUID byte order** is correctly specified; the foot-gun is `uuid.bytes_le` "match .NET" guidance.
- **Object keys** need JCS UTF-16 code-unit ordering (a conformant library, not native `sorted()`).
- **GenDate / Binary** encodings are still unpinned.

## 4. Transaction / concurrency (→ ADR 0006)

Largely validates the design. States: `ReadyForBeginTask`, `ProcessingDataChanges`,
`BroadcastingPropChanges` (`UnitOfWorkService.cs:86-91`). "Commit at wrong place" is thrown whenever
`Commit`/`Save` runs while the state is not `ReadyForBeginTask` (`UndoStack.cs:239-246`).

- **A colliding second writer silently destroys the whole open change set — biggest risk.**
  "Single-writer" is a comment, not a lock: nothing checks thread identity, and a second writer is
  rejected by the state check before it ever waits on the `ReaderWriterLockSlim`. A colliding
  `BeginUndoTask`/`Save` calls `Rollback(0)`, which destroys the *entire* open bundle
  (`UndoStack.cs:705-725`); Motif's own `EndUndoTask` then throws "Cannot end task that has not been
  started" — indistinguishable from its own rollback. The periodic 1-second autosave is **benign** (it
  no-ops while a task is open, `UnitOfWorkService.cs:240-241`); the real threat is any *other* writer,
  including FieldWorks' shutdown `Save()` from a background thread, which has no skip guard
  (`FieldWorks.cs:3919`). architecture.md defers this as "may require a coordinator" — unmitigated.
- **Custom-field retry after a crash is not automatically idempotent.** `AddCustomField` throws on a
  duplicate name (`LcmMetaDataCache.cs:967-983`), and the schema phase writes no applied-log entry, so
  a crash between the schema commit and the data phase leaves the idempotence check reporting "never
  applied." A naive retry re-runs `customField/define` against a project where the field already
  exists and hits that throw — safe only if the ensure/resolve pre-check (custom-fields.md) runs first.
- **LibLCM convenience methods are nested-UoW landmines.** `LexEntry.MoveSenseToCopy`
  (`OverridesLing_Lex.cs:1652`) and `Text.AssociateWithNotebook` (`OverridesCellar.cs:4722`) open bare
  `UndoableUnitOfWorkHelper.Do`; reusing them from inside the outer UoW hits `CheckNotProcessingDataChanges`
  → `Rollback(0)`. Lowering must use `DoUsingNewOrCurrentUOW` (join-or-open) and grep
  `UndoableUnitOfWorkHelper.Do(` before reusing any LibLCM domain method.
- **Read-back is sound, with one caveat.** Reads mid-task are always legal (no getter checks state)
  and side effects apply synchronously before `EndUndoTask` (`CmObject.cs:1695-1723`; homograph update
  verified at `OverridesLing_Lex.cs:2047-2260`). But a few behaviors run only at task close, after
  read-back (DateModified stamping, `UndoStack.cs:294-321`); a Phase 0 spike must confirm no *semantic*
  side effect is task-close-only, or the effect closure would under-report it.
- **Rollback ≠ Undo.** `UndoStack.Rollback` skips `ClearCachesOnUndoRedo` (only `Undo`/`Redo` call it,
  `:616,667`); forward-only setter-hook caches are left stale — `MoStemAllomorph` monomorphemic data,
  `LexEntry` headword (`RepositoryAdditions.cs:1184`) and homograph (`:1247`) indexes. Object graph and
  IdentityMap revert correctly. The engine does it right for `WfiWordform` caches via a real
  `IUndoAction` (`:938-965`) — the headword/homograph caches just weren't given that treatment.
- **On-disk save is not atomic** (host concern): `XMLBackendProvider` writes temp then two separate
  `File.Move`s (`:689-690`); a crash between them leaves the main file briefly absent. Host owns
  save/backup, so this is noted, not Motif's to fix.
