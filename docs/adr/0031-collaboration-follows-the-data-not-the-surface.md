# ADR 0031 — Collaboration follows the data, not the surface: grammar has one integrator

**Status:** accepted, 2026-08-06. Answers `D14`, `D15`, `D16`. Scopes `MOT-10`, `MOT-17`, `MOT-18` and
therefore most of M4. Builds on [ADR 0021](0021-cli-is-the-full-surface-layer-1-churns.md) (the CLI is the
whole surface) and [ADR 0029](0029-agents-address-layer-1-only.md) (the agent is a contributor).

**In plain terms:** a grammar is small enough that one person or one AI writes nearly all of it, and its
rules depend on each other so heavily that two people editing it separately would produce a grammar
neither of them meant. So Motif will not build machinery for several people to edit a grammar at once.
What it builds instead is a durable record of *why* each change was made, because the person who wrote
the grammar will eventually leave and the reasoning must not leave with them. Dictionary and text work is
the opposite — large, safely parallel, and **already handled by other tools**, so Motif touches it only to
give a machine a validated path, plus a recorded report of what the change did to parser coverage. A human
adding a word should use FLEx. See the amendment below, which
narrows this further than the original analysis did.

## Context

The owner set out three ways this system will actually be used:

1. **AI alone.** A strong model works through proposals, compares them, updates them, and reports; it may
   drive a weaker model that produces the changes. A human may later read the rationale and conclusions.
2. **A weaker AI plus a human, inside FieldWorks.** The human occupies the role the strong model held in
   (1). Nearly the same workflow, a different surface.
3. **People debating online.** A forum: proposing words, reporting spelling mistakes, advising each
   other. A maintainer harvests the outcome and edits the grammar. *"Almost a completely different thing."*

And the conclusion to test: **there is no effective way to have distributed collaboration on updating the
grammar**, this is a limitation of the FieldWorks data model, and what rescues us is that the grammar is
small enough for a single integrator — one person, or several people together on a call.

## Measured before deciding

Object counts from two real projects, by class, straight from the `.fwdata`:

| | Larger project (152,222 objects) | Amharic |
| --- | --- | --- |
| Objects, total | 152,222 | 25,840 |
| Text and analysis | 34,308 | 2,522 |
| Lexicon | 4,530 | 274 |
| Hand-authored grammar | **~220** | **~620** |
| Phonological rules (`PhRegularRule`, `PhMetathesisRule`) | **0** | ~~**0**~~ **8** |
| Compound rules (`MoCompoundRule`) | ~~**0**~~ **4** | ~~**0**~~ **1** |
| Human evaluations of an analysis (`CmAgentEvaluation`) | 8 | 8 |

**Why "hand-authored grammar" is smaller than a naive count.** Grouping by class name suggests ~4,900
grammar objects in the larger project, but `MoStemAllomorph` (1,485), `MoStemMsa` (1,417) and `FsFeatStruc` (1,480)
scale with the *lexicon* — roughly one per entry or sense — so they are lexical data wearing grammatical
class names. What a linguist actually authors is the inventory and the rules: 44 environments, 44
phonemes, 37 parts of speech, 25 inflectional templates, 25 slots, 22 symbolic feature values. Counting
the affix inventory as grammar (145 affix allomorphs, 115 inflectional affix MSAs) brings it to ~480.

**So the claim is correct, and by a wider margin than stated.** The hand-authored grammar is about **0.15%
of the project** and two orders of magnitude smaller than the text and analysis data. Neither sample
project contains a single phonological or compound rule. A grammar of this size fits in one head, and the
integration cost of a second editor would exceed the cost of writing the whole thing once.

**Two cells above were wrong, corrected 2026-08-06 (see `E8`, `E9`).** Amharic has **8** `PhRegularRule`
objects, not zero, and both projects have compound rules: the larger project has 4 `MoExoCompound`, Amharic 1
`MoEndoCompound`. Two separate counting mistakes, both mine: the script printed only the twelve largest
classes per group, so I read a missing line as a zero; and `MoCompoundRule` is the **abstract parent**, so
querying it returns zero however many compound rules exist — the instances are always `MoEndoCompound` or
`MoExoCompound`. The same hierarchy trap I had just caught for `MoStemMsa`, missed one paragraph later.

