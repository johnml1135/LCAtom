# ADR 0038 — Expectations are FieldWorks' approved analyses; Motif reads them in aggregate

**Status:** accepted, 2026-08-09. Refines [ADR 0033](0033-three-systems-and-who-owns-which-measure.md) on who
owns which measure, and [ADR 0035](0035-reports-are-advisory-queries-over-stored-assessments.md) on reports
being advisory queries.

**In plain terms:** when someone changes a grammar rule, the question is *did that help, and did it break
anything*. The answer needs something to check against. **We are not building that something — FieldWorks
already has it.** A linguist's approved analyses are exactly a set of assertions about what particular words
should mean, which is what a test is. Motif reads them, runs the parser, and reports where the two disagree.

The analogy that shaped this, from the owner: **approved analyses are unit tests; running text is code
coverage.** One is a small set of deliberate assertions; the other is broad evidence about how much of the
language the grammar reaches.

## Decision

### 1. A Selection is an explicit list of words, not a query

We nearly built a query language with pinned results and a re-run lifecycle. It was wrong, and the reason is
worth keeping: **a query can only express sets that have a unifying principle**, and the normal case has
none. A person has ten or twenty words they care about for ten or twenty different reasons.

So a Selection is the words, listed out, with a note of where they came from. A list can be *exported* from an
Assessment — "everything that failed to parse" — but what is stored is the words. Nothing is re-derived, so
nothing goes stale, and the elaborate provenance chain a re-runnable query would have needed does not exist.

**What we give up:** re-running the same intent against a corpus that has since grown is a fresh manual
export rather than a button. Accepted.

### 2. Expectations live in FieldWorks; the type-level composite is the test

Two things look alike and are not:

- **One occurrence being manually analysed** is the *authoring gesture*. It needs to survive churn well
  enough not to lose the thread, and it **makes no grammatical claim** — which reading a particular sentence
  got is disambiguation, not grammar.
- **The composite of all approved analyses on a word form** is the *durable test*. Adding one establishes a
  test, changing one updates it, removing the last removes it.

So positive expectations need nothing new from Motif. They already exist, in the right grain, in the language
project — where they belong, because an approved analysis is a linguistic fact that outlives Motif.

**A word form can carry several approved analyses at once**, because genuinely ambiguous forms have more than
one correct reading. The unit is therefore a *set*, never "the analysis".

### 3. Key on the word form, compare analyses by content

`WfiAnalysis` GUIDs are **not durable identity**, verified in the FieldWorks source on 2026-08-09:

- Editing a breakdown to something that matches no existing analysis creates a **new** `WfiAnalysis` and
  **deletes the old approved one outright** (`FocusBoxController.ApproveAndMove.cs:404-405`).
- Whether the GUID survives depends on **how many other occurrences share it** — `OnlyUsedThisOnce` mutates
  in place when it is used once and forks a new object when it is shared. The same edit produces different
  identity outcomes depending on unrelated state.
- `Segment.AnalysesRS` is a **reference** sequence, not owning (`MasterLCModel.xml:268`), so one analysis is
  shared across arbitrarily many occurrences.

`WfiWordform` GUID is stable. So records key on the word form, and analyses are compared **by content** —
`MatchesIWfiAnalysis`, which is FieldWorks' own definition of "same analysis" and ignores identity entirely.

**A caveat this leaves standing:** a word form is found-or-created *by its text*, so correcting a spelling in
a text repoints the occurrence at a **different word form** and may delete the old one. The `Form` field never
changes. Watching `Form` therefore catches spelling edits made directly and misses the ordinary case
entirely.

### 4. The before-state must be captured, because FieldWorks destroys it

Update and delete-then-create are the same operation in FieldWorks, and nothing retains what a test used to
be. So *"this was this, and is now this"* is **not answerable after the fact** — the previous approved
analysis is gone.

The approved analysis set therefore belongs in the **comparison footprint**
([conflicts-and-rebase.md](../conflicts-and-rebase.md)) of any change set that touches it. That is existing
machinery, not new machinery, but omitting it makes the query silently return half an answer.

