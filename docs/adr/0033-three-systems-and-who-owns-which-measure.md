# ADR 0033 — Three systems, and which measure each one owns

**Status:** accepted, 2026-08-08. Refines
[ADR 0032](0032-stem-assessment-is-pangloss-supplied-lexicon.md) and the
[grammar-breadth research](../research/2026-08-08-grammar-breadth-black-2025-and-a-path.md). Scopes `MOT-19`.

**In plain terms:** three systems are involved and Motif is the middle one. PanGloss answers "did this word
parse, and how many of a known-good and a known-bad list did I get right." FieldWorks — not Motif — already
defines "does the parser agree with what a human decided," including the count of analyses a human explicitly
rejected, which is the over-generation number we were about to invent. Motif's job is to make those numbers
available headlessly, bind them to a proposal as durable evidence, and add the one measure nobody owns:
whether the words we tested actually exercised the grammar we wrote. The linguist, human or AI, asks Black's
question — *what would make this better* — and Motif's job is to hand them the evidence, not the answer.

## The division, corrected against the source

| Question | Owner | Status |
| --- | --- | --- |
| Did this word parse? What share of a **known-good** list parsed? Of a **known-bad** list? | **PanGloss** | **Already supported.** Suites carry expectations, and `pg-assess`'s golden module documents the encoding for *"this form should not parse at all"* — an empty complete set satisfies it. `closedWorld` plus `required`/`allowed` express the rest |
| Does the parser agree with **what a human decided**? | **FieldWorks defines it; Motif delivers it** | Definitions, JSON format, run-to-run diff and a UI already exist. See below |
| Did our words **exercise the grammar we declared** — every morpheme, feature, template, slot? | **Motif** | **Unowned by either.** Nothing in FieldWorks' `ParserCore` tracks it; PanGloss's interaction coverage is about its own compiler's plan nodes, not about a language's corpus |
| *What would make this grammar better?* | **The linguist**, human or AI | Motif supplies reports. It does not answer this |

## FieldWorks already owns the measure we were about to build

`Src/LexText/ParserCore/ParserReport.cs` computes, per word and in total:

| Field | What it means |
| --- | --- |
| `NumWords`, `NumZeroParses`, `NumParseErrors` | Parse coverage and failures |
| `TotalAnalyses` | How many analyses were produced — ambiguity, in aggregate |
| **`TotalUserApprovedAnalysesMissing`** | An analysis a human **approved** that the parser no longer produces. **Recall against human judgement** |
| **`TotalUserDisapprovedAnalyses`** | An analysis the parser produces that a human **rejected**. **This is the grammar-breadth measure** |
| `TotalUserNoOpinionAnalyses` | Produced, and nobody has judged it |
| `TotalParseTime` | Cost |

And the semantics are the ones this repository already chose. `ParseReport`'s constructor walks the wordform's
analyses, reads each one's `GetAgentOpinion(userAgent)`, and matches parser output against them with
**`MatchesIWfiAnalysis`** — the exact method [ADR 0027](0027-what-counts-as-the-same-word-analysis.md) settled
on as the equality gate. `GetDiff` subtracts an older report from a newer one, so *"here are the new ones since
the last run"* exists too, and `WriteJsonFile`/`ReadJsonFile` give it a serialised form.

**So Motif is not inventing this metric; it is making it reachable.** What FieldWorks has is a desktop feature
over a live cache. What Motif needs is the same numbers computed headlessly, bound to a Proposal, and durable
in a Receipt.

### Decision 1: adopt FieldWorks' definitions and its JSON shape; do not invent a parallel vocabulary

Motif reimplements these counts — `ParserCore` is FieldWorks application code and cannot be referenced from
scope 1, the same constraint that ruled out `HCLoader`
([the seam measurement](../research/2026-08-07-parser-seam-goes-through-the-project-file.md)) — but it
reimplements *the definitions*, keeping the field names and the JSON shape.

**The reason is not tidiness.** A linguist who opens FieldWorks sees `TotalUserDisapprovedAnalyses`. If Motif
reports a differently-defined number under a different name, the two disagree in review and nobody can tell
whether the grammar changed or the metric did. This is the same argument that made kind descriptions seed from
FieldWorks' own labels ([ADR 0023](0023-derived-kind-names-required-descriptions.md)) and the same one that
made the analysis-equality gate `MatchesIWfiAnalysis` rather than a fresh comparison.

### Decision 2: disapproved analyses are the breadth signal, and the sparse data is a finding not an obstacle

The [research note](../research/2026-08-08-grammar-breadth-black-2025-and-a-path.md) proposed measuring
grammar breadth by agreement against existing analyses. **`TotalUserDisapprovedAnalyses` is that measurement,
already specified**, and it is strictly better than what was proposed: an analysis a human actively *rejected*
is far stronger evidence than one that merely differs from what was recorded.

