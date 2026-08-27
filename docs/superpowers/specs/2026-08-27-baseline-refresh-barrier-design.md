# Refreshing a Baseline when nobody can be asked to let go

A Baseline is a saved, file-backed copy of exactly the project state a Dry Run needs, and refreshing it
means capturing the project again. That requires the project, and FieldWorks may be holding it.

The old design asked. A refresh sent the live host an event over the worker's event pipe, and the host
answered **accepted**, **deferred**, or **declined** — the user was, in effect, prompted. That channel is
gone: [ADR 0040](../../adr/0040-one-api-the-cli.md) removes the transport, and decision 6 declines to
attach to a live project as a shared-XML peer, so there is no second way to reach FieldWorks either.
`BaselineRefreshCommandHandler` was deleted with the wire rather than half-ported.

This note settles what replaces it before anything is rebuilt.

## The thing that actually changed

Not "how do we ask" — **whether asking was ever the mechanism**. The old exchange had FieldWorks
volunteer the project. What made the volunteering possible was a live connection, and what made it
*necessary* was that the worker could not otherwise tell whether FieldWorks held the project at all.

It can. `XMLBackendProvider.LockProject` takes a `SimpleFileLock` on `{project}.fwdata.lock`, and
[ADR 0030](../../adr/0030-one-writer-cli-locks-like-fieldworks.md) already established that lock as the
real single-writer mechanism — "opening a `.fwdata` project is taking the FieldWorks lock." Trying the
lock answers the only question a refresh has to ask, and it answers it whether or not Motif is running,
which the pushed observation never did.

## Decisions

### 1. A refresh tries the lock; it does not negotiate

Three outcomes, and they map onto what the CLI already reports:

| Lock | Refresh |
| --- | --- |
| free | take it, capture, publish, release |
| held | **refused as busy** — `FailureReason.Busy`, exit 3 |
| free but capture fails | refused, and the existing Baseline stays current |

**Busy is not a failure of the refresh.** It is the retryable class the failure contract exists to name,
and it is why that class was worth having: a caller may try again once FieldWorks is closed, and knows
that from the exit code rather than from parsing prose.

### 2. "The host declined" stops existing

There is no declining party. The old vocabulary had three answers because a human was behind one of them;
with the lock there are two states and no opinion. `BaselineRefreshHostResult`,
`WorkerEventOutcome.Deferred` and `.Declined` do not come back in another form — they are removed, not
renamed.

This is a real reduction and worth naming as one: a user who had FieldWorks open used to be *asked* to
release the project, and could say yes. Now they close FieldWorks, or the refresh waits. Whether to
restore that as a FieldWorks-side prompt that runs `motif` afterwards is a product question for the
FieldWorks surface, not something the barrier should reinvent.

### 3. The barrier semantics of ADR 0039 decision 3 survive unchanged

> *A refresh is a barrier: Dry Runs ordered before it use the old Baseline and those ordered after it use
> the replacement.*

That is a statement about the per-project lane, and the lane is in-process machinery that outlived the
wire. `ProjectLane.EnqueueAgainstBaselineAsync` already pins work to the Baseline it was ordered against.
Nothing about the barrier depended on the conversation; only *starting* the refresh did.

What does need amending in that decision is its last clause — "if FieldWorks closes, the worker may
acquire the released project on the next opportunity and complete it automatically." That was the deferred
answer's payoff. Under decision 1 a refused refresh is not remembered, so nothing completes later on its
own. A caller that wants that behaviour retries.

### 4. Waiting is the caller's choice, with a bounded default

`ProjectHostReleaseCoordinator` survives and still signals in-process release. It cannot see FieldWorks
letting go, so a refresh that wants to wait polls the lock on the same jittered interval the job runner
uses. The default is not to wait: a `motif` invocation is one call, and a command that blocks silently
for as long as FieldWorks stays open is worse than one that refuses in a second and says why.

`--wait` is where waiting belongs, alongside the job-status wait the CLI surface already owes.

## What this costs, stated plainly

- A refresh cannot happen while FieldWorks holds the project. Previously it could, with the user's
  agreement, mid-session.
- Currentness while FieldWorks is open was already reduced to "not checked" when the observation commands
  went; this does not reduce it further, but it does mean the *remedy* — refresh now — is unavailable in
  the same window.
- Nothing is silently queued. That is deliberate, and it is the part most likely to be argued with: an
  automatic later completion is friendlier, and it is also a promise made to a caller who has since gone
  away, which is exactly the shape of thing this architecture has been removing.

## Not settled here

- **Whether the FieldWorks surface should offer "release and refresh"** as a user action. It could: it
  owns the project and can close it, then run `motif`. That is a surface design question.
- **Whether a refused refresh should leave a durable marker** so a later runner tick can retry without a
  caller. It is a small `Jobs` row if wanted, and it reintroduces the promise decision 4 avoids, so it
  should be decided deliberately rather than added because the machinery is there.
