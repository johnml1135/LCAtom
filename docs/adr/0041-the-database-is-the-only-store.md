# ADR 0041 — The database is the only store, and the runner sweeps every project

**Status:** accepted, 2026-08-28. Completes [ADR 0036](0036-motif-has-its-own-data-store.md) by removing
the file store it was meant to replace, and deletes the migration path that was meant to get there.
Follows [ADR 0040](0040-one-api-the-cli.md): its decision 7 already settles which verbs may touch a live
project, and its decision 5 leaves the Launcher without a job. ADR 0039's Baseline, live-host authority,
per-project queueing and PanGloss bounding remain binding.

**In plain terms:** Motif kept a linguist's proposed changes in a folder of JSON files whose location
depended on which directory you happened to be standing in, so the same project could show you different
work from two different terminals. It also had a second, better home for them — a database sitting beside
the language project — which was fully built and which nothing used. This ADR deletes the folder, keeps
the database, and writes no migration between them: Motif is pre-alpha and there is no stored work worth
carrying across. Because a Proposal now belongs to a project rather than to a directory, every command
must say which project it means. And because Motif's background worker can now be handed several
projects, it keeps a small list of the ones it has seen and checks each of them for work.

## Context

**A Proposal's address was a working directory.** `--store` defaulted to `./.motif`. A Proposal is *about*
a project, but where it lived was about where the command ran, and nothing bound the two: `motif dry-run
<id> --project <fwdata>` took both and never checked they belonged together.

**The destination was already built and already unreachable.** The paired database's schema carries
`Proposals`, `ProposalRevisions`, `Drafts`, `Decisions`, `Receipts`, `Reports` and `AppliedIndex`.
`ProposalRepository` implements them in 290 lines. `SqliteCorpusStore` implements `ICorpusStore` against
`Corpora` and `CorpusDocuments`. Neither is referenced anywhere in `src/` outside its own file — the string
`ProposalRepository` does not appear in the rest of the source tree at all. Every verb calls the file
implementations instead.

**The migration between them had never worked.** `store-cutover` imports the file store into the database
and archives the sources. Measured against the real executable on a Proposal carrying one `add-set-gloss`:

```
$ motif store-cutover --project Probe.fwdata --store .motif
error: Unknown operation kind 'lexical/lexSense/setGloss'.
```

`OperationKindRegistry` ships two hardcoded kinds; the rest are registered by `SIL.Motif.Runner`'s module
initializers. `Commands`' static constructor forces that assembly to load, and its own remarks claim this
"makes registration independent of which command runs first." It does not: `store-cutover` dispatches
straight to `StoreCommands` and never touches `Commands`. The migration refuses every Proposal the CLI can
author, and always has.

**One runner, one project.** `Program.Main` reads a project from argv, drains it, and idles. There is one
runner per user, guarded by a named mutex. A job enqueued into a second project's database while that
runner is alive is claimed by nobody, because the runner has no list of projects to look at.

**The queue has no order.** `Jobs` has no priority or position column, and the claim reads
`ORDER BY UpdatedUtc LIMIT 1` — last-modified, not position. It behaves as FIFO by accident, and any write
to a queued row silently reorders it.

## Decision

### 1. Proposals, Drafts, Corpora and Reports live only in the paired project database

The file store is deleted, not migrated. `ProposalStore`, `FileCorpusStore` and every verb path that reads
them go; `ProposalRepository` and `SqliteCorpusStore` become the implementations the CLI uses.

**No migration code is written.** `store-cutover`, `ProjectStoreCutover`, `FileProposalStoreMigration`,
`LegacyBulkStoreMigration`, `PendingSourceArchive` and the `MigrationLedger` table are deleted outright.
Motif is pre-alpha, ADR 0021 decision 5 already declares stored Proposals non-portable across the churn
window, and regeneration is the remedy it names. Writing a migration would mean first repairing one that
has never worked, in order to carry data that is declared disposable.

### 2. Every verb names its project; `--store` is deleted

`Project.motif.db` is derived from `--project <fwdata>`, so a verb that touches Motif's state must be told
which project it means. Twenty-three verbs gain a required flag.

Discovery — walking up from the working directory to find a single `.fwdata` — is rejected. It reinstates
the property this ADR exists to remove: an answer that depends on where the caller is standing. So is a
stateful `motif use <project>`, and an environment variable, for the same reason. The callers that matter
are FieldWorks and unattended agents; both hold the project path already and would pass it every time.

The cross-project job verbs are the one exception, because they span projects by definition. They resolve
through the machine store instead.

### 3. A Draft is a lifecycle state of a Proposal, not a second store

The `Drafts` table is dropped. `Proposals` gains a `DraftName`, `CurrentIntentDigest` becomes nullable, and
`finalize` is a status transition that writes the first `ProposalRevisions` row.

