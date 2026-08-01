# Plan — work in the `harmony` repository

*Six items. Milestones are defined in [plan-cross-repo.md](plan-cross-repo.md); this file owns
`HAR-*` item status and evidence.*

**Where this plan lives and where the work lands.** The plan is authored here because the analysis that
produced it is here ([harmony-additions-needed.md](harmony-additions-needed.md),
[inventory-harmony.md](inventory-harmony.md),
[inventory-harmony-generation-surface.md](inventory-harmony-generation-surface.md),
[inventory-harmony-conflict-reporting.md](inventory-harmony-conflict-reporting.md)). The **code** lands
in the `harmony` repository, as pull requests there. Until each item is delivered, this file is the
source of truth for its scope; after delivery, harmony's own tests are.

**The one rule that governs every item below.** Harmony core gains **primitives only, never domain
vocabulary** (ADR 0014 decisions 4 and 5). It knows about commits, snapshots, and changes — not about
`IFsFeatStruc`. The acceptance test is mechanical: grepping harmony for LibLCM or MiniLcm type names
must return nothing after every item here ships.

**Numbering.** `HAR-n` is item *n* of [harmony-additions-needed.md](harmony-additions-needed.md), kept
so the evidence behind each survives. **There is no `HAR-4`, `HAR-8`, or `HAR-9`** — those were
reassigned (4 → `CRDT-3`, 8 → `CRDT-8`, 9 → the application layer). Do not renumber.

## Status summary

| Item | Milestone | Size | Blocking? | Status |
| --- | --- | --- | --- | --- |
| `HAR-7` — deterministic materialization result/diagnostic channel and strict atomic-group disposition | **M1**, completed for M4 use | Medium | **Yes** — replay robustness and fail-closed groups | Not started |
| `HAR-3` — converging sequence type | **M3** | Medium | Yes, for 2 grammar fields | Not started |
| `HAR-5` — reference-set policy | **M3** | Small (docs + possibly semantics) | Before 38 classes replicate it | Not started |
| `HAR-6` — cross-owner move rule | **M3** | Medium | Before owning hierarchies land | Not started |
| `HAR-2` — content hash of the change payload | **M4** | Small, backward-compatible | Yes for cross-process checks, approval, and authorization | Not started |
| `HAR-1` — commit removal / revert | Unscheduled — may need no code | Small | No — the file-copy pattern works today | Not started, recommended deferred |

Nothing here has started. No branch, issue, or PR exists in the harmony repository for any item.

## Do not build these — they already exist

Re-stated at the top of the plan because the most expensive mistake available here is rebuilding
something Harmony ships. Full table with evidence in
[harmony-additions-needed.md](harmony-additions-needed.md).

`ObjectSnapshot` already stores the **full prior entity** plus `References`, `EntityIsDeleted`,
`CommitId`, `IsRoot` — that is strictly more than a diff. `GetAtCommit` / `GetBeforeCommit` /
`GetAtTime` read it at any point. `AddChanges` makes a set of changes one atomic commit. `OpaqueChange`
round-trips changes this client cannot interpret. `CommitMetadata.ExtraMetadata` carries provenance.
`Resource/` carries attachments, wired in production. **"Apply it, then unapply it" and "I am at engine
state X" are already built.**

---

## `HAR-7` — deterministic materialization diagnostics and atomic-group disposition — M1/M4

**Not a missing feature. An existing crash pointed in the wrong direction.**

Two earlier claims in this repository were wrong and are corrected here so the item is not
mis-scoped:

1. *"A CRDT has no canonical moment at which to refuse a change."* True of CRDTs generally, **false for
   Harmony.** `DataModel.UpdateSnapshots` takes a `SortedSet<Commit>`, takes the oldest affected commit,
   calls `DeleteStaleSnapshots`, and replays forward; `CommitBase.CompareKey` is
   `(HybridDateTime.DateTime, Counter, Id)`. Every replica evaluates the same commits in the same
   sequence, so a decision taken during apply **is** deterministic.
