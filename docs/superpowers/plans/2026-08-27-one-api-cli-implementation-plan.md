# One API — CLI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Make [ADR 0040](../../adr/0040-one-api-the-cli.md) true of the code. Delete the wire, retire the
`netstandard2.0` target that only the wire justified, give the CLI a machine-readable failure contract, and
put the response shapes where a FieldWorks surface can bind to them.

**Architecture:** One API — `motif <verb> --json`. `SIL.Motif.Contract` is the only assembly that crosses
into a consumer, and carries the response shapes. The CLI and a resident job runner coordinate through
`Project.motif.db` and never message each other. Nothing loads inside FieldWorks.

**Tech Stack:** `SIL.Motif.Contract` `netstandard2.0`; everything else `net10.0`; LibLCM, SQLite
(Microsoft.Data.Sqlite), xUnit. Gate is `./test.ps1`.

**Order:** Tasks 1 and 2 are strictly sequential and come first — every later task is smaller once the wire
is gone. Tasks 3, 4, 5 are independent of each other. Task 6 depends on 5. Task 7 depends on 2 and needs a
design note before code.

**Standing rule for every task:** run `./test.ps1` and the comment-hygiene check before each commit. Test
count is expected to *fall* in task 2 and rise afterwards; state the number and the reason in each commit
rather than letting a drop pass unexplained.

---

### Task 1: Take the CLI's one worker-backed verb in-process

`store-cutover` is the only verb routed over the wire. It must move before the wire can go, and its
behaviour must not change while it moves.

**Files:**
- Modify: `src/SIL.Motif.Cli/Program.cs` (the call at the `store-cutover` case)
- Modify: `src/SIL.Motif.Cli/Worker/StoreCommands.cs`
- Reference: `src/SIL.Motif.Worker/Store/ProjectStoreCutover.cs`, `StoreCutoverCommandHandler.cs`
- Test: `tests/SIL.Motif.Tests/Cli/` — the existing argv process tests for this verb

- [x] **Step 1: Pin the current observable behaviour**

Before changing the call path, assert what the verb does today from outside: exit code, stdout/stderr text,
`--json` shape, and the refusal when the capability is missing. These tests must pass against the *current*
wire-backed implementation, so they are a genuine before/after harness rather than a description of the
new code.

- [x] **Step 2: Run green (not red)**

Run `./test.ps1`. Expected: the new tests pass unchanged. This step is inverted deliberately — the point is
to prove the harness describes today's behaviour before today's behaviour is replaced.

- [x] **Step 3: Call the cutover in-process**

Replace `LaunchedWorkerCommandSession` at the call site with a direct call into the cutover the handler
wraps. Preserve the exclusive-lease semantics the handler provided: the cutover still runs under the
project's exclusive lease, and a caller that cannot take it still refuses rather than proceeding. Keep
`StoreCommands.CutoverAsync`'s signature shape where it costs nothing, so the diff stays readable.

- [x] **Step 4: Run green and commit**

Run `./test.ps1`. The Step 1 tests must still pass, unmodified. If any needed editing, the behaviour
changed — say so explicitly in the commit rather than adjusting the assertion quietly.

---

### Task 2: Delete the wire

3,356 lines of framing, envelopes, handshake, capability negotiation, refusals, dispatch, event sink, and
binary transfer, none of which carries domain behaviour.

**Files:**
- Delete: `src/SIL.Motif.Client/` (whole project — 6 files, 1,034 lines)
- Delete: `src/SIL.Motif.Contract/Worker/` (12 files, 763 lines)
- Delete: `src/SIL.Motif.Worker/WorkerServer.cs`, `BinaryTransferServer.cs`, `WorkerEventSink.cs`,
  `WorkerCommandDispatcher.cs`, `WorkerBuildMetadataProvider.cs`
- Delete: `src/SIL.Motif.Worker/Store/StoreCutoverCommandHandler.cs` and the other command handlers
- Delete: `src/SIL.Motif.Cli/Worker/` client plumbing left unused by Task 1
- Modify: `src/SIL.Motif.Worker/SIL.Motif.Worker.csproj` — drop the `SIL.Motif.Client` reference,
  `System.IO.Pipes.AccessControl`, and the `WorkerMetadataCanonicalJson` / `GenerateWorkerBuildMetadata` /
  `PublishWorkerBuildMetadata` protocol-metadata targets
