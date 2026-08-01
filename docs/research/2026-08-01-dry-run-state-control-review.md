# Dry Run baseline and state-control review

*Preserved research from an xhigh Luna read-only review on 2026-08-01. This is evidence and recommendation, not a decision record. The live plans and ADRs remain authoritative.*

## Recommendation

Use a short-lived, exclusive apply capability combined with optimistic concurrency over a canonical `BaselineToken`.

The agent should deliberate against an immutable project snapshot or private `.fwdata`/`LcmCache` clone. It should not hold a live-project lock for minutes. At apply time, the authoritative host must:

1. acquire exclusive write authority;
2. recompute the baseline token;
3. refuse if it differs from the Dry Run token;
4. execute the entire Proposal in one outer LibLCM unit of work;
5. save and read back under the same authority;
6. emit a Receipt or a recoverable reconciliation status.

A footprint digest alone is insufficient: the current Motif runner checks it before opening its unit of work, so another writer can change the project after the check. The missing piece is enforced write authority around the final check and apply.

## Important vocabulary distinction

Current ADR 0015 terminology is correct:

- `Proposal`: canonical semantic intent.
- `Dry Run`: LibLCM-side evaluation of a Proposal without intended persistence.
- `Assessment`: immutable PanGloss parser run.
- `Receipt`: record of a Proposal applied to a project.

Harmony diagnostics and LexBox synchronization Dry Runs are different concepts.

## What exists today

The current retired Motif walking skeleton already demonstrates part of the desired behavior:

- [`Proposal.cs`](C:/Users/johnm/Documents/repos/motif/src/SIL.Motif.Contract/Model/Proposal.cs) contains semantic operations with authoritative array order.
- [`ProposalDryRunner`](C:/Users/johnm/Documents/repos/motif/src/SIL.Motif.Runner/DryRun/ProposalDryRunner.cs) applies supported operations inside a LibLCM unit of work, captures read-back effects, and rolls back.
- [`BoundDryRunAnchor`](C:/Users/johnm/Documents/repos/motif/src/SIL.Motif.Model/DryRun/BoundDryRunAnchor.cs) records a footprint digest, effect digest, runner version, LibLCM version, and projection version.
- [`ProposalApplier`](C:/Users/johnm/Documents/repos/motif/src/SIL.Motif.Runner/Apply/ProposalApplier.cs) requires a prior Dry Run, checks the current footprint, applies the Proposal in one outer UOW, and writes an idempotency entry.
- [`FootprintProbe`](C:/Users/johnm/Documents/repos/motif/src/SIL.Motif.Runner/Apply/FootprintProbe.cs) recomputes only the fields touched by the Proposal.

This is useful proof-of-concept behavior, but it is not yet the cross-repository apply protocol. The current implementation:

- has no canonical project-wide `BaselineToken`;
- checks the footprint before mutation but does not hold an enforced exclusive lease across check and apply;
- depends on the caller already having exclusive write access;
- saves outside `ProposalApplier`, creating a LibLCM/Harmony durability boundary;
- only supports one operation family;
- relies on mutation-then-rollback, which LibLCM documents as unsafe for some derived caches.

The current live plans, especially ADR 0013 and [`plan-motif.md`](C:/Users/johnm/Documents/repos/motif/docs/plan-motif.md), treat the old runner as superseded by Harmonyâ€™s commit mechanism. The runner remains valuable as an implementation experiment, but the new contract should not create a third change mechanism.

## Are Harmony diagnostics related to Dry Runs?

No.

Harmonyâ€™s current diagnostics concern replay and snapshot materialization. [`SnapshotWorker`](C:/Users/johnm/Documents/repos/harmony/src/SIL.Harmony/SnapshotWorker.cs) can encounter unsupported or opaque changes while reconstructing object snapshots. The planned HAR-7 diagnostic channel is intended to prevent one malformed or unsupported change from poisoning an entire replay batch.

That is fundamentally different from a Motif Dry Run:

| Concern | Harmony diagnostic | Motif Dry Run |
|---|---|---|
| Question | â€œCan this commit history be replayed/materialized?â€ | â€œWhat would this Proposal do to this exact LibLCM baseline?â€ |
| Input | Harmony commits and changes | Semantic Proposal plus LibLCM state |
| Output | Replay/applicability diagnostic | Before-state, expected effects, conflicts, warnings, applicability |
| Mutation | Snapshot/replay processing | Must not persist project changes |
| Ownership | Harmony | Motif/LibLCM host |
| Drift meaning | Invalid or unsupported history | Baseline changed since evaluation |

