# Motif worker, Baseline, Dry Run, and Apply architecture

Motif should let people and AI tools build and evaluate Proposals without repeatedly opening or copying a
FieldWorks project. FieldWorks remains usable during that background work, while every live-project change
still passes through the one process that currently owns its `LcmCache`.

This specification replaces the earlier session-only design in this file. It designs the local CLI-first
worker and the compatible FieldWorks boundary. It does not implement them.

## Decision summary

One on-demand Motif worker runs per logged-in Windows user and serves every project that user's FieldWorks and
CLI clients name. It is the only writer of each sibling `Project.motif.db`, owns durable job queues, and
schedules project work without retaining an idle `LcmCache`.

One saved FieldWorks state becomes a private, minimal, file-backed **Baseline**. Many **Dry Runs** may reuse it.
A Dry Run performs the same LibLCM lowering, mutation, and read-back as Apply against a throwaway cache, then
discards the mutations. **Preflight** is the final non-mutating comparison against the live project immediately
before Apply. **Assessment** means only an immutable PanGloss parser run.

FieldWorks and the CLI may both use Motif at once, but only one host owns live-project operations. While
FieldWorks has a project open, its `netstandard2.0` adapter is that host. The CLI may still author Proposals and
run background work against an existing Baseline, but it may not independently open, save, refresh, or Apply
to that live project. As soon as FieldWorks releases the LibLCM project lock, the worker may acquire it for a
CLI-hosted live operation.

Media authoring is outside this scope. Motif follows normal LibLCM model semantics when deleting an owner that
has media references, but it never copies, creates, replaces, moves, restores, or deletes linked file bytes.

## Components and authority

### One per-user worker

The `net10.0` worker is an on-demand local process. Either FieldWorks or the CLI may start it. It exposes one
named-pipe endpoint, opens project databases lazily, and exits after global idle. An inactive client connection
does not keep it alive; active commands and queued, running, or waiting work do. That includes a refresh
waiting for FieldWorks to release the project, so the worker can acquire it immediately after FieldWorks exits.

The worker owns:

- all writes and migrations for every `Project.motif.db` it opens;
- durable Proposal-authoring commands and workflow records;
- per-project Baseline and LibLCM queues;
- the machine-wide PanGloss queue and process limits;
- job status, progress, cancellation, retry, archive, and cleanup;
- Apply Authorization and reconciliation state.

It does not scan for FieldWorks projects, keep every database open, or hold a live or Baseline `LcmCache` while
idle. A request always names its project.

### FieldWorks adapter

FieldWorks consumes a Motif integration NuGet targeting `netstandard2.0`. The adapter:

- uses the `LcmCache` FieldWorks already owns;
- invokes the Motif Runner in-process for preflight and Apply;
- marshals work onto FieldWorks' safe UI/edit boundary;
- saves and exports a consistent Baseline when FieldWorks authorizes refresh;
- connects to, starts, and negotiates with the worker;
- reports a monotonically increasing edit generation and whether the live project has unsaved changes;
- reports project applied-log entries and completed Receipts to the worker.

The `LcmCache` never crosses a process boundary. The adapter passes it directly to the Runner.

### CLI client and CLI-hosted live operations

The CLI is primarily a named-pipe client. It returns immediately with a job ID for long work, with explicit
`job status`, `job wait`, cancellation, and `--wait` surfaces. Proposal authoring and ordinary database reads
remain immediate commands.

When FieldWorks is not hosting the project, the worker may perform a requested live-project operation by
opening the `.fwdata` itself. When FieldWorks is hosting it, the CLI may:

- start, amend, inspect, and withdraw Proposals;
- inspect jobs, history, reports, and conflicts;
- request a Baseline refresh for FieldWorks to consider;
- queue Dry Runs and Assessments that can use available evidence.

It may not independently open or mutate the live project. A Dry Run with no usable Baseline becomes
`waiting-for-baseline`; it does not make FieldWorks save behind the user's back.

## Local transport

