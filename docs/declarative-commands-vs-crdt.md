# Declarative commands vs. CRDT changes — a point-by-point mapping

*2026-07-27. Can Motif's declarative operation vocabulary fold into Harmony's `IChange` model?
Grounded in the 473 in-scope manifest rows and both codebases, not in the abstract.*

## Headline

**Yes, almost entirely — and the residue is five fields.**

| Measure | Count | Share |
| --- | --- | --- |
| In-scope fields | 473 | 100% |
| `ComparisonClass = unordered` — commutes natively | **412** | **87%** |
| `positional` — needs a sequence CRDT | 56 | 12% |
| `index-as-identity` — index is a *name* | 3 | 0.6% |
| `feeding` — order carries semantics | 2 | 0.4% |

*(Recounted from `manifest/liblcm-inventory.tsv`, `Scope=in`.)*

The "ordered grammar breaks CRDTs" objection — the one thing that survived ADR 0013 — applies to
**5 of 473 fields**. It is real, and it is 1% of the surface.

## Why the fold works at all: no preconditions

`src/SIL.Motif.Contract/Model/OperationEnvelope.cs:62-99` carries `Kind`, `EntityId`, `Target`, **`After`**,
`Placement`, `DependsOn`, and provenance fields. There is **no `Before`**. Motif operations never say
*"if the current value is X, change it to Y."* They say *"make it Y."*

That is the single property that makes them CRDT-compatible. A precondition cannot survive concurrent
application without coordination; a declarative assignment can. Motif arrived at "declarative, no
preconditions" for reviewability reasons and landed on the shape CRDTs require. **This is a
convergence of designs, not a collision.**

## Concept-level mapping

| Motif concept | Harmony equivalent | Fold verdict |
| --- | --- | --- |
| Operation (`OperationEnvelope`) | `IChange` (`Changes/Change.cs:14-33`) | **Direct.** Both are semantic, serializable, polymorphic-by-`$type`, applied to one entity. |
| Change set (ordered operation array) | One `Commit` holding many `IChange`s (`DataModel.AddChanges`) | **Direct 1:1.** A commit is the atomic unit; a change set is the atomic unit. |
| Atomic apply (whole set or nothing) | Commit is applied as a unit | **Direct.** Motif used LibLCM's UOW; Harmony uses its own commit boundary. |
| `Kind` string (`lexical/sense/setGloss`) | `IPolyType.TypeName` (`"jsonPatch:Sense"`, `"delete:Sense"`) | **Direct.** Both are a registry-keyed discriminator. Naming differs; concept identical. |
| `CanonicalId` (22-char base64url of a GUID) | `Guid EntityId` | **Direct**, lossless both ways. Motif's is a display/transport encoding of the same 128 bits. |
| `After` payload | `JsonPatchChange<T>.PatchDocument`, or typed change fields | **Direct.** See the shape table below. |
| `Placement` (before/after/index anchor) | `BetweenPosition` + `OrderPicker` | **Partial.** Same intent; convergence differs (see residue). |
| `DependsOn` (operation dependencies) | Change order within a commit | **Weak.** Harmony relies on within-commit ordering; there is no declared dependency graph. Rarely load-bearing given no preconditions. |
| `Rationale`, `Confidence`, `Provenance`, `Extensions` | `CommitMetadata.ExtraMetadata` (`Dictionary<string,string?>`, documented as "application specific metadata") | **Direct.** A ready-made home; no Harmony change needed. |
| Intent digest (RFC 8785 + SHA-256 over content) | `CommitBase.GenerateHash` — XxHash64 over `Id` + `parentHash` **only** | **GAP.** Harmony's chain gives ordering integrity, not content integrity. See "the one real gap". |
| Assessment ("what would change") | `GetBeforeCommit` / `GetAtCommit` / `GetSnapshotsAtCommit` | **Partial.** Harmony computes this over *committed* history; there is no uncommitted dry-run. |
| Receipt ("what did change") | `Commit` + `ObjectSnapshot` | **Direct.** |
| Applied-change log | The commit log itself | **Direct** — and Harmony's is better, being the storage mechanism rather than a side record. |
| Operations Motif can't interpret | `OpaqueChange` — preserves raw JSON, round-trips, applies once the type is known | **Harmony wins.** Motif had no equivalent. |

## Verb × shape mapping — where the 473 fields land

