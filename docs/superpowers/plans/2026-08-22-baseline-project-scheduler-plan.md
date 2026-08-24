# Baseline and Project Scheduler Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Capture one minimal file-backed saved state and safely reuse it for many ordered Dry Runs without
holding or copying the live project each time.

**Architecture:** A live host streams a deterministic Baseline transport archive to worker storage. The
worker verifies and extracts it once, atomically publishes an immutable directory, schedules one LibLCM lane
per project, and opens the contained `.fwdata` as a single-use file-backed scratch per Dry Run. Project path
plus FieldWorks identity isolates same-identity clones.

**Tech Stack:** LibLCM, streaming ZIP or equivalent bounded bundle container, SHA-256, named pipes, xUnit.

---

### Task 1: Define Baseline identity and project workspace keys

Motif distinguishes saved project states and same-named project copies without guessing from a filename.
The Baseline identity, live observation, locator, workspace-key APIs, and their tests are available from
Stage 2.5; the later tasks consume these exact contracts.

**Files:**
- Use: `src/SIL.Motif.Contract/Baselines/BaselineToken.cs`
- Use: `src/SIL.Motif.Contract/Projects/LiveProjectObservation.cs`
- Use: `src/SIL.Motif.Contract/Projects/ProjectLocator.cs`
- Use: `src/SIL.Motif.Worker/Projects/ProjectWorkspaceKey.cs`
- Use: `tests/SIL.Motif.Tests/Worker/ProjectWorkspaceKeyTests.cs`
- Use: `tests/SIL.Motif.Tests/Contract/BaselineTokenTests.cs`

- [x] **Step 1: Write canonical identity tests**

Pin normalized case-insensitive Windows paths, same identity at different paths, same path with different
identity, Unicode path ordering, token JSON, captured edit generation, digest validation, and the rule that
timestamp, host session, and edit generation are freshness evidence but not semantic identity.

- [x] **Step 2: Run red**

Run `./test.ps1` before implementation. The failing run proves the Baseline observation, token, and
workspace-key types are the missing slice.

- [x] **Step 3: Implement immutable types**

```csharp
public sealed record BaselineToken(
    string ProjectIdentity,
    string SemanticSnapshotDigest,
    string ProjectionVersion,
    string CapturedUtc,
    string BundleDigest,
    string? CapturedHostSessionId,
    long? CapturedEditGeneration);

public sealed record LiveProjectObservation(
    string HostSessionId,
    long EditGeneration,
    bool HasUnsavedChanges,
    string SavedSemanticDigest);

```

The paired-database plan already defines `ProjectLocator`, because database registration needs that identity
before Baseline work begins. The composition bridge canonicalizes that locator once and makes
`ProjectWorkspaceKey.Compute` hash its canonical full path plus exact identity. It must not use project name
alone or migrate an old path implicitly.

- [x] **Step 4: Run green and commit**

Run `./test.ps1`, then:

```powershell
git add src/SIL.Motif.Contract src/SIL.Motif.Worker tests/SIL.Motif.Tests
git commit -m "feat: define baseline and workspace identity"
```

### Task 2: Build and verify the minimal Baseline bundle

One small saved handoff should reproduce FieldWorks behaviour without dragging linked media into background
work.

