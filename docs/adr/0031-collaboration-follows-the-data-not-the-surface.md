# ADR 0031 — Collaboration follows the data, not the surface: grammar has one integrator

**Status:** accepted, 2026-08-06. Answers `D14`, `D15`, `D16`. Scopes `MOT-10`, `MOT-17`, `MOT-18` and
therefore most of M4. Builds on [ADR 0021](0021-cli-is-the-full-surface-layer-1-churns.md) (the CLI is the
whole surface) and [ADR 0029](0029-agents-address-layer-1-only.md) (the agent is a contributor).

**In plain terms:** a grammar is small enough that one person or one AI writes nearly all of it, and its
rules depend on each other so heavily that two people editing it separately would produce a grammar
neither of them meant. So Motif will not build machinery for several people to edit a grammar at once.
What it builds instead is a durable record of *why* each change was made, because the person who wrote
the grammar will eventually leave and the reasoning must not leave with them. Dictionary and text work
is the opposite — large, and safely parallel — and keeps the collaborative surface.

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

| | Sena 3 | Amharic |
| --- | --- | --- |
| Objects, total | 152,222 | 25,840 |
| Text and analysis | 34,308 | 2,522 |
| Lexicon | 4,530 | 274 |
| Hand-authored grammar | **~220** | **~620** |
| Phonological rules (`PhRegularRule`, `PhMetathesisRule`) | **0** | **0** |
| Compound rules (`MoCompoundRule`) | **0** | **0** |
| Human evaluations of an analysis (`CmAgentEvaluation`) | 8 | 8 |

**Why "hand-authored grammar" is smaller than a naive count.** Grouping by class name suggests ~4,900
grammar objects in Sena 3, but `MoStemAllomorph` (1,485), `MoStemMsa` (1,417) and `FsFeatStruc` (1,480)
scale with the *lexicon* — roughly one per entry or sense — so they are lexical data wearing grammatical
class names. What a linguist actually authors is the inventory and the rules: 44 environments, 44
phonemes, 37 parts of speech, 25 inflectional templates, 25 slots, 22 symbolic feature values. Counting
the affix inventory as grammar (145 affix allomorphs, 115 inflectional affix MSAs) brings it to ~480.

**So the claim is correct, and by a wider margin than stated.** The hand-authored grammar is about **0.15%
of the project** and two orders of magnitude smaller than the text and analysis data. Neither sample
project contains a single phonological or compound rule. A grammar of this size fits in one head, and the
integration cost of a second editor would exceed the cost of writing the whole thing once.

**One finding worth flagging separately:** only 8 human evaluations of a word analysis exist in a project
with 6,973 wordforms and 760 analyses. Approval of analyses is a feature FieldWorks has and projects
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
