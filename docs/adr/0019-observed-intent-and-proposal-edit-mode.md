# ADR 0019 — Observe intent at edit time, inside a constrained proposal-edit mode

**Status:** accepted, 2026-08-05. Answers `F26` and `F22` in [grill-plan-a.md](../grill-plan-a.md).

## Context

The [2026-08-03 proposal](../proposal-2026-08-03-bidirectional-and-test-coverage.md) §1 asks for
bidirectional encoding as a foundation, and its most attractive claim is *"someone can draft a change
set from inside FieldWorks, by editing normally."*

If that is the primary human authoring path, everything a human can do must be recoverable from a state
delta. It is not:

| Operation | Recoverable from a delta? |
| --- | --- |
| `set`/`clear`, `create`/`delete`, `addRef`/`removeRef` | Yes |
| `reparent` | **Yes** — the GUID survives the cross-owner move |
| `merge` | **No** — "merged A into B" is indistinguishable from "deleted A, edited B" |
| `replace` | **No** |
| index-as-identity `move` | **No** |

Two committed constraints narrowed the options before the decision:

- **Degrading to delete-plus-create is already forbidden.** `change-set-contract.md` says *never
  delete-plus-create*, because LibLCM's own overwrite is a detach and delete-plus-create would trigger
  a full ownership cascade.
- **Diff is blocked upstream of itself.** `ObjectSnapshot` supports 1 of 473 rows (`F25`). "Make diff
  foundational" is really "build the snapshot substrate first."

## Decision

### 1. Observe the intent; do not recover it

When a human drafts a proposal inside FieldWorks, **FieldWorks records what they did** — *Merge
Entries*, *subclass convert*, a reorder — rather than Motif inferring it from the resulting delta. The
unrecoverable set stops being a problem for the authoring path, because nothing is being recovered.

### 2. Diff keeps the job it is actually good at

Comparing two LibLCM projects that share no edit history: merging projects, reviewing what changed,
re-encoding an edited proposal. Diff stays first-class and its specification (exact-identity two-way,
three-way `ThreeWayAssessment`, O(n log n) LIS with frozen tie-breaking) stands unchanged. It is no
longer load-bearing for *drafting*.

### 3. Drafting happens in an explicit proposal-edit mode, and that mode is constrained

The owner's condition, and the decision's keystone:

> *There will be a different mode when in "edit to create a proposal" so we can constrain what is
> possible — to only the domains we are concerned about.*

The mode is not a passive recorder. It **bounds the edit surface to the domains in scope**, which:

- makes the observation problem finite — only in-scope commands need a recorded intent;
- makes refusal a design-time property rather than a runtime surprise: an unobservable edit is simply
  not offered, instead of being accepted and then rejected at encode time;
- bounds drift, because edits outside the proposal's domains cannot happen in the session at all;
- gives `F24`'s provenance question a clean answer — a proposal is observed, diffed, or authored, and
  the mode is what distinguishes the first.

## Consequences

- **`F22` is largely dissolved for the authoring path.** `merge`, `replace`, and index-as-identity
  `move` are observed rather than inferred. Diff still cannot recover them, and when diffing two
  unrelated projects it must **refuse loudly** — never silently degrade, per the contract's existing
  prohibition.
- **This needs a spike before it is built.** It requires a seam in FieldWorks' command layer.
  [ADR 0003](0003-feasibility-findings.md) deliberately avoided depending on liblcm's *internal undo
  records*; observing FieldWorks' own commands is a different seam, but its existence and stability are
  **unverified**. This is the ADR's one open risk, and it should be spiked alongside `A1` and `E19`.
- **Snapshot substrate work is de-urgentized, not cancelled.** Since drafting no longer depends on
  diff, the 473-row snapshot substrate can be built incrementally behind the domains that need it,
  rather than up front as a precondition.
- **The edit-mode constraint becomes a product surface**, not just a technical guard: it is how a
  reviewer knows the session is producing a proposal rather than editing the project.
- Motif's delivery statement is unchanged — a CLI and a FieldWorks integration. This decision moves
  weight *toward* the FieldWorks integration.
