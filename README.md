# Motif

**A PR-like collaboration system for semantic changes to language data.**

Motif lets humans and AI agents propose, inspect, check, discuss, approve, apply, and audit changes to
lexical and grammar data in a FieldWorks project. Grammar is the first product customer.

The model is Git/GitHub-like: exact candidate revisions, semantic diffs, CI-style checks, typed
review, approvals, stale-input detection, controlled landing, and auditable outcomes. A Proposal is
reviewed before it lands, not merged after the fact.

Motif does not put Git commits or textual patches around `.fwdata`. Its canonical input is a
**Proposal** containing named semantic operations such as `MergeLexicalEntries`, `SplitSense`, or
`CreateAffixProcessRule`. Those operations are lowered into LibLCM mutations and applied through one
unit of work.

> **Status: this is the target architecture and delivery plan, not the current implementation.**
>
> The repository contains a tested one-operation `lexical/sense/setGloss` control slice and extensive
> LibLCM/HermitCrab coverage research. The PR-like review domain, the generated grammar surface, the
> FieldWorks in-process adapter, and receipt sync are planned work. Nothing in the plans should be
> read as already shipped.

**Start with [Plan A](docs/plan-motif.md).** It is the live plan and owns both the milestones and the
work items.

## Delivery

**Motif delivers exactly two things: the `motif` CLI, and a FieldWorks integration.**

| | |
| --- | --- |
| `motif` CLI | `net10.0` executable. Batch, automation, and AI-agent use, against a `.fwdata` project it opens itself |
| FieldWorks integration | `netstandard2.0` Runner hosted in-process, behind FieldWorks-owned Avalonia surfaces |

Everything else is a dependency rather than a deliverable — the Lexbox receipt store is server work in
another repository, PanGloss is a subprocess or native library, and `SIL.Motif.Contract` is a
published contract other runners consume. There is no Motif web app, service, mobile surface, or
FwLite presence.

## Scope

**v1 is lexical and grammar. Text and analysis are staged, not excluded**
([ADR 0017](docs/adr/0017-text-and-analysis-destination-scope.md)). Today the Manifest classifies
`Segment`, `WfiAnalysis`, `WfiWordform`, `Text`, and `CmAgent` as `out` / `not-domain-reachable`,
leaving eight text-adjacent rows in scope, and text supplies immutable *evidence* — occurrences,
context, and selected analyses.

They are in the destination because **coverage gaps are the feeding ground for new and refined
rules**: the words no rule explains yet are the work queue, not a score. What defers them is not
appetite but identity — a manual analysis is two facts, and while *this analysis is human-approved*
has a durable GUID, *this occurrence uses it* has no durable identity anywhere in the model. Authorable
text still needs an occurrence-anchor contract, Unicode normalization and segmentation coordinates,
standoff annotations, provenance, reanchoring and refusal, lowering, and read-back. **Text import is
separable and much cheaper** — `Text`, `StText`, `StTxtPara`, and `Segment` are ordinary GUID-bearing
objects that fit the contract today.

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
                         controlled atomic application
                                     │
                                     ▼
                         Receipt or explicit refusal
