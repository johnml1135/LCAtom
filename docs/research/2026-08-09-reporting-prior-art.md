# Reporting prior art: what makes a grammar-breadth report get used

**In plain terms:** Motif is about to build a report that tells a linguist "your grammar allows this
combination of affixes and nothing in the corpus uses it — is that a missing word, or a rule that's too
loose?" Nobody has built exactly that report before, but five adjacent communities have spent decades
learning how reports like it succeed or fail: rule-based machine translation (Apertium), mutation testing,
code-coverage tooling, combinatorial/pairwise test design, and other finite-state morphology toolkits
(HFST, foma). This note asks each of them the same question — *what makes this kind of report get read and
acted on, rather than ignored or gamed* — and separates what a primary source actually says from what is
this note's own inference. Bottom line up front: every domain converges on the same three lessons — **score
the change, not just the state; never let a single number become a gate; and report gaps as a short, ranked
worklist rather than a wall of numbers** — and Motif's own design (`docs/grammar-coverage-design.md`) has
already independently arrived at versions of all three. The prior art mainly confirms the design and
sharpens two things it hadn't named: an "equivalent mutant" problem for loosened constraints, and an
explicit caution that the pairwise reduction Motif borrowed is weaker evidence than it is being asked to
carry.

**Verification key**, used throughout: **[verified]** — read directly from the primary source (a fetched
page, a paper, source code); **[abstract-only]** — confirmed the claim exists and roughly what it says, but
not against the source's own words in enough detail to quote precisely; **[unverified]** — could not confirm
in the time available, flagged rather than smoothed over. All web research below was done by background
research agents that fetched primary pages/papers directly, cross-checked here where a claim mattered
enough to re-fetch.

---

## 1. Apertium's `testvoc` — the closest working analogue