**The conclusion is unaffected and the arithmetic barely moves.** The larger project gains 4 objects (~220 → ~224) and
Amharic gains 18 (~620 → ~638); the grammar is still ~0.15% of the project and still fits in one head. What
does not survive is the rhetorical flourish — *"neither sample project contains a single phonological or
compound rule"* is simply false, and it was doing real work below, where I used zero compound rules as
evidence that a compounding concern had no surface area. It has some. See
[ADR 0032](0032-stem-assessment-is-pangloss-supplied-lexicon.md), where that concern turned out to be
misplaced for an unrelated reason.

**One finding worth flagging separately:** only 8 human evaluations of a word analysis exist in the larger project,
which has 6,973 wordforms and 760 analyses. Approval of analyses is a feature FieldWorks has and projects
barely use. That is a caution about designing review around it, and it lowers the urgency of `I40` (who
overrules a native speaker when a corrected rule breaks an approved analysis) — the pile being protected
is currently almost empty. Two projects is a thin sample; treat this as a prompt to check more, not proof.

## Where the stated reasoning needed correcting

### The blocker is semantic, not structural — which makes it permanent

The data model does obstruct distributed work, in four concrete ways this repository has already hit:
`.fwdata` records no change history, so "what did you change" is not answerable without a baseline copy
(the reason footprint digests exist at all); sequence order is positional in the file, so a reorder
carries no recoverable intent ([ADR 0026](0026-order-is-declared-not-positional.md)); derived values are
maintained by forward-only hooks, so merged state can be quietly stale
([ADR 0016](0016-scratch-cache-copy-not-undo.md)); and custom-field ids are per-project, so independently
added custom fields collide.

**But none of those is the real obstacle.** Fix all four upstream and distributed grammar editing would
still not work, because **grammar rules are interdependent**: rule order encodes rule interaction, so two
locally-correct edits can compose into a grammar neither author intended. That is a property of what a
grammar *is*, not of how FieldWorks stores it, and it would be equally true in a perfectly versioned
store. This matters because it means **no upstream fix unlocks this**, so the decision below is
permanent rather than provisional. `ComparisonClass=feeding` in the manifest is this same fact already
written down for individual fields; the conclusion here is the same fact at the scale of a whole grammar.

### The three modalities split along the wrong axis

Grouping by *who acts* hides the pattern. Grouping by *what is being changed* explains it:

| What is changed | Size | Do two edits conflict? | Collaboration model |
| --- | --- | --- | --- |
| **Grammar** — rules, templates, phonology, feature system | ~220–480 objects | **Almost always possible** — order and interaction are the content | One integrator. No concurrency control |
| **Lexicon** — entries, senses, examples | ~4,500 objects | Rarely — two new words are independent | Safely parallel; many contributors |
| **Text analysis** — wordforms, analyses, glosses | ~34,000 objects | Rarely — each occurrence stands alone | Safely parallel, and the bulk of the volume |

> **The last column is superseded for the bottom two rows** by the amendment below. "Safely parallel" is
> still true of the data, but it is not an argument for Motif providing the surface: the dictionary already
> syncs through FLEx Lite and texts through Chorus. Motif's involvement there is for machine authorship
> only.

Read this way, **modality 3 is not a different kind of thing.** It is the same system pointed at the part
of the data that happens to be safely parallel. That is a much cheaper answer than treating it as a
separate product to build: word proposals and spelling reports are ordinary Proposals over lexical
fields, and they need no new machinery beyond a place to put them.

### Modalities 1 and 2 are not symmetric, and the asymmetry is the reason for the two descriptions

A strong model will read all forty pending proposals and compare them. A human will read three. So the
FieldWorks surface needs ranking and triage that the CLI surface does not — which is exactly why the
requirement includes **a short primary description and a longer explanation**. The short one exists so a
human can skip; the long one exists so a human can audit. Naming the reason keeps us from designing one
surface and assuming the other falls out of it.

### The single integrator is a single point of failure across time, not just across people

Concurrency is not the risk this project actually faces. In this domain the person holding the grammar
rotates off the project, and if the reasoning lives in their head and in a Zoom call, it leaves with
them. The successor inherits a working grammar and no idea why any of it is that way.

**So the artifact worth building is the rationale record, not the review workflow.** This inverts what M4
is for: not "let several people edit safely" — which we have just established is not achievable — but
"make sure the reasoning outlives the person." That is a smaller build and a larger payoff.

