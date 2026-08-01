# Plan amendment — controlled materialization and baseline-bound apply

**Status:** live plan amendment, 2026-08-01.

This document amends [plan-cross-repo.md](plan-cross-repo.md),
[plan-harmony.md](plan-harmony.md), [plan-lcmcrdt.md](plan-lcmcrdt.md), and
[plan-motif.md](plan-motif.md). Where this document conflicts with those four plans, this document
wins until its changes are consolidated into them. It preserves the findings of the 2026-08-01
reviews:

- [MiniLcm ↔ LibLCM terminology audit](research/2026-08-01-minilcm-liblcm-terminology-audit.md)
- [Harmony selective-materialization review](research/2026-08-01-harmony-selective-materialization-review.md)
- [Dry Run baseline and state-control review](research/2026-08-01-dry-run-state-control-review.md)

The review recommendations are not all decisions. The decisions recorded below are the minimum
needed to keep the live plan honest; unresolved choices are routed to
[grill-plan-2026-08-01.md](grill-plan-2026-08-01.md).

## Decisions added to the plan

### A1 — history merge and state materialization are separate

Harmony continues to accept, retain, deduplicate, and synchronize authored history. That does not
require every stored change to affect the current materialized snapshot.

Materialization is policy-driven:

- existing and unregistered change types retain Harmony's permissive behavior by default;
- selected semantic change types may opt into deterministic fail-closed materialization;
- a refused change remains immutable, addressable history;
- its effect is omitted from the materialized model;
- replay derives the same structured diagnostic on every compatible replica;
- resolution is a later explicit authored action and never a silent rewrite of the original.

Harmony owns only the generic mechanism. LcmCrdt registration and Motif's Manifest/lowering select
the policy for domain fields. Harmony gains no LibLCM, MiniLcm, or grammar vocabulary.

### A2 — a Motif Proposal is a strict atomic materialization group

Harmony's generic best-effort replay remains useful for existing FieldWorks Lite synchronization.
It is not the policy for an accepted Motif Proposal.

One complete Proposal is one atomic semantic unit. If any operation in a Proposal cannot be
materialized without inventing intent:

1. Harmony retains the Proposal's commit and every operation identity;
2. none of that Proposal's effects enter the canonical materialized projection;
3. the triggering operation receives a deterministic `Refused` diagnostic;
4. other operations in the group are marked `DeferredByAtomicGroup` rather than reported as valid
   independent successes;
5. a later resolution names the original diagnostic and creates new history.

Commit insertion atomicity, materialization atomicity, LibLCM UOW atomicity, and cross-store
durability are separate guarantees and must be named separately in APIs and tests.

### A3 — strict ordering carries intent, not a scalar result

The current `SetOrderChange<T>(entityId, double order)` remains available for existing permissive
consumers. It is not the operation used for strict phonological order.

A strict ordered operation carries stable entity identity and authored placement anchors, such as
the intended left/right neighbors, plus enough canonical evidence to distinguish an unambiguous
placement from one invalidated by concurrency. Placement anchors are semantic intent, not permission
for rebase to choose a different target or order.

If the authored placement remains unambiguous, the operation applies. If concurrent changes make
multiple materially different placements defensible, materialization refuses. It must not fall back
to last-writer-wins or manufacture an order neither author requested.

This does not reverse D5 in [grill-decisions.md](grill-decisions.md): baseline observations remain in
the Proposal/Dry Run envelope, not inside an unconditional merging property change. The exact line
between a semantic placement anchor and a baseline precondition remains a grill decision and must be
settled before the strict operation schema freezes.

### A4 — the designed world must be the Dry-Run world

An agent designs a Proposal against an immutable private project workspace and a canonical
`BaselineToken`. A Motif Dry Run evaluates that Proposal against a workspace materialized from that
exact token. Its immutable record binds:

- Proposal identity and intent digest;
- baseline token;
- expected semantic effects and effect digest;
- warnings, conflicts, and applicability;
- Motif runner, LibLCM, model, and projection versions.

