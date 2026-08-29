# ADR 0035 — Reports are advisory queries over stored Assessments

**Status:** accepted, 2026-08-09. Scopes `MOT-19` and `MOT-17`. Builds on
[ADR 0033](0033-three-systems-and-who-owns-which-measure.md) and
[ADR 0034](0034-the-boundary-with-fieldworks-state-versus-change.md), and applies
[ADR 0009](0009-layered-api-primitives-and-composers.md) §1's non-hashed-provenance rule to a new case.

**In plain terms:** PanGloss parsing a project's texts is slow — extrapolated at 100,000 word forms it runs
from about a minute to several hours depending on the language and the machine. So that result is **stored**,
and everything Motif calls a report is a cheap query over it: a few searches and some statistics. Reports never
block anything, even when a project has configured a target. A proposal's report is pinned to the stored
analysis it was computed against, so someone importing a text does not silently make forty pending reports
lie. And which words a report covered is recorded on the proposal but deliberately left out of the proposal's
identity, so a reviewer can run a quick check or a full one — whichever gives them the confidence to merge —
without disturbing the proposal or its approvals.

## Why this needed deciding

`MOT-19` already established that most of the read surface is ephemeral, and that *"the moment a query's output
is cited as evidence on a Proposal it becomes a Check Run."* What it did not settle is where the expensive
input comes from, what happens when it goes stale, and whether a project-configured target has teeth. Those
three interlock, so they are decided together here.

Measured this week and extrapolated to 100,000 word forms, which is the scale the owner named:

| grammar profile | engine | 20 cores | 4 cores |
| --- | --- | --- | --- |
| concatenative | FST | ~1.2 min | ~5 min |
| Amharic-like | FST | ~6 min | **~29 min** |
| Amharic-like | HermitCrab fallback | ~1.8 h | ~9 h |

Storage scales with it: about 645 bytes per word form, so roughly **64 MB per Assessment** at that size. And
one sampled project could not be analysed by either engine at all. **The design therefore cannot assume a
rerun is cheap, and must stay usable while one is outstanding.**

## Decision

### 1. Reports are advisory. A configured target does not gate

A project may configure what it cares about — *never go backwards*, *we want 70%* — and a breach produces a
finding, loudly. **It stops nothing.** No apply is refused, no approval is withheld mechanically.

Two options were considered and declined. Blocking the apply would make Motif refuse linguistic work on its
own arithmetic, and a wrong threshold blocking correct work is the failure nobody forgives. Failing a Check
Run to block *approval* was attractive — it would have put the threshold in the project's hands rather than
ours — and was declined for now as more machinery than the evidence justifies. Nothing in this decision
prevents adding it later; the finding already exists, and gating on it is additive.

This keeps the glossary's existing rule on Assessment literally true: *Motif never renders a verdict from
one*. A report may **advise**, including "this is going the wrong way". It may not **decide**.

### 2. Stored Assessments live in the proposal store

> *Renamed 2026-08-09 by [ADR 0036](0036-motif-has-its-own-data-store.md): the proposal store is now the
> **Motif store**, because it holds Corpora and Assessments as well as Proposals. This decision is unchanged —
> the store simply outgrew its name.*

Not in the project. The one thing Motif already writes into a project — the applied log — is a standing
unresolved risk precisely because **Chorus three-way-merges the `.fwdata` with no idea what any of it means**.
That is a few kilobytes. An Assessment is tens of megabytes per run, and merging two of them would produce
nonsense that looks like data.

The proposal store is already project-scoped, already Motif's, and already outside that sync. **What makes
this safe is that an Assessment is never authoritative** — it is a cached observation of a project state,
identified by the hashes it carries. Losing it costs a rerun, not data. A linguist who copies only the
`.fwdata` loses the cache and nothing else.

### 3. A Proposal's report pins the Assessment it was computed against

**Not** reused against whatever Assessment is newest. [ADR 0028](0028-feeding-reorders-require-a-grammar-delta.md)
already names the failure: *"A Grammar Delta computed across two different baselines attributes someone else's
change to this Proposal."* Reusing a proposal's analysis against a later baseline does exactly that — every
text someone imported in the meantime gets credited or blamed to the proposal.

The alternative — a new Assessment invalidates every proposal's report — is honest but unusable at these
runtimes: importing a text is routine, and greying out forty pending reports for half an hour teaches people
to ignore the staleness marker, after which it never works again.

So Assessments are kept as **bounded history**: the current one, plus any a live Proposal still pins. A report
states which Assessment it stands on, and a reader can see whether that is the current one.

**This decision is deliberately forced by cost uncertainty rather than by measurement.** If a rerun turns out
to take five seconds, pinning costs nothing and is invisible. If it takes forty minutes, pinning is the only
option that leaves the system usable. It is the choice that is never wrong, which is why it did not wait for a
real runtime.