Clients and the worker use a local named-pipe JSON control protocol. Each connection has an independent event
read loop as well as correlated request and response messages. An active FieldWorks adapter registers itself
as the live host of a project, and worker events request Baseline capture, Apply, reconciliation, or
cancellation without requiring the adapter to poll.

Every event that requests work has an event ID and exactly one correlated result envelope. A refresh result
records the presentation response (accept, defer, or decline) and, when accepted, the terminal capture outcome
and optional Baseline Token. Apply results carry a Receipt, refusal, or
reconciliation-needed attempt; reconciliation results carry the new watermark; cancellation results say
whether the boundary accepted it. The worker completes the initiating job or connected CLI Apply only from
that result. Host disconnect before a result follows the operation-specific interruption rule and never
fabricates success.

The protocol includes:

- a version and capability handshake;
- client, request, job, Apply-attempt, and idempotency identifiers;
- structured status, progress, errors, waits, and cancellation;
- a project locator on every project-scoped command;
- explicit command and response schemas that ignore unknown object properties within a compatible protocol
  generation.

Large Baseline bytes are not base64 fields in control JSON. The worker creates and owns a separate
bounded-memory binary-pipe server, then sends a one-use transfer offer containing the transfer ID, direction,
maximum byte count, expiry, and unpredictable pipe name. FieldWorks connects once and streams the saved
minimal Baseline bundle directly into a worker temporary file while both ends hash it. After the stream closes,
FieldWorks sends a correlated completion message containing its byte count and digest. The worker publishes
only if those values equal its independently computed values. It rejects excess, expired, duplicate,
incomplete, or digest-mismatched transfers and deletes the unpublished temporary file.

The protocol is local-user only. It is not a network API, a review server, or a way to transfer a live
`LcmCache`. Every control and binary pipe has an explicit Windows ACL granting the owning user SID and the
required system account only; it rejects other local users and remote clients. Unpredictable names and
handshake identifiers are defense in depth, not authorization.

## Multiple installed Motif versions

Product version and wire-protocol version are different facts. FieldWorks may ship Motif 3.2.0 while a
standalone CLI installs Motif 3.4.2.

FieldWorks and CLI installers place worker binaries in immutable versioned directories and register them with
a small stable launcher. The launcher starts the newest installed worker whose supported protocol interval
covers the maintained client window. It leaves the data path after startup.

Each connection independently negotiates:

- client identity and informational product version;
- minimum and maximum wire-protocol version;
- optional capabilities;
- the selected protocol and effective capabilities.

A newer worker may therefore serve an older FieldWorks adapter and a newer CLI concurrently, exposing only
the capabilities each understands. Additions within a protocol generation are optional and JSON readers
ignore unknown object properties. Existing meanings do not change inside a generation; new required behavior,
new enum meaning, or incompatible shapes require a new protocol generation.

Only the selected worker opens or migrates project databases. Each database records its application ID,
schema generation, and minimum worker generation. Migrations are transactional. A worker refuses a database
newer than it understands, and no component performs an automatic downgrade. If no installed worker overlaps
the client and database requirements, startup fails with an actionable update or reinstall message; it never
starts a second incompatible database owner.

The primary-source comparison behind this design is in
`docs/research/2026-08-22-multiple-client-versions-one-worker.md`.

## Project and storage layout

A project named `Kalaba` has one obvious durable pair:

```text
Kalaba/
  Kalaba.fwdata
  Kalaba.motif.db
  WritingSystemStore/
  LinkedFiles/
  ...
```

The sibling filename stem and directory establish the normal association. People may see the database but do
not edit it. It holds Proposals, Decisions, jobs, archives, Corpora, Assessment results, Reports, Receipts,
conflict records, and a small applied index. This supersedes the file-store choice for Proposals and Receipts
in ADR 0036; a later implementation plan must include a deliberate migration.

Derived working data stays out of the FieldWorks project directory:

```text
%LOCALAPPDATA%/SIL/Motif/<project-key>/
  baseline/
  work/
  unclaimed/
```

