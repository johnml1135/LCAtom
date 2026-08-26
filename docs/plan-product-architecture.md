# Product architecture — semantic collaboration for language projects


> **Current Motif architecture.** Motif targets LibLCM objects directly; Harmony, Chorus, LcmCrdt, and
> replication are outside this product boundary. [ADR 0040](adr/0040-one-api-the-cli.md) defines the process
> boundary: there is one API and it is the CLI, a job runner takes work that outlives a command, and no Motif
> assembly loads inside FieldWorks. [ADR 0039](adr/0039-one-worker-baseline-and-live-host-authority.md)
> still defines the evaluation model — a saved Baseline supports reusable Dry Runs and Apply is immediate in
> the live host — but its named-pipe protocol is withdrawn.

*This is the product-level architecture served by the cross-repository milestone ladder. It
consolidates the controlled-materialization amendment and the 2026-08-01 literature reviews.*

## Goal

Build a PR-like system that humans and AI agents can use to propose, inspect, check, discuss,
approve, apply, and audit semantic changes to lexical, text, and grammar data. Grammar is the first
product customer. A lexical `setGloss` slice may prove lifecycle mechanics, but it does not change
the delivery priority.

“PR-like” names the workflow, not Git storage:

| PR mechanism | Motif equivalent |
| --- | --- |
| candidate revision | immutable Proposal revision |
| base revision | Baseline Token |
| diff | semantic before/after effects |
| CI | typed Check Runs bound to exact inputs |
| review | typed human or AI Review |
| branch protection | versioned project/Construct policy |
| merge queue | bounded live-project gate and immediate final Preflight |
| merge result | Receipt plus after-token |

Motif does not clone Git branches, textual diffs, or merge commits. It borrows exact input binding,
stale-check invalidation, required review, serialized landing, and auditable outcomes.

## Target architecture

One live project has one writer, while Motif keeps review workflow and reusable evaluation evidence beside it.

- **Motif** owns semantic operations and the Proposal, Check Run, Review, Decision, Dry Run, authorization,
  rebase, Conflict, and Receipt contracts used by humans and agents.
- **Motif job runner** owns durable jobs, Baselines, queues, PanGloss orchestration, archive, and
  reconciliation. It claims work from the paired SQLite store and answers no requests. It never owns a
  FieldWorks process's live cache.
- **LibLCM/FieldWorks or the CLI Host** owns model invariants, live project lifecycle, lock, unit of work,
  persistence, and read-back while it hosts the project.
- **FieldWorks surface** renders `motif --json` and causes changes by running verbs. It references
  `SIL.Motif.Contract` for response shapes and nothing else of Motif's, owns no SQLite, and never opens the
  paired database — not even to read ([ADR 0040](adr/0040-one-api-the-cli.md) decision 1). To hand over a
  live project it saves, releases, runs the verb, and reloads.
- **PanGloss** owns immutable Assessments and parser facts; Motif policy decides what evidence is required and
  never turns one Assessment into a linguistic verdict.

## Authority matrix

Every Baseline Token, Dry Run, Check Run, Decision, Apply Authorization, Receipt, and diagnostic binds the
project identity and the versioned evidence its own contract requires. The earlier universal
`authorityKind`/epoch envelope belonged to the superseded Harmony substrate; Motif's accepted Baseline Token
shape is defined by ADR 0039.

| State | Authority | Other components' role |
| --- | --- | --- |
| Language-project data | Live `LcmCache` supplied by its owning host | Worker holds only a saved Baseline and observed evidence |
| Proposal workflow and evidence | Sibling `Project.motif.db`, shared by the CLI and the job runner, which ship together at one version | Live host reports Apply and reconciliation facts |

The live host is the only writer of language-project fields. Motif's sibling database stores workflow and
evidence, not a competing materialization of those fields.

## Three independent state machines

Users need history, execution, and review state to remain distinguishable so one failure never masquerades as
another.

1. **Proposal workflow:** Draft → Submitted → Checks Pending → Ready for Review → Changes Requested
   / Rejected / Approved → Final Preflight → Drift Refused / Applying → Receipt Complete
   / Needs Reconciliation. Apply is immediate and never waits in a durable queue.
2. **Long-running job:** Queued → Waiting for Baseline or Host → Running → Completed / Failed / Cancelled /
   Interrupted. An explicit retry creates a new attempt.
3. **Apply attempt:** Authorization Issued → Mutation Started → Runner Completed in Cache → Save Started →
   Saved → Receipt Recorded, with Refused or Needs Reconciliation exits at the defined boundaries.

A Proposal has a stable identity and immutable revisions. Changing intent creates a new revision and
invalidates Check Runs, Reviews, Decisions, Dry Runs, and authorizations bound to the former digest.

