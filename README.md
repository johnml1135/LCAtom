# Motif

**A PR-like collaboration system for semantic changes to language data.**

Motif is intended to let humans and AI agents propose, inspect, check, discuss, approve, apply, and
audit changes to lexical, text, and grammar data. Grammar is the first product customer.

The destination combines two collaboration models:

- Google-Docs-like retention and convergence: authored work is not lost merely because it cannot be
  applied safely yet;
- Git/GitHub-like control: exact candidate revisions, semantic diffs, CI-style checks, typed review,
  approvals, stale-input detection, controlled landing, and auditable outcomes.

Motif does not put Git commits or textual patches around `.fwdata`. Its canonical input is a
**Proposal** containing named semantic operations such as `MergeLexicalEntries`, `SplitSense`, or
`CreateAffixProcessRule`. Those operations are lowered into the native changes required by the
target store.

> **Status: this is the target architecture and delivery plan, not the current implementation.**
>
> The repository contains a tested one-operation `lexical/sense/setGloss` control slice and extensive
> LibLCM/HermitCrab coverage research. The PR-like review domain, generated grammar surface, new
> Harmony primitives, LcmCrdt grammar support, authority migration, and text authoring are planned
> work. Nothing in the plans should be read as already shipped.

## The intended workflow

```text
author human or agent
        │
        ▼
immutable Proposal revision ────────┐
        │                            │ exact input binding
        ├── semantic validation      │
        ├── static-analysis checks   │
        ├── Motif Dry Run            │ what would change in LibLCM?
        ├── PanGloss Assessment      │ what happens to parsing?
        └── conformance/security     │
                                     ▼
                          typed Reviews and Decision
                                     │
                              final baseline check
                                     │
                         controlled atomic materialization
                                     │
                                     ▼
                         Receipt or explicit refusal
```

A review or check applies only to the exact Proposal revision, Baseline Token, artifacts, tool
contract, and policy revision it evaluated. Changed inputs make that evidence stale.

“Always resolve” has a narrow meaning: history is retained and converges, and every materialization
receives an explicit `Applied`, `Refused`, or `Deferred` disposition. It does **not** mean every
semantic conflict is automatically applied. When authored meaning or a language-project invariant
cannot be preserved, coordination or deterministic refusal is the correct outcome.

## Responsibilities

| Component | Responsibility |
| --- | --- |
| **Motif** | Semantic operations; Proposal, Check Run, Review, Decision, Dry Run, authorization, rebase, and Receipt contracts |
| **Harmony** | Domain-neutral replicated commit history, opaque preservation, convergence primitives, atomic-group mechanics, and deterministic materialization diagnostics |
| **LcmCrdt** | Generated LibLCM-shaped collaborative state and the target authority for domains promoted to CRDT-native operation |
| **LibLCM / FieldWorks** | Model invariants, project lifecycle, unit of work, persistence, and compatibility validation when materializing `.fwdata` |
| **FwLiteProjectSync / FieldWorks adapter** | LcmCrdt–LibLCM translation, private workspaces, exclusive live apply, save/read-back, and recovery |
| **PanGloss** | Immutable parser Assessments and parser facts; Motif policy decides what evidence is required |

Harmony remains domain-neutral: grammar and LibLCM vocabulary do not move into Harmony core.
LcmCrdt receives generated entities, change classes, registrations, EF configuration, and reviewed
hand-written migrations. Motif owns the Manifest, MiniLcm–LibLCM name/shape crosswalk, generator,
semantic operations, and lowering rules.

## Authority and migration

The target supports domains promoted to CRDT-native authority while preserving a FieldWorks-hosted
transition:

| Mode | Canonical materialized state | FieldWorks role |
| --- | --- | --- |
| CRDT-native domain | LcmCrdt projection | LibLCM validates and persists a compatibility projection |
| FieldWorks-hosted transition | live LibLCM model supplied by its owning host | the host is the sole writer during final compare/apply/save |

One field has exactly one authority in an authority epoch. Chorus and Harmony must never
independently merge the same field. Promotion is an explicit, versioned migration backed by
bidirectional round-trip evidence, not a runtime guess.

## Grammar first

Grammar is the first semantic customer because it exercises the difficult parts of the design:

- roughly 30 grammar Constructs and their cross-references;
- phonological rule order where sequence encodes feeding and bleeding;
- alpha variables whose current LibLCM representation derives identity from position;
- HermitCrab validation and interpretation;
- real-project round trips through LcmCrdt, the FieldWorks bridge, and LibLCM;
- parser evidence through PanGloss Assessments.

A lexical `setGloss` operation is used only as the M4 lifecycle control for baselines, reviews,
Drift, recovery, and Receipts. One grammar Construct then proves the full cross-repository path before
the remaining grammar volume is generated.

Lexical coverage expands afterward from the same Manifest. Text currently supplies immutable
evidence—occurrences, context, and selected analyses. Authorable text is a later bounded context that
must first define durable occurrence identity, Unicode normalization and segmentation coordinates,
standoff annotations, provenance, reanchoring/refusal, lowering, and read-back.

