# Worker Composition Bridge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Join the existing worker protocol and database components into one tested, project-keyed vertical
path before Baseline and scheduler work begins.

**Architecture:** `ProjectLocator` is canonicalized once in the LibLCM-free contract and is reused by the
workspace-key, database catalog, and persisted metadata. A compiled worker metadata record is the source for
the handshake and is checked against the installed manifest. One user-scoped worker owns a keyed runtime for
each opened project; the runtime opens one database, completes schema migration and recovery before admission, routes
host events by project, and keeps the process alive while durable work exists. The first real command is a
typed `job.status` request and response over the existing named pipe.

**Tech Stack:** C# 14, `netstandard2.0`, `net10.0`, named pipes, `System.Text.Json 8.0.5`,
`Microsoft.Data.Sqlite`, xUnit, PowerShell build and test gates.

---

## Scope and ownership

This bridge makes the worker usable enough to answer one real project-scoped question and to own the state
that answer depends on. It does not make the bridge a second CLI, Baseline engine, parser service, or
FieldWorks adapter.

The bridge owns these outcomes:

- equivalent valid Windows path spellings resolve to one canonical `ProjectLocator`;
- workspace-key, sibling database path, and persisted locator use the same canonical path;
- the running worker advertises the compiled metadata represented by its installed manifest;
- a project-scoped typed `job.status` command crosses a real named pipe and reads the project database;
- one keyed runtime owns one `MotifDatabase`, one recovery/admission gate, one host route, and its active-work
  keepalive lease;
- the Baseline plan and architecture specification state one unambiguous publication layout.

The following are explicitly outside this bridge and remain owned by their existing plans:

- full CLI command cutover and legacy-store retirement, owned by the CLI and FieldWorks integration plan;
- Baseline capture, bundle transfer handling, publication code, project lanes, and twenty-Dry-Run acceptance;
- PanGloss export, scheduling, CPU limits, and Assessment persistence;
- Apply, final Preflight, reconciliation, Conflict, and live applied-log repair;
- the `net48` FieldWorks adapter and its UI-thread, save, invalidation, and in-process Runner wiring;
- picture, audio, video, linked-file, or other media storage;
- a new migration release policy or schema compatibility window;
- installer, package layout, or launcher redesign;
- a `net48` consumer smoke implementation.

The bridge may invoke the already implemented migration and recovery components while opening a runtime. It
does not add migrations, change release compatibility policy, or migrate CLI commands.

## File map

The implementation changes only the following source and test files. A later plan may consume the interfaces
listed here, but it must not create a second project locator, database opener, global host route, or worker
metadata source.

| File | Responsibility in this bridge |
| --- | --- |
| `src/SIL.Motif.Contract/Projects/ProjectLocator.cs` | Validate and canonicalize the path once at the contract boundary. |
| `src/SIL.Motif.Worker/Projects/ProjectWorkspaceKey.cs` | Hash the already canonical locator without a second path algorithm. |
| `src/SIL.Motif.Worker/Store/ProjectDatabaseCatalog.cs` | Derive the sibling database from the canonical locator. |
| `src/SIL.Motif.Host/Store/MotifDatabase.cs` | Persist and compare the same canonical locator. |
| `src/SIL.Motif.Contract/Worker/WorkerBuildMetadata.cs` | Closed compiled product, protocol, capability, and digest record. |
| `src/SIL.Motif.Contract/Worker/WorkerJson.cs` | Shared protocol JSON options for typed command payloads. |
| `src/SIL.Motif.Worker/WorkerBuildMetadataProvider.cs` | Generated-in-build metadata used by the running worker. |
| `src/SIL.Motif.Worker/WorkerServer.cs` | Advertise compiled metadata and dispatch registered typed commands. |
| `src/SIL.Motif.Client/Worker/WorkerClient.cs` | Preserve the full worker offer on the negotiated connection. |
| `src/SIL.Motif.Client/Worker/WorkerConnection.cs` | Expose the authoritative worker offer to launcher validation. |
| `src/SIL.Motif.Worker/WorkerEventSink.cs` | Route host events through a project-keyed registration. |
| `src/SIL.Motif.Worker/Projects/ProjectHostRegistry.cs` | Own project-to-host registrations and disconnect cleanup. |
| `src/SIL.Motif.Worker/Projects/ProjectRuntime.cs` | Own one opened database, repositories, admission, and keepalive. |
| `src/SIL.Motif.Worker/Projects/ProjectRuntimeRegistry.cs` | Open, retain, and dispose runtimes by workspace key. |
| `src/SIL.Motif.Worker/WorkerCommandDispatcher.cs` | Decode the closed command registry and invoke typed handlers. |
| `src/SIL.Motif.Worker/Jobs/JobStatusCommandHandler.cs` | Read one status from the admitted project runtime. |
| `src/SIL.Motif.Contract/Jobs/JobStatusCommandContracts.cs` | Define the project-scoped request and response DTOs. |
| `src/SIL.Motif.Client/Worker/WorkerJobClient.cs` | Send the typed status request over `WorkerConnection`. |
| `tests/SIL.Motif.Tests/Contract/ProjectLocatorTests.cs` | Lock down canonical locator input and rejection rules. |
| `tests/SIL.Motif.Tests/Worker/ProjectWorkspaceKeyTests.cs` | Prove the key consumes the contract representation. |
| `tests/SIL.Motif.Tests/Store/MotifDatabaseMigrationTests.cs` | Prove database identity and catalog use the same path. |
| `tests/SIL.Motif.Tests/Worker/WorkerMetadataManifestTests.cs` | Prove compiled metadata and installed manifest agreement. |
| `tests/SIL.Motif.Tests/Worker/WorkerLauncherReviewTests.cs` | Prove strict launcher startup and sidecar validation fixtures. |
| `tests/SIL.Motif.Tests/Worker/ProjectRuntimeTests.cs` | Prove ownership, recovery admission, host routing, and leases. |
| `tests/SIL.Motif.Tests/Worker/WorkerJobStatusIntegrationTests.cs` | Exercise the typed command through a real named pipe. |

