# What actually needs to be added — point by point

*2026-07-27. Assumes proposal/review/approval **storage lives in the application**, not in Harmony.
Scope: the core capabilities required so that "the rest" can be built elsewhere.*

## Already there — do not build these

| Requirement | What provides it | Evidence |
| --- | --- | --- |
| **"Here is what it was before"** — the effect record | `ObjectSnapshot` stores the **full entity**, plus `References`, `EntityIsDeleted`, `CommitId`, `IsRoot` | `Db/ObjectSnapshot.cs:57-78` — `Entity = entity; References = entity.GetReferences();` |
| Reading that record at any point | `GetAtCommit`, `GetBeforeCommit`, `GetAtTime`, `GetSnapshotsAtCommit` | `DataModel.cs:300-352` |
| **"I am at engine state X"** | `Commit.Hash` / `ParentHash` chain; `GetProjectSnapshot` → `ModelSnapshot` | `CommitBase.cs:32-40`, `DataModel.cs:289` |
| Apply a set of changes as one atomic unit | `AddChanges(clientId, changes)` → one `Commit` | `DataModel.cs:83` |
| Carrying changes this client can't interpret | `OpaqueChange` — raw JSON round-trips, applies once the type is known | `Changes/OpaqueChange.cs` |
| Attachments (PanGloss reports, metrics) | `Resource/` — create / upload / set-metadata / delete, all as CRDT changes; wired in production | `Resource/*.cs`, `LcmCrdtKernel.cs:68,402` |
| Provenance, rationale, confidence | `CommitMetadata.ExtraMetadata` — `Dictionary<string,string?>`, documented as app-specific metadata | `Core/CommitMetadata.cs` |
| 87% of field semantics | `JsonPatchChange<T>` (generic), `DeleteChange<T>` (generic), `CreateChange<T>` (per type) | see [declarative-commands-vs-crdt.md](declarative-commands-vs-crdt.md) |

**The effect record you want to stash elsewhere already exists and is already persisted.** It is not a
diff — it is the full prior entity state, which is strictly more useful.

---

## Needs adding — core, in Harmony

### 1. Commit removal / revert — for "apply it, then back it away"

**Gap.** There is no way to remove a commit. `DeleteChange` is a *tombstone* (`DeleteChange.cs:12`
sets `DeletedAt`), not history removal. `DeleteStaleSnapshots` (`CrdtRepository.cs:130`) is snapshot
GC, not commit removal.

**Why it is cheaper than it sounds.** Harmony **already recomputes state after inserting a commit in
the middle of history** — late-arriving commits splice in and the hash chain and snapshots are
rebuilt (`SnapshotWorker`, `DataModel.cs:194`). Removal is the *inverse of an operation Harmony
already performs correctly*. The hard part — "recompute everything downstream of position N" — is
built and tested.

**Two shapes, pick one:**
- `RemoveCommit(commitId)` / `RevertTo(commitId)` in `DataModel`, reusing the existing splice-and-
  rebuild path.
- **Or no Harmony change at all**: copy the SQLite file, apply, read, discard the copy. This is
  exactly the scratch-copy pattern already designed for `.fwdata`, and on SQLite it is far cheaper.

**Recommendation:** start with the file-copy pattern (zero core work, proves the workflow), and add
`RevertTo` only if the copy cost shows up in practice.

### 2. Content hashing of the change payload

**Gap.** `CommitBase.GenerateHash` hashes `Id.ToByteArray()` + `parentHashBytes` with XxHash64 —
**the change payload is never hashed** (`CommitBase.cs:32-40`). The chain proves *ordering*, not
*content*. "I approved commit X" is not cryptographically bound to what X contains.

**Needed if** approval must be tamper-evident or drift-invalidated. This is the one capability with no
equivalent anywhere in the stack, and it is **much cheaper before grammar change classes exist**.

**Note:** because the current hash excludes the payload, adding a *separate* content digest field is
backward-compatible — existing commit chains stay valid.

### 3. A converging sequence type — for the 2 `feeding` fields