## Decision

### 1. Motif does not build concurrency control for grammar

No merge of concurrent grammar edits, no branch reconciliation for rules, no distributed grammar
authoring. One integrator at a time, which the project lock already enforces mechanically
([ADR 0030](0030-one-writer-cli-locks-like-fieldworks.md)).

### 2. What we build is the durable rationale record

Every Proposal carries a **short description** and an **extended explanation**, and both survive into the
applied record. The explanation may be written by an AI for a human to read; that is its expected origin,
not a fallback. A human may reply to it, and a reply that changes the proposal produces a **new revision**
— the amend loop, not a comment thread bolted to a frozen document.

### 3. Status is a decision; dependency is structure. They are not the same field

"Depends on another proposal" is **not a status.** It already exists as `requires`, the prerequisite graph
([ADR 0004](0004-prerequisite-graph-stable-ids-bound-apply.md)), and it is a fact about content that
governs apply order. A status is a human or policy decision *about* a proposal. Merging them would let
someone break apply ordering by changing a status.

Statuses: `proposed`, `deferred`, `approved`, `rejected`, `applied`, `superseded`. Dependencies are shown
next to the status, never as one.

### 4. `deferred` means "still wanted, needs re-validation" — never "frozen and still valid"

A deferred Proposal ages. Its bound Dry Run anchor goes stale and the project moves underneath it, so
deferring cannot preserve applicability. Stating this now costs a sentence; discovering it later costs a
wrong apply.

### 5. Review happens where the Proposal is: no server in scope 1