No solution file, generated model file, package reference, or target framework changes occur in this bridge.
Task 2 may add build metadata generation to the existing Worker project file. The existing `net10.0`,
`netstandard2.0`, and `net48` compatibility boundaries remain unchanged.

### Task 1: Canonicalize the project locator once

People may spell the same Windows project path with different separators or dot segments, but Motif must not
turn those spellings into different databases or workspaces. Invalid directory-like paths must fail before
they can reach any storage code.

**Files:**
- Modify: `src/SIL.Motif.Contract/Projects/ProjectLocator.cs`
- Modify: `src/SIL.Motif.Worker/Projects/ProjectWorkspaceKey.cs`
- Modify: `src/SIL.Motif.Worker/Store/ProjectDatabaseCatalog.cs`
- Modify: `src/SIL.Motif.Host/Store/MotifDatabase.cs`
- Create: `tests/SIL.Motif.Tests/Contract/ProjectLocatorTests.cs`
- Modify: `tests/SIL.Motif.Tests/Worker/ProjectWorkspaceKeyTests.cs`
- Modify: `tests/SIL.Motif.Tests/Store/MotifDatabaseMigrationTests.cs`

- [ ] **Step 1: Write the failing contract tests**

Add tests with these exact assertions:

```csharp
[Theory]
[InlineData(@"C:\Projects\Lang.fwdata", @"C:/Projects/Lang.fwdata")]
[InlineData(@"C:\Projects\.\Lang.fwdata", @"C:\Projects\Lang.fwdata")]
[InlineData(@"\\server\share\Projects\Lang.fwdata", "//server/share/Projects/Lang.fwdata")]
public void EquivalentWindowsFileSpellingsProduceOneLocator(string first, string second)
{
    var left = new ProjectLocator(first, "fw-id");
    var right = new ProjectLocator(second, "fw-id");

    Assert.Equal(left, right);
    Assert.Equal(left.FullFwDataPath, right.FullFwDataPath);
}

[Theory]
[InlineData(@".\Lang.fwdata")]
[InlineData(@"C:relative\Lang.fwdata")]
[InlineData(@"C:\Projects\Lang.fwdata\")]
[InlineData(@"C:\Projects\Lang.txt")]
[InlineData(@"\\server\Lang.fwdata")]
public void LocatorRejectsNonCanonicalProjectFile(string path)
{
    Assert.Throws<ArgumentException>(() => new ProjectLocator(path, "fw-id"));
}
```

Extend `ProjectWorkspaceKeyTests` with one cross-module vector. Construct a locator from slash-and-dot
spelling, compute `ProjectWorkspaceKey.CanonicalBytes`, and assert that a second equivalent spelling has the
same key and the same canonical bytes. Keep the existing big-endian length-framing golden digest unchanged.

Extend `MotifDatabaseMigrationTests` to create two equivalent locators, assert
`ProjectDatabaseCatalog.DatabasePathFor` returns one sibling path, open the database with the first locator,
reopen it with the second, and assert the persisted `MotifMetadata.FullFwDataPath` equals the locator's
canonical property. Add a test for a different directory with the same FieldWorks identity and assert it gets
a different workspace key and sibling database path.

- [ ] **Step 2: Run the complete gate and verify it is red**

Run from `C:\Users\johnm\Documents\repos\motif\.claude\worktrees\worker-baseline-implementation`:

```powershell
./test.ps1
```

Expected: the new canonicalization assertions fail because the contract currently returns the raw input and
the worker, catalog, and Host independently normalize it.

- [ ] **Step 3: Implement one canonicalization algorithm**

Make the `ProjectLocator` constructor assign a canonical absolute Windows file path. The algorithm must:

1. replace `/` with `\\`;
2. accept only a drive-absolute path (`C:\\...`) or a UNC path with server and share;
3. reject a trailing separator, an empty filename, and an extension other than `.fwdata`, ignoring case;
4. resolve `.` and `..` segments lexically without consulting filesystem existence;
5. preserve the drive/server/share root and use `\\` separators in the stored value;
6. leave the opaque FieldWorks identity unchanged except for the existing nonblank check.

