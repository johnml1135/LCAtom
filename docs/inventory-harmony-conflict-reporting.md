# Inventory: SIL.Harmony failure/diagnostic semantics on non-mergeable changes

> **HISTORICAL — a path not taken.** This document evaluates routing Motif's changes through a CRDT merge
> layer, or inventories the repositories that would have been involved. **That design was assessed and
> rejected** ([adoption report](harmony-adoption-report.md)); operations target LibLCM directly and there is
> no merge layer. Kept as the evidence behind that decision. **It is not a plan, and nothing in it is
> scheduled.** For the live plan see [Plan A](plan-motif.md).

Scope: what Harmony (and its production consumer LcmCrdt / FwLite sync hosts) actually does today
when a change cannot be meaningfully applied — not what it should do. All claims below are backed
by `path:line` references to code that was read directly, or to tests that exercise that code. Where
a claim would otherwise rest on a doc comment rather than executable code, that is flagged explicitly.

Repos read:
- `C:\Users\johnm\Documents\repos\harmony` (`src/SIL.Harmony`, `src/SIL.Harmony.Core`, `src/SIL.Harmony.Tests`)
- `C:\Users\johnm\Documents\repos\languageforge-lexbox\backend\FwLite\LcmCrdt`
- `C:\Users\johnm\Documents\repos\languageforge-lexbox\backend\FwLite\FwLiteProjectSync`
- `C:\Users\johnm\Documents\repos\languageforge-lexbox\backend\FwHeadless`
- `C:\Users\johnm\Documents\repos\languageforge-lexbox\backend\FwLite\MiniLcm`

---

## 1. The apply path, end to end

Entry points, all in `DataModel.cs` (`C:\Users\johnm\Documents\repos\harmony\src\SIL.Harmony\DataModel.cs`):

- `AddChange` (53-59) → wraps a single change and calls `AddChanges`.
- `AddChanges` (83-91) builds one `Commit` via `NewCommit` (93-103) and calls the private `Add` (105-120).
- `Add` (105-120): opens a repo, takes the per-database `AsyncLock` (`repo.Lock()`), checks `HasCommit` to no-op on duplicates (109), opens a transaction, calls `repo.AddCommit(commit)` (113), then `UpdateSnapshots(repo, updatedCommits)` (114), optionally `ValidateCommits` if `AlwaysValidate` (116), then commits the DB transaction (119).
- `AddManyChanges` (61-80) and `ISyncable.AddRangeFromSync` (138-168) are the batch/sync variants — same shape: `repo.AddCommits` → `UpdateSnapshots` → `ValidateCommits` → transaction commit. `AddRangeFromSync` additionally wraps everything in `try/catch (DbUpdateException e)` (157-167) which logs and dumps `last-failed-import.json` (170-183), then **rethrows**.

Commit persistence, in `CrdtRepository.cs` (`...\harmony\src\SIL.Harmony\Db\CrdtRepository.cs`):
- `AddCommit`/`AddCommits` (389-406) → `AddNewCommits` (408-422): finds the parent commit, recomputes the hash chain for any commits after it (`UpdateCommitHashes`, 424-432; this is how out-of-order/synced commits still produce a consistent hash chain), then `_dbContext.AddRange(newCommits)` and returns the full set of commits that need snapshot recomputation (i.e. the old ones whose hash changed *and* the new ones).

Snapshot recomputation, `DataModel.UpdateSnapshots` (190-212):
- Deletes any snapshots at/after the oldest touched commit (`repo.DeleteStaleSnapshots`, `CrdtRepository.cs:130-139`) — this is what makes out-of-order commit insertion "just work": every snapshot from that point forward is thrown away and rebuilt.
- Constructs a `SnapshotWorker` (`SnapshotWorker.cs:47-51`) and calls `UpdateSnapshots(commitsToApply)` (`SnapshotWorker.cs:53-61`).

`SnapshotWorker.ApplyCommitChanges` (`SnapshotWorker.cs:63-117`) is the actual apply loop:
```csharp
foreach (var commit in commits)
{
    foreach (var commitChange in commit.ChangeEntities.OrderBy(c => c.Index))
    {
        var prevSnapshot = await GetSnapshot(commitChange.EntityId);
        var changeContext = new ChangeContext(commit, commitIndex, intermediateSnapshots, this, _crdtConfig);

        if (prevSnapshot is null) { ... entity = await commitChange.Change.NewEntity(commit, changeContext); }
        else if (prevSnapshot.EntityIsDeleted && commitChange.Change.SupportsNewEntity()) { ... revive ... }
        else if (commitChange.Change.SupportsApplyChange()) { ... entity = prevSnapshot.Entity.Copy(); await commitChange.Change.ApplyChange(entity, changeContext); ... }
        else { continue; } // no-op

        await GenerateSnapshotForEntity(entity, prevSnapshot, changeContext);
    }
}
```
(`SnapshotWorker.cs:70-113`, condensed). `ChangeContext` (`...\harmony\src\SIL.Harmony\Changes\ChangeContext.cs:6-38`) is the `IChangeContext` implementation passed to `IChange.ApplyChange`/`NewEntity`; it exposes `GetSnapshot`, `GetObjectsReferencing`, `GetObjectsOfType`, `GetCurrent<T>`, `IsObjectDeleted` (default-implemented on the interface, `...\harmony\src\SIL.Harmony.Core\IChangeContext.cs:7-14`).

