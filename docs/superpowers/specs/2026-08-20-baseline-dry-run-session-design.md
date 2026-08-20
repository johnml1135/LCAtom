# Baseline-bound Dry Run sessions

Motif should let an agent try many Proposals without repeatedly saving or locking the FieldWorks project.
One saved project state becomes a private, minimal baseline that every Dry Run in the session can reuse.

## Decision summary

Motif pairs one database with one FieldWorks project file. A session saves the project once, materializes one
minimal file-backed baseline, releases the project, and evaluates any number of Proposals against that baseline.
It loads only one baseline cache at a time and never copies linked pictures, audio, or other external files.

`Dry Run` and `preflight` name different operations. A Dry Run performs the same lowering, LibLCM mutation, and
read-back as Apply, but against a throwaway cache whose changes are never saved. Preflight is the final,
non-mutating comparison against the live project immediately before Apply.

Media authoring is outside this scope. Motif may delete model objects that own or reference media as an
incidental LibLCM cascade, but it does not create, import, replace, move, rename, retain, restore, or delete the
external files themselves.

## Project and database layout

A project named `Kalaba` uses this pair in one FieldWorks project directory:

```text
Kalaba/
  Kalaba.fwdata
  Kalaba.motif.db
  WritingSystemStore/
  LinkedFiles/
  ...
```

The shared filename stem and directory establish the association. Motif does not introduce an authority,
replica, synchronization, or database-reattachment concept. Moving or copying the FieldWorks project directory
may carry the pair together in the same way it already carries the `.fwdata` and its supporting directories.

People may see the Motif database beside the `.fwdata`, but they do not edit it directly. Motif owns its schema
and migrations. The database stores Proposal workflow records and their project-bound evidence. Content
digests remain the identity of immutable records even though SQLite, rather than a content-addressed file tree,
stores their bytes.

If approved, this replaces ADR 0036's choice to store Proposals and Receipts as separate files. It does not
silently reinterpret that older decision; the implementation plan must include migration from the existing
file store into the paired database.

### Portable Proposals, project-bound evidence

A Proposal is portable semantic intent. A person may export it from one Motif database and import it into
another, including a project with the same human-readable name.

The Proposal's project-bound records do not travel with that export:

- Baseline Token;
- Dry Run and its expected effects;
- Check Runs;
- Decision and Apply Authorization;
- Receipt.

The destination project must produce new evidence against its own baseline. If a transport format ever carries
old evidence for audit purposes, Motif treats it as historical attachment data and never as authorization to
apply on the destination.

## Baseline Token

A Baseline Token identifies one complete semantic state of one FieldWorks project:

```text
BaselineToken
  projectIdentity
  semanticSnapshotDigest
  projectionVersion
```

`projectIdentity` is the stable identity stored in the FieldWorks model, not the filename or displayed project
name. `semanticSnapshotDigest` is the digest of the whole canonical semantic snapshot. The snapshot excludes
Motif bookkeeping such as the applied log. `projectionVersion` states how Motif interpreted the model when it
computed the digest.

The Baseline Token is not an Apply Authorization. It identifies the complete starting state. A Dry Run also
binds its Proposal intent and footprint-scoped effects so unrelated edits need not invalidate review.

## Session lifecycle

### 1. Materialize the baseline once

The host opens the live project, makes any save visible to the user, waits until the save reaches disk, and
computes the Baseline Token. While the host still has exclusive access, it copies the minimum files needed to
load an equivalent LibLCM model. It then closes the live project and releases its lock.

The minimal baseline contains:

- the `.fwdata` file;
- the project's `WritingSystemStore` contents;
- only additional small configuration files that a load-equivalence test proves LibLCM requires.

It does not contain the `.motif.db`, `LinkedFiles`, `SupportingFiles`, backups, Send/Receive repositories, or
other unrelated project-directory contents. Empty directories may be created when LibLCM requires their
presence; their files are not copied.

The baseline is file-backed because LibLCM's memory-only backend loses project-specific writing-system data,
including collation and valid characters. File-backed describes how LibLCM loads the private baseline; it does
not mean the live project remains open.

### 2. Run many Dry Runs privately

