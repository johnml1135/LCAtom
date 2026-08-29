# ADR 0042 — A Job produces Assessments, an Assessor makes them, and a scope is what makes two comparable

**Status:** accepted, 2026-08-29. Amends [ADR 0035](0035-reports-are-advisory-queries-over-stored-assessments.md)
decision 1, which said a configured target does not gate. Builds on
[ADR 0033](0033-three-systems-and-who-owns-which-measure.md), whose division of labour between PanGloss,
FieldWorks, Motif and the linguist it does not change. The working design this came from is
[the assessment scope design](../assessment-scope-design.md).

**In plain terms:** Motif is about to start telling a linguist whether a change to their grammar made things
better or worse. To do that honestly it has to compare two measurements — and two measurements can only be
subtracted if the same thing was measured both times. This decides what "the same thing" means, names it a
**scope**, and settles who makes a measurement, who stores it, and who is allowed to say which result is
better. The short version of that last part: Motif hands over the evidence and never renders the verdict.

## Context

Motif can author a change, preview it, and apply it. It cannot yet say whether the change helped, which is
the question a linguist actually has. The measuring machinery exists and is unused: a parser seam, a coverage
figure that already refuses to report a percentage when nothing was adjudicated, and five database tables.

Two sibling systems arrived while it sat unused. PanGloss built a SQLite stats cache that books elapsed time
at each grammar object boundary — real per-rule and per-morpheme attribution, keyed by word, object, stratum,
allomorph and direction. And `linguistic-assistant` became a tested prototype of the improvement loop itself:
an MDL objective that charges over-generation in bits, an accept gate with a gold-regression hard-reject, and
a search that stops on stall.

So the question is no longer *how do we measure* but *what does Motif own, and what shape must its output
take so that a loop outside it can drive it*.

## Decision

### 1. A Job produces Assessments; an Assessor makes each one

An **Assessment** is one immutable measurement, made by one **Assessor**, under one **Assessment scope**. A
Job is the queued unit of work and may produce several.

An Assessment is no longer defined as a PanGloss run. PanGloss is the common Assessor; a C# HermitCrab is
another; a model asking whether more lexemes align is a third and is not a parser at all. The code already
assumed this — `JobStatus.CompletedWithAssessmentFailure` only means anything if a Job and an Assessment can
fail separately.

Two Assessments may be compared only when they share an **Assessor**. That check is what stops an alignment
score being subtracted from parse coverage.

**This takes a word back.** `CONTEXT.md` said *"PanGloss owns this word"* and `linguistic-assistant` says the
same. Motif is the system that holds several kinds of measurement, which is what earns the general noun, so
Motif takes it and a PanGloss assessment becomes one kind. The cost is that this must land in all three
glossaries, because a term meaning one thing here and another next door is worse than either choice.

### 2. An Assessment has no lifecycle

It is immutable once written, like a `ProposalRevision`. The Job that produced it carries the states.
*Current* is a **pointer the project holds**, not a state the Assessment carries — the same shape as
`Proposals.CurrentIntentDigest` pointing at a revision.

A third state machine here would give two places the power to call an Assessment stale, and they would
eventually disagree.

### 3. A scope is what must be held equal, and comparability is a subset relation

A run varies in which words it tried, which Assessor and engine ran, what it collected, what limits it
applied, and which grammar it parsed. Only the last may differ between a Baseline's Assessment and a
candidate's. Everything else held equal is the **Assessment scope**.

**A Baseline is measured under the superset** of every scope a Proposal will use, so a candidate measured
under a narrower scope is comparable without measuring the Baseline again. Comparability is therefore
containment, not equality: a candidate compares when its scope is contained in the Baseline's along every
axis, and the comparison is computed over the intersection. Requiring equality would make every Proposal pay
for the widest run.

A **per-word limit is part of the scope**, defaulting to about a second or an equivalent cap on attempts.
Coverage computed under a one-second cap is not comparable with coverage under ten, so the limit cannot be a
run-time flag.

Scopes are **declared** in a human-readable `<project>.motif.toml` beside the project, and the resolved scope
is **embedded in the Assessment by content**, so editing the declaration can never reinterpret a measurement
already taken.

### 4. Reports fall out of the scope, and say so when they cannot

