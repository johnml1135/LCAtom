# Worker Protocol and Launcher Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Establish one version-negotiated local worker endpoint that `net10.0` and `netstandard2.0` clients
can share without allowing multiple database-owning workers.

**Architecture:** LibLCM-free request contracts live in Contract; a small `netstandard2.0` client transports
them over named pipes; a `net10.0` worker dispatches them; a stable launcher chooses the newest installed
compatible worker. Product SemVer is diagnostic only and every connection negotiates protocol and capabilities.

**Tech Stack:** C# 14, named pipes, `System.Text.Json 8.0.5`, Windows mutexes, xUnit.

---

### Task 1: Freeze the handshake and envelope contract

Older and newer clients need one small, explicit way to decide whether they can safely work together.

**Files:**
- Create: `src/SIL.Motif.Contract/Worker/ProtocolVersion.cs`
- Create: `src/SIL.Motif.Contract/Worker/WorkerEnvelope.cs`
- Create: `src/SIL.Motif.Contract/Worker/WorkerEventEnvelope.cs`
- Create: `src/SIL.Motif.Contract/Worker/WorkerEventResultEnvelope.cs`
- Create: `src/SIL.Motif.Contract/Worker/BinaryTransferOffer.cs`
- Create: `src/SIL.Motif.Contract/Worker/BinaryTransferCompletion.cs`
- Create: `src/SIL.Motif.Contract/Worker/WorkerCommands.cs`
- Create: `src/SIL.Motif.Contract/Worker/WorkerHandshake.cs`
- Test: `tests/SIL.Motif.Tests/Contract/WorkerProtocolTests.cs`

- [ ] **Step 1: Write failing round-trip and negotiation tests**

Pin protocol intersection, missing required capabilities, unknown JSON properties, bounded ids, event versus
response framing, one-use binary offers, every currently settled transport discriminator, and product-version
non-authority. Use these public shapes in the tests:

```csharp
var request = new WorkerHandshakeRequest(
    "motif-cli", "3.4.2", new ProtocolRange(1, 2), new[] { "jobs.v1" });
var worker = new WorkerHandshakeOffer(
    "3.5.0", new ProtocolRange(2, 3), new[] { "jobs.v1", "baseline.v1" });
var result = WorkerHandshake.Negotiate(request, worker);
Assert.Equal(2, result.ProtocolVersion);
Assert.Equal(new[] { "jobs.v1" }, result.Capabilities);
```

- [ ] **Step 2: Run the suite and verify red**

Run `./test.ps1`. Expected: compilation fails because the worker contract types do not exist.

- [ ] **Step 3: Implement the closed transport types**

Implement immutable `ProtocolRange`, `WorkerHandshakeRequest`, `WorkerHandshakeOffer`,
`WorkerHandshakeResult`, and `WorkerCommands` as the closed transport discriminator registry for
discriminators settled at this stage.

Each later owning plan must add its closed typed command/response DTO and registry entry before adding
a handler. The generic envelopes remain `JsonElement` framing, but a handler may never accept a command
without its typed DTO and schema.

The transport envelope shapes are:

```csharp
public sealed record WorkerEnvelope(
    string RequestId,
    string Command,
    JsonElement Payload,
    int ProtocolVersion);

public sealed record WorkerEventEnvelope(
    string EventId,
    string Event,
    JsonElement Payload,
    int ProtocolVersion);

public sealed record WorkerEventResultEnvelope(
    string EventId,
    WorkerEventOutcome Outcome,
    JsonElement Payload,
    int ProtocolVersion);

public enum WorkerEventOutcome
{
    Accepted, Deferred, Declined, Completed, Refused, NeedsReconciliation, Cancelled, Failed
}

public sealed record BinaryTransferOffer(
    string TransferId,
    string Direction,
    string PipeName,
    long MaximumBytes,
    DateTimeOffset ExpiresAt);

public sealed record BinaryTransferCompletion(
    string TransferId,
    long ByteCount,
    string Sha256);

public static class WorkerHandshake
{
    public static WorkerHandshakeResult Negotiate(
        WorkerHandshakeRequest client,
        WorkerHandshakeOffer worker);
}
```