### 4. Staleness is a best-effort change summary, and honest about its limits

A report says whether its Assessment still describes the project, summarised in three buckets — **words**,
**grammar**, and **texts and analyses**, each added or changed.

**Where Motif made the change itself it can attribute precisely**, because the applied log records it. Where
the project changed outside Motif's knowledge it cannot, because `.fwdata` carries no change history
([ADR 0031](0031-collaboration-follows-the-data-not-the-surface.md)) and tracking every feature of the language
to reconstruct one is not worth it. In that case the report says **"this Assessment is invalid — rerun"** and
stops there.

**Rerunning is always the reader's decision**, never automatic. At half an hour, a system that reruns on its
own initiative is a system that is busy when you need it.

### 5. A Selection is carried on the Proposal as non-hashed provenance

Which words a report covered is a **Selection**: defined by a query, pinned as an exact hashed list each time
it runs. It travels with the Proposal — *"this one is about the verb paradigm"* is part of the record — but it
is **outside the intent digest**.

**The decisive argument is a consequence, not a principle.** If a Selection were part of the digest, narrowing
it so a rerun takes four minutes instead of forty would produce a **new revision of the Proposal** — which
invalidates its bound Dry Run anchor and discards any approvals it had collected. Changing *what you measure*
would invalidate the review of *what you are changing*.

Two precedents point the same way. [ADR 0009](0009-layered-api-primitives-and-composers.md) §1 already
specifies resolved operations at rest **with the query as non-hashed provenance**, and `J42a` was decided as
*record what the query matched, because drift protects the operations and not the query's intent.* A Selection
is a query about what to measure; measuring changes nothing in the project.

**And this is what makes the confidence dial work**, which is the reason it matters in practice rather than in
theory: a reviewer runs a quick check on a small change and a full one before merging something large, choosing
how much evidence the decision needs — and switching between them disturbs neither the Proposal nor its
approvals.

## Consequences

- **A Selection needs a query language, and it starts at three kinds**: all word forms; the words in named
  texts; and the outcomes of a previous run (failed, timed out, unanalysed). The third is what makes a
  cheap first pass followed by a slower second pass expressible with no scheduler to design. Adding a fourth
  kind should require a reason.
- **Word-only changes skip the FST build.** Adding stems or texts leaves the grammar untouched, so
  [ADR 0032](0032-stem-assessment-is-pangloss-supplied-lexicon.md)'s supplied-lexicon overlay applies and the
  build is avoided; a grammar change pays it.
- **Importance is expressed by naming a Selection, not by scoring words.** No priority model, no per-word
  weights — deferred deliberately, and nothing here forecloses one.
- **Bounded history needs a pruning rule** — an Assessment no live Proposal pins and that is not current is
  deletable. At 64 MB a run this is a real obligation, not housekeeping.
- **Closed 2026-08-09, and it did not need a runtime after all: a Report waits for its Assessment to finish.**
  No partial results, no lower bound computed from an incomplete run. An earlier draft proposed reporting over
  the first 30% of a corpus, labelled — and that is wrong for a sharper reason than untidiness. A
  `CorpusDescriptor` sorts its word forms ordinally, so a partial run is an **alphabetic prefix**, which makes
  any statistic over it *biased* rather than merely imprecise: it is not a sample of the corpus, it is a sample
  of the start of the alphabet. Reporting it as a lower bound would imply an unbiased estimate that the
  processing order cannot support. A Report against an in-flight Assessment is simply not available yet.
- **Risk accepted:** decision 1 means an unattended agent loop can breach a target the project explicitly set
  and carry on. That is the cost of not inventing a gate, and it is revisitable the moment someone wants the
  Check Run path from the declined option.

## Amendments

### 2026-08-29 — decision 1 is narrowed: a project may configure a regression to gate

Decision 1 says reports are advisory and a configured target does not gate.
[ADR 0042](0042-a-job-produces-assessments-an-assessor-makes-them.md) decision 5 narrows that.

A project may declare in a human-readable `<project>.motif.toml` that a regression blocks `apply`. The model
is a failing check on a pull request rather than a hard rule: it blocks, a human may override it, and the
override is recorded as a Decision with an actor and a comment — the machinery `approve` and `reject` already
use.

What survives unchanged is the reasoning this ADR was built on. A report is still a cheap query over a stored
Assessment, still pinned to the Assessment it was computed against, and still carries its Selection as
non-hashed provenance so a reviewer may run a quick check or a full one without disturbing approvals. What
changes is only that a project may now choose to make one of those queries load-bearing, and that choice is
visible in a file a human can read and diff.

The reason for the change is that a regression is worth stopping on even when it turns out to be wrong: an
approved analysis that no longer parses is as often a wrong manual analysis as a bad grammar change, and
surfacing it is how that gets found. Advisory-only could not express "stop, look at this, then decide".