The project key includes the normalized full `.fwdata` path and FieldWorks project identity, so copies with
the same internal identity in different folders never share a worker workspace. A new path gets a new local
folder; Motif does not migrate or infer reuse of an old Baseline. For a managed move, the worker closes the
database and grants one relocation token; FieldWorks moves the `.fwdata` and sibling database as a pair and
registers the new locator before the token expires. The worker persists that grant in its small per-user
locator journal outside the project database. On restart or token expiry it checks only the exact old and new
paths: if the pair exists at exactly one, it completes registration there; if both or neither contain the
expected pair, it records `relocation-blocked` and opens neither. A managed duplication excludes the database
and worker workspace, so the copy starts fresh. If an unmanaged filesystem copy includes a database whose
embedded locator does not match, the worker moves that copied database into its derived `unclaimed/` quarantine,
creates a fresh sibling database, and reports what happened. The quarantined copy follows the configurable
30-day unused-workspace eviction rule; it is never silently joined to the original workflow.

On worker startup, Motif clears every owned `work/` directory and removes local project workspaces unused for
30 days. The local eviction interval is configurable. The sweep checks exact owned paths and live worker
leases and never touches `.fwdata` or the active sibling `.motif.db`. An explicitly quarantined copied
database is derived data and follows its stated eviction rule. Normal terminal job handling clears that job's
work immediately; startup clearing is the crash-recovery backstop.

## Baseline identity and contents

A Baseline Token identifies one complete saved semantic state of one FieldWorks project:

```text
BaselineToken
  projectIdentity
  semanticSnapshotDigest
  projectionVersion
  capturedUtc
  bundleDigest
  capturedHostSessionId?
  capturedEditGeneration?
```

The semantic digest and projection version are authoritative. `capturedUtc` supports the human-readable
status “run against the project as of X” but does not establish identity. The optional host session and edit
generation support same-session freshness comparison and are also descriptive, not semantic identity.
`bundleDigest` detects local Baseline corruption. The Baseline Token is not an Apply Authorization.

The minimal bundle contains:

- the saved `.fwdata` file;
- the project's writing-system store;
- only additional small project-local configuration that equivalence tests prove LibLCM needs.

It excludes `.motif.db`, linked media, supporting files, backups, Send/Receive repositories, and unrelated
project-directory content. Empty directories may be created when LibLCM requires their presence.

The Baseline is file-backed because LibLCM's memory-only backend does not preserve project-specific writing
systems, including collation and valid-character behavior. File-backed means that one private Baseline file
exists; it does not mean FieldWorks closes, the live project remains locked, or every Dry Run writes another
copy.

## Baseline capture and refresh

### FieldWorks-hosted capture

FieldWorks owns the live cache for the lifetime of the open project. When it authorizes a queued refresh, its
adapter:

1. enters FieldWorks' safe edit boundary;
2. finishes the current unit of work and saves the LibLCM model and writing-system state;
3. streams the minimal Baseline bundle to the worker without building a second in-memory representation;
4. waits for digest verification and atomic publication;
5. releases the edit boundary.

No linked media enters the stream. If the connection, save, transfer, or verification fails, the worker
deletes the unpublished temporary bundle and leaves the previous Baseline available with its existing
freshness status.

### CLI-hosted capture

If FieldWorks is not hosting the project, the worker may acquire the LibLCM project lock, open and save the
project, capture the same minimal bundle, close the live cache, and publish it. If FieldWorks opens first, the
refresh returns to `waiting-for-project-host`.

### Refresh as a durable barrier

`refresh` always creates a durable request. While FieldWorks is open, the request is visible there and
FieldWorks decides how to present it. The user or host may accept it now, defer it, or decline it, and the
worker records the actor, response, reason, and time. A deferred request remains waiting; if FieldWorks
closes, the worker may automatically service it once the LibLCM lock is released. A declined request ends
without changing the current Baseline.

Refresh is an ordering barrier in the per-project LibLCM lane:

- earlier Dry Runs finish against the old Baseline;
- refresh captures and atomically publishes the new Baseline;
- later Dry Runs use the new Baseline;
- Apply before refresh makes the refresh capture the applied state;
- refresh before Apply captures the pre-Apply state.

A successful publication releases the barrier and assigns the new Baseline to later work. Decline,
cancellation, save failure, transfer failure, or verification failure fails that refresh attempt and does not
release later work onto the old Baseline. Later Dry Runs remain `waiting-for-baseline` until another refresh
succeeds, or the caller explicitly cancels and submits new work that names the old Baseline and accepts its
prominent stale/currentness warning.

If the worker acquires the project just before FieldWorks reopens, the already-started capture finishes and
releases the project immediately. PanGloss and subsequent Dry Runs never keep the live project locked.

The current Baseline remains reusable until explicit refresh or reference-aware eviction. It does not expire
because a process ended or time passed. A registered FieldWorks host reports a unique session epoch, a
monotonically increasing edit generation within that epoch, unsaved-change state, and the saved semantic
digest. Baseline capture records the epoch and generation. A later generation is comparable only in the same
epoch; a dirty host is known-old, and a new epoch uses the saved semantic digest instead of comparing counters.
After restart, or in a CLI-hosted status probe, Motif also compares the saved project's semantic digest when it
can briefly obtain the project lock. If neither observation is available, status says that currentness was not
checked; it never claims that the Baseline is current. Dry
Runs may continue against a known-old or unchecked Baseline with a prominent warning and the exact “as of”
token; Motif never silently refreshes it.

## Dry Run and PanGloss pipeline

Each Dry Run opens the unchanged Baseline, executes the validated prerequisite plan and requested Proposal on
a single-use file-backed scratch, reads back effects, and disposes it without saving. The next Dry Run starts
from the same unchanged Baseline. At most one Dry Run runs per project at a time.

Assessment is optional but on by default:

```text
motif dry-run <proposal>                  # Dry Run, export, then Assessment
motif dry-run <proposal> --no-assessment  # Dry Run only
```

`--no-assessment` warns that a later Apply will require `--force` unless a completed Assessment is attached
to this exact Dry Run and Proposal revision.

Dry Run and Assessment remain distinct durable records and job stages. After a successful default Dry Run,
Motif asks PanGloss to export every input it needs from the live candidate scratch. Once export completes, the
scratch `LcmCache` is disposed and the per-project LibLCM lane may begin the next Dry Run. PanGloss then builds
and assesses independently from its exported input.

Every new Proposal revision and every retry gets a fresh export and fresh PanGloss build. PanGloss builds its
engine in memory for one attempt. Motif never defines or persists an engine ID, engine cache key, resume token,
or saved-engine path. Only the immutable Assessment result and a bounded diagnostic log survive.

If Dry Run succeeds but export or Assessment fails, the Dry Run remains successful and the pipeline reports
`completed-with-assessment-failure`. Deliberately omitted Assessment reports `completed-dry-run-only`.
Cancellation during export or Assessment cancels the pipeline job but retains the already published Dry Run;
its Assessment disposition is `cancelled`, so Apply needs the same explicit force acknowledgement as any
other unavailable Assessment. A late Assessment remains attached to the exact Proposal intent digest and
Baseline Token it evaluated. Amendment, refresh, supersession, or even Apply does not relabel it as current
and does not discard its historical result.

## Scheduling and resource limits

The one worker maintains two kinds of execution capacity:

- one LibLCM lane per project for refresh, Dry Run, and candidate export;
- two PanGloss slots for the entire PC.

LibLCM work for different projects may proceed concurrently. A PanGloss job holds one global slot for its
entire build-and-analysis process tree. Each slot is capped at 25% of total machine CPU, even when it is the
only occupied slot, so two jobs together consume at most 50%. On Windows, Motif enforces the hard cap with an
OS Job Object around the complete PanGloss process tree rather than relying only on parser thread settings.

Each per-user worker uses one FIFO PanGloss queue across its projects. Machine-wide slot leases prevent
workers in different Windows sessions from exceeding the two-PC-job limit; ordering between different users'
queues is unspecified.