Expose the canonical path only through `ProjectLocator.FullFwDataPath`. Remove `Path.GetFullPath` from
`ProjectWorkspaceKey` and `MotifDatabase` path preparation. Make `ProjectDatabaseCatalog.DatabasePathFor`
use `project.FullFwDataPath` directly, take the filename stem, and append `.motif.db`. Make metadata matching
compare the canonical path with ordinal case-insensitive semantics and the identity with ordinal semantics.
Do not call `File.Exists`, resolve symlinks, inspect directories, or infer identity from a filename.

The resulting core shape is:

```csharp
public sealed record ProjectLocator
{
    public ProjectLocator(string fullFwDataPath, string fieldWorksProjectIdentity)
    {
        FullFwDataPath = WindowsProjectPath.CanonicalFile(fullFwDataPath);
        FieldWorksProjectIdentity = RequireNonBlank(
            fieldWorksProjectIdentity, nameof(fieldWorksProjectIdentity));
    }

    public string FullFwDataPath { get; }
    public string FieldWorksProjectIdentity { get; }
}
```

If a private helper is needed, keep it in `ProjectLocator.cs`; do not add a second path utility to Worker or
Host. Preserve JSON property order and constructor deserialization.

- [ ] **Step 4: Run the complete gate and verify it is green**

```powershell
./test.ps1
```

Expected: comment hygiene passes, the solution compiles, all tests pass, and only the existing PanGloss-gated
tests may skip.

- [ ] **Step 5: Commit the locator boundary**

```powershell
git add src/SIL.Motif.Contract/Projects/ProjectLocator.cs `
  src/SIL.Motif.Worker/Projects/ProjectWorkspaceKey.cs `
  src/SIL.Motif.Worker/Store/ProjectDatabaseCatalog.cs `
  src/SIL.Motif.Host/Store/MotifDatabase.cs `
  tests/SIL.Motif.Tests/Contract/ProjectLocatorTests.cs `
  tests/SIL.Motif.Tests/Worker/ProjectWorkspaceKeyTests.cs `
  tests/SIL.Motif.Tests/Store/MotifDatabaseMigrationTests.cs
git commit -m "fix: unify project locator canonicalization"
```

### Task 2: Make compiled worker metadata authoritative

The launcher currently knows what a manifest claims while the worker advertises hard-coded protocol and
capability values. A worker must advertise the same closed metadata that its immutable installation records.

**Files:**
- Create: `src/SIL.Motif.Contract/Worker/WorkerBuildMetadata.cs`
- Modify: `src/SIL.Motif.Contract/Worker/WorkerCommands.cs`
- Create: `src/SIL.Motif.Worker/WorkerBuildMetadataProvider.cs`
- Modify: `src/SIL.Motif.Worker/WorkerServer.cs`
- Modify: `src/SIL.Motif.Worker/SIL.Motif.Worker.csproj`
- Modify: `src/SIL.Motif.Client/Worker/WorkerClient.cs`
- Modify: `src/SIL.Motif.Client/Worker/WorkerConnection.cs`
- Modify: `src/SIL.Motif.Launcher/InstalledWorkerCatalog.cs`
- Modify: `src/SIL.Motif.Launcher/Program.cs`
- Modify: `src/SIL.Motif.Launcher/WorkerSelector.cs`
- Create: `tests/SIL.Motif.Tests/Worker/WorkerMetadataManifestTests.cs`
- Modify: `tests/SIL.Motif.Tests/Worker/WorkerClientTests.cs`
- Modify: `tests/SIL.Motif.Tests/Worker/WorkerServerTests.cs`
- Modify: `tests/SIL.Motif.Tests/Worker/WorkerLauncherReviewTests.cs`
- Modify: `tests/SIL.Motif.Tests/Worker/WorkerSelectorTests.cs`

- [ ] **Step 1: Write failing metadata agreement tests**

Add tests that:

- construct `WorkerBuildMetadataProvider.Current` and assert its product version is nonblank, its protocol
  interval is valid, and its ordinal-sorted capability list is duplicate-free;
- compute its canonical digest twice and assert byte-for-byte equality;
- connect to a test `WorkerServer` and assert the handshake offer's product version, protocol range, and
  capabilities equal `WorkerBuildMetadataProvider.Current.ToHandshakeOffer()`;
- register an `InstalledWorker` whose version, protocol interval, and capabilities match the compiled record
  and assert `WorkerMetadataAgreement.RequireMatch` succeeds;
- alter exactly one manifest field and assert `RequireMatch` rejects it before process startup;
- alter the manifest capability list and assert the mismatch is rejected rather than silently dropped.

Use this closed shape in the tests:

```csharp
var metadata = new WorkerBuildMetadata(
    "3.4.2", new ProtocolRange(1, 1), new[] { "jobs.v1" });
Assert.Equal("3.4.2", metadata.ProductVersion);
Assert.Equal(metadata, WorkerBuildMetadata.Parse(metadata.ToCanonicalJson()));
Assert.Equal(metadata.MetadataDigest, WorkerBuildMetadata.Parse(
    metadata.ToCanonicalJson()).MetadataDigest);
```

- [ ] **Step 2: Run the complete gate and verify it is red**

```powershell
./test.ps1
```

Expected: compilation fails because the metadata record and agreement check do not exist.

- [ ] **Step 3: Add the closed compiled record and generated build values**

Implement the LibLCM-free record in Contract:

```csharp
public sealed record WorkerBuildMetadata
{
    public WorkerBuildMetadata(
        string productVersion,
        ProtocolRange protocols,
        IReadOnlyList<string> capabilities);

    public string ProductVersion { get; }
    public ProtocolRange Protocols { get; }
    public IReadOnlyList<string> Capabilities { get; }
    public string MetadataDigest { get; }
    public WorkerHandshakeOffer ToHandshakeOffer(string? connectionId = null);
    public string ToCanonicalJson();
    public static WorkerBuildMetadata Parse(string json);
}
```

Canonicalize capabilities with ordinal sorting and serialize product version, minimum and maximum protocol,
and capabilities in fixed property order before hashing UTF-8 bytes with SHA-256. Compatible metadata JSON
may contain unknown additive properties; every required field remains closed and validated.
Add `WorkerBuildMetadataProvider.Current` in Worker from MSBuild-generated constants. The Worker project emits
the same canonical metadata JSON beside the executable during publish; the launcher registration command reads
that file and passes the parsed record to `WorkerMetadataAgreement` before writing `manifest.json`. Do not
derive the protocol or capabilities from the product version.

Add `WorkerMetadataAgreement.RequireMatch(WorkerBuildMetadata compiled, InstalledWorker manifest)` in the
launcher boundary. It compares product version, protocol endpoints, sorted ordinal capabilities, and the
digest recomputed from those fields. The launcher registration path reads the sidecar and passes the parsed
metadata to `InstalledWorkerCatalog.Register`; `ValidateInstalled` rereads it before startup. Preserve the
full `WorkerHandshakeOffer` on `WorkerConnection` and `IWorkerConnection`. After the launcher starts a selected
candidate, require the first connected offer to match that candidate before reporting success. A connection
to an already-running compatible worker uses its offer as authority because no candidate was started in that
call. `WorkerServer` constructs its handshake offer only from
`WorkerBuildMetadataProvider.Current`; remove the `"0.0.0"` and hard-coded protocol interval from production
construction. Keep the compiled capability list empty in this task because no non-handshake production
handler exists yet. Task 4 adds `jobs.v1` atomically with the typed handler; a capability may never advertise
an unavailable command.

- [ ] **Step 4: Run the complete gate and verify it is green**

```powershell
./test.ps1
```

Expected: the handshake, launcher, and metadata tests pass; no target framework changes appear in the build
output.

- [ ] **Step 5: Commit the metadata agreement**

```powershell
git add src/SIL.Motif.Contract/Worker/WorkerBuildMetadata.cs `
  src/SIL.Motif.Contract/Worker/WorkerCommands.cs `
  src/SIL.Motif.Worker/WorkerBuildMetadataProvider.cs `
  src/SIL.Motif.Worker/WorkerServer.cs `
  src/SIL.Motif.Worker/SIL.Motif.Worker.csproj `
  src/SIL.Motif.Client/Worker/WorkerClient.cs `
  src/SIL.Motif.Client/Worker/WorkerConnection.cs `
  src/SIL.Motif.Launcher/InstalledWorkerCatalog.cs `
  src/SIL.Motif.Launcher/Program.cs `
  src/SIL.Motif.Launcher/WorkerSelector.cs `
  tests/SIL.Motif.Tests/Worker/WorkerMetadataManifestTests.cs `
  tests/SIL.Motif.Tests/Worker/WorkerClientTests.cs `
  tests/SIL.Motif.Tests/Worker/WorkerServerTests.cs `
  tests/SIL.Motif.Tests/Worker/WorkerLauncherReviewTests.cs `
  tests/SIL.Motif.Tests/Worker/WorkerSelectorTests.cs
git commit -m "feat: bind worker handshake to build metadata"
```

### Task 3: Add the keyed project runtime and admission gate

One worker can serve many projects, but each project needs one database owner and one host route. The runtime
is the lifetime boundary that prevents each command from opening a new database and makes recovery happen
before any command can observe or mutate project state.