**Gap.** `SetOrderChange<T>` assigns `entity.Order = Order`, a last-writer-wins `double`. Concurrent
inserts between the same neighbours can compute the identical fractional value.

**Scope check: this is 2 fields of 473.** Phonological rule order, where order encodes feeding /
bleeding. HermitCrab itself stores rule order as a flat `List<IPhonologicalRule>`, so what is needed
is a *sequence that converges deterministically* — not a dependency graph.

### 4. A decision, not a feature — the 3 `index-as-identity` fields

**Not a gap; a modelling choice.** Alpha variables use position as an identifier.
`LcmCrdt/Changes/JsonPatchChange.cs` — `JsonPatchValidator` — **already rejects index-based patch
paths**, with the comment *"prevents the use of indexes in the path, as this will cause major problems
with CRDTs."*

Harmony has already ruled this out. The correct response is to model these 3 fields as a **keyed map**
rather than an indexed array. No core feature required — a modelling decision, and arguably a bug fix.

### 5. Reference-set policy — for the 34 `addRef|removeRef` fields

**Gap (documentation, possibly semantics).** Add-wins versus remove-wins is currently implicit in each
hand-written change class. With 75 reference fields across 38 grammar classes coming, this needs to be
an explicit, uniform, documented policy before it is replicated 38 times.

### 6. Cross-owner move — for the 32 `create|delete|move|reparent` fields

**Gap.** `MoveSenseToEntryChange` handles one case by hand. Move-between-owners is the classic CRDT
cycle hazard: two concurrent reparents can produce a cycle or an orphan. Needs a general rule
(reject-cycle-on-apply, or last-writer-wins-on-parent) before grammar's owning hierarchies land.

### 9. A deferred diagnostic channel — so a bad change fails itself, not the batch

*(Numbered 9 because it was found after items 1–8 were written. It is a **core** item, not an
application-layer one. Evidence:
[inventory-harmony-conflict-reporting.md](inventory-harmony-conflict-reporting.md), key claims
re-verified directly.)*

**What is actually there.** The entire apply policy is one branch, `SnapshotWorker.cs:76-110`, with
four arms:

| Situation | Behavior | Loud? |
| --- | --- | --- |
| No prior snapshot, change is `OpaqueChange` | skipped, retained in history (`:78-81`) | silent |
| No prior snapshot, change cannot create | **throws** `NotSupportedException` (`:85`, `EditChange.cs:10-11`) | loud |
| Prior snapshot deleted, change supports create | **resurrects the entity** (`:87-91`) | silent |
| Exists, change cannot apply to existing | does nothing (`:104-109`) | silent |

Plus `MarkDeleted` (`:124-148`), which recursively strips references to a deleted entity and can
cascade into further deletions. `SnapshotWorker` has no logger, so that is entirely silent.

**Two corrections to earlier reasoning in this repository.**

1. *"A CRDT has no canonical moment at which to refuse a change, because replicas see arrivals in
   different orders."* True of CRDTs generally; **false for Harmony.** `DataModel.UpdateSnapshots`
   (`DataModel.cs:190-194`) takes a `SortedSet<Commit>`, takes `commitsToApply.First()` as the oldest
   affected commit, calls `DeleteStaleSnapshots` on it, and replays forward. `CommitBase.CompareKey`
   is `(HybridDateTime.DateTime, Counter, Id)` (`CommitBase.cs:25`, `CompareTo` at `:49-53`). A
   late-arriving commit triggers rewind-and-replay in canonical logical order, so every replica
   evaluates the same commits in the same sequence. A decision taken during apply *is* deterministic.
2. *"Harmony needs loud failure added."* It already has it. `SnapshotWorker.cs:84` comments
   *"this will (and should) throw if the change doesn't support NewEntity"*, and the exception names
   the change type, `CommitId`, and `EntityId`.