2. *"Harmony needs loud failure added."* It already has it. `SnapshotWorker.cs:84-85` comments *"this
   will (and should) throw if the change doesn't support NewEntity"*, and the exception names the change
   type, `CommitId`, and `EntityId`.

**The actual defect.** That throw happens inside a transaction, during a replay that an unrelated
late-arriving commit may have triggered, and `AddRangeFromSync` catches only `DbUpdateException`. One
bad change therefore does not fail *itself* — it fails **every commit in the batch, on every snapshot
regeneration, permanently**. `RegenerateSnapshots` re-hits it on every rebuild.

**Scope.** Convert abort-the-replay into apply-what-you-can-and-record-a-structured-diagnostic. The
insertion points already compute every boolean required: the four-arm branch in `SnapshotWorker`
(`:76-110`) and `MarkDeleted` (`:124-148`), which already knows which entity lost which reference.
Nothing can hold such a record today — `ObjectSnapshot`, `Commit`, and `CommitMetadata` have no
validity or diagnostic field, and `ExtraMetadata` is written at authoring time only, never by the merge
machinery. `ValidateCommits` checks hash-chain integrity only, nothing semantic.

**Also decide while here: delete is not final.** `SnapshotWorker.cs:87-91` resurrects a tombstoned
entity when a later-timestamped change supports creation. Defensible as add-wins. Currently implicit.
Decide it deliberately and document it in the apply-policy table below.

**Deliverables**

1. A persisted diagnostic record with a commit id, entity id, change type, and a stable reason code.
2. The four-arm apply policy written down as a table, with the silent arms named and each arm's
   diagnostic decided.
3. A **loud-by-default channel that does not depend on unbuilt UI** — a log sink, a non-zero exit, a
   counter. This is the condition under which the item is finished.
4. `MarkDeleted`'s cascade emitting a diagnostic per stripped reference.
5. A proof that one Harmony commit can carry Motif's strict atomic-group identity, all-or-none
   disposition, opaque old-client preservation, and payload/provenance binding. If it cannot, expose
   domain-neutral group primitives for Motif's immutable group envelope; do not equate commit
   insertion atomicity with materialization or cross-store durability.

**Acceptance**

- A batch containing one un-appliable change commits every other change and records one diagnostic.
- Regenerating snapshots reproduces the same diagnostic and does not throw.
- A replica that receives the same commits in a different arrival order records the identical
  diagnostic set — the determinism claim above, tested rather than asserted.
- The diagnostic is observable with no UI present.

**Risk if skipped.** This is the only item with a failure already in the field: a sync batch poisoned
permanently by one change. Skipping it means grammar constructs land on top of it.

---

## `HAR-3` — a converging sequence type — M3

**Gap.** `SetOrderChange<T>` assigns `entity.Order = Order`, a last-writer-wins `double`. Two concurrent
inserts between the same neighbours can compute the identical fractional value.

**Scope check, honestly: this is 2 fields of 473.** Phonological rule order, where order encodes
feeding/bleeding. HermitCrab itself stores rule order as a flat `List<IPhonologicalRule>`, so what is
needed is a **sequence that converges deterministically** — *not* a dependency graph. Do not build a
dependency graph.

**Deliverables.** A generic, domain-free sequence primitive with a documented convergence rule and a
tie-break that cannot collide; `SetOrderChange<T>` either reimplemented on it or explicitly retained
for the unordered-adjacent cases with its LWW semantics documented as a choice.

**Acceptance**

- Two replicas that concurrently insert between the same neighbours converge to the same order without
  a coin flip on identical values.
- Three replicas, concurrent reorder + insert + delete, converge.
- No LibLCM or MiniLcm type name appears anywhere in the implementation.

**Consumer.** `MOT-5` maps the 2 `feeding` fields and 3 `positional` fields onto this; `MOT-8` is its
proof against real phonological rule order.

---

