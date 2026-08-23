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

Motif must distinguish saved project states and same-named project copies without guessing from a filename.

**Files:**
- Create: `src/SIL.Motif.Contract/Baselines/BaselineToken.cs`
- Create: `src/SIL.Motif.Contract/Projects/LiveProjectObservation.cs`
- Use: `src/SIL.Motif.Contract/Projects/ProjectLocator.cs`
- Create: `src/SIL.Motif.Worker/Projects/ProjectWorkspaceKey.cs`
- Test: `tests/SIL.Motif.Tests/Worker/ProjectWorkspaceKeyTests.cs`
- Test: `tests/SIL.Motif.Tests/Contract/BaselineTokenTests.cs`

- [ ] **Step 1: Write canonical identity tests**

Pin normalized case-insensitive Windows paths, same identity at different paths, same path with different
identity, Unicode path ordering, token JSON, captured edit generation, digest validation, and the rule that
timestamp, host session, and edit generation are freshness evidence but not semantic identity.

- [ ] **Step 2: Run red**

Run `./test.ps1`. Expected: Baseline observation, token, and workspace-key types are absent.

- [ ] **Step 3: Implement immutable types**

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

- [ ] **Step 4: Run green and commit**

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
- Create: `src/SIL.Motif.LiveHost/Baselines/BaselineBundleWriter.cs`
- Create: `src/SIL.Motif.LiveHost/Baselines/BaselineSemanticDigest.cs`
- Create: `src/SIL.Motif.Worker/Baselines/BaselineBundleReceiver.cs`
- Create: `src/SIL.Motif.Worker/Baselines/BaselineRepository.cs`
- Modify: `Motif.sln`
- Test: `tests/SIL.Motif.Tests/Host/BaselineBundleTests.cs`

- [ ] **Step 1: Write real-project equivalence tests**

Create a seeded `NewLangProjFixture` with custom writing-system collation and valid characters plus a linked
media sentinel larger than the `.fwdata`. Stream the bundle through a throttled stream. Assert it contains the
saved `.fwdata`, writing-system store, and only proven support files; excludes `.motif.db`, `LinkedFiles`,
backups, and unrelated files; opens through `LoadScratchCache`; and is equivalent on objects and writing
systems. Assert peak buffering stays below a fixed small buffer, not project size.

- [ ] **Step 2: Run red**

Run `./test.ps1`. Expected: Baseline bundle types do not exist.

- [ ] **Step 3: Implement streaming capture and atomic publication**

```csharp
public sealed class BaselineBundleWriter
{
    public Task<BaselineToken> WriteAsync(
        LcmCache savedCache,
        Stream destination,
        CancellationToken cancellationToken);
}

public sealed class BaselineBundleReceiver
{
    public Task<BaselinePublication> PublishVerifiedAsync(
        ProjectLocator project,
        string verifiedTemporaryBundle,
        BaselineToken declaredToken,
        CancellationToken cancellationToken);
}

public sealed record BaselinePublication(
    string RootDirectory,
    string FwDataPath,
    BaselineToken Token);
```

`SIL.Motif.LiveHost` targets `netstandard2.0`, references Contract, Model, Runner, and LibLCM, and contains
LibLCM-aware code shared by the `net10.0` CLI host and `net48` FieldWorks adapter. It must not reference Host,
Worker, Client, or SQLite. Require the host to call synchronous `FwDataProjectLoader.Save` before writing.
The writer computes the token and digest during its single stream pass. The client closes the stream and sends
the resulting binary completion plus declared token. The receiver treats the verified temporary bundle as a
transport archive, never as a project. It verifies the declared token, bounded safe entry paths, exactly one
`.fwdata`, required writing-system content, and the exclusion of `.motif.db`, linked media, backups,
repositories, and unrelated files. It extracts once into a worker-owned temporary publication directory,
validates the extracted layout, then atomically renames that directory to its immutable published location.
The repository records the published root and contained `.fwdata` path with the token, then deletes the
temporary transport archive. Any failure deletes only unpublished temporary state and leaves the previous
Baseline available. Do not recursively copy the project folder and do not include linked media.
`BaselineSemanticDigest` walks the existing model-coverage snapshot projection in canonical ID order and
hashes canonical bytes; it is independent of container metadata, while `BundleDigest` hashes the exact
transferred archive.

- [ ] **Step 4: Run green and commit**

Run `./test.ps1`, then:

```powershell
git add Motif.sln src/SIL.Motif.LiveHost src/SIL.Motif.Worker tests/SIL.Motif.Tests/Host
git commit -m "feat: stream minimal file-backed baselines"
```

### Task 3: Integrate live-host leases with the per-project lane

FieldWorks and background work can coexist only when ownership changes and refresh ordering are explicit.