An exported candidate may wait on disk for its PanGloss slot, but no engine is saved. Completion, failure, or
cancellation deletes the export and workspace immediately.

## Durable jobs, cancellation, and retry

Long operations return a job ID. Job records and state transitions are durable in the associated
`.motif.db`; large temporary payloads are not stored in SQLite.

Proposal commands that need no LibLCM resolution may complete while FieldWorks owns the project. A composer
that must resolve live entities either uses the named Baseline and records its “as of” token or waits for a
live-host request; it never reads around the project lock or presents old resolution as current.

Cancellation boundaries are explicit:

- queued work is cancellable;
- a running Dry Run is cancellable and publishes no partial Dry Run;
- a running PanGloss Assessment is cancellable;
- refresh is cancellable until atomic Baseline publication;
- Apply is cancellable only during its non-mutating preparation and lock attempt, never after its Runner unit
  of work begins.

Worker startup clears ephemeral work, marks formerly running attempts `interrupted`, and requeues safe jobs
from their durable inputs. Infrastructure interruption receives at most three automatic attempts with short
increasing delays. A normal deterministic PanGloss failure is not automatically retried. Exhausted work
becomes failed and requires an explicit retry.

## Apply semantics

Apply is synchronous and is never queued. The requesting CLI or FieldWorks client remains connected until it
receives a committed Receipt or a refusal.

Apply may wait up to five seconds to acquire the per-project live-operation gate. This bounded acquisition is
not a queued Apply. An active Baseline capture may finish, but once an Apply is waiting, a not-yet-started
refresh may not jump ahead. If authority is unavailable after five seconds, Apply fails as busy and changes
nothing.

An existing Dry Run against a private Baseline may continue while Apply uses FieldWorks' live cache. This
allows background evaluation to finish against its clearly reported older Baseline. Refresh and Apply remain
mutually exclusive because both cross the live-project boundary.

### Required evidence and Decision

Apply requires:

- a valid Proposal and complete prerequisite state;
- a successful exact-bound Dry Run;
- a current `approved` Decision bound to the exact Proposal intent digest;
- compatible project identity, footprint, effects, Runner, LibLCM, and projection state;
- no unresolved Apply attempt or Conflict that affects this Proposal or its prerequisites.

Amendment invalidates the Decision and Dry Run binding. `--force` never bypasses approval, failed Dry Run,
prerequisites, project identity, intent, footprint, effect drift, or model safety.

Assessment results are advisory. A completed Assessment satisfies evidence availability however poor its
linguistic findings; Motif never derives a pass/fail verdict from those findings. A missing, queued, running,
cancelled, or tool-failed Assessment causes Apply to fail immediately unless the CLI supplies `--force` or
FieldWorks records the equivalent explicit acknowledgement. The Receipt records that the expected parser
evidence was unavailable. `--force` means only that acknowledgement.

FieldWorks may present one deliberate “Approve and Apply” action, but it durably records the human Decision
before mutation. CLI `apply` never creates an approval implicitly.

### One-use Apply Authorization

Before mutation, the worker validates durable policy, records `apply-starting`, and issues one opaque,
short-lived, one-use Apply Authorization bound to:

- project identity;
- Proposal ID and intent digest;
- Dry Run anchor and Baseline Token;
- approved Decision;
- Assessment disposition and accepted warning;
- unique Apply attempt ID.

The connected client returns that opaque authorization to the worker, which verifies and consumes it exactly
once. The worker sends the registered live host immutable verified claims; the host passes those claims and
its live `LcmCache` directly to the `netstandard2.0` Runner. The Runner performs final footprint/effect
preflight against the live model. If clean, it opens one outer LibLCM
unit of work, applies and reads back the Proposal, and writes the Apply attempt into the project applied log in
the same unit of work. FieldWorks then saves and sends the Receipt to the worker. The CLI-hosted path uses the
same state machine internally.