- Modify: `src/SIL.Motif.Worker/Program.cs`, `WorkerLifetime.cs` — start and idle-exit without an endpoint
- Modify: `Motif.sln` — remove the Client project
- Modify, **not keep untouched**: `src/SIL.Motif.Launcher/` — see the widening note below
- Keep, untouched: `WorkerMutexOwner.cs`, `WorkerWorkTracker.cs`
- Delete: `tests/SIL.Motif.Tests/Client/` (2 files) and the wire tests under
  `tests/SIL.Motif.Tests/Worker/` (24 files — protocol, handshake, refusal, transfer)

- [x] **Step 1: Inventory what the tests actually cover**

Before deleting 26 test files, classify each: *wire behaviour* (delete with the wire) versus *durable or
lifetime behaviour that happens to be tested through the wire* (must survive, retargeted at the in-process
call). Write the classification into the commit message. This is the step where coverage is silently lost
if it is rushed.

- [x] **Step 2: Retarget the survivors**

Rewrite the second category to exercise the same behaviour in-process. They should fail now, because the
in-process seam they need does not exist yet for every handler.

- [x] **Step 3: Delete the wire and make the survivors pass**

Remove the files above. Where a deleted handler was the only caller of durable logic, the logic moves to
the CLI or job runner — it is not deleted with its handler.

- [x] **Step 4: Re-ground the Launcher on schema, not protocol**

Widened after Task 1 disproved the ADR's "the Launcher survives intact"
([ADR 0040 amendment, 2026-08-27](../../adr/0040-one-api-the-cli.md)). All three Launcher files are
protocol-coupled and the project references `SIL.Motif.Client`.

Keep the mechanism — registration, listing, digest and hash verification, the per-user mutex, process
start, run-until-idle. Replace the criterion: `WorkerBuildMetadata` becomes `{productVersion,
supportedSchema}`, `InstalledWorker` drops `Protocols` and `Capabilities` for the supported schema
generation, `WorkerSelector` compares that, and `EnsureConnectedAsync` becomes "ensure the runner is
running" with no handshake. `WorkerSelectorTests` (25) and `WorkerLauncherReviewTests` (9) are rewritten
against the new criterion rather than deleted — the behaviour they cover survives, its input changes.

The catalog's on-disk manifest format changes with it. Decide and record whether an existing manifest is
migrated or discarded; discarding is defensible pre-alpha, but it should be a decision rather than a
surprise.

- [x] **Step 5: Run green and commit**

Run `./test.ps1`. Report the new total and the delta, split into "wire tests deleted", "tests retargeted",
and "launcher tests rewritten", so the drop is accounted for rather than merely noted.

---

### Task 3: Retire `netstandard2.0` from the Runner

Its only reason was in-process loading by `net48` FieldWorks, which ADR 0040 decision 3 withdraws.
`SIL.Motif.Contract` **keeps** the target — it is the assembly FieldWorks and the non-.NET runners consume.

**Files:**
- Modify: `src/SIL.Motif.Runner/SIL.Motif.Runner.csproj` — `netstandard2.0;net10.0` → `net10.0`
- Delete: `src/SIL.Motif.Runner/Compatibility/IsExternalInit.cs`,
  `src/SIL.Motif.Runner/Compatibility/ModuleInitializerAttribute.cs`
- Keep: `src/SIL.Motif.Contract/Compatibility/IsExternalInit.cs` and the Contract target
- Decide: `src/SIL.Motif.Model` keeps `netstandard2.0` only if Task 5 shows Contract's response records
  need it. Do this task after Task 5 if that is still unknown.

- [x] **Step 1: Assert the target set**

A test that reads the `.csproj` files and asserts exactly which projects target `netstandard2.0`. This is
the guard that stops the target creeping back, and it is why this task has a test at all.

- [x] **Step 2: Run red**

Run `./test.ps1`. Expected: the assertion fails because the Runner still multi-targets.

- [x] **Step 3: Drop the target and its shims**

Remove the TFM and the two compatibility files. Modern BCL is now available in the Runner; do not spend
this task using it.

- [x] **Step 4: Run green and commit**

Run `./test.ps1`. Confirm `SIL.Motif.Contract` still builds for `netstandard2.0` and still has no LibLCM
reference — that is the property FieldWorks depends on.

---

### Task 4: Make a conflicted save fail loudly

