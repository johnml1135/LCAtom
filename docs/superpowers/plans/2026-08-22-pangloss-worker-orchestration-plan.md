# PanGloss Worker Orchestration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Attach a fresh optional PanGloss Assessment to each Dry Run while bounding total machine resource
use and retaining no parser engine.

**Architecture:** PanGloss exports private inputs from the candidate scratch before the scratch releases the
project lane. Each per-user worker has one FIFO across its projects; machine-wide leases provide two total
slots without defining cross-user order. A Windows Job Object caps each full process tree at 25 percent CPU;
only result and bounded log become durable.

**Tech Stack:** existing PanGloss process seam, Windows Job Objects, named mutex/semaphore lease file, SQLite,
xUnit and `RealParserFactAttribute` integration tests.

---

### Task 1: Separate candidate export from Assessment execution

PanGloss should receive everything it needs once, then finish without keeping the FieldWorks model open.

**Files:**
- Create: `src/SIL.Motif.Host/Parser/IPanGlossCandidateExporter.cs`
- Create: `src/SIL.Motif.Host/Parser/PanGlossCandidateExporter.cs`
- Create: `src/SIL.Motif.Host/Parser/PanGlossAssessmentProcess.cs`
- Modify: `src/SIL.Motif.Host/Parser/PanGlossParser.cs`
- Test: `tests/SIL.Motif.Tests/Parser/PanGlossCandidateExportTests.cs`

- [x] **Step 1: Write contract tests with a fake PanGloss executable**

Assert export consumes the live candidate `LcmCache`, execution consumes only the exported directory, each
attempt exports anew, cancellation kills the process, and no engine path/id/cache key appears in any public or
persisted type.

- [x] **Step 2: Run red**

Run `./test.ps1`. Expected: exporter and assessment process types are absent.

- [x] **Step 3: Implement the two-stage seam**

```csharp
public interface IPanGlossCandidateExporter
{
    Task ExportAsync(
        LcmCache candidate,
        string emptyDestination,
        CancellationToken cancellationToken);
}

public sealed class PanGlossAssessmentProcess
{
    public Task<AssessReport> RunAsync(
        string exportedCandidate,
        CancellationToken cancellationToken);
}
```

PanGloss owns filenames and content. Motif validates only containment, completion, and bounded output. Keep the
existing direct file seam as compatibility scaffolding until the worker path is green.

- [x] **Step 4: Run green and commit**

Run `./test.ps1`, then:

```powershell
git add src/SIL.Motif.Host tests/SIL.Motif.Tests/Parser
git commit -m "feat: separate pangloss export and assessment"
```

### Task 2: Enforce the machine-wide two-by-25-percent envelope

Parser work must stay responsive and predictable even when several projects or Windows sessions request it.

**Files:**
- Create: `src/SIL.Motif.Worker/PanGloss/MachinePanGlossQueue.cs`
- Create: `src/SIL.Motif.Worker/PanGloss/WindowsCpuJob.cs`
- Create: `src/SIL.Motif.Worker/PanGloss/MachineSlotLease.cs`
- Test: `tests/SIL.Motif.Tests/Worker/MachinePanGlossQueueTests.cs`
- Test: `tests/SIL.Motif.Tests/Worker/WindowsCpuJobTests.cs`

- [x] **Step 1: Write controlled-process tests**

Queue jobs from three projects in one user namespace and prove FIFO admission. Add two simulated user
namespaces and assert only capacity, not cross-user order: never more than two machine leases, each process
tree assigned before resume, 25-percent hard cap even with one job, combined cap of 50 percent, child-process
containment, cancellation, and lease recovery after owner death.

- [x] **Step 2: Run red**

Run `./test.ps1`. Expected: machine scheduling types are absent.

- [x] **Step 3: Implement machine leases and Windows Job Object limits**

```csharp
public sealed class MachinePanGlossQueue
{
    public Task<T> RunAsync<T>(
        string jobId,
        Func<WindowsCpuJob, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken);
}

public sealed class WindowsCpuJob : IDisposable
{
    public void AssignProcess(Process process);
    public void Terminate();
}
```

Use the Windows Job Object CPU hard-cap flag at 2500 basis points and kill-on-close. Coordinate the two slots
through a machine-global primitive plus ownership metadata so independent per-user workers cannot exceed the
PC total. Do not raise a lone job above 25 percent.