LexBoxâ€™s [`DryRunMiniLcmApi`](C:/Users/johnm/Documents/repos/languageforge-lexbox/backend/FwLite/FwLiteProjectSync/DryRunMiniLcmApi.cs) is also unrelated in meaning. It records which synchronization writes would have been issued by the CRDTâ†”`.fwdata` synchronizer. It has no Proposal identity, baseline token, effect digest, approval capability, or atomic apply gate.

These three diagnostics should remain separate types and namespaces.

## Pattern evaluation

| Pattern | Assessment |
|---|---|
| Optimistic version token / CAS | Recommended foundation. A token detects drift cheaply and works with offline Proposal creation. The compare must occur after acquiring exclusive apply authority. Official database systems use this pattern by checking the original version/value during update; see [Microsoft optimistic concurrency](https://learn.microsoft.com/en-us/dotnet/framework/data/adonet/optimistic-concurrency) and [etcd transactions](https://etcd.io/docs/v3.6/learning/api/). |
| Content-addressed snapshot | Recommended for evidence, reproducibility, and agent workspaces. It does not by itself authorize mutation of the live project. [Gitâ€™s object model](https://git-scm.com/book/en/v2/Git-Internals-Git-Objects.html) is the useful analogy. |
| Repeatable-read / serializable transaction | Useful inside Harmonyâ€™s database, but not sufficient across LibLCMâ€™s in-memory object graph, XML-backed `.fwdata`, PanGloss, and external stores. Serializable databases can abort transactions on conflicts; they do not make independent systems one transaction. See [PostgreSQL isolation](https://www.postgresql.org/docs/current/transaction-iso.html). |
| Lease / lock | Required for the final apply window. It should be short-lived and acquired only after review/Dry Run. The existing `.fwdata.lock` is useful lifecycle protection, but it is not a complete application-level lease protocol. |
| Capability-based authority | Recommended as the API shape: an expiring, project-scoped, one-use `ApplyAuthorization`. This resembles ownership at runtime, but it cannot provide Rustâ€™s compile-time guarantee. See [Rust ownership](https://doc.rust-lang.org/stable/book/ch04-00-understanding-ownership.html). |
| Immutable project clone | Recommended for agent deliberation and PanGloss work. LibLCM already exposes [`LcmCache.CreateCacheCopy`](C:/Users/johnm/Documents/repos/liblcm/src/SIL.LCModel/LcmCache.cs), and LexBox already clones CRDT SQLite databases in [`SnapshotAtCommitService`](C:/Users/johnm/Documents/repos/languageforge-lexbox/backend/LcmCrdt/LcmCrdt/SnapshotAtCommitService.cs). |
| Per-agent `LcmCache` | Safe if each cache is backed by a private copy and disposed reliably. Never allow multiple writable caches against the same `.fwdata` path. LexBoxâ€™s [`FwDataFactory`](C:/Users/johnm/Documents/repos/languageforge-lexbox/backend/FwLite/FwLiteProjectSync/FwDataFactory.cs) currently caches by path and explicitly notes that a lease object is still missing. |
| Proposal branches in CRDT history | Good for offline authoring, review, and merge. A CRDT branch head is not automatically proof that the live `.fwdata` state is identical. |
| Rebase and re-Dry-Run | Mandatory after drift. Existing conflict rules are sound: rebase may refresh evidence or unambiguous anchors, but may not alter intent or operation order. |
| Scoped footprint | Good for diagnostics and future optimization. It is safe for authorization only if the footprint includes the complete transitive read/effect closure. |
| Whole-project digest | Best correctness choice for the first version. It is conservative but avoids silently missing LibLCM cascades, computed values, incoming references, or hidden dependencies. |

CRDT transactions are useful for bundling Proposal changes within Harmony. For example, [Yjs transactions](https://docs.yjs.dev/api/y.doc) group changes atomically and its updates are designed to be commutative, associative, and idempotent. That does not solve the separate problem of authorizing a mutation against a live LibLCM cache.

## Proposed contract

### `BaselineToken`

A canonical, immutable description of the state against which the Proposal and Dry Run were produced:

```text
BaselineToken
  projectIdentity
  authorityIdentity
  generation
  semanticSnapshotDigest
  scope = whole-project | sound-scoped
  projectionVersion
  liblcmVersion
  modelVersion
  harmonyHead / stateVector, if applicable
```

For v1:

- `scope` should be `whole-project`;
- `semanticSnapshotDigest` should use Motifâ€™s NFD/NFSC normalization rules;
- the digest should exclude Motif bookkeeping such as the applied log;
- the token should include the projectâ€™s stable LibLCM identity;
- file timestamps, file length, Harmony commit ID alone, and LexBoxâ€™s current JSON project snapshot are insufficient.

A Harmony commit head may be included as provenance, but it should not be the sole token. Harmonyâ€™s current [`CommitBase.GenerateHash`](C:/Users/johnm/Documents/repos/harmony/src/SIL.Harmony.Core/CommitBase.cs) hashes the commit ID and parent hash, not the complete payload. That is not a content-addressed approval token.

### `Proposal`

The Proposal remains semantic CRUD+ intent:

```text
Proposal
  proposalId
  intentDigest
  contractVersions
  ordered operations
  dependencies
  nonSemantic extensions
```

A generated LibLCM Mutation Plan remains output-only. It must never become the canonical input or authorization object.

The Proposal may reference the baseline and Dry Run artifacts, but those references are evidence, not low-level mutation instructions.

### `DryRunRecord`

An immutable record containing:

```text
DryRunRecord
  dryRunId
  proposalId
  intentDigest
  baselineToken
  expectedEffects
  footprintDigest
  effectDigest
  warnings
  conflicts
  applicability
  runnerVersion
  liblcmVersion
  projectionVersion
  createdAt
```

A Dry Run produced against a scratch clone is valid only if the clone was materialized from the referenced baseline token.

### `Assessment`

A separate PanGloss record:

```text
Assessment
  assessmentId
  proposalId or artifact reference
  grammar/model digest
  word-set digest
  baseline/candidate artifact references
  parser version
  diagnostics and results
```

It should not be merged with the LibLCM Dry Run. If the grammar or word set changes, the Assessment becomes stale independently of the LibLCM token.

### `ApplyAuthorization`

A short-lived capability minted by the live apply authority after review:

```text
ApplyAuthorization
  authorizationId
  projectIdentity
  proposalId
  intentDigest
  dryRunId
  baselineToken
  effectDigest
  holderIdentity
  nonce
  expiresAt
  oneUse
  policyDecisionReference
```

It is invalid if:

- the Proposal content changes;
- the baseline token changes;
- the Dry Run or Assessment is no longer acceptable;
- the authority lease expires;
- the authorization is presented to another project or replica.

### Atomic apply sequence

The live host should perform:

1. Acquire the project-scoped exclusive apply lease.
2. Verify the authorization is valid and unexpired.
3. Recompute the current `BaselineToken` while holding the lease.
4. Refuse with `DriftRefusal` if it differs from the Dry Run token.
5. Open one outer LibLCM UOW.
6. Apply operations in Proposal order; individual operations must join the outer UOW and never commit independently.
7. Read back the actual semantic effects.
8. Commit the UOW.
9. Save the `.fwdata` project while the lease remains held.
10. Compute the after-token.
11. Persist or reconcile the Receipt.
12. Release the lease.

The critical property is not â€œthe token was checked recently.â€ It is:

> No permitted writer can change the authoritative state between the final token check and persistence of the Proposal.

If FieldWorks or another process can write around this protocol, atomic confidence is impossible. The system must either prevent those writes or refuse to claim the guarantee.

## Handoffs

| Component | Responsibility |
|---|---|
| Motif | Proposal schema, canonicalization, semantic snapshots, Dry Run records, effect/footprint digests, rebase rules, drift diagnostics, apply orchestration, receipts |
| Harmony | Commit/change transport, snapshots, history, review-related application entities, artifact references, CRDT convergence primitives |
| LcmCrdt | Generated LibLCM-shaped CRDT entities and changes, replica state vectors, offline Proposal/review transport |
| FwLiteProjectSync | CRDTâ†”`.fwdata` synchronization, sync-specific Dry Runs, project snapshot materialization, live baseline-token provider, lifecycle coordination |
| LibLCM | Loaded `LcmCache`, object mutation, UOW rollback/commit, XML backend locking, save lifecycle |
| PanGloss | Immutable Assessments, grammar/word-set artifacts, parser results and diagnostics |

The current [`CrdtFwdataProjectSyncService`](C:/Users/johnm/Documents/repos/languageforge-lexbox/backend/FwLite/FwLiteProjectSync/CrdtFwdataProjectSyncService.cs) should retain its synchronization Dry Run API. It should not be renamed or repurposed to mean Motifâ€™s Proposal Dry Run.

## Agent workspace lifecycle

1. A controller materializes a known baseline from the authoritative LibLCM project or CRDT commit.
2. The agent receives the `BaselineToken`.
3. A private `.fwdata` copy, `LcmCache.CreateCacheCopy`, or CRDT SQLite fork is created.
4. The agent designs and revises the Proposal without holding a live-project lock.
5. Motif runs the Dry Run against that exact baseline clone.
6. PanGloss runs any Assessment against the corresponding frozen grammar and word-set artifacts.
7. The Proposal, Dry Run, Assessment, and evidence are stored immutably.
8. Review occurs through Harmony/application-layer records.
9. The live host mints `ApplyAuthorization`.
10. The host performs the final lease/token/UOW/save sequence.
11. The host emits a Receipt or `NeedsReconciliation`.
12. The private cache and scratch project are disposed and removed according to retention policy.

A Git worktree is not the right abstraction for `.fwdata`; a validated scratch project copy is.

## Multiple agents and offline replicas

- Multiple agents may author Proposals against the same baseline.
- The first Proposal successfully acquiring apply authority and matching the token may apply.
- Later Proposals must refuse with drift and undergo a new Dry Run.
- Non-overlapping Proposals should not be silently applied from old evidence until sound effect-closure rules exist.
- Offline replicas may author and merge Proposals and Assessments.
- Offline replicas must not receive a reusable live apply capability.
- Apply authorization is minted only by the live authority after synchronization and final validation.

For crash recovery:

- OS-held file locks naturally disappear when the owning process exits.
- Remote coordination should use a TTL lease; etcd documents leases as keys that expire after their TTL, with atomic transactions guarded by comparisons. See [etcd leases and guarantees](https://etcd.io/docs/v3.5/learning/api_guarantees/).
- Authorization should be one-use and expire quickly.
- An interrupted apply must be recoverable by comparing the projectâ€™s applied marker and current semantic state.
- If the expected after-state is present, complete the Receipt.
- If neither expected state nor a clean baseline is present, mark the run `NeedsReconciliation`; never blindly retry.

## UI, tools, and performance

The UI and CLI should expose:

- baseline identity and token;
- Dry Run freshness;
- Assessment freshness;
- authorization expiry;
- exact drift reason;
- â€œrefresh and re-runâ€ action.

Useful commands would be conceptually:

```text
motif proposal dry-run
motif proposal authorize
motif proposal apply
```

The API should return typed tokens and records, not accept a low-level Mutation Plan as input.

A whole-project semantic digest may be expensive. The first implementation can compute it on a scratch clone or during an explicit apply preflight. Later, an incremental digest index or authoritative generation counter can optimize it. The existing Motif ADR correctly notes that incoming-reference indexing can have a whole-project first-touch cost.

## What the current plans cover

Covered:

- Proposal/review as application-layer concepts in ADR 0013.
- Harmony commits, snapshots, historical reads, and CRDT synchronization.
- Scratch-copy workflows and no-mutation experimentation in [`motif-overall-plan.md`](C:/Users/johnm/Documents/repos/motif/docs/motif-overall-plan.md).
- PanGloss baseline/candidate Assessments.
- One outer LibLCM unit of work, read-back, rollback, and receipts.
- Exact identity matching, canonical normalization, effect sets, and rebase restrictions.
- LexBox CRDT forks and synchronization Dry Runs.
- LibLCM cache creation, `.fwdata` locking, disposal, and save APIs.

Missing:

- A shared, canonical `BaselineToken`.
- A project-wide semantic digest or authoritative generation.
- An apply capability and lease protocol.
- A final compare-and-apply critical section.
- A durable Receipt state machine across LibLCM, Harmony, and external stores.
- Crash recovery and reconciliation.
- A sound definition of a scoped transitive footprint.
- A binding between PanGloss Assessment validity and the relevant project/model token.
- Multiple-agent and offline authorization rules.
- A defined authority relationship between CRDT state and live `.fwdata`.
- Payload-bound Harmony content hashes.
- A cache lease/ownership abstraction in LexBoxâ€™s `FwDataFactory`.

## Minimal implementation slice

1. Define `BaselineToken`, `DryRunRecord`, `ApplyAuthorization`, `Receipt`, and `DriftRefusal` in the Motif application layer.
2. Use one operation, `lexical/sense/setGloss`, as the conformance slice.
3. Compute a whole-project semantic baseline digest, while retaining the existing footprint digest for diagnostics.
4. Add an apply coordinator around the existing `LcmCache` that owns exclusive authority and performs the final token check.
5. Execute the Proposal and applied marker in one outer UOW, save under the same authority, and return before/after tokens.
6. Add tests for:
   - unchanged baseline succeeds;
   - changed baseline refuses before mutation;
   - two agents, one winner and one drift refusal;
   - mid-Proposal rollback;
   - stale/expired authorization;
   - crash/reconciliation behavior;
   - separate Motif Dry Run versus LexBox synchronization Dry Run.
7. Keep Harmony diagnostics unchanged except for documenting their separate role.

This slice would prove the required guarantee without expanding the CRDT generator or creating a new Harmony-level mutation mechanism.

No files were edited or committed. The repositories already contained changes in Motif and LexBox; Harmony and LibLCM were clean when checked.