**The actual defect.** The throw happens inside a transaction (`DataModel.cs:75-79`, `:151-155`)
during a replay that may have been triggered by an unrelated late-arriving commit — and
`AddRangeFromSync` catches only `DbUpdateException` (`:157`), so it propagates uncaught. One bad
change therefore does not fail *itself*; it fails **every commit in the batch, on every snapshot
regeneration, permanently**. `RegenerateSnapshots` (`:234-243`) re-hits it on every rebuild.

**What is needed** is not more crashing but converting the existing crash from *abort-the-replay*
into *apply what you can and record a structured diagnostic*, which the review layer then surfaces as
"needs review". Nothing can hold such a record today: `ObjectSnapshot`, `Commit`, and `CommitMetadata`
have no validity or diagnostic field, and `ExtraMetadata` is written at authoring time only, never by
the merge machinery. `ValidateCommits` checks hash-chain integrity only — nothing semantic.

The insertion points already compute every boolean required: the four-arm branch itself, and
`MarkDeleted`, which already knows which entity lost which reference.

**Note a live semantic while you are here:** delete is not final. A later-timestamped change that
supports creation resurrects a tombstoned entity (`:87-91`). Defensible as add-wins, currently
implicit, and worth deciding deliberately.

---

## Needs adding — application layer, not Harmony

### 7. CRDT → full `.fwdata` materialization

**This is the biggest concrete build item, and it is not in Harmony.**

`CrdtFwdataProjectSyncService` *reconciles two existing projects* — it has `Sync`, `Import`,
`SyncDryRun`, `ImportDryRun` (`:22-37`), all of which assume a `.fwdata` already exists. Producing a
**complete `.fwdata` from a CRDT project** is a different operation, and the write path has known
holes — e.g. `FwDataMiniLcmApi.cs:615` throws `NotSupportedException("Morph types cannot be created in
fwdata; they are predefined")`.

Your workflow — *"make these changes and then give me the full fwdata"* — needs this. It belongs in
`FwLiteProjectSync`, alongside the existing dry-run infrastructure.

**Note the useful precedent:** `DryRunMiniLcmApi` already exists and records what *would* have been
written. That is the right shape to reuse.

### 8. Everything else you named

Proposal storage, review queue, approval state, conversation, permissions — all application layer,
all outside Harmony. Comment threads already ship (`LcmCrdt/Changes/Comments/`), and Lexbox already
has orgs, projects, users, and a permission service.

---

## Summary table

| # | Item | Where | Size | Blocking? |
| --- | --- | --- | --- | --- |
| 1 | Commit removal / revert | Harmony (or none, via file copy) | Small — inverse of existing splice | No — file copy works today |
| 2 | Content hash of payload | Harmony | Small, backward-compatible | **Only if approval must be tamper-evident** |
| 3 | Converging sequence type | Harmony | Medium | Yes, for 2 grammar fields |
| 4 | Keyed-map modelling for alpha variables | Modelling decision | None | No |
| 5 | Reference-set policy (add/remove-wins) | Harmony + docs | Small | Before 38 classes replicate it |
| 6 | Cross-owner move rule | Harmony | Medium | Before owning hierarchies land |
| **9** | **Deferred diagnostic channel + apply-policy table** | **Harmony** | **Small — the branch already computes the booleans** | **Yes — today one bad change poisons every replay** |
| 7 | CRDT → full `.fwdata` | `FwLiteProjectSync` | **Large** | Yes, for your export workflow |
| 8 | Proposal / review / approval | Lexbox + FwLite | Large but conventional | No |

**Four of the seven core items are small. One (7) is large and lives outside Harmony. Items 1 and 4
may require no code at all.** Item 9 is the one with a live failure mode attached: it is not a missing
feature so much as an existing crash pointed in the wrong direction.

## The thing you got right that changes the plan

*"The CRDT actually makes 'apply this, now unapply it' and 'I am at engine state X' really easy to
compute."*

Confirmed, and stronger than stated: the state-at-any-point machinery is **already built and in
production** (`ObjectSnapshot` + `GetAtCommit`/`GetBeforeCommit`), and the recompute-after-splice
machinery that makes removal tractable is **already built** for late-arriving commits. The expensive
part of "apply then unapply" is the part that already works.
