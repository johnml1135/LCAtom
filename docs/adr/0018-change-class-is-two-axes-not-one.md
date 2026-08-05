# ADR 0018 — A change class is two orthogonal axes, and both are already in the kind name

**Status:** accepted, 2026-08-05. Answers `G28`, the second gate of
[grill-plan-a.md](../grill-plan-a.md), and resolves `G27` and `G29` as consequences.

## Context

The [2026-08-03 proposal](../proposal-2026-08-03-bidirectional-and-test-coverage.md) §2 offered six
change classes and invited a challenge. The [manifest audit](../research/2026-08-03-manifest-trust-audit.md#8-does-the-proposed-change-class-taxonomy-partition-the-manifest-g27)
found they do not partition: only **52%** of the 473 in-scope rows land unambiguously in one class, 73
rows have no bucket, 59 straddle several, and 61 are not authorable at all.

**The failure is diagnostic, not fatal.** The six classes conflate two different axes:

- Classes 1–4 (lexical entries, grammar rules, texts, manual analysis) are a **domain** axis.
- Class 5 (links and relationships) is an **operation-shape** axis.

They are orthogonal, which is exactly why `addRef|removeRef|move` rows are simultaneously
"relationships" and "ordering," and why `lists` and `system` — real domains — have no class at all.

## Decision

**A change class is a `(domain, shape)` pair, not one label.**

Verified against all 473 in-scope rows: the two axes yield **21 populated cells and every row lands in
exactly one. Zero straddle.**

| Domain (`Group`) | Rows |
| --- | ---: |
| `grammar` | 230 |
| `lexical` | 157 |
| `system` | 47 |
| `lists` | 39 |

| Shape (`Verbs`) | Rows |
| --- | ---: |
| `set\|clear` | 220 |
| `create\|delete` | 99 |
| `n/a` (not authorable) | 61 |
| `addRef\|removeRef` | 34 |
| `create\|delete\|move\|reparent` | 32 |
| `addRef\|removeRef\|move` | 27 |

### The consequence that matters: this is not a new vocabulary

ADR 0009's kind namespace is already `group / construct / verb` — for example
`lexical/sense/setGloss`. So:

- **domain = the kind's first segment**, which is the manifest's `Group` column;
- **shape = derived from the kind's verb segment.**

**Both axes are already in the kind name.** A change class is therefore a *projection* of an
identifier that already exists — not a second classification to author, version, and keep aligned.

This has three effects:

1. **Nothing new becomes versioned contract.** There is no new label to rename later, so the
   major-forcing risk that made `G28` urgent does not arise. This also discharges
   [ADR 0017](0017-text-and-analysis-destination-scope.md) decision 5's concern about settling class
   naming before text is in view: adding a `text` domain is adding a `group`, which is minor-safe.
2. **`G27` dissolves rather than being answered.** "Are the six classes the right cut" was the wrong
   question; the cut was two cuts. The 73 homeless rows get a home (`lists`, `system` are domains), and
   the 59 straddling rows stop straddling because their two memberships were one on each axis.
3. **The 61 non-authorable rows are honestly represented**, as shape `n/a`, instead of being an
   embarrassment for a taxonomy that only described editable operations.

### `G29` — ordering does not get its own class

Ordering is a **shape**, not a domain, so it needs no class of its own. And the substantive worry
behind `G29` — that 54 of 56 `positional` rows are display order while 2 are grammatical meaning — is
**already carried by a column that exists**: `ComparisonClass` distinguishes `positional` (56) from
`feeding` (2). The manifest already separates "order is presentation" from "order is meaning."

### Risk tier is derived, not an axis

Review depth, approver count, and blast radius are a **function of** `(domain, shape)` plus
`ComparisonClass`, computed rather than authored. Shared vocabulary is the motivating case: the 39
`lists` rows have project-wide blast radius that no lexical edit has, and that falls out of the domain
axis without needing a third classification.

## Consequences

- The proposal's §2 table is superseded. Its **row counts were all verified exact**; it is the grouping
  that changes.
- `G27` and `G29` close. `G28` closes.
- A UI or policy keyed on a single "class" label must say **which axis it means**. This is the one real
  cost of the decision, and it is a naming discipline rather than machinery.
- Adding text later adds a **domain value**, not a new axis — consistent with ADR 0017's finding that
  the domain-side work is additive and the hashed-side work is not.