Every Apply receives an immediate Receipt, refusal, or Needs Reconciliation result. Where authored meaning or
a domain invariant cannot be preserved, deterministic refusal is the correct outcome; an ambiguous
persistence boundary enters Needs Reconciliation and never causes automatic re-Apply.

## Checks, reviews, and policy

Check Runs are immutable facts bound to:

`Proposal revision + Baseline Token + relevant artifact digests + policy revision + tool contract`.

Initial kinds are schema/capability validation, static analysis, Dry Run, PanGloss Assessment correlation,
coverage/conformance, privacy/security, policy, and artifact verification. Changed input makes a
check stale.

Reviews are typed by actor and role. AI review is advisory by default, may recommend or abstain, can
never impersonate a linguist or native speaker, and cannot mint live authority. Any autonomous apply
policy must be explicit, operation-family scoped, versioned, independently checked, provenance-bound,
least-privileged, expiring, and auditable.

Semantic owner routing is keyed by Construct, operation family, lexical/text domain, evidence type,
and sensitivity—not file paths. Routing identifies expertise; policy determines required approvals.

## Convergence and strict semantics

History acceptance is convergent. Materialization is deterministic, policy-versioned, and may fail
closed. CRDT convergence alone does not prove LibLCM validity or linguistic correctness.

Permissive collections use convergent primitives. Strict ordered grammar carries stable target
identity and semantic placement intent; ambiguity is retained and refused, never resolved through a
scalar last-writer-wins fallback. One strict Proposal is one atomic materialization group.

Proposal atomicity is a Motif guarantee implemented by one outer LibLCM UOW. It is distinct from the later
project-file save and worker Receipt write, which reconciliation joins after an interruption. Every finalized
Proposal already supplies immutable group identity and payload/provenance binding; no separate replicated
group envelope is on Motif's delivery path.

## Controlled apply

Agents work on private, baseline-bound project copies and never hold the live project during
deliberation. Final apply:

1. acquires a short exclusive capability with fencing/version validation;
2. verifies the exact Proposal, evidence, policy, and payload digests;
3. compares the live footprint and engine versions with the exact approved Dry Run and Baseline evidence;
4. refuses Drift before mutation without replacing the saved Baseline;
5. applies operations in declared dependency order in one outer LibLCM unit of work;
6. reads back, saves, computes the after-token, and records a Receipt;
7. enters `NeedsReconciliation` after ambiguous cross-store failure and never blindly retries.

The LibLCM UOW, project-file save, and worker Receipt store are separate durability boundaries, not a
distributed transaction.

## Domain order

The product expands one proven operation family at a time, with grammar first and text authoring deliberately
later.

1. A lexical `setGloss` control slice proves Proposal revision, checks, Drift, rollback, save, and
   recovery.
2. Grammar is the first product operation family and proves the complete cross-repository path.
3. Remaining grammar Constructs follow only after the strict-order residue passes real-project
   conformance.
4. Lexical coverage expands from the manifest.
5. Text/annotation authoring follows a separate bounded-context design.

Text is not delivered by the current grammar plan. Today it is evidence: frozen occurrences,
selected analyses, and anchors used by Assessments and Reviews. Authorable text requires durable
occurrence identity, a declared Unicode/segmentation coordinate system, standoff/layered
annotations, re-anchoring/refusal rules, provenance, lowering, read-back, and manifest coverage.
Ambiguous anchors must refuse rather than silently retarget.

## Common operation-family exit gate

No operation family is complete until it has the full gate in `AGENTS.md`, plus concurrency and
mixed-version proof: closed schema; prose semantics; validation; lowering; Dry Run effects; atomic
apply/read-back; conflict/rebase behavior; snapshot/diff; positive, negative, rollback, round-trip,
concurrency, old-client, and conformance fixtures; and manifest coverage evidence.

Generated output and CRDT convergence are necessary, never sufficient.

Generated artifacts carry reproducible provenance binding the LibLCM model, manifest, name/shape
crosswalk, generator, dependency lock, build environment, and generated-output digests.

## Evidence behind this consolidation

These sources explain the design pressures behind the accepted direct-LibLCM architecture; they do not
override its ADRs or current plan.

- [Plan A](plan-motif.md) — the controlled-materialization amendment was folded into `MOT-9` and
  [ADR 0016](adr/0016-scratch-cache-copy-not-undo.md), then deleted
- [Review synthesis](research/2026-08-01-pr-like-collaboration-synthesis.md)
- [Grill queue](grill-plan-a.md)
- [I-confluence](https://www.vldb.org/pvldb/vol8/p185-bailis.pdf)
- [Local-first software](https://doi.org/10.1145/3359591.3359737)
- [W3C Web Annotation](https://www.w3.org/TR/annotation-model/)
- [SLSA provenance](https://slsa.dev/spec/v1.2/provenance)