Reject no-overlap ranges and absent required capabilities. Compare capability names with ordinal byte-stable
semantics, reject duplicates, and bound every identifier and list before allocation. JSON readers must ignore
unknown object properties but reject unknown required commands and enum values.

- [ ] **Step 4: Run green and commit**

Run `./test.ps1`; expect all runnable tests to pass. Then:

```powershell
git add src/SIL.Motif.Contract tests/SIL.Motif.Tests/Contract
git commit -m "feat: define worker wire negotiation"
```

### Task 2: Add the cross-runtime named-pipe client

Both the CLI and FieldWorks need the same lightweight client without taking a dependency on project internals.

**Files:**
- Create: `src/SIL.Motif.Client/SIL.Motif.Client.csproj`
- Create: `src/SIL.Motif.Client/Worker/WorkerClient.cs`
- Create: `src/SIL.Motif.Client/Worker/WorkerConnection.cs`
- Create: `src/SIL.Motif.Client/Worker/BinaryTransferClient.cs`
- Modify: `Motif.sln`
- Test: `tests/SIL.Motif.Tests/Worker/WorkerClientTests.cs`

- [ ] **Step 1: Write a loopback server test**

The test starts a `NamedPipeServerStream`, completes the handshake, receives one envelope, and returns one
envelope while interleaving a worker event. Assert the dedicated event loop receives it without blocking the
request and returns exactly one correlated event result; duplicate, unknown, and mismatched event IDs refuse.
Assert cancellation closes the connection and a mismatched response id is rejected. Add binary upload tests
for exact length/digest, expiry, excess bytes, and second-use refusal.

- [ ] **Step 2: Run red**

Run `./test.ps1`. Expected: the new client project and types are absent.

- [ ] **Step 3: Implement a `netstandard2.0` client with bounded frames**

The project references only `SIL.Motif.Contract` and `System.Text.Json 8.0.5`. Implement:

```csharp
public sealed class WorkerClient
{
    public Task<WorkerConnection> ConnectAsync(
        string pipeName,
        WorkerHandshakeRequest handshake,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class WorkerConnection : IDisposable
{
    public WorkerHandshakeResult Negotiated { get; }
    public event EventHandler<WorkerEventEnvelope> EventReceived;
    public Task<WorkerEnvelope> SendAsync(
        WorkerEnvelope request,
        CancellationToken cancellationToken);
    public Task UploadAsync(
        BinaryTransferOffer offer,
        Stream source,
        CancellationToken cancellationToken);
    public Task CompleteEventAsync(
        WorkerEventResultEnvelope result,
        CancellationToken cancellationToken);
}
```

Use a four-byte little-endian length prefix, a fixed maximum control-frame size, one continuous read loop,
and exact request/response correlation. Dispatch unsolicited events separately from responses. Stream binary
uploads in bounded buffers, compute byte count and SHA-256 during the single pass, and send completion only
after the stream closes. Do not reference Host, Runner, LibLCM, or SQLite.

- [ ] **Step 4: Run green and commit**

Run `./test.ps1`, then commit:

```powershell
git add Motif.sln src/SIL.Motif.Client tests/SIL.Motif.Tests/Worker
git commit -m "feat: add cross-runtime worker client"
```

### Task 3: Add the single-instance worker shell

Starting Motif twice should connect to one owner, not create two competing owners of the same work.

**Files:**
- Create: `src/SIL.Motif.Worker/SIL.Motif.Worker.csproj`
- Create: `src/SIL.Motif.Worker/Program.cs`
- Create: `src/SIL.Motif.Worker/WorkerServer.cs`
- Create: `src/SIL.Motif.Worker/WorkerEventSink.cs`
- Create: `src/SIL.Motif.Worker/BinaryTransferServer.cs`
- Create: `src/SIL.Motif.Worker/WorkerLifetime.cs`
- Modify: `Motif.sln`
- Test: `tests/SIL.Motif.Tests/Worker/WorkerServerTests.cs`

- [ ] **Step 1: Write process-level tests**

