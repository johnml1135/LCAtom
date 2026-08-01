# Motif

*Named, reusable units of linguistic intent for FieldWorks' LibLCM data.*

**Motif** is the semantic operation vocabulary for changing a language project — a small, named,
reusable unit of intent (`MergeLexicalEntries`, `SplitSense`, `CreateAffixProcessRule`) that recurs
across a project and is *lowered* into concrete changes. In music a motif is the smallest recognizable
unit that recurs and is developed across a work; that is exactly the shape of a semantic operation. The
name sits in the family this ecosystem already has: **Chorus** (sync), **Harmony** (merge), **Motif**
(intent). Slots: repository and product **Motif**, CLI **`motif`**, and inside FieldWorks' Avalonia UI
the plain-words label **"Proposed Changes"**. Recorded in
[grill-decisions D7](docs/grill-decisions.md#d7--the-name-is-motif-and-it-absorbs-lcatom); the project
was called *LCAtom* until 2026-07-30 and that name is retired.

**Why it exists.** A person or an AI working on a language asks *"what if we change this — does the
text parse better?"* Motif makes that question safe to ask, cheap to repeat, and honest to answer:
author a change at a high level, review the exact state delta before anything is touched, let
[PanGloss](https://github.com/sillsdev/machine) parse a text and report, compare against earlier runs,
then keep it or throw it away. Everything HermitCrab supports must be authorable here, in a friendly
way — that is the primary completeness criterion. See
[ADR 0010](docs/adr/0010-hermitcrab-experimentation-is-the-primary-purpose.md).

Motif is deliberately storage- and workflow-agnostic: a proposed change can come from a file, a Git
repository, a database, a web service, an AI agent, a FieldWorks panel, or another application, and
Motif gives it one meaning regardless. Where those changes are *stored and merged* is settled and is not
Motif's: it is Harmony's, per ADR 0013 below.

## Status — direction reversed, 2026-07-27

> **Read [ADR 0013](docs/adr/0013-harmony-is-the-change-mechanism.md) before anything else.**
>
> This repository set out to build a change-set contract and runner. That premise is **withdrawn**.
> `SIL.Harmony` — the CRDT/commit-log engine under FieldWorks Lite — already provides semantic change
> objects, hash-chained commits, per-object snapshots, before/after state at any commit, validation,
> and `OpaqueChange` for changes a client cannot yet interpret. Together with LibLCM's unit of work,
> that is **two** supported change-management mechanisms already in the ecosystem. A third needs to
> show both are inadequate. It cannot.
>
> Every prior document here was written without Harmony having been read. That is the error.
>
> **What survives is analysis, not machinery:** the 100%-classified coverage manifest, the
> HCLoader-derived grammar map, and the finding that ordered grammar (feeding/bleeding rule order,
> index-as-identity alpha variables, positional `MoAffixProcess.Output`) cannot ride on a
> last-writer-wins scalar order. That last one is a **defect report against Harmony's
> `SetOrderChange`**, not an argument for this repository.
>
> The shipped code below is real and passes its tests. It is no longer on a path to being the API.

## Where the plan stands, 2026-07-30

> [ADR 0014](docs/adr/0014-generate-the-crdt-layer-from-masterlcmodel.md) settles **how** the
> LibLCM-shaped CRDT layer gets built: it is **generated** from `MasterLCModel.xml`, whose 898 field
> declarations are exactly the 898 rows of `manifest/liblcm-inventory.tsv`. Structure comes from
> LibLCM's model file, policy from the manifest, joined on `(Class, Field)`, with unmatched keys
> failing the build. Generated output targets `LcmCrdt` in lexbox; **Harmony core gains CRDT
> primitives only, never domain vocabulary.**
>
> The acceptance gate is falsifiable: regenerate the three `IPossibility` entities the join can reach
> (`PartOfSpeech`, `MorphType`, `ComplexFormType`) and diff against their tested implementations. It
> licenses the mechanical majority only — it contains no `feeding` or `index-as-identity` fields, so
> the ordered-grammar residue still needs its own proof. Note also that a hand-maintained
> **MiniLcm ↔ LibLCM name map** is a prerequisite the model file does not supply.
>
> Supporting inventories, all evidence-based with `path:line` citations:
> [liblcm-codegen](docs/inventory-liblcm-codegen.md) ·
> [harmony-generation-surface](docs/inventory-harmony-generation-surface.md) ·
> [harmony-conflict-reporting](docs/inventory-harmony-conflict-reporting.md).
> Live decisions: [grill-decisions.md](docs/grill-decisions.md) (D1–D7).

## Language

[CONTEXT.md](CONTEXT.md) is the canonical glossary and is binding in code, CLI verbs, and prose. The
three terms most often got wrong, settled by
[ADR 0015](docs/adr/0015-proposal-assessment-dry-run-vocabulary.md):

- **Proposal** — the stored, reviewable set of changes. *Not* "change set", which retired with the
  contract ADR 0013 withdrew.
- **Assessment** — an immutable PanGloss run: does the grammar parse better? PanGloss owns this word.
- **Dry Run** — what a Proposal would do to a live LibLCM model, computed without mutating it.

Documents dated before 2026-07-31 use the retired vocabulary and are historical records.

## The live plans — three repositories, one milestone ladder

ADR 0014's consequence is that this is a **three-repo change with a package chain between two of them**,
so there are three plans and one file that keeps them aligned. **Start with the cross-repo ladder;** it
is the only place milestones are defined.

- **[plan-cross-repo.md](docs/plan-cross-repo.md)** — milestones M0–M5, the dependency edges between
  repos, and the rules that keep the other three plans from drifting apart.
- [plan-motif.md](docs/plan-motif.md) — this repo: the MiniLcm ↔ LibLCM name map, the `(Class, Field)`
  join, the generator, and the semantic + lowering layers (`MOT-1`…`MOT-8`).
- [plan-harmony.md](docs/plan-harmony.md) — the `harmony` repo: **primitives only, never domain
  vocabulary** — converging sequence, reference-set policy, cross-owner move, deferred diagnostic
  channel (`HAR-*`, numbered from [harmony-additions-needed.md](docs/harmony-additions-needed.md)).
- [plan-lcmcrdt.md](docs/plan-lcmcrdt.md) — `languageforge-lexbox` (`backend/FwLite/LcmCrdt`): the
  generated entities, change classes, registrations, and hand-written migrations (`CRDT-1`…`CRDT-8`).
- [motif-overall-plan.md](docs/motif-overall-plan.md) — the product-level pitch the four serve: the
  evidence corpus, proposal/review workflow, FieldWorks Avalonia surface, and the `motif` CLI.

**Nothing in any of the four has started.** The first item is `MOT-1`, the name map, because ADR 0014
identified it as required and non-existent.

## Status of the implementation

There is a working, tested, end-to-end implementation, not just a plan — of the design ADR 0013
withdrew. `motif open` loads a real FieldWorks project; a draft can be authored, assessed, applied
atomically, read back, and logged — all through the real `SIL.Motif.{Contract,Model,Runner,Host,Cli}`
projects, exercised by 82/82 passing tests against a real `LcmCache`. It carries the new name; that does
not promote it. See [build stages](docs/build-stages.md) for what "done" means stage by stage, and
[motif-overall-plan.md](docs/motif-overall-plan.md)'s Phase 0 for the quarantine that has **not** yet
been done.

- **The catalog is one operation deep.** The only operation implemented end to end is
  `lexical/sense/setGloss`. There are no create, delete, sequence, or grammar operations yet — see the
  [operation-catalog plan](docs/operation-catalog-plan.md) for the roadmap to lexical and grammar
  completeness, and [implementation plan](docs/implementation-plan.md) for per-phase status.
- **Forward HermitCrab projection is deleted, not pending.** PanGloss reads `.fwdata` directly, so
  there is nothing to project — see
  [ADR 0011](docs/adr/0011-experiment-loop-boundary-motif-is-the-record.md). Reverse `Expand`
  (authoring grammar in HC-friendly terms) remains the primary grammar surface and is unbuilt.
- **The LibLCM coverage manifest is fully classified** (`manifest/liblcm-inventory.tsv`, 898 rows,
  zero unclassified in-scope rows) but nothing yet generates operation kinds from it — see
  [issues register](docs/issues.md). Building that generator is now the next piece of work
  ([ADR 0012](docs/adr/0012-build-order-hc-spine-first-kinds-generated.md)): **332 kinds for the
  HermitCrab-reachable surface, 915 for all of it, against ~12 hand-written type handlers.**
- **Next up:** the HC-reachable lexical spine (L0, ~37 fields), then grammar (G0–G2), then export +
  attachments to close the loop. The rest of the lexical catalog is backfilled after, driven by the
  non-HermitCrab consumers. See [build stages](docs/build-stages.md).

Everything else in this repository is normative design written ahead of the code — deliberately, and
still binding. The full document set:

- [Architecture and decisions](docs/architecture.md)
- [Rationale](docs/rationale.md)
- [Decision records](docs/adr/) — start with
  [ADR 0010](docs/adr/0010-hermitcrab-experimentation-is-the-primary-purpose.md) (why this exists),
  then [ADR 0011](docs/adr/0011-experiment-loop-boundary-motif-is-the-record.md) (where it stops) and
  [ADR 0012](docs/adr/0012-build-order-hc-spine-first-kinds-generated.md) (what gets built first)
- [Normative change-set contract](docs/change-set-contract.md)
- [Custom fields](docs/custom-fields.md)
- [Applied-change log](docs/applied-log.md)
- [HermitCrab projection](docs/hermitcrab-projection.md)
- [Flexicon harvest](docs/flexicon-harvest.md)
- [HC grammar map — the normative grammar write-surface](docs/hc-grammar-map.md)
- [HC surface scope — three coverage tiers](docs/hc-surface-scope.md)
- [API surface layer 1 — the LibLCM primitive surface](docs/api-surface-layer1.md)
- [API surface — the HC grammar-facing surface](docs/api-surface-hc.md)
- [Issues register](docs/issues.md)
- [Stress-test findings](docs/stress-test-findings.md)
- [Stage-2 change management (vision)](docs/stage2-change-management.md)
- [Conflict and rebase semantics](docs/conflicts-and-rebase.md)
- [Cross-repo plan — the live milestone ladder](docs/plan-cross-repo.md), and the three plans it
  aligns: [motif](docs/plan-motif.md) · [harmony](docs/plan-harmony.md) · [LcmCrdt](docs/plan-lcmcrdt.md)
- [Motif overall plan (product pitch)](docs/motif-overall-plan.md)
- [What Harmony needs added — evidence behind the `HAR-*` items](docs/harmony-additions-needed.md)
- [Implementation plan](docs/implementation-plan.md) — **superseded by ADR 0013**, retained for its
  per-phase status detail
- [Operation-catalog plan (lexical & grammar)](docs/operation-catalog-plan.md) — likewise superseded
- [Build stages](docs/build-stages.md)
- [Implementation-session handoff](HANDOFF.md) — **obsolete**, see its own banner

## Product boundary

This repository owns:

- the versioned semantic operation vocabulary;
- JSON parsing, validation, and canonicalization;
- canonical entity-ID/GUID conversion;
- canonical semantic snapshots and digests;
- deterministic two-way and common-ancestor three-way mechanical diff;
- assessment, planning, conflict diagnostics, rebase, atomic apply, read-back, and receipts;
- a thin applied-change log written into the project for provenance and idempotence;
- conformance fixtures proving that all supported clients receive the same behavior.

It does not own:

- opening, saving, closing, locking, backing up, or disposing FieldWorks projects;
- review queues, permissions, approvals, hosting, Git history, or database storage;
- linguistic entity matching between unrelated projects;
- arbitrary C#, Python, reflection, or raw-property mutation;
- UI behavior;
- the Notebook, scripture, and interlinear-text analysis surface, which remains FieldWorks-centric;
- a competing implementation in Flexicon, GramTrans, FlexToolsMCP, or FieldWorks.

LibLCM remains the authority for model invariants, persistence, ownership, and undo/redo. This
runner is the semantic interoperability layer above it.

## Motivating consumers

This runner exists because several independent tools each need the same thing — a mechanical,
repeatable, reviewable, sequenceable, and rebasable way to update the LibLCM model for **dictionaries
and grammars** — and each is otherwise forced to hand-roll raw property mutation against LibLCM. It is
being built for them, and they are expected to be refactored to call this compiled engine rather than
each reimplementing apply, ordering, delete-closure, and conflict semantics:

- **Linguistic Assistant** — an AI quality-assurance and language-documentation assistant that
  *emits* canonical Change Sets (lexical, morphophonology, and bilingual sense-link tiers) from corpus
  and parallel-text evidence. It already speaks this contract's vocabulary and independently reached
  the same "no CRDT for ordered grammar" conclusion; this repository is the conforming runner it
  targets.
- **PanGloss** — a Rust HermitCrab/FST morphological-parsing engine that emits FieldWorks
  investigation handoffs when a grammar fails to parse a word. The grammar and morphophonology edits
  those handoffs motivate are realized as Change Sets against the LibLCM grammar model.
- **Flexicon** — [github.com/MattGyverLee/flexicon](https://github.com/MattGyverLee/flexicon) — a
  Python (`pyflexicon`) wrapper over LibLCM for reading and writing FLEx projects.
- **FlexToolsMCP** — [github.com/MattGyverLee/FlexToolsMCP](https://github.com/MattGyverLee/FlexToolsMCP)
  — an MCP server that lets AI assistants manipulate FLEx lexicon data in natural language over the
  LibLCM/flexlibs API surface.
- **GramTrans** — [github.com/MattGyverLee/GramTrans](https://github.com/MattGyverLee/GramTrans) — a
  FlexTools module that transfers grammar components (phonology, morphology, lexicon scaffolding,
  templates) between FLEx projects.

The shared scope is dictionaries and grammars, including the phonological and morphological model that
Hermit Crab and PanGloss consume. It deliberately excludes the Notebook, scripture, and
interlinear-text analysis surface, which stays FieldWorks-centric.

## Governing invariant

For every supported state:

```text
Normalize(Apply(A, Diff(A, B))) == Normalize(B)
```

Application is atomic at the whole-change-set boundary. The runner never silently reorders
operations, guesses linguistic identity, changes authored intent during rebase, or partially
applies a change set.

