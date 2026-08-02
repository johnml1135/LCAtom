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
> LibLCM/HermitCrab coverage research. The PR-like review domain, the generated grammar surface, the
> FieldWorks in-process adapter, receipt sync, and text authoring are planned work. Nothing in the
> plans should be read as already shipped.

## Delivery

**Motif delivers exactly two things: the `motif` CLI, and a FieldWorks integration.** The CLI is a
`net10.0` executable for batch, automation, and AI-agent use; the FieldWorks integration is a
`netstandard2.0` runner hosted in-process behind FieldWorks-owned Avalonia surfaces.

Everything else is a dependency rather than a deliverable — the Lexbox receipt store is server work in
another repository, PanGloss is a subprocess or native library, and `SIL.Motif.Contract` is a
published contract other runners consume. There is no Motif web app, service, mobile surface, or
FwLite presence.

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
| **Harmony / LcmCrdt** | FwLite's substrate for offline, multi-device, mobile lexical work. **Not on Motif's path** — see the [adoption report](docs/harmony-adoption-report.md) |
| **LibLCM / FieldWorks** | Model invariants, project lifecycle, unit of work, persistence, and compatibility validation when materializing `.fwdata` |
| **FieldWorks adapter** | Hosts the `netstandard2.0` Runner in-process; UI-thread marshalling, one undoable unit of work, save, read-back, and recovery |
| **Lexbox** | Proposal and Receipt object store, optional per project |
| **PanGloss** | Immutable parser Assessments and parser facts; Motif policy decides what evidence is required |

Motif owns the Manifest, the generator, the semantic operations, and the lowering rules. Its
operations target **LibLCM objects directly**, so no MiniLcm crosswalk is required and no generated
code lands in LcmCrdt.

## Authority and migration

The target supports domains promoted to CRDT-native authority while preserving a FieldWorks-hosted
transition:

**The live LibLCM model is the only authority on Motif's path.** The process owning the loaded
`LcmCache` is the sole writer; Chorus merges between people, as it does today. There is no second
merge engine and no promotion of domains to a CRDT authority.

FwLite keeps its own authority over its own lexical data through Harmony. The two products do not
share a substrate, and one field never has two authorities.

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

- **[Plan A](docs/plan-motif.md) — the live plan: milestones, model join, generator, scratch-cache dry run, FieldWorks adapter, review domain;**
- [work in other repositories](docs/plan-cross-repo.md) — FieldWorks, PanGloss, liblcm, lexbox;
- [why not Harmony](docs/harmony-adoption-report.md) — the two alternate proposals and the decision;
- [withdrawn LcmCrdt plan](docs/plan-lcmcrdt.md) — what the previous routing required;
- [product architecture](docs/plan-product-architecture.md) — normative end-state boundaries;
- [overall product plan](docs/motif-overall-plan.md) — user workflow, evidence corpus, UI, and CLI.

The milestone sequence is:

1. **M1** — fail-closed model join and a generator that reads it without a liblcm checkout;
2. **M2** — one generated operation family applied end to end, with a scratch-cache dry run;
3. **M3** — FieldWorks hosts Dry Run and Apply in-process, on `net48`;
4. **M4** — a Proposal reviewed, approved, applied, and its Receipt shared through Lexbox;
5. **M5** — one grammar Construct authored, applied, and parsed by PanGloss;
6. **M6** — the remaining grammar surface, and the ordered residue proven on real projects.

## Maintainer review and open decisions

The architecture has been source-checked and compared with current literature, but it deliberately
leaves project-owner and maintainer decisions open.

- [Plan A grill queue](docs/grill-plan-a.md) carries the open questions;
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

Current project targets — two runtimes only, `net10.0` and `net48`, with `netstandard2.0` where an
assembly must load in both:

- `SIL.Motif.Contract` and `SIL.Motif.Model`: `netstandard2.0`, LibLCM-free;
- `SIL.Motif.Runner`: `netstandard2.0;net10.0`, because it runs in-process in whichever host owns the
  live `LcmCache` — FieldWorks while FieldWorks is `net48`, the `net10.0` host afterwards;
- host, CLI, and tests: `net10.0`.

All LibLCM-dependent projects pin `SIL.LCModel 11.0.0-beta0150`. See
[AGENTS.md](AGENTS.md#compatibility-targets) for the full table and rationale.

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