**Files:**
- Create: `src/SIL.Motif.Worker/Projects/ProjectHostRegistry.cs`
- Create: `src/SIL.Motif.Worker/Projects/ProjectOperationGate.cs`
- Create: `src/SIL.Motif.Worker/Projects/ProjectRuntime.cs`
- Create: `src/SIL.Motif.Worker/Projects/ProjectRuntimeRegistry.cs`
- Modify: `src/SIL.Motif.Worker/WorkerEventSink.cs`
- Modify: `src/SIL.Motif.Worker/WorkerServer.cs`
- Modify: `src/SIL.Motif.Worker/Projects/ProjectWorkspaceKey.cs`
- Modify: `src/SIL.Motif.Worker/Jobs/WorkerRecoveryCoordinator.cs`
- Modify: `src/SIL.Motif.Worker/WorkerWorkTracker.cs`
- Modify: `src/SIL.Motif.Worker/Store/ProjectDatabaseCatalog.cs`
- Create: `tests/SIL.Motif.Tests/Worker/ProjectRuntimeTests.cs`
- Modify: `tests/SIL.Motif.Tests/Worker/WorkerServerTests.cs`

- [ ] **Step 1: Write failing runtime lifecycle tests**

Add tests that:

- call `GetOrOpen` twice with equivalent locator spellings and assert reference equality, one database path,
  and one `ProjectWorkspaceKey`;
- inspect the database file while the runtime is alive and assert a second registry cannot acquire it, using
  the existing owner-lock boundary pinned by `OnlyOneOwnerMayMigrateAndOpenAtATime` rather than adding another
  ownership mechanism;
- seed an interrupted job and an owned `work` directory, open the runtime, and assert startup cleanup and
  recovery finish before `ProjectRuntimeAdmission.Ready` is observable;
- make recovery throw and assert the runtime remains rejected, disposes the database, and opens no command;
- register live hosts for two projects on two connections and assert an event for project A never appears on
  project B's stream;
- disconnect a project host and assert only that project's registration and pending events are removed;
- seed a queued, running, or waiting job and assert the runtime holds a `WorkerWorkTracker` lease; after the
  job becomes terminal and the runtime is idle, assert the lease is released and the worker can eventually
  exit;
- release an idle runtime and assert its `MotifDatabase` is disposed, then open it again and assert a fresh
  runtime is constructed;
- hold a shared operation lease and assert `TryReleaseIfIdle` refuses to dispose the runtime until that lease
  ends;
- queue an exclusive lease behind one shared lease, assert a later shared lease cannot jump ahead, release the
  first lease, and assert the exclusive lease runs alone before later shared work resumes.

Use these interfaces in the tests:

```csharp
public enum ProjectRuntimeAdmission
{
    Opening,
    Recovering,
    Ready,
    Rejected,
    Disposed
}

public sealed class ProjectRuntime : IDisposable
{
    public ProjectLocator Project { get; }
    public string WorkspaceKey { get; }
    public MotifDatabase Database { get; }
    public JobRepository Jobs { get; }
    public ProjectRuntimeAdmission Admission { get; }
    public bool HasActiveWork { get; }
    public Task<IDisposable> AcquireOperationAsync(CancellationToken cancellationToken);
    public Task<IDisposable> AcquireExclusiveAsync(CancellationToken cancellationToken);
}

public sealed class ProjectRuntimeRegistry : IDisposable
{
    public ProjectRuntime GetOrOpen(ProjectLocator project);
    public bool TryReleaseIfIdle(string workspaceKey);
    public bool TryGet(string workspaceKey, out ProjectRuntime runtime);
}
```

- [ ] **Step 2: Run the complete gate and verify it is red**

```powershell
./test.ps1
```

Expected: compilation fails because the keyed runtime and project host registry do not exist.

- [ ] **Step 3: Implement one runtime per workspace key**

`ProjectRuntimeRegistry` stores a `ConcurrentDictionary<string, Lazy<ProjectRuntime>>` keyed by
`ProjectWorkspaceKey.Compute(project)`. `GetOrOpen` must use the canonical locator from Task 1 and construct
exactly one `MotifDatabase` through `ProjectDatabaseCatalog.OpenOwned`. The runtime creates its repositories
from that database and owns their lifetime; no command handler calls `ProjectDatabaseCatalog.OpenOwned`
directly.

`ProjectDatabaseCatalog.OpenOwned` completes the sibling database's schema migration and identity validation.
Run the existing `WorkerRecoveryCoordinator.RecoverStartup` immediately after repositories are constructed and
before setting `Admission = Ready`. Recovery includes the existing exact-path startup cleanup and interrupted
job handling. If schema migration, identity validation, cleanup, or recovery throws, set `Rejected`, dispose
every created resource, remove the dictionary entry, and rethrow the original exception. A rejected runtime
never serves a command. Importing the CLI-selected legacy Proposal and bulk-store sources remains in plan 6,
because their exact `--store` location is not derivable from `ProjectLocator`.

The canonical workspace key remains the durable identity and routing key; only derived filesystem paths use a
Windows-safe storage segment so the key's digest prefix cannot become an invalid directory name.