The file store needed two places for a structural reason: `objects/` is content-addressed and immutable, so
a mutable work-in-progress could not physically live there. The database has no such constraint —
`ProposalRevisions` already carries the immutability and `Proposals` is already the mutable pointer, which
is the same object/ref split. `Commands.New` already mints the `ProposalId` when the draft is created, so a
draft is an identified Proposal with no committed revision. Keeping a separate table would preserve a
workaround for a constraint that no longer exists and would make `finalize` a two-table move that can
half-fail. Drafts appear in `list`, marked: unfinished work that is invisible is the worse failure.

### 4. A machine store holds what is not about any one project

A second SQLite database in the worker root holds `KnownProjects` — the canonical `.fwdata` path, workspace
key and last-seen time of every project this installation has been pointed at — and `Usage`, the surface's
own usage log that ADR 0021 decision 4 requires.

It is a database rather than a configuration file because of the usage log, not the registry. Every
invocation appends a usage row and several invocations may run at once; two processes appending to one file
interleave, and the lock that fixes it is the lock SQLite already has. The registry then maintains itself:
since decision 2 gives every verb a project, each invocation upserts it on the way past, so there is no
`motif register` verb and no way to be stale by omission. A registered project whose file has gone is
dropped by the next sweep rather than raising an error.

**What does not move into it.** PanGloss machine capacity stays two OS named mutexes
(`Global\MotifPanGlossSlot-0`, `-1`), and the runner singleton stays a named mutex. The kernel releases a
mutex when its holder dies; a database row would need a lease and an expiry sweep, which is strictly more
machinery for a weaker guarantee.

### 5. The runner sweeps every known project, and stays a pseudo-daemon

Each poll tick the runner reads `KnownProjects`, opens each project's paired database, and claims work.
Measured against the real `ProjectDatabaseCatalog.OpenOwned` and `JobClaims.Claim` over ten separate paired
databases: **5.66 ms per open-and-claim, 56.6 ms for a sweep of ten projects.**

That measurement rejects the alternative — a wake table in the machine store that the CLI writes on enqueue
so the runner opens a project only when told to. It buys tens of milliseconds and costs a second place
where "is there work?" is answered, whose failure is a hint that goes missing and a job that sits
unclaimed forever.

The runner is not a service. It idles out after its timeout, and the CLI restarts it by spawning
unconditionally after enqueueing — the named mutex makes that a no-op when one is already running, so no
protocol is needed. One race must be closed explicitly: if the running instance is inside its final idle
tick, the spawned process fails to acquire ownership, exits, and the first exits too, stranding the job
until the next command. The spawned process therefore retries acquisition for a short window rather than
exiting immediately.

### 6. Jobs are globally ordered, and the order is a column

`Jobs` gains `QueueOrder REAL`, defaulting to enqueue time in epoch milliseconds. The claim orders by it,
and the sweep becomes a k-way merge: peek the head of every known project, claim the globally first. So
`jobs list --all` shows the order work will actually run in, across projects, and `jobs move` means what it
says everywhere.

Fractional indexing rather than swapping neighbours' positions, because the neighbour usually lives in a
*different project's database* and no transaction spans two SQLite files. A half-completed swap leaves two
jobs claiming one position; giving the moved row a value between its new neighbours writes exactly one row.

Verbs: `jobs list --all`, `jobs cancel`, `jobs requeue`, and `jobs move <id> --before <id> | --to-top |
--to-bottom`. Single-step move is rejected: dragging a job up a forty-job queue should not be forty
invocations.

### 7. `dry-run` becomes a job; `apply` stays a synchronous verb

`motif dry-run` enqueues against the published Baseline and prints a job id. The in-process path that
loaded the live project, saved it, and copied the file to a scratch is deleted.

ADR 0040 decision 7 already recorded this split — *"Dry Runs use a saved Baseline and never open the live
project… Only Baseline capture and Apply need the project itself, and both are deliberate acts at a save
boundary."* The code had two Dry Runs measuring against two different things, which is the worst of the
available outcomes: two answers to "what would this Proposal do" that drift apart silently.

`apply` cannot become a job. A runner meets `LcmFileLockedException` the moment FieldWorks holds the
project, which is exactly `BaselineRefreshOutcome.ProjectInUse`. It stays one invocation at a save
boundary, per ADR 0040 decision 7.

Consequently `CliSession` is deleted. Its four `Commands` overloads were `DryRun`, `DryRunJson`, `Apply`
and `ApplyJson`; dry-run leaves the process and apply is a single invocation, so nothing holds a project
open across commands. The Launcher is deleted with it: ADR 0040 decision 5 ships the CLI and runner as one
artifact at one version, so `InstalledWorkerCatalog`'s version selection answers a question nobody asks.

### 8. Finished jobs are capped at 500 per project, and never expire by age