For v1 the authorization token covers a whole-project normalized semantic snapshot. A scoped
footprint remains useful evidence and may become authorization-grade only after its complete
transitive read/effect closure is proven.

A Harmony head or commit-chain hash may be recorded as provenance. Neither is the sole baseline
token: the current Harmony hash does not bind the complete change payload, and the authoritative
`.fwdata` state can have lifecycle not represented by one Harmony head.

### A5 — agents receive private workspaces, not long live locks

Each agent receives its own validated `.fwdata` copy or equivalent private project materialization,
its own loaded `LcmCache`, and the baseline token from which that workspace was created. Multiple
writable caches must never point at the same `.fwdata` path.

Agents may deliberate, revise, run Dry Runs, and request PanGloss Assessments without holding the
live project's write authority. Workspace creation, retention, disposal, and crash cleanup are
explicit host responsibilities.

### A6 — apply uses a short exclusive capability and final comparison

Application requires a short-lived, project-scoped, holder-bound, one-use `ApplyAuthorization`.
The authoritative host must, while holding exclusive project write authority:

1. validate the authorization and its Proposal/Dry Run/effect bindings;
2. recompute the live whole-project `BaselineToken`;
3. refuse with Drift before mutation if it differs;
4. open one outer LibLCM unit of work;
5. apply all Proposal operations in authored order;
6. read back and verify the actual semantic effects;
7. roll back the whole Proposal on any failure;
8. commit and save while authority remains held;
9. compute the after-token;
10. complete or reconcile the Harmony handoff and Receipt;
11. consume the authorization and release authority.

The guarantee is not “the project was checked recently.” It is: no permitted writer can change the
authoritative project between the final comparison and persistence. If another writer can bypass the
host, the product must refuse to claim atomic confidence.

### A7 — the three diagnostic/evaluation records stay distinct

- A **Motif Dry Run** asks what a Proposal would do to one exact LibLCM baseline.
- A **Harmony materialization diagnostic** explains why retained history did or did not affect a
  projected snapshot.
- A **Lexbox synchronization Dry Run** records writes the CRDT↔`.fwdata` reconciler would issue.
- An **Assessment** is an immutable PanGloss run over frozen grammar and evidence artifacts.

They may reference one another but never share one overloaded schema or name. User-facing copy may
describe a Motif Dry Run as an “impact analysis,” but `Dry Run` remains the canonical contract term.

### A8 — MiniLcm names remain a projection contract

The terminology audit found no evidence that the broad MiniLcm/LibLCM divergence is accidental.
The plan does not request a wholesale breaking rename.

`MOT-1` is strengthened from a generator prerequisite into a versioned name-and-shape crosswalk. Each
mapping must classify:

- exact, renamed, flattened, combined, product-only, or unsupported shape;
- LibLCM and MiniLcm class/field names;
- identity and ownership behavior;
- read/write support and known lossiness;
- normalization and representation conversion;
- canonical JSON/change discriminator and compatibility policy;
- evidence and confidence.

The generator may produce adapters and compatibility reports from this crosswalk. It must not infer
semantic equivalence merely because two public names match.

## Amended milestone ladder

Milestones M0–M3 keep their order. The former M4 and M5 move to M5 and M6 so the controlled-baseline
gate precedes grammar volume.