**Files:**
- Create: `src/SIL.Motif.LiveHost/SIL.Motif.LiveHost.csproj`
- Create: `src/SIL.Motif.Contract/Baselines/BaselineCommandContracts.cs`
- Modify: `src/SIL.Motif.Contract/Worker/WorkerCommands.cs`
- Modify: `src/SIL.Motif.Contract/Worker/WorkerJson.cs`
- Create: `src/SIL.Motif.Client/Worker/BaselineClient.cs`
- Modify: `src/SIL.Motif.Client/Worker/WorkerConnection.cs`
- Modify: `src/SIL.Motif.Worker/WorkerServer.cs`
- Modify: `src/SIL.Motif.Worker/WorkerCommandDispatcher.cs`
- Modify: `src/SIL.Motif.Worker/BinaryTransferServer.cs`
- Modify: `src/SIL.Motif.Worker/Program.cs`
- Modify: `src/SIL.Motif.Worker/SIL.Motif.Worker.csproj`
- Create: `src/SIL.Motif.Worker/Baselines/BaselineTransferRegistry.cs`
- Create: `src/SIL.Motif.Worker/Baselines/BaselineWorkspaceCatalog.cs`
- Create: `src/SIL.Motif.Worker/Baselines/BaselineTransferOfferCommandHandler.cs`
- Create: `src/SIL.Motif.Worker/Baselines/BaselinePublishCommandHandler.cs`
- Create: `src/SIL.Motif.LiveHost/Baselines/BaselineBundleWriter.cs`
- Create: `src/SIL.Motif.LiveHost/Baselines/BaselineSemanticDigest.cs`
- Create: `src/SIL.Motif.Worker/Baselines/BaselineBundleReceiver.cs`
- Create: `src/SIL.Motif.Worker/Baselines/BaselineRepository.cs`
- Modify: `src/SIL.Motif.Worker/Store/ProjectDatabaseCatalog.cs`
- Modify: `src/SIL.Motif.Worker/Projects/ProjectRuntime.cs`
- Modify: `src/SIL.Motif.Worker/Projects/ProjectRuntimeRegistry.cs`
- Modify: `Motif.sln`
- Test: `tests/SIL.Motif.Tests/Contract/BaselineCommandContractTests.cs`
- Test: `tests/SIL.Motif.Tests/Client/BaselineClientTests.cs`
- Test: `tests/SIL.Motif.Tests/Host/BaselineBundleTests.cs`
- Test: `tests/SIL.Motif.Tests/Worker/BaselineBundleReceiverTests.cs`
- Test: `tests/SIL.Motif.Tests/Worker/BaselineTransferIntegrationTests.cs`
- Test: `tests/SIL.Motif.Tests/Worker/WorkerMetadataManifestTests.cs`
- Test: `tests/SIL.Motif.Tests/Store/BaselineRepositoryTests.cs`

- [x] **Step 1: Write closed-contract, transfer, and publication tests**

Create a seeded `NewLangProjFixture` with custom writing-system collation and valid characters plus a linked
media sentinel larger than the `.fwdata`. Stream the bundle through a throttled stream. Assert it contains the
saved `.fwdata`, writing-system store, and only proven support files; excludes `.motif.db`, `LinkedFiles`,
backups, and unrelated files; opens through `LoadScratchCache`; and is equivalent on objects and writing
systems. Assert peak buffering stays below a fixed small buffer, not project size. Add closed JSON tests for
the Baseline offer request/response, publish request/response, binary completion correlation, failure shape,
command discriminators, and the exact `baseline.v1` capability. Add named-pipe tests proving `WorkerServer`
preserves the existing `job.status` handler, adds both Baseline handlers, rejects either Baseline command
without negotiated `baseline.v1`, obtains the keyed `ProjectRuntime`, requires
`ProjectRuntimeAdmission.Ready`, and acquires the existing exclusive operation lease before reading or
writing the sibling SQLite Motif store. A valid project
locator must select the catalog-derived sibling database; no command may accept an arbitrary database or
publication path, and the worker must never create a second SQLite store.

- [x] **Step 2: Run red**

Run `./test.ps1`. Expected: the closed Baseline contracts, client façade, composed handler, receiver, and
repository tests fail because the bundle slice is not implemented.

- [x] **Step 3: Implement the closed Baseline protocol and worker capability**

Add `WorkerCommands.BaselineOffer = "baseline.offer"` and
`WorkerCommands.BaselinePublish = "baseline.publish"`; both require the exact `baseline.v1` capability.
Add `baseline.v1` beside `jobs.v1` in generated worker build metadata and assert that the published manifest,
handshake offer, `WorkerCommands.RequiredCapability`, and Client request agree. Define these closed payloads;
their constructors reject blank identifiers, invalid tokens, inconsistent success/failure pairs, and unknown
failure codes:

```csharp
public sealed record BaselineOfferRequest(ProjectLocator Project);

public sealed record BaselineOfferResponse(
    BinaryTransferOffer? Offer,
    BaselineCommandFailure? Failure);

public sealed record BaselinePublishRequest(
    ProjectLocator Project,
    string TransferId,
    BaselineToken Token);

public sealed record BaselinePublishResponse(
    BaselinePublicationResult? Publication,
    BaselineCommandFailure? Failure);

public sealed record BaselinePublicationResult(
    string ProjectKey,
    BaselineToken Token);

public sealed record BaselineCommandFailure(
    BaselineFailureCode Code,
    bool Retryable,
    string Message);

public enum BaselineFailureCode
{
    CapacityUnavailable,
    TransferUnknown,
    TransferInvalid,
    ProjectRuntimeUnavailable,
    BundleInvalid,
    PublicationFailed
}
```

