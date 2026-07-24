# Applied-change log

A deliberately thin, append-only record written into the project itself, recording which Change Sets
LCAtom applied. It answers three questions: *did LCAtom already apply this Change Set, who applied
it, and when.*

It is not the record of the change. Change Sets, Assessments, and Receipts live outside this
repository and may be discarded without loss of this record.

## Where it lives

`LexDb.Resources` — a collection (`card="col"`) of `CmResource` (class 70), which FieldWorks already
uses for named one-time markers such as `TeStyles` and `FlexStyles`. `CmResource` has exactly two
properties, and both are used:

- `Version` (`Guid`) holds the Change Set GUID. **This is the only field used for matching.**
- `Name` (`Unicode`) holds a packed, single-line provenance string.

## Record format

```
Version = <change set GUID>
Name    = LCAtom|<format>|<timestamp>|<user>|<description>
```

Every field before the description is fixed-width or constrained, so the description is a free tail
and **no escaping is needed**: parse by splitting on the first four `|` and taking the remainder.

| Field | Rule |
| --- | --- |
| `LCAtom` | literal prefix identifying entries this runner owns |
| `<format>` | decimal format version, currently `1` |
| `<timestamp>` | UTC ISO 8601 basic, fixed 16 characters, e.g. `20260724T055701Z` |
| `<user>` | applier identity, at most 64 characters, may be empty, must not contain `\|` or control characters |
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

- **Presence** of GUID `G`: LCAtom applied `G` to this project, at that time, by that user. It does
  *not* mean `G`'s effects are still present — a later Change Set or a manual edit may have changed
  or reverted them.
- **Absence** of GUID `G`: LCAtom never applied `G` to this project. This is the idempotence check.
- The log says nothing about non-LCAtom edits. Projects will carry manual FieldWorks edits
  indefinitely and those leave no entry. The log is a positive record of LCAtom applications only.
- The log travels inside the project, so restoring an older backup correctly restores the older log.

## Foreign entries

Entries whose `Name` lacks the `LCAtom` prefix belong to FieldWorks or other tools. They are never
read, rewritten, or deleted. An `LCAtom`-prefixed entry that fails to parse is reported as a
diagnostic and left untouched.