Every path that accesses a runtime repository must hold `ProjectRuntime.AcquireOperationAsync()` for the whole
access: command handlers, queued-job runners, schedulers, host-event continuations, and ordinary keepalive
refreshes. Startup recovery uses a separate internal refresh boundary while admission is still `Recovering`.
The writer-preferring `ProjectOperationGate` admits shared operations concurrently, but once an exclusive
waiter arrives, later shared work cannot jump ahead. The lease makes idle release safe now and gives plan 6 an
exclusive cutover barrier: legacy import can wait for all existing runtime activity and expose migrated state
atomically. Stage 2.5 does not guess a legacy location or perform that import. Startup recovery runs before
ordinary admission and therefore does not need to reacquire the gate it is establishing.

Keepalive is derived from durable job state, not from connection count. Acquire one `WorkerWorkTracker`
lease while any job for the project is queued, running, or waiting. `ProjectRuntime.RefreshWorkLease` queries
`JobRepository.ListActive` after recovery and after every production job transition; later job handlers and
schedulers must call that runtime boundary rather than mutate and forget the lease. Release it when no active
state remains. An ordinary connected client with no work does not hold the process alive.
`TryReleaseIfIdle` succeeds only when there is no active job, no live host registration, no command using the
runtime, and no pending event. The public registry constructor requires both activity predicates; the
WorkerServer composition boundary supplies the real project host registry and event-sink pending-state query.
Public callers also supply one `ProjectRuntimeActivity` boundary and perform activity mutations through it;
the idle decision and those mutations use that same synchronization root.

Use this concrete construction boundary:

```csharp
public sealed class ProjectRuntimeRegistry : IDisposable
{
    private readonly ProjectDatabaseCatalog _catalog;
    private readonly Func<JobRepository, string, WorkerRecoveryCoordinator> _recoveryFactory;
    private readonly WorkerWorkTracker _work;
    private readonly Func<string, bool> _hasLiveHost;
    private readonly Func<string, bool> _hasPendingEvents;
    private readonly ConcurrentDictionary<string, Lazy<ProjectRuntime>> _runtimes = new();

    public ProjectRuntimeRegistry(
        ProjectDatabaseCatalog catalog,
        Func<JobRepository, string, WorkerRecoveryCoordinator> recoveryFactory,
        WorkerWorkTracker work,
        Func<string, bool> hasLiveHost,
        Func<string, bool> hasPendingEvents,
        ProjectRuntimeActivity activity);

    public ProjectRuntime GetOrOpen(ProjectLocator project)
    {
        var canonical = project;
        var key = ProjectWorkspaceKey.Compute(canonical);
        var lazy = _runtimes.GetOrAdd(key, _ => new Lazy<ProjectRuntime>(
            () => ProjectRuntime.Open(canonical, key, _catalog, _recoveryFactory, _work,
                () => _hasLiveHost(key), () => _hasPendingEvents(key)),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try { return lazy.Value; }
        catch { _runtimes.TryRemove(new KeyValuePair<string, Lazy<ProjectRuntime>>(key, lazy)); throw; }
    }
}
```

The exact recovery factory may be an existing concrete constructor rather than the name shown in the sketch;
the production rule is that registry construction injects the clock, cleaner, and recovery coordinator so
tests control them. Do not add a new migration implementation.

- [ ] **Step 4: Replace the global host route with a project-keyed route**

`ProjectHostRegistry` maps the workspace key to one live host registration containing the canonical locator,
server connection id, host session id, negotiated protocol, stream, and shared write gate. Registering a
second host for the same key fails with a typed busy error; registering hosts for different keys succeeds.
`Unregister` is idempotent and removes only the matching connection and host-session generation. Event-sink
host lookup, pending registration, and disconnect faulting use one lock order, so a disconnect cannot fall
between host lookup and pending insertion.

Change `WorkerEventSink` so every send method receives a `ProjectLocator` (or a resolved workspace key) and
looks up that project's host. Keep the existing bounded correlation, one-result rule, write serialization,
and disconnect failure semantics. Remove its single `_stream` and single `_protocolVersion` fields. Change
`WorkerServer.RegisterLiveHost` and `UnregisterLiveHost` to require the project locator and route through the
registry. A connection may remain an ordinary client without being a host for any project.

The event API becomes:

```csharp
internal sealed record ProjectHostRegistration(
    string ConnectionId,
    string HostSessionId,
    int ProtocolVersion,
    Stream Stream,
    SemaphoreSlim WriteGate);

internal interface IProjectHostRegistry : IDisposable
{
    void Register(ProjectLocator project, ProjectHostRegistration registration);
    bool Unregister(ProjectLocator project, string connectionId, string hostSessionId);
    bool TryGet(ProjectLocator project, out ProjectHostRegistration registration);
}

public sealed class WorkerEventSink
{
    public Task<WorkerEventResultEnvelope> SendAsync(
        ProjectLocator project,
        string eventName,
        JsonElement payload,
        CancellationToken cancellationToken);
}
```

Preserve the settled event discriminators. Do not add Baseline, Apply, or reconciliation handlers; this task
only ensures that those already-defined event envelopes cannot cross project boundaries.

- [ ] **Step 5: Run the complete gate and verify it is green**

```powershell
./test.ps1
```