A job row carries `InputJson` — the whole Proposal — and `DryRunJson` — the whole Dry Run. Once every dry
run is a job, these are not receipt-sized rows, and every terminal transition already sets `ArchivedUtc`
while nothing has ever purged. The bound is a count, not a duration: the last 500 finished jobs per
project, as a constant rather than a knob. Queued, running and waiting rows are never counted and never
purged.

The existing eligibility rule survives unchanged, and is the reason the engine is kept rather than
rewritten: an attempt is not purged while a later attempt in its lineage is still live, which is what
`HasLaterAttempt`'s production callers depend on.

### 9. What stays a file, and why

Baseline bundles, Dry Run scratch copies, the PanGloss workspace, and the `.fwdata` itself. Each is opened
by a program that is not Motif: LibLCM opens a project from a directory, and PanGloss is a separate
executable reading a directory. Storing them as blobs would mean extracting them to a directory before
every use.

The rule is therefore not "database over files". It is: **anything only Motif reads lives in a database;
anything another program must open stays a file, and the database records its path and digest.** That is
what `Baselines` already does.

## Consequences

- Two Proposals for one project can no longer exist in two directories. The failure it replaces — a
  required flag on twenty-three verbs — is loud and immediate.
- Every test that drives a Proposal through the CLI gains a project fixture. Thirteen test files.
- `dry-run` requires a published Baseline. `JobStatus.WaitingForBaseline` and `BaselineRefreshBarrier`
  exist for exactly this, but a Proposal's first dry run on a fresh project is now two jobs, and the first
  is the slow one.
- A second schema exists and must be versioned. `MotifSchema` currently assumes one shape.
- Idleness becomes a property of the sweep loop rather than a cached lease, so a bug in the sweep shows up
  as a runner that never exits rather than one that exits early. That is the better failure of the two, and
  it is still a failure.
- Roughly 1,400 lines of migration code and 1,000 lines of its tests are deleted, along with `CliSession`
  (~450 with tests) and the Launcher. None of it was reachable from a verb.

## Rejected alternatives

- **Keep the file store and delete the database columns.** It would mean deleting `Baseline` and `Drift`
  from the glossary, since a live-file-copy Dry Run participates in neither.
- **Keep both, and migrate.** Rejected in decision 1: the migration has never worked, and the data it
  would carry is already declared disposable.
- **Discovery, `MOTIF_PROJECT`, or `motif use`.** Rejected in decision 2; all three reinstate an answer
  that depends on the caller's location.
- **A wake table so the runner opens only projects with work.** Rejected in decision 5 on the measurement.
- **A duration-based retention policy.** Rejected in decision 8: a count bounds the file without a
  parameter anybody has to reason about.
- **Machine capacity and the runner singleton as database rows.** Rejected in decision 4; a named mutex is
  released by the kernel when its holder dies.

## Amendments

### 2026-08-28 — Corpora were already in a database, just the wrong one

The Context above says `SqliteCorpusStore` is "not referenced anywhere in `src/` outside its own file". That
is true of `ProposalRepository` and **false of the corpus store**. Measured:

```
new FileCorpusStore(    → 0 in src/, 4 in tests/
new SqliteCorpusStore(  → 1 in src/:
    CorpusCommands.StoreFor(storeDir) => new SqliteCorpusStore(Path.Combine(storeDir, "motif.db"))
```

`FileCorpusStore` is the dead one. Corpora already live in SQLite — but in a **store-local** `motif.db`
under whichever `.motif` directory the caller resolved, which is a third database beside the project's
paired one and the machine store. Confirmed by running the real executable: `motif add-corpus` from a
working directory wrote `<cwd>/.motif/motif.db`.

The claim was written from a stale reading taken before this ADR's own Task 1 landed, and it was not
re-checked. Decision 1 is unaffected in substance — corpora still end up in the paired project database —
but the work is not "swap the file implementation for the SQLite one". It is **repoint the SQLite one at
the paired project database**, and delete `FileCorpusStore` and `CorpusStoreMigration`, neither of which
has a production caller.

Rule adopted: **re-measure a "nothing references this" claim against the tree the work will actually run
on**, not against the tree the claim was first written on.

### 2026-08-28 — `promote-gloss` has a window where it cannot see a corpus

`Commands.PromoteGloss` takes one store directory and uses it for both its Proposal store and its corpus
lookup. Once decision 2 derives that directory from `--project`, promote-gloss reads
`<project dir>/.motif/motif.db` while `add-corpus` still writes `<cwd>/.motif/motif.db`, so a corpus added
from a working directory is invisible to it.

The gate does not see this: `PromoteGlossTests` drives the in-process `Commands.PromoteGloss(storeDir, …)`
and passes one directory to both, so the two agree in the test and disagree only through the CLI.

The window opens when decision 2 lands for the Proposal verbs and closes when the corpus verbs follow. It
is recorded rather than patched because the patch — restoring a separate corpus-store flag — would add back
the flag this ADR deletes, for one intermediate commit.
