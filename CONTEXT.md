# Motif

Motif is the semantic vocabulary and tooling for proposing, evaluating, and applying changes to a
FieldWorks language project — its lexicon and, above all, its grammar. It sits between **LibLCM** (which
owns the data and is the only authority on it) and **PanGloss** (which parses with the result).

## The unit of change

**Motif operation**:
The smallest durable unit a Proposal is made of, named for its field and its verb —
`grammar/fsFeatDefn/setAbbreviation`. Named for the musical sense: the smallest recognizable unit that
recurs and is developed across a work. This is the hashed vocabulary a digest is computed over, so it
does not churn ([ADR 0021](docs/adr/0021-cli-is-the-full-surface-layer-1-churns.md)).
_Avoid_: command, CRUD+ operation, mutation, edit

**Intent**:
A unit of linguistic purpose an author states, which Lowering turns into the several Motif operations
that realize it — `AuthorLexemeForm`, `AuthorFeatureStructure`. What an agent addresses; the verb
surface may churn beneath a stable operation vocabulary
([ADR 0029](docs/adr/0029-agents-address-layer-1-only.md)).
_Avoid_: macro, template, high-level operation, composite

**Lowering**:
Turning one Motif operation into the concrete changes that realize it against a particular store.
_Avoid_: compilation, translation, expansion

**Proposal**:
A stored, named set of Motif operations that is reviewable as one unit. It owns its attached
Assessments, has a lifecycle, and can be applied to a language project or discarded. A Proposal does
not combine unrelated changes.
_Avoid_: PR, change set, change group, patch, branch

**Draft**:
A Proposal that is still being authored: it has an id and a name, and no committed revision yet.
Finalizing does not move it anywhere — it commits the first immutable revision and changes the
Proposal's state. A Draft is therefore a phase of a Proposal's life, never a separate thing kept
somewhere else ([ADR 0041](docs/adr/0041-the-database-is-the-only-store.md) decision 3).
_Avoid_: working copy, staging area, scratch proposal, uncommitted proposal

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
own contracts. Motif stores Assessments and compares them; it never renders a verdict from one. An
Assessment records the Assessment scope it ran under, because that decides which Reports can be
answered from it at all.
_Avoid_: parse report, evaluation, score, verdict

**Assessment scope**:
What a run holds equal so that two runs can be subtracted: which words, which engine, what the run
collects, and what limits it applies. Declared per project and embedded in each Assessment by content,
so editing the declaration cannot reinterpret a measurement already taken. A Baseline is measured under
the superset of every scope a Proposal will use, so a narrower candidate run is comparable without
measuring the Baseline again.
_Avoid_: profile, config, settings, run options

**Grammar Delta**:
The exact structural difference between two Assessments — which analyses were added, removed,
retained, or became incomplete.
_Avoid_: diff, score change, improvement

**Dry Run**:
What a Proposal would do, computed by applying it to a throwaway copy of the project and reading the
effects back from the engine — never by predicting them. The live model is not mutated
([ADR 0016](docs/adr/0016-scratch-cache-copy-not-undo.md)).
_Avoid_: assessment, preview, plan, simulation

**Baseline**:
A saved, minimal, file-backed copy of the project state from which Motif can make many independent
Dry Run scratches. It contains the LibLCM and writing-system data needed to reproduce engine behaviour,
but no linked media bytes. A Baseline is replaced explicitly, not merely because time passed.
_Avoid_: snapshot, backup, session cache, live model

**Preflight**:
The final non-mutating comparison against the live model immediately before Apply. It proves that the
approved evidence still matches the project; it is not the earlier, reusable Dry Run.
_Avoid_: dry run, assessment, validation pass

**Drift**:
The condition where the project has moved since a Dry Run was computed, so the Dry Run no longer
describes what applying the Proposal would do.
_Avoid_: staleness, conflict, merge failure

**Apply Authorization**:
An opaque, one-use, short-lived grant from the Motif worker for exactly one Apply attempt. It binds the
project, Proposal intent, approved Decision, Dry Run, Baseline, and Assessment disposition; it is neither
the human Decision nor a general security credential.
_Avoid_: approval, token, permission

**Conflict**:
A loud, derived condition in which the language project's applied history and the Motif store disagree
about a Proposal. It is shown ahead of ordinary workflow states until a person resolves it, but it is not
itself a Proposal lifecycle state.
_Avoid_: drift, merge conflict, failed assessment

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
may be prepared and Proposals are applied. The Runner is always handed an already-loaded model; the
FieldWorks adapter or the `net10.0` Host owns loading, saving, locking, and disposal.
_Avoid_: cache, session, connection

**Motif store**:
Everything Motif keeps about **one language project**, in that project's paired sibling database:
Proposals, Drafts, Decisions, jobs, Assessments, Reports, Receipts, Corpora, and the applied index.
Content digests still identify immutable intent and evidence, but the storage container is not itself
content-addressed. There is no merge engine and no replication. Nothing about a project lives anywhere
else, and nothing that is not about a project lives here — that is the Machine store.
_Avoid_: proposal store, change store, database, repository, queue

**Machine store**:
The single database for one logged-in user, holding what belongs to the installation rather than to any
project: the Known projects, and the usage log of the Motif API's own calls. It is deliberately small.
Machine-wide exclusion — PanGloss capacity and the single-runner guarantee — is not kept here but in
named operating-system mutexes, because the kernel releases those when a process dies.
_Avoid_: global store, config, registry, settings, central database

**Known project**:
A language project this installation has been pointed at, recorded in the Machine store when a command
names it. The list is what lets the Motif job runner find work in a project it was not launched with. A
Known project whose file has gone is forgotten rather than reported.
_Avoid_: registered project, workspace, recent project, project list

**Queue order**:
The position a job holds in the single ordered run of work across every Known project. It is stored,
not derived from when a row last changed, so moving a job changes what runs next rather than only what a
list displays ([ADR 0041](docs/adr/0041-the-database-is-the-only-store.md) decision 6).
_Avoid_: priority, rank, position, sort order

**Motif job runner**:
The one on-demand process for a logged-in Windows user that takes work which must outlive a command —
durable jobs, project queues, Baseline refreshes, and PanGloss orchestration. It claims work from the
paired database; nothing sends it requests, and it answers none. It never owns a FieldWorks user's live
`LcmCache`. Called *the worker* in documents written before 2026-08-26, when it also served a named-pipe
protocol that [ADR 0040](docs/adr/0040-one-api-the-cli.md) withdrew.
_Avoid_: server, service, daemon, project host, worker

**Motif API**:
The `motif` executable and its `--json` output. The only interface Motif exposes — to AI agents, scripts,
tests, and the FieldWorks surface alike. There is no library entry point for a host and no wire protocol.
_Avoid_: wire protocol, worker protocol, endpoint, RPC

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
