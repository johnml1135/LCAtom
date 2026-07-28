# SIL.Harmony — Exhaustive Capability Inventory

Scope read for this inventory: **every** `.cs` file in `harmony/src/SIL.Harmony/`, `harmony/src/SIL.Harmony.Core/`,
`harmony/src/SIL.Harmony.Linq2db/`, `harmony/src/SIL.Harmony.Sample/`, and `harmony/src/SIL.Harmony.Tests/`
(all test files, not a sample), plus the `harmony/README.md` and `git log`. On the consumer side: the Harmony
integration points in `languageforge-lexbox/backend/FwLite/LcmCrdt/` and the server-side sync endpoint in
`languageforge-lexbox/backend/LexBoxApi/Controllers/CrdtController.cs`. `harmony/src/Ycs/` (a vendored Yjs/YCS
port) was inspected only enough to confirm it is **not** part of Harmony proper — it is referenced solely by
`SIL.Harmony.Sample`'s `Example`/`NewExampleChange`/`EditExampleChange` (`Example.cs:3`,
`NewExampleChange.cs:5`, `EditExampleChange.cs:5`) as an optional pattern for a CRDT text field a consumer
*could* adopt; LcmCrdt does not use it (no `Ycs` reference in LcmCrdt).

All line numbers are `path:line` against the file as read. VERIFIED = read directly in source. INFERRED = deduced from behavior/tests/naming without an explicit doc comment confirming intent.

---

## 1. Every public type, by directory

### `SIL.Harmony.Core` (the wire/storage-agnostic core; no EF dependency except `IQueryable` extension methods)