Expected operational failures return exactly one `BaselineCommandFailure`; success returns exactly one offer
or publication. Malformed envelopes, unknown commands, and capability violations remain protocol errors that
close the connection. The Client sequence is `baseline.offer`, binary upload and raw
`BinaryTransferCompletion`, then `baseline.publish`. Control-pipe ordering guarantees the server processes
the completion before the publish envelope. The transfer id is the correlation id across all three steps;
the request id continues to correlate each command response.

- [x] **Step 4: Implement the worker-owned verified-transfer lifecycle**

`WorkerServer` owns one `BinaryTransferServer` and one `BaselineTransferRegistry`, rooted under the worker
root passed by `Program` during composition. Only after the process owns the global worker mutex, and before
it accepts connections, may startup recovery clean that transfer root. The per-connection offer handler binds
each server-issued offer
to connection id, canonical project workspace key, and expiry. `WorkerControlConnection` treats a frame with
`Command` as a `WorkerEnvelope`; otherwise it recognizes a raw `BinaryTransferCompletion` only when every
required property is present, ignores unknown object properties within this protocol generation, and asks the
registry to complete it.
The registry converts a successful completion into this worker-internal value and never exposes a path on
the wire:

```csharp
internal sealed record VerifiedBinaryTransfer(
    string TransferId,
    string TemporaryPath,
    long ByteCount,
    string Sha256);
```

Only `BaselineTransferRegistry.Claim(connectionId, project, transferId)` can return that value, once, after
rechecking transfer-root containment, digest, length, expiry, connection binding, and project binding.
Connection loss, expiry, cancellation, failed publication, and worker shutdown delete unclaimed `.tmp` and
`.ready` files. Before accepting connections, startup recovery scans only the dedicated transfer root and
deletes orphan `.tmp` and `.ready` files left by a crash; containment/reparse tests prove it cannot follow or
delete outside that root. The registry owns a ready file from the atomic `.tmp`-to-`.ready` move until a
publish handler claims it; the receiver owns it after claim and always deletes it when publication finishes
or fails.

- [x] **Step 5: Implement streaming capture and atomic publication**

```csharp
public sealed class BaselineBundleWriter
{
    public Task<BaselineToken> WriteAsync(
        LcmCache savedCache,
        Stream destination,
        CancellationToken cancellationToken);
}

internal sealed class BaselineBundleReceiver
{
    public Task<BaselinePublication> PublishVerifiedAsync(
        VerifiedBinaryTransfer transfer,
        BaselineToken declaredToken,
        BaselinePublicationTarget target,
        CancellationToken cancellationToken);
}

internal sealed record BaselinePublication(
    string RootDirectory,
    string FwDataPath,
    BaselineToken Token);
```

`SIL.Motif.LiveHost` targets `netstandard2.0`, references Contract, Model, Runner, and LibLCM, and contains
only bundle writing and semantic-digest code. It must not reference Host, Worker, Client, or SQLite. Require
the host to call synchronous `FwDataProjectLoader.Save` before writing. The writer computes the token and
semantic digest during its single stream pass. The Client uses the server-issued offer, closes the binary
stream, sends its correlated completion, then sends the declared token in `baseline.publish`.

The receiver treats the verified temporary bundle as a transport archive, never as a project. It verifies the
declared token, bounded safe entry paths, exactly one `.fwdata`, required writing-system content, and the
exclusion of `.motif.db`, linked media, backups, repositories, and unrelated files. It extracts once into a
worker-owned temporary publication directory, validates the layout, and atomically publishes under
`%LOCALAPPDATA%/SIL/Motif/<storage-segment>/baseline/<bundle-digest>/`, where the storage segment comes only
from `BaselineWorkspaceCatalog.For(ProjectRuntime)` and is never caller-supplied. The catalog returns an
internal `BaselinePublicationTarget`; no public or wire API accepts a path or storage segment. Do not
recursively copy the
project folder and do not include linked media. `BaselineSemanticDigest` walks the existing model-coverage
snapshot projection in canonical ID order and hashes canonical bytes; it is independent of container
metadata, while `BundleDigest` hashes the exact transferred archive.