| Milestone | motif | harmony | LcmCrdt / FwLiteProjectSync | Gate |
| --- | --- | --- | --- | --- |
| **M0** — crosswalk seed | `MOT-1` versioned name/shape crosswalk | — | — | Crosswalk covers M2's three entities and records shape/lossiness, not names alone |
| **M1** — generator and refusal foundation | `MOT-2`, `MOT-3` | expanded `HAR-7` materialization result/diagnostic channel | — | Unmatched keys fail; one bad change cannot poison replay; diagnostics reproduce deterministically |
| **M2** — regenerate shipped behavior | `MOT-4` | — | `CRDT-1` | Existing tests pass unmodified and every generated mapping cites the crosswalk |
| **M3** — convergence and strictness primitives | `MOT-5` | expanded `HAR-3`, plus `HAR-5`, `HAR-6` | `CRDT-2`, `CRDT-3` | Permissive sequences converge; strict ambiguity retains/refuses rather than guesses; no domain names enter Harmony |
| **M4** — reviewed world equals applied world | new `MOT-9` | `HAR-7` strict atomic group behavior | new `CRDT-9` | Two agents start at one baseline: one applies atomically, the other gets Drift before mutation; crash recovery never blindly retries |
| **M5** — one grammar construct end to end | `MOT-6` | — | `CRDT-4`, `CRDT-5` | One construct converges, round-trips through Chorus/`.fwdata`, and passes the M4 authorization gate |
| **M6** — remaining constructs and ordered residue | `MOT-7`, `MOT-8` | — | `CRDT-6`, `CRDT-7` | Ambiguous feeding-order work produces stable refusal; unambiguous order and keyed alpha variables survive real-project round trips |
| **Unscheduled** | — | `HAR-1`, `HAR-2` | `CRDT-8` | Existing triggers remain, but the grill must revisit whether payload binding is now required by approval |

Amended item counts: **MOT 9 · HAR 6 (1, 2, 3, 5, 6, 7) · CRDT 9**.

## Owner-plan amendments

### `plan-harmony.md`

`HAR-7` expands from “deferred diagnostic channel” to **generic materialization result and diagnostic
channel**.

Required contract:

```text
OperationKey = (CommitId, ChangeIndex)
DiagnosticKey = (OperationKey, PolicyKey, PolicyRevision)
Disposition = Applied | Refused | DeferredByDependency | DeferredByAtomicGroup | Resolved
```

Diagnostics are deterministic derived state, idempotently persisted or regenerated. They contain no
local timestamps or random identity. The original change is immutable. A resolution is new authored
history referring to the diagnostic.

`HAR-7` supports both:

- best-effort materialization for current consumers; and
- strict atomic materialization groups for Motif Proposals.

Acceptance additions:

- arrival-order permutations produce identical snapshot and diagnostic sets;
- duplicate delivery is idempotent;
- a strict group with one refused operation materializes none of the group;
- unrelated history still materializes;
- diagnostics are observable after reconnect without requiring UI;
- old clients never interpret an unknown strict change as a permissive scalar update.

`HAR-3` expands from one converging sequence to two explicit behaviors:

- a generic permissive converging sequence for ordinary collaborative ordering; and
- a strict anchor-carrying ordered operation that refuses semantic ambiguity.

The existing scalar `SetOrderChange<T>` remains legacy/permissive. Strict fields never fall back to it.

### `plan-motif.md`

`MOT-1` gains the crosswalk fields listed in A8 and an acceptance test proving that same-name but
different-shape concepts cannot be silently treated as equivalent.

Add `MOT-9` — **baseline-bound Dry Run and atomic apply contract** — at M4. It owns the portable
contracts for:

- `BaselineToken`;
- immutable `DryRunRecord`;
- `ApplyAuthorization`;
- `DriftRefusal`;
- `Receipt` and reconciliation status;
- agent-workspace provenance;
- whole-Proposal materialization policy metadata.

`MOT-9` does not open projects or own project lifecycle. The authoritative host supplies an already
loaded cache or private workspace and enforces the capability.

Acceptance:

- unchanged baseline succeeds;
- changed baseline refuses before mutation;
- Proposal content, operation order, Dry Run, effect digest, project, holder, or expiry mismatch
  invalidates authorization;
- one failure rolls back the complete Proposal;
- before/after tokens and read-back effects bind the Receipt;
- Motif Dry Run, Harmony diagnostic, Lexbox sync Dry Run, and PanGloss Assessment cannot be
  accidentally deserialized as one another.

Former M4 `MOT-6` moves to M5. Former M5 `MOT-7` and `MOT-8` move to M6.

### `plan-lcmcrdt.md`