**Files:**
- Use: `src/SIL.Motif.Worker/Projects/ProjectHostRegistry.cs`
- Create: `src/SIL.Motif.Worker/Projects/ProjectFreshnessTracker.cs`
- Create: `src/SIL.Motif.Worker/Scheduling/ProjectLane.cs`
- Create: `src/SIL.Motif.Worker/Scheduling/ProjectWorkItem.cs`
- Create: `src/SIL.Motif.Worker/Baselines/BaselineCommandHandler.cs`
- Test: `tests/SIL.Motif.Tests/Worker/ProjectLaneTests.cs`

- [ ] **Step 1: Write deterministic concurrency tests**

With controlled task completions, assert one lane per project, concurrent lanes across projects, refresh as a
barrier, old/new Baseline assignment, waiting-for-host transfer after lease loss, accept/defer/decline refresh
responses, an already-started capture finishing, an Apply waiter outranking refresh work that has not started,
and Apply proceeding concurrently with a Dry Run already isolated on a private Baseline. Pin known-old from a
later edit generation or changed saved semantic digest, and “currentness not checked” when neither live
observation nor a lock-safe saved-project probe is available. For decline, cancellation, save, transfer, and
verification failure, assert later jobs remain `waiting-for-baseline`; only successful replacement releases
the barrier, unless the caller cancels and resubmits explicitly against the old token.

- [ ] **Step 2: Run red**

Run `./test.ps1`. Expected: scheduler types are absent.

- [ ] **Step 3: Implement an explicit lane state machine**

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

Queue order, not task timing, chooses the Baseline. Live-host registration is a renewable connection lease;
registration includes a unique host-session epoch and saved semantic digest, change notifications update the
generation and dirty state, and disconnect releases authority. Generation comparisons are valid only inside
one epoch; a new epoch compares semantic digest. A queued refresh records an accept, defer, or decline response
durably. Only an accepted or deferred request remains executable by the CLI Host after FieldWorks releases
authority and the lock is available; decline terminates that request. The Apply gate
conflicts with refresh/capture but not a Dry Run that has already opened private Baseline state; per-project
Dry Runs remain serial with each other. `BaselineCommandHandler` uses the admitted project runtime and keyed
host route from the composition bridge, issues the binary offer, and passes the server-verified temporary
archive to `BaselineBundleReceiver`. It never opens another database or registers a global host.

Startup recovery already precedes admission through the project runtime. Workspace eviction and Baseline
cleanup must additionally hold the same project-lane lease/reference gate, or use a compare-and-delete
generation, so a new pin or lease cannot appear between checking and deletion. Preserve the checked
bottom-up, nonrecursive deletion and rename/reparse substitution tests.

- [ ] **Step 4: Run green and commit**

Run `./test.ps1`, then:

```powershell
git add src/SIL.Motif.Worker tests/SIL.Motif.Tests/Worker
git commit -m "feat: schedule project baseline work"
```

### Task 4: Run twenty isolated Dry Runs from one Baseline

The performance promise is one saved state reused many times, with every evaluation independent and honest
about when that state was captured.

**Files:**
- Create: `src/SIL.Motif.LiveHost/Baselines/BaselineScratchFactory.cs`
- Create: `src/SIL.Motif.Worker/Jobs/DryRunJobHandler.cs`
- Modify: `src/SIL.Motif.Runner/DryRun/DryRunScratch.cs`
- Test: `tests/SIL.Motif.Tests/Worker/BaselineDryRunIntegrationTests.cs`

- [ ] **Step 1: Write the twenty-run acceptance test**

Capture once, execute the same Proposal twenty times, and assert identical effect and footprint digests,
unchanged Baseline bytes, no saved scratch mutation, one active scratch at a time, no linked-media bytes in
workspace, known-old warning after a higher live edit generation, changed saved semantic digest after restart,
and “currentness not checked” when observation is unavailable. Add cancellation injection before publication
and prove no partial Dry Run record exists.

- [ ] **Step 2: Run red**

Run `./test.ps1`. Expected: the job handler and scratch factory are absent.

- [ ] **Step 3: Implement file-backed single-use scratches**

```csharp
public sealed class BaselineScratchFactory
{
    public DryRunScratch OpenSingleUse(string publishedFwDataPath);
}
```

Open the `.fwdata` recorded inside the immutable published Baseline directory directly through
`LoadScratchCache`; do not copy or extract the project for each Dry Run. The scratch may read the sibling
writing-system/support content in that directory but may not mutate or save it. Run the existing validated
`PrerequisiteExecutionPlan` and `ProposalDryRunner`, dispose without saving, verify the published directory
remains unchanged, and publish the Dry Run record only after complete read-back.
Return status containing `BaselineToken` and `CapturedUtc`; use `ProjectFreshnessTracker` to report current,
known-old, or not-checked status, never to silently refresh or cancel.

- [ ] **Step 4: Run green and commit**

Run `./test.ps1`, then:

```powershell
git add src/SIL.Motif.LiveHost src/SIL.Motif.Runner src/SIL.Motif.Worker tests/SIL.Motif.Tests/Worker
git commit -m "feat: reuse baselines for isolated dry runs"
```
