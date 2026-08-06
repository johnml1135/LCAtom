# ADR 0028 — A reorder of a feeding field cannot be approved on a Dry Run alone

**Status:** accepted, 2026-08-05. Resolves `C12`, the highest-risk item in Plan A. Makes `MOT-8`'s acceptance
concrete and gives `MOT-10` its first change-class-conditional check requirement.

## Context

Phonological rule order encodes feeding and bleeding: one rule creating or destroying the conditions another
needs. Reordering two rules can change which words the grammar accepts **without changing a character of any
rule's content.** Two manifest rows carry that property — `PhPhonData.PhonRules` and
`LexEntry.AlternateForms`.

**The staleness machinery already handles its half, precisely.** Worth stating, because this ADR is not a
repair:

- `move` uses **identity-relative anchors** — `{after, before}`, the adjacent items — and *"numeric indices are
  not canonical intent"* (`change-set-contract.md:342-355`).
- Anchors refresh only when **exactly one gap satisfies the unchanged authored anchors**; if several positions
  are plausible, the operation conflicts (`:357-360`).
- The stored anchor is the **footprint digest plus engine version**, and *"a pre-flight that finds an effect
  delta stops and hands the delta to the application or user"* (`:702-719`).
- Changing the authored anchors is an explicit rebase with a new digest.

So a reorder cannot be silently applied into a world that moved. That is a real guarantee and it is built.

**What it does not cover is comprehension, and that gap opens when nothing has drifted.** Baseline unchanged,
exactly one gap satisfies the anchors, pre-flight clean, move applies — and the grammar now accepts different
words. Nothing in the anchor chain fires, correctly, because nothing was stale.

The reviewer saw: *"rule 7 now sits after rule 3."* That is true, complete, and silent about consequence. For
these two fields the diff is small and the consequence is large, which is the exact shape of change a review
process exists to catch.

## Decision

### 1. A reorder of a `feeding` field requires a Grammar Delta before approval

Not a Dry Run — an **Assessment** before and after, and the **Grammar Delta** between them: which analyses were
added, removed, retained, or became incomplete. The reviewer approves the consequence, not the position.

This needs no new machinery. Assessment and Grammar Delta are existing vocabulary (`CONTEXT.md`), the
evaluation path is [ADR 0016](0016-scratch-cache-copy-not-undo.md)'s copy-apply-save-parse, and
[ADR 0027](0027-what-counts-as-the-same-word-analysis.md) settled which comparison is valid — so *"here are the
words whose analysis changed"* is computable rather than aspirational.

### 2. Both Assessments run against the same baseline

Stated because getting it wrong would make the artifact worse than nothing: the before-and-after runs must
differ **only** by the proposal. Same baseline, same engine versions, one scratch copy with the proposal
applied and one without. A Grammar Delta computed across two different baselines attributes someone else's
edits to this reorder, and it would look authoritative while doing it.

### 3. Check requirements may depend on the change class — this is the first case

`MOT-10`'s review domain gains the notion that a change class can *require* a particular Check Run, rather
than every proposal carrying the same checks. The precedent matters more than the instance: it is how a
review process stays proportionate instead of demanding a parser run for a spelling correction.

**Scope: 2 of 494 fields.** That is what makes a mandatory parser run affordable.

### 4. The other ordered exception needs a different check, not this one

The exception table has two categories and they fail differently, so they get different checks:

| | Fields | Failure mode | Required check |
| --- | --- | --- | --- |
| `feeding` | 2 | Silent semantic change — the grammar quietly accepts different words | **Grammar Delta** (this ADR) |
| `index-as-identity` | 3 | Hard error — the 24-per-rule alpha-variable ceiling throws and kills the grammar load | **Pre-apply traversal check** (`C13`: call `IPhRegularRule.FeatureConstraints`, do not reimplement the walk) |

A loud crash needs a cheap pre-flight check. A silent meaning change needs an expensive parse comparison.
Applying either check to the other category would be waste in one direction and negligence in the other.

## Consequences

- **`MOT-8`'s acceptance becomes testable.** *"A reorder of real phonological rules produces a review a
  linguist can judge"* was previously unfalsifiable. It now means: the Grammar Delta is produced, it is bound
  to one baseline, and it names the analyses that changed.
- **Approval on these two fields costs a parser run** — minutes, not instant. Accepted deliberately: it applies
  to two fields, and those two are where a small diff hides a large consequence.
- **A reorder whose Grammar Delta is empty is still worth approving**, and the empty result is the useful
  finding: the reorder was safe. That should be reported as a positive result rather than an absence.
- **`C13`'s check is now scheduled rather than merely answered.** It belongs to the same family of
  class-conditional checks and lands with the first `index-as-identity` operation.
- **Deferred, and named so it is not rediscovered:** whether an *ordinary* `positional` reorder — the other 56
  rows, where order is display order — should offer a Grammar Delta on request. It is not required, because
  order carries no linguistic meaning there, but the boundary between the 2 and the 56 rests entirely on the
  manifest's classification being right, which is now a derived-and-checked column with a cited exception
  table ([ADR 0022](0022-structure-is-derived-policy-is-five-rows.md)).
