# Harmony dual merge/materialization policy

*Preserved research from an xhigh Luna read-only review on 2026-08-01. This is evidence and recommendation, not a decision record. The live plans and ADRs remain authoritative.*

## Conclusion

Yesâ€”Harmony can support both behaviors, provided it separates:

```text
Replicated commit history
        â†“ always merge, deduplicate, retain forever
Deterministic materialization
        â”œâ”€ permissive policy â†’ apply legacy behavior
        â””â”€ fail-closed policy â†’ apply, or preserve as refused/orphaned diagnostic
```

A refused change must not be removed from the commit log or rejected by the server. It remains synchronized history, while the strict materialized projection omits its effect and records a deterministic diagnostic.

This is different from saying that one field simultaneously has both canonical outcomes. Replicas converge only when they use the same policy and policy revision. An old YOLO client may converge on history while producing a different local projection.

No files were edited or committed.

## Local architecture evidence

The Motif glossary already separates Harmonyâ€™s CRDT change store from the canonical FieldWorks model and identifies phonological rule order as semantic, not scalar ordering: [CONTEXT.md](C:/Users/johnm/Documents/repos/motif/CONTEXT.md:62), [CONTEXT.md](C:/Users/johnm/Documents/repos/motif/CONTEXT.md:97).

The planned Harmony work explicitly identifies the needed shape:

- Harmony already replays commits deterministically using `(DateTime, Counter, Id)`.
- The current failure is that one unappliable change poisons the transaction.
- The intended correction is â€œapply what you can and record a structured diagnostic.â€
- The current `SetOrderChange<T>` is a LWW `double`, which is inadequate for phonological order.

See [plan-harmony.md](C:/Users/johnm/Documents/repos/motif/docs/plan-harmony.md:51) and [plan-harmony.md](C:/Users/johnm/Documents/repos/motif/docs/plan-harmony.md:105).

In Harmony:

- `SyncResults` currently reports only missing commits, not materialization diagnostics: [DataModel.cs](C:/Users/johnm/Documents/repos/harmony/src/SIL.Harmony/DataModel.cs:13).
- `AddRangeFromSync` inserts commits and updates snapshots inside one transaction, catching only `DbUpdateException`: [DataModel.cs](C:/Users/johnm/Documents/repos/harmony/src/SIL.Harmony/DataModel.cs:138).
- Snapshot replay processes commits in sorted order and operations by `ChangeEntity.Index`: [SnapshotWorker.cs](C:/Users/johnm/Documents/repos/harmony/src/SIL.Harmony/SnapshotWorker.cs:63).
- An unknown `OpaqueChange` is retained, but unsupported known changes can throw or silently no-op: [SnapshotWorker.cs](C:/Users/johnm/Documents/repos/harmony/src/SIL.Harmony/SnapshotWorker.cs:76).
- Operation identity is naturally `(CommitId, Index)`: [ChangeEntity.cs](C:/Users/johnm/Documents/repos/harmony/src/SIL.Harmony.Core/ChangeEntity.cs:5).
- `SetOrderChange<T>` stores only an absolute `double` and applies it directly: [SetOrderChange.cs](C:/Users/johnm/Documents/repos/harmony/src/SIL.Harmony/Changes/SetOrderChange.cs:11).

The commit hash should not be used as the stable diagnostic identity. Harmonyâ€™s hash includes the parent hash, and late commits can cause the chain to be rewritten: [CommitBase.cs](C:/Users/johnm/Documents/repos/harmony/src/SIL.Harmony.Core/CommitBase.cs:25), [CrdtRepository.cs](C:/Users/johnm/Documents/repos/harmony/src/SIL.Harmony/CrdtRepository.cs:384).

In Lexbox:

