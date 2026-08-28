# The database is the only store — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Deliver [ADR 0041](../../adr/0041-the-database-is-the-only-store.md). Proposals, Drafts, Corpora
and Reports live only in the paired project database; every verb names its project; a machine store holds
Known projects and the usage log; the runner sweeps every Known project in one global queue order; and
`dry-run` becomes a job.

**Architecture:** Vocabulary is the `codebase-design` glossary — module, interface, depth, seam, adapter,
leverage, locality. Domain terms are `CONTEXT.md`'s, including the four added with this plan: **Draft**,
**Machine store**, **Known project**, **Queue order**.

**Tech Stack:** `net10.0` except `SIL.Motif.Contract`; LibLCM, SQLite, xUnit. Gate is `./test.ps1`.

**Standing rules:**
- Run `./test.ps1` before every commit and state the test count and its delta.
- Deletions are as much a deliverable as additions. Name what was deleted, with line counts.
- No migration code. Anything that reads the file store is deleted, not adapted.

**Order and why:** Tasks 1–2 are pure deletion and shrink the tree before anything is rewritten in it.
Task 3 builds the machine store, which Tasks 5–7 need. Task 4 is the schema, which Tasks 6, 8 and 9 need.
Tasks 5–7 move the CLI onto the database. Task 8 makes the runner a sweeper. Task 9 is the queue. Task 10
is the last one that can be done at any point.

---

### Task 1: Delete the migration path and the `store-cutover` verb

ADR 0041 decision 1. This never worked — `store-cutover` refuses every Proposal the CLI can author,
because `OperationKindRegistry` is populated by `SIL.Motif.Runner`'s module initializers and only
`Commands`' static constructor forces them to load.

**Files:**
- Delete: `src/SIL.Motif.Worker/Store/ProjectStoreCutover.cs` (153),
  `FileProposalStoreMigration.cs` (494), `LegacyBulkStoreMigration.cs` (710), `PendingSourceArchive.cs` (45)
- Delete: `src/SIL.Motif.Cli/Worker/StoreCommands.cs`
- Modify: `src/SIL.Motif.Cli/Program.cs` — drop the verb and its banner line
- Delete: `tests/SIL.Motif.Tests/Store/{FileProposalStoreMigrationTests,LegacyBulkStoreMigrationTests,ProjectStoreCutoverTests}.cs`,
  `tests/SIL.Motif.Tests/Cli/StoreCutoverArgvTests.cs`
- Modify: `tests/SIL.Motif.Tests/Cli/FailureContractTests.cs` — it drives `store-cutover`; retarget its
  cases at a surviving verb rather than deleting the coverage

- [ ] **Step 1: Retarget `FailureContractTests` first, and run green**

It is the only test file here that covers something surviving. Its four envelope cases must keep asserting
the same properties against a verb that still exists.

- [ ] **Step 2: Delete, and run green**

`MigrationLedger` stays in the schema until Task 4 drops it, so nothing here touches `MotifSchema`.

---

### Task 2: Delete `CliSession` and the Launcher

ADR 0041 decision 7. `CliSession`'s four `Commands` overloads are `DryRun`, `DryRunJson`, `Apply`,
`ApplyJson`, and no verb calls any of them.

**Files:**
- Delete: `src/SIL.Motif.Cli/CliSession.cs` (325), `tests/SIL.Motif.Tests/Cli/CliSessionTests.cs`
- Modify: `src/SIL.Motif.Cli/Commands.cs` — the four session overloads and
  `BuildSessionDryRunProjection`/its apply twin
- Delete: `src/SIL.Motif.Launcher/` and its project reference from `src/SIL.Motif.Cli/SIL.Motif.Cli.csproj`
- Modify: `motif.sln`
- Delete: the Launcher's tests

- [ ] **Step 1: Confirm nothing else reaches either, then delete and run green**

Six test files mention `CliSession`. Check each: a test that only *constructs* it goes with it; a test that
asserts something about `Commands` needs its non-session overload instead.

---

