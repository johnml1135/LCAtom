# Grilling Harmony on ordering — what it would need, and whether it's still a CRDT

Scope note: [ADR 0013](adr/0013-harmony-is-the-change-mechanism.md) has decided Harmony is the change
mechanism. This document does not revisit that. It scopes the one surviving technical objection —
ordering — against Harmony's actual code, not in the abstract.

All file paths are absolute-relative to their repo root. `VERIFIED` = read the code/test directly in
this session. `INFERRED` = reasoned from verified facts but not independently executed/tested.

## 1. How ordering converges today — traced end to end

**VERIFIED.** Convergence in Harmony does not depend on `Order` at all — it depends on commit
ordering, and `Order` merges the way any ordinary field merges: last-writer-wins under that commit
order.

- Every commit carries a `HybridDateTime` (wall clock + logical counter) and a `Guid Id`.
  `CommitBase.CompareKey` is the tuple `(HybridDateTime.DateTime, HybridDateTime.Counter, Id)`
  (`harmony/src/SIL.Harmony.Core/CommitBase.cs:25`), and `CompareTo` orders on it
  (`CommitBase.cs:49-53`). `HybridDateTime.CompareTo` breaks DateTime ties on the logical counter
  (`harmony/src/SIL.Harmony.Core/HybridDateTime.cs:28-35`).
- `DefaultOrder`/`WhereAfter`/`ToSortedSetAsync` (`harmony/src/SIL.Harmony.Core/QueryHelpers.cs:58-71,
  114-128, 138-143`) all sort/filter on that same tuple. This is a **total order**, not a partial one —
  every two commits, however produced, compare unequal in a fixed, replica-independent way (ties are
  broken by `Id`, a `Guid`, so a same-millisecond same-counter double-write still resolves
  deterministically).
- On ingest, `CrdtRepository.AddNewCommits` finds the oldest new commit, pulls every existing commit
  after that point, unions them with the new ones into a `SortedSet<Commit>`, and rewrites the parent
  hash chain across that whole merged, re-sorted range (`harmony/src/SIL.Harmony/Db/CrdtRepository.cs:
  408-422`, `UpdateCommitHashes` at `424-432`). So a late-arriving, "earlier" commit doesn't append —
  it **splices in** and the history is renumbered. This is how Harmony guarantees the same replay order
  on every replica regardless of arrival order.
- Replay is a single linear scan over that sorted set: `SnapshotWorker.ApplyCommitChanges` iterates
  `commits` (already a `SortedSet<Commit>`) and within each commit iterates
  `commit.ChangeEntities.OrderBy(c => c.Index)` (`harmony/src/SIL.Harmony/SnapshotWorker.cs:63-70`).
  Every `IChange.ApplyChange` mutates a **copy** of the previous snapshot's entity
  (`prevSnapshot.Entity.Copy()`, `SnapshotWorker.cs:95`) and a new `ObjectSnapshot` is appended
  (`GenerateSnapshotForEntity`, `SnapshotWorker.cs:207-234`). Nothing here is ordering-specific — it's
  how *every* field converges.
- Critically, `Commit.GenerateHash` hashes only `Id` and `parentHash` bytes
  (`harmony/src/SIL.Harmony.Core/CommitBase.cs:32-40`) — **never the change payload**. The hash chain
  proves commit *sequence* integrity, not content integrity. This matters for §6.

**Concrete outcome, `SetOrderChange` on the same entity from two replicas concurrently** (VERIFIED
mechanism, both Harmony's generic type and LcmCrdt's copy behave identically):
`entity.Order = Order` (`harmony/src/SIL.Harmony/Changes/SetOrderChange.cs:37-41`;
`languageforge-lexbox/backend/FwLite/LcmCrdt/Changes/SetOrderChange.cs:14-18`) is an ordinary field
assignment inside `ApplyChange`. Whichever of the two `SetOrderChange` commits sorts later under
`CompareKey` is applied second and its `Order` value is what every replica converges to — **plain,
deterministic LWW**. The earlier writer's positional intent is silently discarded, exactly as it would
be for a `SetGlossChange` racing another `SetGlossChange`. No crash, no divergence, no corruption — an
ordinary lost update, same failure class as every other scalar field in Harmony.

