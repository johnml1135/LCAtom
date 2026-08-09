# ADR 0036 — Motif has its own data store, and only curated subsets cross into FieldWorks

**Status:** accepted, 2026-08-09. Generalises [ADR 0035](0035-reports-are-advisory-queries-over-stored-assessments.md)
decision 2 from "Assessments live in the proposal store" to a store in its own right. Bounded by
[ADR 0034](0034-the-boundary-with-fieldworks-state-versus-change.md).

**In plain terms:** the data Motif works with is about to be far larger than the project it describes. Pulling
text from Wikipedia and elsewhere to measure how much of a language a grammar reaches — and to build spelling
correction and word-prediction models from the same text — means tens to hundreds of megabytes. None of that
belongs in the FieldWorks project: it would bloat the file, and hand Chorus a merge it cannot do. **So Motif
keeps its own store**, and material crosses into FieldWorks only when somebody deliberately promotes a curated
subset of it.

## Why this outgrew the previous decision

ADR 0035 put stored Assessments in the proposal store on the grounds that an Assessment is a cache — losing it
costs a rerun, not data. That reasoning holds and this ADR does not disturb it. What changed is scale and
kind:

- **Corpora from outside the project.** A grammar's reach is measured against real running text, and the
  interesting text is not the project's own handful of interlinearised stories. At Wikipedia scale this is
  10–100 MB before any analysis.
- **A second consumer that is not the parser.** Spelling correction and word-prediction n-grams are built from
  the same text. That makes the corpus a first-class asset rather than a parser input.
- **Analyses at that scale.** Parsing 100,000 word forms produces roughly 64 MB of results
  ([ADR 0035](0035-reports-are-advisory-queries-over-stored-assessments.md)); a project accumulates several.

## Decision

### 1. Motif stores its own data, outside the FieldWorks project

Corpora, their provenance, Assessments, n-gram models and reports live in Motif's store. **Nothing about that
data enters the `.fwdata` by default.**

Two reasons, and the second is the one that forecloses the alternative. Size: a project file is a working
document a linguist copies, backs up and syncs, and multiplying it by an order of magnitude with material they
did not author changes what it is. And merge: Motif already has one unresolved risk from writing a few
kilobytes of applied log into the project, because **Chorus three-way-merges the `.fwdata` with no idea what
any of it means**. Handing it megabytes of corpus and analysis would turn a known risk into a certainty.

### 2. Curated subsets are promoted into FieldWorks deliberately

The store is not a walled garden. **A subset can be pulled into the project for analysis** — the words a
linguist wants to interlinearise, the stems worth adding to the lexicon, the analyses worth keeping. That
crossing is an explicit act, reviewable like any other change, and it is the only route in.

This is the shape the rest of the design already assumes. [ADR 0032](0032-stem-assessment-is-pangloss-supplied-lexicon.md)
puts stem evaluation in a throwaway overlay and gives Motif the job of **promotion** — the one thing PanGloss
declares a non-goal. Promotion from the corpus store is the same act with a different source.

### 3. A corpus is text; a Selection is a derived word set. They are different objects

This distinction was blurred and the blur was about to cost something. `CorpusDescriptor` deduplicates and
sorts, which is right for the set of distinct forms handed to the parser and **destroys exactly what the other
consumers need**: frequency ranks the unparsed-form worklist, and sequence is what n-gram models are built
from.

So the corpus keeps its running text, its order and its repetition. A Selection is derived from it, and
deduplication belongs there.

### 4. Every corpus states its origin and tokenisation; qualification is separate and usually absent

Recorded on the corpus, not alongside it:

- **Origin** — description, location, retrieval date, and **licence**. Wikipedia is CC-BY-SA, and a project
  publishing a dictionary derived from it carries an attribution obligation. The moment to record that is when
  the text arrives.
- **Tokenisation** — method, version, and what it did with punctuation, numerals, mixed script and case. At
  corpus scale **tokenisation decides most of what "unparsed" means**: a form invented by splitting on an
  apostrophe fails to parse and reads as a gap in the grammar. Two corpora tokenised differently are not
  comparable even when the source text is identical.
- **Qualification** — a named person's dated claim that the corpus is clean and in scope, with their reason.
  **Optional, usually absent, and its absence is meaningful.**

### 5. Reach and correctness need different corpora, and the code refuses to confuse them

A large uncurated corpus is **excellent evidence of reach** — what share of real running text a grammar
touches, and which unparsed forms are most frequent, which is the best available worklist for what to add next.
It is **worthless as evidence of correctness**, because a failed analysis there is ambiguous between a real
gap, a typo, and a token the grammar was never meant to cover. Those demand opposite responses, and one number
averaging them tells a project to work on whichever cause is loudest rather than whichever matters.

So a corpus without a qualification **cannot produce an accuracy figure at all** — not a footnoted one. The
report states that accuracy is not computable and why, and says reach figures remain valid. *"I could not
look"* must never read as *"everything is fine"*, and a precision figure over unvetted text is that failure in
its most persuasive form, because it looks like evidence.

## Consequences

- **The proposal store becomes the Motif store**, and its shape is now a real design question rather than a
  folder of JSON: corpora, Assessments and n-gram models have different lifetimes, sizes and pruning rules.
  Not designed here.
- **Promotion into FieldWorks needs a surface** — which words, which analyses, and what the record of that
  crossing looks like. Named by decision 2, not built.
- **FieldWorks has no model for a corpus attestation**, checked on 2026-08-09: `CmAgentEvaluation` carries a
  bare approve/disapprove with no date and no reason, and `Text` offers only a free-text `Source`. So this
  record has no FieldWorks counterpart to align with — unlike the parser-report counts, which
  [ADR 0033](0033-three-systems-and-who-owns-which-measure.md) deliberately mirrors.
- **Licence obligations become trackable**, which is a reason to want this beyond measurement: a project that
  cannot say where its word list came from cannot safely publish from it.
- **Risk accepted:** a store outside the project is a store outside the backup, the sync and the habits built
  around the `.fwdata`. Losing it costs reruns and re-pulls rather than authored work — everything in it is
  either cached or re-fetchable — but "everything in it is derived" is a property that must be *maintained*,
  and the first genuinely authored thing to land there breaks it.
