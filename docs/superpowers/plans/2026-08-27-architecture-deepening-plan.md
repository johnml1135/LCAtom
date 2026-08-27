# Architecture deepening — from the 2026-08-27 review

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Deepen four modules whose current shape is already producing wrong answers or duplicated work.
Findings and their evidence come from the architecture review; every count below was measured against the
working tree.

**Architecture:** Vocabulary is the `codebase-design` glossary — module, interface, depth, seam, adapter,
leverage, locality. Domain terms are `CONTEXT.md`'s: Baseline, Proposal, Dry Run, Motif store, Motif job
runner, Motif API.

**Tech Stack:** `net10.0` except `SIL.Motif.Contract`; LibLCM, SQLite, xUnit. Gate is `./test.ps1`.

**Order:** Task 1 first — it is the one producing a wrong answer today. Tasks 2 and 3 compose with each
other (same exception→`FailureReason` table) and should land together. Task 4 is independent.

**Standing rule:** run `./test.ps1` and comment hygiene before each commit; state the test count and delta.

**What this plan deliberately does not do:** four findings are decisions rather than defects and are queued
for grilling instead. They are listed under *Queued for grilling* at the end. Do not implement them here.

---

### Task 1: Collapse the Baseline lifecycle into one module

A `baseline-refresh` job reports **Completed having published no Baseline**. `Program.CaptureAsync` writes a
zip to `<root>/captures/<guid>.zip`, discards the returned `BaselineToken`, and nothing ever reads that
directory. ADR 0039 §2 already states the required sequence — *"stream the bundle to worker-owned temporary
storage, then let the worker verify and atomically publish it"* — but no module owns it, so a caller
performed one step of four.

Measured: `BaselineBundleReceiver` (547 lines) has 0 production references and 21 test constructions.
`BaselineRepository.Record` has no production caller. `BaselineWorkspaceCatalog` is assigned to a field
that is never read.

**Files:**
- Create: `src/SIL.Motif.Worker/Baselines/BaselineRefresh.cs`
- Modify: `src/SIL.Motif.Worker/Program.cs` — drop `CaptureAsync`, construct the new module
- Modify: `src/SIL.Motif.Worker/Baselines/BaselineRefreshJobHandler.cs` — or delete it if it becomes a
  pass-through once the module owns the sequence
- Test: `tests/SIL.Motif.Tests/Worker/BaselineRefreshTests.cs`,
  `tests/SIL.Motif.Tests/Integration/RunnerSpineTests.cs`

- [ ] **Step 1: Assert the stored outcome, not the job status**

The spine test currently asserts only that the job left `queued`/`running`. Extend it to assert a Baseline
was actually published and recorded — this is the assertion the original plan called for and did not write,
and it is what makes the bug visible.

- [ ] **Step 2: Run red**

Expect the new assertion to fail against a Completed job, proving the gap rather than assuming it.

- [ ] **Step 3: Give the sequence one interface**

`Task<BaselineToken> RefreshAsync(ProjectLocator project, CancellationToken ct)` owning capture → verify →
publish → record, with `BaselineBundleWriter`, `VerifiedBinaryTransfer`, `BaselineBundleReceiver` and
`BaselineRepository` as private stages. The `Func<LcmCache, CancellationToken, Task>` capture callback
threaded through `Program` → `BaselineRefreshJobHandler` → `BaselineRefreshBarrier` goes away: the barrier
decides *whether*, this module does *what*.

Keep the barrier. It owns a genuinely separate decision — whether the project can be opened at all — and
has its own tests.

- [ ] **Step 4: Run green and commit**

Report how many of the six zero-production-construction modules this reached.

---

### Task 2: Make the failure contract reach every verb

Sixteen `Build*` helpers in `Commands.cs` return a bare `int 1` with no `FailureReason`, and
`Program.cs:385` emits a `FailureEnvelope` only when a reason is present. Measured at the executable: a
well-formed but absent Proposal id gives exit **1** — which the contract defines as *"retrying unchanged
cannot help"* — and raw text on stderr under `--json`. It should be exit 2, `NotFound`, enveloped.

`FailureContractTests` covers only `store-cutover`, which is why the gate is green.

**Files:**
- Modify: `src/SIL.Motif.Cli/Commands.cs` (the `(int, Projection?, string)` tuple and its 16 sites)
- Modify: `src/SIL.Motif.Cli/Program.cs`
- Test: `tests/SIL.Motif.Tests/Cli/FailureContractTests.cs`

- [ ] **Step 1: Widen the failure-contract tests past one verb**

Cover a verb from each group: an absent Proposal is `NotFound`/2; a malformed id is `InvalidArgument`/1; a
missing project is `InvalidArgument`/1. Assert the envelope is present under `--json` in each.

- [ ] **Step 2: Run red**

- [ ] **Step 3: Carry the reason through the tuple**