The attempt journal advances through `authorization-issued`, `mutation-started`,
`runner-completed-in-cache`, `save-started`, `saved`, and `receipt-recorded`, or ends as `refused` or
`needs-reconciliation`. Runner refusal before mutation leaves the cache unchanged. Runner failure during its
unit of work requires the host to discard or reload the cache before further work. Any failure or disconnect
after Runner completion but before confirmed durable save is ambiguous and becomes `needs-reconciliation`;
it is never retried automatically. The host fences further editing and Motif live work while its own
persistence recovery either confirms the same save or unloads and reloads the project. It must not continue
with a mutated, unsaved cache. After reload, the project applied log determines whether Apply persisted. If
save completed but the Receipt report was lost, the positive applied-log entry reconstructs the Receipt on
reconciliation.

## Reconciliation and Conflict

Reconciliation is routine, automatic synchronization between the live project's applied log and the worker.
When a FieldWorks adapter registers a live cache, a CLI host opens one, or a Proposal list is requested through
a live host, the host sends applied-log entries after the last acknowledged watermark.

The worker idempotently:

- marks matching Proposals applied;
- completes matching `apply-starting` or `needs-reconciliation` attempts;
- reconstructs a missing database Receipt from the project-owned applied record;
- retains unknown applied Proposal IDs as small tombstones so a later import cannot apply them twice.

When `.fwdata` and `.motif.db` disagree in a way that cannot be resolved from a positive applied-log entry,
Motif records a derived **Conflict** condition instead of guessing. Conflict is layered over workflow status.
It appears at the top of Proposal lists, never auto-archives, and opens to a deterministic explanation of:

- what the project records;
- what the database records;
- when each fact was recorded;
- plausible causes stated as possibilities;
- the consequences of each available user resolution.

The user may accept the project's account, verify and repair Motif's account, rerun the Proposal, or leave the
Conflict unresolved. Accepting a project account with no applied entry preserves the audit record, marks the
database Proposal not applied, and requires a fresh Dry Run and Decision before Apply. Repairing missing
project history is allowed only when live read-back exactly matches the stored Receipt and intent; the host
then writes the missing applied entry in an explicit reconciliation unit of work and saves. Otherwise repair
is refused. Rerun is available only after resolving the Proposal as not applied. A Conflict blocks only that
Proposal and dependents whose prerequisite satisfaction it makes uncertain. It does not freeze unrelated
Proposals, Dry Runs, or Assessments.

## Workflow archive and retention

Proposal workflow status and job status are separate. `cancelled` belongs to jobs. An author who abandons a
Proposal marks it `withdrawn`.

Terminal Proposals (`applied`, `rejected`, `superseded`, and `withdrawn`) move immediately to Archive and no
longer appear in the main list. Terminal jobs likewise leave active job lists. Archive retention defaults to
30 days and is locally configurable, including shorter, longer, or forever. Archived records may be deleted
manually. Stale is not terminal: a stale Proposal may remain in the main list indefinitely.

Reference-aware cleanup removes unpinned old Baselines, full Assessment payloads, logs, and other archived
bulk data when their retention permits. It never removes evidence pinned by active work.

Applied history has a small permanent core in both places:

- `.fwdata` contains the project-owned applied record needed for project truth and disconnect recovery;
- `.motif.db` contains a small applied index for workflow lookup without opening LibLCM.

Deleting an archived applied Proposal removes its bulky workflow and Assessment payloads but not those small
records. The project-owned record contains the Proposal identity and digest, rationale, actor, timestamp,
before/result identity, and accepted warnings. No Baseline, full Assessment, or PanGloss workspace survives in
it.

PanGloss work has no 30-day retention. Candidate exports and workspaces are deleted at terminal state and all
remaining owned work is cleared on worker startup. A bounded diagnostic log may follow ordinary archive
retention.

## External-file boundary

FieldWorks stores pictures, audio, video, and other linked files outside `.fwdata`. The `.fwdata` stores model
objects and paths to those files.