```

A review or check applies only to the exact Proposal revision, Baseline Token, artifacts, tool
contract, and policy revision it evaluated. Changed inputs make that evidence stale.

Every application receives an explicit `Applied`, `Refused`, or `Deferred` disposition. Nothing is
applied silently, and a Proposal that cannot preserve authored meaning or a language-project invariant
is refused deterministically rather than merged optimistically.

## Responsibilities

| Component | Responsibility |
| --- | --- |
| **Motif** | Semantic operations; Proposal, Check Run, Review, Decision, Dry Run, authorization, rebase, and Receipt contracts |
| **LibLCM / FieldWorks** | Model invariants, project lifecycle, unit of work, persistence, and compatibility validation. **The only authority on Motif's path** |
| **FieldWorks adapter** | Hosts the `netstandard2.0` Runner in-process; UI-thread marshalling, one undoable unit of work, save, read-back, and recovery |
| **Lexbox** | Proposal and Receipt object store, optional per project |
| **PanGloss** | Immutable parser Assessments and parser facts; Motif policy decides what evidence is required |
| **Harmony / LcmCrdt** | FwLite's substrate for offline, multi-device, mobile lexical work. **Not on Motif's path** — see the [adoption report](docs/harmony-adoption-report.md) |

Motif owns the Manifest, the generator, the semantic operations, and the lowering rules. Its
operations target **LibLCM objects directly**, so no MiniLcm crosswalk is required and no generated
code lands in LcmCrdt.

## Authority

**The live LibLCM model is the only authority on Motif's path.** The process owning the loaded
`LcmCache` is the sole writer; Chorus merges between people, as it does today. There is no second
merge engine, no CRDT, and no promotion of domains to a CRDT authority.

FwLite keeps its own authority over its own lexical data through Harmony. The two products do not
share a substrate, and one field never has two authorities. Why that split rather than one system is
argued in full in the [adoption report](docs/harmony-adoption-report.md).

Dry runs never mutate the live model. They run against a scratch copy of the loaded cache
([ADR 0016](docs/adr/0016-scratch-cache-copy-not-undo.md)), because neither `Rollback` nor `Undo` is
safe to build on — `Rollback` skips the forward-only setter hooks `Undo` runs, and LibLCM has
genuinely non-undoable units of work.

## Grammar first

Grammar is the first semantic customer because it exercises the difficult parts of the design:

- roughly 30 grammar Constructs and their cross-references;
- phonological rule order where sequence encodes feeding and bleeding;
- alpha variables whose current LibLCM representation derives identity from position, with a hard
  24-per-rule ceiling that throws and kills the whole grammar load;
- HermitCrab validation and interpretation;
- real-project round trips through the FieldWorks adapter and LibLCM;
- parser evidence through PanGloss Assessments.

A lexical `setGloss` operation is used only as the lifecycle control for baselines, reviews, Drift,
recovery, and Receipts. One grammar Construct then proves the full path before the remaining grammar
volume is generated. Lexical coverage expands afterward from the same Manifest.

## Delivery plan

- **[Plan A](docs/plan-motif.md)** — the live plan: milestones, model join, generator, scratch-cache
  dry run, FieldWorks adapter, review domain;
- [work in other repositories](docs/plan-cross-repo.md) — FieldWorks, PanGloss, liblcm, lexbox;
- [why not Harmony](docs/harmony-adoption-report.md) — the two alternate proposals and the decision;
- [withdrawn LcmCrdt plan](docs/plan-lcmcrdt.md) — what the previous routing required;
- [product architecture](docs/plan-product-architecture.md) — normative end-state boundaries;
- [overall product plan](docs/motif-overall-plan.md) — user workflow, evidence corpus, UI, and CLI.

**Two scopes** ([ADR 0020](docs/adr/0020-cli-first-fieldworks-planned-not-built.md)). Scope 1
establishes the LibLCM seams and proves them through the CLI, with an AI agent as the author. Scope 2 is
the FieldWorks integration — planned in full, and deliberately not built until scope 1 works.

Milestone ids are stable; the order is `M1 → M2 → M4 → M5 → M6`, then scope 2.

| | | Scope |
| --- | --- | --- |
| **M1** | fail-closed model join, and a generator that reads it without a liblcm checkout | 1 |
| **M2** | one generated operation family applied end to end from the CLI, with a scratch-cache dry run | 1 |
| **M4** | a Proposal authored by an agent, reviewed, approved, applied, with a durable Receipt | 1 |
| **M5** | one grammar Construct authored, applied, and parsed by PanGloss | 1 |
| **M6** | the remaining grammar surface, and the ordered residue proven on real projects | 1 |
| **M3** | FieldWorks hosts Dry Run and Apply in-process, on `net48` | **2** |
| **M4b** | Receipts shared between people, through Lexbox | **2** |

M1 and M2 are mechanical. **M4 is the product**, and it is AI-facing first — the agent is the first
author, not the last. M5 is the first thing a linguist would recognise as the point.

Scope 2 is planned now so scope 1 cannot make it more expensive: `netstandard2.0` on
Contract/Model/Runner, one JSON stack everywhere, a Runner that never owns a cache, and an apply that
never calls `Save` are build-time invariants throughout, not later concerns.

## Open decisions

The architecture has been source-checked and compared with current literature, but it deliberately
leaves project-owner decisions open. **[The Plan A grill queue](docs/grill-plan-a.md) is the live
list.** It leads with four measurements, because later answers depend on them — most importantly what
`LcmCache.CreateCacheCopy` actually costs, an API with zero callers anywhere in liblcm or FieldWorks.

Beyond those, the questions that most affect the design are whether roughly 300 heuristically
classified Manifest rows need a dedicated audit before the generator reads them, whether a reviewer
can actually see that a phonological reorder changed the grammar's meaning, whether review state must
work offline, and who owns keeping Motif's and FwLite's change vocabularies aligned.

Supporting records: the [decision log](docs/grill-decisions.md), the
[research synthesis](docs/research/2026-08-01-pr-like-collaboration-synthesis.md), and the
[evidence ledger](docs/research/2026-08-01-grill-evidence-ledger.md) — the last two predate Plan A and
are evidence rather than plans.

## Present implementation

The repository currently contains `SIL.Motif.Contract`, `SIL.Motif.Model`, `SIL.Motif.Runner`,
`SIL.Motif.Host`, `SIL.Motif.Cli`, and tests. The implemented control slice can:

- parse and canonicalize a Proposal containing `lexical/sense/setGloss`;
- compute stable intent and effect digests;
- open a real FieldWorks project through the host;
- perform a Dry Run (today by mutation-and-rollback; ADR 0016 replaces this with a scratch copy);
- detect footprint Drift and refuse an unbound apply;
- apply in one LibLCM unit of work, read back, persist, and record an applied marker;
- exercise the whole flow through the `motif` CLI — `open`, `new`, `add-set-gloss`, `finalize`,
  `list`, `show`, `dry-run`, `apply`, `log`.

This is a tested control and proving surface for one operation kind, not evidence that the planned
product is complete.

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

Canonical Proposal input is semantic intent, never a low-level LibLCM mutation script. The **intent
contract is public and versioned**; generated LibLCM Mutation Plans are **private and output-only**,
which is why drift is compared over effects and never over the plan. Operation order is
authoritative. Unknown operation kinds and semantic properties fail closed. Diff is
exact-identity-based and linguistically unaware. Every operation family must satisfy the complete
schema, semantics, validation, lowering, Dry Run, apply, read-back, conflict/rebase, snapshot/diff,
rollback, round-trip, concurrency, compatibility, and coverage gate before it is complete.

The contract is not private to us: `SIL.Motif.Contract` is deliberately LibLCM-free because non-.NET
runners consume it, and [ADR 0007](docs/adr/0007-cross-language-digest-determinism.md) exists so
digests are reproducible across languages.

## Repository

The project was previously named LCAtom. That name is retired. The repository, product, namespaces,
solution, and CLI are now **Motif**:

<https://github.com/johnml1135/motif>