`ProjectRuntime` constructs and exposes `BaselineRepository` from its already-owned `MotifDatabase`, exactly
as it does `JobRepository`. `BaselinePublishCommandHandler` computes the workspace key, requires
`ProjectRuntimeRegistry.TryGet`, acquires `ProjectRuntime.AcquireExclusiveAsync`, and only then claims the
connection/project-bound verified transfer. While holding that lease it asks the receiver to publish and
records the result through `runtime.Baselines`; its publication target comes from
`BaselineWorkspaceCatalog.For(runtime)`. The receiver never opens SQLite. If the durable record fails,
the handler removes only the just-created unreferenced publication and preserves the previous repository row
and Baseline directory. A same-digest retry is idempotent. Any outcome deletes the claimed transport archive.
The handler returns the closed failure response for expected operational failures and does not leak paths.

- [x] **Step 6: Run green and commit**

Run `./test.ps1` and confirm the media exclusion, path-containment, Ready admission, operation-lease,
single-sibling-database, old-Baseline-preservation, archive-deletion, and named-pipe integration tests pass,
then:

```powershell
git add Motif.sln src/SIL.Motif.Contract src/SIL.Motif.Client src/SIL.Motif.LiveHost src/SIL.Motif.Worker tests/SIL.Motif.Tests/Contract tests/SIL.Motif.Tests/Client tests/SIL.Motif.Tests/Host tests/SIL.Motif.Tests/Store tests/SIL.Motif.Tests/Worker
git commit -m "feat: stream minimal file-backed baselines"
```

### Task 3: Integrate live-host leases with the per-project lane

FieldWorks and background work can coexist only when ownership changes and refresh ordering are explicit.

**Files:**
- Create: `src/SIL.Motif.Contract/Projects/LiveHostObservationCommandContracts.cs`
- Modify: `src/SIL.Motif.Contract/Worker/WorkerCommands.cs`
- Modify: `src/SIL.Motif.Contract/Worker/WorkerJson.cs`
- Create: `src/SIL.Motif.Client/Worker/LiveHostObservationClient.cs`
- Modify: `src/SIL.Motif.Client/Worker/WorkerConnection.cs`
- Use: `src/SIL.Motif.Worker/Projects/ProjectHostRegistry.cs`
- Create: `src/SIL.Motif.Worker/Projects/ProjectFreshnessTracker.cs`
- Create: `src/SIL.Motif.Worker/Scheduling/ProjectLane.cs`
- Create: `src/SIL.Motif.Worker/Scheduling/ProjectWorkItem.cs`
- Create: `src/SIL.Motif.Worker/Baselines/BaselineRefreshCommandHandler.cs`
- Modify: `src/SIL.Motif.Worker/WorkerServer.cs`
- Modify: `src/SIL.Motif.Worker/WorkerCommandDispatcher.cs`
- Test: `tests/SIL.Motif.Tests/Worker/ProjectLaneTests.cs`
- Test: `tests/SIL.Motif.Tests/Contract/LiveHostObservationContractTests.cs`
- Test: `tests/SIL.Motif.Tests/Worker/LiveHostObservationIntegrationTests.cs`

- [x] **Step 1: Write deterministic concurrency tests**

With controlled task completions, assert one lane per project, concurrent lanes across projects, and closed
JSON/named-pipe schemas for live-host registration, observation updates, and disconnect. Registration and
updates must carry the canonical `ProjectLocator`, nonblank host session, edit generation, dirty state, and
saved semantic digest. A stale session or lower generation update is ignored; a new session establishes a
new generation epoch and compares the saved semantic digest. Assert refresh as a barrier, old/new Baseline
assignment, waiting-for-host transfer after lease loss, accept/defer/decline refresh responses, an
already-started capture finishing, an Apply waiter outranking refresh work that has not started, and Apply
proceeding concurrently with a Dry Run already isolated on a private Baseline. Pin known-old from a later edit
generation or changed saved semantic digest, and “currentness not checked” when neither live observation nor
a lock-safe saved-project probe is available. For decline, cancellation, save, transfer, and verification
failure, assert later jobs remain `waiting-for-baseline`; only successful replacement releases the barrier,
unless the caller cancels and resubmits explicitly against the old token.

- [x] **Step 2: Run red**

Run `./test.ps1`. Expected: scheduler types are absent.

- [x] **Step 3: Implement an explicit lane state machine**

```csharp
public enum ProjectWorkKind { Refresh, DryRun, CandidateExport }

public sealed class ProjectLane
{
    public Task<ProjectWorkResult> EnqueueAsync(
        ProjectWorkItem item,
        CancellationToken cancellationToken);

    public Task<IDisposable?> TryAcquireApplyGateAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
```

