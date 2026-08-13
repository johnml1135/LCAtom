> *Terminology, 2026-08-09: the measure this document designs is now called **feature coverage** — which
> declared grammar features and combinations the analysed words exercise. **Grammar coverage** now means
> something else: how much of a language the grammar reaches. The filename is left as it is because other
> documents cite it by name. See `CONTEXT.md`, Coverage and generation.*

# Feature coverage: features, their interactions, and holes

**In plain terms:** a grammar is a set of features that combine — slots in a template, affixes that attach to
categories, classes that license affixes. Most defects live in the *combinations*, not the parts. This report
compares the combinations a grammar **licenses** against the ones the analysed words actually **use**, and
lists what is left over. Each leftover is a hole, and a hole means one of two things: the grammar allows
something the language does not, or the word list is missing a word. **Motif cannot tell which, and will not
guess** — it names the hole, and where it can, shows what the missing word would look like so a person can
say which it is in one glance.

Ownership is fixed by [ADR 0033](adr/0033-three-systems-and-who-owns-which-measure.md): PanGloss measures
parse coverage over known-good and known-bad lists, FieldWorks defines agreement with human judgement, and
this is Motif's.

## Why interactions rather than a checklist of features

Black's canonical over-generation example is a combination, not a missing part. Orizaba Nahuatl: `ti-` is both
2sg and 1pl, but 1pl *additionally* requires `-h`. A single template with an optional slot licenses `ti-`
without `-h` — which is not a word. Singular and plural need **separate templates**. Black's conclusion, as
recorded in the sibling harvest, is that *"syntactically possible optional-slot combinations are not realizable
recipes."*

Every feature there is individually exercised and correct. The defect is one slot pair. **So interaction
coverage is not a refinement of feature coverage; it is the level at which the defect the methodology guide
warns about becomes visible at all.**

## The dimensions, and the fields they come from

All of these are already in Motif's emitted catalog, so both sides of the comparison are reachable.

| Interaction | Declared by | Why it matters |
| --- | --- | --- |
| **slot × slot** within a template | `MoInflAffixTemplate.{PrefixSlots,Slots,SuffixSlots,Procliti…,Enclitic…}` (sequences) plus `MoInflAffixSlot.Optional` | Black's Nahuatl case exactly |
| **affix × category** | `MoInflAffMsa.PartOfSpeech`, `.AffixCategory` | A tense affix on a pronoun |
| **affix × inflection class** | `MoInflAffMsa`/`MoDerivAffMsa` class fields | Class-conditioned allomorphy |
| **affix × exception class** | `MoDerivAffMsa.FromProdRestrict`, `MoInflAffMsa.FromProdRestrict` | The `-ity`/`[+Latinate]` mechanism |
| **stem allomorph × environment** | `MoStemAllomorph`, `PhEnvironment` | Allomorphs matching where they should not |
| **compound rule × member category** | `MoCompoundRule`, `MoEndoCompound`/`MoExoCompound` | Restricting productivity (§2.2.6) |
| **rule × rule order** | `ComparisonClass=feeding` rows | Already flagged by [ADR 0028](adr/0028-feeding-reorders-require-a-grammar-delta.md) |
| **declared non-combinations** | `MoMorphAdhocProhib`, `MoAlloAdhocProhib` | These *shrink* the licensed space — see below |

## The explosion, and two reductions borrowed rather than invented

The full cross-product is astronomical and enumerating it would be useless. Two standard reductions:

**1. Pairwise as a floor, not a ceiling.** *Corrected 2026-08-09 against the primary source; an earlier
draft of this section said 2-way coverage "buys most of the value", which is more confident than the research
supports.* Kuhn, Wallace & Gallo (IEEE TSE 30(6), 2004) measure the **failure-triggering fault interaction**
number across nine datasets and report 2-way cumulative coverage ranging **70% (server software) to 97%
(medical devices)** — and their own conclusion is the *upper* bound, "no dataset required more than 4–6
parameters", with 3–6-way named as the practical pseudo-exhaustive range. They also caution that the power-law
shape "would require many more data sets" to generalise.

So 2-way is the cheapest large fraction of the value and a sound starting floor, **with a residual between 3%
and 30% that is real**. Two consequences: report the order a figure was computed at, never a bare "coverage";
and keep the design able to escalate specific combinations to 3-way — the obvious candidates being the ones
[ADR 0028](adr/0028-feeding-reorders-require-a-grammar-delta.md) already flags for rule-order interaction,
where third-factor effects are exactly what `feeding` means.

**And the transfer to morphology is unverified.** No study of a natural-language grammar's feature-interaction
space was found; these are software-configuration numbers. Worth measuring against a real project's attested
combinations rather than assumed.

PanGloss reached pairwise independently for its own compiler and adds a refinement worth copying: **prune by
independence** — combinations provably orthogonal are *retired*, not tested. A slot pair in different templates
cannot interact; do not report it as a hole. NIST's constraint work confirms there is **no established
algorithm for discovering** which combinations are infeasible — the tester supplies them, and the tooling only
checks validity efficiently. So the ad hoc prohibitions and category restrictions are not a shortcut around a
known method; they are the method.

**2. Only what the grammar licenses.** Never enumerate combinations the grammar already forbids. Ad hoc
prohibitions, category restrictions and exception classes all *reduce* the declared space.

