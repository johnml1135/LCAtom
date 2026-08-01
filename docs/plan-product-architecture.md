# Product architecture — semantic collaboration for language projects

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
| merge queue | serialized final comparison and apply queue |
| merge result | Receipt plus after-token |

Motif does not clone Git branches, textual diffs, or merge commits. It borrows exact input binding,
stale-check invalidation, required review, serialized landing, and auditable outcomes.

## Target architecture

Harmony and LcmCrdt are the modern collaborative successor to monolithic `.fwdata` state:

- **Harmony** owns domain-neutral replicated commit history, opaque preservation, group mechanics,
  convergence primitives, and deterministic materialization diagnostics.
- **LcmCrdt** owns the generated LibLCM-shaped collaborative state and is the target authority for
  domains promoted to CRDT-native operation.
- **Motif** owns semantic operations and the Proposal, Check Run, Review, Decision, Dry Run,
  authorization, rebase, and Receipt contracts used by humans and agents.
- **LibLCM/FieldWorks** owns model invariants, project lifecycle, unit of work, persistence, and
  compatibility validation whenever a FieldWorks project is materialized.
- **FwLiteProjectSync / the FieldWorks adapter** owns private workspaces, translation between
  LcmCrdt and LibLCM, exclusive live apply, save/read-back, and recovery.
- **PanGloss** owns immutable Assessments and parser facts; Motif policy decides what evidence is
  required and never turns one Assessment into a verdict.

This target is earned incrementally. “Modern reincarnation of fwdata” is a product destination, not
a claim that current LcmCrdt already covers the full LibLCM model.

## Authority matrix

Every Baseline Token, Dry Run, Check Run, Decision, Apply Authorization, Receipt, and diagnostic
names an `authorityKind`, authority epoch, project identity, schema/model versions, and projection
version.

| Mode | Canonical materialized state | FieldWorks role |
| --- | --- | --- |
| CRDT-native domain | LcmCrdt projection | LibLCM validates and persists a compatibility projection |
| FieldWorks-hosted transition | live LibLCM model supplied by its owning host | the host is the sole writer during final compare/apply/save |

One field is governed by exactly one authority in an epoch. Chorus and Harmony must never
independently merge the same field. Authority changes are explicit migrations with round-trip
evidence, never runtime guesswork.

## Three independent state machines

1. **History:** accepted, replicated, opaque/known. History is never deleted merely because current
   software cannot materialize it.
2. **Materialization:** materialized, refused, deferred by dependency, deferred by atomic group,
   resolved by later authored history.
3. **Proposal workflow:** Draft → Submitted → Checks Pending → Ready for Review → Changes Requested
   / Rejected / Approved → Queued → Final Comparison → Drift Refused / Applying → Receipt Complete
   / Needs Reconciliation.

A Proposal has a stable identity and immutable revisions. Changing intent creates a new revision and
invalidates Check Runs, Reviews, Decisions, Dry Runs, and authorizations bound to the former digest.

“Always resolve” means history is always retained and converges, and every materialization receives
an explicit `Applied`, `Refused`, or `Deferred` disposition. It does not mean every semantic conflict
auto-materializes. Where authored meaning or a domain invariant cannot be preserved, coordination or
deterministic refusal is the correct resolved outcome.

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

Proposal atomicity is a Motif materialization guarantee. It is distinct from Harmony commit insertion,
LibLCM UOW atomicity, and cross-store durability. M4 must prove whether one Harmony commit supplies
group identity, all-or-none materialization, old-client opacity, and payload/provenance binding; if
not, Motif defines an immutable group envelope before grammar volume.

## Controlled apply

Agents work on private, baseline-bound project copies and never hold the live project during
deliberation. Final apply:

1. acquires a short exclusive capability with fencing/version validation;
2. verifies the exact Proposal, evidence, policy, and payload digests;
3. recomputes the Baseline Token while authority is held;
4. refuses Drift before mutation;
5. applies authored operation order in one outer LibLCM unit of work;
6. reads back, saves, computes the after-token, and records a Receipt;
7. enters `NeedsReconciliation` after ambiguous cross-store failure and never blindly retries.

The LibLCM UOW, file save, Harmony persistence, and Receipt store are separate durability boundaries,
not a distributed transaction.

## Domain order

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

- [Controlled-materialization amendment](plan-amendment-2026-08-01-controlled-materialization.md)
- [Review synthesis](research/2026-08-01-pr-like-collaboration-synthesis.md)
- [Grill queue](grill-plan-2026-08-01.md)
- [I-confluence](https://www.vldb.org/pvldb/vol8/p185-bailis.pdf)
- [Local-first software](https://doi.org/10.1145/3359591.3359737)
- [W3C Web Annotation](https://www.w3.org/TR/annotation-model/)
- [SLSA provenance](https://slsa.dev/spec/v1.2/provenance)