`GenerateSnapshotForEntity` (`SnapshotWorker.cs:207-234`) builds a new `ObjectSnapshot` and stores it via `AddSnapshot` (236-247) into either `_rootSnapshots` or `_pendingSnapshots`. `UpdateSnapshots` then persists everything through `CrdtRepository.AddSnapshots` (`CrdtRepository.cs:303-337`), which also projects the entity into the strongly-typed EF table if `EnableProjectedTables` is on (`ProjectSnapshot`, 339-370).

Nowhere in this loop is there a try/catch around `NewEntity`/`ApplyChange`. Any exception thrown by a change implementation propagates straight out of `ApplyCommitChanges` → `UpdateSnapshots` → `Add`/`AddManyChanges`/`AddRangeFromSync`, aborting the whole batch (the DB transaction is never committed, `DataModel.cs:112,119`).

---

## 2. A change targeting an entity that does not exist (never created)

Code path: `SnapshotWorker.cs:73-85`. `prevSnapshot` is `null`. Two sub-cases:

- **The change is an `OpaqueChange`** (unknown `$type` from a newer client) → `continue` (76-82), i.e. silently skipped for this snapshot pass, but the change itself stays in the commit's `ChangeEntities` in the database forever (nothing is dropped from history — only the *projection* is skipped). Comment at `SnapshotWorker.cs:80`: "Keep unknown changes in history until this client understands how to apply them." Confirmed no-diagnostic by test `ChangeConverterTests.Unknown_type_deserializes_to_OpaqueChange` (`...\harmony\src\SIL.Harmony.Tests\ChangeConverterTests.cs:34-48`) — deserialization succeeds silently, `opaque.SupportsNewEntity()`/`SupportsApplyChange()` both `false` (`OpaqueChange.cs:25-26`).
- **The change is a known type** → `entity = await commitChange.Change.NewEntity(commit, changeContext)` (85). This is where behavior forks by *base class* of the concrete `IChange`:
  - If it derives from `CreateChange<T>` (`...\harmony\src\SIL.Harmony\Changes\CreateChange.cs`), `NewEntity` is implemented by the concrete type and is expected to succeed — this is exactly the "entity created" case (e.g. `CreateSenseChange.NewEntity`, `...\languageforge-lexbox\backend\FwLite\LcmCrdt\Changes\CreateSenseChange.cs:38-55`).
  - If it derives from `EditChange<T>` (`...\harmony\src\SIL.Harmony\Changes\EditChange.cs`), `NewEntity` is **not overridden** and the base implementation throws:
    ```csharp
    public override ValueTask<T> NewEntity(Commit commit, IChangeContext context)
    {
        throw new NotSupportedException(
            $"type {GetType().ShortDisplayName()} does not support NewEntity, ... CommitId: {commit.Id}, EntityId: {EntityId}");
    }
    ```
    (`EditChange.cs:8-12`). So: **an edit-type change against an entity that was never created throws `NotSupportedException` synchronously, mid-loop, aborting the entire batch/transaction.** This is a real, unconditional code path — not a hypothetical — but it is **not covered by any test** in `SIL.Harmony.Tests` (grep for `NotSupportedException` / "does not support NewEntity" in the test project returns nothing). No structured diagnostic is attached; the exception message is the only information, and it is not caught anywhere in `DataModel.cs` except that `AddRangeFromSync`'s catch clause only matches `DbUpdateException` (`DataModel.cs:157`), so a `NotSupportedException` from this path propagates uncaught out of `DataModel.AddChanges`/`AddManyChanges`/`ISyncable.AddRangeFromSync` alike.

There is a second, narrower "entity not found" symbol, `EntityNotFoundException` (`...\harmony\src\SIL.Harmony.Core\EntityNotFoundException.cs:3`), but it has exactly one call site in all of Harmony: `ResourceService.DownloadResource` (`...\harmony\src\SIL.Harmony\ResourceService.cs:186-195`), used for direct remote-resource lookups by ID — it is **not part of the commit/change apply path** at all. A repo-wide grep for `EntityNotFoundException` in LcmCrdt found zero uses.