### Task 3: The machine store

ADR 0041 decision 4. A second SQLite database in the worker root holding `KnownProjects` and `Usage`.

`MotifSchema` currently assumes one shape, so this is where that assumption is broken. The seam is real —
two schemas, two databases — so it earns a separate module rather than a flag.

**Files:**
- Create: `src/SIL.Motif.Host/Store/MachineDatabase.cs`, `MachineSchema.cs`
- Create: `src/SIL.Motif.Host/Store/KnownProjectRegistry.cs`
- Modify: `src/SIL.Motif.Projection/Usage/UsageLogFile.cs` → a `UsageLog` writing to the machine store
- Test: `tests/SIL.Motif.Tests/Store/MachineStoreTests.cs`

- [ ] **Step 1: Write the tests**

`KnownProjectRegistry.Record` upserts and is idempotent; `List` returns what was recorded; a project whose
file is gone is dropped by `Forget`; concurrent usage appends from two connections both land.

- [ ] **Step 2: Run red**

- [ ] **Step 3: Implement**

`MachineDatabase.Open(root)` resolving `<root>/motif.db`, its own schema generation, WAL, and the same
corruption/unavailable translation `MotifDatabase` uses. Do **not** generalise `MotifSchema` to serve both
— duplicate the small amount that is genuinely common rather than growing a parameterised schema module.

- [ ] **Step 4: Run green and commit**

---

### Task 4: The schema change

ADR 0041 decisions 3, 6 and 8. One schema generation, carrying every structural change the rest of the plan
needs, so the compatibility floor moves once.

**Files:**
- Modify: `src/SIL.Motif.Host/Store/MotifSchema.cs`
- Modify: `src/SIL.Motif.Worker/Store/ProposalRepository.cs`
- Test: `tests/SIL.Motif.Tests/Store/MotifDatabaseMigrationTests.cs`

- [ ] **Step 1: Write the schema tests**

- [ ] **Step 2: Run red**

- [ ] **Step 3: Change the schema**

Drop `Drafts` and `MigrationLedger`. `Proposals` gains `DraftName TEXT NULL` with a unique index, and
`CurrentIntentDigest` becomes nullable. `Jobs` gains `QueueOrder REAL NOT NULL` and an index on it.

Then extend `ProposalRepository` so a Draft is a Proposal it can create, amend and finalize —
`CreateDraft`, `SaveDraft`, `Finalize`, and `List` including drafts. `finalize` writes the first
`ProposalRevisions` row and sets `CurrentIntentDigest` in one transaction.

- [ ] **Step 4: Run green and commit**

State what the compatibility floor moved to and why it moved once rather than three times.

---

### Task 5: Proposal and draft verbs onto the database

ADR 0041 decisions 1, 2 and 3. The largest task; `Commands.cs` does the file I/O directly, so this is a
rewrite of its Proposal path rather than a swap of one collaborator.

**Split in two, because each half can be green on its own and the whole cannot be reviewed at once.**
`Commands.cs` is 1,726 lines with 20 `ProposalStore` call sites, and nine test files hold 49 more.

- **5a — every verb names its project, storage unchanged.** Each verb gains a required `--project`, and
  the store directory is derived from the project (`<project dir>/.motif`) rather than from the working
  directory. This alone closes ADR 0041 decision 2's defect: the same project reached from two terminals
  now resolves to one store. `--store` survives only for the corpus verbs until Task 6.
- **5b — the storage swaps underneath.** `ProposalStore` gives way to `ProposalRepository`, and the verb
  signatures 5a settled do not move again.

A single pass was rejected: the intermediate state where `finalize` writes the database and `list` reads
files fails `ProposalWorkflowTests`, so the storage swap cannot be staged verb-by-verb. Splitting on
*where the store is* against *what the store is* gives two green landings instead.

**Files:**
- Modify: `src/SIL.Motif.Cli/Commands.cs`, `src/SIL.Motif.Cli/Program.cs`
- Delete: `src/SIL.Motif.Cli/Store/ProposalStore.cs`
- Modify: ~13 test files under `tests/SIL.Motif.Tests/Cli/`

