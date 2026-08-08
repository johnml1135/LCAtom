# ADR 0034 — The boundary with FieldWorks: state versus change

**Status:** accepted, 2026-08-08. Answers "are we duplicating FieldWorks, and should we go whole hog?".
Bounds [ADR 0033](0033-three-systems-and-who-owns-which-measure.md) decision 1 and
[the grammar-coverage design](../grammar-coverage-design.md).

**In plain terms:** we are not going to rebuild FieldWorks. The line is not *which numbers* we compute but
*which question* we answer. FieldWorks answers "how are things right now, for this word" — a linguist looking
at words. Motif answers "what did this particular change do, and why was it made" — a record of decisions.
When the same number appears on both sides that is a coincidence of arithmetic, not a duplicated feature. And
in five years the thing that will have justified Motif is the record, not the reports: reports get rebuilt in
every tool, but a durable history of *why the grammar is the way it is* exists nowhere and cannot be
reconstructed after the fact.

## The owner's sharper framing, which is right

> *"FieldWorks already has some reporting, but 'surface level' — comparing analysis to parsing on words, not
> asking 'this inflectional affix can be added to all verb classes, but it is only ever added to one — either
> in passive/auto or manual analysis. This is a gap' and the follow-on 'is this a word?'"*

That is the distinction exactly. `ParserReport` iterates **words**: for each word form, did it parse, and does
the parse agree with the human opinion recorded against it. Every number it produces is a sum over words.

The gap question is not a sum over words. It is a question about the **grammar's structure** — a declared
licence (this affix may attach to every verb class) set against observed use (it only ever attaches to one) —
and no amount of per-word reporting surfaces it, because every word involved parsed correctly. It is the same
shape as Black's Nahuatl case: nothing is wrong with any individual word.

## Is it duplication? Thinner than it looks

`MatchesIWfiAnalysis` (`ParseResult.cs:102`) compares **only LibLCM data**: `MorphBundlesOS.Count`, and per
bundle `MorphRA`, `MsaRA` and `InflTypeRA`. Nothing in the comparison is FieldWorks-specific. The only
FieldWorks thing involved is `ParseAnalysis` — the container holding *its parser's* output.

So the situation is not "Motif reimplements a FieldWorks feature". It is:

| | Parse results from | Compared against | Comparison rule |
| --- | --- | --- | --- |
| FieldWorks | its own parser, in-process | human opinions in the project | morph count + `MorphRA`/`MsaRA`/`InflTypeRA` |
| Motif | **PanGloss**, via a project file | the same human opinions | **the same rule** |

**Two different producers, one comparison rule, applied to the same stored judgements.** The code cannot be
shared without sharing a parse-result type, and the parse results genuinely come from different engines. What
must be shared is the *rule*, which is why [ADR 0033](0033-three-systems-and-who-owns-which-measure.md)
decision 1 keeps FieldWorks' field names and semantics rather than inventing a parallel definition. That is
the whole of the duplication, and it is a handful of counters.

*(Noted and not pursued: because the comparison is pure LibLCM semantics, it could be relocated into LibLCM
and shared. The shareable part is small and the parse-result types still differ, so this ranks below the two
upstream asks already queued in [the cross-repo plan](../plan-cross-repo.md).)*

## Decision 1: the boundary is state versus change

Apply this to any proposed report, and it decides without further argument:

> **Does this answer a question about the current state, or about a specific change?**
> State belongs to FieldWorks. Change belongs to Motif.

- *"Which of my words don't parse?"* — state. FieldWorks.
- *"Does the parser agree with what I approved?"* — state. FieldWorks, and Motif only because Motif must bind
  that number to a Proposal as durable evidence.
- *"What did this Proposal do to that number?"* — change. Motif.
- *"Which declared combinations does nothing exercise?"* — state, but structural, and nobody owns it, so Motif
  takes it ([ADR 0033](0033-three-systems-and-who-owns-which-measure.md) decision 3).
- *"Why is this rule the way it is?"* — change, historically. Motif, and **only** Motif.

## Decision 2: what Motif will not build, named now so the slide is visible

Each of these is locally tempting, each would be a worse copy, and each is where the boundary would fail:

- **A word-level analysis browser or editor.** Looking at words and fixing their analyses is what FieldWorks
  is for and is good at.
- **A bulk approve/disapprove surface.** Motif should make judgements *worth making* by reporting how many
  analyses are unjudged; collecting them is FieldWorks' interlinear workflow.
- **An interlinear text view.** Not remotely ours.
- **A second parser.** PanGloss's, always ([ADR 0033](0033-three-systems-and-who-owns-which-measure.md)).

**The five-year test**, and it is checkable rather than aspirational: *can a linguist do all of their
word-level work in FieldWorks and still get everything Motif offers?* If the answer ever becomes no — if using
Motif well starts to require doing word work inside it — the boundary has failed and this ADR is the thing to
re-read.

## Decision 3: gap-finding may use every analysis, whatever produced it

The owner's parenthesis — *"either in passive/auto or manual analysis"* — corrects a worry recorded in
[ADR 0033](0033-three-systems-and-who-owns-which-measure.md) decision 2 and materially improves the design.

That ADR treated thin human-judgement data as a limit on the whole measurement programme: two sampled projects
hold **8** agent evaluations between them. But for **gap-finding, provenance does not matter.** Sena 3 holds
**760** analyses. If a declared combination appears in none of them — machine-produced or hand-made — it is
unexercised, and that is a fact about the corpus and the grammar regardless of who or what wrote the analyses.
**So the denominator for coverage is ~760, not 8.** The programme is far better supplied than decision 2
assumed.

**But the asymmetry is strict and must be enforced, because ignoring it produces circular evidence:**

| | Evidence usable | Why |
| --- | --- | --- |
| **A gap** (combination unused) | **All analyses** | Absence is absence. Nothing produced it, by any route |
| **Over-broad** (combination used but wrong) | **Human judgement only** | A machine analysis produced *by* the suspect rule cannot vindicate that rule |

A machine analysis is evidence a combination *occurs*; it is never evidence the combination is *correct*.
Conflating the two would let an over-broad rule generate its own justification — the grammar marking its own
homework, and precisely the trap the owner's distinction between the two over-generations exists to avoid.

## Consequences

- **Coverage work is unblocked and better supplied than recorded.** Gap-finding runs against all 760 Sena 3
  analyses rather than waiting on human judgement.
- **Every report must state its evidence class.** A figure derived from all analyses answers a different
  question from one derived from human-judged analyses, and a reader who cannot tell which will draw the wrong
  conclusion from the stronger-sounding one.
- **`ADR 0033` decision 2 is narrowed, not withdrawn.** The unjudged-analysis count remains the leading
  indicator for *precision*; it was never the constraint on *coverage*.
- **The named non-goals are a review criterion.** A proposal to add any of decision 2's four items should be
  declined by pointing here, or should amend this ADR deliberately — the failure mode is a series of individually
  reasonable additions.
- **Risk accepted:** keeping FieldWorks' field names means Motif's counters can drift from FieldWorks' if either
  changes. A conformance fixture against a real `ParserReport` JSON would catch it and is still not built —
  recorded in [ADR 0033](0033-three-systems-and-who-owns-which-measure.md) and still true.