Expected: runtime lifecycle, recovery ordering, keyed host routing, and existing worker transport tests pass.

- [ ] **Step 6: Commit the project runtime**

```powershell
git add src/SIL.Motif.Worker/Projects `
  src/SIL.Motif.Worker/WorkerEventSink.cs `
  src/SIL.Motif.Worker/WorkerServer.cs `
  src/SIL.Motif.Worker/WorkerWorkTracker.cs `
  src/SIL.Motif.Worker/Store/ProjectDatabaseCatalog.cs `
  tests/SIL.Motif.Tests/Worker/ProjectRuntimeTests.cs `
  tests/SIL.Motif.Tests/Worker/WorkerServerTests.cs
git commit -m "feat: own keyed project runtimes"
```

### Task 4: Prove one typed `job.status` command

The first vertical command should return a durable fact without opening a live LibLCM cache. This proves the
contract registry, server dispatch, keyed runtime, database ownership, and client correlation are actually
connected.

**Files:**
- Create: `src/SIL.Motif.Contract/Jobs/JobStatusCommandContracts.cs`
- Create: `src/SIL.Motif.Contract/Worker/WorkerJson.cs`
- Modify: `src/SIL.Motif.Contract/Worker/WorkerCommands.cs`
- Modify: `src/SIL.Motif.Worker/WorkerBuildMetadataProvider.cs`
- Modify: `src/SIL.Motif.Worker/SIL.Motif.Worker.csproj`
- Create: `src/SIL.Motif.Worker/WorkerCommandDispatcher.cs`
- Create: `src/SIL.Motif.Worker/Jobs/JobStatusCommandHandler.cs`
- Modify: `src/SIL.Motif.Worker/WorkerServer.cs`
- Create: `src/SIL.Motif.Client/Worker/WorkerJobClient.cs`
- Create: `tests/SIL.Motif.Tests/Worker/WorkerJobStatusIntegrationTests.cs`
- Modify: `tests/SIL.Motif.Tests/Contract/WorkerProtocolTests.cs`
- Modify: `tests/SIL.Motif.Tests/Worker/WorkerMetadataManifestTests.cs`

- [ ] **Step 1: Write the failing closed-contract and pipe tests**

Add contract tests that serialize and parse these DTOs with stable JSON names and tolerate an additive unknown
property:

```csharp
public sealed record JobStatusRequest(ProjectLocator Project, string JobId);

public sealed record JobStatusResponse(
    string JobId,
    string ProjectKey,
    bool Found,
    string? Kind,
    JobStatus? Status,
    int? Attempt,
    string? UpdatedUtc,
    bool? CancellationRequested,
    JobFailureCategory? FailureCategory,
    long? Version);
```

Add a protocol test asserting `WorkerCommands.IsKnown("job.status")` is true, an unknown command remains
rejected, a client that did not negotiate `jobs.v1` cannot dispatch `job.status`, and a `job.status` envelope
cannot be deserialized as a different command DTO.

Add the named-pipe integration test with this sequence:

1. create a temporary `.fwdata` locator and the sibling database through the existing catalog;
2. insert one durable job with `JobStatus.WaitingForBaseline` through `JobRepository`;
3. start `WorkerServer` with an injected catalog and runtime registry;
4. connect a `WorkerClient` using the `jobs.v1` capability;
5. call `WorkerJobClient.GetStatusAsync(project, jobId, cancellationToken)`;
6. assert the response contains the expected job id, workspace key, status, attempt, and update timestamp;
7. assert a second equivalent locator returns the same record and a different project path does not;
8. assert the test server has not opened a live `LcmCache` and the database remains owned by the one runtime.

Add negative tests for a non-ready/rejected runtime, a missing project locator, a malformed job id, and a
protocol version or capability mismatch. Each must return a correlated refusal or close the connection before
the handler reads the database. A syntactically valid unknown job id is different: the handler queries the
repository and returns `Found = false` without treating absence as a protocol refusal.

- [ ] **Step 2: Run the complete gate and verify it is red**

```powershell
./test.ps1
```

Expected: compilation fails because the command discriminator, DTOs, dispatcher, handler, and typed client do
not exist.

- [ ] **Step 3: Add the closed command and typed client**

Add `JobStatus = "job.status"` to `WorkerCommands`, include it in `All`, and associate it with `jobs.v1`.
Expose the association through a closed command descriptor or `RequiredCapability` lookup. Preserve the
negotiated capability set on `WorkerControlConnection` and refuse a registered command when that connection
did not negotiate its required capability. Add `jobs.v1` to the compiled metadata and emitted sidecar in the
same change, and extend the metadata agreement test to prove the running offer and manifest both contain it.
Do not make the command registry accept arbitrary strings.

Implement the client façade:

```csharp
public sealed class WorkerJobClient
{
    private readonly WorkerConnection _connection;

    public WorkerJobClient(WorkerConnection connection) => _connection = connection;

    public async Task<JobStatusResponse> GetStatusAsync(
        ProjectLocator project, string jobId, CancellationToken cancellationToken)
    {
        var request = new JobStatusRequest(project, jobId);
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, WorkerJson.CreateOptions());
        using var requestDocument = JsonDocument.Parse(requestBytes);
        var payload = requestDocument.RootElement.Clone();
        var response = await _connection.SendAsync(new WorkerEnvelope(
            Guid.NewGuid().ToString("N"), WorkerCommands.JobStatus, payload,
            _connection.Negotiated.ProtocolVersion), cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<JobStatusResponse>(response.Payload, WorkerJson.CreateOptions())
            ?? throw new InvalidDataException("The job status response was empty.");
        if (!result.Found)
            throw new KeyNotFoundException("The requested job was not found.");
        return result;
    }
}
```

Implement `WorkerJson.CreateOptions()` in Contract and use it from the client and worker framing code. It must
register the established enum converters, preserve the established property naming and unknown-property
behavior, and return a fresh options object so callers cannot mutate a process-wide singleton. Validate
`ProjectLocator` and `JobId` before sending. A response must preserve the request id and negotiated protocol
version.

- [ ] **Step 4: Implement the dispatcher and real database handler**

Make `WorkerCommandDispatcher` map only registered discriminators to typed handlers:

```csharp
public interface IWorkerCommandHandler
{
    string Command { get; }
    Task<JsonElement> HandleAsync(JsonElement payload, CancellationToken cancellationToken);
}

public sealed class JobStatusCommandHandler : IWorkerCommandHandler
{
    public string Command => WorkerCommands.JobStatus;
    public Task<JsonElement> HandleAsync(JsonElement payload, CancellationToken cancellationToken);
}
```

The handler deserializes `JobStatusRequest`, obtains the runtime from `ProjectRuntimeRegistry`, requires
`Admission == Ready`, acquires a shared runtime operation lease, calls `JobRepository.Get(jobId)` without
opening another database, maps the returned `JobRecord` to `JobStatusResponse`, and releases the lease only
after serialization. Missing records produce `Found = false`; the typed client maps that response to
`KeyNotFoundException`. Neither side fabricates a terminal status. The handler does not invoke Runner, open
LibLCM, schedule work, or mutate a job.

Change `WorkerServer.WorkerControlConnection.RunAsync` to pass a non-handshake `WorkerEnvelope` to the
dispatcher and write exactly one correlated response. Keep handshake processing before dispatcher creation,
reject an unregistered command, reject a protocol mismatch, and close on malformed payloads. Construct the
dispatcher with the runtime registry and the `JobStatusCommandHandler`; later owning plans add their own
typed handlers explicitly.

- [ ] **Step 5: Run the complete gate and verify it is green**

```powershell
./test.ps1
```

Expected: the full suite passes, including a real named-pipe `job.status` round trip and the three existing
PanGloss skips when the executable is unavailable.

- [ ] **Step 6: Commit the vertical command**

```powershell
git add src/SIL.Motif.Contract/Jobs/JobStatusCommandContracts.cs `
  src/SIL.Motif.Contract/Worker/WorkerJson.cs `
  src/SIL.Motif.Contract/Worker/WorkerCommands.cs `
  src/SIL.Motif.Worker/WorkerBuildMetadataProvider.cs `
  src/SIL.Motif.Worker/SIL.Motif.Worker.csproj `
  src/SIL.Motif.Worker/WorkerCommandDispatcher.cs `
  src/SIL.Motif.Worker/Jobs/JobStatusCommandHandler.cs `
  src/SIL.Motif.Worker/WorkerServer.cs `
  src/SIL.Motif.Client/Worker/WorkerJobClient.cs `
  tests/SIL.Motif.Tests/Worker/WorkerJobStatusIntegrationTests.cs `
  tests/SIL.Motif.Tests/Worker/WorkerMetadataManifestTests.cs `
  tests/SIL.Motif.Tests/Contract/WorkerProtocolTests.cs
git commit -m "feat: route typed job status command"
```

The master plan, Baseline plan, and architecture specification already settle the transport archive versus
published-directory distinction before this implementation begins. Task 4 consumes that boundary and does
not implement Baseline transfer or publication.

## Final bridge verification

The user should be able to see one coherent worker boundary: a project locator identifies one database and
workspace, the worker metadata identifies one compatible executable, a project runtime owns the database,
and a real pipe command reads durable state from that runtime. The Baseline documents then describe the
storage handoff that the next stage will consume.

- [ ] Run `./test.ps1` from the repository root.
- [ ] Run `git diff --check`.
- [ ] Run `rg -n "net8\.0|TargetFramework" src tests` and confirm no target framework changed.
- [ ] Run `rg -n "0\.0\.0|new ProtocolRange\(1, 1\), Array\.Empty" src/SIL.Motif.Worker` and confirm no
  production hard-coded handshake metadata remains. Run `rg -n "publishedBaselineFwDataPath" docs/superpowers`
  and confirm the ambiguous Baseline path name is absent.
- [ ] Confirm `git status --short` contains only the intended task changes before each task commit.