- [ ] **Step 1: Give the verb tests a project fixture**

Every test currently passing `--store <dir>` needs a project. The existing `ProjectStoreCommand.Run` seam
is what a verb goes through; a fixture that creates a `.fwdata` and lets the paired database be created is
enough — no LibLCM load.

- [ ] **Step 2: Run red**

- [ ] **Step 3: Move the verbs**

`new`, `add-*`, `compose-*`, `promote-gloss`, `label`, `comment`, `finalize`, `reopen`, `duplicate`,
`remove-operations`, `split`, `defer`, `approve`, `reject`, `supersede`, `list`, `show` — each gains a
required `--project` and runs through `ProjectStoreCommand.Run`.

Drafts appear in `list`, marked, per ADR 0041 decision 3.

**Restore one property Task 2 could not keep.** Three `CommandsRefusalsTests` cases asserted
`session.PristineRebuildCount == 0` to pin that a store-consistency refusal happens *before* the project
is touched. The counter went with `CliSession`, and the tests kept their other assertions. The property is
real and worth asserting again once these move onto `ProposalRepository`: a refusal reaching the caller
without the project file's write time changing says the same thing, and says it about the live path.

- [ ] **Step 4: Run green and commit**

---

### Task 6: Corpus verbs onto the database, and `--store` deleted

**Files:**
- Modify: `src/SIL.Motif.Cli/CorpusCommands.cs`, `Program.cs`
- Delete: `FileCorpusStore` from `src/SIL.Motif.Host/Corpus/ICorpusStore.cs`,
  `src/SIL.Motif.Host/Corpus/CorpusStoreMigration.cs`

**Corrected 2026-08-28 — see the ADR's amendment.** `CorpusCommands.StoreFor` already returns a
`SqliteCorpusStore`; what is wrong is *which* database it points at — `<storeDir>/motif.db`, a third
database beside the paired one and the machine store. `FileCorpusStore` has 0 production constructions and
`CorpusStoreMigration` has no caller at all.

**Do this next, ahead of 5b.** Task 5a opens a window where `promote-gloss` reads the project's store while
`add-corpus` writes the working directory's; this task closes it.

- [ ] **Step 1: Write the test that fails today**

`motif add-corpus --project <fwdata>` then `motif promote-gloss --project <fwdata> --corpus <id>` from a
*different* working directory. That is the break 5a opened, and it must be red before it is fixed.

- [ ] **Step 2: Repoint the corpus store at the paired project database**

`StoreFor` takes the project, not a store directory, and resolves the paired `<project>.motif.db`. The
corpus verbs gain a required `--project`. `Corpora` and `CorpusDocuments` already exist in that schema.

- [ ] **Step 3: Delete `FileCorpusStore` and `CorpusStoreMigration`, remove `--store` entirely, run green**

The usage log moves to `MachineUsageLog` here, since `storeDir` was its last remaining reader.

---

### Task 7: `dry-run` becomes a job

ADR 0041 decision 7.

**Files:**
- Modify: `src/SIL.Motif.Cli/JobCommands.cs`, `Program.cs`, `Commands.cs`
- Modify: `src/SIL.Motif.Worker/Jobs/DryRunJobHandler.cs`, `src/SIL.Motif.Worker/Program.cs`

- [ ] **Step 1: Write the argv test**

`motif dry-run --project <fwdata> <proposalId>` prints a job id and exits 0; `--wait` blocks until
terminal. Absent Baseline leaves the job `WaitingForBaseline` rather than failing.

- [ ] **Step 2: Run red**

- [ ] **Step 3: Fix the handler's contract and register it**

`DryRunJobHandler.RunAsync` refuses a job that is not `Queued`, but `JobRunnerLoop` claims before it
dispatches, so the handler would throw on the first job. Its precondition must match the loop.

- [ ] **Step 4: Delete `BuildFileDryRunProjection` and the in-process path; run green and commit**