Recorded as defect A8 in `docs/issues.md`. `HeadlessLcmUi.ConflictingSave` returns `true`, which means
revert; `SaveInternal` then returns normally, so a conflicted apply reports success while discarding the
work. Latent only because the exclusive `.fwdata` lock currently makes foreign conflicts impossible — which
is exactly the containment ADR 0040 decision 6 declines to give up.

**Files:**
- Modify: `src/SIL.Motif.Host/LcmUtils/HeadlessLcmUi.cs`
- Test: `tests/SIL.Motif.Tests/Host/`

- [x] **Step 1: Write the test**

Assert that the conflicting-save callback throws rather than returning, matching every other ambiguous
callback in the type and matching the class's own stated contract.

- [x] **Step 2: Run red**

Run `./test.ps1`. Expected: it returns `true` instead of throwing.

- [x] **Step 3: Throw like its neighbours**

Make it fail loudly. The message should say what happened and that a headless process has no correct answer
to give — not merely that it is unimplemented.

- [x] **Step 4: Run green, update issues.md, and commit**

Move A8 to `fixed`. Note in the row that the containment is now belt-and-braces: architecture keeps the
path unreachable, and the code fails loudly if that ever changes.

---

### Task 5: Move the response shapes into Contract

The records `--json` serialises are declared in `SIL.Motif.Projection`, which references LibLCM, ICU, and
libpalaso. A FieldWorks surface cannot bind to them. Until this lands, the typed half of the CLI contract
is undelivered.

**Files:**
- Create: `src/SIL.Motif.Contract/Responses/` — the serialisable records
- Modify: `src/SIL.Motif.Projection/` — builders keep the `LcmCache` work and return Contract records
- Modify: `src/SIL.Motif.Host/Analysis/`, `src/SIL.Motif.Cli/` — the other declaration sites
- Test: `tests/SIL.Motif.Tests/Contract/`

- [x] **Step 1: Write the binding test**

A test that deserialises real `--json` output for each verb group using **only** Contract types, with no
reference to Projection, Host, or LibLCM. This is the test that actually proves the FieldWorks story, and
it should read like the consumer it stands in for.

- [x] **Step 2: Run red**

Run `./test.ps1`. Expected: the types are not in Contract.

- [x] **Step 3: Move the records, leave the builders**

Move only what is serialised. Anything needing an `LcmCache` to populate stays in Projection and returns
the Contract record. If a record cannot move because it embeds a LibLCM type, that is a finding worth
recording — it means the JSON was leaking a model type into the wire format.

- [x] **Step 4: Run green and commit**

Run `./test.ps1`. Then confirm Contract still has no LibLCM `PackageReference`, and record whether
`SIL.Motif.Model` turned out to be needed by the moved records — Task 3 depends on that answer.

---

### Task 6: Give the CLI a machine-readable failure contract

Today all 59 refusals render as `error: <text>` on stderr with exit code `1`, `--json` or not. Specified in
[the Motif API](../../cli-api.md).

**Files:**
- Create: `src/SIL.Motif.Contract/Responses/FailureEnvelope.cs`, and the closed `reason` code set
- Modify: `src/SIL.Motif.Cli/Commands.cs` (`Fail`/`FailText`, and the 59 call sites), `Program.cs`
- Test: `tests/SIL.Motif.Tests/Cli/`

- [x] **Step 1: Write the failure-contract tests**

Cover: `--json` failure emits one envelope object on stderr and nothing on stdout; the human rendering is
unchanged without `--json`; each exit code is produced by a representative case; and the `2`/`3` split
holds — a refusal is not retryable, a lock is.

- [x] **Step 2: Run red**

Run `./test.ps1`. Expected: plain text and exit code `1` everywhere.

- [x] **Step 3: Implement the envelope and the codes**

Keep every existing message string exactly as written — this task adds an envelope and classifies, it does
not reword refusals. Assign each of the 59 sites a `reason` and an exit code. Where a site's correct code
is genuinely unclear, choose `2` and note it; do not invent a new code to avoid the decision.

- [x] **Step 4: Run green and commit**

Run `./test.ps1`. Record how many sites landed on each exit code — a distribution heavily skewed to one
code means the classification was not really done.

---

### Task 6b: Design and rebuild the Baseline refresh barrier

**Design settled** in
[the refresh barrier design](../specs/2026-08-27-baseline-refresh-barrier-design.md): a refresh tries the
`.fwdata` lock rather than negotiating, and a held project is refused as Busy. Rebuilding the barrier on
that basis is not yet done, and it amends ADR 0039 decision 3's automatic-later-completion clause, so it
wants the owner's agreement before code.