**The footprint for an analysis change set is:** word form GUID, its `Form`, the content digest of its
approved analysis set, and **existence**. Not the occurrence list — occurrence references change every time
anyone edits any text containing the word, almost never affecting what the change set means, and a footprint
that goes stale constantly trains people to ignore staleness. Existence is in because `DeleteIfSpurious`
collects unreferenced word forms, and a change set targeting a collected word form is not stale but broken.

### 5. One read API, and "what changed" is the difference between two responses

Rather than a change-tracking type, there is a query: **per word form, the aggregate of its analyses — manual
and automatic** — with links through to the words, counts and instances per manual analysis, and an option to
run the parser over all of them and compare against what is recorded.

**This is a Report, not a new concept.** `CONTEXT.md` already defines one as *"a query over an Assessment and
the project's own data, producing statistics and findings — advisory always"*. That is precisely this, so it
inherits the existing rules rather than needing its own: it gates nothing, and it renders no verdict.

**Change falls out of diffing two responses.** Differences in the automatic analyses are grammar coverage
moving; differences in counts are text churn; what remains — differences in the manual analyses — is a test
being established, updated or removed. Nothing separate to design, nothing to keep in sync.

**Established, updated and removed are always reported separately and never netted against passing.**
Deleting the last approved analysis on a word form improves every aggregate while reducing what is checked —
the grammar equivalent of deleting a failing test. It is also *routinely correct*, which is why it is
reported rather than warned about: flagging the honest case as a transgression is how people learn to route
around warnings.

### 6. Motif does not attribute cause

An earlier draft split results into "matched because the grammar improved" and "matched because the
expectation moved". **That was wrong** — the evidence does not support it. We know the analysis changed and
we know it passes now; which caused which is exactly what we cannot see, and the grammar change may well have
helped the words whose analyses also changed.

So Motif reports the facts side by side and declines to infer. This is the same rule that makes a **Hole**
undecided by construction.

### 7. A word with no analysis gets no expectation — only a counted, explicitly weak signal

Added 2026-08-09, closing the gap decision 2 left open.

Earlier drafts wanted a home for *this word is correctly spelled and should nevertheless not be analysed* — a
borrowed proper noun, a code-switch — because FieldWorks cannot say it. **We are not building one.**

**Wait until there is an analysis.** A word form that nobody has analysed carries no expectation, and Motif
does nothing about it. The only thing reported is a count: **of the correctly-spelled word forms with no
manual analysis, how many does the grammar parse.**

**That number is weak evidence, and it is weak in a specific direction that matters here.** Nobody has checked
these words, so a parse is not known to be a *correct* parse. A rising number is equally consistent with the
grammar improving and with the grammar getting looser — and looseness is the failure mode this project exists
to catch. So it can support a claim about **reach** — a floor under how much of the language is touched — and
never a claim about correctness. Same asymmetry [ADR 0036](0036-motif-has-its-own-data-store.md) decision 5
applies to unattested corpora, one layer down.

It is therefore reported plainly and used for one thing: **assessing a claim of complete language coverage.**
Outside that, it should not be quoted, and the report says so.

## Consequences

- **`MOT-23`** — the aggregate read API above. Not built.
- **`MOT-22`** — `WfiWordform.SpellingStatus` read and written by Motif, so "that one isn't a word" sticks.
- **The only negative Motif records is "not correctly spelled"** (`MOT-22`). *Correctly spelled but should
  not be analysed* is deliberately not modelled — decision 7. `HumanAndParserAgree` exists in liblcm as
  `throw new NotImplementedException()`, so that ground was surveyed once and abandoned upstream too; worth a
  conversation if the weak signal ever proves insufficient, and not worth blocking on.
- **A useful interaction:** `DeleteIfSpurious` refuses to delete a word form whose `SpellingStatus` is not
  `undecided` — *"we know something about it that we don't want to forget"*. Marking junk as `incorrect` is
  therefore what stops it evaporating and taking the negative evidence with it.
- **Terminology moved**, recorded in `CONTEXT.md`: **feature coverage** is the measure of which declared
  features and combinations the analysed words exercise; **grammar coverage** now means how much of a
  language the grammar reaches. `GrammarCoverageFigure` and `ModelCoverageReport` were renamed to match.
- **Deferred deliberately:** spelling corrections — a mistyped form paired with what was meant — are a
  separate Motif data type, not designed until spelling correction is built.