Replace the tuple's `int` with `FailureReason?` so the envelope at `Program.cs` becomes unconditional. The
16 sites keep their existing message text exactly; only the classification is added.

- [ ] **Step 4: Run green and commit**

State the distribution of reasons across the 16 sites, as the earlier failure-contract work did. A
distribution collapsed onto one reason means the classification was not really done.

---

### Task 3: One module for running a verb against the paired store

`JobCommands` and `StoreCommands` each define private `Locate`, `ParseVersion` and `Refuse`, and each
rebuild the same open → act → translate ladder. The comments on `Locate` and `ParseVersion` are duplicated
verbatim. The interface a new verb author must know is unwritten and large.

ADR 0021 says the verb set is expected to churn, which is exactly why this belongs behind one interface.

**Files:**
- Create: `src/SIL.Motif.Cli/ProjectStoreCommand.cs`
- Modify: `src/SIL.Motif.Cli/JobCommands.cs`, `src/SIL.Motif.Cli/Worker/StoreCommands.cs`
- Test: `tests/SIL.Motif.Tests/Cli/`

- [ ] **Step 1: Write the module's tests**

The exception→`FailureReason` table is the interface's real content, so test it directly: a missing project
file, a held database, an unsupported schema, and a successful act each produce the documented reason and
exit code.

- [ ] **Step 2: Run red**

- [ ] **Step 3: Implement and adopt it in both callers**

`CommandResult RunAgainstProject(string fwDataPath, Func<MotifDatabase, ProjectLocator, CommandResult> act)`
with `Locate`, `ParseVersion`, `MotifSchema.CurrentSchema` and the translation table behind it.

- [ ] **Step 4: Run green and commit**

Name what was deleted: two `Locate`, two `ParseVersion`, two `Refuse`, two catch ladders.

---

### Task 4: Extract the claim protocol from `JobRepository`

822 lines and 30 public members holding four things: row persistence, the claim/lease protocol, retry and
recovery policy, and an archive-retention engine. The state machine's rules are restated twice more inside
it (`ValidateFailureCategory` on the write path, `ValidatePersisted` on the read path).

The seam goes between the durable row store and the claim protocol, because that is where something varies:
`JobRunnerLoop` uses only `ClaimNext` and `Heartbeat`, and their real interface is two methods.

**Scope limit:** extract the protocol only. Whether the eight methods with zero production callers
(`RequestCancellation`, `PurgeArchived`, and six others) should be wired or deleted is a decision, and is
queued for grilling rather than settled here.

**Files:**
- Create: `src/SIL.Motif.Worker/Jobs/JobClaims.cs`
- Modify: `src/SIL.Motif.Worker/Jobs/JobRepository.cs`, `Jobs/JobRunnerLoop.cs`
- Test: `tests/SIL.Motif.Tests/Worker/JobLeaseTests.cs`

- [ ] **Step 1: Point the existing lease tests at the new interface**

`JobLeaseTests` already covers claiming, expiry, heartbeat and the stale-token case. Retarget it at the
extracted module; the assertions do not change, which is the evidence the seam is in the right place.

- [ ] **Step 2: Run red**

- [ ] **Step 3: Extract**

`JobClaims` over the same database: `Claim(projectKey, ownerId, nowUtc, lease)` and
`Renew(jobId, claimToken, nowUtc, lease)`. `JobRunnerLoop` takes it instead of the whole repository, so the
loop's interface shrinks from 30 members to two.

- [ ] **Step 4: Run green and commit**

---

## Queued for grilling

These are decisions, not defects. Each needs an answer before any code moves.

| # | Question |
| --- | --- |
| G1 | **`CliSession`** — 325 lines plus four `Commands` overloads, reachable from nine test files and no verb. Give it the verb that justifies it, or delete ~450 lines? |
| G2 | **The unwired Dry Run path** — `DryRunJobHandler`, `DryRunAssessmentPipeline`, `MachinePanGlossQueue`, `ProposalRepository`, `ReportRepository`, `ProjectWorkspaceEvictor`: register the handler, or move them to a branch? Compiled-and-tested-but-unreachable is the worst of the three. |
| G3 | **`JobRepository`'s dead surface** — cancellation and archive retention are complete, tested, and unreachable. Wire them, or delete them? |
| G4 | **The work lease** — five near-identical refresh entry points and an interface obligation stated only in a comment. Make the lease derived rather than cached, or keep the cache and make the obligation explicit in the type? |

## Out of scope

- **The Launcher.** `SIL.Motif.Cli.csproj` references it; no CLI source mentions it. It exists to start a
  worker and connect to it, and the connecting half is gone. Its fate belongs with G2's answer.
- **`ProjectDatabaseCatalog.Open`** — a verbatim alias of `OpenOwned` with one caller, in a test. Delete it
  opportunistically when touching that file; not worth a task.
