# Claiming a job when more than one process can

When one worker owned the database, nothing had to claim anything: the only process that could pick up
work was the one that wrote it down, and recovery at startup was safe because a job found running could
only have been left by this process's own previous life. [ADR 0040](../../adr/0040-one-api-the-cli.md)
ends that. A `motif` invocation writes a job row and exits; a job runner picks it up; either may die while
the other is alive. **A row therefore has to record who is working on it and until when**, so that a
runner which stopped breathing can have its work taken back rather than stranding it.

This note settles that before any of it is built.

## What is already there

More than expected, because the durable layer was built for this before the wire was.

| | |
| --- | --- |
| `Jobs.Version` | optimistic concurrency, `INTEGER NOT NULL DEFAULT 0 CHECK (Version >= 0)` |
| `JobRepository.Transition` | already takes an `expectedVersion` and refuses on mismatch |
| `IX_Jobs_Status_Updated` | `(Status, UpdatedUtc)` — a work-queue claim index in all but name |
| `Attempt`, `LineageId` | retry identity, with a unique index on the pair |
| `NotBeforeUtc` | scheduled retry, already used by infrastructure backoff |
| `MarkRunningInterrupted` | the startup sweep, which the lease partly replaces |

What is missing is only the ownership half: **no `OwnerId`, no `LeaseUntil`, no claim token, no
heartbeat.** Verified: zero occurrences of any of them.

## Decisions

### 1. Claiming is one conditional UPDATE, under `BEGIN IMMEDIATE`

SQLite has no `SELECT … FOR UPDATE SKIP LOCKED`, so a claim cannot skip a locked row — but it does not
need to, because SQLite has exactly one writer at a time. That single global write lock *is* the
serialisation, and a conditional update is enough:

```sql
UPDATE Jobs
   SET Status = 'running', OwnerId = $owner, ClaimToken = $token,
       LeaseUntilUtc = $until, Version = Version + 1, UpdatedUtc = $now
 WHERE JobId = (SELECT JobId FROM Jobs
                 WHERE Status = 'queued' AND ProjectKey = $project
                   AND (NotBeforeUtc IS NULL OR NotBeforeUtc <= $now)
                 ORDER BY UpdatedUtc LIMIT 1)
   AND Status = 'queued';
```

The repeated `AND Status = 'queued'` on the outer statement is the part that matters: the subquery and
the update are one statement under one write lock, and the predicate makes the claim idempotent under
retry. A claim that affects zero rows means somebody else won, which is an ordinary outcome and not an
error.

`Microsoft.Data.Sqlite`'s parameterless `BeginTransaction()` already issues `BEGIN IMMEDIATE`, so a claim
takes the write lock at the start rather than upgrading into it. That is what the repository already does
everywhere, so nothing changes; it is recorded here because the alternative — a deferred transaction that
reads then writes — produces a `SQLITE_BUSY` that `busy_timeout` will not retry.

### 2. Ownership is an id plus a claim token, and the token is what authorises a transition

`OwnerId` names the process. `ClaimToken` is minted fresh on every claim, including a reclaim of the same
job by the same process after a restart.

The token exists for one failure: a runner stalls long enough for its lease to expire, the job is
reclaimed by someone else, and then the stalled runner wakes up and finishes what it thinks is still its
job. `OwnerId` alone would let it, because it is still the same process. Every transition therefore
carries the token it was claimed with, and a transition whose token no longer matches the row is refused
in the same way a stale `expectedVersion` is.

### 3. A lease is time-bounded and extended by heartbeat, never by progress

`LeaseUntilUtc` is set at claim and pushed forward by a periodic heartbeat while the job runs. Progress
reporting does not extend it: a job that reports progress and then wedges would hold its lease forever,
which is the failure the lease exists to end.

Lease duration and heartbeat interval belong together and are recorded as a ratio rather than two loose
numbers — the heartbeat runs at a third of the lease, so two consecutive missed beats still leave margin
before another process may take the row.

### 4. Reclaim is a sweep, and it is the same statement as a claim

A job whose `LeaseUntilUtc` has passed is claimable by the predicate in decision 1, extended with
`OR (Status = 'running' AND LeaseUntilUtc < $now)`. There is no separate reclaim path, no repair pass, and
no state that only a sweeper can produce. A reclaimed job increments `Attempt`, so a job that wedges
repeatedly exhausts `MaxAttempts` and stops rather than cycling forever.

### 5. `MarkRunningInterrupted` narrows to this process's own orphans

The startup sweep currently marks *every* running job interrupted, which was right when only one process
could have left one. It must now mark only rows this `OwnerId` left behind. Rows owned by a live runner
are none of a starting process's business, and rows owned by a dead one are handled by lease expiry, which
does not need a second mechanism racing it.

### 6. Wake-up is jittered polling

SQLite has no `LISTEN/NOTIFY`. A runner polls; the interval is jittered so that two runners started
together do not converge on the same instant and contend on every tick. Polling a local SQLite file is
cheap, and the alternative — a filesystem watcher on the database — reports writes that are not
necessarily new queued work.

## Schema generation 8

Four additive, nullable columns and one index. Nothing is rewritten, and every existing row remains valid
with all four null, which is exactly the state "no one has claimed this".

```sql
ALTER TABLE Jobs ADD COLUMN OwnerId TEXT NULL;
ALTER TABLE Jobs ADD COLUMN ClaimToken TEXT NULL;
ALTER TABLE Jobs ADD COLUMN LeaseUntilUtc TEXT NULL;
ALTER TABLE Jobs ADD COLUMN HeartbeatUtc TEXT NULL;
CREATE INDEX IX_Jobs_Lease ON Jobs(Status, LeaseUntilUtc);
```

### What this migration does to `MinimumWorkerVersion`

This is the first migration where the compatibility floor is a real choice rather than a formality, so it
is worth being explicit: **it does not raise the floor.**

The columns are additive and nullable. A build that predates them writes and reads every other column
correctly and simply never claims — which is safe, because claiming is what the newer build does. Raising
the floor would lock out an older CLI for no benefit, and under
[ADR 0040 decision 5](../../adr/0040-one-api-the-cli.md) the CLI and the runner ship together anyway, so
the skew window is an upgrade race measured in seconds.

The gate that *does* fire is the unconditional one: a build supporting generation 7 opening a database at
8 is refused outright at `MotifDatabase`. That refusal is now user-facing text, so this work includes
checking that it says something a person can act on.

## What this note deliberately does not decide

- **How many runners may claim from one project at once.** The mechanism supports several; whether Motif
  wants more than one is a scheduling question, and today's per-project lane says one.
- **Whether a reclaimed job resumes or restarts.** It restarts, because that is what `Attempt` already
  means. Genuine resumption needs per-job checkpointing that nothing has asked for yet.