- The server stores and merges incoming commits by key; it performs no semantic replay: [CrdtCommitService.cs](C:/Users/johnm/Documents/repos/languageforge-lexbox/backend/LexBoxApi/Services/CrdtCommitService.cs:12).
- The server then broadcasts `OnProjectUpdated`: [CrdtController.cs](C:/Users/johnm/Documents/repos/languageforge-lexbox/backend/LexBoxApi/Controllers/CrdtController.cs:41).
- Reconnect already triggers a catch-up sync: [LexboxHubConnection.cs](C:/Users/johnm/Documents/repos/languageforge-lexbox/backend/FwLite/FwLiteShared/Projects/LexboxHubConnection.cs:198), [LexboxHubConnection.cs](C:/Users/johnm/Documents/repos/languageforge-lexbox/backend/FwLite/FwLiteShared/Projects/LexboxHubConnection.cs:240).
- Notification code currently reports entry changes and deletions only: [SyncService.cs](C:/Users/johnm/Documents/repos/languageforge-lexbox/backend/FwLite/FwLiteShared/Sync/SyncService.cs:209).
- `LcmCrdtKernel` explicitly registers `SetOrderChange` for existing entities, so a global behavior change would affect current consumers: [LcmCrdtKernel.cs](C:/Users/johnm/Documents/repos/languageforge-lexbox/backend/FwLite/LcmCrdt/LcmCrdtKernel.cs:354).

The FwData bridge is particularly sensitive: it diffs CRDT state against FwData and depends on ordering and regeneration semantics. See [FwLite/AGENTS.md](C:/Users/johnm/Documents/repos/languageforge-lexbox/backend/FwLite/AGENTS.md:166).

The checked-out Harmony repository has no `AGENTS.md` or `docs/` directory; I used its README, source, and tests. Its README confirms that changes represent user intent and are stored permanently: [README.md](C:/Users/johnm/Documents/repos/harmony/README.md:65).

## Relevant primary patterns