A Report is a query over an Assessment. It is answerable only if the run collected what it needs. Asking a
run that recorded word-level timing only which rules were slow must fail **naming the reason** — *this scope
did not collect per-object counters* — rather than returning zeros, which is indistinguishable from a grammar
whose rules cost nothing.

### 5. A regression may gate, per project, and an override is recorded

ADR 0035 decision 1 said reports are advisory and a configured target does not gate. **That is amended.** A
project may declare in its `.motif.toml` that a regression blocks `apply`, on the model of a failing check on
a pull request: it blocks, a human may override, and the override is durable.

Two things constitute a regression: **coverage dropping**, and **an approved analysis no longer being
produced** — the second being sharper, because ADR 0038 makes approved analyses the expectations.

The override is a **Decision**, reusing the actor-and-comment machinery `approve` and `reject` already have,
rather than a second record that would drift from the first. Surfacing a regression is valuable even when
overridden: it is often the manual analysis that is wrong, and that is a finding worth recording rather than
silently passing.

### 6. Applying promotes the candidate Assessment; the Baseline refreshes lazily

A project holds a current Assessment. Applying a Proposal promotes that Proposal's candidate Assessment to
current, so a measurement already paid for is not immediately thrown away and recomputed.

Promotion is bookkeeping only. The Baseline is stale the moment the project changes, and refreshing it is the
slow operation, so it refreshes when something next needs it. A promoted Assessment carries the Baseline
token it was measured against, so it can never silently describe a project that has moved — the same
mechanism Drift already uses.

### 7. Motif never holds a verdict, and the loop lives outside it

PanGloss generates, Motif orchestrates and presents, the linguist judges. This restates ADR 0033 decision 4
rather than extending it, and it decides where the improvement loop lives: **outside**.
`linguistic-assistant` is the AI linguist that ADR 0033's table already names. Its MDL objective, its accept
gate and its stall limit are all answers to *what would make this better*, and they belong on its side of the
line.

The practical consequence is a test of this API rather than a constraint on it: if that loop can drive Motif
through `motif --json` alone, the surface is complete.

### 8. Per-rule cost belongs to PanGloss; Motif stores and reports it

PanGloss books `self_time_ns` at each object boundary, nesting-aware, and calls it exact rather than
apportioned. Motif reads that cache and reports from it, and does not derive cost attribution of its own.
Deriving it would have Motif inventing a measure of PanGloss's internals from the outside, which is the
mistake ADR 0033 decision 1 exists to prevent, and it would drift the moment PanGloss's engine changed.

The cache is a file PanGloss owns the format of; Motif records its path and digest, per
[ADR 0041](0041-the-database-is-the-only-store.md) decision 9.

## Consequences

- Adding a new kind of measurement is adding an Assessor, not a new concept. Every scope, report and
  comparison mechanism continues to apply.
- Two glossaries outside this repo now disagree with it about the word *Assessment*, until they are updated.
- A Baseline's Assessment becomes the expensive, wide, infrequent run, and candidate Assessments become
  cheap and narrow. Nothing yet decides how the superset is computed, or what happens when a Proposal wants a
  scope the Baseline never ran.
- `<project>.motif.toml` is a second configuration surface beside the paired database, and it can be edited
  into disagreement with the Assessments that reference it.
- Retention is unanswered for both Assessments and stats caches, where jobs are already capped.

## Rejected alternatives

- **One PanGloss run is one Assessment.** Rejected in decision 1: it makes every new kind of measurement a
  new concept, and it contradicts a job status the code already has.
- **Giving an Assessment a lifecycle.** Rejected in decision 2; two authorities on staleness will disagree.
- **Requiring scope equality for comparison.** Rejected in decision 3: it makes every Proposal pay for the
  Baseline's full scope, which defeats the point of a fast candidate run.
- **Keeping reports purely advisory.** Rejected in decision 5. The owner's model is a pull request's failing
  check, which blocks and can be overridden — advisory-only cannot express that.
- **Motif ranking candidates.** Rejected in decision 7. Every convenience that reads *"tell me which proposal
  is best"* moves a verdict into Motif, and ADR 0033 decision 4 puts it with the linguist.
- **Motif deriving per-rule cost from per-word timings.** Rejected in decision 8; it would invent a measure
  of another system's internals from outside.

## Amendments

### 2026-08-29 — comparability is a join on words, not containment of scopes

