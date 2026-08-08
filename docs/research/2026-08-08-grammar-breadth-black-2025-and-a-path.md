# Grammar breadth: what Black (2025) says, and a path to measuring it

**In plain terms:** the thing worth measuring is whether a grammar accepts word forms the language does not
actually have. Andy Black's guide — the methodology this work follows — names that failure twice in the same
words, gives a worked example, and catalogues the mechanisms for fixing it, in an explicit order with ad hoc
rules last. What it does **not** give is any way to *find* the problem: it assumes a linguist notices a wrong
parse by reading it. That gap is the opening for Motif, and it cannot be closed by a coverage number, because
**over-generation is invisible on a list of real words** — every real word is supposed to parse. Measuring it
needs either something to compare against or the grammar run backwards, and both are available.

## Two different over-generations, and only one is ours

The owner's distinction, and it matters because a metric aimed at the wrong one is worse than none:

| | Whose problem | Status |
| --- | --- | --- |
| **The FST proposer is loose** — it proposes candidates HermitCrab then rejects | **PanGloss's**, internal | Expected and handled. We should never see it |
| **The grammar is too broad** — it licenses combinations the language does not have | **Ours** | The only one Motif should measure |

PanGloss says the first is not a correctness matter in as many words. Its own research notes list
"over-relies on confirm to prune (proposer looseness)" as a trigger for grammar *optimisation*, measured by
`RejectionShare`, `ProposalCandidateCount`/`ConfirmationCount` and `DuplicateAnalysisRatio` — and record that
*"a high rejection share is expected overapproximation evidence, not itself a correctness problem."* Every one
of those metrics is pinned at `Info` severity regardless of magnitude, deliberately.

**So proposed-versus-confirmed counts measure PanGloss's efficiency, not our grammar's breadth.** An earlier
draft of this thinking proposed exactly that as a cheap proxy for grammar over-generation. It is not one, and
it would have looked plausible for a long time.

## What Black actually says

*"A Conceptual Introduction to Morphological Parsing for FieldWorks Language Explorer"*, H. Andrew Black, SIL,
3 July 2025, 108 pages.

### The parser's first task is rejection, so breadth is a first-order defect

Black's three tasks for any parser begin with *"see if it is a legitimate word"* — before segmenting it or
glossing it. And the framing of the whole guide:

> *"Clearly, **properly using and controlling the constraints is the major task** in implementing a parser for
> a given language."*

The 108 pages are a constraint catalogue, split into morphotactics (which morphemes may co-occur, in what
order) and morphophonemics (what shape a morpheme takes where). **Grammar development, in this methodology,
*is* constraint authoring** — so a grammar that accepts too much has failed at the primary task, not at a
refinement of it.

### He names our failure mode twice, in nearly identical words

§2.1.6, on exception features:

> *"sometimes a morphological parser will find combinations of stem and affix that are **simply incorrect**.
> This may be due to historical or some other seemingly arbitrary reasons."*

§2.4, on ad hoc rules:

> *"it is not unusual for the parser to sometimes return **a parse that is simply incorrect**. These are
> sometimes due to allomorphs matching in places one would not have expected them to match."*

And the worked example, Orizaba Nahuatl: modelling the absolutive suffix as deriving nouns from verbs is
correct for `tlakuika-tl` 'song', and it then licenses a spurious reading of `komitl` 'jug' as `*kom-i-tl`
'jug-drink-abs'. **One correct generalisation, one bogus extra analysis of an ordinary word.** That is the
shape of the defect, and note what it looks like in a coverage number: nothing. `komitl` parsed before and
parses now.

### The fixes, in his escalation order

Principled mechanisms first — categories and co-occurrence, inflection classes and features, exception
features (§2.1.6), *separate templates instead of one template with optional slots* (§2.1.2.1–3), allomorph
environments and ordering (§3.1.3–4), restricting a compound rule's productivity (§2.2.6). Then, and only
then:

> *"When one has used **all** the mechanisms provided by the parser to the best of one's ability and such
> incorrect parses continue to surface, one may well wish for some kind of mechanism to rule them out.
> FieldWorks Language Explorer provides 'Ad hoc Rules' for such situations."*

Ad hoc rules come in two kinds — morpheme-oriented (§2.4) and allomorph-oriented (§3.10) — each with a ladder
of tightness: *Adjacent before / Adjacent after / Somewhere before / Somewhere after / Anywhere*.

### The most actionable thing in the guide is a footnote

Footnote 39:

> *"One approach to this is to **strive to make the tightest constraint possible** (i.e., use one of the
> adjacency ways first if possible; if not, then try the somewhere case; if that does not work, then try the
> anywhere case). That way, should you encounter another case involving these particular morphemes, then you
> will know more: it is now clear that you need looser constraints. **You can then add some
> comments/annotations to document what you have learned.**"*