## Cross-repository delivery

The work is coordinated by one milestone ladder:

- [cross-repository plan](docs/plan-cross-repo.md) — milestones and dependency edges;
- [Motif plan](docs/plan-motif.md) — crosswalk, model join, generator, semantic and review domains;
- [Harmony plan](docs/plan-harmony.md) — domain-neutral primitives and diagnostics;
- [LcmCrdt plan](docs/plan-lcmcrdt.md) — generated collaborative model and FieldWorks bridge work;
- [product architecture](docs/plan-product-architecture.md) — normative end-state boundaries;
- [overall product plan](docs/motif-overall-plan.md) — user workflow, evidence corpus, UI, and CLI.

The milestone sequence is:

1. build the MiniLcm–LibLCM name/shape crosswalk and fail-closed model join;
2. prove generation by reproducing already-shipped LcmCrdt entities;
3. add required Harmony sequence, reference, move, payload-binding, and diagnostic primitives;
4. prove the PR-like control plane with `setGloss`;
5. deliver one grammar Construct end to end;
6. generate the remaining grammar surface and prove the ordered residue with real projects.

Full CRDT-only creation of a brand-new `.fwdata` is conditional. Selective bidirectional
compatibility is mandatory for every promoted domain.

## Maintainer review and open decisions

The architecture has been source-checked and compared with current literature, but it deliberately
leaves project-owner and maintainer decisions open.

- [architecture-first grill queue](docs/grill-plan-2026-08-01.md) preserves 107 stable question IDs;
- [evidence ledger](docs/research/2026-08-01-grill-evidence-ledger.md) classifies them as 47 resolved
  principles, 27 bounded evidence tasks, and 33 owner decisions;
- [decision log](docs/grill-decisions.md) records decisions as they are made;
- [research synthesis](docs/research/2026-08-01-pr-like-collaboration-synthesis.md) captures the
  primary-source and cross-repository evidence.

The highest-impact maintainer questions concern:

1. v1 authority and the migration boundary between FieldWorks-hosted and CRDT-native domains;
2. whether one Harmony commit can represent one strict Motif atomic materialization group;
3. policy registration and replay across mixed client/policy versions;
4. semantic move intent and concurrent ordered-grammar behavior;
5. deterministic diagnostic storage, identity, resolution, and headless reporting;
6. payload binding, authorization trust boundaries, fencing, and cross-store recovery;
7. MiniLcm/LibLCM capability mismatches, especially morph-type creation;
8. repository ownership, package rollout, compatibility testing, and sign-off.

The intent is to answer these jointly with the Harmony and LcmCrdt maintainers before implementation
commits bind the wrong abstraction.

## Present implementation

The repository currently contains `SIL.Motif.Contract`, `SIL.Motif.Model`, `SIL.Motif.Runner`,
`SIL.Motif.Host`, `SIL.Motif.Cli`, and tests. The implemented control slice can:

- parse and canonicalize a Proposal containing `lexical/sense/setGloss`;
- compute stable intent and effect digests;
- open a real FieldWorks project through the host;
- perform a mutation-and-rollback Dry Run;
- detect footprint Drift;
- apply in one LibLCM unit of work, read back, persist, and record an applied marker;
- exercise the flow through the `motif` CLI.

This code predates the final Harmony/LcmCrdt architecture and is a tested control/proving surface,
not evidence that the planned product is complete.

Current project targets:

- `SIL.Motif.Contract` and `SIL.Motif.Model`: `netstandard2.0`, LibLCM-free;
- runner, host, CLI, and tests: `net8.0`, pinned to `SIL.LCModel 11.0.0-beta0150`.

Run the tests with:

```powershell
dotnet test Motif.sln
```

## Vocabulary and design constraints

[CONTEXT.md](CONTEXT.md) is the canonical glossary. In particular:

- **Motif operation** — one named unit of semantic intent;
- **Proposal** — the immutable, reviewable unit of operations;
- **Dry Run** — what a Proposal would do to a live LibLCM model;
- **Assessment** — an immutable PanGloss parser run;
- **Drift** — the live project no longer matches the evaluated baseline;
- **Receipt** — the durable record of controlled application;
- **Manifest** — reviewed classification of the LibLCM model surface;
- **Construct** — one staged grammar capability.

Canonical Proposal input is semantic intent, never a low-level LibLCM mutation script. Generated
LibLCM Mutation Plans are output-only. Operation order is authoritative. Unknown operation kinds and
semantic properties fail closed. Diff is exact-identity-based and linguistically unaware. Every
operation family must satisfy the complete schema, semantics, validation, lowering, Dry Run, apply,
read-back, conflict/rebase, snapshot/diff, rollback, round-trip, concurrency, compatibility, and
coverage gate before it is complete.

## Repository

The project was previously named LCAtom. That name is retired. The repository, product, namespaces,
solution, and CLI are now **Motif**:

<https://github.com/johnml1135/motif>

Start with [the product architecture](docs/plan-product-architecture.md), then read
[the cross-repository plan](docs/plan-cross-repo.md) and the owning repository plan relevant to your
review.