- [x] **Step 4: Run green and commit**

Run `./test.ps1`, then:

```powershell
git add src/SIL.Motif.Worker tests/SIL.Motif.Tests/Worker
git commit -m "feat: bound pangloss machine resources"
```

### Task 3: Join Dry Run, export, and Assessment job stages

A person should see one coherent job while Motif still preserves the distinction between project effects and
parser evidence.

**Files:**
- Create: `src/SIL.Motif.Worker/Jobs/DryRunAssessmentPipeline.cs`
- Create: `src/SIL.Motif.Worker/Jobs/DryRunAssessmentCommandHandler.cs`
- Modify: `src/SIL.Motif.Worker/Jobs/DryRunJobHandler.cs`
- Modify: `src/SIL.Motif.Worker/Store/MotifSchema.cs`
- Test: `tests/SIL.Motif.Tests/Worker/DryRunAssessmentPipelineTests.cs`

- [x] **Step 1: Write pipeline outcome tests**

Assert Assessment defaults on, `--no-assessment` stops after Dry Run, scratch disposal occurs immediately
after export, the next project Dry Run begins while prior PanGloss runs, late results bind exact intent and
Baseline, poor linguistic results complete normally, tool failure yields
`completed-with-assessment-failure`, and amendment never relabels history. Pin deliberate omission as
`completed-dry-run-only`. Cancel during export or Assessment, retain the published Dry Run, mark the pipeline
job cancelled and Assessment disposition cancelled, and delete the candidate workspace.

- [x] **Step 2: Run red**

Run `./test.ps1`. Expected: pipeline coordinator is absent.

- [x] **Step 3: Implement durable stage boundaries**

```csharp
public sealed record DryRunAssessmentRequest(
    CanonicalId ProposalId,
    string IntentDigest,
    BaselineToken Baseline,
    bool IncludeAssessment);

public sealed class DryRunAssessmentPipeline
{
    public Task<JobRecord> ExecuteAsync(
        DryRunAssessmentRequest request,
        CancellationToken cancellationToken);
}
```

`DryRunAssessmentCommandHandler` accepts the closed worker command DTO and creates the durable job. Commit Dry
Run before export/Assessment. Store immutable Assessment and bounded log only after complete parse. Never
persist the engine or retry deterministic PanGloss refusal automatically.

- [x] **Step 4: Run green and commit**

Run `./test.ps1`, then:

```powershell
git add src/SIL.Motif.Worker tests/SIL.Motif.Tests/Worker
git commit -m "feat: orchestrate dry run assessments"
```

### Task 4: Delete all ephemeral PanGloss work

Large parser inputs and engine work should disappear as soon as their small durable result is safe.

**Files:**
- Create: `src/SIL.Motif.Worker/PanGloss/PanGlossWorkspace.cs`
- Modify: `src/SIL.Motif.Worker/Jobs/WorkerRecovery.cs`
- Test: `tests/SIL.Motif.Tests/Worker/PanGlossWorkspaceTests.cs`

- [x] **Step 1: Write terminal and startup cleanup tests**

Cover success, failure, cancellation, worker crash, locked file, and malicious path escape. Assert result and
bounded log remain durable while candidate export, engine work, and partial output disappear.

- [x] **Step 2: Run red**

Run `./test.ps1`. Expected: workspace ownership API is absent.

- [x] **Step 3: Implement owned workspace cleanup**

```csharp
public sealed class PanGlossWorkspace : IDisposable
{
    public string Root { get; }
    public void CompleteAndDelete();
}
```

Create under the verified worker `work` root with an ownership marker. On startup delete every marked
PanGloss workspace before requeueing safe jobs. Failure to delete becomes a bounded diagnostic and next-start
retry, never retention under the 30-day archive policy.

- [x] **Step 4: Run green and commit**

Run `./test.ps1`, then:

```powershell
git add src/SIL.Motif.Worker tests/SIL.Motif.Tests/Worker
git commit -m "feat: clear ephemeral pangloss work"
```

### Assessment archive integration checkpoint

Once Assessment pinning exists, archive deletion must query active Reports and Assessment pins before removing
any shared Assessment payload. The recovery/archive layer must not infer those references or delete shared data
until this PanGloss-owned query is available.
