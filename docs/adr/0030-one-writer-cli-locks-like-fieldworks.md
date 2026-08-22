# ADR 0030 — There is one writer: the CLI locks the project exactly as FieldWorks does

**Status:** partly superseded by
[ADR 0039](0039-one-worker-baseline-and-live-host-authority.md), 2026-08-22. The FieldWorks file lock
and one-live-writer findings remain binding. Decisions 1 and 2 below are historical: a reusable Baseline
now permits Dry Runs while FieldWorks is open, while only the live host may save, refresh, or Apply.

## Context

While wiring the Dry Run onto a scratch copy, the implementation drifted toward a plausible-sounding
idea: since the Dry Run now mutates only a throwaway copy, the `dry-run` command need not open the live
project at all, and *not* taking a write lock on it looks like a safety improvement.

The owner rejected the premise the idea rested on:

> *Motif is working as a CLI or in FieldWorks, never both at the same time. In fact, the CLI should lock
> the FieldWorks project as if it were FieldWorks itself. Both should not be allowed to make edits on a
> single project at the same time.*

This is not a small correction. "Avoid the lock" quietly reclassifies Motif from *a writer of this
project* to *a bystander that reads around one*, and everything the bound anchor does depends on Motif
being the former. A baseline measured while another program could still be editing is not a baseline.

## The mechanism already exists, and it is FieldWorks'

Verified in liblcm rather than assumed. `XMLBackendProvider.StartupInternal`
(`src/SIL.LCModel/Infrastructure/Impl/XMLBackendProvider.cs:149-154`) is:

```csharp
CreateSettingsStores();
LockProject();
return ReadInSurrogates(currentModelVersion);
```

and `LockProject` (`:371`) acquires a `SimpleFileLock` on `{project}.fwdata.lock`, throwing
`LcmFileLockedException` when it cannot — whose message is literally *"FieldWorks cannot open the
project {0} because another program is using it."* `UnlockProject` runs from `ShutdownInternal`, i.e. on
dispose.

So **opening a `.fwdata` project is taking the FieldWorks lock.** Two consequences:

1. Motif-as-CLI already excludes FieldWorks and vice versa, for as long as it holds the cache open. The
   requirement needs no new mechanism — only the discipline of *actually opening the project* and
   holding it for the whole command.
2. ADR 0006 decision 4 said *"single-writer is not an enforced lock"* and that Apply *"requires a
   host-provided guarantee of exclusive write access."* That remains true of the in-process
   `ReaderWriterLockSlim` it was describing, but the **cross-process** guarantee it asked a host to
   provide turns out to be provided by the backend itself for this project type. Decision 4's
   requirement is met by construction; what it cannot cover is a second writer inside one process,
   which is a different problem.

## Decision

### 1. One writer per project, and while Motif runs, Motif is it

Every command that could apply — `dry-run` and `apply` — opens the live project and holds it open for
the whole command, releasing on the way out. `dry-run` does this **even though its mutations land on a
copy**, because the point of holding it is not protecting the copy; it is that the anchor `dry-run`
binds must describe a project nobody else can move underneath it.

### 2. The CLI and the FieldWorks integration are one product in two hosts, never concurrent

There is no scenario in which Motif-in-FieldWorks and Motif-as-CLI operate on one project at once. They
are two deployments of the same code (ADR 0020's two scopes), and the lock makes the exclusion
mechanical rather than a convention anyone has to remember.

**What this buys, and it is the reason to state it as a decision:** Motif needs no cross-process
coordination, no CLI↔FieldWorks handshake, and no merge between what one did and what the other did
concurrently — because there is no concurrently. Every drift the anchor guards against is *sequential*:
the project changed between a Dry Run and an Apply, by a writer that had the lock in between and gave
it up. That is a strictly easier problem, and it is easier because of the lock, not despite it.

### 3. A locked project is a clear refusal, not a stack trace

If FieldWorks (or another Motif invocation) holds the project, `LcmFileLockedException` surfaces from
the open. The CLI reports it in its own terms — name the project, say what is holding it, say to close
that and retry — rather than passing along a message that says "FieldWorks cannot open the project"
when the thing that could not open it was Motif.

### 4. The scratch copy locks itself, at its own path

`ScratchCacheFactory.CreateFromFileCopy` copies the project folder to a new location before opening it,
so the scratch acquires its own lock on its own `.lock` file and cannot contend with the live project.
This is a **requirement on the copy, not an incidental property of it**: any future "open the same file
a second way" scratch strategy would deadlock against the live cache Motif is itself holding.

## Consequences

- **`dry-run` costs one live open plus one copy open.** The live open is what takes the lock and what
  makes the save-before-copy of ADR 0016 possible at all — you cannot save a project you have not
  opened. Both were briefly dropped in implementation; this ADR is why they are back.
- **`dry-run` now fails when FieldWorks has the project open.** Correct, and the same answer FieldWorks
  gives a second FieldWorks. Previously (had the drifted version shipped) it would have succeeded and
  produced an anchor that was stale on arrival.
- **The tests keep two caches open at once**, live and scratch — legal precisely because they are at
  different paths. Comments in the operation round-trip tests that warned against "two live `LcmCache`
  instances on the same `.fwdata`" are about the same file, and that hazard is unchanged.
- **Scope 2 inherits this unchanged.** Inside FieldWorks, FieldWorks itself holds the lock and Motif is
  code running behind it, so "one writer" holds for the same reason with no extra work. The only part
  that differs is that the host has real unsaved edits when Motif asks it to save (ADR 0016).
- **Not addressed:** two Motif commands in one process against one cache. The file lock cannot see them
  and neither can this ADR; that is ADR 0006 decision 4's in-process residue, and the CLI avoids it by
  being one command per process.