**Negative finding:** there is no "entity created automatically as a stub" behavior — Harmony either runs the concrete `NewEntity` (which the author of that change type must have written to make sense with no prior state) or throws.

---

## 3. A change targeting a tombstoned (`DeletedAt`-set) entity, and delete/edit ordering

Code path, still `SnapshotWorker.cs:76-110`:
- If `prevSnapshot.EntityIsDeleted && commitChange.Change.SupportsNewEntity()` (87-91) → **revive**: `NewEntity` is called again, which is why `DeleteChange<T>` is defined as `EditChange<T>` (`...\harmony\src\SIL.Harmony\Changes\DeleteChange.cs:5`, so `SupportsNewEntity()` is `false` for it per `Change<T>.SupportsNewEntity` default, `Change.cs:79-82`) while `CreateChange<T>`-derived types (`SupportsNewEntity()` true by default) can resurrect a tombstoned entity.
- Else if `commitChange.Change.SupportsApplyChange()` (92-103) → normal edit path runs even on a deleted entity (the code comment explicitly allows edits on deleted entities — "update existing entity", 94). If the change itself sets `DeletedAt` (e.g. `DeleteChange<T>.ApplyChange`, `DeleteChange.cs:10-14`, `context.Adapt(entity).DeletedAt = context.Commit.DateTime;`) and the entity wasn't already deleted, `MarkDeleted` fires (98-102) to cascade reference cleanup (see §4).
- Else (`SupportsApplyChange()` false, i.e. a bare `CreateChange<T>` re-applied to something that already exists and isn't deleted) → `continue`, a silent no-op (104-110). Verified by test `DeleteAndCreateTests.NewEntityOnExistingEntityIsNoOp` (`...\harmony\src\SIL.Harmony.Tests\DeleteAndCreateTests.cs:189-214`) — asserts the snapshot set is byte-for-byte unchanged.

**Delete-before-edit ordering does not matter** because Harmony does not apply commits in wall-clock arrival order — it always rebuilds from the oldest affected commit forward, in `HybridDateTime` order (`CrdtRepository.DeleteStaleSnapshots`, `CrdtRepository.cs:130-139`, plus `CommitBase.CompareKey`/`CompareTo`, `...\harmony\src\SIL.Harmony.Core\CommitBase.cs:25,49-53`). So "delete arrives after an edit" (by sync/insertion order) is normalized into "delete's logical/hybrid timestamp is before or after the edit's" and re-simulated correctly either way. This is exercised directly by tests:
- `DeleteAndCreateTests` (`...\harmony\src\SIL.Harmony.Tests\DeleteAndCreateTests.cs`): `DeleteAndUndeleteInSameCommitWorks` (10-32), `...InSameSyncWorks` (34-55), `UpdateAndUndeleteInSameCommitWorks` (57-79), `...InSameSyncWorks` (81-103), `CreateDeleteAndUndeleteInSameCommitWorks` (105-126), `...InSameSyncWorks` (128-148) — all assert the same final state regardless of arrival/commit order.
- `DataModelReferenceTests.DeleteAfterTheFactRewritesReferences` (287-296) and `DeleteRetroactivelyRemovesRefs` (320-332): a delete commit inserted *before* (in logical time) an existing reference-adding commit retroactively removes the reference, proving the whole downstream snapshot chain is recomputed rather than patched.

No exception, no warning, and no persisted marker distinguishes "edit landed on an entity later found to be deleted" from a normal edit — the final snapshot is simply computed as if history had always been in the corrected order.

---

## 4. References to a deleted entity — `GetReferences` / `RemoveReference`

`IObjectBase` contract (`...\harmony\src\SIL.Harmony.Core\IObjectBase.cs:6-35`):
```csharp
public Guid[] GetReferences();
public void RemoveReference(Guid id, CommitBase commit);
```

Invocation site: `ObjectSnapshot`'s constructor calls `GetReferences()` when a snapshot is built (`...\harmony\src\SIL.Harmony\Db\ObjectSnapshot.cs:57-68`, line 61: `References = entity.GetReferences();`), so every snapshot carries the referenced-entity-id array for later querying (`ObjectSnapshot.References`, line 73).

Cascade cleanup driver: `SnapshotWorker.MarkDeleted` (`SnapshotWorker.cs:124-148`), called whenever an entity transitions from not-deleted to deleted (both the direct-edit path at 98-102 and recursively at 143-146):
```csharp
private async ValueTask MarkDeleted(Guid deletedEntityId, ChangeContext context)
{
    var toRemoveRefFrom = await GetSnapshotsReferencing(deletedEntityId, true).ToArrayAsync();
    foreach (var snapshot in toRemoveRefFrom)
    {
        var updatedEntry = snapshot.Entity.Copy();
        var wasDeleted = updatedEntry.DeletedAt.HasValue;
        updatedEntry.RemoveReference(deletedEntityId, commit);
        var deletedByRemoveRef = !wasDeleted && updatedEntry.DeletedAt.HasValue;
        await GenerateSnapshotForEntity(updatedEntry, snapshot, context);
        if (deletedByRemoveRef) await MarkDeleted(updatedEntry.Id, context); // recursive cascade
    }
}
```
`GetSnapshotsReferencing` (`SnapshotWorker.cs:174-177`) is a linear scan (`s.References.Contains(entityId)`) over pending, root, and DB snapshots (`GetSnapshotsWhere`, 179-205).

**Referential cleanup is fully automatic and runs unconditionally as part of the apply loop** — it is not opt-in and not deferred to a later pass. It is also **entirely silent**: `MarkDeleted` has no logging (`SnapshotWorker` takes no `ILogger` at all — its constructors at lines 21-30/47-51 only take snapshots/lookup/repo/config), throws nothing, and returns nothing. The only observable trace is the new snapshot itself (with the reference field mutated/nulled or `DeletedAt` cascaded).

Concrete `RemoveReference` implementations in the production model (`MiniLcm.Models`, `...\languageforge-lexbox\backend\FwLite\MiniLcm\Models\`) confirm the pattern is "null the field or cascade-delete, never report":
- `Sense.RemoveReference` (`Sense.cs:36-46`): if the *entry* was deleted, cascades `DeletedAt` onto the sense; if the *part of speech* was deleted, nulls `PartOfSpeechId`/`PartOfSpeech`; always drops the dead id out of `SemanticDomains`.
- `ComplexFormComponent.RemoveReference` (`ComplexFormComponent.cs:66-70`): if either side entry or the sense is the deleted id, cascades `DeletedAt` onto the component itself.
- `Entry.RemoveReference` (`Entry.cs:90-92`): no-op (entries currently hold no forward references that need cleanup).

The adapter that bridges these to Harmony's `IObjectBase` is `MiniLcmCrdtAdapter` (`...\languageforge-lexbox\backend\FwLite\LcmCrdt\Objects\MiniLcmCrdtAdapter.cs:26-34`) — a thin pass-through, no added diagnostics.

**Negative finding:** nothing anywhere in this chain records *that* a cascade fired, on *which* entity, referencing *which* deleted id, or *when*. It is pure silent data mutation.

---

## 5. Any diagnostic / validation-result / conflict-record / warning channel in the apply path?

Searched for: exception types, result/return types, logging calls, `IValidatable`, "conflict", "diagnostic", "warning", "needs review" across `SIL.Harmony` and `SIL.Harmony.Core`.

**Exception types that exist:** `CommitValidationException` (`...\harmony\src\SIL.Harmony\CommitValidationException.cs`, §7), `EntityNotFoundException` (§2, unrelated to apply path), `NotSupportedException` thrown ad hoc from `EditChange<T>.NewEntity` (§2) and `OpaqueChange.NewEntity` (`OpaqueChange.cs:21-23`), plain `ArgumentException`/`ArgumentNullException` from lookup helpers in `DataModel.cs` (e.g. 248-249, 341, 362 area) and `CrdtRepository.cs:274`. None of these are structured — they are exception messages only, no error codes, no attached entity/commit metadata beyond what's interpolated into the string, and none are caught and converted into a return value anywhere in the library.

**Logging:** `ILogger` is injected into exactly two classes: `DataModel` (`DataModel.cs:26`) and `CrdtRepository` (`CrdtRepository.cs:54`). Their only use:
- `DataModel.cs:159` (log + dump JSON) inside `ISyncable.AddRangeFromSync`'s `catch (DbUpdateException e)` (157-167) — a DB-level constraint/FK failure, not a semantic merge failure.
- `CrdtRepository.cs:333` inside `AddSnapshots`'s `catch (DbUpdateException e)` (325-335) — same category, DB save failures.
`SnapshotWorker` (the class that actually runs `IChange.ApplyChange`/`NewEntity` and the reference-cascade) has **no logger dependency at all** (constructors, `SnapshotWorker.cs:21-30,47-51`). There is no logging anywhere inside `ApplyCommitChanges`, `MarkDeleted`, or `GenerateSnapshotForEntity`.

**Result types:** `DataModel.AddChange`/`AddChanges` return the `Commit` itself (success-or-throw, no partial-failure shape). `SyncResults` (`DataModel.cs:13`) is `record SyncResults(Commit[] MissingFromLocal, Commit[] MissingFromRemote, bool IsSynced)` — `IsSynced` is a single top-level bool (see §8); no per-commit or per-change status.

**`IValidatable` / validation attributes:** grep for `IValidatable` across `SIL.Harmony` returns nothing. No validation-result type exists in the library.

**Conclusion — negative finding, stated explicitly:** Harmony has **no channel** today for "applied, but flag this" — no result object, no side-table, no event, no log line, at the point where a change is actually applied to a snapshot. The only "loud" failures that exist are (a) full-batch-aborting exceptions for structurally unsupported operations (§2, §7) and (b) DB-level `DbUpdateException` logging that is about storage integrity, not merge semantics.

---

## 6. Do `ObjectSnapshot`, `Commit`, or `CommitMetadata` carry any validity/diagnostic field?

**`ObjectSnapshot`** (`...\harmony\src\SIL.Harmony\Db\ObjectSnapshot.cs:32-80`), full field list:
```csharp
public required Guid Id { get; init; }
public required string TypeName { get; init; }
public required IObjectBase Entity { get; init; }
public required Guid[] References { get; init; }
public required Guid EntityId { get; init; }
public required bool EntityIsDeleted { get; init; }
public required Guid CommitId { get; init; }
public required Commit Commit { get; init; }
public required bool IsRoot { get; init; }
```
No status/flag/diagnostic field. `EntityIsDeleted` is a tombstone flag, not a validity flag — it reflects the domain model's own `DeletedAt`, not anything about whether the snapshot itself is "sound." There is also `SimpleSnapshot` (same file, 8-30), a projection record for cheap listing (`Id, TypeName, EntityId, CommitId, IsRoot, HybridDateTime, CommitHash, EntityIsDeleted`) — same story, no such field.

**`Commit`** (`...\harmony\src\SIL.Harmony\Commit.cs:7-41`):
```csharp
[JsonIgnore] public List<ObjectSnapshot> Snapshots { get; init; } = [];
[JsonIgnore] public string Hash { get; private set; }
[JsonIgnore] public string ParentHash { get; private set; }
```
plus inherited from `CommitBase` (`...\harmony\src\SIL.Harmony.Core\CommitBase.cs:10-54`): `Id`, `HybridDateTime`, `DateTime`, `Metadata` (`CommitMetadata`), `ClientId`. And from `CommitBase<TChange>` (56-68): `ChangeEntities`. No status/flag field anywhere on the commit itself.

**`CommitMetadata`** (`...\harmony\src\SIL.Harmony.Core\CommitMetadata.cs:3-19`):
```csharp
public string? AuthorName { get; set; }
public string? AuthorId { get; set; }
public string? ClientVersion { get; set; }
public Dictionary<string, string?> ExtraMetadata { get; set; } = new();
public string? this[string key] { get => ExtraMetadata.GetValueOrDefault(key); set => ExtraMetadata[key] = value; }
```
This is the **one place** with genuine extensibility: `ExtraMetadata` is a free-form `Dictionary<string,string?>` (also indexer-accessible) attached to every commit, intended for "application specific metadata" (doc comment, line 9-11). It is set once at commit-creation time by the calling application (`commitMetadata` parameter threaded through `DataModel.AddChange`/`AddChanges`/`NewCommit`, `DataModel.cs:53-56,83-87,93-103`) — nothing in Harmony itself writes to it during apply, and nothing reads it back out during apply either. It is a place an application *could* stash "this commit needs review," but nothing does that today, and it's commit-level (not per-change, not per-entity), decided at authoring time rather than at apply/merge time.

**Negative finding:** none of the three types carry a validity flag, diagnostic payload, or "needs review" marker that is populated by the merge/apply machinery itself.

---

## 7. `DataModel.ValidateCommits` and `RegenerateSnapshots`

`ValidateCommits` (`DataModel.cs:214-232`):
```csharp
private async Task ValidateCommits(CrdtRepository repo)
{
    Commit? parentCommit = null;
    await foreach (var commit in repo.CurrentCommits().AsNoTracking().AsAsyncEnumerable())
    {
        var parentHash = parentCommit?.Hash ?? CommitBase.NullParentHash;
        var expectedHash = commit.GenerateHash(parentHash);
        if (commit.Hash == expectedHash && commit.ParentHash == parentHash) { parentCommit = commit; continue; }

        var actualParentCommit = await repo.FindCommitByHash(commit.ParentHash);
        var commitWithSnapshots = await repo.CurrentCommits().Include(c => c.Snapshots).SingleAsync(c => c.Id == commit.Id);
        throw new CommitValidationException(
            $"Commit {commit} does not match expected hash, parent hash [{commit.ParentHash}] !== [{parentHash}], ...");
    }
}
```
This checks **only structural hash-chain integrity** — that each commit's stored `Hash`/`ParentHash` matches what `GenerateHash` (`...\harmony\src\SIL.Harmony.Core\CommitBase.cs:32-40`, an `XxHash64` over the commit id + parent hash bytes) would produce given the actual chain order in the DB. It says nothing about whether any *change* inside those commits applied sensibly. Confirmed by the only test that exercises it, `DataModelIntegrityTests.InvalidCommitHashesResultInException` (`...\harmony\src\SIL.Harmony.Tests\DataModelIntegrityTests.cs:19-34`): it corrupts `ParentHash` directly (`addedCommit.SetParentHash("BBAADD")`, line 26) and asserts a `CommitValidationException` with message `"*does not match expected hash*"`. There is no test that feeds it a semantically-broken-but-hash-consistent history.

On failure: throws `CommitValidationException` (`CommitValidationException.cs:3-8`, a plain `Exception` subclass with only a message, no structured payload) which aborts the enclosing transaction the same as any other unhandled exception in the apply path (§1). It runs automatically after every `AddChanges`/`Add` call when `HarmonyConfig.AlwaysValidateCommits` is true (`DataModel.cs:20,116`), which is the **default** (`HarmonyConfig.cs:23`: `public bool AlwaysValidateCommits { get; set; } = true;`), and unconditionally after every sync batch (`AddManyChanges`, `DataModel.cs:78`; `ISyncable.AddRangeFromSync`, `DataModel.cs:154`) regardless of the flag. A repo-wide grep of `languageforge-lexbox/backend` for `AlwaysValidateCommits` found no override, so LcmCrdt runs with the default (validate-always) in production too.

`RegenerateSnapshots` (`DataModel.cs:234-243`):
```csharp
public async Task RegenerateSnapshots()
{
    await using var repo = await _crdtRepositoryFactory.CreateRepository();
    await repo.DeleteSnapshotsAndProjectedTables();
    repo.ClearChangeTracker();
    var allCommits = await repo.CurrentCommits().Include(c => c.ChangeEntities).ToSortedSetAsync();
    await UpdateSnapshots(repo, allCommits);
}
```
`DeleteSnapshotsAndProjectedTables` (`CrdtRepository.cs:141-151`) wipes the `Snapshots` table and, if `EnableProjectedTables`, every projected entity table. Then it replays **every** commit in the database from scratch through the identical `UpdateSnapshots`/`SnapshotWorker` path used for normal apply (§1) — same silent-degrade/throw behavior, no extra checks, no report. It is a full rebuild, not an audit: it will happily and silently re-run any of the graceful-degradation behaviors from §2-4 again. Confirmed by `SnapshotTests.RegenerateSnapshots_WillArriveAtTheSameState` (`...\harmony\src\SIL.Harmony.Tests\SnapshotTests.cs:146-165`) which only asserts the final projected state and snapshot-id set are consistent with a fresh build — it does not assert anything about diagnostics because none exist to assert.

---

## 8. What do the sync hosts report?

**`DataModel.SyncWith`/`SyncHelper.SyncWith`** (`...\harmony\src\SIL.Harmony\SyncHelper.cs:22-42`): computes `missingFromLocal`/`missingFromRemote` via `GetChanges` (diff by `SyncState`, a per-client-head timestamp map, `...\harmony\src\SIL.Harmony.Core\SyncState.cs`), then calls `AddRangeFromSync` on each side (37-40) and returns `SyncResults(missingFromLocal, missingFromRemote, true)` (41) — `IsSynced` is hardcoded `true` on the success path; the only way it's `false` is the early return at line 26 when either side's `ShouldSync()` is false (e.g. offline). **If `AddRangeFromSync` throws (as it will on any of the failures in §2/§5/§7), `SyncWith` never returns at all — there is no partial/degraded `SyncResults`.** The result shape is binary: fully synced, or an exception.

**`CrdtSyncService.SyncHarmonyProject`** (`...\languageforge-lexbox\backend\FwHeadless\Services\CrdtSyncService.cs:14-28`):
```csharp
var syncResults = await dataModel.SyncWith(lexboxRemoteServer);
if (!syncResults.IsSynced) throw new InvalidOperationException("Sync failed");
logger.LogInformation("Synced with Lexbox, Downloaded changes: {MissingFromLocal}, Uploaded changes: {MissingFromRemote}", ...);
```
Only reports **counts** of commits moved in each direction (line 25-27) — nothing about their content or whether any individual change inside them degraded gracefully or would have thrown. Any exception from `SyncWith` (per §1/§5) propagates out of this method uncaught.

**`SyncHostedService`/`SyncWorker.ExecuteSync`** (`...\languageforge-lexbox\backend\FwHeadless\Services\SyncHostedService.cs:18-99, 101-277`): the outer `ExecuteAsync` loop (23-53) wraps the whole per-project sync in one `try/catch (Exception e)` (32-42) and reduces everything to a single `SyncJobResult(SyncJobStatusEnum.UnknownError, e.ToString())` (line 40) if anything throws. `ExecuteSync` itself is a long sequence of coarse-grained named outcomes — `SyncJobStatusEnum.ProjectNotFound` (133), `UnableToAuthenticate` (144), `SyncBlocked` (153, 174, 240), `SendReceiveFailed` (177, 244), `ProjectIncompatible` (182), `SuccessHarmonyOnly` (210), and the final success (276, wrapping `SyncResult` from `CrdtFwdataProjectSyncService`) — **one status enum value for the entire job**, never per-change. `SyncJobResult` (`LexCore.Sync`, referenced but not opened in this investigation — the shape used here is `new SyncJobResult(SyncJobStatusEnum, string?)` and `new SyncJobResult(SyncResult)`) carries a single status + a single string message, not a list of per-change problems.

**`CrdtFwdataProjectSyncService.Sync`/`Import`** (`...\languageforge-lexbox\backend\FwLite\FwLiteProjectSync\CrdtFwdataProjectSyncService.cs:27-40, 109-146`): returns `SyncResult(int CrdtChanges, int FwdataChanges)` (or `DryRunSyncResult` adding per-record dry-run logs, 16-20) — again pure counts. The category-by-category sync methods it calls (`WritingSystemSync.Sync`, `PublicationSync.Sync`, `PartOfSpeechSync.Sync`, `SemanticDomainSync.Sync`, `ComplexFormTypeSync.Sync`, `MorphTypeSync.Sync`, `EntrySync.SyncFull`, lines 117-143) are outside Harmony/LcmCrdt proper (`MiniLcm.SyncHelpers`); a grep for `LogWarning|LogError|LogInformation` in that folder returned **zero matches** — no logging of any kind in that layer either.

**Conclusion:** every sync host surfaces a binary-or-enum result at the job/batch granularity. None of them surface per-change or per-entity failures; a semantic problem two layers down (missing reference, invalid patch index, unsupported edit-on-nonexistent-entity) is either silently absorbed by the graceful-degradation code in the change types themselves (§2-4) or blows up the entire sync job as one opaque `UnknownError`/exception string (§1, §5).

---

## 9. Is there any existing notion of "applied but flagged"?

No. Searched for `Conflict`, `NeedsReview`, `RequiresReview`, `FlaggedFor`, `ReviewStatus` (case-insensitive) across `SIL.Harmony` and across `LcmCrdt`. In Harmony: zero real hits (the only string matches were incidental substrings in `using System.Diagnostics...` and similar, not semantic hits). In LcmCrdt: three file hits, all false positives on inspection — `Templates/blank-project-template.json:15728` is a semantic-domain name `"Conflict"` in seed data; `FullTextSearch/EntrySearchService.cs:227-228` is a comment about SQLite's `ON CONFLICT` clause; `Changes/ExampleSentences/AddTranslationChange.cs:14` is a comment ("could happen if Chorus recreates a translation due to a merge conflict") describing *why* a silent dedupe-and-replace is done (`AddTranslationChange.cs:12-18`, `entity.Translations.RemoveAll(t => t.Id == Translation.Id); entity.Translations.Add(Translation);`) — i.e. even where the word "conflict" appears in a comment, the actual behavior is silent overwrite, not flagging.

The closest things that exist to "accepted into history but not fully resolved" are:
- `OpaqueChange` (§2): accepted into history, deliberately *not* applied, but this is a forward-compatibility mechanism for unknown change types, not a review/flag mechanism, and it produces no user-visible signal — it's invisible unless someone inspects raw commit JSON.
- The various silent reference-nulling behaviors in concrete `Change<T>` implementations (`SetPartOfSpeechChange.cs:20-26`, `AddSemanticDomainChange.cs:14-15`, `MoveSenseToEntryChange.cs:19-22`, `Sense.RemoveReference`, `ComplexFormComponent.RemoveReference`) — these are accepted into history as ordinary snapshots with no distinguishing trait from a "clean" change.

**Negative finding, stated explicitly:** there is no data model, no query, and no code path anywhere examined that represents "this change/entity is in the accepted history but needs human attention."

---

## 10. Assessment: real insertion points for a deferred diagnostic

This is evidence-based enumeration of where instrumentation could attach without altering merge outcomes (i.e., additive only) — not a design.

1. **`SnapshotWorker.ApplyCommitChanges`, the branch dispatch itself** (`SnapshotWorker.cs:76-110`). Every one of the branches (`prevSnapshot is null` + known change / `EntityIsDeleted && SupportsNewEntity()` / `SupportsApplyChange()` / silent `continue`) is already a distinguishable, named case in the source. A diagnostic hook could observe which branch fired for a given `commitChange` without changing which branch fires. This is also the one place that currently has zero logger/diagnostic dependency at all (constructors at `SnapshotWorker.cs:21-30,47-51` take no `ILogger`), so adding an optional sink here is a net-new capability, not a rewire.
2. **`SnapshotWorker.MarkDeleted`** (`SnapshotWorker.cs:124-148`). Already computes `wasDeleted`/`deletedByChange`/`deletedByRemoveRef` as explicit booleans (see `SnapshotWorker.cs:98,138`) purely to decide control flow — these are exactly the signal a "reference to a now-dead entity was silently dropped/cascaded" diagnostic needs, and they are already materialized as local variables today; only their *reporting* is missing.
3. **`IChangeContext`/`ChangeContext`** (`...\harmony\src\SIL.Harmony.Core\IChangeContext.cs`, `...\harmony\src\SIL.Harmony\Changes\ChangeContext.cs`). Concrete `Change<T>.ApplyChange`/`NewEntity` implementations already call `context.GetCurrent<T>`, `context.IsObjectDeleted`, `context.GetSnapshot` to decide "does this reference resolve" (e.g. `SetPartOfSpeechChange.cs:20-26`, `CreateSenseChange.cs:40-41,53`, `AddEntryComponentChange.cs:39-46`). Because `IChangeContext` is the one object threaded into every `IChange` implementation, it is the natural place to add an opt-in "record this observation" method (e.g. an addition alongside `GetCurrent<T>`) that individual change types could call when they notice a dangling reference — without touching `SnapshotWorker`'s dispatch logic.
4. **`EditChange<T>.NewEntity`'s `NotSupportedException`** (`EditChange.cs:8-12`) and `OpaqueChange.NewEntity`'s `NotSupportedException` (`OpaqueChange.cs:21-23`) are the two spots that already synthesize a descriptive message including `CommitId`/`EntityId` — today that information is thrown away as an aborting exception. Converting (or duplicating) these into a captured record rather than a hard throw is the direct answer to the user's "invalid index"/"entity doesn't exist" framing, and the insertion point is exactly these two methods (plus the `JsonPatchValidator.ValidatePatchDocument` throw at `...\languageforge-lexbox\backend\FwLite\LcmCrdt\Changes\JsonPatchChange.cs:38-54`, which is LcmCrdt's own precedent for "reject a structurally invalid change" — it already throws `NotSupportedException` for index-based JSON Patch paths at construction/deserialization time, i.e. this exact "fail loudly with structured info" instinct already exists in the codebase, just synchronously and destructively rather than as a deferred record).
5. **`CommitMetadata.ExtraMetadata`** (`...\harmony\src\SIL.Harmony.Core\CommitMetadata.cs:12,14-18`). The only free-form, persisted, per-commit extensibility field in the whole data model. It's populated at authoring time today, but nothing stops a post-hoc process from writing into it on an already-persisted `Commit` row (subject to whatever storage/update semantics the consumer wants) — it is the only *existing* field anywhere in `ObjectSnapshot`/`Commit`/`CommitMetadata` capable of holding arbitrary review-state text without a schema change to Harmony itself. Note it's commit-granularity, not per-change or per-entity, which is coarser than what "this entity doesn't exist" wants.
6. **`DataModel.ValidateCommits`** (`DataModel.cs:214-232`) is structurally the right *shape* for a second, semantic pass (it already runs, unconditionally by default, after every write) but today does hash-chain checking only and throws-to-abort on failure; a semantic-diagnostic pass would need to be a genuinely separate, non-aborting traversal — reusing this method's throw-based contract would be wrong for "record and continue."
7. **Sync-host layer** (`CrdtSyncService.SyncHarmonyProject`, `SyncWorker.ExecuteSync`, `CrdtFwdataProjectSyncService.Sync`/`Import`) is the wrong altitude for a *per-change* diagnostic — everything here already operates on counts (`SyncResult(int CrdtChanges, int FwdataChanges)`) and job-level enums (`SyncJobStatusEnum`), by design, and there is no existing per-commit or per-change hook to attach to without adding a return-value plumbing change through several layers (`DataModel.SyncWith` → `SyncHelper.SyncWith` → `ISyncable.AddRangeFromSync`) that does not exist today.

**Summary:** the two levels closest to "the entity/change itself" — inside `SnapshotWorker.ApplyCommitChanges`/`MarkDeleted`, and inside individual `IChange.ApplyChange`/`NewEntity` implementations via `IChangeContext` — are where the actual yes/no decisions about resolvability already live as ordinary control flow, with zero existing reporting infrastructure to conflict with. Everything above that (validation pass, sync hosts) already commits to a coarser, job/commit-level, exception-or-count-only contract that would need to change shape, not just gain a hook.