The CLI and FieldWorks are two surfaces over one record, held with the project. **No web service, no
review database, no replicated store.** This answers `D14` (we build neither a second comment system nor
a dependency on another team's), `D15` (review works offline because it never needed a network), and
`D16` (sharing stays a deliberate export, not a mode the system runs in).

### 6. Modality 3 stays out of scope, and stays possible for free

We build nothing for online debate. We keep it reachable by **not** foreclosing it: a Proposal is already
an immutable, content-addressed document, so any forum can reference one by digest without Motif knowing
the forum exists. The single obligation this creates is negative — **never make a Proposal require a
server to be meaningful.**

### 7. AI recommends; a human decision is recorded as a human decision

`MOT-10` already requires that AI actors are labelled and cannot satisfy a human or native-speaker role
by implication. That stands, and modality 1 makes it load-bearing rather than theoretical: an unattended
run can approve and apply, so the record must always show which of those it was.

## Amended 2026-08-06, later the same day — Motif exists for the grammar; words and texts ride along for the machines

The owner narrowed this further, and the narrowing is sharper than the section above:

> *Texts and new stems don't change the compilation of the FST in PanGloss. They can be done in parallel,
> but that synchronization is handled by Chorus and FLEx Lite. We don't need proposals and compilation
> reports and timing analysis. Motif can handle texts and words because it needs to, especially with
> AI-centric workflows, but humans updating texts and words don't need Motif.*

**What this corrects above.** The table said dictionary and text work "keeps the collaborative surface."
Wrong emphasis: there is no Motif surface for them to keep, because they already have one somewhere else.
Motif is not the tool for humans doing dictionary or text work, and offering to be would be duplicating a
shipped product.

### The parser-compilation point turns a principle into a structural rule

A new stem or a new text cannot invalidate a compiled parser, so **a lexical or text proposal can never
require a parser run.** [ADR 0028](0028-feeding-reorders-require-a-grammar-delta.md) already said review
should stay proportionate rather than "demanding a parser run for a spelling fix" — that was a principle
someone had to apply with judgement. It is now derivable: the expensive checks attach to grammar changes,
and which changes those are is already a column in the manifest. Nobody decides it per proposal.

### One thing a new stem *does* change, and it is cheap

A new stem is not behaviourally neutral even though it is compilation-neutral: it can make a wordform that
previously had one analysis ambiguous, so an existing analysis can become contested without any rule
changing. That is a lookup, not a compile, so it is cheap to detect — **report it, never gate on it.** With
8 human evaluations across the two sampled projects it is close to theoretical today.

### One correction to the premise, which does not change the conclusion

"Handled by Chorus and FLEx Lite" is solid for the dictionary and thinner for texts. This repository already
records that FLEx Lite's model — `MiniLcm`, `LcmCrdt`, `FwLiteProjectSync` — **"has no text/segment/wordform/
analysis surface"** ([component ownership research](../component-repository-ownership-research.md), against a
pinned commit). So the dictionary has a modern sync story and **texts and analyses have Chorus alone**, which
this repo separately flags as [not merging the applied log](../harmony-adoption-report.md#standing-risk--chorus-does-not-merge-the-applied-log)
and which the owner has already assessed as weak (`E19`, deliberately deferred).

That is not Motif's problem to solve and it does not change any decision here. It is recorded so that
"parallel text work is handled" is not read later as "handled well."

### Added decisions

**8. Motif exists for the grammar.** Dictionary and text support exists so that a *machine* has a validated,
recorded path to change them — not because collaboration over them needs one. When a human wants to add a
word, the answer is "use FLEx", and that is a feature.

**9. Expensive checks are grammar-only, and derived rather than judged.** Parser compilation, timing,
coverage deltas and Grammar Deltas attach to grammar operations. A lexical or text proposal gets the cheap
path: validate, apply, receipt.

**10. No human-facing review surface for dictionary or text proposals.** Not deferred — not wanted. Building
one would put Motif in competition with FLEx Lite over work FLEx Lite already synchronises, and would be a
second comment system in the same organisation, which decision 5 declined for the grammar case too.

**What this leaves M4 as:** the rationale record for grammar changes, and a machine-usable path for
everything else.

### Amended again, same day — decision 9 was too coarse: "no compile" is not "no report"

Decision 9 said a lexical or text Proposal "gets the cheap path: validate, apply, receipt." That is wrong,
and it discards the most useful thing available. The owner:

> *PanGloss has a native system for adding stems in specific categories for end users in FieldWorks. If I
> add 200 new stems with the proper categories, I should be able to analyze existing text and get a good
> report without even recompiling the grammar. Conversely, if I add a new text, I should be able to analyze
> it and get new coverage information without compiling the grammar newly. This may be important for an AI
> or human to assess when they get a new list of words — to determine that they are categorized correctly,
> and have the appropriate allomorphs.*

**Recompiling the parser and running the parser are different costs, and I had collapsed them into one.**
The right taxonomy is by what a check *needs*, which is derivable rather than judged:

| Check needs | Applies to | Cost |
| --- | --- | --- |
| A **recompiled** parser | grammar changes only | expensive |
| A **parser run** over text, no recompile | grammar, **stems, and texts** | cheap, and load-bearing |
| Neither | everything else | free |

So a Proposal adding 200 stems earns a real report, and that report is the **whole point of the change**:
did the stems get the right category, do they have the right allomorphs, do occurrences that previously
failed to parse now parse — and *correctly*, not merely at all. A Proposal adding a text earns fresh
coverage numbers. Neither needs a compile.

### The loop this creates is the one an AI will live in

```
   new text  ------------> analyze -----> coverage + the forms that did not parse
                                                   |
                                                   v
   confirm they now parse <--- analyze <--- propose stems with categories
                                                   |
                                                   +--> anything still failing is
                                                        a GRAMMAR question, and only
                                                        that needs a recompile
```

**No step in that loop compiles anything.** That is why an AI-centric workflow is viable at all: the cycle
is cheap enough to run on every Proposal and to run unattended, and it self-selects the small number of
cases that genuinely need grammar work. It also gives the coverage ramp question (`I37`) a natural answer
shape — delta against the previous run — though it does not settle which denominator to use.

### Two conditions this rests on, and neither is confirmed here

1. **A stem added at runtime must go through the same machinery a compiled-in stem does.** If the runtime
   path skips any rule application, the report is optimistically wrong: a stem would look correctly
   categorised when it is not, which is worse than no report. This is a question for PanGloss, recorded as
   an interface requirement in [the cross-repo plan](../plan-cross-repo.md#pangloss).
2. **"Cheap" needs a number.** Reanalysing the larger project's corpus is 6,973 wordforms; the cost scales with corpus
   size, not grammar size, so it is almost certainly fine — but it is unmeasured, and a coverage report
   that takes four minutes changes how often it can run. Measure before relying on it.

**Note what does not change:** a human adding stems in FieldWorks gets this report from PanGloss natively
and still does not need Motif (decisions 8 and 10 stand). What Motif adds is the report as **durable
evidence bound to a Proposal** — "these 200 stems moved coverage from 71% to 78%, and 14 produced no
analysis" — recorded rather than glanced at on screen.

### Read against the PanGloss source, 2026-08-06 — the two conditions resolve unevenly

I recorded the two conditions above as requirements on another repository. They were checkable here, so I
checked them.

**Condition 1 is satisfied by construction, and that is the one that mattered.** The worry was that a stem
added at runtime might skip rule application and make a report optimistically wrong. It cannot, because
there is no runtime-addition path to diverge from: `Grammar` owns the lexicon outright
(`entries: Vec<LexEntryDef>`, `pg-grammar/src/model.rs`), and nothing in `pg-grammar`, `pg-lexicon` or the
FFI adds an entry to a loaded grammar — no `add_entry`, no `insert_entry`, no `extend_lexicon`. New stems
arrive by rebuilding the grammar and loading it again, so a "new" stem *is* a compiled-in stem. **The risk
of a silently optimistic coverage report does not exist in this architecture.**

**Condition 2 changes shape.** *(Superseded 2026-08-06 by
[ADR 0032](0032-stem-assessment-is-pangloss-supplied-lexicon.md): an incremental path does exist — a
supplied-lexicon overlay beside the grammar, which I missed by grepping for the verbs I expected on the type
I expected. The paragraph below is accurate about `Grammar` and wrong about the system.)*
"Without recompiling" is true of the expensive step and false of the literal
mechanism. The FFI exposes exactly seven entry points — `hc_grammar_load`, `hc_grammar_free`,
`hc_parse_word`, `hc_parse_batch`, `hc_parse_word_opts`, `hc_parse_batch_opts`, `hc_buf_free` — so adding
stems means a **full reload**, not an increment. But `hc_grammar_load` calls `pg_grammar::load(xml)`, which
is an XML-to-structs parse and **not** an FST build. So each iteration costs one grammar reload rather than
a compilation, which is the property the workflow actually needs.

**What is still unknown is a number, not a design.** Nobody has measured `hc_grammar_load` on a real
grammar, or established whether pattern compilation happens eagerly at load or lazily during parse
(`pg-parse` depends on `pg-fst`). **Measure before proposing an incremental-add API:** a reload costing
40 ms needs no new interface, and one costing four seconds needs one badly.

**And the assessment layer already exists.** [ADR 0028](0028-feeding-reorders-require-a-grammar-delta.md)
asserted that a Grammar Delta "needs no new machinery" because it is existing vocabulary. Confirmed, with an
implementation: `pg-assess` ships `assess`, `compare`, `golden-diff` and `investigate`, exporting
`GrammarDelta`, `CaseDelta`, `DeltaCategory`, `AnnotationChange` and a versioned `DELTA_SCHEMA`. Its founding
principle is the one Motif reached independently — *"an analysis identity is a value, not a reference"*,
carrying stable source keys rather than compiler-assigned ordinals, precisely so a grammar edit yields
ordinary added/removed evidence instead of a comparison failure. **Motif consumes this; it does not build a
second one.**

### Amended again — the check class is derived per revision, and never declared

> *We should also assume that a new set of texts or a new set of words may also carry with it a grammar
> change, that becomes visible because of the new texts and words, as it is being refined. We need to assess
> each item live, as a proposal can change categories during its lifetime.*

This corrects the wording above. "Derivable from the operation rather than judged per Proposal" reads as
computed once. It is not: **a Proposal's check class is a function of its current revision, recomputed on
every revision.** A Proposal that begins as 200 stems and grows a rule change becomes a grammar Proposal and
inherits the expensive checks, and the cheap results it already had go stale — which `MOT-10`'s existing
stale-binding rules already cover, so this needs no new machinery.

**The stronger form: class is never declared, only derived.** If an author had to label a Proposal "lexical"
at creation, the system would fight the very workflow being described, where adding words is what *reveals*
the grammar problem. A declared class would need correcting by hand, and that correction is exactly the step
people forget.

### The workflow this is all for, stated plainly

> *A non-grammar-authoring linguist may analyse a large text, then sync it by Chorus, where the
> grammar-authoring linguist will then create a Proposal and see the coverage increase and change as he tries
> out different grammar rules to better align with the text analysis.*

Three things follow, and the third is a gap.

**It confirms sequential, not concurrent.** Two people, one hand-off through Chorus, no merge of grammar
edits — which is what decision 1 assumed, now with a named division of labour instead of an assumption.

**The text analysis is the target; the grammar is fitted to it.** Coverage is the objective function, and the
grammar author iterates: try a rule, reanalyse, read the number. Per-iteration cost therefore sets how many
variants a person can try in an afternoon, which is why the measurement above is worth taking before
anything else in this area is built.

**And it exposes a class of drift Motif does not guard.** A grammar Proposal's justification is a coverage
number computed against texts the Proposal never touches. Sync in new texts and that number is stale while
the footprint digest is untouched and reports everything as fine. Motif's drift machinery protects the
objects a Proposal *changes*; nothing yet protects the evidence it *rests on*. Recorded as `B24`, and
**decided the same day** by [ADR 0032](0032-stem-assessment-is-pangloss-supplied-lexicon.md) §4: a coverage
number always cites its corpus, that corpus's hash, the lexicon overlay revision, and the grammar identity.

## Consequences

- **`MOT-10` shrinks and changes shape.** Its centre is the rationale record, the revision loop, and
  statuses. Concurrent-review reconciliation leaves its scope entirely.
- **`MOT-19`'s reports have a named first consumer**: the strong model in modality 1, comparing pending
  proposals. Triage and ranking are a requirement of the FieldWorks surface specifically, not of both.
- **The 80–90%-by-one-author claim is scoped to grammar and must not be generalised.** The lexicon and the
  analyses are many-contributor by nature and hold 99% of the objects; designing their surface as
  single-integrator would be a serious error, and this ADR is not licence to do it.
- **Risk accepted, and named:** modality 1 can approve and apply language data with no human involved. We
  are not resolving that here, but decision 7 guarantees the record always distinguishes it, so a policy
  can be imposed later without re-deriving who did what.
- **Risk accepted:** the sparse-approval finding rests on two projects. If a project turns out to have
  thousands of human-approved analyses, decision 5's "no server" answer is unaffected, but `I40` becomes
  urgent again.
- **What would reopen this ADR:** evidence of a project whose *grammar* is genuinely co-authored by
  several people working apart. Not a large grammar — a *divided* one.

## The three workflows, as they will actually run

```
1  AI ALONE (CLI)                              2  HUMAN + AI (FieldWorks)
   strong model                                   human
     |  reads reports, compares proposals           |  reads short descriptions, triages
     |  writes short + extended rationale           |  reads the extended rationale
     v                                              v
   weaker model ---> Proposal (immutable)         weaker model ---> Proposal (immutable)
     |                                              |
     |  dry run on a copy, anchor bound             |  dry run on a copy, anchor bound
     v                                              v
   decision recorded AS AI                       decision recorded AS HUMAN
     |                                              |
     +---------------> apply ---> Receipt <----------+
                                    |
                          rationale survives the person

3  PEOPLE ONLINE (out of scope, kept possible)
   forum / chat / email
     |  words proposed, spellings reported, advice given
     v
   a maintainer authors Proposals from it, in 1 or 2 above
     ^
     |  may reference a Proposal by its digest — no server required of us
```

## Amendments

### 2026-08-31 — decision 7 is superseded: there is no Decision to label

Decision 7 said an AI recommends and a human decision is recorded as a human decision, with the actor type
never inferred. That rule governed a **Decision** record — an approved-or-rejected verdict bound to one exact
Proposal revision — and the owner has since removed approval from the product altogether. There is no verdict
to attribute, so there is nothing for decision 7 to govern.

**In plain terms:** nobody signs off a change any more. A Proposal is applied when the measurements say it is
safe to apply, and a person who wants to apply it anyway says `--force`.

What replaced it is evidence rather than authority. `apply` refuses when no Assessment covers the Proposal's
current content, when the Assessment measured a different project state than the one being applied to, or when
it shows a regression against the project's current Assessment; `--force` overrides all three. The `approve`
verb, the `approved` status, the `Decisions` table and the `DecisionActorType` human/ai distinction are gone.

The concern behind decision 7 — never letting an AI's judgement pass as a human's — is not abandoned; it is
relocated to where an actor still appears. `apply` records who applied, in the Receipt and the applied log,
and that is a fact about who ran the command rather than a claim about who agreed with it.
