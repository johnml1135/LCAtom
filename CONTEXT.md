# Motif

Motif is the semantic vocabulary and tooling for proposing, evaluating, and applying changes to a
FieldWorks language project — its lexicon and, above all, its grammar. It sits between **LibLCM** (which
owns the data and is the only authority on it) and **PanGloss** (which parses with the result).

## The unit of change

**Motif operation**:
A small, named, reusable unit of linguistic intent — `MergeLexicalEntries`, `SplitSense`,
`CreateAffixProcessRule`. Named for the musical sense: the smallest recognizable unit that recurs and
is developed across a work.
_Avoid_: command, CRUD+ operation, mutation, edit

**Lowering**:
Turning one Motif operation into the concrete changes that realize it against a particular store.
_Avoid_: compilation, translation, expansion

**Proposal**:
A stored, named set of Motif operations that is reviewable as one unit. It owns its attached
Assessments, has a lifecycle, and can be applied to a language project or discarded. A Proposal does
not combine unrelated changes.
_Avoid_: PR, change set, change group, patch, branch

**Construct**:
One of the ~30 grammar things a Proposal can be about — a stratum, a natural class, an affix template,
a phonological rule. **The unit in which grammar support is staged and delivered**, and hand-authored
because the grouping is linguistic judgement. It used to double as the middle segment of an operation's
name; it no longer does ([ADR 0023](docs/adr/0023-derived-kind-names-required-descriptions.md)) — that
segment is derived from the declaring class. Construct is now purely about *what work ships together*.
_Avoid_: entity, class, feature

## Evaluating a Proposal

Two different evaluations, deliberately named apart. One asks *does the grammar parse better?*; the
other asks *what would this do to the project?*

**Assessment**:
An immutable PanGloss run: one grammar against one frozen set of evaluation words, producing parse
results and diagnostics. PanGloss owns this word — it has a `pg-assess` crate and uses the term in its
own contracts. Motif stores Assessments and compares them; it never renders a verdict from one.
_Avoid_: parse report, evaluation, score, verdict

**Grammar Delta**:
The exact structural difference between two Assessments — which analyses were added, removed,
retained, or became incomplete.
_Avoid_: diff, score change, improvement

**Dry Run**:
What a Proposal would do, computed by applying it to a throwaway copy of the project and reading the
effects back from the engine — never by predicting them. The live model is not mutated
([ADR 0016](docs/adr/0016-scratch-cache-copy-not-undo.md)).
_Avoid_: assessment, preview, plan, simulation

**Drift**:
The condition where the project has moved since a Dry Run was computed, so the Dry Run no longer
describes what applying the Proposal would do.
_Avoid_: staleness, conflict, merge failure

**Receipt**:
The record that one Proposal was applied to one project, naming the before and after state. The
durable edge in a project's history.
_Avoid_: application receipt, success result, audit log

**Report**:
A query over an Assessment and the project's own data, producing statistics and findings. **Advisory
always** — a Report never gates anything.
_Avoid_: score, verdict, metric, dashboard, health check

**Check Run**:
A Report cited as evidence on a Proposal, which freezes its inputs and binds it to the state it was
computed against.
_Avoid_: check, test, gate, validation, CI run

**Selection**:
A named set of word forms to be parsed, listed out in full, with a note of where they came from. Not a query
and not a sample — a person may pick fourteen words with nothing in common, and why they matter is theirs.
A list can be exported from an Assessment, but what is kept is the words, so nothing has to be re-derived.
_Avoid_: query, sample, filter, scope, subset, test set, corpus descriptor

**Hole**:
A combination the grammar licenses that no analysis exercises. Undecided by construction: it means an
over-broad rule, a missing word, or an unreachable combination, and Motif does not guess which.
_Avoid_: gap, miss, failure, uncovered case

## Where state lives

**Canonical data**:
The FieldWorks language project itself — `.fwdata` on disk, or the live LibLCM model loaded from it.
The authority on model invariants, ownership, and validity.
_Avoid_: source of truth, database, backing store

**Live model**:
A loaded, in-memory LibLCM model representing the project as it is right now, against which Dry Runs
are computed and Proposals applied. A Motif tool never opens or owns a project's lifecycle; it is
handed one.
_Avoid_: cache, session, connection

**Motif store**:
Everything Motif keeps outside the language project: Proposals and Receipts as immutable content-addressed
documents, plus Corpora and Assessments. There is no merge engine and no replication.
_Avoid_: proposal store, change store, database, repository, queue

**Text**:
FieldWorks' term, kept for FieldWorks' meaning: an interlinearised document **in the language project**.
Never used for a Corpus or a Document, which Motif holds and FieldWorks does not.
_Avoid_: using this for corpus material

