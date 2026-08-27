# ADR 0039 — One worker coordinates saved Baselines; the live host remains authoritative

**Motif can evaluate several Proposals without repeatedly locking or copying a user's open project.**
One local worker owns Motif's durable workflow, while FieldWorks or the CLI host remains the only owner
of the live language project and performs Apply immediately.

**Status:** accepted, 2026-08-22; **partly superseded 2026-08-26 by
[ADR 0040](0040-one-api-the-cli.md)**. Decisions 1 and 8 — the named-pipe protocol, its clients, and wire
capability negotiation — are withdrawn: there is now one API and it is the CLI, and the FieldWorks boundary
in decision 2 is a process boundary rather than an in-process one. **Decisions 2 through 7 and 9 remain
binding**: the saved Baseline, live-host authority, per-project live-access queue, PanGloss bounding,
synchronous Apply with one-use authorization, explicit reconciliation, the single sibling SQLite database,
and media behaviour are all still the contract. This ADR supersedes ADR 0030 decisions 1 and 2, ADR 0036
decision 6, and the long-lived CLI process mechanism in ADR 0020 decision 1. It preserves their
one-live-writer, content-identity, and cross-runtime constraints. The complete protocol and lifecycle
contract is in
[the worker and Baseline design](../superpowers/specs/2026-08-20-baseline-dry-run-session-design.md),
itself partly superseded.

## Context

The first CLI implementation proved that a real file-backed LibLCM scratch preserves writing-system
collation and valid-character behaviour, while a memory-only copy does not. It also proved that one saved
project can support many Dry Runs. Holding the live project open for every Dry Run, however, prevents
FieldWorks and the CLI from doing useful work concurrently and turns a reusable evaluation input into a
long-lived lock.

FieldWorks will eventually load `SIL.Motif.Runner` in-process and hand it the `LcmCache` that FieldWorks
already owns. The CLI uses the `net10.0` Host when it owns a closed project. Neither arrangement permits an
out-of-process worker to own or receive an `LcmCache`; it needs a durable, file-backed handoff instead.

The workflow also needs work that outlives a command: Proposal authoring, queued Baseline refreshes, Dry
Runs, PanGloss Assessments, cleanup, and recovery after interruption. Multiple installed clients may carry
different Motif product versions, but allowing each to start a database-owning process would create a more
dangerous compatibility problem than rejecting an incompatible client.

## Decision

### 1. One on-demand worker serves all projects for one Windows user

A single `net10.0` Motif worker owns the user's Motif databases, durable jobs, per-project queues,
Baselines, temporary work, and PanGloss orchestration. The CLI and the future FieldWorks adapter are clients
over a local named-pipe JSON protocol. Large Baseline transfers use a separate binary pipe so the control
protocol stays bounded.

The worker starts on demand and exits after an idle period. Active commands and any queued, running, or
waiting work keep it alive. It holds no idle `LcmCache`; a host opens a project only while it owns that
project's FieldWorks lock and releases it as soon as the live operation finishes.

The worker is an internal local process, not a network service or a third user-facing product. FieldWorks'
installer installs the `net10.0` runtime explicitly. The in-process Runner and FieldWorks client package
remain `netstandard2.0` so `net48` FieldWorks can load them.

### 2. The live host and the saved Baseline have different authority

The process holding the loaded live `LcmCache` is the only live-project authority. While FieldWorks has a
project open, the CLI may author Proposals, inspect workflow state, and run work against an existing
Baseline, but it may not independently open, save, refresh, or apply to that live project.

A Baseline is a minimal file-backed bundle containing exactly the LibLCM and writing-system state needed to
reproduce engine behaviour. It excludes linked media bytes. FieldWorks creates it at a safe edit boundary:
save the project and writing systems, stream the bundle to worker-owned temporary storage, then let the
worker verify and atomically publish it. When FieldWorks is closed, the `net10.0` Host performs the same
capture while it owns the project lock.

A Baseline persists until explicit replacement or reference-aware cleanup. Its identity binds project
identity, semantic state, projection version, and bundle hashes; its timestamp explains “run against the
project as of X” but does not define validity. FieldWorks reports its edit generation and dirty state, while
a CLI-hosted probe compares the saved semantic digest when it can acquire the lock. If neither check is
available, Motif says currentness was not checked. Live edits do not cancel an already-running Dry Run; they
produce a prominent warning that its result describes the older Baseline.

