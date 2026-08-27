# Decisions queued for grilling — 2026-08-27

The [architecture deepening plan](2026-08-27-architecture-deepening-plan.md) fixed the four findings that
were defects. These four are not defects. Each is a decision with a real cost either way, and each needs an
answer before any code moves.

Every count below was measured against `2b38663`. Run them again before grilling — they are the kind of
number that goes stale.

---

## G1 — What is `CliSession` for?

`src/SIL.Motif.Cli/CliSession.cs` is 325 lines. Six test files drive it. In production it is reachable only
through four `Commands` overloads — `DryRun`, `DryRunJson`, `Apply`, `ApplyJson` — that take a session, and
**no verb calls any of them.** The verbs call the sibling overloads that open a project themselves.

What it holds is a live `LcmCache` kept open across several commands so a `dry-run` followed by an `apply`
pays one project load rather than two. That is a real saving and there is nowhere to spend it: the CLI is
one process per invocation, and ADR 0040 settled that FieldWorks talks to Motif by running `motif --json`.

**The question.** Is there a verb coming that holds a project open across several operations — something
like `motif shell`, or a `--batch` that reads a proposal list — or is the session a shape left over from
the wire protocol that ADR 0040 removed? If nothing is coming, this is roughly 450 lines including its
tests.

**What makes it hard.** The tests are good tests. They cover pristine-rebuild counting and footprint gating,
which are real behaviours, and deleting them deletes coverage of code nothing runs.

---

## G2 — The Dry Run path that compiles, is tested, and cannot be reached

Production constructions, then test constructions:

| Module | prod | test |
| --- | --- | --- |
| `DryRunJobHandler` | 0 | 8 |
| `DryRunAssessmentPipeline` | 0 | 4 |
| `MachinePanGlossQueue` | 0 | 5 |
| `ProposalRepository` | 0 | 9 |
| `ReportRepository` | 0 | 1 |
| `ProjectWorkspaceEvictor` | 0 | 1 |

`ProposalRepository` is not merely unconstructed — the string does not appear anywhere in `src/` outside its
own file. The CLI's Proposals live in `ProposalStore`, on disk. So the database-backed Proposal path is
written, tested, and read by nobody.

The runner has exactly one registered job kind, `baseline-refresh`. `dry-run-assessment` was put out of
scope of the integration spine plan on the grounds that it follows once the loop has one working kind. The
loop now has one working kind.

**The question.** Register `DryRunJobHandler` and make this reachable, or move it to a branch until there is
a caller? Compiled-and-tested-but-unreachable is the worst of the three: it costs build time and review
attention, it reads as working, and the first person to wire it will find out how much of it still holds.

**Related.** `SIL.Motif.Launcher` is referenced by `SIL.Motif.Cli.csproj` and mentioned by no CLI source. It
exists to start a worker and connect to it, and the connecting half went with the wire protocol. Its fate
follows this answer.

---

## G3 — `JobRepository`'s methods with no production caller

After the `JobClaims` extraction the repository is 719 lines and 28 public members. These have no caller in
`src/` outside the repository itself:

| Method | prod | test |
| --- | --- | --- |
| `RequestCancellation` (both overloads) | 0 | 8 |
| `UpdateProgress` | 0 | 2 |
| `ListAttempts` | 0 | 17 |
| `ListArchived` | 0 | 2 |
| `ListEligibleArchived` | 0 | 4 |
| `PurgeArchived` | 0 | 1 |
| `DeleteArchived` | 0 | 4 |

Two distinct stories, and they may deserve different answers.

**Cancellation** is half-wired: `JobRecord.CancellationRequested` is read — `motif jobs show` prints it —
but nothing ever sets it, because no verb calls `RequestCancellation`. The CLI API owes a `motif jobs
cancel`. That is a small verb over an already-tested method.

**Archive retention** is a complete engine — a policy type, eligibility, purge, delete — with no scheduler
and no verb. `BaselineRetentionCleaner` is in the same position on the Baseline side.

**The question.** Wire cancellation as a verb and delete retention until something schedules it? Wire both?
Delete both? The 17 `ListAttempts` calls in tests are worth looking at before deciding — a method used
heavily by tests to inspect state and never by production is a test-support method wearing a repository
method's clothes.

---

## G4 — Is the work lease derived or cached?

`ProjectRuntime` caches an `IDisposable? _workLease` and offers five ways to bring it up to date:
`RefreshWorkLease`, `TryRefreshWorkLease`, `RefreshWorkLeaseDuringRecovery`, `RefreshWorkLeaseCore`, and the
implicit refresh inside `HasActiveWork`. They differ in whether they take an operation lease first and in
whether they may block.

The integration spine hit this. A real runner would not exit after draining its queue, because the runtime
held a work lease until something asked it to re-check; the fix was a `runtime.RefreshWorkLease()` call in
`Program.DrainAsync` with a comment explaining that the process otherwise never idles. That is an obligation
on the caller stated only in a comment — the interface does not say it, and the type cannot enforce it.

**The question.** Make the lease derived, so `HasActiveWork` answers from the durable rows every time and
there is nothing to refresh — paying a query per check? Or keep the cache and put the obligation in the
type, so a caller that finishes work cannot forget?

**What makes it hard.** The query is against the same database the runner is writing to, and `HasActiveWork`
is on the shutdown path, which is exactly where a surprising blocking call hurts. Measure the query before
assuming derived is affordable.

---

## Order

G2 first. It is the largest body of code, its answer decides the Launcher's fate, and both G3's cancellation
half and G4's cost model change depending on whether a second job kind is coming.