| Type | File:Line | Purpose |
|---|---|---|
| `ChangeEntity<TChange>` | `ChangeEntity.cs:5` | One change's storage row: `Index` (order within commit), `CommitId`, `EntityId`, `Change` (the payload). VERIFIED |
| `CommitBase` (abstract) | `CommitBase.cs:10` | Non-generic base of a commit: `Id`, `HybridDateTime`, `ClientId`, `Metadata`, `Hash`/`ParentHash` computation (`GenerateHash`), `CompareKey`, `IComparable<CommitBase>`. VERIFIED |
| `CommitBase<TChange>` (abstract) | `CommitBase.cs:57` | Adds `ChangeEntities: List<ChangeEntity<TChange>>`. Base of both `Commit` (client, `IChange`) and `ServerCommit` (server, `ServerJsonChange`). VERIFIED |
| `CommitMetadata` | `CommitMetadata.cs:3` | `AuthorName`, `AuthorId`, `ClientVersion`, plus a free-form `ExtraMetadata: Dictionary<string,string?>` with an indexer (`commit.Metadata["SyncDate"]` etc. — used by LcmCrdt for `SyncDate`, `Template`). VERIFIED |
| `CrdtConstants` | `CrdtConstants.cs:3` | One constant: `ChangeDiscriminatorProperty = "$type"` — the JSON discriminator property name, documented as never-changeable because it's persisted. VERIFIED |
| `EntityNotFoundException` | `EntityNotFoundException.cs:3` | Thrown e.g. by `ResourceService.DownloadResource` when no snapshot exists. VERIFIED |
| `HybridDateTime` (record) | `HybridDateTime.cs:10` | `(DateTimeOffset DateTime, long Counter)`, `IComparable`; hybrid logical clock so clock resets don't reorder a client's own history. VERIFIED |
| `IHybridDateTimeProvider` / `HybridDateTimeProvider` | `HybridDateTime.cs:58,64` | `GetDateTime()` returns a clock value guaranteed `>` the last one it issued (bumps counter if wall clock hasn't advanced); `TakeLatestTime(IEnumerable<HybridDateTime>)` folds in the max of incoming commits' times (called on sync) so the local clock never issues a time behind synced data. VERIFIED |
| `IChangeContext` | `IChangeContext.cs:3` | The interface a `Change<T>.ApplyChange`/`NewEntity` receives: `Commit`, `GetSnapshot(id)`, `GetCurrent<T>(id)`, `IsObjectDeleted(id)`, `GetObjectsReferencing(id)`, `GetObjectsOfType<T>(jsonTypeName)`, internal `Adapt(obj)`. VERIFIED — full breakdown in §3. |
| `IObjectBase` | `IObjectBase.cs:6` | The base contract every CRDT entity (or its adapter) must satisfy: `Id`, `DeletedAt`, `GetReferences()`, `RemoveReference(id, commit)`, `Copy()`, `GetObjectTypeName()`, `DbObject`. `[JsonPolymorphic]`. VERIFIED |
| `IObjectSnapshot` | `IObjectSnapshot.cs:3` | Storage-agnostic snapshot shape: `Id`, `TypeName`, `Entity`, `References[]`, `EntityId`, `EntityIsDeleted`, `CommitId`, `Commit`, `IsRoot`. `ObjectSnapshot` (in `SIL.Harmony`) implements this. VERIFIED |
| `IRemoteResourceService<TMetadata>` | `IRemoteResourceService.cs:8` | App-implemented: `DownloadResource(remoteId, cachePath) -> DownloadResult`, `UploadResource(resourceId, localPath, metadata) -> UploadResult<TMetadata>`. Harmony treats the remote id as opaque (could be a URL). VERIFIED |
| `QueryHelpers` (static) | `QueryHelpers.cs:5` | LINQ extension methods used everywhere: `GetSyncState`, `GetChanges`, `GetMissingCommits` (both `IQueryable` async and plain `IEnumerable` sync overloads), `DefaultOrder`/`DefaultOrderDescending` (by `HybridDateTime.DateTime`, then `.Counter`, then `Id` — this triple is the **canonical total order** used everywhere), `WhereAfter`/`WhereBefore` (same triple, strict/inclusive). VERIFIED |
| `ServerCommit` | `ServerCommit.cs:7` | Server-side commit type: `CommitBase<ServerJsonChange>` + `ProjectId`. Used so the sync **server** doesn't need to know concrete `IChange` types. VERIFIED |
| `ServerJsonChange` | `ServerCommit.cs:25` | Generic placeholder: `Type` (discriminator) + `ExtensionData: Dictionary<string,JsonElement>` — i.e. the server stores/forwards change JSON opaquely. Implicit conversion from `JsonElement`. VERIFIED |
| `SyncState` (record) | `SyncState.cs:3` | `Dictionary<Guid ClientId, long UnixMillis>` — one logical clock reading per client, the entire sync handshake payload. VERIFIED |
| `IChangesResult` / `ChangesResult<TCommit>` | `SyncState.cs:4,9` | Result of a "what do you have that I don't" query: `MissingFromClient`/`MissingFromClient` (typed), `ServerSyncState`. VERIFIED |

### `SIL.Harmony` root files

| Type | File:Line | Purpose |
|---|---|---|
| `Commit` | `Commit.cs:7` | Client commit: `CommitBase<IChange>` + `Snapshots: List<ObjectSnapshot>` (nav prop) + `Hash`/`ParentHash` (private setters, only mutable via `SetParentHash`, which recomputes `Hash`). VERIFIED |
| `CommitValidationException` | `CommitValidationException.cs:3` | Thrown by `DataModel.ValidateCommits` when the hash chain doesn't match. VERIFIED |
| `CrdtKernel` (static) | `CrdtKernel.cs:11` | DI wiring: `AddCrdtDataDbFactory<TContext>` (per-call DbContext via `IDbContextFactory`), `AddCrdtData<TContext>` (single scoped, non-disposing wrapper — for when EF already owns the DbContext scope), `AddCrdtRemoteResources<TMetadata>`, `AddCrdtDataCore` (registers `HarmonyConfig` options, `JsonSerializerOptions`, `TimeProvider.System`, `IHybridDateTimeProvider`, `CrdtRepositoryFactory`, `DataModel`), `NewTimeProvider` (seeds the hybrid clock from the latest commit already in the DB at startup). VERIFIED |
| `DataModel` | `DataModel.cs:15` | **The** application-facing API. Full method table in §2. VERIFIED |
| `ISyncable` | `ISyncable.cs:3` | The sync contract both `DataModel` and any remote peer must implement: `AddRangeFromSync`, `GetSyncState`, `GetChanges`, `SyncWith`, `SyncMany`, `ShouldSync`. VERIFIED |
| `NullSyncable` | `ISyncable.cs:13` | No-op `ISyncable` (returns empty everything, `ShouldSync() => false`). VERIFIED |
| `JsonSyncable` | `JsonSyncable.cs:10` | A **file-based** `ISyncable` implementation: one `client_{guid}.jsonl` file per client id, newline-delimited JSON `Commit` rows, per-client `AsyncLock` for concurrent-safe appends. Alternative to a DB-backed `DataModel` peer — e.g. syncing via a shared folder or as a portable export format. VERIFIED |
| `ModelSnapshot` | `ModelSnapshot.cs:5` | A point-in-time index over all current `SimpleSnapshot`s (`Dictionary<Guid EntityId, SimpleSnapshot>`) plus `LastChange`/`LastCommitId`/`LastCommitHash` (from the newest snapshot). Returned by `DataModel.GetProjectSnapshot()`; used to detect "are two replicas at the same state" (`LastCommitHash` equality, exercised in `SyncTests.CanSyncSimpleChange`). VERIFIED |
| `ResourceService<TMetadata>` | `ResourceService.cs:12` | Full method table in §7. VERIFIED |
| `SnapshotWorker` (internal) | `SnapshotWorker.cs:12` | The change-application engine. Detailed in §5. VERIFIED |
| `SyncHelper` (internal static) | `SyncHelper.cs:4` | Implements the generic two-`ISyncable` sync algorithm (`SyncWith`, `SyncMany`) reused by both `DataModel` and `JsonSyncable`. Also `SyncWithResourceUpload` (upload pending resources, then sync). VERIFIED |

### `Changes/`

| Type | File:Line | Purpose |
|---|---|---|
| `IChange` | `Change.cs:12` | The change contract: `EntityId` (mutable, `[JsonIgnore]`), `EntityType` (`[JsonIgnore]`), `ApplyChange(IObjectBase, IChangeContext)`, `NewEntity(Commit, IChangeContext) -> IObjectBase`, `SupportsApplyChange()`, `SupportsNewEntity()`. VERIFIED |
| `Change<T>` (abstract) | `Change.cs:39` | Typed base: wraps `IChange.NewEntity`/`ApplyChange` around abstract `NewEntity(Commit, IChangeContext) -> ValueTask<T>` / `ApplyChange(T, IChangeContext)`. Default `SupportsApplyChange() => this is not CreateChange<T>`; default `SupportsNewEntity() => this is not EditChange<T>`. VERIFIED |
| `CreateChange<T>` (abstract) | `CreateChange.cs:5` | A change that only creates; `ApplyChange` is a no-op (never called because `SupportsApplyChange()` is false by inheritance from `Change<T>`'s type check). VERIFIED |
| `EditChange<T>` (abstract) | `EditChange.cs:5` | A change that only edits; `NewEntity` throws `NotSupportedException` if ever invoked (defensive — should never happen because `SupportsNewEntity()` is false). VERIFIED |
| `DeleteChange<T>` | `DeleteChange.cs:5` | Built-in generic delete: `EditChange<T>` that sets `DeletedAt = commit.DateTime`. `TypeName => "delete:" + typeof(T).Name`. VERIFIED |
| `SetOrderChange<T>` | `SetOrderChange.cs:11` | Built-in generic reorder change for any `T : IPolyType, IObjectBase, IOrderableCrdt`. Static helpers `Between(left,right)`, `After(previous)`, `Before(preceding)` compute a new `double Order` by averaging/±1. `TypeName => "setOrder:" + T.TypeName`. VERIFIED |
| `IOrderableCrdt` | `SetOrderChange.cs:6` | `double Order { get; set; }` + `Guid Id` — marker for entities orderable via `SetOrderChange<T>`. VERIFIED |
| `OpaqueChange` | `OpaqueChange.cs:9` | **Forward-compatibility escape hatch.** Represents an `IChange` whose `$type` this client doesn't recognize. Preserves `TypeName` + raw `JsonElement` so it round-trips losslessly. `SupportsApplyChange()`/`SupportsNewEntity()` both `false` — `SnapshotWorker` explicitly skips `OpaqueChange` when there's no prior snapshot (`SnapshotWorker.cs:78-82`, comment: "Keep unknown changes in history until this client understands how to apply them"). Added in harmony PR #80 (`aae2ee0`). VERIFIED |
| `PeekThenConcreteChangeConverter` (internal) | `PeekThenConcreteChangeConverter.cs:14` | The `JsonConverter<IChange>` that owns all `IChange` polymorphism (NOT `[JsonPolymorphic]`/STJ's built-in mechanism — see §3). Peeks `$type` as the *required first property*; known type → deserializes via cached `JsonTypeInfo`; unknown → captures the whole object as `OpaqueChange` preserving exact JSON. Write path re-serializes `OpaqueChange.RawJson` verbatim, or serializes concrete changes normally (discriminator injected by a synthetic property, see `HarmonyConfig.AddSyntheticTypeDiscriminator`). VERIFIED |
| `ChangeContext` (internal) | `ChangeContext.cs:6` | The concrete `IChangeContext` implementation used during change application; wraps a `SnapshotWorker`, exposes `Commit`, `CommitIndex`, `IntermediateSnapshots`. VERIFIED |

### `Db/`

| Type | File:Line | Purpose |
|---|---|---|
| `CrdtDbContextFactory<TContext>` / `ICrdtDbContextFactory` | `CrdtDbContextFactory.cs:7,21` | Wraps `IDbContextFactory<TContext>` (used by `AddCrdtDataDbFactory`). VERIFIED |
| `CrdtDbContextNoDisposeFactory<TContext>` | `CrdtDbContextFactory.cs:27` | Wraps a single injected `TContext` in a `NoDisposeWrapper` so `DataModel`'s internal `await using` doesn't dispose a context owned by DI (used by `AddCrdtData`). VERIFIED |
| `CrdtDbContextModelExtensions.UseCrdt` (static) | `CrdtDbContextOptionsExtensions.cs:9` | Call from `OnModelCreating`: applies `CommitEntityConfig`, `SnapshotEntityConfig`, `ChangeEntityConfig`, then every registered object type's model configuration (from `ObjectTypeListBuilder.ModelConfigurations`). VERIFIED |
| `CrdtRepositoryFactory` (internal) | `CrdtRepository.cs:17` | Creates `CrdtRepository` instances (async/sync) via `ActivatorUtilities`; `Execute(func)` helper for scoped one-shot repo use. VERIFIED |
| `CrdtRepository` (internal) | `CrdtRepository.cs:47` | The actual EF/SQL layer. Full breakdown of the out-of-order-commit logic in §4. VERIFIED |
| `ScopedDbContext` (internal) | `CrdtRepository.cs:488` | An `ICrdtDbContext` wrapper that filters `Commits`/`Snapshots` to `WhereBefore(ignoreChangesAfter, inclusive: true)` — backs `GetScopedRepository`, i.e. "pretend history ends at commit X." Used for `GetAtCommit`/`GetSnapshotsAtCommit`. VERIFIED |
| `DbSetExtensions` (static) | `DbSetExtensions.cs:6` | Same `DefaultOrder`/`WhereAfter`/`WhereBefore`/`AsTracking` helpers as `QueryHelpers` but specialized for `IQueryable<ObjectSnapshot>` (joins through `.Commit`). VERIFIED |
| `ChangeEntityConfig` | `ChangeEntityConfig.cs:9` | EF config for `ChangeEntities` table: composite key `(CommitId, Index)`, `Change` column stored as `jsonb` via `HasConversion` (serialize/deserialize through the app's `JsonSerializerOptions`). VERIFIED |
| `CommitEntityConfig` | `CommitEntityConfig.cs:8` | EF config for `Commits` table: PK `Id`, `HybridDateTime` as a `ComplexProperty` (`DateTime` converted to/from UTC ticks, `Counter`), a composite index on `(DateTime, Counter, Id)` via `EFCore.ComplexIndexes` (worked around an EF Core 10 limitation, `efcore#11336`), `Metadata` as `jsonb`. VERIFIED |
| `SnapshotEntityConfig` | `SnapshotEntityConfig.cs:8` | EF config for `Snapshots` table: PK `Id`, unique index `(CommitId, EntityId)`, index on `EntityId`, FK to `Commit`, `Entity` stored as `jsonb`. VERIFIED |
| `ICrdtDbContext` | `ICrdtDbContext.cs:7` | The interface a consumer's `DbContext` must implement to plug into Harmony: `Commits`/`Snapshots` queryables (default via `Set<T>()`), `SaveChangesAsync`, `FindAsync`, `Set<TEntity>()`, `Database`, `ChangeTracker`, `Entry`/`Add`/`AddRange`/`Remove`. VERIFIED |
| `ObjectSnapshot` | `ObjectSnapshot.cs:32` | The concrete `IObjectSnapshot`: `Id`, `TypeName`, `Entity: IObjectBase`, `References[]`, `EntityId`, `EntityIsDeleted`, `CommitId`, `Commit`, `IsRoot`. `ShadowRefName = "SnapshotId"` — the shadow FK column name projected tables use to point back at the snapshot that produced their current row (changing it needs a migration, per comment). `ForTesting(commit)` factory for tests. VERIFIED |
| `SimpleSnapshot` (record) | `ObjectSnapshot.cs:8` | A lightweight projection of `ObjectSnapshot` (no `Entity` payload) used by `ModelSnapshot`/`GetProjectSnapshot`; `IsType<T>()` helper. VERIFIED |

### `Entities/`

| Type | File:Line | Purpose |
|---|---|---|
| `IObjectBase<TThis>` | `Entities/IObjectBase.cs:5` | Default-implements `IObjectBase.GetObjectTypeName()`/`DbObject` for the common case where the CRDT entity *is* its own storage type (i.e., not using a custom adapter). Requires `TThis : IPolyType`. VERIFIED |
| `IPolyType` | `IPolyType.cs:6` | `static abstract string TypeName { get; }` — every `IObjectBase`/`IChange` implementer needs a stable discriminator name. VERIFIED |
| `ISelfNamedType<T>` | `IPolyType.cs:11` | Default-implements `IPolyType.TypeName => typeof(T).Name` — the common case (discriminator = CLR type name); used by nearly all sample/LcmCrdt change types. VERIFIED |

### `Adapters/`

| Type | File:Line | Purpose |
|---|---|---|
| `IObjectAdapterProvider` (internal) | `IObjectAdapterProvider.cs:8` | `GetRegistrations()`, `Adapt(obj)`, `CanAdapt(obj)`. VERIFIED |
| `AdapterRegistration` (internal record) | `IObjectAdapterProvider.cs:6` | `(Type ObjectDbType, Func<ModelBuilder, EntityTypeBuilder> EntityBuilder)`. VERIFIED |
| `DefaultAdapterProvider` | `DefaultAdapterProvider.cs:9` | The common path: CRDT entity classes directly implement `IObjectBase<T>`; `Adapt(obj)` just casts. `.Add<T>()` registers both the EF entity and the JSON derived-type. VERIFIED |
| `CustomAdapterProvider<TCommonInterface, TCustomAdapter>` | `CustomAdapterProvider.cs:9` | Lets a consumer's domain model stay Harmony-agnostic: entities implement only `TCommonInterface` (app-defined), and a separate `TCustomAdapter : ICustomAdapter<TCustomAdapter, TCommonInterface>` wraps them to satisfy `IObjectBase`. This is exactly the pattern LcmCrdt uses (`MiniLcmCrdtAdapter` wraps `IObjectWithId`). `.AddWithCustomPolymorphicMapping<T>(typeName, configureEntry)` lets a type's JSON discriminator differ from its default. VERIFIED |
| `ICustomAdapter<TSelf, TCommonInterface>` | `CustomAdapterProvider.cs:66` | `static abstract TSelf Create(TCommonInterface obj)`, `static abstract string AdapterTypeName`. VERIFIED |

### `Config/`

| Type | File:Line | Purpose |
|---|---|---|
| `HarmonyConfig` | `HarmonyConfig.cs:12` | The root options object. Full knob table in §9. VERIFIED |
| `BeforeSaveObjectDelegate` | `HarmonyConfig.cs:10` | `ValueTask (object obj, ObjectSnapshot snapshot)` — hook invoked right before every snapshot is persisted; lets a consumer stamp derived/denormalized fields onto the projected entity (used in `PersistExtraDataTests` to stamp `LastCommitId`/`DateTime`/`Counter`). VERIFIED |
| `ObjectTypeListBuilder` | `ObjectTypeListBuilder.cs:9` | Registry of CRDT object types + their adapters. `DefaultAdapter()`, `CustomAdapter<TCommonInterface,TAdapter>()`, internal `Adapt(obj)` (dispatches to the single adapter, or the first one that `CanAdapt`), `Freeze()` (adds the shadow-FK model configuration for projected tables), `CheckFrozen()`. VERIFIED |
| `ChangeTypeListBuilder` | `ChangeTypeListBuilder.cs:8` | Registry of `IChange` types: `.Add<TDerived>()` (idempotent — no-ops if already added), `Types: IReadOnlyList<RegisteredChangeType>`, `Freeze()`. VERIFIED |
| `RegisteredChangeType` (readonly record struct) | `ChangeTypeListBuilder.cs:6` | `(Type Type, string Discriminator)`. VERIFIED |
| `JsonOptionsBuilder` (internal) | `JsonOptionsBuilder.cs:5` | Collects `Action<JsonSerializerOptions>` callbacks registered via `HarmonyConfig.ConfigureJsonOptions`, applies them all once (after Harmony's own resolver/converter are wired), then freezes (further calls throw). VERIFIED |

### `Resource/` (full inventory in §7)

| Type | File:Line |
|---|---|
| `CreateRemoteResourceChange<TMetadata>` | `CreateRemoteResourceChange.cs:6` |
| `CreateRemoteResourcePendingUploadChange<TMetadata>` | `CreateRemoteResourcePendingUpload.cs:6` |
| `DeleteRemoteResourceChange<TMetadata>` | `DeleteRemoteResourceChange.cs:6` |
| `HarmonyResource<TMetadata>` | `HarmonyResource.cs:5` |
| `LocalResource` | `LocalResource.cs:6` |
| `RemoteResource<TMetadata>` | `RemoteResource.cs:14` |
| `NoMetadata` | `RemoteResource.cs:9` |
| `RemoteResourceNotEnabledException` | `RemoteResourceNotEnabledException.cs:3` |
| `RemoteResourceUploadedChange<TMetadata>` | `RemoteResourceUploadedChange.cs:9` |
| `SetRemoteResourceMetadataChange<TMetadata>` | `SetRemoteResourceMetadataChange.cs:6` |

### `Helpers/`

| Type | File:Line | Purpose |
|---|---|---|
| `DerivedTypeHelper` (internal static) | `DerivedTypeHelper.cs:7` | Reflection over STJ's `JsonPolymorphismOptions` (via `[UnsafeAccessor]` into an internal STJ static method!) to look up derived-type discriminators for a base type at runtime; `GetEntityDiscriminator<TBase>(Type)`, `GetEntityType<T>(discriminator)`, `AddDerivedType(...)`. VERIFIED — note the coupling to STJ internals via `UnsafeAccessor`, a fragility risk on STJ upgrades. |
| `LinqHelpers` (static) | `LinqHelpers.cs:3` | `FullOuterJoin` extension — used by `ResourceService.AllResources()` to join local + remote resource lists. VERIFIED |

### `SIL.Harmony.Linq2db` (separate NuGet package, optional)

| Type | File:Line | Purpose |
|---|---|---|
| `Linq2dbKernel.UseLinqToDbCrdt` (static) | `Linq2dbKernel.cs:13` | Registers a linq2db mapping schema mirroring the EF `HybridDateTime` conversion (UTC ticks), so linq2db queries against `Commit.HybridDateTime.DateTime` return correct results. Needed because linq2db doesn't automatically inherit EF's `HasConversion` mappings. LcmCrdt depends on this (`LcmCrdtKernel.cs:131-140`, with an explicit runtime guard because a `null` mapping schema previously caused a silent local-time bug, issue #2092). VERIFIED |

---

## 2. Full `DataModel` API

`DataModel` (`DataModel.cs:15`) implements `ISyncable, IAsyncDisposable`. Constructor is `internal` — must come from DI (`CrdtKernel.AddCrdtDataCore`, `DataModel.cs:57`).

| Method | Signature (essentials) | What it does / guarantees | Line |
|---|---|---|---|
| `AddChange` | `(Guid clientId, IChange change, CommitMetadata? meta = null) -> Task<Commit>` | Wraps a single change in a new commit and applies it. Delegates to `AddChanges`. | `53` |
| `AddChanges` | `(Guid clientId, IEnumerable<IChange> changes, CommitMetadata? meta = null) -> Task<Commit>` | Builds one `Commit` with N `ChangeEntities` (index-ordered) at the current hybrid time, then `Add`s it (locks the repo, checks not-already-present, opens a transaction unless already in one, `AddCommit`, `UpdateSnapshots`, optionally `ValidateCommits`, commits transaction). | `83` |
| `AddManyChanges` | `(Guid clientId, IEnumerable<IChange> changes, Func<CommitMetadata?> metaFactory, int changesPerCommitMax = 100) -> Task` | Chunks a large change set into multiple commits (`changesPerCommitMax` each), same clientId/metadata factory, applies them all under one transaction. | `61` |
| `ISyncable.AddRangeFromSync` | `(IEnumerable<Commit> commits) -> Task` | The sync-receive path: takes the max `HybridDateTime` of the incoming commits into the local clock (`_timeProvider.TakeLatestTime`), filters out commits already present (`FilterExistingCommits`), applies the rest inside a transaction, always validates (unconditionally, unlike `Add`). On `DbUpdateException`, dumps a diagnostic JSON to `FailedSyncOutputPath` and rethrows. **Idempotent**: re-adding an already-known commit is a no-op (`DataModelIntegrityTests.CanAddTheSameCommitMultipleTimesVisSync`). | `138` |
| `ISyncable.ShouldSync` | `() -> ValueTask<bool>` | Always `true` for `DataModel` (contrast `NullSyncable`=false, and `JsonSyncable`=true). | `185` |
| `RegenerateSnapshots` | `() -> Task` | **Full snapshot rebuild from commit history.** Deletes all snapshots + projected-table rows (`DeleteSnapshotsAndProjectedTables`), clears change tracker, loads *all* commits in `DefaultOrder`, replays them through `UpdateSnapshots`. Test-verified to reach the same logical state (`RegenerateSnapshots_WillArriveAtTheSameState`) though snapshot **row identity** is not preserved (new random `ObjectSnapshot.Id`s). This is Harmony's only "recompute everything" primitive — no incremental variant exists. | `234` |
| `GetLatestSnapshotByObjectId` | `(Guid entityId) -> Task<ObjectSnapshot>` | Current snapshot for one entity; throws `ArgumentException` if none. | `245` |
| `GetLatestSnapshots` | `() -> IAsyncEnumerable<ObjectSnapshot>` | Streams every entity's current `ObjectSnapshot`. | `252` |
| `GetLatest<T>` | `(Guid objectId) -> Task<T?>` | Current materialized entity (unwraps `ObjectSnapshot.Entity.DbObject` as `T`), or `null`. | `261` |
| `QueryLatest<T>` | `(Func<IQueryable<T>,IQueryable<T>>? apply = null) -> IAsyncEnumerable<T>` | Queries the **projected table** for `T` (requires `EnableProjectedTables`); auto-orders by `Order` then `Id` when `T : IOrderableCrdt`. Throws (`NotSupportedException`, from `CrdtRepository.GetCurrentObjects`) if projected tables are disabled. | `267` |
| `QueryLatest<T,TResult>` | `(Func<IQueryable<T>,IQueryable<TResult>> apply) -> IAsyncEnumerable<TResult>` | Projection overload. | `273` |
| `GetProjectSnapshot` | `(bool includeDeleted = false) -> Task<ModelSnapshot>` | All current `SimpleSnapshot`s + last-change metadata, wrapped in a `ModelSnapshot`. This is the "what state am I in" fingerprint used to detect two replicas converged (`LastCommitHash` equality). | `289` |
| `GetBySnapshotId<T>` | `(Guid snapshotId) -> Task<T>` | Materialize the entity stored in a specific (possibly historical) snapshot row. | `295` |
| `GetSnapshotsAtCommit` | `(Commit commit) -> Task<Dictionary<Guid, ObjectSnapshot>>` | **All entities' state as of a given commit** (inclusive). Uses `GetScopedRepository(commit)` (history truncated at that commit) to get the nearest persisted snapshots, then replays any pending commits on top via `SnapshotWorker.ApplyCommitsToSnapshots` if the persisted snapshots don't already reach that point (sparse-snapshot design, see §5). | `300` |
| `GetAtTime<T>` | `(DateTimeOffset time, Guid entityId) -> Task<T>` | Finds the last commit at/before `time`, delegates to `GetAtCommit`. Throws if no commit exists at/before that time. | `317` |
| `GetAtCommit<T>` (3 overloads) | `(Guid/Commit commitId/commit, Guid entityId[, CrdtRepository repo]) -> Task<T>` | Entity state as of (inclusive) a specific commit. Throws (`ArgumentNullException`) if the entity didn't exist yet. | `325,332,338` |
| `GetBeforeCommit<T>` (3 overloads) | `(Guid/Commit commitId/commit, Guid entityId[, repo]) -> Task<T?>` | Entity state *immediately before* a commit (i.e., exclusive) — returns `default` if there's no state before that commit (entity didn't exist yet) or no earlier commit at all. Added recently (harmony PR #81, `a14c5bb`) specifically to support "what did this look like right before this change" UI (used by LcmCrdt's `HistoryService.LoadChangeContext`). | `345,352,358` |
| `GetSyncState` | `() -> Task<SyncState>` | Per-client latest-commit-time map for this replica. | `393` |
| `GetChanges` | `(SyncState remoteState) -> Task<ChangesResult<Commit>>` | Commits this replica has that the given remote state doesn't. | `399` |
| `SyncWith` | `(ISyncable remoteModel) -> Task<SyncResults>` | Full bidirectional sync with one peer (delegates to `SyncHelper.SyncWith`). | `405` |
| `SyncMany` | `(ISyncable[] remotes) -> Task` | Sync with N peers, star-topology (local acts as hub: pulls all, then pushes the union to everyone) — see `SyncHelper.SyncMany`. | `410` |
| `DisposeAsync` | — | No-op (`ValueTask.CompletedTask`); repositories are created/disposed per-call via the factory. | `122` |

**`SyncResults` record** (`DataModel.cs:13`): `(Commit[] MissingFromLocal, Commit[] MissingFromRemote, bool IsSynced)`.

**Internal helpers** worth noting for correctness reasoning: `ValidateCommits` (`214`) walks `CurrentCommits()` in order and recomputes each commit's expected hash from its predecessor, throwing `CommitValidationException` with a detailed message (including all snapshots on the bad commit) on mismatch — proven by `DataModelIntegrityTests.InvalidCommitHashesResultInException`. `UpdateSnapshots` (`190`) is the bridge from "a batch of commits was added" to "snapshots are updated": it deletes now-stale snapshots (`DeleteStaleSnapshots`), bulk-preloads a snapshot-id lookup when more than 10 commits are being applied at once (`196-208`, to avoid N+1 and to work around a SQLite bound-parameter limit via `EF.Parameter`), then delegates to `SnapshotWorker`.

---

## 3. The change model

### `IChange` and its hierarchy (all VERIFIED — see §1 tables for file:line)

- `IChange` is the polymorphic root. Every concrete change type additionally implements `IPolyType` (usually via `ISelfNamedType<T>`, which defaults `TypeName` to the CLR type name) to supply a stable JSON discriminator.
- `Change<T>` is the typed base almost everyone extends; `CreateChange<T>` / `EditChange<T>` further specialize it to make the create-vs-edit intent explicit and to fail loudly if used the wrong way (`EditChange<T>.NewEntity` throws `NotSupportedException`; `CreateChange<T>.ApplyChange` is a guarded no-op, `Change.cs:56-61`, `Debug.Fail` if ever called).
- Built-ins: `DeleteChange<T>` (generic soft-delete), `SetOrderChange<T>` (generic fractional-order reorder).
- **`SetWordTextChange`-style dual-purpose changes** (implementing `Change<T>` directly, overriding both `NewEntity` and `ApplyChange`) are explicitly discouraged in comments (`SetWordTextChange.cs:8`: "recommended to use a CreateChange for new entities and EditChange for updates") but supported and used in tests.

### Discrimination / serialization mechanism (VERIFIED, and this is the part that changed most recently — harmony PR #80, `aae2ee0`)

Contrary to what `[JsonPolymorphic]`-on-`IObjectBase` might suggest, **`IChange` polymorphism is NOT handled by `System.Text.Json`'s built-in polymorphism** — it's fully custom, owned by `PeekThenConcreteChangeConverter` (doc comment on `IChange`, `Change.cs:6-11`, and on the converter itself, `PeekThenConcreteChangeConverter.cs:8-13`). Mechanism:

1. `HarmonyConfig.CreateJsonSerializerOptions` (`HarmonyConfig.cs:39`) builds one `JsonSerializerOptions` with a custom `TypeInfoResolver` (`MakeJsonTypeResolver`, `HarmonyConfig.cs:86`) plus `options.Converters.Add(new PeekThenConcreteChangeConverter(...))`.
2. The type-info modifier (`JsonTypeModifier`, `HarmonyConfig.cs:94`) injects a **synthetic, get-only, serialize-first** `$type` property onto every registered concrete change type (`AddSyntheticTypeDiscriminator`, `HarmonyConfig.cs:121`) — `Order = int.MinValue` forces it first in the JSON so the reader can peek it before committing to a shape.
3. **Read**: `PeekThenConcreteChangeConverter.Read` requires `$type` as the literal first JSON property (throws `JsonException` "*first property*" otherwise — tested, `ChangeConverterTests.Requires_type_as_first_property`). If the discriminator matches a registered `ChangeTypeListBuilder` entry, it re-winds the reader and lets STJ materialize the concrete type normally. If unrecognized, it captures the **entire raw JSON object** into an `OpaqueChange` (preserving `TypeName` and `EntityId` if parseable) — tested end-to-end (`ChangeConverterTests.Unknown_type_deserializes_to_OpaqueChange`, `OpaqueChange_round_trips_original_discriminator`, `Mixed_commit_round_trips_known_and_opaque_changes`).
4. **Write**: an `OpaqueChange` writes its `RawJson` back out verbatim (so round-tripping through a client that doesn't understand a new change type is lossless); a concrete change serializes normally, discriminator coming from the synthetic property.
5. `ObjectTypeListBuilder`'s `IObjectBase` polymorphism is different: it *does* use STJ's real polymorphism (`typeInfo.PolymorphismOptions!.DerivedTypes.Add(type)`, `HarmonyConfig.cs:107-114`) — objects are a closed, app-controlled set with no forward-compat concern analogous to changes arriving from a newer peer over sync.

**Practical implication**: Harmony has a first-class, tested answer to "a newer client wrote a change type I don't know about" (forward compatibility across app versions during sync) — it stores the payload opaquely and skips applying it until understood, rather than crashing or dropping data. There is **no equivalent mechanism for `IObjectBase`/entity types** — an unregistered object type is a hard failure (`ObjectTypeListBuilder.Adapt` throws `ArgumentException`, `ObjectTypeListBuilder.cs:91`).

### `CreateChange`/`JsonPatchChange` in LcmCrdt (consumer-side, not part of Harmony itself)

`LcmCrdt.Changes.JsonPatchChange<T>` (`languageforge-lexbox/backend/FwLite/LcmCrdt/Changes/JsonPatchChange.cs:12`) is `EditChange<T>` wrapping a `SystemTextJsonPatch.JsonPatchDocument<T>`. It is LcmCrdt's dominant "edit an existing field" change — nearly every `UpdateXxx` API method in `CrdtMiniLcmApi` diffs before/after via `JsonPatchChangeExtractor.ToChanges<T>()` (`JsonPatchChangeExtractor.cs:12`, only emits a change if the patch has operations) rather than hand-writing a dedicated change class per field. `JsonPatchValidator.ValidatePatchDocument` (`JsonPatchChange.cs:32`) **forbids index-based JSON-patch paths** ("no path operation can be made with an index" / "remove at index not supported") — this is a hard rule specifically because CRDT merges of array-index operations are unsound (two replicas could both mean "index 2" after independent inserts). `JsonPatchExampleSentenceChange` (a `JsonPatchChange<ExampleSentence>` subclass, `CustomJsonPatches/JsonPatchExampleSentenceChange.cs:12`) shows the escape valve for that rule: it special-cases `/Translation/{wsId}` paths and rewrites them onto the correct list entry by writing-system id instead of index, applying all other ops unmodified via `SystemTextJsonPatch`'s `ObjectAdapter`.

### `IChangeContext` — what it offers to a change's `ApplyChange`/`NewEntity` (VERIFIED, `IChangeContext.cs`)

| Member | Signature | Purpose |
|---|---|---|
| `Commit` | `CommitBase` | The commit this change is part of (for `commit.DateTime` stamping, e.g. `DeletedAt`). |
| `GetSnapshot` | `(Guid entityId) -> ValueTask<IObjectSnapshot?>` | Raw snapshot lookup (including in-flight/uncommitted-to-DB-yet snapshots within the same batch — see §5). |
| `GetCurrent<T>` | `(Guid entityId) -> ValueTask<T?>` (default-implemented) | Typed convenience over `GetSnapshot`. |
| `IsObjectDeleted` | `(Guid entityId) -> ValueTask<bool>` (default-implemented) | `true` if no snapshot or snapshot is soft-deleted — used pervasively by changes to avoid creating dangling/invalid references (e.g. `NewWordChange.cs:15-16`, `SetAntonymReferenceChange.cs:19-20`). |
| `Adapt` | `(object obj) -> IObjectBase` (internal) | Wraps a plain domain object back into `IObjectBase` via the configured adapter — used by generic changes like `DeleteChange<T>`/`SetOrderChange<T>` that need to set `DeletedAt`/`Order` through the interface. |
| `GetObjectsReferencing` | `(Guid entityId, bool includeDeleted = false) -> IAsyncEnumerable<object>` | All current entities whose `GetReferences()` include this id — used for duplicate-detection changes like `TagWordChange.IsDuplicate` (`TagWordChange.cs:40-49`). |
| `GetObjectsOfType<T>` | `(string jsonTypeName, bool includeDeleted = false) -> IAsyncEnumerable<T>` | Query current entities of one type by their discriminator — used e.g. by `SetTagChange` to detect duplicate tag text before creating/renaming (`SetTagChange.cs:13,26`). |

This is the **entire read surface available to a `Change` while it's being applied** — a change can look at current (or, transitively through the worker, in-flight) state of any entity, but cannot see anything from "the future" (later commits) or query arbitrary EF-mapped columns — only what `IObjectBase`/`GetReferences` expose.

---

## 4. Commits and history

### Shape (VERIFIED)

- `Commit : CommitBase<IChange>` (`Commit.cs:7`): `Id` (Guid, PK), `HybridDateTime`, `ClientId`, `Metadata: CommitMetadata`, `ChangeEntities: List<ChangeEntity<IChange>>`, `Snapshots: List<ObjectSnapshot>` (EF nav prop), `Hash`/`ParentHash` (private setters — only `SetParentHash(parentHash)` can change them, which recomputes `Hash`).
- `ServerCommit : CommitBase<ServerJsonChange>` (`ServerCommit.cs:7`) is the server's storage shape — same envelope, opaque change payload, plus `ProjectId`.

### Exactly what the hash covers (VERIFIED, `CommitBase.cs:32-40`)

```csharp
public string GenerateHash(string parentHash) {
    var idBytes = Id.ToByteArray();
    var parentHashBytes = Convert.FromHexString(parentHash);
    // hash = XxHash64(idBytes ++ parentHashBytes)
}
```

The hash is `XxHash64` over **only the commit's own `Id` (16 bytes) concatenated with the parent commit's hash (hex-decoded)** — a classic hash-chain. It does **not** hash `HybridDateTime`, `ClientId`, `Metadata`, or any change payload. Confirmed by tests: `CommitTests.SameGuidGivesSameHash` (same `Id` ⇒ same hash regardless of other fields not being copied) and `SameParentGuidGivesSameHash`/`ParentChangesHash`/`ChangingParentChangesHash` (hash is purely a function of `(Id, ParentHash)`). Practical consequence: **the hash chain proves ordering/lineage integrity of commit *identities*, not content integrity** — it would not detect a commit's change payload being silently altered in the DB (only `Id` and chain position are tamper-evident). `NullParentHash = "0000"` is the sentinel for "no parent" (`CommitBase.cs:12`).

### Ordering (VERIFIED)

Canonical order everywhere (`QueryHelpers.DefaultOrder`, `CommitBase.CompareKey`, `DbSetExtensions.DefaultOrder`): **`(HybridDateTime.DateTime, HybridDateTime.Counter, Id)`**, ascending, lexicographic. This triple is a total order (Guid as final tiebreaker), which is what makes deterministic replay/merge possible across replicas that received commits in different wall-clock arrival order.

### Out-of-order / late commits (VERIFIED — this is one of Harmony's most important properties)

`CrdtRepository.AddNewCommits` (`CrdtRepository.cs:408-422`):

```csharp
var oldestAddedCommit = newCommits.MinBy(c => c.CompareKey);
var parentCommit = await FindPreviousCommit(oldestAddedCommit);
var existingCommitsToUpdate = await GetCommitsAfter(parentCommit);
var commitsToApply = existingCommitsToUpdate.UnionBy(newCommits, c => c.Id).ToSortedSet();
UpdateCommitHashes(commitsToApply, parentCommit);   // rewrites the hash chain forward from parentCommit
_dbContext.AddRange(newCommits);
return commitsToApply;                              // = all commits from parentCommit onward, new AND pre-existing
```

So a commit dated **before** existing history is not rejected or appended at the tail — Harmony **splices it into its chronological position and rewrites every subsequent commit's hash** (`UpdateCommitHashes`, `CrdtRepository.cs:424-432`, walks the sorted set reassigning `ParentHash`/`Hash` in sequence). The XML doc on `AddCommit` states this explicitly: "If the new commit was authored before any commits that are already in the database, then history will be rewritten by updating those commit hashes" (`CrdtRepository.cs:384-388`).

Consequences flowing from this, all directly tested:
- **Stale snapshots after the insertion point are deleted and must be regenerated.** `DataModel.UpdateSnapshots` calls `repo.DeleteStaleSnapshots(oldestAddedCommit)` before reapplying (`DataModel.cs:194`); `CrdtRepository.DeleteStaleSnapshots` only bothers if the most recent existing snapshot is at/after the inserted commit's time (a performance short-circuit, `CrdtRepository.cs:130-139`).
- Late-arriving reference/delete/tag-uniqueness changes are retroactively reconciled: `DataModelReferenceTests.DeleteRetroactivelyRemovesRefs`, `DeleteAfterTheFactRewritesReferences`, `SnapshotsDontGetMutatedByADelete`, `DeleteDoesNotEffectARootSnapshotCreatedBeforeTheDelete`; `DataModelSimpleChanges.CanCreate2EntriesOutOfOrder`, `ChangeInsertedInTheMiddleOfHistoryWorks`; `DataModelReferenceTests.CanCreate2TagsWithTheSameNameOutOfOrder`, `CanUpdateTagWithTheSameNameOutOfOrder` — inserting a commit **before** an existing one that would now conflict (e.g. duplicate tag text) causes the *later* one (in the new chronological order) to self-delete via its own `NewEntity`/`ApplyChange` duplicate-check logic re-running against the now-different "current" state at that point in history.
- The rewrite is not free: `SnapshotWorker.ApplyCommitsToSnapshots` is invoked with every commit at/after the insertion point (not just the new ones), which is why `DataModelPerformanceTests`/benchmarks specifically measure "add a change when many snapshots already exist."
- `ValidateCommits` (`DataModel.cs:214-231`) is the safety net: if hash rewriting were ever skipped or done wrong, the very next validation pass (unconditional after sync, optional-but-default-on after local `AddChange`) throws `CommitValidationException`.

### `CommitMetadata` (VERIFIED, `CommitMetadata.cs`)

Well-known fields `AuthorName`, `AuthorId`, `ClientVersion`; free-form `ExtraMetadata: Dictionary<string,string?>` with an indexer. LcmCrdt uses `ExtraMetadata["SyncDate"]` (set/read via `CommitHelpers.SyncDate`/`SetSyncDate`, `CommitHelpers.cs:12-27`) to track which commits have round-tripped through a sync, and `ExtraMetadata["Template"]="true"` + `AuthorId = SystemAuthorId` (a well-known constant Guid) to stamp system/template-imported commits (`CommitHelpers.StampAsTemplate`).

### What can happen to an *existing* commit — no delete/revert primitive in Harmony itself

Harmony's own API surface has **no method to remove a commit**. The only way a commit's effects disappear is: (a) it's superseded by later changes in normal CRDT fashion, or (b) an application deletes the row directly (bypassing `DataModel`) and calls `RegenerateSnapshots()` — which is exactly what `LcmCrdt.SnapshotAtCommitService.DeleteCommitsAfter` does (see §"hard limits" below) — an entirely consumer-side operation using `context.Set<Commit>().RemoveRange(...)` plus `dataModel.RegenerateSnapshots()`, not a Harmony API. This is unsupported/unvalidated by Harmony itself (no guard against re-syncing a commit back in from a peer that still has it, no re-validation call built into that helper beyond `RegenerateSnapshots`, which does not call `ValidateCommits`).

---

## 5. Snapshots

### `ObjectSnapshot` (VERIFIED — full field list in §1)

One row = one entity's materialized state as of one commit. `IsRoot` marks a snapshot that has no "previous" snapshot it depends on for context (either the entity's first-ever snapshot, or a snapshot that replaced another root snapshot for the *same commit*, `SnapshotWorker.cs:219`). `TypeName` is the polymorphic discriminator (`IObjectBase.GetObjectTypeName()`), used for `GetObjectsOfType<T>` queries and `SimpleSnapshot.IsType<T>()`.

### `SnapshotWorker` (internal, `SnapshotWorker.cs`) — the change-application engine (VERIFIED)

Two entry points: `UpdateSnapshots(SortedSet<Commit>)` (persists to DB, called from `DataModel.UpdateSnapshots`) and the `static ApplyCommitsToSnapshots(snapshots, repo, commits, config)` (in-memory only, no persistence — used by `DataModel.GetSnapshotsAtCommit`/`GetAtCommit`/`GetBeforeCommit` to compute a **hypothetical/historical** state without writing anything). Both funnel through `ApplyCommitChanges` (`SnapshotWorker.cs:63-117`):

For each commit (in order), for each `ChangeEntity` in that commit (ordered by `Index`):
1. Look up the entity's previous snapshot (`GetSnapshot`, checks three tiers in order: `_pendingSnapshots` (this batch, non-root), `_rootSnapshots` (this batch, root), then a `_snapshotLookup` cache, falling back to a DB query — `SnapshotWorker.cs:150-172`).
2. **No previous snapshot**: if the change is an `OpaqueChange`, skip entirely (unknown-type forward-compat, `78-82`). Otherwise call `change.NewEntity(commit, context)` — throws if the change doesn't support it (e.g. an `EditChange<T>` targeting a never-created entity).
3. **Previous snapshot exists but is soft-deleted, and the change supports `NewEntity`**: "revive" — call `NewEntity` again (this is how `NewWordChange`/`CreateEntryChange` etc. double as "undelete" operations, tested in `DataModelSimpleChanges.NewEntity_UndeletesAnEntry`, `DeleteAndCreateTests.*`).
4. **Otherwise, if the change supports `ApplyChange`**: copy the previous entity (`prevSnapshot.Entity.Copy()`), apply the change, and if that transitioned it from not-deleted to deleted, cascade-remove references via `MarkDeleted` (below).
5. **Otherwise** (entity exists, not deleted, change doesn't support applying — e.g. a `CreateChange<T>` replaying against an already-created entity): **no-op**, no new snapshot (`NewEntityOnExistingEntityIsNoOp` test — verifies snapshot *ids* are unchanged, i.e. truly nothing happens, not even a redundant snapshot).
6. Generate a new snapshot for the resulting entity (`GenerateSnapshotForEntity`).

**`MarkDeleted`** (`SnapshotWorker.cs:124-148`): when an entity transitions to deleted, find every current (optionally including already-deleted) snapshot whose `References[]` contains the deleted id (`GetSnapshotsReferencing`), call `entity.RemoveReference(deletedId, commit)` on each (which may itself delete *that* entity, e.g. `Definition.RemoveReference` unconditionally sets `DeletedAt`), and recurse if so — cascading deletes transitively through the reference graph. This is why deleting a `Word` cascades to its `Definition`s and their `Example`s in the sample project, and to `Sense`s/`ExampleSentence`s/`ComplexFormComponent`s in LcmCrdt.

### Snapshot retention / thinning strategy (VERIFIED, `SnapshotWorker.cs:207-234`, `243-247`)

Harmony does **not** persist a snapshot for every commit that touches an entity — it keeps: (a) always the **root** snapshot (first-ever, or the one that "restarted" the chain at a shared commit), (b) always the **latest** snapshot, and (c) roughly every-other intermediate snapshot as a re-application checkpoint (`context.CommitIndex % 2 == 0 && !prevSnapshot.IsRoot && IsNew(prevSnapshot)` — the previous snapshot is kept as an "intermediate" only on even commit indices *and* only if it was newly created in this same batch, not loaded from DB, `226-229`). Multiple changes to the same entity **within one commit** collapse to a single snapshot (only the last one is kept; `OnlySaveTheLastSnapshotWhenThereAreMultipleChangesToAnEntityInOneCommit` test). Net effect: snapshot count scales sub-linearly with commit count (`ModelSnapshotTests.CanGetSnapshotFromEarlier` explicitly documents "there will only be a snapshot for every other commit"), and reconstructing an arbitrary historical point may require replaying a handful of intervening commits on top of the nearest kept snapshot — which is exactly what `GetSnapshotsAtCommit`/`GetAtCommit` do via `SnapshotWorker.ApplyCommitsToSnapshots`. `WorstCaseSnapshotReApply` test exercises the pathological case (1000 commits, all non-root snapshots deleted, forcing full replay from the root) to confirm correctness (not performance) is preserved.

### `DeleteStaleSnapshots` / regeneration (VERIFIED, `CrdtRepository.cs:130-139`, `DataModel.cs:234-243`)

- `DeleteStaleSnapshots(oldestChange)`: deletes every snapshot dated at/after `oldestChange`'s time (short-circuits if the most recent existing snapshot predates it — nothing to do). Called automatically before every `UpdateSnapshots` pass so replaying commits from `oldestChange` onward never sees now-invalid downstream snapshots.
- `DeleteSnapshotsAndProjectedTables`: wipes the `Snapshots` table *and*, if `EnableProjectedTables`, every projected table row for every registered object type (via reflection-invoked generic `DeleteProjectedTable<T>`). Used only by `RegenerateSnapshots`.
- There is **no time-based/age-based snapshot expiry** — retention is purely the every-other-commit thinning above; snapshots are never deleted "because they're old," only because they're stale relative to a rewrite.

### What state is retained, and for how long — summary

Commits (with their changes) are retained **forever** — the README says so explicitly ("Changes will be serialized and stored forever," `README.md:94`) and there is no API to prune commit history. Snapshots are a derived/disposable cache: thinned automatically per the above, fully regenerable from commit history at any time via `RegenerateSnapshots()`, and the source of truth is always the commit log, never the snapshot table.

---

## 6. Entities and adapters — hard constraints

### `IObjectBase` contract (VERIFIED, `IObjectBase.cs` in both `Core` and `Entities` namespaces — see §1)

Every CRDT-managed type must, directly or via an adapter, provide: a **stable `Guid Id`**, a **mutable `DateTimeOffset? DeletedAt`** (soft-delete is baked into the model — there is no hard delete), `GetReferences(): Guid[]` (every other entity this one points at, for cascade-delete/reference-integrity bookkeeping), `RemoveReference(Guid, CommitBase)` (react to a referenced entity disappearing — often by nulling the FK or self-deleting), `Copy(): IObjectBase` (a **deep-enough** copy — `SnapshotWorker` always copies before mutating so old snapshots are never retroactively changed; `ChangesToSnapshotsAreNotSaved` test enforces this), `GetObjectTypeName(): string` (stable discriminator — "should not change over time" per the doc comment, `IObjectBase.cs:31`), and `DbObject: object` (`[JsonIgnore]`) — the actual materialized domain object underneath (identity function for `IObjectBase<T>`, or the wrapped object for a custom adapter).

### EF Core coupling — precisely how deep it goes (VERIFIED)

- **Harmony's core storage tables (`Commits`, `ChangeEntities`, `Snapshots`) are EF Core entities, mapped via `IEntityTypeConfiguration<T>`** (`CommitEntityConfig`, `ChangeEntityConfig`, `SnapshotEntityConfig`). A consumer's `DbContext` must call `modelBuilder.UseCrdt(config)` in `OnModelCreating` (`CrdtDbContextModelExtensions.UseCrdt`, `CrdtDbContextOptionsExtensions.cs:9`).
- **A consumer's `DbContext` must implement `ICrdtDbContext`** (`ICrdtDbContext.cs:7`) — this is a direct dependency on `Microsoft.EntityFrameworkCore` types (`DbSet<T>`, `DatabaseFacade`, `ChangeTracker`, `EntityEntry`). There is no non-EF storage backend and no abstraction that would let a consumer swap in, say, a plain key-value store or Dapper.
- `CrdtRepository` issues **raw parameterized SQL** for the "current snapshots" query (`MakeCurrentSnapshotsQuery`, `CrdtRepository.cs:165-187`, a window-function `FromSql` with literal `"Snapshots"`/`"Commits"`/column names embedded in the SQL string) — this hardcodes **table and column names**, meaning a consumer cannot freely rename Harmony's own tables (only entity type registration and projected-table names are configurable) and the SQL as written is written to work against SQLite/PostgreSQL specifically (the dialect used in `EFCore.ComplexIndexes`/`FromSql` raw SQL, per `CommitEntityConfig.cs:25-28` comments referencing SQLite/Postgres reverse-scan behavior).
- **Projected tables** (`HarmonyConfig.EnableProjectedTables`, default `true`): when enabled, every registered object type gets its own real EF-mapped table (in addition to the JSON blob in `Snapshots`), with a **shadow foreign key column** (`ObjectSnapshot.ShadowRefName = "SnapshotId"`, `ObjectSnapshot.cs:50`) pointing back at the snapshot that produced the current row (`ObjectTypeListBuilder.Freeze()`, `HasOne(typeof(ObjectSnapshot)).WithOne().HasForeignKey(...).OnDelete(DeleteBehavior.SetNull)`, `ObjectTypeListBuilder.cs:22-31`). Changing that constant's string value would require a migration (explicit comment). Consumers query current data via this projected table (`DataModel.QueryLatest<T>`/`GetCurrentObjects<T>`), which **throws `NotSupportedException` if `EnableProjectedTables` is `false`** (`CrdtRepository.cs:284-291`) — i.e. the "pure JSON blob, no projection" mode exists in config but is not really usable for querying through `DataModel`'s own API; a consumer would have to hand-roll snapshot-JSON queries.
- **Object types need `EntityTypeBuilder<T>` configuration** (relationships, indexes, column types/conversions) supplied at registration time (`ObjectTypeListBuilder.DefaultAdapter().Add<T>(builder => ...)`), i.e. **every CRDT entity is, in the projected-tables case, also a first-class EF entity** with all that implies (navigation properties, FK constraints, `OnDelete` behavior — LcmCrdt uses `DeleteBehavior.Cascade`/`SetNull` extensively in `LcmCrdtKernel.ConfigureCrdt`).
- **Complex/collection-valued fields need manual `HasConversion` to JSON columns** — Harmony provides no generic "just serialize this field as JSON" convention; every consumer (Sample and LcmCrdt alike) hand-writes `HasConversion(x => JsonSerializer.Serialize(...), json => JsonSerializer.Deserialize<...>(...))` per field, with `"jsonb"` as the SQLite storage column type by convention.
- **A CRDT entity's identity/discriminator must be stable in JSON**, because `Snapshots.Entity` is a JSON blob keyed by that discriminator (`SnapshotEntityConfig`) — renaming a CRDT type breaks deserialization of every already-stored snapshot referencing the old name (LcmCrdt explicitly flags this risk in `MiniLcmCrdtAdapter.GetObjectTypeName`: "we might not want to do this as a refactor rename... will cause problems," `MiniLcmCrdtAdapter.cs:48-49`).
- **linq2db is a second, parallel query engine** layered on top for some LcmCrdt queries (`SIL.Harmony.Linq2db`), requiring its own mapping-schema mirror of any custom EF conversions (`Linq2dbKernel.cs`) — this is not required by Harmony core (`EnableProjectedTables`/EF alone is sufficient) but is load-bearing for LcmCrdt specifically and is a second place conversions must be kept in sync (confirmed by the runtime guard in `LcmCrdtKernel.cs:138-140` added after issue #2092, a real regression from schema drift between the two).

### Consumer-side `IObjectWithId` vs Harmony's `IObjectBase` (VERIFIED, LcmCrdt)

LcmCrdt does **not** implement `IObjectBase` directly on its domain models — it defines its own `IObjectWithId` (`MiniLcm/Models/IObjectWithId.cs:19`, a plain-C#, EF/Harmony-agnostic interface: `Id`, `DeletedAt`, `GetReferences()`, `RemoveReference(id, DateTimeOffset)`, `Copy()`), and bridges via `MiniLcmCrdtAdapter : ICustomAdapter<MiniLcmCrdtAdapter, IObjectWithId>` (`Objects/MiniLcmCrdtAdapter.cs:8`) registered through `ObjectTypeListBuilder.CustomAdapter<IObjectWithId, MiniLcmCrdtAdapter>()` (`LcmCrdtKernel.cs:194`). This is the exact use case `CustomAdapterProvider` exists for (§1) — it lets `MiniLcm` (the domain model package) have zero dependency on `SIL.Harmony`.

### What Harmony structurally cannot represent as an entity

- No polymorphic/inheritance object hierarchies beyond what STJ's discriminator mechanism supports (flat discriminator map per base type).
- No entity without a `Guid Id` — the whole snapshot/commit-linking model is keyed on `Guid EntityId` everywhere (`ChangeEntity.EntityId`, `ObjectSnapshot.EntityId`).
- No entity that isn't fully JSON-serializable through the configured `JsonSerializerOptions` (custom converters can paper over this, as LcmCrdt does extensively for `MultiString`/`RichString`/`WritingSystemId`/etc., but the constraint is real).

---

## 7. Resources (`Resource/` namespace + `ResourceService<TMetadata>`)

### Model (VERIFIED)

- **`RemoteResource<TMetadata>`** (`RemoteResource.cs:14`): the **CRDT-synced** side of a resource — `Id`, `DeletedAt`, `RemoteId?` (null until uploaded/known), `Metadata?: TMetadata` (app-defined, synced). It's a normal `IObjectBase<RemoteResource<TMetadata>>`, so it participates in commits/snapshots/sync exactly like any other entity. `NoMetadata` is a marker type for apps that don't need synced metadata.
- **`LocalResource`** (`LocalResource.cs:6`): **not** a CRDT entity — no `Id`/commit history, a plain EF row (`Id`, `LocalPath`, `FileExists()`) representing "this machine has a local copy of resource `Id` at this path." Never synced — each replica tracks its own local cache independently. Registered manually (`HarmonyConfig.AddRemoteResourceEntity`, `HarmonyConfig.cs:157-162`) as an ordinary EF entity with `HasKey`/`Property`, outside the `ObjectTypeListBuilder`/adapter machinery.
- **`HarmonyResource<TMetadata>`** (`HarmonyResource.cs:5`): the app-facing DTO joining the two — `Id`, `RemoteId?`, `LocalPath?`, `Metadata?`, plus `Local`/`Remote` bool flags (`MemberNotNullWhen`-annotated). Constructed by merging a `LocalResource?` and `RemoteResource<TMetadata>?` (throws `ArgumentException` if both are given and their ids mismatch).

### Changes (all `EditChange`/`CreateChange` over `RemoteResource<TMetadata>`, VERIFIED)

| Change | Purpose |
|---|---|
| `CreateRemoteResourceChange<TMetadata>` | Create with a known `RemoteId` (already uploaded, or a pre-existing server-side resource being attached). |
| `CreateRemoteResourcePendingUploadChange<TMetadata>` | Create with `RemoteId = null` — "known locally, not yet uploaded." |
| `RemoteResourceUploadedChange<TMetadata>` | Transition pending → uploaded: sets `RemoteId`, optionally overwrites `Metadata` if the upload returned some. |
| `SetRemoteResourceMetadataChange<TMetadata>` | Update metadata only. |
| `DeleteRemoteResourceChange<TMetadata>` | Soft-delete (same `DeletedAt = commit.DateTime` pattern as `DeleteChange<T>`). |

### `ResourceService<TMetadata>` — full method table (VERIFIED, `ResourceService.cs`)

| Method | Purpose |
|---|---|
| `AddExistingRemoteResource(path, clientId, resourceId, remoteId, metadata?, commitMeta?)` | Attach a resource that's already on the remote (server-side import case). |
| `AddLocalResource(path, clientId, metadata?, id?, resourceService?, commitMeta?)` | Register a **new** local file; if `resourceService` given, attempts immediate upload (falls back to "pending upload" on any exception, logged, never throws to the caller — `catch (Exception e)` swallow-and-log at `ResourceService.cs:86-91`). |
| `SetResourceMetadata(...)` | Update metadata via a change. |
| `ListResourcesPendingUpload()` | Local resources whose `RemoteResource.RemoteId` is still null. |
| `UploadPendingResources(clientId, remoteService, commitMeta?)` | Upload every pending resource; **partial-failure-safe**: if any individual upload throws, the `finally` block still persists whatever changes *did* succeed before rethrowing implicitly propagating (actually: the exception from `UploadResource` propagates out of the `foreach`, but the `finally` ensures already-collected `changes` are saved — "save what we got" semantics, `ResourceService.cs:139-152`). |
| `UploadPendingResource(resourceId\|HarmonyResource, clientId, remoteService, commitMeta?)` | Upload a single specific pending resource. |
| `ListResourcesPendingDownload()` | Remote resources with a `RemoteId` that aren't locally cached. |
| `DownloadResource(resourceId\|RemoteResource, remoteService)` | Download + record a `LocalResource`. |
| `GetLocalResource(resourceId)` | Local cache lookup only. |
| `AllResources()` / `GetResource(resourceId)` | Full outer join of local + remote view (`LinqHelpers.FullOuterJoin`). A remote resource that's soft-deleted is treated as absent even if a local copy exists (`GetResource`, `ResourceService.cs:244`). |
| `DeleteResource(clientId, resourceId, commitMeta?)` | Soft-deletes the CRDT record **and** deletes the local cache row (not the local file itself, per `DeleteLocalResource` in `CrdtRepository` — only the DB row). |

### Sync behavior for resources (VERIFIED)

`RemoteResource<TMetadata>` metadata and existence sync exactly like any other CRDT entity (ordinary commits/changes) — confirmed by `RemoteResourcesMetadataTests.SetResourceMetadata_UpdatesAndSyncs` (syncs to a forked replica and both sides converge). **The actual binary file content never travels through Harmony's commit/sync stream** — only the `RemoteId` (an opaque string identifier) and `Metadata` sync; the file itself moves via the app-supplied `IRemoteResourceService<TMetadata>.DownloadResource`/`UploadResource`, entirely outside Harmony (README: "instructs application code to download a resource," `IRemoteResourceService.cs:10-16`). `LocalResource` (the "do I have the bytes locally" bookkeeping) is never synced at all.

### How LcmCrdt uses it (VERIFIED, `LcmMediaService.cs`)

`LcmMediaService : IRemoteResourceService<LcmFileMetadata>` implements the app-supplied upload/download against a real HTTP media server (`IMediaServerClient`, proxied lexbox → FwHeadless). Notable hardening on top of bare `ResourceService`: single-flight download coalescing (`ConcurrentDictionary<Guid, Task<LocalResource>>` keyed by file id, to avoid a UNIQUE-constraint race when two callers request the same uncached file concurrently, `LcmMediaService.cs:65-133`), retry-with-backoff on transient HTTP statuses (`408/502/503/504/520`, `LcmMediaService.cs:140-188`), and an explicit "are we offline" branch (`ReadFileResult.Offline`) distinguishing "can't download because offline" from a real error. `Sense.Pictures` (a `List<Picture>` field with its own resource-id references) is the main consumer of this in the LcmCrdt domain model.

---

## 8. Sync — wire format and what a non-.NET peer would need to speak

### `ISyncable` contract (VERIFIED, `ISyncable.cs:3`) — repeated from §1 for convenience

`AddRangeFromSync(IEnumerable<Commit>)`, `GetSyncState() -> SyncState`, `GetChanges(SyncState) -> ChangesResult<Commit>`, `SyncWith(ISyncable) -> SyncResults`, `SyncMany(ISyncable[])`, `ShouldSync() -> bool`.

### The generic sync algorithm (VERIFIED, `SyncHelper.SyncWith`, `SyncHelper.cs:22-42`)

1. If either side's `ShouldSync()` is false, no-op (returns `IsSynced = false`).
2. `local.GetSyncState()` → `remote.GetChanges(localState)` → commits missing from local + remote's sync state.
3. `local.GetChanges(remoteSyncState)` → commits missing from remote.
4. (Test-only branch: if both sides are in-process `DataModel`s, JSON-round-trip the commit arrays to simulate network transit.)
5. `local.AddRangeFromSync(missingFromLocal)`; `remote.AddRangeFromSync(missingFromRemote)`.

`SyncMany` (`SyncHelper.cs:43-72`) makes the local replica act as a star-topology hub: pulls from every remote in turn (accumulating everything locally), then pushes the full local state (now including everyone else's changes) back out to every remote — so N replicas converge in one call without needing all-pairs sync.

### The actual wire format (VERIFIED against the real server, `LexBoxApi/Controllers/CrdtController.cs`)

`CrdtHttpSyncService`/`CrdtProjectSync` (LcmCrdt, `RemoteSync/CrdtHttpSyncService.cs:89-139`) implement `ISyncable` over Refit-generated HTTP calls to:

| Endpoint | Method | Request | Response |
|---|---|---|---|
| `/api/crdt/{projectId}/get` | GET | — | `SyncState` JSON (`{"ClientHeads": {"<guid>": <unix-ms-long>, ...}}`) |
| `/api/crdt/{projectId}/add` | POST | `StreamJsonAsyncEnumerable<ServerCommit>` body (streamed JSON array of `ServerCommit`), `?clientId=` | 200 OK; triggers a SignalR `OnProjectUpdated` broadcast to other connected clients on that project |
| `/api/crdt/{projectId}/changes` | POST | `SyncState` JSON body | `{MissingFromClient: ServerCommit[] (streamed IAsyncEnumerable), ServerSyncState: SyncState}` |
| `/api/crdt/{projectId}/countChanges` | POST | `SyncState` JSON body | `int` |
| `/api/crdt/checkConnection` | GET | — | 200 OK (health check, `ShouldSync` gate) |

A `ServerCommit` (`ServerCommit.cs:7`) is the wire shape: `Id`, `HybridDateTime` (`{DateTime, Counter}`), `ClientId`, `Metadata` (`{AuthorName, AuthorId, ClientVersion, ExtraMetadata: {...}}`), `ProjectId`, `ChangeEntities: [{Index, CommitId, EntityId, Change: {"$type": "...", ...fields}}]`. **A non-.NET peer implementing this protocol would need to**: (1) speak this exact JSON shape including the `$type`-first-property discriminator convention for each `Change` object it wants to *round-trip* (it does not need to *understand* unknown `$type`s — it can store/forward them as opaque JSON, which is exactly what the real server does via `ServerJsonChange`); (2) compute/verify the `XxHash64(Id ++ ParentHash)` hash chain if it wants to validate integrity, though nothing in the wire protocol *requires* the hash to be sent — `Commit.Hash`/`ParentHash` are `[JsonIgnore]` (`Commit.cs:33,39`) and are **not part of the wire payload at all**, recomputed locally by each receiver from `Id`/ordering; (3) implement the `SyncState`/`GetChanges` timestamp-diffing logic (`QueryHelpers.GetMissingCommits`, §1) per client-id, using Unix-millisecond timestamps; (4) know that `HybridDateTime.Counter` is an `int64` tiebreaker, and that ordering is `(DateTime, Counter, Id)`.

### Alternative peer: `JsonSyncable` (file-based `ISyncable`, VERIFIED, `JsonSyncable.cs`)

Not part of the HTTP wire protocol, but demonstrates the same `Commit` JSON shape used for a **file-based** peer: one `client_{guid}.jsonl` file per client, one JSON `Commit` per line, deduplicated by `Id` on write. This is directly exercised in cross-backend tests (`SyncableTests`, `CrossSyncableTests`) proving a `DataModel` and a `JsonSyncable` can sync bidirectionally through the same `ISyncable` contract with no special-casing — i.e. the `ISyncable` abstraction is genuinely peer-agnostic, not hard-wired to EF/DataModel-to-DataModel.

### What Harmony does *not* specify about the wire

There is no schema/versioning envelope beyond the JSON shapes themselves, no compression, no chunking/pagination contract in the interface itself (LcmCrdt's controller streams via `IAsyncEnumerable`/`StreamJsonAsyncEnumerable`, but that's a LcmCrdt/ASP.NET Core choice, not something `ISyncable` mandates), and no authentication/authorization (handled entirely by the HTTP layer around it — `[RequireScope(...)]`/`permissionService` in `CrdtController`).

---

## 9. Configuration and extension points

### `HarmonyConfig` — every knob (VERIFIED, `HarmonyConfig.cs`)

| Knob | Default | Effect |
|---|---|---|
| `EnableProjectedTables` | `true` | Maintain real EF tables per object type (for querying) in addition to the JSON `Snapshots` blob. Disabling breaks `DataModel.QueryLatest<T>`/`GetCurrentObjects<T>` (throws `NotSupportedException`). |
| `BeforeSaveObject: BeforeSaveObjectDelegate` | no-op | Hook called with `(object dbObject, ObjectSnapshot snapshot)` right before a snapshot is persisted — for stamping derived fields. |
| `AlwaysValidateCommits` | `true` | Re-validate the entire hash chain after every local `AddChange`/`AddChanges` (not just after sync, which always validates regardless). Off in perf tests (`alwaysValidate: false`) because it's O(all commits) per write. |
| `ChangeTypeListBuilder` | empty | Registry of known `IChange` types (discriminator ↔ CLR type). |
| `ObjectTypeListBuilder` | empty | Registry of known object types + adapters + EF model config. |
| `JsonSerializerOptions` (lazy) | Harmony-managed | Built once, on first access, from the above two registries + `ConfigureJsonOptions` callbacks; **frozen thereafter** (both registries `Freeze()`; further `.Add<T>()` calls throw `InvalidOperationException`). |
| `ConfigureJsonOptions(Action<JsonSerializerOptions>)` | — | Escape hatch to tweak the JSON options (e.g. naming policy) — runs *after* Harmony's resolver/converter are wired, and is itself frozen after first use of `JsonSerializerOptions` (`ConfigureJsonOptions_throws_after_freeze` test). Must not remove the `PeekThenConcreteChangeConverter` or replace `TypeInfoResolver`, per the doc comment (`HarmonyConfig.cs:53-56`) — this is a documented but **unenforced** invariant. |
| `RemoteResourcesEnabled` / `RemoteResourceMetadataType` | `false` / `null` | Set only via `AddRemoteResourceEntity<TMetadata>()`; gates all `ResourceService` methods (`RemoteResourceNotEnabledException` if called without it). |
| `LocalResourceCachePath` | `./localResourceCache` (absolute) | Where downloaded resource files land; consumer can override (LcmCrdt sets it per-project, `LcmCrdtKernel.cs:69-72`). |
| `FailedSyncOutputPath` | `./failedSyncs` (absolute) | Where `AddRangeFromSync`'s failure-diagnostic JSON dump is written on a `DbUpdateException`. |

### Every place a consumer plugs in (VERIFIED)

1. **DI registration**: `AddCrdtDataDbFactory<TContext>`/`AddCrdtData<TContext>` (`CrdtKernel.cs`) — choose per-call vs. shared-scope `DbContext` lifetime.
2. **Object type registration**: `config.ObjectTypeListBuilder.DefaultAdapter().Add<T>(entityBuilder)` (direct `IObjectBase<T>` implementers) or `.CustomAdapter<TCommon, TAdapter>().Add<T>(...)` (domain model stays Harmony-agnostic).
3. **Change type registration**: `config.ChangeTypeListBuilder.Add<TChange>()` — **every change type ever used must be registered up front**; this list is also the authoritative enumeration LcmCrdt reuses for TypeScript codegen and activity-log labeling (`LcmCrdtKernel.AllChangeTypes()`, `HistoryService.GetChangeTypeKeyFromType`).
4. **JSON customization**: `config.ConfigureJsonOptions(...)`.
5. **`BeforeSaveObject` hook** for derived/denormalized fields.
6. **Remote resources**: `AddCrdtRemoteResources<TMetadata>()` + a consumer-supplied `IRemoteResourceService<TMetadata>` implementation per sync direction.
7. **DbContext**: implement `ICrdtDbContext`, call `modelBuilder.UseCrdt(config)`.
8. **Sync peer**: implement `ISyncable` (HTTP, file, or anything else) — or use the provided `JsonSyncable`.
9. **linq2db** (optional, separate package): `builder.UseLinqToDbCrdt(provider)` if a consumer wants linq2db-flavored queries (e.g. for SQL functions EF can't express) against the same tables.
10. **`IHybridDateTimeProvider`** is DI-registered and replaceable (LcmCrdt doesn't override it; Harmony's own tests substitute `MockTimeProvider` for determinism).

---

## 10. Tests — what's actually pinned

The `SIL.Harmony.Tests` project (32 files) is comprehensive relative to the library's size. Grouped by what behavior is guaranteed vs. incidental:

| Guaranteed (has a dedicated, specific test) | Test file(s) |
|---|---|
| Hash = `XxHash64(Id ++ ParentHash)` only; hash chain rewrites on out-of-order insert; validation throws with a detailed message on tamper | `CommitTests`, `DataModelIntegrityTests.InvalidCommitHashesResultInException` |
| Out-of-order/late commit insertion is fully supported and retroactively reconciles references, deletes, and uniqueness | `DataModelReferenceTests` (10+ cases), `DataModelSimpleChanges` (ordering cases), `SnapshotTests` |
| Idempotent re-sync of an already-known commit | `DataModelIntegrityTests.CanAddTheSameCommitMultipleTimesVisSync`, `SyncableTests.AddRangeFromSync_IsIdempotent` |
| Cascading soft-delete through the reference graph, including same-commit and same-sync-batch races | `DataModelReferenceTests`, `DeleteAndCreateTests`, `DefinitionTests`, `ExampleSentenceTests` |
| Undelete via `NewEntity` replay on a soft-deleted entity | `DataModelSimpleChanges.NewEntity_UndeletesAnEntry`, `DeleteAndCreateTests` (6 variants incl. same-commit/same-sync) |
| Snapshot immutability (mutating a returned entity doesn't affect storage) | `DataModelSimpleChanges.ChangesToSnapshotsAreNotSaved` |
| Snapshot thinning (root always kept, latest always kept, ~every-other intermediate kept) | `SnapshotTests.MultipleChangesPreservesRootSnapshot`/`PreservesSomeIntermediateSnapshots` |
| Historical reconstruction (`GetAtCommit`/`GetBeforeCommit`/`GetAtTime`/`GetSnapshotsAtCommit`) correctness, including after deleting all non-root snapshots (forcing full replay) | `ModelSnapshotTests` (7 cases) |
| `RegenerateSnapshots()` reaches equivalent logical state (but not the same snapshot row ids) | `SnapshotTests.RegenerateSnapshots_WillArriveAtTheSameState` |
| `IChange` JSON discrimination: `$type` required first, unknown → `OpaqueChange`, round-trips losslessly, mixed known/unknown in one commit | `ChangeConverterTests` (6 cases) |
| Order/fractional-order reordering (`SetOrderChange<T>`, `Between`/consistent tie-break by `Id`) | `DefinitionTests` |
| Duplicate-prevention patterns (unique tag text, unique word-tag pairs) survive same-commit and out-of-order arrival | `SnapshotTests.DuplicatePrevention*`, `DataModelReferenceTests.CanCreate2TagsWithTheSameNameOutOfOrder` |
| Multi-client / N-way sync converges to identical state (`GetProjectSnapshot().LastCommitHash` equality) | `SyncTests`, `SyncableTests`, `CrossSyncableTests` |
| Cross-backend sync (`DataModel` ↔ `JsonSyncable`) via the same `ISyncable` contract | `CrossSyncableTests`, parameterized `SyncableTests` |
| SQLite bound-parameter-limit workarounds (large sync batches, large `FilterExistingCommits` calls) | `SyncTests.CanSyncCommitsWithMoreEntitiesThanTheSqliteParameterLimit`, `RepositoryTests.FilterExistingCommits_WorksWithMoreCommitsThanTheSqliteParameterLimit` |
| Thread-safety of concurrent `AddChange` against the same DB (per-connection-string `AsyncLock`) | `MultiThreadingTests` |
| Custom adapter round-trip (`ICustomAdapter`) for a Harmony-agnostic domain model | `CustomObjectAdapterTests` |
| `ConfigureJsonOptions` composition and freeze-after-use | `ConfigTests` |
| Resource lifecycle: create/pending-upload/upload/download/delete, metadata propagation and override rules, upload-failure resilience (still recorded as pending), commit-metadata attachment on every resource operation | `RemoteResourcesTests`, `RemoteResourcesMetadataTests`, `WordResourceTests` |
| Performance non-regression (writing a change with 10,000 existing snapshots vs. 1) | `DataModelPerformanceTests` (excluded from Debug builds) |

**Incidental / not specifically pinned** (works, but no dedicated regression test found): hash-chain rewrite performance characteristics at large scale beyond the specific 10k-snapshot perf test; `BeforeSaveObject` interaction with projected-table cascade deletes; behavior when `EnableProjectedTables=false` combined with `QueryLatest` (only the `NotSupportedException` path, not a full "headless" workflow); multi-hop `CustomAdapterProvider` scenarios with more than one custom adapter registered side-by-side with a default adapter (`ObjectTypeListBuilder.Adapt`'s `CanAdapt` fallback loop, `ObjectTypeListBuilder.cs:84-90`, has no dedicated test found).

---

## Analysis (the 15%)

### Is there ANY notion of a recorded-but-not-applied change? Any branching, staging, or draft concept?

**No**, with one narrow, purpose-specific exception. There is no branch/draft/staging concept anywhere in the codebase (confirmed by a repo-wide grep for `branch`/`draft`/`staging` across `SIL.Harmony`/`SIL.Harmony.Core` — zero matches). The one thing that is "recorded but not (yet) applied" is **`OpaqueChange`**: an unrecognized `$type` from a newer peer is stored verbatim in the `ChangeEntities` table and skipped by `SnapshotWorker` when the target entity doesn't exist yet (`SnapshotWorker.cs:76-82`) — but this is explicitly a forward-compatibility mechanism for *unknown* change types, not a general "hold this valid, known change for later application" facility. There is no way for an application to author a change today and mark it "don't apply yet" — `DataModel.AddChange`/`AddChanges` always synchronously runs it through `SnapshotWorker` in the same call. There is also no notion of multiple concurrent "heads"/branches per project — history is a single, continuously-reconciled hash chain per project, and `ClientId` is purely a sync-optimization partition key (which client authored which commits, to make delta-sync efficient), not a branch.

### Can you apply changes hypothetically and compute an effect without committing? What exactly would that take?

**Partially yes, but not as a general-purpose "dry run an arbitrary new change" API.** Harmony's own machinery already does this internally: `SnapshotWorker.ApplyCommitsToSnapshots` (`SnapshotWorker.cs:32-42`) is a static, non-persisting variant of the exact same commit-application logic used by `UpdateSnapshots` — it takes a dictionary of starting snapshots and a `SortedSet<Commit>`, runs them through `ApplyCommitChanges`, and returns the resulting in-memory snapshot dictionary **without any DB write**. `DataModel.GetSnapshotsAtCommit`/`GetAtCommit`/`GetBeforeCommit` are all built on this. What it takes to use this for a genuine "what if I applied this hypothetical change" query, concretely:
- Construct a real `Commit` object (with a real `Id`, a `HybridDateTime` from the current provider or a synthetic one, and one `ChangeEntity` wrapping the candidate `IChange`) — `WriteChange(..., add: false)` in the test helper (`DataModelTestBase.cs:100-123`) shows exactly this pattern, used throughout the test suite to build commits without persisting them.
- Load (or already have) the current `Dictionary<Guid, ObjectSnapshot>` for the affected entities via `CrdtRepository.GetCurrentSnapshotsAndPendingCommits`/`CurrentSnapshots()`.
- Call the internal `SnapshotWorker.ApplyCommitsToSnapshots(snapshots, repo, [thatCommit], config)` — **but this method is `internal`** (`SnapshotWorker.cs:12`), so a consumer outside the `SIL.Harmony`/`SIL.Harmony.Tests` assemblies (`InternalsVisibleTo` is scoped only to the test project, `SIL.Harmony.csproj`) cannot call it directly today. A consumer can only reach it indirectly through `DataModel.GetSnapshotsAtCommit(commit)` — which requires the commit to already exist in the DB — or by calling `AddChange` for real and inspecting the result (i.e., there's no true "commit, see the effect, and roll back" primitive; the closest is "commit for real, and if you don't like it, author an inverse/compensating change or delete-and-regenerate," see below).
- The change's `IChangeContext` (`GetSnapshot`/`GetObjectsReferencing`/`GetObjectsOfType`) would resolve correctly against the hypothetical in-memory state, because `ChangeContext` is constructed per-invocation from whatever `SnapshotWorker` instance it's given (`ChangeContext.cs:11-18`) — it has no hidden dependency on the change actually being persisted.

**Bottom line**: the *mechanism* for a side-effect-free "apply this and tell me what happens" already exists and is exercised by tests, but it is not exposed as public API — making it usable would require either (a) Harmony making `SnapshotWorker`/`ApplyCommitsToSnapshots` public (or wrapping it in a new public `DataModel` method, e.g. `DataModel.PreviewChange(IChange) -> Task<IObjectBase>`), or (b) a consumer doing what LcmCrdt already does for a related but heavier-weight purpose (see next answer) — fork the whole database and run a real commit against the fork.

### Can a commit be removed or reverted? What would it cost, given how late-arriving commits are already handled?

**Not through any Harmony API** — there is no `DataModel.RemoveCommit`/`RevertCommit` method, and the internal machinery assumes commits are append-only (from any single replica's perspective) plus reorderable-by-insertion, never subtractable. The consuming codebase (LcmCrdt) has built exactly this capability **entirely outside Harmony**, in `SnapshotAtCommitService.GetProjectSnapshotAtCommit`/`DeleteCommitsAfter` (`SnapshotAtCommitService.cs:92-112`):

```csharp
var idsAfter = await context.Commits.WhereAfter(targetCommit).Select(c => c.Id).ToArrayAsync();
context.Set<Commit>().RemoveRange(context.Set<Commit>().Where(c => idsAfter.Contains(c.Id)));
await context.SaveChangesAsync();
// ... then:
await dataModel.RegenerateSnapshots();
```

This is a **direct EF `RemoveRange` on the `Commits` DbSet**, bypassing `DataModel` entirely, followed by a full `RegenerateSnapshots()` to rebuild consistent state from whatever's left. Notably, `SnapshotAtCommitService` does this **only on a forked copy of the database** (`ForkDatabase`, backing up the SQLite file to a temp path first, `SnapshotAtCommitService.cs:41-42,83-90`) — the comment even flags the operation as risky ("deleting commits is kinda risky business 🕵️‍♂️", `SnapshotAtCommitService.cs:50`). This is used for a **read-only "what did the project look like at commit X" snapshot preview** (`api.TakeProjectSnapshot()` on the fork), not for actually truncating a live project's history.

What this costs, and what it *doesn't* handle, given everything documented above about out-of-order commits:
- **`RegenerateSnapshots()` is O(all remaining commits)** — it deletes every snapshot and projected-table row and replays the entire remaining history from scratch (`DataModel.cs:234-243`); there's no "just fix up the tail" fast path (contrast with normal out-of-order insertion, which at least short-circuits `DeleteStaleSnapshots` when nothing is actually stale).
- **`ValidateCommits` is never called** by `RegenerateSnapshots` or by this delete-then-regenerate pattern — so a real "delete a commit from live history" operation would not get Harmony's own hash-chain safety net for free; the deleter is trusting `RegenerateSnapshots` to produce a self-consistent result, not verifying the (now-shorter) hash chain is coherent. Whether the remaining commits' `ParentHash` values are still correct after commits in the *middle* are removed (not just the tail, which is all `SnapshotAtCommitService` does) is untested — the existing test suite only exercises hash rewriting for *insertion*, never *removal*.
- **It does nothing about sync.** If any peer still has the deleted commits and later syncs, `AddRangeFromSync`'s `FilterExistingCommits` only filters by `Id` already present — a deleted-then-re-synced-in commit would be silently **re-added**, because Harmony has no tombstone/exclusion-list concept for "this commit existed once and was deliberately removed." A real revert feature would need to either (a) leave a tombstone commit that peers respect, or (b) accept that any replica which already has the commit will reintroduce it on next sync — this is a structural gap, not an oversight in the LcmCrdt helper specifically.
- The `WhereAfter`/`WhereBefore` machinery Harmony already has (used identically for out-of-order insertion and for this deletion) is exactly what makes "delete everything after commit X" a one-line query — so the *querying* infrastructure for a revert feature is already fully present and reused; what's missing is (a) a public, supported API for it, (b) tombstone/anti-entropy semantics so sync doesn't undo the revert, and (c) validation coverage for the "remove from the middle, not just the tail" case.

### What are the hard limits — things Harmony structurally cannot do without a core change?

1. **No way to mark a change "recorded but not applied yet" / no staging or branching.** (See above — `OpaqueChange` is the only near-miss, and it's forward-compat-specific, not a general staging mechanism.)
2. **No public hypothetical-apply API.** The engine (`SnapshotWorker.ApplyCommitsToSnapshots`) exists and is exercised, but is `internal`; exposing it is a (small, mechanical) core change, not a workaround-able consumer-side feature.
3. **No commit removal/tombstone/anti-entropy-safe revert.** Deleting a commit and calling `RegenerateSnapshots()` works locally (LcmCrdt does exactly this today) but is silently undone by sync from any peer that still has the commit — fixing this needs a first-class tombstone concept in the wire protocol and sync algorithm, a genuine core change.
4. **The commit hash chain authenticates identity/order, not content** — it is not a content-integrity mechanism (§4). Any feature relying on "the hash proves nothing was tampered with in the payload" would be building on a false assumption without a core change to what `GenerateHash` covers.
5. **EF Core (and, transitively for LcmCrdt-style consumers, linq2db) is not an abstraction boundary — it's load-bearing all the way down** (raw `FromSql` window-function SQL with hardcoded table/column names, `ICrdtDbContext` requiring real `DbSet`/`EntityEntry`/`ChangeTracker` types). A consumer wanting a non-EF, non-relational storage backend would need to fork/rewrite `CrdtRepository`, not just implement an interface.
6. **No entity-type forward-compatibility analogous to `OpaqueChange`.** An unregistered `IObjectBase` derived type is a hard failure (`ObjectTypeListBuilder.Adapt` throws), unlike unregistered *change* types. Rolling out a new entity type across a fleet of unevenly-updated clients has no graceful degradation path the way rolling out a new change type does.
7. **Snapshot retention is a fixed every-other-commit heuristic**, not configurable per entity type or by any policy hook — a consumer with very hot, frequently-changed entities (where more frequent snapshotting would speed up historical reconstruction) or very cold entities (where thinning could be far more aggressive) has no lever for it beyond `RegenerateSnapshots()` (which doesn't change the retention policy, only re-runs it from scratch).
8. **`AlwaysValidateCommits` is O(all commits) and whole-chain** — there is no incremental/tail-only validation option, which is presumably why it's normally left on only in tests/until proven safe (perf tests explicitly disable it).

---

## What I could not determine

- **Whether `AddCrdtRemoteResources<TMetadata>` can be called more than once with different `TMetadata` types in the same app** (i.e., multiple independent resource "kinds") — nothing in the code or tests suggests it's supported (the config only tracks one `RemoteResourceMetadataType`), but I found no explicit test or doc asserting it's disallowed either; I did not attempt to compile a multi-type scenario to confirm the failure mode.
- **The exact behavior of hash-chain validation when a commit is removed from the *middle* of history** (as opposed to the tail, which is all `SnapshotAtCommitService` exercises) — I found no test covering this, and reasoning about it from the code is necessarily inferential (marked INFERRED above), not verified by running it.
- **Whether `CustomAdapterProvider`'s `CanAdapt`-based fallback dispatch (`ObjectTypeListBuilder.Adapt`, used when more than one adapter provider is registered) has any documented precedence/tie-breaking rule** beyond "first one that returns true" — no test exercises more than one custom adapter simultaneously.
- **Performance characteristics of hash-chain rewriting at very large scale for very-out-of-order inserts** (e.g. inserting a commit near the very start of a million-commit history) — the perf test suite measures snapshot re-application cost, not commit-hash-rewrite cost specifically, and I did not find a dedicated benchmark for that path.
- **Whether the raw `FromSql` window-function query in `CrdtRepository.MakeCurrentSnapshotsQuery` has been validated against database engines other than SQLite and PostgreSQL** (the CommitEntityConfig comments mention both explicitly) — I did not find CI matrix evidence either way; LcmCrdt's production usage is SQLite-only as far as I could see in this pass.
- I did **not** read `LcmCrdt.Tests` (the consumer's own test project) in this pass — the brief scoped the "test" inventory (§10) to Harmony's own tests, and I treated LcmCrdt purely as a usage example. Consumer-side test coverage of e.g. `SnapshotAtCommitService`'s revert behavior (there are `LcmCrdt.Tests/SnapshotAtCommitServiceTests.cs` and `CrdtRepairTests.cs` per the directory listing) was not read and could sharpen the "cost of revert" analysis above.