Tightest constraint first; loosen only when a counter-example forces it; **write down what the counter-example
taught you.** That is a documented discipline with a documented artifact — and the artifact is exactly the
rationale record [ADR 0031](../adr/0031-collaboration-follows-the-data-not-the-surface.md) already decided the
review milestone exists to produce.

### And a diagnostic he states outright

§2.4.2, on grouping ad hoc rules:

> *"Occasionally one finds a situation where a set of ad hoc constraints have a common theme... **This may be
> a hint as to what is really happening and may lead you to discover a linguistically-motivated way to model
> them.** Or it could be that the FieldWorks Language Explorer model just does not happen to provide the
> appropriate linguistic mechanism to model the phenomenon correctly."*

**A cluster of ad hoc rules means a generalisation is missing.** That is a measurable property of a grammar,
it needs no parser run, and Motif can already see it — the emitted catalog covers `MoMorphAdhocProhib` and
`MoAlloAdhocProhib`.

### The gap: he gives no way to find the problem

Searching the full text for a detection or evaluation methodology — approve, disapprove, test the parser,
word list, corpus — returns **nothing**. The guide tells a linguist how to fix an over-broad grammar once
they have noticed a wrong parse. Noticing is left to reading. On a project with 6,973 word forms and a loop
that will be driven by a machine, that is the part Motif has to supply.

## Why coverage cannot see it, stated plainly

Over-generation on a corpus of attested words shows up in exactly two ways, and coverage counts neither:

1. **Extra analyses of words that already parsed** — the Nahuatl case. Coverage is unchanged.
2. **Acceptance of forms that never appear in the corpus at all** — invisible, because nothing asks.

So a grammar can be loosened until it accepts nonsense while coverage rises monotonically. Coverage is recall
and nothing else. **Measuring breadth needs either a comparison target or the grammar run backwards.**

## Four candidate signals, cheapest first

**1. Ambiguity growth — analyses per word, as a delta.** Free with any run we already do. A change that
raises the mean analyses per word without raising coverage is adding readings, not reach. Black treats
ambiguity as a genuine linguistic phenomenon (§1.1.3), so this is evidence and never a verdict — but it is the
Nahuatl signature exactly: `komitl` goes from one analysis to two.

**2. Agreement with the analyses a project already holds.** For every word form that already has an analysis,
does the parser still produce it, and does it now produce *others*? A new reading of a word a human already
settled is the strongest over-generation evidence available. Uses the comparison
[ADR 0027](../adr/0027-what-counts-as-the-same-word-analysis.md) already settled — morph count, and per
morpheme the allomorph, category record and inflection type. Sena 3 holds 760 analyses, which is signal even
though only 8 carry a human evaluation.

**3. Ad hoc rule census and tightness audit — Black's own diagnostic, and uniquely ours.** Count ad hoc rules,
flag clusters sharing a morpheme (§2.4.2's "hint"), and flag any rule looser than it needs to be (footnote 39).
**No parser run at all** — it reads the grammar. It also gives the rationale record something concrete to
carry: *this rule is at Anywhere because these two counter-examples forced it off Adjacent.*

**4. Generation against attestation — the direct test, and the expensive one.** Over-generation is properly
measured by running the grammar *forwards*: generate the forms it licenses and ask which ones the language
never uses. PanGloss already ships this — `pangloss generate <grammar> <root-morpheme-id> …` and
`hc_generate_words`. Cost: generation is combinatorial, and absence from a finite corpus is weak evidence for
any single form, since most real words are unattested in any corpus (the same Zipf argument that makes the
supplied-lexicon scope guard safe). So this yields a **ranked suspicion list for a human**, not a metric.

## Proposed path

**First, (3) — the ad hoc census and tightness audit.** It implements a documented discipline from the
methodology guide, needs no parser run, works on a project with no analysed data at all, produces exactly the
rationale the review milestone was scoped around, and is the one signal that is unambiguously Motif's job
rather than PanGloss's. It is also the cheapest thing on this list by a wide margin.

**Then (2) with (1) alongside**, since both come out of one assessment run: agreement against existing
analyses as the primary breadth signal, with ambiguity growth as the always-available fallback for projects
that have no analyses yet. Report them beside coverage, never folded into it — a single number that mixes
recall and precision can be moved in the wrong direction by improving either half.

**Defer (4)** until something above shows it is needed. It is the theoretically right test and the one most
likely to drown a reviewer in plausible-looking noise.

**What none of this settles**, and it is the owner's call rather than a measurement: whether a coverage rise
with no breadth signal beside it may ever count as evidence that a grammar change was good.