But two sampled projects hold **8 `CmAgentEvaluation` objects between them** against 7,646 word forms
([ADR 0031](0031-collaboration-follows-the-data-not-the-surface.md)). The mechanism exists throughout — model,
report, and UI — and projects barely use it.

**Narrowed 2026-08-08 by [ADR 0034](0034-the-boundary-with-fieldworks-state-versus-change.md) decision 3:**
thin judgement data limits *precision* only. **Gap-finding may use every analysis whatever produced it**, so
its denominator in the 152,222-object project is ~760 rather than 8 — absence is absence regardless of who wrote the analyses. The
strict asymmetry is that a machine analysis is evidence a combination *occurs* and never evidence it is
*correct*, since a rule cannot vindicate itself through the analyses it generated.

**So the number is right and the data is thin, which changes what Motif should do about it.** Not invent a
different metric, but make disapproving cheap and make its value visible: a report that says *"this grammar
produces 340 analyses nobody has judged"* is actionable, where a precision figure computed over 8 judgements
is not. **The no-opinion count is the leading indicator; the disapproved count is the lagging one.** Report
both, and never present a precision figure derived from a handful of judgements as though it characterised the
grammar.

### Decision 3: Motif owns grammar coverage, because the join is the thing Motif already built

> *Renamed 2026-08-09: the measure this decision assigns to Motif — **did our words exercise what we
> declared** — is now called **feature coverage**. "Grammar coverage" was reassigned to the measure people
> reach for it to mean, how much of a language the grammar reaches. The decision is unchanged; only the name
> moved. See `CONTEXT.md`, Coverage and generation.*

Did our words exercise what we declared? Neither other system can answer it well, and Motif can answer it
cheaply, because it is a set difference over GUIDs Motif now holds on both sides:

- **What was declared** — every morpheme, inflection feature, affix template, slot, inflection class and
  exception class in the project. Motif reads these field by field; the emitted catalog already covers 34
  grammar classes.
- **What was exercised** — the morpheme and category GUIDs appearing in parse results, which is precisely what
  the project-file route yields and the reason it was chosen over HermitCrab XML.

Both sides are FieldWorks GUIDs, so the join needs no heuristics. **This is the payoff of the identity work
rather than a new capability**, and it produces the report a linguist can act on directly: *you declared 25
affix slots and the corpus exercised 19; here are the six with no attestation.*

**And it is interactions, not a checklist.** The owner's refinement: *"really, it's multiple grammar features
and their interactions. I want reports, stats and 'holes' that may point to over broad rules or missing analysis
words."* Black's canonical over-generation case is a slot *pair* — Orizaba Nahuatl licensing `ti-` without the
`-h` that 1pl requires — where every individual feature is exercised and correct. So the level that finds the
defect is the combination, and a hole is deliberately ambiguous between an over-broad rule and a missing word.
The design is [grammar-coverage-design.md](../grammar-coverage-design.md).

**Why it belongs here and not in PanGloss.** PanGloss holds the compiled grammar but not the notion of a
reviewable declared inventory, and its coverage vocabulary already means something else — capability coverage
and plan-interaction coverage, both about its own compiler. Putting a third meaning of "coverage" in that
repository would be a naming collision on a concept that is review-facing, and review surfaces are Motif's
([ADR 0021](0021-cli-is-the-full-surface-layer-1-churns.md)).

### Decision 4: Motif reports; it does not answer *"what would make this better"*

Black's guide is 108 pages of constraint mechanisms with an explicit escalation order and no detection
methodology — it assumes a linguist reads a wrong parse and knows what to reach for. Motif supplies the
evidence that makes reading unnecessary: what failed, what a human rejected, what nobody has judged, what was
never exercised, and what changed since the last run. **Choosing the fix stays with the linguist**, human or
AI, and the ad hoc rule census is where Motif comes closest to the line — it reports that a cluster exists,
which Black says *"may be a hint"*, and stops there.

## Consequences

- **The three "coverage" words must stay distinguishable.** PanGloss has capability coverage and
  plan-interaction coverage; PanGloss also measures parse coverage over word lists; Motif has grammar
  coverage. Whatever surfaces these must name which one it means every time — an unqualified "coverage" in
  this project is now ambiguous three ways.
- **`MOT-19`'s report set is now enumerable** rather than open-ended: parse coverage against known-good and
  known-bad lists, the four human-agreement counts, grammar coverage, the ad hoc census, and run-to-run deltas
  of each.
- **A known-bad word list is a project asset nobody currently curates.** PanGloss can consume one; no project
  has one. Building the mechanism without seeding the list produces a metric that always reads 0%.
- **Risk accepted:** reimplementing FieldWorks' counts means they can drift from FieldWorks'. Mitigation is
  the field names and JSON shape, which make a disagreement visible as a diff rather than as two numbers
  nobody compares. A conformance fixture against a real `ParserReport` JSON would close it properly and is not
  done.