Each Dry Run opens the same unchanged minimal baseline, applies one validated prerequisite plan and requested
Proposal, reads the effects back, and disposes the cache without saving it. The next Dry Run reopens the same
unchanged baseline. No per-run project-directory copy is created.

The session holds at most one loaded LibLCM cache during this loop. It does not hold the live project's lock,
save the live project again, or grow disk use with the number of Dry Runs.

Every Dry Run in the session names the same Baseline Token. Amending a Proposal changes its intent digest but
does not require a new baseline. A new baseline is required only when the project state used for evaluation
changes or when the projection version changes.

### 3. Preflight and Apply

Apply reopens the live project and obtains exclusive access for the short final operation. Preflight recomputes
the current Baseline Token and the Proposal's current footprint before mutation.

Apply refuses if the project identity differs, the Proposal intent differs from the Dry Run, or a changed
project state changes the Proposal's footprint or effects. A changed whole-project Baseline Token does not by
itself reject an unrelated edit: an identical footprint and effect set fast-forward the binding to the current
token. A successful preflight opens one outer LibLCM unit of work, applies the Proposal, reads back the actual
effects, commits, saves once, computes the result token, and records the Receipt.

The session deletes its minimal baseline after Apply or when the session ends.

## External-file boundary

FieldWorks stores pictures, audio, video, and other linked files outside `.fwdata`, normally beneath
`LinkedFiles/Pictures`, `LinkedFiles/AudioVisual`, and `LinkedFiles/Others`. The `.fwdata` stores model objects
and paths to those files.

The initial Motif contract supports only model changes. It permits an existing operation to delete a model
owner, such as a lexical entry, even when LibLCM's ownership cascade removes picture, media, or file-reference
objects from the model. Motif reports those semantic model effects in the Dry Run and Receipt.

Motif does not delete or otherwise alter the linked file bytes. A file left without a model reference remains
on disk. If LibLCM is ever shown to delete or change an external file as a side effect of an otherwise supported
operation, Motif must refuse that operation until the media contract exists.

The deferred media contract must answer at least:

- where newly authored or replaced files are staged before Apply;
- how a Dry Run accesses staged bytes without changing the project;
- how Apply makes model and filesystem changes recoverable across separate durability boundaries;
- how replaced or deleted files are held for recovery;
- when an unreferenced file may be garbage-collected;
- how file identity, content digests, deduplication, names, and collisions work;
- how a Receipt records byte-level effects without embedding large files;
- how backup, export, and Proposal portability include or exclude media payloads.

Until that contract is accepted, no operation family may author an external-file effect.

## Resource guarantees

For a session of `N` Dry Runs against one baseline:

- the live project is saved once before baseline materialization;
- the live project is not locked during the Dry Run loop;
- the baseline's disk size is independent of `LinkedFiles` size;
- disk use does not grow with `N`;
- at most one LibLCM cache is loaded during the Dry Run loop;
- every Dry Run begins from byte-identical model and writing-system inputs;
- no Dry Run saves its mutations.

The implementation should measure baseline creation time, per-run open time, peak managed memory, and temporary
disk bytes on a large real project. These measurements verify the guarantees; they do not weaken them.

## Acceptance tests for a later implementation plan

The implementation plan must include tests proving:

1. one save and one minimal baseline support twenty Dry Runs;
2. all twenty runs start from the same Baseline Token and produce stable effects for identical intent;
3. the live project lock is released before the first Dry Run begins;
4. `LinkedFiles` containing large picture and audio fixtures are never copied or opened;
5. the paired `.motif.db` is never copied into a baseline;
6. temporary disk use remains constant across twenty runs;
7. only one LibLCM cache is live during the Dry Run loop;
8. project-specific collation and valid characters match the saved live project;
9. a Proposal exported to another project carries no usable Baseline Token or Dry Run authorization;
10. deleting a model owner may remove media-reference objects but leaves linked file bytes untouched;
11. any detected external-file mutation causes refusal before the live project changes.

## Explicit exclusions

This specification does not design or implement Harmony, Chorus, replication, multi-project authority,
concurrent editing, media authoring, media retention, media garbage collection, or the FieldWorks adapter. It
does not authorize code changes. An implementation plan follows only after this specification is reviewed and
approved.