| Pattern | Applicability |
|---|---|
| Ordinary CRDT convergence | The commit set and deterministic projection fit the CRDT model: replicas receiving the same updates deterministically reach the same state. [CRDTs](https://arxiv.org/abs/1805.06358) |
| Invariant confluence | If concurrent ordered operations are invariant-confluent, they can apply offline. If not, correctness requires coordination or refusal. [Coordination Avoidance in Database Systems](https://www.vldb.org/pvldb/vol8/p185-bailis.pdf) |
| Conflict-aware operations | CARDs attach consistency guards to operations and block only executions that violate them. This closely matches fail-closed materialization. [Conflict-Aware Replicated Data Types](https://arxiv.org/abs/1802.08733) |
| Escrow/rights | Works when an invariant can be divided into local rights. If rights are exhausted, the operation fails or must obtain rights. This is suitable for bounded counters, not obviously for semantic rule order. [Bounded Counter CRDT](https://asc.di.fct.unl.pt/~nmp/pubs/srds-2015.pdf) |
| MV-register/conflict values | Preserves concurrent alternatives instead of choosing one. Useful for diagnostics or conflict-aware UI, but existing LibLCM consumers expect one valid ordered structure. [Eventually Consistent Register Revisited](https://arxiv.org/abs/1511.05010) |
| Suggestions/staging | Google Docs keeps edits as deferred suggestions with explicit accept/reject status. This supports a separate proposal/resolution layer, not automatic canonical application. [Google Docs suggestions](https://developers.google.com/workspace/docs/api/how-tos/suggestions?hl=en) |
| Compensation | A later operation can reverse an earlier one without deleting history. This is appropriate for explicit corrections, not for silently applying an invalid intermediate state. [git-revert](https://git-scm.com/docs/git-revert.html) |
| Deterministic replay validation | Replicas must begin from equivalent state and make deterministic decisions; failed re-execution can be aborted with no lasting materialized effect. [Coda operation shipping](https://www.usenix.org/event/usenix99/full_papers/lee/lee_html/node8.htm) |

The important distinction is the same one visible in Git: merging histories and materializing a working tree are separate operations. Git can retain both histories while stopping materialization at a conflict; the conflict is then resolved by a new action. [git-merge](https://git-scm.com/docs/git-merge).

## Recommended data model

Use a deterministic operation key:

```text
OperationKey = (CommitId, ChangeIndex)
```

Do not use the mutable hash-chain value.

A diagnostic should contain at least:

```text
DiagnosticKey
OperationKey
EntityId
ChangeDiscriminator
FieldPath or PolicyKey
PolicyRevision
Disposition: Orphaned | Refused | Deferred | Resolved
ReasonCode
BaselineDigest
ExpectedFacts
ObservedFacts
Anchor/causal evidence
Candidate resolutions
Human-readable explanation
ResolvedByOperationKey, if resolved
```

The stable key should be:

```text
DiagnosticKey =
    (OperationKey, PolicyKey, PolicyRevision)
```

If Harmony needs a single identifier, derive it deterministically from that tuple using the projectâ€™s canonical textual GUID/network-byte-order rules. Do not use `Guid.ToByteArray()` as a portable identity conversion.

Diagnostics should normally be derived from the replicated commit set and persisted as an idempotent materialization table. They should not be ordinary authored commits, because that creates feedback loops and duplicated diagnostic history.

A later author action may be an ordinary new Harmony change referencing the diagnostic. The original operation remains immutable.

## Three concrete designs

### 1. Policy registry with a structured apply decision â€” recommended

Add an optional materialization-policy registry to `HarmonyConfig`, keyed by change discriminator, object discriminator, and optionally field path.

Conceptually:

```text
Unregistered change/field â†’ Permissive
Registered strict field   â†’ Evaluate(...)
                            Apply
                            or Refuse + Diagnostic
```

The policy interface should be additive rather than changing `IChange.ApplyChange`, preserving existing change classes and serializers.

The policy belongs in Lexbox/LcmCrdt registration; Harmony only knows generic discriminators, fields, snapshots, and decisions. This aligns with ADR 0014â€™s rule that the manifest supplies policy while Harmony remains domain-free: [ADR 0014](C:/Users/johnm/Documents/repos/motif/docs/adr/0014-generate-the-crdt-layer-from-masterlcmodel.md:68).

Advantages:

- preserves current permissive behavior by default;
- supports type-level and field-level strictness;
- does not require every existing consumer to implement a new interface;
- gives Harmony one place to persist diagnostics and continue replay;
- permits generated LcmCrdt registrations for `feeding` fields while leaving ordinary Sense/example ordering permissive.

Critical limitation: the existing `SetOrderChange<T>(Guid, double)` has no semantic placement anchor. It cannot safely support strict phonological ordering merely by registering `Order` as fail-closed. It should either remain YOLO or be refused wholesale as a legacy, anchorless operation until replaced.

### 2. Strict change classes

Introduce an optional interface such as:

```text
IFailClosedChange
    Evaluate(existingSnapshot, context) -> ApplyDecision
```

Only new semantic change classes implement it. Existing `IChange` classes retain legacy behavior.

A phonological sequence change would carry explicit semantic intent, for example:

```text
InsertRule(ruleId, leftAnchorId, rightAnchorId, expectedSequenceDigest)
MoveRule(ruleId, beforeId/afterId, expectedNeighbors)
DeleteRule(ruleId)
```

The evaluator may apply the change only when its anchors and semantic preconditions remain unambiguous.

Advantages:

- policy travels with the change type;
- complex grammar logic stays near the operation;
- old clients can preserve unknown strict changes as `OpaqueChange`.

Disadvantages:

- field-level policy is less centralized;
- policy revision and compatibility are harder to audit;
- every strict operation class must be designed carefully.

### 3. Strict projection plus application-level resolution

Keep Harmonyâ€™s commit history unchanged and add a strict projection that produces:

```text
CanonicalSnapshot
ActiveDiagnostics
HistoricalDiagnostics
```

Review, acceptance, amendment, and resolution remain application-level state over Harmony commits, consistent with [ADR 0013](C:/Users/johnm/Documents/repos/motif/docs/adr/0013-harmony-is-the-change-mechanism.md:63).

A tool can:

1. inspect the orphaned operation and explanation;
2. author a new Proposal or explicit resolution change;
3. reference the original `DiagnosticKey`;
4. let deterministic replay re-evaluate the original operation or apply the amendment;
5. observe the diagnostic transition to `Resolved`.

This is the least disruptive approach for current server consumers. The server continues storing all commits, while strict clients expose a reliable diagnostic stream.

### Optional: MV-register conflict values

For fields that can tolerate conflict-aware consumers, Harmony could materialize all concurrent values rather than refusing one. This resembles MV-register designs and Automergeâ€™s ability to expose both winning and losing values with stable operation IDs. [Automerge conflicts](https://automerge.org/docs/reference/documents/conflicts/)

It is not appropriate as the first phonological-order solution: LibLCM and current LcmCrdt consumers expect a normal sequence, not a `Conflict<RuleSequence>` value.

## Re-resolution and notifications

On replay:

- persist the commit regardless of materialization outcome;
- evaluate each operation independently;
- continue with unrelated operations;
- cascade refusal only where later operations depend on the refused result;
- upsert the diagnostic by deterministic key;
- retain historical diagnostics as resolved/superseded rather than deleting them.

Lexbox already has the reconnect path. Extend the local sync result or event bus with something like:

```text
MaterializationDiagnosticsChanged(
    projectId,
    addedOrUpdatedDiagnosticKeys,
    resolvedDiagnosticKeys,
    affectedEntityIds
)
```

The server need not transmit diagnostics as part of the commit protocol. It can continue broadcasting â€œproject updatedâ€; after reconnect the client syncs commits, recomputes diagnostics, and publishes the diagnostic event. This preserves compatibility with the current server implementation.

The current `SyncResults` record can gain an optional diagnostics field, or diagnostics can be queried after `SyncWith` to avoid changing the wire format.

## CAP and offline consequences

This design preserves offline availability for authoring and history replication:

- the author can create the change offline;
- the server accepts and converges the commit history;
- every strict replica eventually reaches the same refusal decision.

It does not promise that the refused operation immediately appears in the canonical semantic model. That is deliberate.

If the system instead requires every offline replica to immediately materialize one globally valid phonological order, then an ambiguous ordered operation must either be deterministically chosenâ€”which may invent authorial intentâ€”or require coordination. CAP and invariant-confluence results explain why there is no third option in the general case. [Brewerâ€™s conjecture](https://doi.org/10.1145/564585.564601)

Escrow can avoid coordination only when the invariant can be represented as transferable rights. Semantic rule placement depends on neighboring rule identities and contents, so it is not naturally an escrowable numeric resource.

A refusal itself converges if and only if:

1. all replicas have the same commit set;
2. replay order is deterministic;
3. policy/schema revision is identical;
4. validators use canonical, deterministic inputs;
5. diagnostic keys do not contain local timestamps or random IDs.

Thus:

```text
same history + same policy revision
    â†’ same canonical projection
    â†’ same active diagnostic set
```

Replicas may temporarily disagree while receiving different histories. That is ordinary eventual convergence, not a refusal failure.

## Phonological rule-order proof and test requirements

The existing Harmony sequence tests are not sufficient. They test mechanical order, not feeding/bleeding semantics. The current Harmony tests do establish useful baselines for idempotent sync and regenerated snapshot equivalence: [SyncableTests.cs](C:/Users/johnm/Documents/repos/harmony/src/SIL.Harmony.Tests/Syncable/SyncableTests.cs:106), [SnapshotTests.cs](C:/Users/johnm/Documents/repos/harmony/src/SIL.Harmony.Tests/SnapshotTests.cs:146).

Required additional tests:

- Concurrent inserts into the same gap converge by operation identity, never by equal `double` values.
- Concurrent reorder, insert, and delete converge in every arrival order.
- Duplicate delivery is idempotent for both snapshots and diagnostics.
- Late-arriving commits reproduce the same diagnostic keys.
- An orphaned anchor is retained with a stable reason code.
- A concurrent move whose semantic order is ambiguous produces no canonical phonological order.
- A deterministic, unambiguous move applies successfully.
- A refused operation does not poison unrelated operations in the same commit.
- A refused operation does not silently become a LWW scalar order.
- Old LcmCrdt consumers using `SetOrderChange<Sense>`, `SetOrderChange<ExampleSentence>`, and similar types retain their current permissive behavior.
- FwData synchronization does not manufacture a compensating edit when CRDT materialization refuses an operation.
- Reconnect causes tools to receive the diagnostic event even when the SignalR notification was missed.

For the semantic proof, construct rule pairs where:

```text
Rule A then Rule B â‰  Rule B then Rule A
```

using a real HermitCrab/phonological evaluation. The strict policy should accept only when the authored anchors and available evidence identify one legal interpretation. Otherwise it should preserve both operation identities and refuse materialization.

## Recommendation

Implement Design 1 plus Design 3:

- always merge and retain commits;
- add a generic, versioned materialization-policy registry;
- default all unregistered fields to current permissive behavior;
- add per-operation deterministic diagnostics;
- keep diagnostics derived/persisted, not authored;
- make resolution a new explicit change or Proposal;
- introduce a new semantic sequence operation for phonological rules;
- leave existing scalar `SetOrderChange<T>` unchanged for current consumers until migrated.

Do not make Harmony globally fail-closed, and do not make a strict phonological field rely on the existing `double` order value. The architecture can support both modes, but safe fail-closed phonological ordering requires richer operation intent than Harmony currently receives.
