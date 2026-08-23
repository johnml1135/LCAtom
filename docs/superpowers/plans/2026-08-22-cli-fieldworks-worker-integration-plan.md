# CLI and FieldWorks Worker Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Make the CLI a complete worker client and ship the `netstandard2.0` package FieldWorks needs to use
the same workflow while retaining its live `LcmCache`.

**Architecture:** Shared projections remain host-neutral. CLI commands send worker requests and render their
responses. The FieldWorks package registers live authority, captures Baselines at a host-provided safe edit
boundary, invokes Runner Apply in-process, saves, and reconciles; it contains no UI and no SQLite.

**Tech Stack:** `net10.0` CLI, `netstandard2.0` client/Runner integration package, named pipes,
`System.Text.Json`, LibLCM, xUnit process tests.

---

### Task 1: Convert the CLI entry point into a worker client

Short-lived CLI commands should all see the same durable workflow without opening project storage themselves.

**Files:**
- Create: `src/SIL.Motif.Cli/Worker/WorkerCommandClient.cs`
- Modify: `src/SIL.Motif.Cli/SIL.Motif.Cli.csproj`
- Modify: `src/SIL.Motif.Cli/Program.cs`
- Modify: `src/SIL.Motif.Cli/Commands.cs`
- Modify: `src/SIL.Motif.Cli/CorpusCommands.cs`
- Modify: `src/SIL.Motif.Worker/Projects/ProjectRuntime.cs`
- Modify: `src/SIL.Motif.Worker/Store/FileProposalStoreMigration.cs`
- Modify: `src/SIL.Motif.Worker/Store/LegacyBulkStoreMigration.cs`
- Test: `tests/SIL.Motif.Tests/Cli/WorkerCommandDispatchTests.cs`
- Test: `tests/SIL.Motif.Tests/Store/FileProposalStoreMigrationTests.cs`
- Test: `tests/SIL.Motif.Tests/Store/LegacyBulkStoreMigrationTests.cs`

- [ ] **Step 1: Write argv process tests**

Run the real `motif` executable against a fake worker. Assert launcher startup, handshake, structured refusal,
JSON/text parity, authoring immediacy, and that no CLI command opens SQLite directly after migration. Separate
pure authoring commands from composers that require LibLCM resolution: the latter must name an available
Baseline and display its “as of” token or wait for a live-host request; they cannot read around the lock.
Seed both legacy stores, keep a worker status read active, and assert it may finish against the old state while
cutover waits for its lease. Assert every project operation submitted after the exclusive cutover request
waits and sees only the committed post-import state.

- [ ] **Step 2: Run red**

Run `./test.ps1`. Expected: worker dispatch does not exist and current commands still own files/cache.

- [ ] **Step 3: Implement request-only command handlers**

```csharp
public sealed class WorkerCommandClient
{
    public Task<TResponse> ExecuteAsync<TRequest, TResponse>(
        string command,
        TRequest request,
        CancellationToken cancellationToken);
}
```

Keep command parsing and rendering in CLI; move state changes and queries behind explicit worker commands.
Every project command sends `ProjectLocator`. Unknown/missing capabilities produce actionable refusal before
sending the command. Replace the CLI's Host and Runner project references with Client, Contract, and Projection
references once the final direct command is migrated; live closed-project operations run inside Worker through
Host, never inside the CLI process. The composition bridge supplies the first real `job.status` handler and
the project-runtime dispatch boundary. Baseline/Dry Run, PanGloss, and Apply/reconciliation handlers are
created in their owning plans; this task adds the remaining Proposal/job handlers required by CLI cutover.
The cutover must reject any command lacking a registered closed-schema handler.

Before routing the first project command, send the exact user-selected `--store` location through a dedicated
cutover request. The project runtime takes its writer-preferring exclusive operation lease: it blocks every new
repository user and waits for commands, queued-job runners, schedulers, host callbacks, and other current
operations to finish. Refactor both importers to accept one caller-owned destination transaction and defer
source archival. Import both sources and write the cutover ledger row in that one transaction; rollback leaves
both legacy sources and the destination's pre-cutover state intact. After commit, archive both sources. An
archive failure is cleanup debt, not a failed database cutover: the ledger makes retry idempotent and the CLI
routes to the worker without writing either old source again. Release exclusive admission only after commit
and the first archival attempt, so no post-cutover operation can observe partial destination state.