Start two worker processes for the same test user namespace. Assert the second reports the existing endpoint
instead of becoming an owner. Assert an idle worker exits, while a registered queued/waiting work lease keeps
it alive. Exercise worker-to-client events and a one-use binary server that rejects wrong digest, excessive
length, expiry, and reconnection while deleting every unpublished temporary file. Inspect control and binary
pipe ACLs and prove only the owning user SID and required system account connect; a different local identity
and a remote-pipe attempt refuse before handshake.

- [ ] **Step 2: Run red**

Run `./test.ps1`. Expected: worker executable and lifetime types do not exist.

- [ ] **Step 3: Implement the shell**

Use one user-scoped mutex and pipe name derived from the Windows user SID. Construct every pipe with explicit
`PipeSecurity`; a predictable SID-derived name is not an access check. Implement:

```csharp
public interface IWorkerWorkTracker
{
    bool HasQueuedRunningOrWaitingWork { get; }
}

public sealed class WorkerLifetime
{
    public Task RunUntilIdleAsync(
        TimeSpan idleTimeout,
        IWorkerWorkTracker work,
        CancellationToken shutdown);
}
```

The server validates handshake before dispatch, exposes an explicit handler for every closed command DTO,
never opens a database during handshake, and treats inactive connections as idle. This task supplies the
transport sink; the composition bridge replaces its provisional single-host registration with project-keyed
routing before any project handler uses it. The resulting sink sends capture, Apply, reconciliation, and
cancellation requests only to the live host registered for that project, awaits exactly one correlated event
result, and resolves the initiating command or job from that result. `BinaryTransferServer`
creates unpredictable one-use pipe offers, owns their temporary files, independently hashes the stream, and
publishes nothing before a correlated completion matches its length and SHA-256. Use an injected monotonic
clock in tests.

- [ ] **Step 4: Run green and commit**

Run `./test.ps1`, then commit:

```powershell
git add Motif.sln src/SIL.Motif.Worker tests/SIL.Motif.Tests/Worker
git commit -m "feat: add single-instance motif worker"
```

### Task 4: Select the newest compatible installed worker

People should normally receive the newest worker their installed clients can understand, with a clear refusal
when no safe combination exists.

**Files:**
- Create: `src/SIL.Motif.Launcher/SIL.Motif.Launcher.csproj`
- Create: `src/SIL.Motif.Launcher/InstalledWorkerCatalog.cs`
- Create: `src/SIL.Motif.Launcher/WorkerSelector.cs`
- Create: `src/SIL.Motif.Launcher/Program.cs`
- Modify: `Motif.sln`
- Test: `tests/SIL.Motif.Tests/Worker/WorkerSelectorTests.cs`

- [ ] **Step 1: Write selection tests**

Pin newest-compatible selection, capabilities, missing overlap, immutable version directories, and the rule
that product version alone never rejects an otherwise compatible worker.

- [ ] **Step 2: Run red**

Run `./test.ps1`. Expected: launcher types do not exist.

- [ ] **Step 3: Implement selection and actionable refusal**

Implement:

```csharp
public sealed record InstalledWorker(
    Version ProductVersion,
    string ExecutablePath,
    ProtocolRange Protocols,
    IReadOnlyList<string> Capabilities);

public static class WorkerSelector
{
    public static InstalledWorker SelectNewestCompatible(
        IEnumerable<InstalledWorker> installed,
        WorkerHandshakeRequest client);
}
```

Sort SemVer descending only after protocol/capability filtering. The launcher connects to an existing worker
first, starts the selected executable hidden if none responds, and fails with install/update guidance if no
candidate overlaps. The composition bridge adds one compiled metadata source and requires the immutable
installed manifest to agree with the running worker's product version, protocol interval, and capabilities;
the manifest is never an independent claim about executable behavior.

- [ ] **Step 4: Verify and commit**

Run `./test.ps1`, confirm `rg -n "net8.0" src tests` returns no target, then commit:

```powershell
git add Motif.sln src/SIL.Motif.Launcher tests/SIL.Motif.Tests/Worker
git commit -m "feat: negotiate installed worker versions"
```