**What it actually checks [verified].** `testvoc` does not run over held-out text. It **walks the
lexicon/FST itself** — via `lt-expand` or `hfst-fst2strings` — to enumerate every surface form the analyser
*can* produce, re-analyses each one, and pushes it through the rest of the pipeline (transfer, bilingual
dictionary, generation). "Bad" means the pipeline emits a marker instead of one clean surface form:
`#` (generation failed — a round-trip failure between analysis and generation), `@` (the bilingual
dictionary or a transfer rule couldn't map the form), `/` (more than one analysis survived to generation,
i.e. unresolved ambiguity). Source: <https://wiki.apertium.org/wiki/Testvoc>; confirmed against actual
pipeline code in `dev/testvoc/generation.sh` in `apertium-swe-dan`
(<https://github.com/apertium/apertium-swe-dan>), which substitutes the expanded lexicon for the analysis
stage of the `.mode` file and greps output for `#`/`/`.

For a monolingual analyser (no translation stage), this degenerates to exactly the check Motif's own
research called "generation against attestation" (`docs/research/2026-08-08-grammar-breadth-black-2025-and-a-path.md`,
signal 4): run the grammar forwards and ask whether what it accepts round-trips to a real form. **This is
prior art for exactly the mechanism that note deferred as "the expensive one."**

**Output shape [verified].** No summary percentage. The report is a raw stream of `lemma/analysis → output`
lines, filtered to only the offending ones (`grep '[#@/]'`). Granularity is per generated surface
form/paradigm cell — not per corpus token, not a single pass/fail per lexicon entry. The wiki names a real
limit of this granularity: single-word testvoc misses transfer errors that "are not visible when translating
single lexical units," which needs a separate multi-word pass (<https://wiki.apertium.org/wiki/Testvoc>).

**How maintainers actually use it [verified].** Three concrete, sourced facts, not just reputation:

- The wiki states plainly that mature language pairs "abandon" ad hoc regression tests and rely on
  `testvoc` and the corpus-diff sibling tool instead (<https://wiki.apertium.org/wiki/Wiki_regression_testing>),
  and "testvoc clean" is a named, checked gate for reaching "staging" maturity
  (<https://wiki.apertium.org/wiki/Staging>).
- `apertium-nno-nob`'s `dev/precommit-testvoc.sh` (fetched directly from the repo) does **not** run testvoc
  over the whole dictionary before a commit. It diffs the dictionary against the last commit, extracts only
  the *changed* entries, and testvoc-checks just those, printing "appear testvoc clean; go ahead and commit"
  or a warning. **This is a diff-scoped precommit gate, built independently of anything in code-coverage
  tooling, arriving at the same design** (see §3).
- CI (`.travis.yml` in the same repo, fetched directly) runs the corpus-diff sibling on every build, not
  full testvoc — corroborating that full testvoc is reserved for less-frequent runs.

**Known failure modes at scale [verified, but indirect].** No page frames this as "testvoc is too noisy, we
ignore it" outright. The evidence for a cost/noise problem is circumstantial but consistent: the existence
of the diff-scoped precommit script, and a maintainer's personal workflow notes
(<https://wiki.apertium.org/wiki/User:Ilnar.salimzyan/On_testing>) that explicitly separate a lightweight
per-commit check from full testvoc run "once in a while." One concrete false-positive mode is documented: an
`lt-trim`-reduced analyser tested through the untrimmed pipeline manufactures spurious `@` errors that
"won't appear when running the real pipeline" (same wiki page). **No source discusses maintainers gaming or
disabling testvoc — unverified, not found**, which differs from the code-coverage literature's clear
gaming record (§3) and is worth noting as an asymmetry: testvoc reports raw bad output, not a percentage, so
there is nothing to game toward.

**Coverage and ambiguity tooling, kept separate from over-generation [verified].** Coverage is a single
number: tokens analysed ÷ total tokens, computed by `dev/coverage.sh`
(<https://wiki.apertium.org/wiki/Asturian#Calculating_coverage>), formalized in the `apertium-quality`
toolkit as `aq-covtest` (corpus + binary dictionary → %, written to `quality-stats.xml`) and `aq-ambtest`
(dictionary → average analyses-per-word)
(<https://wiki.apertium.org/wiki/Apertium-quality/Application_Documentation>). `apertium-dixtools` separately
profiles which dictionary *entries* a corpus run actually exercised, including zero-use entries
(<https://wiki.apertium.org/wiki/Dictionary_coverage>) — structurally the closest Apertium analogue to
Motif's "declared vs exercised" join, though it operates on lexicon entries, not on slot/category/class
combinations.

**Vocabulary [verified].** Apertium's own community does use **"overgeneration"** as the term for "the
analyser accepts/produces something that isn't a real word," kept distinct from "coverage" (recall on a
corpus): *"the resulting analyser will over-generate"* (<https://wiki.apertium.org/wiki/Talk:Automatically_trimming_a_monodix>);
*"Hebrew overgeneration of nouns"* (<https://wiki.apertium.org/wiki/Maltese_and_Hebrew>). There is no
dedicated glossary page for it — it is used consistently in practice without being formally defined — unlike
"coverage," which has its own page and its own tool. **This is directly usable**: Motif's ADR 0033 already
distinguishes "the FST proposer is loose" (PanGloss's problem) from "the grammar is too broad" (Motif's);
Apertium's "overgeneration" is an established word for exactly Motif's half of that split.

## 2. Mutation testing as a framing

The proposed analogy: loosen a grammar constraint and see whether any corpus word's analysis changes. If
none does, the corpus cannot detect that constraint — a fact about the corpus's power, not the grammar's
correctness. Researched against DeMillo, Lipton & Sayward's foundational framing (1978), Offutt & Lee's work
on strong vs. weak mutation (1991/1994), Offutt & Pan on equivalent mutants (1997), and current tool
documentation (Stryker, PIT).

**Is the analogy sound? [verified at the load-bearing point, abstract-only elsewhere].** Under *strong*
mutation — the classical default — a mutant is "killed" only if it produces different final output than the
original program on some test input. That reduces to exactly Motif's criterion: does the analysis change?
**The analogy is closer to isomorphic than metaphorical at this specific point.** And the two share the
*same* epistemic limit, which is the actual argument for making it: a killed mutant proves only that the
test suite can *distinguish* mutant from original, never that the original was correct (Jia & Harman, IEEE
TSE 37(5), 2011 — confirmed at abstract/metadata level, full text not retrieved). A word whose analysis
doesn't change under a loosened constraint proves the corpus **cannot discriminate** that constraint, not
that the constraint (or the grammar) is right. This is precisely the "a hole is ambiguous on purpose"
argument `grammar-coverage-design.md` already makes, arrived at independently by a different literature.

**Where it strains [abstract-only].** Classical mutation operators perturb arbitrarily — they can tighten or
loosen program behavior. Motif's proposed mutations are one-directional: always loosening. The nearest
published precedent for mutating a *specification* rather than code — Black, Okun & Yesha, "Mutation
Operators for Specifications" (ASE 2000) — uses spec mutants to *generate new test cases from the spec*,
whereas Motif's corpus is fixed, historical, and not generated from the grammar. Motif's version is closer to
**testing a static regression corpus against a synthetic defect** than to test-suite construction. This
should be named explicitly if the mutation analogy is used in Motif's own docs, so nobody imports the
assumption that a bigger, generated mutant set is the natural next step — it isn't, for a fixed word list.

**Cost [abstract-only — secondary summaries of primary techniques].** The literature's standard cost
controls are selective mutation (a reduced operator set, Offutt et al.), mutant sampling, mutant schemata
(Untch, Offutt & Harrold, ISSTA 1993 — compiling all mutants into one metaprogram, reporting >300% speedup
over interpreted execution), and higher-order mutants. Tool practice (PIT community guidance) is blunter:
run mutation testing nightly or on demand, not per-commit, because it is too slow for every build.

**Equivalent mutants [verified for the core claim, unverified for prevalence numbers].** The general problem
is undecidable — it reduces to program equivalence (Offutt & Pan, *STVR* 7(3), 1997). Offutt & Pan use
constraint-based reasoning to catch roughly half of a small equivalent-mutant benchmark, but this doesn't
generalize into a solved problem. Prevalence figures circulating online (commonly "15–25%," sometimes higher)
come only from secondary/tertiary sources in this research pass and should be treated as order-of-magnitude,
**not** a number to cite as fact. Tool practice: neither mutmut nor cosmic-ray auto-detects equivalence;
teams manually annotate/whitelist (e.g. mutmut's `# pragma: no mutate`).

**This has a direct, currently unnamed analogue for Motif: a loosened constraint can be logically redundant
given other constraints already in force** — the loosening changes nothing reachable, the same way an
equivalent mutant changes nothing observable. `grammar-coverage-design.md` already has a "prune by
independence" reduction borrowed from PanGloss, but that prunes combinations that *can't interact*, not
constraints whose loosening is a no-op given the rest of the grammar. Nothing in Motif's documents currently
distinguishes "the corpus is too thin to catch this" from "this loosening was never observable no matter what
the corpus contained" — and per the mutation literature, no tool solves this automatically; it is manual
triage, always. **This is worth naming as its own category before someone mistakes a structurally-inert hole
for a coverage gap.**

**Scores in practice [verified against tool docs].** Stryker computes score as `killed+timeout` over
`valid` mutants, supports `high`/`low`/`break` thresholds, and fails CI below `break`
(<https://stryker-mutator.io>). PIT exposes a `mutationThreshold` that can fail a build, and states outright
that mutation coverage is "the gold standard against which all other types of coverage are measured"
(<https://pitest.org>) — a direct claim that line coverage is a weaker signal, which supports Motif's own
ADR 0033 refusal to report a coverage figure "as though it characterised the grammar." **Gaming of mutation
scores specifically is attested only at blog/practitioner level in this pass — unverified against a
peer-reviewed source** — teams write tests that exist only to kill mutants once the score becomes a required
gate. Directionally consistent with the code-coverage gaming record below, not independently proven here.

## 3. Code-coverage report design — thirty years of actionability lessons

**Why reports get gamed or ignored [verified — one primary academic source, one primary industry source]:**
Brian Marick's "How to Misuse Code Coverage" is the foundational skeptical text, but its host
(`exampler.com`) and its known PDF mirrors returned connection/TLS errors throughout this research —
**unverified against the primary PDF directly**; the claim below rests on three independent secondary
sources quoting it consistently, which is reasonable but not the same as reading it. Reported quote:
organizations that mandated a coverage percentage "got just the percentage they wanted" — developers wrote
tests, checked the number, and stopped at the threshold rather than where testing was actually useful.
Coverage is legitimate as a diagnostic hint, illegitimate as a management target.

Google's own testing blog makes the identical argument independently and **was fetched and verified
directly** (mirror used after the canonical page rendered only comments): "a high code coverage percentage
does not guarantee high quality in the test coverage"; coverage is "a lossy and indirect metric" that "does
not guarantee that the covered lines or branches have been tested correctly, it just guarantees that they
have been executed"; mandated targets "can backfire (pressure to 'hit the metric' almost never yields desired
outcome)." (Bender, Argüelles, Ivanković, "Code Coverage Best Practices," Aug 2020,
<https://testing.googleblog.com/2020/08/code-coverage-best-practices.html>.) **Two independent primary-ish
sources, twenty years apart, reach the same conclusion — this is the single best-attested finding in this
whole note.**

**Goodhart framing [abstract-only — the "Goodhart" label is this note's own, not the sources']:** No academic
paper found frames coverage under Goodhart's law by name. Marick's mandate anecdote is the clearest
documented instance of the mechanism (a targeted number stops meaning what it meant before it was targeted).
Secondary discussion adds one mechanism worth keeping regardless of attribution quality: a **ratchet-then-slack
effect** — once a gate is pushed from 70% to 90%, the margin lets new untested code creep back in without
ever tripping the gate again, so the number drifts down even though it never technically fails.

**Per-diff vs per-project — direct from tool documentation, verified:**

- Codecov: "a developer should test their own code versus test all of the code"; mandating 100% project
  coverage "puts an undue burden and stress on a team" (<https://about.codecov.io>).
- `diff-cover`: "Diff coverage is the percentage of new or modified lines that are covered by tests. This
  provides a clear and achievable standard for code review: If you touch a line of code, that line should be
  covered." — the stated rationale is *achievability*, contrasted explicitly with whole-project 100%
  (<https://github.com/Bachmann1234/diff_cover>, README).
- SonarQube's "Clean as You Code": "you aren't responsible for anyone else's code... focus away from new code
  to old code" makes ownership diffuse, and the docs name the actual failure mode directly — stacking
  whole-project conditions risks "an ignored quality gate" (<https://docs.sonarsource.com>). **This is the
  most explicit primary statement found anywhere in this research that over-broad gates get switched off by
  frustrated teams rather than obeyed.**
- Qlty's docs frame the two as deliberately complementary, not competing: total coverage as a long-term trend
  backstop ("to prevent merging changes that significantly decrease coverage"), diff coverage as the
  actionable forward gate — "Focus on Diff Coverage for new code rather than trying to immediately increase
  Total Coverage" (<https://docs.qlty.sh>).

**Nobody's documentation addresses the reverse failure** — good project-wide numbers masking a badly-tested
diff — as a named risk. That gap matters for Motif: a whole-grammar hole count could fall (tightening
elsewhere) while a specific Proposal introduces a new, unexamined loosening, and none of the surveyed tools'
own documentation discusses guarding against that specific shape of problem.

**What makes a report get read, not skipped [verified for the concrete design choices, abstract for the
synthesis]:** SonarQube's own naming of "ignored gate" as a designed-against failure mode; Danger.js
posting reports as inline PR comments with deliberately truncated tables (max rows, wrapped filenames) so
the report fits legibly rather than becoming a wall of text a reviewer learns to skip
(<https://danger.systems>); and a convergent practitioner pattern (multiple tool docs, aggregated, abstract
level) of coloring by *regression vs. improvement* rather than absolute percentage, and showing a trend line
so one run is read against history rather than in isolation.

## 4. NIST / Kuhn et al. — combinatorial and pairwise testing

**The actual empirical claim [verified against the primary paper, PDF read directly]:** Kuhn, Wallace &
Gallo, "Software Fault Interactions and Implications for Software Testing," *IEEE TSE* 30(6), 2004,
introduces the **failure-triggering fault interaction (FTFI) number** — the minimum number of parameters
that must be set jointly to trigger a given failure — and reports cumulative percent of faults triggered by
n-way interactions across nine datasets (medical-device recalls, browser and server bug trackers, a NASA
planner, and NASA-Goddard's own 329 error reports):

| FTFI | Medical devices | Browser | Server | NASA (their study) |
| --- | --- | --- | --- | --- |
| 1-way | 66% | 28.6% | 41.7% | 67.5% |
| 2-way | 97% | 76.1% | 70.3% | 93.3% |
| 3-way | 99% | 95.0% | 89.3% | 98.8% |
| 4-way | 100% | 97.2% | 96.4% | 100.0% |
| 6-way | — | 100.0% | 100.0% | — |

The paper's own conclusion is narrower than the popular summary "most bugs are pairwise": it is **"no
dataset required more than 4–6 parameters"**, and it names 3–6-way as the range for practical
"pseudo-exhaustive" testing, not 2-way. **This directly qualifies a claim in Motif's own design doc.**
`grammar-coverage-design.md` states "combinatorial testing's established result is that most defects involve
one or two factors, so 2-way coverage buys most of the value at a tiny fraction of the size" — that is a
real reading of the popularized version of this result, but the primary source's own numbers show 2-way
coverage ranging from 70% (server software) to 97% (medical devices), and the authors explicitly recommend
higher orders for confidence. **Stated plainly, as the task asked: Motif's design doc is more confident in
pairwise sufficiency than the source it draws on.** This doesn't mean 2-way is a bad starting floor — it is
still the cheapest large fraction of the value — but it should be documented as a floor with a known,
nontrivial residual, not as "most of the value," and Motif should keep language open to escalating specific
combinations (e.g. slots already flagged by feeding/rule-order concerns, ADR 0028) to 3-way.

**Does it generalize to a grammar? [verified as a limitation, i.e. the source itself says no]:** The authors
state the pattern "appears to follow a power law, but many more data sets would be required to make this
generalization," explicitly flag that fielded medical devices showed *higher* 2-way proportions than
in-development systems (contradicting a naive expectation that maturity reduces high-order faults), and
caution the approach "may not be effective for real-time or other software that depends on testing event
sequences." No study of a natural-language grammar's feature-interaction space was found — the closest
published adjacent domain is combinatorial testing of software product-line feature models, itself only an
analogy the researching agent drew, not a claim any source makes. **This is an open question, not a settled
transfer**, and it is worth testing pairwise sufficiency empirically against Sena 3's actual attested
combinations rather than assuming the software-configuration numbers hold for morphology.

**How covering-array results are reported [verified against NIST's own paper]:** Kuhn, Dominguez Mendoza,
Kacker & Lei, "Combinatorial Coverage Measurement Concepts and Applications" (NIST/IWCT 2013), documents
NIST's CCM tool reporting **multiple granularities at once**, not a single percentage: *simple t-way
coverage* (% of combinations fully covered), *total variable-value configuration coverage* (% of individual
value-tuples covered — can be much higher than simple coverage, e.g. 33% simple vs. 79% total in their
worked example), *(p,t)-completeness*, and exportable lists of uncovered/invalid combinations. **This
directly supports Motif's existing three-output design** (stats, reports, ranked hole worklist) — NIST
converged independently on "report several granularities plus an exportable gap list," not one number.

**Pruning infeasible/orthogonal combinations [verified]:** Yu, Lei, Nourozborazjany, Kacker & Kuhn's
constraint-handling paper (ICST 2013) is explicit that **there is no NIST-documented algorithm for
discovering which combinations are infeasible** — the tester must supply constraints, either as explicit
forbidden tuples or boolean/relational expressions; NIST's contribution is *efficient validity checking*
once told what's infeasible (a CSP solver, with optimizations cutting solver calls by 1–2 orders of
magnitude), not discovery. **This matches Motif's existing plan exactly** — the ad hoc-prohibition and
category-restriction fields already shrink the declared space (`grammar-coverage-design.md`'s "only what the
grammar licenses" reduction), and PanGloss's "prune by independence" is the discovery mechanism NIST itself
doesn't provide. Nothing here suggests Motif is missing an established discovery method; it confirms none
exists to miss.

## 5. Other morphological-analyser tooling: FieldWorks beyond `ParserReport.cs`, foma, HFST/GiellaLT

**FieldWorks — searched locally, confirms ADR 0034's characterization rather than contradicting it
[verified].** A grep across `FieldWorks/Src` for classes named `*Report*`/`*Statistics*`/`*Stats*` (22
matches) turns up nothing that measures grammar structure. The two candidates that looked promising were
both word/corpus-level:

- `Src/LexText/ParserCore/TaskReport.cs` — a generic progress/timing tree (`TaskPhase`, `DurationTicks`,
  nested subtasks) used for UI progress reporting. Not a data report at all.
- `Src/LexText/Interlinear/StatisticsView.cs` — corpus statistics only: word token/type counts per writing
  system and a sentence count, built by walking `IWfiWordform`s in interesting texts (lines 109–197). No
  reference to templates, slots, inflection classes, or affix categories anywhere in the file.

A second, targeted grep for the grammar-structure field names Motif's own design already emitted
(`MoInflAffixTemplate`, `MoInflAffixSlot`, `AdhocProhib`, `InflectionClass`) across every `*Report*.cs` file
in the FieldWorks tree returned **zero matches**. That is a clean, direct confirmation — not an inference —
of ADR 0033's claim that "nothing in FieldWorks' `ParserCore` tracks it": no FieldWorks report class anywhere
in the tree reasons about declared grammar structure at all; every one of them sums over words or wall-clock
time. `ConcordanceDlg.cs` (`Src/LexText/Morphology`) is a bulk-assign editing UI, not a report, and
corroborates ADR 0034's "word-level analysis browser/editor" non-goal list by showing what that kind of
surface actually looks like in FieldWorks (a source/target tree pair plus a concordance browse view) — useful
as a concrete picture of the thing Motif has already decided not to build.

**foma — inspected the local checkout directly, no coverage concept exists at all [verified].** foma's own
introduction (`docs/simpleintro.md`, lines 60–116) documents its self-testing primitives: `words` (every
string an FST accepts), `pairs`/`upper-words`/`lower-words` (transducer input/output sets), and `up`/`down`
(single-string membership testing, printing `???` on rejection). Every regex compile also reports a
structural size line — *"371 bytes. 2 states, 4 arcs, 4 paths."* — a network-size metric, not a
correctness one. `interface.l` (lines 198–210) adds `random-lower`/`random-upper`/`random-words`/`random-pairs`
for sampling an FST's accepted language when it's too large to enumerate; the `CHANGELOG` records these were
later improved to give "a more random distribution" with duplicate counts prefixed rather than repeated
(lines 24, 73). **All of this samples what the compiled network itself accepts — none of it compares against
an external corpus of attested words**, so foma has no built-in analogue to coverage, holes, or
over-generation detection at all. That gap is exactly why the next layer exists.

**HFST / GiellaLT — the community that actually built the missing layer, and it has usable vocabulary
[verified, fetched directly].** The GiellaLT infrastructure (which builds ~100 language projects on
HFST/foma/Xerox backends) ships `divvun/morph-test` (<https://github.com/divvun/morph-test>) and an earlier
`HfstTester` (<https://giellalt.uit.no/tools/HfstTester.html>). Both use YAML test files pairing a
morphological analysis with expected surface forms (`juoga+Pron+Indef+Sg+Gen: [form1, form2]`, `[]` for "must
not exist"), and both define a test as passing only when **"all and only" the listed forms/analyses appear**
— i.e. they check under-generation and over-generation as two distinct, separately-failing conditions in the
same test line, with a documented escape hatch (`--ignore-extra-analyses` / `-i`) for legitimate homonymy.
Reporting has four documented verbosity levels — normal (full detail), compact (per-test pass/fail counts),
terse (`.`/`!` per test), final (summary only) — a deliberately layered format rather than one report shape
for every audience. **Caveat on precision**: this summary of the exact README wording was produced by the
research agent's own page-fetch-and-summarize step rather than a line-by-line reading I did personally, so
treat the substance (all-and-only checking, an explicit over-generation flag, tiered verbosity) as solid but
the specific phrase "over-generation detection" as a paraphrase, not a confirmed verbatim quote.

**Cross-domain vocabulary comparison.** No single term crosses all five domains, but there is real
convergence on the "rule too broad" half of a hole:

| Domain | Term for "accepts something that isn't real" | Term for "never observed" |
| --- | --- | --- |
| Apertium | overgeneration | (no dedicated term; "low coverage") |
| HFST/GiellaLT (`morph-test`) | "extra analyses" / over-generation via `-i` flag | (test simply fails as under-generation) |
| Black (2025) / Motif | "the grammar is too broad" / "simply incorrect" parse | — |
| NIST/Kuhn | — | "uncovered combination" / "invalid combination" |
| Mutation testing | (no direct analogue — closest is a "surviving mutant") | (a mutant with no test able to kill it) |

"Hole," as Motif defines it, is a deliberate synthesis of the right-hand and left-hand columns into one
undecided category — which is a genuinely novel move relative to every one of these communities; none of
them merges "too broad" and "never observed" into a single ambiguous unit, because none of them had Motif's
reason to (the FieldWorks GUID join making both sides cheap to compute at once). **Recommendation: keep
"hole" as Motif's term for the merged, undecided case, but gloss reports with "overgeneration" and "uncovered
combination" for readers arriving from these other communities** — the vocabulary already exists on both
sides of the merge and borrowing it costs nothing.

---

## Concrete recommendations for Motif's report design

1. **Score the Proposal's delta as the primary figure; keep the whole-grammar sweep as a slower backstop.**
   Triangulated three independent ways: Codecov/`diff-cover`/SonarQube's stated rationale (ownership,
   achievability), Apertium's own `precommit-testvoc.sh` (diff-scoped, pre-commit, built independently of any
   code-coverage tooling), and Qlty's explicit "diff for action, total for regression backstop" split. This
   already matches ADR 0034's state-versus-change boundary — the prior art is confirmation, not a new idea,
   but it is unusually strong confirmation because three unrelated communities landed on the same shape
   without copying each other.

2. **Never gate on a hole count, and never publish it as a bare percentage.** Marick's mandate anecdote and
   Google's own coverage post agree that a targeted number stops measuring what it measured before it was
   targeted; a hole count is *more* exposed to this than code coverage, because a hole is already admitted to
   be ambiguous (over-broad rule vs. missing word) — a hard threshold would optimize toward whichever
   resolution is cheaper to fake, not whichever is true. Report it as a ranked worklist (already Motif's
   plan) with trend history, never as a scored gate.

3. **Report multiple granularities, not one number, mirroring NIST's own CCM design.** NIST's tooling reports
   simple coverage, total configuration coverage, and an exportable gap list simultaneously because a single
   figure conflates "how much is fully covered" with "how much is touched at all." Motif's existing "stats /
   reports / ranked holes" three-output design already does this; the prior art is a reason to keep those
   outputs structurally separate rather than eventually collapsing them into a dashboard number under review
   pressure.

4. **Treat pairwise (2-way) as an explicit floor with a stated residual, not as "most of the value."** Kuhn et
   al.'s own numbers show 2-way interaction coverage ranging 70–97% by domain, and the authors themselves
   caution against generalizing without more data. Motif's design doc should say plainly that 2-way is the
   affordable starting point and name a path to escalate specific slot pairs (e.g., those already flagged by
   ADR 0028's feeding/rule-order concern) to 3-way, rather than implying 2-way is close to complete.

5. **Name the "equivalent loosening" problem before someone hits it and misreads it.** A loosened constraint
   that changes nothing reachable — because another constraint already forecloses it — is Motif's version of
   an equivalent mutant, and per the mutation-testing literature this is undecidable in general and always
   ends in manual triage in every tool that has this problem. Extend the existing "prune by independence"
   reduction (borrowed from PanGloss) to explicitly flag "this loosening was structurally inert" as a
   category distinct from "the corpus didn't catch this," so the two don't get conflated in the report.

6. **Keep "hole" as the merged term, but gloss it against the vocabulary its two halves already have.**
   "Overgeneration" (Apertium, HFST/GiellaLT) for the over-broad reading and "uncovered combination" (NIST)
   for the never-attested reading are both established, load-bearing terms in their own communities. A
   one-line glossary note in Motif's reports costs nothing and helps a reader who has used any of these other
   tools recognize what they're looking at immediately.

7. **Design the report surface the way Danger.js and SonarQube's "new code" view do: inline, truncated,
   colored by direction of change, not a wall of numbers.** SonarQube's own documentation names "an ignored
   quality gate" as the specific failure their new-code scoping exists to prevent — the clearest primary
   statement found anywhere in this research that an over-broad report gets switched off, not obeyed. Keep
   the hole worklist short, ranked, and inline in whatever surface a linguist already reviews a Proposal in.

## Open questions this research could not settle

- **Whether pairwise interaction coverage is even approximately right for a grammar's feature space.**
  Kuhn et al.'s numbers come entirely from software-configuration systems; no study of a linguistic grammar's
  combinatorics was found, and the authors' own paper says the pattern needs "many more data sets" before
  generalizing even within software. This can only be answered by checking pairwise sufficiency against
  Sena 3's actual attested combinations, not by more literature search.
- **Whether Motif has (or will have) any actual gate to game.** Every gaming/gating lesson in §3 and §2 comes
  from teams whose merges are blocked by a threshold. ADR 0034 makes Motif a recorder of change rather than a
  gatekeeper of state, and it isn't settled from the documents read for this note whether any review surface
  built on top of Motif's reports will ever function as a hard gate — if one never does, several of these
  gaming lessons are precautions against a failure mode that can't occur here.
- **How common structurally-inert ("equivalent") loosenings will actually be in a real grammar.** The
  mutation-testing literature treats this as a real, non-trivial cost in code; whether it is common enough in
  a morphological grammar to need dedicated tooling, or rare enough that manual triage during review is
  sufficient, is unmeasured — the same open status `grammar-coverage-design.md` already gives to generation's
  cost on a real grammar.
- **Marick's exact wording could not be independently confirmed.** `exampler.com` and its known mirrors
  returned connection/TLS errors throughout this research; the claim rests on three converging secondary
  quotations rather than the primary PDF. The substance is corroborated independently by Google's own (fetched
  directly) blog post, so the *finding* is solid, but the specific Marick quote should be treated as
  once-removed if it is ever reused verbatim.
- **Apertium's per-pair `<repo>/stats` wiki pages (`Category:Datastats`) were not opened.** Their existence is
  confirmed by search; their exact field layout — which would be the most directly comparable template to a
  Motif stats page — is unverified in this pass.
- **The literal wording of `divvun/morph-test`'s README around "over-generation"** was relayed through an
  automated fetch-and-summarize step rather than read line-by-line; the mechanism it describes is solid, the
  exact phrase is not confirmed verbatim.

## Sources

Apertium: [Testvoc](https://wiki.apertium.org/wiki/Testvoc) ·
[Wiki regression testing](https://wiki.apertium.org/wiki/Wiki_regression_testing) ·
[Staging](https://wiki.apertium.org/wiki/Staging) ·
[Corpus test](https://wiki.apertium.org/wiki/Corpus_test) ·
[Dictionary coverage](https://wiki.apertium.org/wiki/Dictionary_coverage) ·
[Ambiguity](https://wiki.apertium.org/wiki/Ambiguity) ·
[Apertium-quality Application Documentation](https://wiki.apertium.org/wiki/Apertium-quality/Application_Documentation) ·
[Apertium-quality Quickstart](https://wiki.apertium.org/wiki/Apertium-quality/Quickstart) ·
[Apertium-viewer](https://wiki.apertium.org/wiki/Apertium-viewer) ·
[Talk:Automatically trimming a monodix](https://wiki.apertium.org/wiki/Talk:Automatically_trimming_a_monodix) ·
[Maltese and Hebrew](https://wiki.apertium.org/wiki/Maltese_and_Hebrew) ·
[User:Ilnar.salimzyan/On testing](https://wiki.apertium.org/wiki/User:Ilnar.salimzyan/On_testing) ·
[apertium-swe-dan](https://github.com/apertium/apertium-swe-dan) ·
[apertium-nno-nob](https://github.com/apertium/apertium-nno-nob) ·
[apertium-tat-bak](https://github.com/apertium/apertium-tat-bak).

Mutation testing: DeMillo, Lipton & Sayward, "Hints on Test Data Selection," *Computer* 11(4), 1978 ·
Offutt & Lee, "How Strong is Weak Mutation?", ISSTA/TAV 1991/1994 ·
Offutt & Pan, "Automatically Detecting Equivalent Mutants and Infeasible Paths," *STVR* 7(3), 1997 ·
Untch, Offutt & Harrold, "Mutation Analysis Using Mutant Schemata," ISSTA 1993 ·
Black, Okun & Yesha, "Mutation Operators for Specifications," ASE 2000 ·
Jia & Harman, "An Analysis and Survey of the Development of Mutation Testing," *IEEE TSE* 37(5), 2011 ·
[Stryker docs](https://stryker-mutator.io) · [PIT docs](https://pitest.org).

Code coverage: Marick, ["How to Misuse Code Coverage"](http://www.exampler.com/testing-com/writings/coverage.pdf)
(unreachable directly this pass; quoted via converging secondary sources) ·
[Google Testing Blog, "Code Coverage Best Practices," 2020](https://testing.googleblog.com/2020/08/code-coverage-best-practices.html) ·
Ivanković, Petrović, Just & Fraser, ["Code Coverage at Google," ESEC/FSE 2019](https://storage.googleapis.com/gweb-research2023-media/pubtools/5172.pdf) ·
[Codecov](https://about.codecov.io) · [diff-cover](https://github.com/Bachmann1234/diff_cover) ·
[SonarQube Clean as You Code](https://docs.sonarsource.com) · [Qlty coverage metrics](https://docs.qlty.sh/coverage/metrics) ·
[Danger.js](https://danger.systems).

Combinatorial testing: Kuhn, Wallace & Gallo, "Software Fault Interactions and Implications for Software
Testing," *IEEE TSE* 30(6), 2004 ·
Kuhn, Dominguez Mendoza, Kacker & Lei, "Combinatorial Coverage Measurement Concepts and Applications,"
IWCT 2013 ·
Yu, Lei, Nourozborazjany, Kacker & Kuhn, "An Efficient Algorithm for Constraint Handling in Combinatorial
Test Generation," ICST 2013.

Other morphological tooling: `FieldWorks/Src/LexText/ParserCore/TaskReport.cs` ·
`FieldWorks/Src/LexText/Interlinear/StatisticsView.cs` ·
`FieldWorks/Src/LexText/Morphology/ConcordanceDlg.cs` (all local checkout, paths relative to the
`FieldWorks` sibling repository) ·
[foma `docs/simpleintro.md`](https://github.com/mhulden/foma) (local checkout, `foma/foma/docs/simpleintro.md`
lines 60–116; `foma/foma/interface.l` lines 198–210; `foma/foma/CHANGELOG` lines 24, 73) ·
[divvun/morph-test](https://github.com/divvun/morph-test) ·
[GiellaLT HfstTester](https://giellalt.uit.no/tools/HfstTester.html).

Motif's own documents this note checked claims against:
[ADR 0033](../adr/0033-three-systems-and-who-owns-which-measure.md) ·
[ADR 0034](../adr/0034-the-boundary-with-fieldworks-state-versus-change.md) ·
[`grammar-coverage-design.md`](../grammar-coverage-design.md) ·
[grammar-breadth research](2026-08-08-grammar-breadth-black-2025-and-a-path.md).