## `HAR-5` — reference-set policy — M3

**Gap: documentation, and possibly semantics.** Add-wins versus remove-wins is currently implicit in
each hand-written change class. With 75 reference fields across 38 grammar classes arriving, this must
be explicit, uniform, and documented **before it is replicated 38 times**. Evidence that the shape is
uniform enough to have one policy: `MiniLcm/Models/Sense.cs:30-46` implements
`GetReferences`/`RemoveReference` as exactly three shape rules — owner → delete self, `rel/atomic` →
null it, `rel/col` → filter it.

**Deliverables.** A stated policy per reference shape, the generic change types made to honour it
uniformly, and a test that a hand-written class deviating from it is detectable.

**Acceptance**

- Concurrent add and remove of the same reference resolves the same way on every replica, per the
  documented policy.
- The policy is one sentence a reviewer can apply to a generated diff without reading the
  implementation. That is the actual point of the item.

---

## `HAR-6` — cross-owner move rule — M3

**Gap.** `MoveSenseToEntryChange` handles one case by hand. Move-between-owners is the classic CRDT
cycle hazard: two concurrent reparents can produce a cycle or an orphan. A general rule is needed
before grammar's owning hierarchies land — 235 `owning` field declarations exist in
`MasterLCModel.xml`.

**Deliverables.** One documented rule — reject-cycle-on-apply *or* last-writer-wins-on-parent — applied
generically, plus the diagnostic from `HAR-7` when a move is refused.

**Acceptance**

- Two replicas concurrently reparenting A under B and B under A converge, with no cycle and no orphan,
  and the loser is recorded rather than silently dropped.
- The rule is generic: no entity type is named in it.

**Ordering note.** `HAR-6` wants `HAR-7` in place first, so a refused move has somewhere to be
recorded. Both are inside the M1→M3 sequence, so this is satisfied by the ladder, not by an extra edge.

---

## `HAR-2` — content hash of the change payload — mandatory for M4 trust boundaries

**Gap.** `CommitBase.GenerateHash` hashes `Id.ToByteArray()` + `parentHashBytes` with XxHash64 — the
**change payload is never hashed** (`CommitBase.cs:32-40`, verified). The chain proves *ordering*, not
*content*. "I approved commit X" is not cryptographically bound to what X contains.

**Condition.** Needed **only if** approval must be tamper-evident or drift-invalidated. That is an open
question in [grill-decisions.md](grill-decisions.md), and this item is deliberately unscheduled until
it is answered. Do not build it speculatively.

**Cheap now, expensive later, and safe either way.** Because the current hash excludes the payload,
adding a *separate* content-digest field is backward-compatible — existing commit chains stay valid.
And it is much cheaper before grammar change classes exist. If the approval question is still open when
M3 starts, that asymmetry is the argument for doing it anyway.

---

## `HAR-1` — commit removal / revert — likely no code at all

**Gap.** There is no way to remove a commit. `DeleteChange` is a *tombstone* (it sets `DeletedAt`), not
history removal; `DeleteStaleSnapshots` is snapshot GC.

**Why it is cheaper than it sounds.** Harmony **already recomputes state after inserting a commit in
the middle of history** — late commits splice in and the hash chain and snapshots rebuild. Removal is
the inverse of an operation Harmony already performs correctly.

**Recommendation: build nothing.** Copy the SQLite file, apply, read, discard the copy — the same
scratch-copy pattern already designed for `.fwdata`, and far cheaper on SQLite. Add `RevertTo(commitId)`
only if the copy cost shows up in practice. Recorded here so the option is not rediscovered as a
requirement.

---

## Cross-links

- Milestones, dependency edges, and the alignment rules: [plan-cross-repo.md](plan-cross-repo.md)
- What consumes these primitives: [plan-lcmcrdt.md](plan-lcmcrdt.md), [plan-motif.md](plan-motif.md)
- Full evidence with `path:line` citations: [harmony-additions-needed.md](harmony-additions-needed.md)