- [ ] **Step 4: Run green and commit**

Run `./test.ps1`, then:

```powershell
git add src/SIL.Motif.Cli src/SIL.Motif.Worker/Projects/ProjectRuntime.cs `
  src/SIL.Motif.Worker/Store/FileProposalStoreMigration.cs `
  src/SIL.Motif.Worker/Store/LegacyBulkStoreMigration.cs tests/SIL.Motif.Tests/Cli `
  tests/SIL.Motif.Tests/Store/FileProposalStoreMigrationTests.cs `
  tests/SIL.Motif.Tests/Store/LegacyBulkStoreMigrationTests.cs
git commit -m "feat: route cli through motif worker"
```

### Task 2: Add asynchronous job surfaces and Baseline status

People and automation need to start slow work, leave, return, cancel safely, and understand which saved
project state produced a result.

**Files:**
- Modify: `src/SIL.Motif.Cli/Program.cs`
- Create: `src/SIL.Motif.Projection/JobProjection.cs`
- Create: `src/SIL.Motif.Projection/BaselineStatusProjection.cs`
- Modify: `src/SIL.Motif.Projection/Rendering/CommandTextRenderer.cs`
- Test: `tests/SIL.Motif.Tests/Cli/JobCommandDispatchTests.cs`

- [ ] **Step 1: Write exact CLI contract tests**

Pin `job status <id>`, `job wait <id>`, `job cancel <id>`, `refresh --project`, `dry-run` default Assessment,
`dry-run --no-assessment`, and `--wait`. Assert output says “run against the project as of X”, prominently
warns on known older Baseline, says “currentness not checked” when it cannot compare, and distinguishes
waiting-for-baseline from waiting-for-project-host. Assert `--no-assessment` warns that Apply will require
`--force` without later evidence. Cancellation after a published Dry Run retains that record, reports the
pipeline job cancelled, and records cancelled Assessment disposition.

- [ ] **Step 2: Run red**

Run `./test.ps1`. Expected: verbs and projections are absent.

- [ ] **Step 3: Implement async-default rendering**

Long commands return job id and current status unless `--wait` is present. JSON and text consume the same
projection:

```csharp
public sealed record JobProjection(
    string JobId,
    string Kind,
    string Status,
    string Project,
    string? BaselineCapturedUtc,
    string? Warning,
    string? Failure);
```

Cancellation reports whether it was accepted at the current boundary; it never implies rollback of Apply.

- [ ] **Step 4: Run green and commit**

Run `./test.ps1`, then:

```powershell
git add src/SIL.Motif.Cli src/SIL.Motif.Projection tests/SIL.Motif.Tests/Cli
git commit -m "feat: expose worker jobs in cli"
```

### Task 3: Make CLI Apply immediate and explicit

Applying is a connected decision with an immediate result, not background work that might run later.

**Files:**
- Modify: `src/SIL.Motif.Cli/Program.cs`
- Create: `src/SIL.Motif.Cli/Apply/ApplyCommand.cs`
- Modify: `src/SIL.Motif.Projection/ApplyProjection.cs`
- Test: `tests/SIL.Motif.Tests/Cli/ApplyCommandDispatchTests.cs`

- [ ] **Step 1: Write refusal and force tests**

Assert no approved Decision fails, poor completed Assessment applies normally, missing/pending/cancelled/failed
Assessment requires exactly `--force`, force does not bypass any other refusal, live-host absence/busy fails
within five seconds, and Apply never returns a job id or queues after disconnect.

- [ ] **Step 2: Run red**

Run `./test.ps1`. Expected: current Apply bypasses the new worker policy.

- [ ] **Step 3: Implement connected authorization flow**

```csharp
public sealed record ApplyCommandRequest(
    ProjectLocator Project,
    CanonicalId ProposalId,
    bool ForceUnavailableAssessment,
    string Actor);
```

CLI requests the opaque authorization and presents it back on the connected Apply command. The worker consumes
it once, sends verified claims to the active live host, and correlates the host's Receipt/refusal result to the
waiting CLI. CLI never handles claims or creates approval implicitly. Delete the old direct
`ProposalApplier.Apply` overload after all production call sites use `ApplyRequest`.

- [ ] **Step 4: Run green and commit**

Run `./test.ps1`, then:

```powershell
git add src/SIL.Motif.Cli src/SIL.Motif.Runner src/SIL.Motif.Projection tests/SIL.Motif.Tests/Cli
git commit -m "feat: apply synchronously through worker policy"
```

### Task 4: Create the FieldWorks integration package

FieldWorks needs a small package that uses its open project directly while delegating durable coordination to
the worker.

**Files:**
- Create: `src/SIL.Motif.FieldWorks/SIL.Motif.FieldWorks.csproj`
- Create: `src/SIL.Motif.FieldWorks/MotifProjectSession.cs`
- Create: `src/SIL.Motif.FieldWorks/IFieldWorksEditBoundary.cs`
- Create: `src/SIL.Motif.FieldWorks/IFieldWorksProjectState.cs`
- Create: `src/SIL.Motif.FieldWorks/IFieldWorksWorkerEventHandler.cs`
- Create: `src/SIL.Motif.FieldWorks/FieldWorksBaselineHost.cs`
- Create: `src/SIL.Motif.FieldWorks/FieldWorksApplyHost.cs`
- Create: `src/SIL.Motif.Worker/Projects/ProjectRelocationCoordinator.cs`
- Create: `src/SIL.Motif.Worker/Projects/ProjectLocatorJournal.cs`
- Modify: `Motif.sln`
- Test: `tests/SIL.Motif.Tests/FieldWorks/MotifProjectSessionTests.cs`

- [ ] **Step 1: Write a fake-host integration test**

Use a real seeded `LcmCache` plus a fake UI/edit boundary. Assert registration and lease release, no cache over
pipe, a continuously consumed worker-event channel, edit-generation/dirty notifications, save-before-stream,
bounded one-use transfer, refresh accept/defer/decline, Apply on the supplied cache in one UOW, save after
success, no save after refusal, Receipt report, and applied-log reconciliation on reconnect. Pin a managed
move handshake that closes the database and carries the `.fwdata`/sibling database pair, a managed duplicate
that excludes database and workspace, token expiry/rollback, and an unmanaged copied database moving to
worker-owned `unclaimed/` quarantine before the new locator registers fresh. Pin configurable 30-day
quarantine eviction and an explicit status explanation. Crash after moving but before completion and worker
restart with a closed database must recover from the per-user locator journal by checking the exact old/new
paths. Exactly one expected pair completes there; both or neither produces `relocation-blocked` and opens
neither. This is project-registration status, not the Proposal Conflict condition.

- [ ] **Step 2: Run red**

Run `./test.ps1`. Expected: FieldWorks integration project is absent.

- [ ] **Step 3: Implement the UI-free `netstandard2.0` package**

The project targets `netstandard2.0` and references Client, Contract, Model, Runner, and LiveHost; it does not
reference Host, Worker, or SQLite.

```csharp
public interface IFieldWorksEditBoundary
{
    Task<T> RunAsync<T>(Func<LcmCache, Task<T>> operation);
    Task SaveAsync(LcmCache cache);
}

public interface IFieldWorksProjectState
{
    event EventHandler ProjectChanged;
    string HostSessionId { get; }
    long EditGeneration { get; }
    bool HasUnsavedChanges { get; }
    string SavedSemanticDigest { get; }
}

public interface IFieldWorksWorkerEventHandler
{
    Task<BaselineRefreshResult> HandleBaselineRefreshAsync(BaselineRefreshRequest request);
    Task<LiveApplyResult> HandleApplyAsync(LiveApplyRequest request);
    Task<ReconcileResult> HandleReconcileAsync(ReconcileRequest request);
    Task<CancellationResult> HandleCancellationAsync(CancelLiveWorkRequest request);
}

public sealed class MotifProjectSession : IDisposable
{
    public Task RegisterAsync(CancellationToken cancellationToken);
    public Task<BaselineToken> CaptureBaselineAsync(CancellationToken cancellationToken);
    public Task ReconcileAsync(CancellationToken cancellationToken);
    public Task<ProjectRelocationGrant> PrepareMoveAsync(
        ProjectLocator destination,
        CancellationToken cancellationToken);
    public Task CompleteMoveAsync(
        ProjectRelocationGrant grant,
        CancellationToken cancellationToken);
}
```

