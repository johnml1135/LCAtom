# Integration spine — stand the product up

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Make one real `motif.exe` and one real runner process complete a job through the paired database,
and prove a killed runner's work is reclaimed. Decisions and their rejected alternatives are in
[the grill record](../../grill-integration-tests.md).

**Architecture:** `motif <verb>` enqueues a `baseline-refresh` row and exits. A runner process claims it
under a lease, runs `BaselineRefreshBarrier`, captures and publishes, and transitions the row. A second
`motif jobs show` reads the result. Nothing messages anything.

**Tech Stack:** `net10.0` throughout except `SIL.Motif.Contract`; LibLCM, SQLite, xUnit. Gate is
`./test.ps1`, and integration tests live in it.

**Order:** Task 1 is the precondition for every other task — nothing can be driven without the verbs and
the overrides. Tasks 2 and 3 are the loop and are sequential. Task 4 depends on 2. Tasks 5 and 6 are
independent of everything and can be done any time.

**Standing rule:** run `./test.ps1` and the comment-hygiene check before each commit, and state the test
count and its delta.

---

### Task 1: Make the runner and the CLI drivable at all

**Files:**
- Modify: `src/SIL.Motif.Worker/Program.cs` — worker-root and namespace overrides, lease option
- Modify: `src/SIL.Motif.Worker/JobRunnerHost.cs` — accept the namespace it is given
- Modify: `src/SIL.Motif.Cli/Program.cs` — two new verbs
- Create: `src/SIL.Motif.Cli/JobCommands.cs`
- Test: `tests/SIL.Motif.Tests/Cli/`

- [ ] **Step 1: Write the argv tests for the two verbs**

Enqueue prints a job id and exits 0; `jobs show <id>` prints the durable status as JSON; an unknown job id
is `NotFound` (exit 2) rather than a crash; a malformed id is `InvalidArgument` (exit 1). These are argv
tests against the real executable, because a verb that only works in-process is the thing this whole plan
exists to stop believing.

- [ ] **Step 2: Run red**

Run `./test.ps1`. Expected: unknown verbs.

- [ ] **Step 3: Add the verbs and the overrides**

Two verbs, both routed through `JobRepository` in-process like every other verb. The runner gains:
`MOTIF_WORKER_ROOT` (an operator needs this to run two installations), a lease-duration option (an
operator needs this to tune a wedged runner), and an owner-namespace override.

**The namespace override is test-only and must say so where it is defined.** It exists because a test
runner would otherwise fight the developer's real runner, and two concurrent test runs would fight each
other. Do not describe it as an operational feature.

- [ ] **Step 4: Run green and commit**

---

### Task 2: Give the runner a work loop

There is no `Kind` → handler dispatch anywhere, so the loop and its first handler land together.

**Files:**
- Create: `src/SIL.Motif.Worker/Jobs/JobRunnerLoop.cs`
- Create: `src/SIL.Motif.Worker/Baselines/BaselineRefreshJobHandler.cs`
- Modify: `src/SIL.Motif.Worker/Program.cs` — run the loop instead of idling
- Test: `tests/SIL.Motif.Tests/Worker/`

- [ ] **Step 1: Write the loop's tests in-process**

Claim → dispatch → terminal transition; a kind with no handler fails the job rather than looping on it
forever; a handler that throws produces a failed job carrying the reason; the loop heartbeats while a
handler runs; the loop exits when idle and does **not** exit while holding a lease.

That last one is the property most easily lost: a runner that exits on an idle timer while a job is
running abandons a leased row for the lease's whole duration.

- [ ] **Step 2: Run red**

- [ ] **Step 3: Implement the loop and the refresh handler**

Jittered polling, per [the job lease design](../specs/2026-08-27-job-lease-design.md). The handler calls
`BaselineRefreshBarrier` — its first production construction — and translates its three outcomes into job
states: `Refreshed` → completed; `ProjectInUse` → a retryable failure; `CaptureFailed` → failed with the
reason.

- [ ] **Step 4: Run green and commit**

---

### Task 3: One job, two real processes

The test that has never existed.

**Files:**
- Create: `tests/SIL.Motif.Tests/Integration/RunnerSpineTests.cs`
- Create: `tests/SIL.Motif.Tests/TestFixtures/RunnerProcess.cs`

- [ ] **Step 1: Write it**

A real `motif.exe` enqueues against a real project (a 48 KB copy from `PristineProjectFixture`), a real
runner process claims and completes it, and a second `motif.exe` reads the terminal status back. The
runner is started with a short idle timeout, an isolated namespace and a temp worker root, and the test
fails if it is still alive at the end.

Assert the *stored* result, not just the exit codes: a Baseline row exists that the refresh published.

- [ ] **Step 2: Run red, and read the failure**

It will fail for a real reason — a missing override, a path assumption, a runner that exits before
claiming. Record what it was in the commit, because those are the findings this task exists to produce.

- [ ] **Step 3: Make it pass without weakening it**

If a step needs a longer timeout, say so and say why. If it needs a sleep, that is a signal the loop lacks
an observable state, and the fix belongs in the loop rather than the test.

- [ ] **Step 4: Run green and commit**

---

### Task 4: Kill the runner and prove the reclaim

**Files:**
- Modify: `tests/SIL.Motif.Tests/Integration/RunnerSpineTests.cs`

- [ ] **Step 1: Write it**

Enqueue, let a runner with a one-second lease claim, kill the process outright, start a second runner, and
assert the row is reclaimed with `Attempt` incremented and reaches a terminal state.

Killing must be a real process kill, not a cancellation: the failure the lease was designed for is a
process that stops existing without unwinding anything.

- [ ] **Step 2: Run red**

- [ ] **Step 3: Make it deterministic**

A refresh over a 48 KB project may finish before the kill lands. Make the *lease* short rather than the
work slow: the reclaim is a function of lease expiry, so a one-second lease and a killed process is
deterministic without racing the handler.

- [ ] **Step 4: Run green and commit**

---

### Task 5: Split the parser tests by what they assert

**Files:**
- Modify: `tests/SIL.Motif.Tests/Parser/ParserSeamIntegrationTests.cs` (attribute unchanged; document why)
- Modify: `tests/SIL.Motif.Tests/Parser/GrammarCoverageFigureIntegrationTests.cs`

- [ ] **Step 1: Add the fake-backed coverage-figure test**

Assert the figure cites the digests its run carried — Motif's provenance plumbing, which a fake serves
honestly. Leave the two correlation tests on `RealParserFact` and record in the attribute's own docs why
a fake cannot serve them.

- [ ] **Step 2: Run green and commit**

---

### Task 6: Rename `EndToEndCliTests`

**Files:**
- Rename: `tests/SIL.Motif.Tests/Cli/EndToEndCliTests.cs`

- [ ] **Step 1: Rename to what it covers, and commit**

It walks new → add-set-gloss → finalize → list → show through the command layer. Name it for the proposal
workflow it exercises. The false name is part of why the absence of end-to-end coverage went unnoticed.

---

## Out of scope

- **`dry-run-assessment` as a job kind.** It follows once the loop has one working kind.
- **`--wait`.** Owed by the CLI API; the test polls instead, deliberately.
- **Wiring the refresh barrier to FieldWorks.** There is no FieldWorks surface yet.
- **`MachinePanGlossQueue`, `BaselineRetentionCleaner`.** Still zero production constructions, and still
  waiting on a second job kind to have a reason to run.
