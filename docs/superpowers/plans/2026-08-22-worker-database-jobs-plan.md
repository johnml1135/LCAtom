# Worker Database and Durable Jobs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Move all local workflow records into one paired SQLite database and give long operations a durable,
recoverable lifecycle.

**Architecture:** The worker is the only database opener and migrator. Schema metadata rejects downgrade;
commands use transactions; ephemeral bytes remain in worker-owned directories; archive and retry policy are
durable facts rather than process memory.

**Tech Stack:** `net10.0`, `Microsoft.Data.Sqlite`, WAL, transactional migrations, xUnit.

---

### Task 1: Establish database identity and migration ownership

One process must recognize, upgrade, and protect each project's Motif database before any workflow uses it.

**Files:**
- Create: `src/SIL.Motif.Contract/Projects/ProjectLocator.cs`
- Replace: `src/SIL.Motif.Host/Store/SqliteMotifDatabase.cs`
- Create: `src/SIL.Motif.Host/Store/MotifDatabase.cs`
- Create: `src/SIL.Motif.Host/Store/MotifSchema.cs`
- Create: `src/SIL.Motif.Worker/Store/ProjectDatabaseCatalog.cs`
- Test: `tests/SIL.Motif.Tests/Store/MotifDatabaseMigrationTests.cs`

- [ ] **Step 1: Write failing migration tests**

Test new database metadata including the registered `ProjectLocator`, atomic upgrade, wrong application id,
newer schema refusal, minimum-worker refusal, locator mismatch detection before writes, failed migration
rollback, and exclusive migrator ownership.

- [ ] **Step 2: Run red**

Run `./test.ps1`. Expected: metadata and migration APIs are missing.

- [ ] **Step 3: Implement the owner-only opener**

Use SQLite `PRAGMA application_id`, `PRAGMA user_version`, WAL, foreign keys, and a busy timeout. Expose:

```csharp
public sealed class MotifDatabase
{
    public static MotifDatabase OpenOwned(
        string path,
        ProjectLocator project,
        int supportedSchema,
        Version workerVersion);

    public SqliteConnection OpenConnection();
}
```

Create the minimal shared locator contract here because database identity needs it before Baseline scheduling:

```csharp
public sealed record ProjectLocator(
    string FullFwDataPath,
    string FieldWorksProjectIdentity);
```

The Baseline plan consumes this contract and adds workspace-key coverage; it does not create a second locator.

Move the existing Corpus and Assessment DDL into ordered migrations. No Client, CLI, or FieldWorks-facing
assembly may reference `Microsoft.Data.Sqlite` after this move. `ProjectDatabaseCatalog` is the only
production component that constructs `MotifDatabase`; the Host assembly contains the `net10.0` implementation
because its existing Corpus and Assessment repositories already live there.

- [ ] **Step 4: Run green and commit**

Run `./test.ps1`, then:

```powershell
git add src/SIL.Motif.Contract src/SIL.Motif.Host src/SIL.Motif.Worker tests/SIL.Motif.Tests/Store
git commit -m "feat: centralize motif database ownership"
```

### Task 2: Migrate all existing local records into the sibling SQLite database

Existing Proposals, Corpora, and Assessments must survive the storage move byte-for-byte and remain usable
without manual conversion.

**Files:**
- Create: `src/SIL.Motif.Worker/Store/ProposalRepository.cs`
- Create: `src/SIL.Motif.Worker/Store/ReportRepository.cs`
- Create: `src/SIL.Motif.Worker/Store/FileProposalStoreMigration.cs`
- Create: `src/SIL.Motif.Worker/Store/LegacyBulkStoreMigration.cs`
- Modify: `src/SIL.Motif.Cli/Store/ProposalStore.cs`
- Test: `tests/SIL.Motif.Tests/Store/ProposalRepositoryTests.cs`
- Test: `tests/SIL.Motif.Tests/Store/FileProposalStoreMigrationTests.cs`
- Test: `tests/SIL.Motif.Tests/Store/LegacyBulkStoreMigrationTests.cs`

- [ ] **Step 1: Write failing parity and migration tests**

Seed current draft, object, manifest, Decision, and status files plus a current `.motif/motif.db` containing
Corpora, Assessment runs, analyses, and pins. Assert one transaction imports each source into the sibling
database, preserves exact Proposal JSON and intent digest plus all bulk-data relationships, is idempotent, and
renames legacy sources only after commit. Inject failure after every table and prove rollback leaves both
sources readable and the destination unopened by clients.

- [ ] **Step 2: Run red**

Run `./test.ps1`. Expected: repository and migration types are absent.

- [ ] **Step 3: Implement normalized workflow tables**

Create `Proposals`, `ProposalRevisions`, `Drafts`, `Decisions`, `Receipts`, `Reports`, and `AppliedIndex`.
`Reports` stores the durable aggregate/report document and exact evidence bindings; Corpora, Assessments, and
analysis rows remain in their migrated typed tables. Keep canonical
Proposal JSON as bytes/text exactly as parsed today. Expose transactions through:

```csharp
public interface IProposalRepository
{
    ProposalRecord Get(CanonicalId proposalId);
    IReadOnlyList<ProposalRecord> List(ProposalListFilter filter);
    void SaveRevision(ProposalRevisionRecord revision);
    void SaveDecision(DecisionRecord decision);
}
```