**Concrete outcome, concurrent inserts between the same two neighbours L and R:** Each replica computes
`(L.Order + R.Order) / 2` locally, independently, from state it believes is current
(`languageforge-lexbox/backend/FwLite/LcmCrdt/OrderPicker.cs:36`, `61` — the same midpoint formula
twice, and the comment at `OrderPicker.cs:24-27` explicitly acknowledges the merge outcome is a coin
flip: *"even if that were the case, there's about a 50/50 chance that that's what actually should
happen"*). If both replicas see the same L/R at insert time, **both compute the identical double**.
`Order` is not a uniqueness constraint — ties are broken by `Id`
(`harmony/src/SIL.Harmony/DataModel.cs:277-281`: `OrderBy(Order).ThenBy(Id)`;
`languageforge-lexbox/backend/FwLite/LcmCrdt/QueryHelpers.cs:39-47`: `ApplySortOrder` does the same
independently). This is not hypothetical — Harmony's own test suite asserts it as intended behavior:
`ConsistentlySortsItems` (`harmony/src/SIL.Harmony.Tests/DefinitionTests.cs:90-106`) creates two
entities with the *same* `Order` value and asserts a fixed, Guid-derived interleaving. **The result
converges** (every replica computes the same final order), but the relative order of the two
concurrently-inserted items is arbitrary — an artifact of Guid comparison, unrelated to either user's
intent, and unrelated to insertion time.

**Precision degeneration — INFERRED, not reproduced here.** Repeated bisection between the same two
neighbours (many inserts landing in the same gap over a project's lifetime) is a known fractional-
indexing failure mode: IEEE-754 doubles have ~52 bits of mantissa, so enough successive `(a+b)/2`
midpoints collapse to identical values, after which the Guid tiebreak becomes permanent and further
distinctions are lost. I did not find or reproduce a concrete failure case in this codebase; flagging as
a real but *unverified-in-this-repo* risk of the status quo.

**A finding not asked for but load-bearing for §6:** Harmony ships a *generic* ordering mechanism
(`IOrderableCrdt`, `SetOrderChange<T>` in `SIL.Harmony.Changes`) that `DataModel.QueryLatest` special-
cases (`harmony/src/SIL.Harmony/DataModel.cs:277-281`). **LcmCrdt does not use it.** LcmCrdt defines its
own, structurally identical but type-unrelated `IOrderableNoId`/`IOrderable`
(`languageforge-lexbox/backend/FwLite/MiniLcm/Models/IOrderable.cs:3-11`), its own
`SetOrderChange<T>` (`LcmCrdt/Changes/SetOrderChange.cs`), and its own tie-break
(`LcmCrdt/QueryHelpers.cs:39-47`). Harmony's generic `IOrderableCrdt` path in `DataModel.cs:277-281`
never fires for any LcmCrdt entity. **Two parallel, duplicate implementations of the same fractional-
order scheme exist in this stack today**, and a fix authored only against Harmony's generic type does
not, by itself, fix the thing that actually drives grammar/lexicon ordering.

## 2. Real options for ordered sequences, assessed against this codebase

| Option | What changes in `IChange`/snapshot/sync | Converges without coordination? | Cost here |
| --- | --- | --- | --- |
| **Status quo** (fractional `double` + LWW) | Nothing — already shipped | Yes, deterministically (§1), but with silent-intent-loss and a precision ceiling | Zero |
| **RGA/Logoot/LSEQ sequence CRDT** | New `IChange` (e.g. `InsertChange<T>`) carrying a dense, comparable *identifier* instead of a raw `double`; `IOrderableCrdt`/entity schema needs an identifier field/type, not just `double Order`; `QueryLatest`'s `EF.Property<double>(...)` sort (`DataModel.cs:279`) becomes a sort over an opaque-but-comparable key, ideally still SQL-orderable via a byte-comparable encoding; every existing project needs a one-time backfill from current `Order` values | Yes — concurrent inserts between the same neighbours get distinct identifiers by construction, no collision. Concurrent *relative order* of two siblings inserted at the same spot is still arbitrary (resolved by site-id/tiebreak in the identifier scheme) — RGA/Logoot don't recover "true" intent either, they only guarantee no precision collision | Largest: new entity metadata, identifier-bloat on repeated concurrent inserts at one spot (well-documented Logoot/RGA cost), full sync/wire-format addition, migration for every `IOrderable`-typed table |
| **Explicit predecessor/successor edge** (`PredecessorId: Guid?` per item, walk to materialize order) | `SetOrderChange`'s `double Order` becomes `Guid? PredecessorId`; read path changes from `ORDER BY Order` to a topological walk (in-memory pass or recursive CTE); tie-break for concurrent children of the same predecessor can **reuse `CommitBase.CompareKey`/`DefaultOrder` verbatim** (`QueryHelpers.cs:114-119`) — no new comparison machinery needed | Yes, same class of guarantee as RGA (leaderless, deterministic tiebreak from existing commit-order primitives), at a smaller code footprint. Must defend against cycles (two concurrent commits each naming the other's item as predecessor) — the scalar `Order` field cannot cycle; a predecessor edge can, and the walk must detect and deterministically break it | Moderate: one field-type swap, a walk/CTE at read time instead of an index sort, cycle-detection is new surface area |
| **"Order is not a CRDT — serialize it"** | Two sub-variants. (a) **Whole-list LWW**: replace N per-item `Order` fields with one `List<Guid>` field on the owner (e.g. `PhPhonData.PhonRulesOrder`), changed by a single ordinary `EditChange<T>` — **zero new Harmony primitive**, this already works today with existing `Change<T>` machinery. (b) **Session lock**: require a held edit-lock/turn before a `SetOrderChange`/list-rewrite on a given sequence is accepted | (a) Still converges leaderlessly, but *worse* than the status quo for partial edits: one whole-list LWW write can silently clobber a concurrent single-item insert anywhere else in the list, not just at the contested position. (b) **No** — a lock is explicit coordination, and Harmony has no lock manager; `ISyncable`/`SyncHelper` is leaderless by design. This is the one option that is honestly *not* a CRDT technique for this field | (a) cheap but strictly worse merge granularity than today; (b) requires a new cross-cutting policy layer outside Harmony's model entirely |

Direct answer to the embedded question: **(a) is a real option that needs zero Harmony changes and is
already strictly worse than the status quo's granularity** — not recommended, but worth naming because
it's what "give up and serialize" concretely looks like in this codebase, and (b) is the one point on
this table where the honest answer to "is it still a CRDT" is no.

## 3. Does feeding-order actually need a sequence CRDT, or something else?

Pushing on this as instructed: **HermitCrab's own engine represents phonological rule order as a flat,
ordered list, not a dependency graph.** `Stratum` holds `_prules: List<IPhonologicalRule>`
(`machine/src/SIL.Machine.Morphology.HermitCrab/Stratum.cs:24`, exposed via
`PhonologicalRules` at `Stratum.cs:105`). `HCLoader` builds that list by iterating
`PhonRulesOS.Where(r => !r.Disabled).OrderBy(r => r.OrderNumber)`
(`FieldWorks/Src/LexText/ParserCore/HCLoader.cs:302`) and adding rules to the stratum in that order.
There is no field, table, or edge anywhere in HermitCrab's C# model, nor in HCLoader, that names "rule
A feeds rule B." Feeding/bleeding is the **linguistic name for what happens when you apply rules from a
list in sequence to a string** — it is emergent from strict, ordered pipeline composition, not a
declared relation between two specific rules. Any two rules in the list can feed or bleed each other
depending on their content; that set of interactions is not statically knowable from the schema (it
would require running the phonology), so there is no fixed pair of rules to put an edge *between*.

Consequence: **feeding-order does not require a new "dependency edge" primitive.** It requires that the
*sequence itself* — `PhonRulesOS`'s order — converges correctly, which is exactly the §2 sequence-CRDT
problem, no different in kind from ordering paragraphs in a document. What feeding-order actually
demands, beyond bare convergence, is that a content edit to rule N never silently perturbs rule N's
*position*, and that a reorder is visible and attributable — both already true of the status quo's
`Order` field; what's missing is precision/collision robustness (§1), not a different relation type.

The one place an explicit relation *would* help is **application-level prediction**: warning a user
"moving rule X after rule Y changes what Y produces on these forms" requires actually running the rule
pipeline, which is exactly the kind of check `docs/hc-grammar-map.md`'s "Silent-loss surface" section
already assigns to Motif/the composer layer, not to the change-representation layer. That's a
validation feature, not a Harmony data-model requirement.

## 4. Index-as-identity and positional Output/Input — do they dissolve?

Testing the hypothesis honestly, field by field.

**`MoAffixProcess.Output` → `Input` (`MoCopyFromInput`/`MoModifyFromInput.ContentRA`) — dissolves
fully.** `docs/api-surface-layer1.md:133-137` already establishes that `ContentRA` is a `rel/atomic`
**reference** (a GUID pointer to a specific object in `Input`), not an index. `HCLoader` converts that
reference to a position string only when compiling to HermitCrab's own rule-text format:
`(copyFromInput.ContentRA.IndexInOwner + 1).ToString(...)` (`HCLoader.cs:1383`) and the identical
pattern for `ModifyFromInput` (`HCLoader.cs:1416`). That numbering is an artifact of the **export
target's** format (HermitCrab's `CopyFromInput`/`ModifyFromInput` rule parts reference input
positionally, an implementation detail of that engine), not of how LibLCM or Harmony stores the
relationship. **Harmony needs nothing new here** — `ContentRA` is already handled correctly by ordinary
reference-field semantics that every `rel/atomic` field already gets. The only sequence-CRDT need in
this pair is on `Input` itself (it's a genuine `owning/seq` — pattern position is real content), which
is the same requirement as §2/§3, not an additional mode. The renumbering hazard on export is entirely a
pre-apply validation concern, and `docs/hc-grammar-map.md:113-121` already assigns it there.

**Alpha variables (index-as-identity) — collapses into the same requirement as feeding, does not
dissolve to "no sequence needed."** `VariableNames` is a **fixed, load-time-computed array**
(`HCLoader.cs:37-41`, 24 entries: α…ω), populated by scanning `IPhRegularRule.FeatureConstraints` — a
*virtual* that walks `StrucDesc` then each RHS's `StrucChange`/context slots in fixed order and assigns
`variables[var] = VariableNames[i]` for each newly-seen constraint (`HCLoader.cs:2007-2009`). This is
never stored data — it is recomputed from scratch, by position, on every load, by code
(`HCLoader.cs`) that this project has already decided is immutable and authoritative
(`docs/adr/0010-hermitcrab-experimentation-is-the-primary-purpose.md`, referenced in
`docs/hc-grammar-map.md:8-11`). No matter what identity scheme Harmony invented internally for "the
constraint currently called α," the moment the project round-trips through `.fwdata` and real FieldWorks
loads it, names are re-derived from position again. So there is nothing to *name* — the fields being
ordered (`StrucDesc`, `StrucChange`, `LeftContext`/`RightContext`) are genuinely positional structural
content (a rewrite-rule pattern, where "first segment" versus "second segment" is the linguistic claim,
like a regex), not arbitrary containers whose members happen to have stable identity. **This does not
support modelling them as a keyed map** — position there *is* the content, exactly as with `PhonRulesOS`.

Net effect on the "three mechanisms" framing from ADR 0013: it was two problems, not three, from
Harmony's point of view. **`MoAffixProcess.Output`/`Input` dissolves entirely** — zero new Harmony
primitive, already handled as an ordinary reference. **Feeding-order and index-as-identity collapse into
one shared requirement** — a correctly-converging ordered-sequence primitive (§2) — because both are
cases where position is genuine content and the failure mode is "silent renumbering/renaming on
concurrent edit," not "wrong storage shape." The silent-renaming *risk* itself doesn't disappear; it
relocates entirely to pre-apply validation at the consuming layer, which `docs/hc-grammar-map.md` and
`docs/api-surface-layer1.md` already require independent of any CRDT redesign.

## 5. Minimum viable change

`docs/adr/0013-harmony-is-the-change-mechanism.md:67-69` frames review/approval as "an application-level
state machine over Harmony commits" — propose → review → accept — which is an async, session-boundary
workflow, not live co-editing. If that framing holds, grammar sequences are edited by one person per
session, and merges happen at sync time between sessions, not mid-keystroke. **INFERRED, not measured**:
I found no telemetry or product documentation confirming actual concurrency patterns; this is read off
the ADR's own stated model, not verified against usage data (see "what I could not verify").

Staged plan, ordered by what's actually blocking:

1. **Ship now, independent of any Harmony change.** Pre-apply validation for the 24-alpha-variable
   ceiling (simulating HCLoader's exact `StrucDesc`→`StrucChange` scan order) and for
   `Input`-reorder-renumbers-`Output` — both already required by `docs/hc-grammar-map.md:111-121` and
   `docs/api-surface-layer1.md:126-137` regardless of what Harmony does. This is where §4's dissolved
   risk actually gets caught.
2. **Cheap, Harmony/LcmCrdt-level fix.** Add a collision/precision guard to `OrderPicker.PickOrder`
   (`languageforge-lexbox/backend/FwLite/LcmCrdt/OrderPicker.cs:8-38`) — detect when a computed midpoint
   equals or nearly-equals a neighbour and rebalance (spread) the affected range. Bounds the one real
   status-quo defect (precision degeneration, §1) without a new primitive.
3. **Cheap, structural fix.** Consolidate Harmony's generic `IOrderableCrdt`/`SetOrderChange<T>` and
   LcmCrdt's duplicate `IOrderable`/`SetOrderChange<T>` (§1, §6) so a fix lands once instead of twice.
4. **Defer.** RGA/Logoot/predecessor-successor sequence CRDT (§2) — justified only if live, concurrent,
   multi-user grammar co-editing becomes an actual product feature. Until then it solves a race that
   session-boundary merging doesn't produce.
5. **Don't build.** A dependency-edge primitive for feeding (§3) — nothing in HermitCrab's own model
   motivates it; the actual gap is prediction/validation, already scoped as an Motif-layer job.

## 6. Blast radius

`SIL.Harmony` is published standalone to nuget.org as a general-purpose "CRDT application library for
C#... build offline first applications" (`harmony/README.md:1-5`), and ships its own unrelated sample
domain (`Word`/`Definition`, `harmony/src/SIL.Harmony.Sample/`). It is **not** an internal LexBox module.
LexBox's default build consumes it as an ordinary versioned `PackageReference Include="SIL.Harmony"`
(`languageforge-lexbox/backend/Harmony.Linq2db.References.props:18-21`); building against Harmony's
source tree directly is an **opt-in dev toggle** (`UseHarmonySource`, same file, lines 1-17), not the
default. So while the two repos are developed in lockstep in practice, LexBox's real build/CI path is a
genuine external-package consumer, and ordinary semver/compatibility discipline applies to any breaking
change to `SIL.Harmony.Changes.SetOrderChange<T>` or `IOrderableCrdt`.

Two reassuring, verified facts bound the risk of a schema-shaped fix:

- **Commit hashes don't cover payload.** `GenerateHash` hashes only `Id` and `parentHash`
  (`harmony/src/SIL.Harmony.Core/CommitBase.cs:32-40`). Changing how `Order`/sequence data is
  represented, or backfilling a new identifier scheme from existing `Order` doubles on
  `RegenerateSnapshots` (`DataModel.cs:234-243`), does not invalidate any existing hash chain.
- **The forward-compat pattern already exists.** `OpaqueChange` preserves unknown `$type` payloads
  verbatim and lets them "become real" once the client understands them
  (`harmony/src/SIL.Harmony/Changes/OpaqueChange.cs:1-27`). A new sequence-order `IChange` subtype is
  just another polymorphic change — old clients on old code degrade to treating it as opaque
  automatically, by the existing mechanism, with no new plumbing.

What's *not* free: existing SQLite databases have a real `Order REAL` column
(e.g. the `Add Order to Complex Form Components` migration,
`languageforge-lexbox/backend/FwLite/LcmCrdt/Migrations/20250115153509_Add Order to Complex Form
Components.cs`). Any new sequence primitive should be additive — a new nullable
column/entity/identifier type alongside the existing `Order`, opt-in per entity type — rather than
replacing the column, both to avoid a breaking migration for every existing project and to avoid forcing
every other `IOrderableCrdt`/`IOrderable` consumer (inside or outside this monorepo) onto a new scheme on
someone else's timeline. And as noted in §1: **a fix authored only against Harmony's generic
`IOrderableCrdt` type does not touch LcmCrdt at all** — `DataModel.QueryLatest`'s ordering special-case
(`DataModel.cs:277-281`) never fires for LcmCrdt entities, which implement their own parallel
`IOrderable` (`MiniLcm/Models/IOrderable.cs`). Whoever does this work has to either fix both places or
consciously retire one of them.

## Three questions a human must decide

1. **Is grammar editing actually single-writer-per-session**, or does ADR 0013's "review/approval as an
   async state machine" framing paper over a real case where two linguists edit the same rule list
   before syncing? Section 5's staged deferral of the RGA/Logoot investment is only safe if this holds;
   I could not verify it against usage data.
2. **Where should the fix live** — Harmony's generic, published `IOrderableCrdt`/`SetOrderChange<T>`
   (benefits every Harmony consumer, wider blast radius, real semver discipline required) or only
   LcmCrdt's private duplicate (narrower, faster, but perpetuates two parallel implementations of the
   same scheme, §1/§6)?
3. Given §4's finding that `Output`/`Input` needs **zero** Harmony change and alpha-variables need only
   the *same* sequence-convergence primitive as feeding (not a third mode), is there still appetite for
   the larger RGA/Logoot/predecessor-edge investment, or does the staged MVP (validation + a precision
   guard) close the surviving ADR 0013 objection well enough to stop blocking?

## Confidence and what I could not verify

**Confidence: medium-high** on §1, §2, §4, §6 — these are traced directly against code and, for the
convergence claims in §1, against a passing test (`ConsistentlySortsItems`,
`harmony/src/SIL.Harmony.Tests/DefinitionTests.cs:90-106`) that encodes the exact behavior described.
**Medium** on §3 — the "no dependency graph exists" claim rests on `Stratum.cs`'s list declaration and
`HCLoader.cs`'s ordered iteration; I did not read HermitCrab's full rule-application loop
(`Morpher.cs`, the synthesis/analysis pipeline) end to end to rule out an internal dependency structure
elsewhere in that engine. **Medium** on §5 — the single-writer-per-session premise is read off ADR
0013's own stated design intent, not measured usage.

Could not verify, explicitly:

- Real-world concurrency patterns for grammar editing in FwLite/PanGloss sessions (no telemetry or
  product-usage evidence available in these repos).
- The actual population/audience of the published `SIL.Harmony` NuGet package outside LexBox — the
  README and package badge imply a public audience, but I have no way to enumerate external consumers
  from this checkout.
- Whether FLEx's UI already lets grammar authors think of `MoAffixProcess.Output`'s `ContentRA` as
  positional in practice (versus the schema's reference semantics) — matters for whether §4's dissolve
  also holds at the *authoring UX* layer, not just the storage layer; not checked.
- HermitCrab's full rule-application code path beyond `Stratum.cs`'s list declaration (see confidence
  note above).
- The fractional-indexing precision-collision failure mode (§1) is standard literature, not something I
  reproduced or found a specific bug report for in this codebase.