---

### Task 8: The runner sweeps every Known project

ADR 0041 decisions 5 and 6, and the answer to G4.

**Files:**
- Modify: `src/SIL.Motif.Worker/Program.cs`, `Jobs/JobRunnerLoop.cs`
- Delete: `src/SIL.Motif.Worker/WorkerWorkTracker.cs` and `IWorkerWorkTracker`
- Modify: `src/SIL.Motif.Worker/Projects/ProjectRuntime.cs` — the cached lease and five refresh methods
- Test: `tests/SIL.Motif.Tests/Integration/RunnerSpineTests.cs`

- [ ] **Step 1: Extend the spine test to two projects**

One runner, two projects, one job in each; both reach terminal. This is the test that fails today and is
the reason this task exists.

- [ ] **Step 2: Run red**

- [ ] **Step 3: Sweep, and derive idleness from it**

The loop peeks the head of every Known project and claims the globally first by `QueueOrder`. Idleness is
"no Known project has a queued, running or waiting row" — computed by the sweep, not cached. Close the
kick race: a spawned runner that loses the ownership mutex retries for a short window rather than exiting.

- [ ] **Step 4: Run green and commit**

---

### Task 9: The job verbs

ADR 0041 decision 6.

**Files:**
- Modify: `src/SIL.Motif.Cli/JobCommands.cs`, `Program.cs`
- Modify: `src/SIL.Motif.Worker/Jobs/JobRepository.cs`, `JobClaims.cs`

- [ ] **Step 1: Write the verb tests**

`jobs list --all` orders across two projects by `QueueOrder`; `jobs move --to-top` changes what claims
next; `jobs cancel` stops a running handler; `jobs requeue` inserts a fresh attempt.

- [ ] **Step 2: Run red**

- [ ] **Step 3: Implement**

`jobs cancel` sets `CancellationRequested`; the heartbeat reads it and cancels the handler's token, landing
in the loop's existing `OperationCanceledException` → `Cancelled` path. `jobs move` writes one row, giving
it a `QueueOrder` between its new neighbours. `jobs requeue` calls the existing `JobRepository.Retry`.

**`QueueOrder` ties are real, so the order must break them.** The default is `julianday('now')` in epoch
milliseconds, and two jobs enqueued inside one millisecond get the same value. Measured through the real
`JobRepository.Create`: 200 inserts spanning 1,389 ms produced **199 distinct values — one tie** — and a
caller enqueueing in bulk would collide far more often. Every ordering must therefore be
`ORDER BY QueueOrder, JobId`, in the claim and in `jobs list --all` alike, or two jobs can be claimed in an
order that changes between runs. `jobs move --before <id>` also has no midpoint to pick when the two
neighbours are tied: it must renumber the tie rather than write a duplicate.

- [ ] **Step 4: Run green and commit**

---

### Task 10: Cap finished jobs at 500 per project

ADR 0041 decision 8.

**Files:**
- Modify: `src/SIL.Motif.Worker/Store/ArchivePolicy.cs`, `Jobs/JobRepository.cs`
- Modify: `src/SIL.Motif.Worker/Program.cs` — purge on a sweep tick when a project has nothing active

- [ ] **Step 1: Write the tests, run red, implement, run green and commit**

`ArchivePolicy` becomes a retained count. `IsEligibleArchive`'s lineage rule is unchanged — an attempt is
not purged while a later attempt in its lineage is live — and that is why the engine is kept rather than
rewritten.

---

## Out of scope

- **`ProjectWorkspaceEvictor`, `DryRunAssessmentPipeline`, `MachinePanGlossQueue`, `ReportRepository`.**
  Still zero production constructions after Task 7. `MachinePanGlossQueue` waits on a job kind that runs
  PanGloss; the others wait on the Assessment path. Revisit once `dry-run` is running as a job.
- **Reports.** `ReportRepository` moves with the same cut, but no verb reads Reports today.
- **`--wait` polling interval and output shape.** Settle when Task 7 lands.