| Motif verb pair | Fields | Field shape | CRDT type required | Harmony mechanism today | Fits? |
| --- | --- | --- | --- | --- | --- |
| `set` \| `clear` | **220** | `basic` scalars, `rel/atomic` | LWW register | `JsonPatchChange<T>` — **generic**, no per-field class | ✅ **Free.** |
| `create` \| `delete` | **99** | `owning/atomic`, `owning/col` | Tombstone / add-once | `CreateChange<T>` (per-type, must construct) + `DeleteChange<T>` (generic, sets `DeletedAt`) | ✅ Delete free; create needs one class per type |
| `addRef` \| `removeRef` | **34** | `rel/col` | OR-Set (add-wins or remove-wins) | Hand-written per type (`AddSemanticDomainChange`, `RemoveSemanticDomainChange`) | ⚠️ Works; **policy choice undocumented** |
| `create`\|`delete`\|`move`\|`reparent` | **32** | `owning/seq` | Sequence + move-between-owners | `SetOrderChange<T>` + hand-written (`MoveSenseToEntryChange`) | ⚠️ Move-between-owners is the classic CRDT cycle hazard |
| `addRef`\|`removeRef`\|`move` | **27** | `rel/seq` | OR-Set + sequence | `SetOrderChange<T>` + `ReorderSensePictureChange` | ⚠️ Order convergence is the residue |
| `n/a` (read-only / derived) | 61 | — | — | — | ✅ Nothing to map |

**353 of the 412 mutable fields (86%) map onto mechanisms that already exist and are generic.** The
220 `set|clear` fields in particular need *zero* new change classes — `JsonPatchChange<T>` covers them
all, per type not per field.

## Structural shape mapping

| `Kind / Card` | Fields | CRDT primitive | Notes |
| --- | --- | --- | --- |
| `basic` (scalar) | 209 | LWW register | The easy majority. `JsonPatchChange`. |
| `owning / atomic` | 69 | Nested register + tombstone | "Create into occupied slot implies detach" needs a documented rule. |
| `rel / atomic` | 57 | LWW reference | Dangling-reference policy needed; LibLCM gives this free, CRDT store does not. |
| `owning / col` | 39 | OR-Set of owned children | Add-wins vs remove-wins is a real choice. |
| `rel / col` | 38 | OR-Set of references | Same, plus referential integrity (`GetReferences`/`RemoveReference`). |
| `owning / seq` | 33 | Sequence CRDT | Only *some* are semantically ordered — most are display order. |
| `rel / seq` | 28 | Sequence CRDT over references | Same. |

## The residue — what genuinely does not fold

Five fields, in two kinds, and they are **not** the same problem.

**1. `feeding` (2 fields) — order is meaning.** Phonological rule order encodes feeding/bleeding: rule
7's output is rule 8's input. A merge that reorders rules silently changes what the grammar means.
This needs a sequence CRDT that converges deterministically — and per the ordering research,
HermitCrab itself stores rule order as a flat `List<IPhonologicalRule>`, so it needs a *converging
sequence*, not a dependency graph.

**2. `index-as-identity` (3 fields) — the index is a name.** Alpha variables are assigned by
first-appearance scan with a hard 24-per-rule ceiling; position *is* the identifier. This is not an
ordering problem at all — it is a keyed map wearing an array's clothes.

**Harmony already knows this.** `LcmCrdt/Changes/JsonPatchChange.cs` — `JsonPatchValidator` rejects
any patch path containing an index, with the comment: *"prevents the use of indexes in the path, as
this will cause major problems with CRDTs."* It throws `NotSupportedException` on `remove` at an
index. **The rule Motif derived from HCLoader is already enforced in Harmony's code.** Two teams
reached the same conclusion independently.

## The one real gap

**Content integrity.** `CommitBase.GenerateHash` hashes `Id.ToByteArray()` + `parentHashBytes` with
XxHash64 — *the change payload is never hashed*. So the commit chain proves **ordering**, not
**content**. "I approved commit X" is not cryptographically bound to what X contains.

Motif's intent digest (RFC 8785 canonical JSON + SHA-256) did bind content, and
`stage2-change-management.md` built effect-scoped, drift-invalidated approval on top of it. If that
requirement survives, this is the one thing that must be added to Harmony rather than folded into it.
It is cheap now and awkward after grammar change classes exist.

**Second, smaller gap:** no uncommitted dry-run. `GetBeforeCommit`/`GetAtCommit` replay committed
history; there is no "apply these changes hypothetically and show me the effect" path, because
`ChangeContext` is DB-backed. Whether that matters depends on whether review happens before or after
a proposal enters the log.

## Verdict

**Fold Motif's vocabulary into Harmony's `IChange`; do not port its machinery.**

- The **verb vocabulary** (`set|clear`, `create|delete`, `addRef|removeRef`, `move|reparent`) is a
  clean superset-free match to `JsonPatchChange` / `CreateChange` / `DeleteChange` / `SetOrderChange`
  plus per-type reference changes. 86% needs no new mechanism.
- The **manifest** keeps its value: it is the type system that says which of the 473 fields gets which
  treatment, and it already carries `ComparisonClass` — the exact column that decides which CRDT
  primitive applies.
- The **digest scheme, envelope, parser, registry, and runner** do not fold and should not be ported.
  Harmony has working equivalents for all but content-hashing.
- The **residue is 5 fields**, and one of the two kinds (index-as-identity) is arguably a modelling
  error to be fixed rather than a CRDT feature to be built.

The honest summary for the grill: *the declarative command design and the CRDT change design are the
same design, discovered twice. One has a commit log, sync, and a maintainer; the other has a
classified model inventory and a grammar map. Take the inventory to the log.*
