# Grill — integration tests, and what the spine actually needs

*2026-08-27. Seven decisions, taken after two of the plan's premises turned out to be wrong. The
questions are recorded with what settled them, because the corrections are the useful part.*

## Why this was grilled at all

The repository has 1,201 tests and no test that runs Motif. `motif.exe` and the runner executable have
never met; `ClaimNext` has no caller; and `DryRunJobHandler`, `DryRunAssessmentPipeline`, `PanGlossParser`,
`BaselineRetentionCleaner`, `MachinePanGlossQueue` and `BaselineRefreshBarrier` each have **zero**
production constructions. The seams are well covered. The spine has never been stood up.

## Two premises that did not survive contact with the repository

### The 10.1-second cache load was the wrong number

The plan was shaped around it — a real project being expensive was the reason to consider a separate slow
suite. That figure comes from `docs/issues.md` B13 and describes a **43 MB, 61-entry** project.

`PristineProjectFixture` had already measured the number that matters and written it down:

> creating a project costs about 3.9 seconds; copying the saved **48 KB** of it and reopening costs about
> **35 milliseconds**.

A minimal project is 48 KB and opens in 35 ms. Real projects in integration tests are affordable, the
sharing pattern already exists, and the case for a separate suite evaporated.

### The loop and its first handler are one piece of work

The plan listed "the runner work loop" and "wire a real handler" as separate layers. There is no
`Kind` → handler dispatch anywhere in the codebase, so there is nothing for a loop to dispatch *to*. They
land together or not at all.

## Three blockers the plan had not named

| | |
| --- | --- |
| **The CLI has no job or baseline verbs** | It does not reference `JobRepository` at all. Nothing can enqueue or observe a job today, so "two processes, one database" has no surface to drive. |
| **The worker root is hardcoded** | `%LOCALAPPDATA%\SIL\Motif`, no override. A real-process test writes into the developer's actual profile. |
| **The owner mutex is per-SID** | A test runner fights a real one, and two concurrent test runs fight each other. |

All three are addressed the way this repository already addresses them: an environment override, following
`MOTIF_LIBLCM_CHECKOUT`, `MOTIF_PANGLOSS_EXE` and `MOTIF_FIELDWORKS_CHECKOUT`.

## The decisions

### 1. One gate, budgeted

Integration tests stay in `./test.ps1`. No separate suite and no trait filter, because `test.ps1`'s own
documentation explains why it has no filter parameter — *"a filter nobody questions is where the next one
hides"* — and the 35 ms copy removes the cost that would have justified breaking that.

### 2. `baseline-refresh` is the first job kind

It needs a project and the capture path, and no export, no scratch and no parser. It is a real product
kind, so no test-only kind leaks into production dispatch, and it gives `BaselineRefreshBarrier` its first
production construction.

Rejected: `dry-run-assessment` drags in `DryRunScratch`, the export, the lane and the Baseline at the same
moment as the loop, so a failure would have four plausible causes. A trivial no-op kind would sit in
production dispatch forever with nothing stopping it being the only one that works.

### 3. Two verbs: enqueue, and `jobs show`

The test polls `jobs show` itself, which is exactly what an AI agent would do and keeps the queue visible.
`--wait` is already owed by [the CLI API](cli-api.md) and can arrive later without changing either verb.

Rejected: a single blocking `--wait` call would make a broken loop and a slow loop look identical, and
would require `--wait`'s own polling to be correct before the loop it is polling was proven.

### 4. A real runner process, with the overrides that makes possible

The runner runs as its built executable beside a real `motif.exe`. This is the only arrangement that
proves what [ADR 0040](adr/0040-one-api-the-cli.md) rests on.

Rejected: an in-process loop proves the logic and nothing about the shipped executable — which is exactly
how the deleted wire sat unexercised for months while its seams stayed green.

### 5. The parser tests split by what they assert

Two of the three assert things only a real parser can produce — that every morpheme it names is an object
the project contains, and that the fallback engine agrees on which words parse. A fake echoing back what
the test told it would turn both into tautologies, so they keep `RealParserFact` and keep skipping without
a real build.

The third asserts that a coverage figure *cites its own run*. That is Motif's provenance plumbing rather
than linguistics, so it gains a fake-backed sibling that runs everywhere.

The distinction worth keeping: **a fake is honest where the assertion is about Motif, and dishonest where
the assertion is about the parser.**

### 6. `EndToEndCliTests` is renamed

It drives `Commands.New(...)` in-process and walks new → add-set-gloss → finalize → list → show. That is a
good proposal-workflow test with a false name, and the false name is part of why the absence of end-to-end
coverage went unnoticed. Renaming costs nothing; promoting it would re-prove the new integration test
slowly.

### 7. Kill-and-reclaim lands in this round

The lease duration becomes a runner option so a test can set it short, kill the process mid-job, start a
second runner, and assert the row is reclaimed with `Attempt` incremented.

`JobLeaseTests`' own remarks already admit they do not cover a runner killed mid-claim. Deferring this
would leave the lease in precisely the state this work exists to correct: built, green at the seam, never
stood up.

## The cost, stated plainly

This round adds four seams for testability: worker root, mutex namespace, lease duration, and the parser
path. Three are things an operator needs anyway — you cannot run two Motif installations, tune a lease, or
point at a parser build without them.

**The mutex namespace is genuinely test-only.** It is recorded here as such rather than dressed up as an
operational feature.

## What this round retires

- *"`motif.exe` and `worker.exe` have never met."*
- *"The seams are tested; the spine has never been stood up."*
- `BaselineRefreshBarrier`'s zero production constructions.