The initial Motif contract supports model changes only. It may delete a lexical entry or other model owner and
allow LibLCM to remove owned media-reference objects exactly as FieldWorks normally would. Dry Run and Receipt
report those semantic model effects.

Motif does not delete or alter the linked bytes. An unreferenced file remains on disk. If LibLCM is ever shown
to mutate external bytes as a side effect of an otherwise supported operation, Motif refuses that operation
until a media contract exists.

No initial operation may create, import, replace, rename, move, retain, restore, or garbage-collect external
media. A future media specification must design staging, recoverable filesystem application, held files,
content identity, collisions, deduplication, Receipt representation, backup, export, and Proposal portability
before any such operation is admitted.

The standalone [linked-media boundary](../../media-boundary-spec.md) is the operation-family admission
contract; this section states only the worker/Baseline consequence.

## Resource guarantees

For twenty Dry Runs against one Baseline:

- the live project is saved and captured once;
- the live project is not locked by the Dry Run loop;
- every Dry Run starts from identical model and writing-system inputs;
- no Dry Run saves its scratch mutations;
- Baseline disk use is independent of linked-media size and Dry Run count;
- one Dry Run `LcmCache` is active per project;
- Apply may concurrently use the one live `LcmCache` without changing the running Dry Run's baseline;
- a completed candidate export releases its `LcmCache` before PanGloss build or analysis;
- at most two PanGloss builds/Assessments run on the PC, each capped at 25% CPU;
- no PanGloss engine is persisted.

Measurements on large projects must record Baseline creation time, scratch open time, peak managed memory,
temporary disk bytes, export size, PanGloss CPU enforcement, and cleanup latency. Measurements verify these
guarantees; they do not weaken them.

## Acceptance requirements for implementation plans

Later implementation plans must decompose this architecture and prove at least:

1. one saved minimal Baseline supports twenty stable Dry Runs without copying linked media;
2. FieldWorks stays open and editable outside its brief authorized capture boundary;
3. the `netstandard2.0` adapter streams an equivalent saved model and writing-system bundle without buffering
   the whole project;
4. CLI live-project commands are refused while FieldWorks hosts the project, while authoring and Baseline-only
   work remain available;
5. queued refresh transfers from waiting FieldWorks authority to CLI-hosted execution after FieldWorks closes;
6. refresh barriers give earlier and later Dry Runs the specified Baselines;
7. Apply waits at most five seconds, never queues, and cannot be cancelled after mutation starts;
8. exact approval, Dry Run, Assessment availability, one-use authorization, final preflight, applied-log, save,
   Receipt, and reconciliation boundaries fail safely under injected disconnects;
9. a completed but linguistically poor Assessment never gates Apply, while unavailable Assessment evidence
   requires the narrow explicit force acknowledgement;
10. positive applied-log reconciliation repairs database state and unresolved disagreement becomes a loud,
    local Conflict without freezing unrelated work;
11. two global PanGloss slots cap complete process trees at 25% CPU each and schedule fairly across projects;
12. restart clearing, three-attempt interruption retry, immediate PanGloss cleanup, 30-day local workspace
    eviction, and configurable archive retention preserve all referenced evidence;
13. a current worker serves older and newer clients through negotiated protocols and capabilities, while a
    second worker cannot open the same databases;
14. database migration refuses newer schemas and never downgrades them;
15. FieldWorks deletion semantics remove model-owned media references while linked bytes remain untouched;
16. moving or duplicating same-identity projects creates distinct local workspaces without collision.

## Explicit exclusions

This specification does not design Harmony, Chorus, replication, a network review service, concurrent live
project writers, PanGloss's export format, persisted PanGloss engines, media authoring or storage, external-file
garbage collection, online worker deployment, or Lexbox Receipt sharing. It does not add a `net8.0` target.

The implementation must be split into independently reviewable plans: durable worker/protocol and version
launcher; project database and job lifecycle; minimal Baseline and queue scheduler; PanGloss orchestration;
Apply Authorization and reconciliation; and the FieldWorks adapter. None of those implementations is
authorized merely by this design document.