That second reduction gives the metric a property worth having deliberately: **tightening a rule shrinks the
declared space, so it lowers the hole count.** The number moves the right way when a linguist does what Black
prescribes. A metric that punished constraint-adding would be worse than none.

## A hole is ambiguous on purpose

A licensed combination that no analysed word uses means one of:

- **the rule is too broad** — the combination should not be licensed, and a constraint is missing;
- **the word list is short a word** — the combination is real but unattested;
- **or the combination is structurally unreachable** — licensed on paper but blocked by some other constraint,
  so no word could exercise it however hard anyone tried. This third reading came out of the mutation-testing
  comparison (the equivalent-mutant analogue) and the literature is clear that separating it is **always manual
  triage**, with no automated test. It is rarer than the other two and must not be silently folded into them.

**Motif cannot distinguish these and must not appear to.** What does distinguish them is **generation**:
render the hole as a concrete candidate word form and ask a person — or an AI — *"is this a word?"* PanGloss
ships `pangloss generate <grammar> <root-morpheme-id> …`.

**But generation is not part of the report, and an earlier draft of this section had it inside one.** A Report
is a query that must be *exceptionally cheap* — a few searches and some statistics over an Assessment that
already exists. Generation is combinatorial. So generation is a **follow-on action a reader triggers on one
hole**, never a column the report fills in for every row. The report's job is to produce the worklist; deciding
to spend generation on a row is the reader's.

That turns the report into a worklist with a one-question decision per row, which is the form Black's method
actually needs: he tells a linguist what to reach for once they have seen a wrong form.

**Prior art for exactly this exists and should be borrowed.** GiellaLT's `morph-test`, which drives ~100
language projects on HFST and foma, pairs an analysis with its expected surface forms and passes only when
**"all and only"** the listed forms appear — checking under-generation and over-generation as two separately
failing conditions on the same line, with an explicit escape hatch for legitimate homonymy. That is the shape
this report wants. It also ships **four verbosity levels** (full detail, per-test counts, one character per
test, summary only), which answers a question this design had left open: the human and the machine want the
same report at different verbosities, not two different reports.

**Vocabulary: borrow rather than invent, on both sides of the merge.** Apertium and GiellaLT both say
**overgeneration** for the too-broad half; NIST says **uncovered combination** for the never-observed half.
Motif's "hole" deliberately merges the two into one undecided category, which no other community does — so
keep "hole" as the term for the merged case, and gloss reports with the borrowed terms so a reader arriving
from either community knows which half they are looking at.

## The conservatism requirement, and why it is not optional

**An analysis records the affix, not the slot.** `WfiMorphBundle` carries `Morph` and `Msa`; there is no slot
reference. And `MoInflAffMsa.Slots` is a *collection*, so one affix may fill several slots.

So when every affix in an analysis maps to exactly one slot, the slot combination is determined; otherwise the
analysis is consistent with several combinations. **A hole report must treat those as possibly exercised and
must not list them.** A phantom hole spends the reviewer's attention, which is the scarce resource here — and
a report that cries wolf gets ignored wholesale, taking the true holes with it. Under-reporting is recoverable;
over-reporting is not.

## Which evidence counts, and the asymmetry that must be enforced

Added 2026-08-08 ([ADR 0034](adr/0034-the-boundary-with-fieldworks-state-versus-change.md) decision 3). The
exercised side may be built from **every analysis in the project, whatever produced it** — machine or hand.
Absence is absence. That puts the larger project's denominator at ~760 analyses rather than the 8 carrying a human
evaluation, so this design is far better supplied than first recorded.

**But the asymmetry is strict.** All analyses may establish that a combination is *unused*; only human
judgement may establish that a used combination is *wrong*. A machine analysis produced by the suspect rule
cannot vindicate that rule — a grammar marking its own homework. Every figure must therefore say which evidence
class it rests on, because a reader who cannot tell will draw the wrong conclusion from whichever sounds
stronger.

## The three outputs

**Stats** — per dimension: how many combinations are licensed, how many exercised, how many holes, and the
run-to-run delta of each. Plus the count of analyses nobody has judged, which
[ADR 0033](adr/0033-three-systems-and-who-owns-which-measure.md) establishes as the leading indicator while
approve/disapprove data stays thin.

**Reports** — per dimension, the holes enumerated, each with the features involved, where they were declared,
and a generated candidate form where generation can produce one.

**Holes** — the ranked worklist. Ranking is a judgement and should start crude and honest: holes involving
frequently-attested morphemes first, since a gap around a common affix matters more than one around a rare
one; and holes where the declaration is *unconstrained* — a slot with no category restriction, an affix with
no exception class — flagged as likelier over-broad, because an unconstrained declaration is how a grammar
gets too permissive in the first place.

## What to build first

**The slot-combination report for one template.** It is Black's canonical case, every field it needs is
already emitted, it is the smallest thing that can find a real defect, and it forces the conservatism rule to
be written before the reporting gets ambitious. Everything else in the table above is the same shape with
different fields.

## What this design does not settle

- **Ranking is unvalidated.** Frequency and unconstrained-ness are guesses about what a linguist finds useful,
  and the only way to find out is to show someone a real report.
- **Generation's cost on a real grammar is unmeasured** in this targeted form, though the blanket form is known
  to be combinatorial.
- **Whether a hole count belongs in a Proposal's evidence** or only in a standing project report. A number that
  changes when someone adds a text is not a property of the Proposal, which is the same distinction that made a
  corpus hash part of a coverage figure's provenance.