Decision 3 said a candidate Assessment compares to a Baseline's when its scope is **contained** in the
Baseline's along every axis. That is wrong, and it was wrong in an expensive direction: it would have refused
comparisons that are perfectly meaningful and forced Baselines to be re-run to permit them.

**Two Assessments are compared by joining on the word.** What must match is the word and the **kind** of
measurement. Everything else — Assessor, engine, limits, which corpus the words came from — is **context that
annotates the comparison rather than gating it**. Two runs whose word sets were resolved from different
corpora still compare, on the words they have in common.

That the join key is a word, and that two analyses of it are the same analysis when `MatchesIWfiAnalysis`'s
shape agrees, was already settled by [ADR 0027](0027-what-counts-as-the-same-word-analysis.md). Decision 3
reinvented a coarser mechanism on top of a finer one that already existed.

A scope keeps its meaning as *what a run was told to do*, and is still embedded by content so a measurement
cannot be reinterpreted later. It is no longer a gate on comparison. The consequence for decision 4 stands
unchanged and is in fact the mechanism that replaces containment: a Report that needs data the run never
collected must say so, naming the reason — and that check is per-report, not per-comparison.

### 2026-08-29 — an Assessment is one *kind* of measurement, and one invocation yields several

Decision 1 implied one Assessor invocation produces one Assessment. It produces several, of different kinds.
One PanGloss run can yield: the size of the compiled engine; the time to parse a subset of words; per-morpheme
and per-rule timing over a subset; the correctness of parses against manual analysis; the difference between
two sets of automatic analysis; which words now complete that did not before.

So **Assessment kind** is part of an Assessment's identity, and it is what a comparison matches on alongside
the word. This also means a *difference* can itself be an Assessment — which collapses a distinction the
earlier draft kept apart, and is the more honest model: a delta is a measurement like any other, and storing
it is how it stays citable as evidence.

### 2026-08-29 — a Report is a presentation, and computing it may be delegated

Decision 4 treated a Report as a query Motif runs. Sharpened: an Assessor returns an Assessment in **raw
form** — a SQLite database, analyses of specific words — and the **Report is the presentation of that raw
material**. Producing one may mean handing the raw data back to the Assessor that owns its format, for
example asking PanGloss to interpret its own stats cache or to compare two sets of data. The result is then
rendered for a CLI reader or a FieldWorks view.

This does not move ownership. Motif still orchestrates, stores and cites; what changes is that Motif does not
assume it must compute every interpretation itself, and specifically must not reimplement a reading of
another system's format when that system can be asked. It is decision 8's reasoning applied one level up.

### 2026-08-29 — a Trial is the composite, and what survives an apply

The stage that produces both a Dry Run and a candidate's Assessments is a **Trial**, and `trial` is the verb.
It is a kick-off rather than a transition: a Proposal does not move when one is started, nothing is frozen,
editing continues, and a Proposal may have many across its life. See
[the Proposal lifecycle](../proposal-lifecycle.md).

Two consequences of "no state change" are worth stating here because they touch this ADR's decisions.

**A Trial may measure uncommitted content**, so its Assessments cannot pin to a revision. They cite the
draft's intent digest instead. That is consistent with decision 3's rule — a scope is embedded by content —
and it means two Trials either side of an edit are distinguishable and comparable without either being a
lie. `finalize` is no longer the gateway to measurement; it commits a text so a Decision can bind to exact
words, and nothing more.

**On apply, a Proposal's Trials, Dry Runs and Assessments become deletable**, by configuration in the
`<project>.motif.toml`, defaulting to on — the same instinct as deleting a branch when a pull request
merges. This completes the retention picture: Assessment rows are otherwise kept, stats caches are capped,
and a Proposal's working artifacts are swept when it lands.

**The trap in that, stated so nobody has to find it.** Applying *promotes* one candidate Assessment to be the
project's current one (decision 6). That Assessment is no longer the Proposal's working scratch — it is the
project's measurement and the baseline every future Trial compares against. A purge that treats it as one of
the applied Proposal's artifacts destroys the reference point for every later comparison, and the damage is
invisible until the next Trial fails to produce a delta. Promotion must therefore happen before the sweep,
and the promoted Assessment must be excluded from it by identity rather than by hoping the ordering holds.