`LegacyBulkStoreMigration` attaches the old database read-only, copies its known schema with explicit column
maps, verifies row counts and foreign-key relationships, and records the source digest in the migration
ledger. The CLI file store and old database opener remain only as migration readers until CLI cutover in plan
6; no new writes target them.

- [ ] **Step 4: Run green and commit**

Run `./test.ps1`, then:

```powershell
git add src/SIL.Motif.Worker src/SIL.Motif.Cli tests/SIL.Motif.Tests/Store
git commit -m "feat: persist proposal workflow in sqlite"
```

### Task 3: Add the durable job state machine

Long-running work needs an honest status that survives a command exit or worker restart.

**Files:**
- Create: `src/SIL.Motif.Contract/Jobs/JobContracts.cs`
- Create: `src/SIL.Motif.Worker/Jobs/JobRepository.cs`
- Create: `src/SIL.Motif.Worker/Jobs/JobStateMachine.cs`
- Modify: `src/SIL.Motif.Host/Store/MotifSchema.cs`
- Test: `tests/SIL.Motif.Tests/Worker/JobStateMachineTests.cs`

- [ ] **Step 1: Write every legal and illegal transition**

Pin `queued`, `waiting-for-baseline`, `waiting-for-project-host`, `running`, `completed`,
`completed-dry-run-only`, `completed-with-assessment-failure`, `failed`, `cancelled`, and `interrupted`.
Assert terminal states cannot reopen except through an explicit retry that creates a new attempt, and that
cancellation after published Dry Run retains that record while setting Assessment disposition to cancelled.

- [ ] **Step 2: Run red**

Run `./test.ps1`. Expected: job contracts are missing.

- [ ] **Step 3: Implement transactional transitions**

Use:

```csharp
public sealed record JobRecord(
    string JobId,
    string ProjectKey,
    string Kind,
    JobStatus Status,
    int Attempt,
    string InputJson,
    string? ResultJson,
    string CreatedUtc,
    string UpdatedUtc);

public sealed class JobStateMachine
{
    public JobRecord Transition(
        JobRecord current,
        JobStatus next,
        string? resultJson = null);
}
```

Persist progress as bounded structured JSON. Large payload paths are worker-owned and never placed in the
database. Cancellation is a durable request flag checked by execution boundaries.

- [ ] **Step 4: Run green and commit**

Run `./test.ps1`, then:

```powershell
git add src/SIL.Motif.Contract src/SIL.Motif.Worker tests/SIL.Motif.Tests/Worker
git commit -m "feat: add durable job lifecycle"
```

### Task 4: Implement recovery, archive, and derived-work cleanup

Interrupted and old work should recover predictably without allowing temporary data to accumulate forever.

**Files:**
- Create: `src/SIL.Motif.Worker/Jobs/WorkerRecovery.cs`
- Create: `src/SIL.Motif.Worker/Store/ArchivePolicy.cs`
- Create: `src/SIL.Motif.Worker/Store/WorkspaceCleaner.cs`
- Create: `src/SIL.Motif.Worker/Store/ProjectWorkspaceEvictor.cs`
- Create: `src/SIL.Motif.Worker/Baselines/BaselineRetentionCleaner.cs`
- Test: `tests/SIL.Motif.Tests/Worker/WorkerRecoveryTests.cs`
- Test: `tests/SIL.Motif.Tests/Store/ArchivePolicyTests.cs`

- [ ] **Step 1: Write clock-controlled failure tests**

Assert startup marks running work interrupted, clears exact owned `work` paths, retries infrastructure
interruptions at most three times with increasing delays, never retries Apply or deterministic parser failure,
archives terminal Proposals immediately, retains stale work, and respects 30-day/configurable/forever policy.
Pin whole-workspace eviction after 30 days of disuse, preservation under any live lease or durable reference,
and reference-aware deletion of superseded Baselines only after no active job, Dry Run, Decision, or Receipt
pins them.

- [ ] **Step 2: Run red**

Run `./test.ps1`. Expected: recovery and cleanup types do not exist.

- [ ] **Step 3: Implement safe exact-path cleanup**

Inject `IClock` and `IWorkspaceOwnership`. Resolve and verify every deletion target beneath
`%LOCALAPPDATA%/SIL/Motif/<project-key>/work`; never accept a FieldWorks project directory or sibling database
as a cleanup root. `ProjectWorkspaceEvictor` may remove an entire derived project-key directory only after the
configured disuse interval, no worker lease, and no durable reference. `BaselineRetentionCleaner` queries
references before deleting exact published Baseline files. An `unclaimed/` copied database is removable only
under the same exact-path and disuse checks; the active sibling database is never a cleanup target. Preserve
permanent applied-index rows.

```csharp
public sealed record ArchivePolicy(TimeSpan Retention, bool Forever);

public sealed class WorkerRecovery
{
    public IReadOnlyList<string> RecoverInterruptedJobs(DateTimeOffset now);
}
```

- [ ] **Step 4: Run green and commit**

Run `./test.ps1`, then:

```powershell
git add src/SIL.Motif.Worker tests/SIL.Motif.Tests/Worker tests/SIL.Motif.Tests/Store
git commit -m "feat: recover and archive worker state"
```
