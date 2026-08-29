# The Proposal lifecycle

*2026-08-29. What every state means, what moves between them, and where Jobs and Assessments attach. The
states and guards below are read from the code, not aspirational — `ManifestStatus`,
`Commands.DeferrableFrom`/`ApprovableFrom`/`RejectableFrom`/`SupersedableFrom`, and
`ProposalRepository`.*

**In plain terms:** a proposed change to a linguist's grammar goes through a handful of named stages —
being written, submitted, judged, and finally written into the project. This says exactly what each stage
means and what may follow it, so that "committed" and "approved" and "applied" cannot drift into meaning
whatever the reader assumed. It also says where the measuring fits: a measurement is attached to a proposal,
but it is never one of the proposal's stages.

## The seven states

| State | What is true of it |
| --- | --- |
| **draft** | Being authored. Carries a draft name and its in-progress content. Has **no committed revision** if it came from `new` or `duplicate`; has one behind it if it came from `reopen`. |
| **proposed** | Has a committed, immutable revision. Awaiting a judgement. This is what `finalize` produces. |
| **approved** | A **Decision** of `approved` is bound to *this exact revision*. Ready to apply. |
| **rejected** | A Decision of `rejected` is bound to this revision. |
| **deferred** | Still wanted, not currently applicable (ADR 0031 decision 4). Any Decision on it is cleared. |
| **applied** | Written into the project. A Receipt records what was applied and to what. |
| **superseded** | Replaced by another Proposal, which is named on it. |

A **Decision is scoped to the content it was recorded against.** Amending a Proposal — reopening it and
finalizing again — writes a new revision and clears the Decision, because approval of one text is not
approval of another. That is why `approved` is both a status and a Decision row: they are set together and
they move together.

## The transitions, and the verb that causes each

```mermaid
stateDiagram-v2
    [*] --> draft : new / duplicate
    draft --> proposed : finalize
    draft --> [*] : discard-draft
    proposed --> draft : reopen
    proposed --> approved : approve
    proposed --> rejected : reject
    proposed --> deferred : defer
    deferred --> approved : approve
    deferred --> rejected : reject
    approved --> rejected : reject
    approved --> deferred : defer
    approved --> applied : apply
    proposed --> superseded : supersede
    deferred --> superseded : supersede
    approved --> superseded : supersede
    rejected --> superseded : supersede
    applied --> [*]
    rejected --> [*]
    superseded --> [*]
```

The guards are asymmetric on purpose, and each asymmetry says something:

- **`approve` may not follow `rejected`.** A rejection is a judgement about a text; changing your mind means
  amending the Proposal or superseding it, not quietly re-approving the same words.
- **`reject` may follow `approved`.** Withdrawing an approval before it is applied must always be possible.
- **`defer` may not follow `rejected`.** Deferring means *still wanted, later*; a rejected Proposal is not
  wanted.
- **`supersede` may follow anything non-terminal, including `rejected`**, because a replacement should be
  able to point at what it replaces however that ended.
- **Only `approved` may be applied.** Applying is the only irreversible step, and it requires a Decision
  bound to the exact revision being written.

## Where measurement attaches

**Assessments attach to a Proposal. They are not states of it.** A Proposal that has been measured is still
`proposed`; a Proposal nobody has measured is also `proposed`. What differs is the evidence hanging off it,
not its stage.

This is deliberate, and it is the same rule ADR 0042 decision 2 applies to Assessments themselves: a Job
carries the states, an Assessment is immutable and stateless, and a Proposal's lifecycle is about judgement
rather than about measurement. Folding "being assessed" into the Proposal's states would give two authorities
on what a Proposal is doing, and they would disagree the moment a second Assessment was queued.

The states do, however, **decide what may be queued**: there is nothing to measure about a `draft` with no
committed revision, and nothing worth measuring about a `superseded` one.

## Trial, and why it is not a state

**Settled: the composite is a Trial, and `trial` is the verb.** One attempt at a Proposal on a throwaway
copy, producing a Dry Run *and* Assessments.

A Trial is **a kick-off, not a transition**. An author starts one whenever they want to know how things
stand; the Proposal does not move, nothing is frozen, and editing continues afterwards. A Proposal may have
many Trials across its life, and they are comparable to each other as well as to the Baseline — which is the
point, because that is how an author sees whether the last edit helped.

This means a Trial can measure **uncommitted** content, and therefore cannot pin to a revision, because there
is not one. It does not need to: content here is addressed by digest, so a Trial computes the draft's intent
digest and its Assessments cite that, whether or not those bytes ever become a committed revision. Two Trials
either side of an edit cite different digests, so neither is a lie and both stay comparable.

The consequence for `finalize` is a simplification. It stops being the gateway to measurement and becomes
only what it should always have been: committing a text so a **Decision** can be bound to exact words.
Measure whenever; commit when you want a judgement.

`dry-run` survives as its own verb. Reading effects off a scratch is cheap; parsing a corpus is not, and
somebody who only wants to know whether their operations resolve against the project should not wait for a
parser. So `dry-run` is the cheap question and `trial` is the expensive one that contains it.

## What happens to all of it when the Proposal is applied

A Proposal accumulates Trials, Dry Runs and Assessments. On apply, they become deletable — **by
configuration, defaulting to on**, in the same spirit as deleting a branch when a pull request merges. The
`<project>.motif.toml` owns that choice, so a project that wants to keep its working history may.

**One thing must never be swept up in that.** Applying *promotes* one candidate Assessment to be the
project's current one (ADR 0042 decision 6). That Assessment is no longer working scratch — it is the
project's measurement, the thing the next Trial will be compared against. A purge that treated it as one of
the applied Proposal's artifacts would delete the baseline for every future comparison, and the loss would
only surface at the next Trial, as a comparison that could not be made.

## The stage that had no name

`dry-run` is a job that opens a single-use scratch from the Baseline, applies the Proposal, and reads the
effects back. Under ADR 0042 that same job should also produce the candidate's Assessments — one scratch, one
Baseline load, no chance of the two measuring different grammars.

That makes the verb under-describe itself. `CONTEXT.md` keeps the two outputs deliberately apart:

> *Two different evaluations, deliberately named apart. One asks **does the grammar parse better?**; the other
> asks **what would this do to the project?***

A **Dry Run** answers the second. An **Assessment** answers the first. The composite — one attempt at a
Proposal on a throwaway copy, producing both — has no name.

**Settled: Trial.** *Evaluation* was the tempting reuse — the glossary already calls the two outputs "two
different evaluations" — and that is exactly why it was rejected: "an Evaluation contains two evaluations"
reads fine once and confuses everyone after. *Trial* adds a word rather than overloading one, carries the
right sense of a single attempt on a throwaway copy, and leaves Dry Run and Assessment untouched.

The verb is `trial`, matching the noun, as `label`, `comment` and `split` already do. `try` was rejected: it
reads as an attempt that might fail, and a Trial is a measurement rather than a gamble.