### 3. Live access is queued per project; PanGloss work is separately bounded

Each project has one ordered live-access lane for Baseline refresh, Dry Run preparation, and candidate
export. A refresh is a barrier: Dry Runs ordered before it use the old Baseline and those ordered after it
use the replacement. A requested refresh waits when FieldWorks owns the live model; if FieldWorks closes,
the worker may acquire the released project on the next opportunity and complete it automatically.

Apply is not a queued job. It waits for the project gate for at most five seconds, with priority over work
that has not started, and then either applies synchronously or refuses as busy. Work already using the gate
finishes normally.

PanGloss exports all files it needs from the candidate scratch and owns that private interchange format.
Each Proposal Assessment builds a fresh engine, keeps it only in memory, stores only the result and a bounded
log, and deletes its export immediately at completion or on the next worker startup. At most two PanGloss
process trees run on one PC, each capped at 25 percent of total CPU for its complete build and analysis.
Each user worker schedules its projects FIFO. Machine-wide leases enforce only the two-process capacity
across Windows sessions; ordering between different users is unspecified.

### 4. Jobs are durable and async; Apply is synchronous

Long operations return a job id by default and support status, wait, and cancellation; the CLI offers
`--wait`. Durable authoring state is written immediately rather than relying on worker memory. Startup clears
all ephemeral work, marks interrupted runs, and retries safe infrastructure interruptions from fresh inputs
at most three times with backoff. Deterministic parser failures are not retried automatically.

Queued jobs may be cancelled. A running Dry Run publishes no partial result; PanGloss is terminated; a
Baseline refresh may be cancelled until atomic publication. Apply may be cancelled only before mutation
begins.

### 5. Apply requires exact evidence and a one-use authorization

Without an approved Decision bound to the current Proposal intent, Apply refuses. FieldWorks may present
“Approve and Apply” as one user action, but it durably records the Decision first. A completed Assessment is
advisory even when its grammar result is poor. A missing, pending, cancelled, or tool-failed Assessment
requires explicit `--force`; force acknowledges unavailable parser evidence and bypasses no safety check.

The worker issues an opaque, short-lived, one-use Apply Authorization bound to the project, Proposal intent,
Dry Run, Baseline, Decision, Assessment disposition, and attempt id. When the client presents it, the worker
verifies and consumes it once, then sends immutable verified claims to the live host. The host passes those
claims with its own `LcmCache` to the Runner. The Runner performs the final live Preflight, applies one LibLCM
unit of work, and writes the project's applied-log entry in that unit. The host saves and reports the Receipt.
A disconnect is never an instruction to retry Apply.

### 6. The language project and Motif store reconcile explicitly

Whenever a live host registers or lists workflow state, it sends the project's applied-log delta or
watermark. Positive applied entries repair the Motif database, complete known attempts, and reconstruct the
minimal Receipt; unknown Proposal ids become tombstones. A disagreement becomes a derived Conflict shown at
the top of the main list with a deterministic explanation. It blocks only the affected Proposal and its
dependents until a person resolves it.

### 7. One sibling SQLite database holds local workflow state

The durable pair is `Project.fwdata` and `Project.motif.db`. The database contains Proposals, revisions,
Decisions, jobs, Assessments, Reports, Receipts, Corpora, and the applied index. Immutable intent and evidence
retain content-digest identity even though their storage container is SQLite. FieldWorks-managed moves carry
the database; a managed duplicate starts fresh. A copied database found under a mismatched locator is moved
to worker-owned `unclaimed/` quarantine and replaced by a fresh sibling rather than silently re-associated.

Derived state lives under `%LOCALAPPDATA%/SIL/Motif/<project-key>/`. The key includes the normalized full
project path and FieldWorks project identity, so same-identity clones in different folders do not collide.
A path move starts a new derived folder; abandoned derived folders are evicted after 30 days on worker
startup. This cleanup never deletes `.fwdata` or `.motif.db`.

Terminal Proposals archive immediately and are hidden from the main list. Terminal jobs are also hidden.
Archived state is retained locally for 30 days by default, configurable through forever, and may be deleted
manually. Stale nonterminal work may remain indefinitely. A minimal applied record remains in both the
`.fwdata` applied log and the database after archive cleanup.