**Corpus**:
A body of running text Motif holds, with a record of where it came from, how it was tokenised, and what
anyone attests about it. Never part of the language project.
_Avoid_: text, texts, dataset, word list, sample

**Document**:
One unit within a Corpus — an article, a file. The boundary counts and n-gram models must not run across.
_Avoid_: text, article, item, record

**Corpus bundle**:
The handoff an outside tool writes when it has fetched text for Motif: a small file describing one Corpus and
naming its Documents, with each one's origin and licence. It names files; it does not contain them.
_Avoid_: import, package, archive, manifest

**Licence capabilities**:
What a licence permits — redistribute, derive, use commercially — as distinct from what it is called. Three
states: yes, no, and nobody established it. The last blocks derived work exactly as "no" does.
_Avoid_: licence (that is the name), permissions, rights, flags

**HC interpretation**:
The rules by which grammar in a language project becomes a HermitCrab grammar — the semantics
FieldWorks' `HCLoader` implements and PanGloss ports. An authority on meaning, not a stored format.
_Avoid_: HC XML, export, projection

## Coverage and generation

> **"Coverage" is ambiguous in this project and must always be qualified.** An unqualified "coverage" is
> never acceptable in prose, a report, or an API name. There are five senses:
>
> | Term | What it measures | Whose |
> | --- | --- | --- |
> | **parse coverage** | What share of a word list parsed. The raw figure | PanGloss |
> | **grammar coverage** | How much of a language the grammar reaches — parse coverage reported over a named Corpus, with provenance | Motif |
> | **feature coverage** | Which declared grammar features, and which combinations of them, the analysed words actually exercise. What a Hole is an absence in | Motif |
> | **test coverage** | What the manual analyses assert. Always qualified in prose and API names — this repo has literal unit tests | Motif |
> | **model coverage** | Whether the generator accounts for every field in LibLCM's model. The terms in this section concern this one | Motif |
>
> *Renamed 2026-08-09.* **Feature coverage** was called *grammar coverage* until the two were being used in
> one sentence. The name moved to the measure people reach for it to mean — *how much of the language does
> this grammar handle* — and the feature-combination measure took the name that says what it counts.
> Documents written before that date use the old sense; `docs/grammar-coverage-design.md` is about **feature
> coverage** despite its filename, which is left alone because it is cited by name elsewhere.
>
> **Grammar coverage and feature coverage answer opposite questions** and a grammar can score well on one and
> badly on the other: *how much text can the grammar touch* versus *how much of the grammar does the text
> reach*. That is why both exist.

**Manifest**:
One row per field in LibLCM's model, recording what is in scope and which Construct it belongs to. Those
two are human judgement that exists nowhere else. Verbs and comparison behaviour are **derived** from
LibLCM's own structural declarations and checked against the manifest, not taken from it
([ADR 0022](docs/adr/0022-structure-is-derived-policy-is-five-rows.md)).
_Avoid_: inventory, schema, spec

**Group**:
The first segment of an operation kind — **`lexical`**`/lexSense/setGloss`. **Derived** from the declaring
class's LibLCM prefix family ([ADR 0024](docs/adr/0024-group-is-derived-domain-is-editorial.md)). Its job is
namespacing and versioning granularity: what changes together when LibLCM changes. **Not** a statement about
who should review something.
_Avoid_: domain, namespace, area

**Domain**:
Which linguistic area a field belongs to for **review purposes** — who should look at a change. Hand-authored,
never hashed, and deliberately allowed to disagree with the kind's `group`: `MoForm.Form` is named
`grammar/moForm/setForm` and reviewed as `lexical`, because a lexeme form is lexicon even though its class is
morphology.
_Avoid_: group, category, class

**Class segment**:
The middle segment of an operation kind — `lexical/`**`lexSense`**`/setGloss`. **Derived**, as the LibLCM
class where the field is declared with its first letter lowercased
([ADR 0023](docs/adr/0023-derived-kind-names-required-descriptions.md)). Deliberately ugly and never
curated: what a human needs in order to understand an operation lives in its **description**, which nothing
hashes. **Not the same thing as a Construct** — that word means a staging unit and nothing else now.
_Avoid_: construct, name map, mapping table, alias list

**Description**:
The required, never-hashed sentence explaining what an operation does, seeded from the labels FieldWorks
already shows linguists. Free to improve at any time, because no digest depends on it. It exists for the
human reviewing a Proposal, not for the agent authoring one.
_Avoid_: comment, doc, label

**Ordered grammar**:
The grammar whose meaning depends on sequence — phonological rule order encoding feeding and
bleeding, and alpha variables using position as identity. The part that cannot ride on a
last-writer-wins order value.
_Avoid_: sequences, lists, sorted fields