`MotifProjectSession` continuously dispatches worker events to the supplied handler, returns exactly one
correlated result envelope, and reports project-state changes to the worker. A `LiveApplyRequest` contains
worker-verified claims, never the opaque authorization. FieldWorks supplies the boundary implementation,
refresh presentation, and filesystem
move/duplicate operation. `ProjectRelocationCoordinator` closes the database and issues the one-use grant;
`ProjectLocatorJournal` persists the exact old locator, new locator, database identity, token, and expiry
outside the database so completion or restart can re-register the only verified pair. On an ungranted locator mismatch, it moves the copied database to
the exact worker-owned `unclaimed/` path before creating a fresh sibling and records the explanation for
status. The package may start the launcher hidden, but never opens the project or database itself.

- [ ] **Step 4: Run green and commit**

Run `./test.ps1`, verify the project targets only `netstandard2.0`, then:

```powershell
git add Motif.sln src/SIL.Motif.FieldWorks tests/SIL.Motif.Tests/FieldWorks
git commit -m "feat: add fieldworks motif integration package"
```

### Task 5: Pin authority, media, reconciliation, and mixed-version end to end

The final scenarios prove that the separate pieces behave as one product under the conflicts users will
actually create.

**Files:**
- Create: `manifest/media-boundary.tsv`
- Create: `src/SIL.Motif.Generator/MediaBoundaryManifest.cs`
- Modify: `src/SIL.Motif.Generator/Program.cs`
- Test: `tests/SIL.Motif.Tests/Coverage/MediaBoundaryCoverageTests.cs`
- Create: `tests/SIL.Motif.Tests/EndToEnd/WorkerArchitectureTests.cs`
- Create: `tests/SIL.Motif.Tests/Compatibility/FrozenProtocolV1ClientTests.cs`
- Create: `tests/SIL.Motif.Net48Smoke/SIL.Motif.Net48Smoke.csproj`
- Create: `tests/SIL.Motif.Net48Smoke/Program.cs`
- Modify: `README.md`
- Modify: `docs/plan-motif.md`

- [ ] **Step 1: Add the media-boundary coverage gate**

Classify every operation family as `none` or `model-delete-only`. Fail generation and CI for an unclassified
family, any external-byte authoring/storage behavior, or either classification without its required
real-project conformance fixture. `none` must prove the family's exercised LibLCM members neither own nor
reference external media. `model-delete-only` must prove FieldWorks-equivalent model cascade while linked
sentinel bytes remain untouched. Join `media-boundary.tsv` to the authoritative operation-family and LibLCM
coverage inventories by canonical family key in the existing generator; reject missing, duplicate, orphaned,
or coverage-inconsistent rows. A future family cannot enter generated output without the row and fixture.

- [ ] **Step 2: Add end-to-end scenarios**

Cover two compatible clients on one worker, same-identity clones in different folders, FieldWorks-open CLI
authoring plus live-operation refusal, refresh handoff after FieldWorks closes, twenty old-Baseline Dry Runs
while live Apply succeeds, interrupted Apply reconciliation, loud Conflict ordering, terminal archive, and
lexical-entry deletion that removes model-owned media references while leaving linked sentinel bytes untouched.
Use a frozen protocol-generation-one request/response fixture or frozen client assembly for the older side of
the mixed-version test; two clients compiled from current source do not prove compatibility. Build and run a
minimal `net48` consumer that loads the FieldWorks integration package, constructs its LibLCM-free client
surface, and completes a handshake against the current worker. It need not open FieldWorks or implement UI.

- [ ] **Step 3: Run red against any missing integration**

Run `./test.ps1`. Expected: each scenario either passes through already-built seams or exposes one concrete
missing wire-up; fix only the missing integration in its owning focused file and rerun after each change.

- [ ] **Step 4: Remove superseded production paths**

Delete direct CLI database ownership, direct CLI live-cache Dry Run/Apply dispatch, production `CliSession`
usage, and legacy Proposal file writes only after migration tests prove equivalence. Keep measurement helpers
whose names clearly identify them as non-production.

- [ ] **Step 5: Update user documentation to built status**

Change only claims proven by the end-to-end tests. Preserve the architecture links, two-deliverable framing,
media exclusion, and current runtime matrix.

- [ ] **Step 6: Run the final gate and commit**

Run `./test.ps1` and `git diff --check`. Expected: zero comment violations, zero build errors, every runnable
test passing, and only parser-dependent skips when PanGloss is unavailable. Then:

```powershell
git add src tests Motif.sln README.md docs/plan-motif.md
git commit -m "feat: complete worker-backed motif workflow"
```