### 8. Clients negotiate wire compatibility, not product-version equality

FieldWorks and the CLI install immutable versioned worker directories. A stable launcher selects the newest
installed worker compatible with the connecting client's wire-protocol interval and required capabilities.
Product SemVer is reported for diagnostics but does not decide compatibility. The JSON protocol evolves
additively and ignores unknown properties where the contract permits.

Only the selected worker opens or migrates SQLite. Each database records an application id, schema
generation, and minimum worker version. Upgrades are transactional; older workers refuse a newer schema and
never downgrade it. Incompatible clients fail clearly. Side-by-side workers do not share a database, and an
initial broker or multi-generation database layer is out of scope. This follows the compatibility strategy
documented in [the versioning research](../research/2026-08-22-multiple-client-versions-one-worker.md).

### 9. Media behaviour matches FieldWorks without storing media

Deleting a model object has the same logical cascade FieldWorks gives that operation, including deletion of
owned media-reference objects. Motif does not copy, archive, restore, author, or delete linked picture,
audio, video, or other external bytes. Adding or replacing media is deferred until a separate storage and
retention contract exists. [The initial linked-media boundary](../media-boundary-spec.md) defines the current
operation-family admission gate.

## Amendments

### 2026-08-27 — a refused refresh is not completed later

Decision 3 ends: *"A requested refresh waits when FieldWorks owns the live model; if FieldWorks closes, the
worker may acquire the released project on the next opportunity and complete it automatically."*

That was the payoff of the deferred answer in the live-host exchange, and
[ADR 0040](0040-one-api-the-cli.md) removes the channel that exchange ran over. Under
[the refresh barrier design](../superpowers/specs/2026-08-27-baseline-refresh-barrier-design.md) a refresh
attempts the project's own file lock instead of negotiating: free, it captures; held, it is **refused as
busy** and not remembered. Nothing completes later on its own, and a caller that wants that retries.

**Everything else in decision 3 stands.** The refresh is still a barrier, Dry Runs ordered before it still
use the old Baseline and those after it the replacement, and PanGloss is still separately bounded — none
of that depended on the conversation, only on the per-project lane, which outlived the wire.

The reduction is deliberate and worth naming: a user with FieldWorks open used to be asked to release the
project and could agree. Now they close it, or the refresh waits. Restoring that as a FieldWorks-side
prompt that runs `motif` afterwards is a question for that surface rather than for this barrier.

## Consequences

This design makes repeated evaluation cheap and concurrent while keeping live-project writes under the host
the user already trusts.

- One save can support many independent Dry Runs without keeping the live project locked, duplicating the
  whole project for every run, or retaining many live caches.
- A Dry Run and an Assessment can overlap later live work because PanGloss receives an exported candidate,
  not the `LcmCache` or a durable engine.
- The paired database is local workflow state, not a competing authority for linguistic data. The `.fwdata`
  and its live `LcmCache` remain authoritative for the language project.
- The existing one-shot CLI, file proposal store, and warm `CliSession` are useful proofs, not the final
  process boundary. Migration must preserve their contract behaviour while moving ownership to the worker.
- FieldWorks integration is still a distinct implementation phase, but the cross-runtime package and pipe
  protocol are now part of the CLI-first architecture rather than a later replacement.
- The architecture deliberately accepts clear incompatibility refusal instead of concurrent database owners.

## Rejected alternatives

The rejected designs either lose FieldWorks behavior, retain too much expensive state, or introduce competing
owners for one project and database.

- **Call the reusable operation Preflight.** Rejected because it executes the work on a throwaway state and
  reads it back; Dry Run is the clearer name. Preflight is reserved for the final live comparison.
- **Pass an `LcmCache` to the worker.** Rejected because it cannot cross a process boundary and the owning host
  must retain lifecycle and persistence authority.
- **Use a memory-only Baseline.** Rejected because it drops file-backed project configuration that affects
  observable LibLCM behaviour.
- **Keep PanGloss engines between Proposals.** Rejected because a fresh candidate grammar is the required
  input and engine retention adds large, invalidation-prone state.
- **Queue Apply.** Rejected because the user's Decision and evidence must be checked against the live project
  while the caller remains connected; Apply succeeds now or refuses now.
- **Run one worker per project or per product version.** Rejected because it duplicates scheduling state and
  creates competing SQLite owners.