Add `CRDT-9` — **private agent workspace and live apply authority** — at M4. The implementation lands
primarily in `FwLiteProjectSync` and the project host, not Harmony core.

Responsibilities:

- materialize a private `.fwdata`/`LcmCache` workspace from a named baseline;
- prove that the workspace corresponds to the returned token;
- prevent two writable caches from sharing one project path;
- dispose and clean up abandoned workspaces under an explicit retention policy;
- acquire short exclusive live-project write authority;
- recompute the token inside that authority;
- hold authority through UOW apply, read-back, save, and Receipt handoff;
- expose typed Drift and recovery results;
- recover from crashes without blind replay.

Acceptance:

- two agents may deliberate concurrently on isolated copies;
- the first accepted Proposal changes the live token;
- the second stale authorization refuses before mutation;
- lock/lease expiry and process death are recoverable;
- no supported writer can bypass the final comparison critical section;
- `.fwdata`, Harmony, and Receipt partial durability is classified and reconciled explicitly.

Former M4 `CRDT-4`/`CRDT-5` move to M5. Former M5 `CRDT-6`/`CRDT-7` move to M6. `CRDT-8` remains
unscheduled and distinct: full CRDT→`.fwdata` creation is not the same as creating a private copy from
an existing authoritative project.

## New dependency edges

| Edge | Reason |
| --- | --- |
| `MOT-1` → `MOT-9` | Baseline snapshots cannot be canonical without a reviewed name/shape contract |
| `CRDT-1` → `MOT-9` | Bind authorization only after the generated mechanical path is proven |
| `HAR-7` → `CRDT-9` | Strict refusal and group disposition must exist before the live host relies on them |
| `MOT-9` ↔ `CRDT-9` | Portable token/capability contract and lifecycle enforcement are two halves of one gate |
| `MOT-9`, `CRDT-9` → `MOT-6`, `CRDT-4`, `CRDT-5` | The first grammar construct must prove the reviewed world is the applied world |
| `HAR-3`, `HAR-7` → `MOT-8` | Ordered proof needs both strict placement and stable refusal |

## Durability boundary

LibLCM UOW commit, `.fwdata` save, Harmony commit persistence, and Receipt persistence are not one
distributed transaction. The implementation must use an explicit recoverable state machine. The
exact names remain open, but it must distinguish at least:

```text
Prepared
AppliedToLiveModel
Saved
RecordedInHarmony
ReceiptComplete
NeedsReconciliation
```

Recovery compares applied markers, before/after semantic tokens, intended effects, and Harmony
operation identity. It completes missing bookkeeping when provable and never repeats an uncertain
mutation blindly.

## Documentation vocabulary

Use these qualified phrases in APIs and prose:

- “Motif Dry Run” when ambiguity with synchronization is possible;
- “Harmony materialization diagnostic” rather than Dry Run;
- “Lexbox synchronization Dry Run” for `DryRunMiniLcmApi` output;
- “PanGloss Assessment” when ambiguity with informal evaluation is possible;
- “private agent workspace” rather than branch or worktree for copied `.fwdata`;
- “exclusive apply authority” or “apply capability,” not Rust ownership or permission, in the
  normative contract.

## Deliberately unresolved

This amendment does not settle:

- the exact strict sequence algorithm;
- whether policy is registered by change discriminator, entity/field path, schema metadata, or a
  combination;
- the exact boundary between a semantic placement anchor and a forbidden baseline precondition;
- whether strict group identity is exactly one Harmony commit or an explicit group envelope;
- whether approval requires payload-bound cryptographic hashing;
- which process is the unique live apply authority in every deployment;
- lock versus TTL lease mechanics;
- workspace retention and evidence privacy;
- the Receipt recovery state names and storage owner;
- when a scoped baseline token becomes sound enough to replace whole-project hashing;
- whether the grammar API adopts LibLCM-aligned names or retains MiniLcm product vocabulary.

These decisions are queued, in dependency order, in
[grill-plan-2026-08-01.md](grill-plan-2026-08-01.md).