Queue order, not task timing, chooses the Baseline. The Worker composes each observation command with the
existing `ProjectRuntimeRegistry`, the keyed host route, the runtime Ready admission, and the same operation
lease used by persistence; it never opens another database or registers a global host. Live-host registration
is a renewable connection lease; change notifications update the generation and dirty state, and disconnect
releases authority. Generation comparisons are valid only inside one epoch; a new epoch compares semantic
digest. A queued refresh records an accept, defer, or decline response durably. Only an accepted or deferred
request remains executable by the CLI Host after FieldWorks releases authority and the lock is available;
decline terminates that request. Apply has priority over refresh work that has not started and uses the
per-project Apply gate. The gate conflicts with refresh/capture but not a Dry Run that has already opened
private Baseline state; per-project Dry Runs remain serial with each other. Cleanup and eviction acquire the
same lane/reference gate, or use a compare-and-delete generation, so a new pin or host lease cannot appear
between checking and deletion. Preserve the checked bottom-up, nonrecursive deletion and rename/reparse
substitution tests.

- [x] **Step 4: Run green and commit**

Run `./test.ps1`, then:

```powershell
git add src/SIL.Motif.Contract src/SIL.Motif.Client src/SIL.Motif.Worker tests/SIL.Motif.Tests/Contract tests/SIL.Motif.Tests/Client tests/SIL.Motif.Tests/Worker
git commit -m "feat: schedule project baseline work"
```

### Task 4: Run twenty isolated Dry Runs from one Baseline

The performance promise is one saved state reused many times, with every evaluation independent and honest
about when that state was captured.

**Files:**
- Create: `src/SIL.Motif.Host/Baselines/BaselineScratchFactory.cs`
- Create: `src/SIL.Motif.Worker/Jobs/DryRunJobHandler.cs`
- Modify: `src/SIL.Motif.Runner/DryRun/DryRunScratch.cs`
- Test: `tests/SIL.Motif.Tests/Worker/BaselineDryRunIntegrationTests.cs`
- Test: `tests/SIL.Motif.Tests/Host/BaselineScratchFactoryTests.cs`

- [x] **Step 1: Write the twenty-run acceptance test**

Capture once, execute the same Proposal twenty times, and assert identical effect and footprint digests,
unchanged Baseline bytes, no saved scratch mutation, one active scratch at a time, no linked-media bytes in
workspace, known-old warning after a higher live edit generation, changed saved semantic digest after restart,
and “currentness not checked” when observation is unavailable. Assert the capture performs one saved-project
operation and the twenty Dry Runs perform no per-run project copy, use one cache/project at a time, and never
save or mutate the canonical project. Add cancellation injection before publication and prove no partial Dry
Run record exists.

- [x] **Step 2: Run red**

Run `./test.ps1`. Expected: the Worker job handler and Host scratch factory are absent.

- [x] **Step 3: Implement file-backed single-use scratches**

```csharp
public sealed class BaselineScratchFactory
{
    public DryRunScratch OpenSingleUse(string publishedFwDataPath);
}
```

Put `BaselineScratchFactory` in `SIL.Motif.Host` targeting `net10.0`, where `FwDataProjectLoader` already
lives. The Worker calls this Host factory for a published `.fwdata`; `SIL.Motif.LiveHost` remains
`netstandard2.0` and is limited to bundle writing and semantic digest computation, with no Host, Worker,
Client, or SQLite reference. Open the `.fwdata` recorded inside the immutable published Baseline directory
directly through `LoadScratchCache`; do not copy or extract the project for each Dry Run. Reuse
`DryRunScratch.Adopt` and the existing `ProposalDryRunner`; modify Runner only if a concrete API gap is
demonstrated by a failing test. The scratch may read the sibling writing-system/support content in that
directory but may not mutate or save it. Dispose without saving, verify the published directory remains
unchanged, and publish the Dry Run record only after complete read-back.
Return status containing `BaselineToken` and `CapturedUtc`; use `ProjectFreshnessTracker` to report current,
known-old, or not-checked status, never to silently refresh or cancel.

- [x] **Step 4: Run green and commit**

Run `./test.ps1`, confirm the one-save/twenty-Dry-Run, media exclusion, cache/project ownership, and
no-saved-mutation assertions, then:

```powershell
git add src/SIL.Motif.Host src/SIL.Motif.Runner src/SIL.Motif.Worker tests/SIL.Motif.Tests/Host tests/SIL.Motif.Tests/Worker
git commit -m "feat: reuse baselines for isolated dry runs"
```
