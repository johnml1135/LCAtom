# Applied-change log

A deliberately thin, append-only record written into the project itself, recording which Change Sets
Motif applied. It answers three questions: *did Motif already apply this Change Set, who applied
it, and when.*

It is not the record of the change. Change Sets, Assessments, and Receipts live outside this
repository and may be discarded without loss of this record.

## Where it lives

`LexDb.Resources` — a collection (`card="col"`) of `CmResource` (class 70), which FieldWorks already
uses for named one-time markers such as `TeStyles` and `FlexStyles`. `CmResource` has exactly two
properties, and both are used:

- `Version` (`Guid`) holds the stable `changeSetId`. **This is the field used for identity matching.**
- `Name` (`Unicode`) holds a packed, single-line provenance string.

## Record format

```
Version = <changeSetId>
Name    = Motif|<format>|<timestamp>|<user>|<intentDigest>|<description>
```

Every field before the description is fixed-width or constrained, so the description is a free tail
and **no escaping is needed**: parse by splitting on the first five `|` and taking the remainder.

> **The prefix changed on 2026-07-30**, from `LCAtom` to `Motif`, with the product rename
> (grill-decisions D7). This is a **format-breaking change to persisted data**: an entry written by the
> older code is not recognized by the current parser and is treated as a foreign `CmResource` — that is,
> ignored, never rewritten. It was taken now, and without a `<format>` bump, because nothing has shipped
> and no project in the field carries an `LCAtom`-prefixed entry. If one is ever found, it is a
> migration, not a parse bug.

| Field | Rule |
| --- | --- |
| `Motif` | literal prefix identifying entries this runner owns |
| `<format>` | decimal format version, currently `1` |
| `<timestamp>` | UTC ISO 8601 basic, fixed 16 characters, e.g. `20260724T055701Z` |
| `<user>` | applier identity, at most 64 characters, may be empty, must not contain `\|` or control characters |
| `<intentDigest>` | the Change Set's intent digest recorded at apply, fixed-length lowercase hex, no `\|` |
| `<description>` | free single-line text, at most 128 characters, may contain `\|` |

No length limit is declared on `Unicode` model properties in `MasterLCModel.xml`, and the 100-character
limits in the LCM source apply to custom-field metadata rather than to model properties. `Name` is
nonetheless capped at 256 characters total. Overlong, multi-line, or control-character input is
rejected at validation rather than truncated.

The description is human-facing only. It is subject to LibLCM's ordinary Unicode handling and must
never be used for matching.

## Applier identity

An opaque, host-supplied string passed on apply. The runner is stateless and never infers identity.
FieldWorks supplies its configured user when it has one; an agent supplies its own name, for example
`linguistic-assistant`. Empty is permitted and means the host did not supply one.

## Exclusions

The log is normatively excluded from **both**:

- the canonical semantic snapshot, via the `runner-bookkeeping` coverage classification;
- `expectedEffects` and every effect digest.

The second exclusion is not optional. The entry carries a timestamp and an identity, so including it
would make every effect digest unique — destroying drift detection and approval continuity — and
would change the project's semantic digest on every apply, so two operators applying the same Change
Set could never agree on a result digest.

## Atomicity

The entry is written inside the same unit of work as the change it records. A rolled-back application
leaves no entry. Exactly one entry is written per applied Change Set.

## What presence and absence mean

- **Presence** of GUID `G`: Motif applied `G` to this project, at that time, by that user. It does
  *not* mean `G`'s effects are still present — a later Change Set or a manual edit may have changed
  or reverted them.
- **Absence** of GUID `G`: Motif never applied `G` to this project. This is the idempotence check.
- **Content check**: `G` present but the stored `<intentDigest>` differing from the Change Set now
  under consideration is surfaced — same identity, different content — rather than reported as a clean
  "already applied." Matching is on the stable `changeSetId`; the digest catches drift in what that
  identity refers to (the Flyway/Liquibase checksum pattern; see
  [ADR 0004](adr/0004-prerequisite-graph-stable-ids-bound-apply.md)).
- The log says nothing about non-Motif edits. Projects will carry manual FieldWorks edits
  indefinitely and those leave no entry. The log is a positive record of Motif applications only.
- The log travels inside the project, so restoring an older backup correctly restores the older log.

## Sync behavior

FieldWorks projects sync between machines with Chorus Send/Receive, a 3-way XML merge. Distinct
entries — different `Version` GUIDs — always union: LibChorus retains every unmatched insertion and
never drops one, so two operators applying different Change Sets and then syncing both keep their
entries. For that union to be free of a spurious order-ambiguity note, FieldWorks must register
`CmResource` as GUID-keyed and order-irrelevant — the pattern Chorus's own append-only `.ChorusNotes`
log already uses. That registration lives in FLExBridge and is verified in Phase 0.
See [ADR 0003](adr/0003-feasibility-findings.md).

The one collision is two entries written under the *same* `Version` GUID with different `Name` text —
a race applying the same Change Set on two diverged copies before either syncs. Chorus keeps one and
overwrites the other's `Name`. This costs only provenance: the GUID still appears exactly once, so
the idempotence check (which reads only the GUID) is unaffected, and the record was never
authoritative.

## Foreign entries

Entries whose `Name` lacks the `Motif` prefix belong to FieldWorks or other tools. They are never
read, rewritten, or deleted. An `Motif`-prefixed entry that fails to parse is reported as a
diagnostic and left untouched.