Found while executing Task 2, and owed a design rather than an improvisation. The refresh barrier ran an
accept/defer/decline conversation with the live host over the event pipe, so a refresh could ask FieldWorks
to release a project and learn whether it agreed, deferred, or refused. ADR 0040 removes that transport and
decision 6 declines to peer into a live project, so the conversation has no replacement mechanism.
`BaselineRefreshCommandHandler` was deleted with the wire rather than half-ported.

This is a real capability currently removed, not merely re-plumbed. It needs a design note first, settling:
what a refresh does when FieldWorks holds the project (wait on the `.fwdata.lock`, refuse, or queue for the
next opportunity); how "the host declined" is expressed when nothing can ask it; and whether the barrier
semantics in ADR 0039 decision 3 survive unchanged or need amending. Do not restore the handler by
inventing a channel.

---

### Task 7: Design and build the job lease

The only genuinely new design in ADR 0040. `Jobs` has `Version`, `Attempt`, `LineageId` and the
`(Status, UpdatedUtc)` index, but no `OwnerId`, `LeaseUntil`, claim token, or heartbeat — verified, zero
occurrences. Today one worker owns recovery because it is the only process, and `MarkRunningInterrupted`
sweeps at startup on that assumption. With a runner that can die while a CLI keeps running, a job claimed
by a dead owner must be reclaimable.

**Files:**
- Create: `docs/superpowers/specs/2026-08-27-job-lease-design.md`
- Modify: `src/SIL.Motif.Host/Store/MotifSchema.cs` — schema generation 8
- Modify: `src/SIL.Motif.Worker/Jobs/JobRepository.cs` — claim, heartbeat, reclaim
- Test: `tests/SIL.Motif.Tests/Worker/`, `tests/SIL.Motif.Tests/Store/`

- [x] **Step 1: Write the design note before any code** — [job lease design](../specs/2026-08-27-job-lease-design.md)

Settle: claim query shape (single-row `UPDATE ... WHERE JobId = (SELECT ... LIMIT 1) AND Status = 'Queued'`
under a write transaction, since SQLite has no `SKIP LOCKED`); owner identity and a claim token so a
revived stale owner cannot finalise a job reassigned away from it; lease duration and heartbeat interval;
reclaim policy and its interaction with `Attempt` and the existing interrupted-recovery path; and wake-up
by jittered polling, since SQLite has no `LISTEN/NOTIFY`. State explicitly what happens to
`MarkRunningInterrupted`'s startup sweep, which the lease partly replaces.

- [x] **Step 2: Write the concurrency tests**

Two processes racing for one queued job — exactly one wins. A claimed job is invisible to other claimants
until its lease expires. A job whose owner stops heartbeating is reclaimed. A revived stale owner cannot
transition a job reassigned away from it. Prefer two real processes over two connections for the race test;
if that is impractical, say so in the test file rather than letting the name imply more.

- [x] **Step 3: Run red**

Run `./test.ps1`. Expected: no lease columns, no claim query.

- [x] **Step 4: Implement schema 8 and the claim protocol**

Add the columns and the migration. Set `MinimumWorkerVersion` deliberately — this is the first migration
where the choice matters, and the first live exercise of ADR 0040 decision 5.

- [x] **Step 5: Verify the ship-together constraint is real**

Open a database migrated to schema 8 with a binary that supports 7 and assert the refusal at
`MotifDatabase.cs:82` produces a message a user could act on. Under ADR 0040 decision 5 that exception is
UX, not an internal invariant. If the message is not actionable, fix the message in this task.

- [x] **Step 6: Run green and commit**

Run `./test.ps1`.

---

## Out of scope

Named so they are not picked up by accident:

- **The FieldWorks surface itself.** Task 5 makes it *possible*; building it is scope 2.
- **Binding redirects for `System.Text.Json 8.0.5` in a `net48` host.** A real open question
  (ADR 0040 decision 3), answerable only when FieldWorks first references Contract.
- **Retiring `netstandard2.0` from Contract.** It stays. See ADR 0040 decision 3.
- **Routing more verbs anywhere.** There is nothing left to route them to; they already run in-process.
- **Wiring PanGloss into the real job runner.** Separate work, deliberately not bundled here.
