# Should Motif use Harmony? — two alternate proposals

*2026-08-01. Written in response to "look at Harmony — we should use that." This is that assessment.
Scope: lexical and grammar. **Text and analysis are deliberately excluded** — see [scope](#scope) below.*

## Answer in brief

**Yes, we should use Harmony — for FwLite, where it already is and where it is the right tool.**

**No, we should not use it as the change mechanism for the FieldWorks-hosted grammar path.** The
reasons are runtime-level and semantic-level, not preference:

1. Harmony is `net10.0`-only because its core dependency is EF Core 10. FieldWorks is `net48` until
   its Avalonia migration completes. Harmony cannot be loaded into the process that owns the data.
2. Harmony has no effect model. It records what a change *intends*; it has no read-back of what the
   engine actually *did*. PR-style review is approval of a transition, and there is nothing in
   Harmony for an approval to bind to.
3. Harmony's authority model is a materialized SQLite projection. FieldWorks' authority is the live
   `LcmCache` and `.fwdata`. Running both over the same fields is the dual-authority failure our own
   research already rules out.

What we *should* take from the "use Harmony" instinct is real and worth saying plainly: **the two
change vocabularies should converge even though the substrates cannot.** That is proposal 2.

## Scope

Lexical and grammar only. The manifest enumerates the text and analysis classes and marks them out of
scope with the reason `not-domain-reachable` — `Segment` (10 rows), `WfiAnalysis` (9),
`WfiMorphBundle` (5), `WfiWordform` (4), `Text` (6), `CmAgent` (7), `StTxtPara` (7), all `out`. Only
eight text-adjacent rows are in scope (`StText` 4, `WfiWordSet` 3, `StPara` 1). Text mutation needs its
own bounded context and a text-occurrence anchor contract; both are deferred and neither is costed
here.

---

## The two systems side by side

Both systems represent "a change to linguistic data" as JSON. They are not variants of one design —
they answer different questions, for different hosts, with different authorities.

| | **LcmCrdt / Harmony** (FwLite) | **Motif Change Set** (FieldWorks) |
| --- | --- | --- |
| **Unit** | `IChange`, applied within a `Commit` | `OperationEnvelope`, applied within a `Proposal` |
| **Discriminator** | `$type`, required as the literal *first* JSON property | `kind`, e.g. `lexical/sense/setGloss` |
| **Vocabulary shape** | Open set of app-defined classes; 36 bespoke + 2 generic in 1,298 LOC | Closed, versioned catalog of **10 primitive verbs**; everything else is a Layer-1 composer |
| **Target model** | MiniLcm — 13 entity types, lexical only | LibLCM objects — 180 classes, 473 in-scope fields of 898 |
| **How a field edit is expressed** | `JsonPatchChange<T>` wrapping RFC 6902 JSON Patch (index paths forbidden) | `set` / `clear` with an `after` value; no index paths by construction |
| **Preconditions** | none | none — **no `before` field**; both are declarative, and this is why they are compatible |
| **Identity** | `Guid EntityId` | 22-char base64url of 128 bits, network-order mapped to a GUID |
| **Ordering** | Hybrid logical clock, total order `(DateTime, Counter, Id)` | Authored array order; `requires` forms a prerequisite **DAG** |
| **Content integrity** | **None.** Hash is XxHash64 over `(Id, ParentHash)` — the payload is never hashed | RFC 8785 canonical JSON → SHA-256 **intent digest**; `proposalId` frozen and separate |
| **What changed, actually** | **Nothing.** The snapshot *is* the state; there is no effect record | **Expected effects** read back from the engine: `(canonicalId, field, before, after, cause)` + effect digest |
| **Dry run** | none — `AddChange` applies synchronously; `SnapshotWorker` is `internal` | `ProposalDryRunner.Run` → effects + `BoundDryRunAnchor`; apply refuses without one |
| **Drift** | n/a | four classes: identical / same-nature-wider-scope / changed-values / changed-meaning |
| **Unknown change types** | `OpaqueChange` — round-trips losslessly, applies once understood | strict closed parsing; unknown `kind` is rejected |
| **Conflict reporting** | none — `SnapshotWorker` has no logger | typed diagnostics with stable codes; severity distinguishes dangling vs engine-nulled |
| **Storage** | `jsonb` column in SQLite, keyed `(CommitId, Index)` | content-addressed files (`ProposalStore`), objects + manifests |
| **Applied record** | the commit log itself | `AppliedLogEntry` in `LexDb.Resources` — **inside the `.fwdata`** |
| **Merge model** | deterministic replay of a totally-ordered log | single writer, one LibLCM unit of work; Chorus merges between people |
| **Runtime** | `net10.0` only (EF Core 10) | `netstandard2.0` — loads in `net48` and `net10.0`; **no EF** |
| **Registration** | hand-typed in `LcmCrdtKernel.ConfigureCrdt` | generated from `MasterLCModel.xml` (ADR 0014) |

The row that decides the architecture is **"what changed, actually."** Harmony answers "what is the
state now." Motif answers "what did this change do, and is that still what you approved." A pull
request is the second question.

---

## Proposal 1 — one system: adopt Harmony as the change mechanism

Fold Motif's operation vocabulary into Harmony's `IChange`, extend LcmCrdt's model to grammar, and
make the CRDT the single history for both products. This is the strongest form of the suggestion, and
it has real merit.

**In its favour**

- One history, one sync protocol, one review surface, one thing to maintain, one thing to explain.
- Harmony gains a second serious consumer, which is the usual justification for keeping a framework.
- FwLite gains grammar, so grammar work reaches mobile and offline users.
- The vocabularies genuinely fit: our own analysis found **412 of 473 fields (87%) commute natively**,
  and 353 of the 412 mutable fields map onto mechanisms Harmony already ships generically. Motif
  arrived at "declarative, no preconditions" for reviewability reasons and landed on exactly the shape
  CRDTs require. That is a convergence of designs, not a coincidence.
- Harmony already solves things Motif does not: `OpaqueChange` forward-compatibility, a working sync
  protocol, and resources/attachments wired in production.

**Against**

- **Runtime.** Harmony is `net10.0`-only and cannot be made `netstandard2.0` — EF Core 10 is the
  floor. FieldWorks is `net48` for the duration of the Avalonia migration. The change mechanism cannot
  live in a runtime the data's owner cannot load.
- **No effect model, and it is not a small addition.** Approval binds to a transition — `before` and
  `after` over the footprint plus the engine's own cascade. Harmony has no read-back, no expected
  effects, no drift oracle, and no content hash at all: `CommitBase.GenerateHash` covers `Id` and
  `ParentHash` only, so "I approved commit X" is not bound to what X contains. Content hashing is
  `harmony-additions-needed.md` item 2 and is unbuilt.
- **No dry run.** `AddChange` applies synchronously; the non-persisting apply path exists but
  `SnapshotWorker` is `internal` with `InternalsVisibleTo` scoped to Harmony's own tests.
- **No proposals, branches, or selective merge.** Confirmed by direct search: no branch concept, no
  held-un-applied state for a *known* change type, no per-change status field, no commit removal API.
  All of it would be new.
- **Dual authority.** Harmony's state is a materialized SQLite projection. FieldWorks' is the live
  cache. Our own research forbids an uncoordinated dual-peer mode over the same property set, and this
  would be exactly that unless FieldWorks stops being authoritative — which is a 20–50 engineer-year
  programme by our own estimate.
- **You still need the lowering.** LibLCM cannot execute an `IChange`. Applying to FieldWorks requires
  a LibLCM lowering regardless, so this proposal does not remove a system — it inserts one.
- **It resurrects deleted work.** Generating against MiniLcm types requires the LibLCM↔MiniLcm name
  and shape crosswalk (ADR 0014 decision 3) which "is required and does not yet exist." Targeting
  LibLCM directly deletes that artifact.
- **The grammar residue lands on unbuilt Harmony features.** Phonological rule feeding order and
  cross-owner moves need `HAR-3`, `HAR-5`, and `HAR-6`, none of which have a branch or an issue.

## Proposal 2 — two systems, one shared intent vocabulary

Keep both substrates and make them share the layer that matters.

- **Motif's intent contract** is the public, versioned, digest-bearing surface. It is generated from
  `MasterLCModel.xml` joined to the manifest, targets LibLCM objects, and is what other people and
  other tools consume.
- **The lowered commands** — the LibLCM mutation plan — stay private and output-only. Nobody outside
  needs them; they change whenever a lowering improves, and effect digests are required to stay stable
  under exactly that.
- **Harmony/LcmCrdt stays FwLite's substrate**, unchanged, for the product it was built for: mobile,
  offline, multi-device, no LibLCM. That is where CRDT semantics actually earn their cost.
- **Receipts sync to Lexbox** — see [receipts](#receipts-and-sync) below.

**In its favour**

- Each substrate matches its host's authority. No dual-authority mode, no second merge engine.
- Works on `net48` today: `netstandard2.0`, no EF.
- Effects, drift classes, dry run, receipts, and the applied log already exist and are tested.
- Deletes the crosswalk, and unblocks grammar from `HAR-3`/`HAR-5`/`HAR-6`.
- Chorus keeps doing the between-people merging it already does for FieldWorks projects.

**Against**

- Two vocabularies to keep aligned, and nothing mechanical keeps them aligned. Someone must own the
  correspondence.
- Two review surfaces are possible, and FwLite already ships comment threads
  (`LcmCrdt/Changes/Comments/`). Building a second review store is a duplication to make deliberately,
  not by accident.
- A lexical edit made in FwLite and the same edit made through a Proposal are different records with
  different histories. Explaining that to a user is real work.
- Harmony keeps a single consumer, so the "why does SIL maintain generic CRDT infrastructure" question
  stays open. It is a fair question; it is not answered by adopting Harmony somewhere it does not fit.

---

## Comparison

| | **1 — adopt Harmony** | **2 — two systems, shared vocabulary** |
| --- | --- | --- |
| Works in `net48` FieldWorks | ❌ EF Core 10 floor | ✅ `netstandard2.0`, no EF |
| Approval binds to what changed | ❌ no effect model, no content hash | ✅ effect digest + four drift classes |
| Dry run before apply | ❌ would be new | ✅ built |
| Proposals held un-applied | ❌ would be new | ✅ built |
| Single authority per project | ❌ dual authority, or a multi-year rewrite | ✅ FieldWorks owns its cache |
| Grammar reaches mobile/offline | ✅ | ❌ desktop only |
| One history to explain | ✅ | ❌ two |
| Needs the MiniLcm↔LibLCM crosswalk | ✅ yes, unbuilt | ❌ not needed |
| Blocked on unbuilt Harmony features | `HAR-2/3/5/6/7` | none |
| Removes a system | ❌ inserts one — LibLCM still needs a lowering | ❌ keeps two, honestly |

## Recommendation

**Proposal 2, with the convergence taken seriously.**

The vocabularies should converge; the substrates cannot. Concretely:

1. **One intent vocabulary.** Motif's ten primitive verbs are a clean match for Harmony's generic
   mechanisms — `set`/`clear` → `JsonPatchChange<T>`, `create`/`delete` → `CreateChange<T>`/
   `DeleteChange<T>`, `addRef`/`removeRef` → the reference change classes. Where FwLite and Motif
   express the same edit, they should use the same verb name and the same field semantics, and the
   generated catalog should be the source both draw from.
2. **Two lowerings.** The same verb lowers to a LibLCM unit of work in FieldWorks and to an `IChange`
   in FwLite. That is the only honest way to serve two engines.
3. **Harmony work is dropped from the critical path, and nobody loses anything they asked for.**
   `HAR-3`, `HAR-5`, `HAR-6`, and `HAR-7` were not requested by FwLite or its users — they were
   *necessitated by the original plan*, which routed grammar through the CRDT and so had to make the
   CRDT safe for grammar's ordering, reference, and reparenting semantics. Remove that routing and the
   need goes with it. They remain available as genuine FwLite improvements if a FwLite requirement ever
   asks for them, but they stop being anybody's blocker. `plan-cross-repo.md`'s milestone ladder
   currently sequences grammar work behind them and should be corrected.

This is not "we looked at Harmony and said no." It is "Harmony is the right substrate for the product
it serves, and the wrong substrate for a `net48` host with a different authority — and the part worth
sharing is the vocabulary, which we should share deliberately."

---

## What we owe either way

### Two-layer contract

The **higher intent contract is public and will be depended on**; the **lowered command plan is
private and output-only**. That asymmetry is already load-bearing in the design: drift comparison is
over effects, never over the mutation plan, precisely so that an improved lowering produces a
different plan and identical effects without training reviewers to dismiss warnings.

This also settles a claim worth retiring: the format is *not* private. `SIL.Motif.Contract` is
declared LibLCM-free specifically because "it is consumed by non-.NET runners (Python/Rust) as the
normative description of Change Set shape." ADR 0007 exists solely to make digests reproducible across
languages. ADR 0015 records that renaming a field changed another repository's committed domain model.
And AI agents author change sets, which our own design already treats as untrusted input requiring
strict closed parsing. We own the format; we do not get to treat it as ours alone.

### Receipts and sync

Receipts must be durable and shareable. The applied log is thin by design —
`(proposalId, formatVersion, timestamp, user, intentDigest, description)` — it records *that*
something applied, not what it did. The effects live in the `Receipt`, which today is returned and
never stored anywhere durable.

**Lexbox is the right home.** It already has organisations, projects, users, and a permission service,
and it is already the sync authority for FwLite. Proposals and receipts are immutable,
content-addressed documents with frozen identities — they need an object store and an HTTP API, not a
merge engine, so no CRDT is required to share them. Sharing should be **optional per project**, so a
linguist working alone is not obliged to publish.

Review state — comments, approvals, decisions — is the mutable part, and that is an ordinary server
database unless offline review becomes a requirement.

---

## Evidence

All claims above were read from source in the repositories listed, not from prior documentation:

- `harmony/src/SIL.Harmony{,.Core}` — ~3,950 LOC; `CommitBase.GenerateHash`, `SnapshotWorker`,
  `PeekThenConcreteChangeConverter`, `OpaqueChange`, `ISyncable`; `src/Directory.Build.props` targets
  `net10.0`.
- `languageforge-lexbox/backend/FwLite` — `LcmCrdt` 28,533 LOC of which 20,158 are EF migrations;
  `LcmCrdt/Changes` 38 files / 1,298 LOC; `MiniLcm` 9,914 LOC with no Harmony reference;
  `IMiniLcmApi` implemented by both `CrdtMiniLcmApi` and `FwDataMiniLcmApi`.
- `motif/src` — `ProposalDryRunner`, `ProposalApplier`, `FootprintProbe`, `CacheReusability`,
  `ProjectAppliedLog`; `Contract`/`Model` `netstandard2.0`, `Runner` `netstandard2.0;net10.0`, no EF
  anywhere.
- `motif/manifest/liblcm-inventory.tsv` — 898 rows, 180 classes, 473 in scope; text/analysis marked
  `out` / `not-domain-reachable`.
- `liblcm/src/SIL.LCModel` — `LcmCache.CreateCacheCopy`, `BackendProviderType.kMemoryOnly`,
  `NonUndoableUnitOfWorkHelper`.
- `FieldWorks/Src` — `HCLoader.Load(LcmCache, logger)`, `GenerateHCConfig`, SDK-style `net48` projects
  with `PackageReference`.
- `PanGloss/rust/crates` — `pg-ffi` `cdylib` "for P/Invoke from net48"; `pg-snapshot` GUID-keyed
  interchange format.

### Open questions this report does not answer

- The wall-clock cost of `LcmCache.CreateCacheCopy` into `kMemoryOnly` (ADR 0016 depends on it, and
  that API has zero callers in `liblcm` or `FieldWorks`).
- Whether `System.Text.Json` lands cleanly in the FieldWorks `net48` dependency graph, which today has
  no STJ reference at all.
- Whether Chorus merges `LexDb.Resources` sanely (above).
- Whether the shared-vocabulary convergence in the recommendation is worth a generated cross-check, or
  whether human review of the correspondence is sufficient.

---

## Standing risk — Chorus does not merge the applied log

**Neither proposal creates this and neither proposal fixes it.** It is recorded here because it bounds
what "collaboration" can currently mean, whichever proposal is adopted.

`ProjectAppliedLog` writes into `LexDb.Resources`, inside the `.fwdata`. Chorus transports and
three-way-merges the `.fwdata` with generic field-level rules and no knowledge of what a `proposalId`
or an `intentDigest` means. Two linguists applying different proposals offline and then synchronising
means Chorus merges our approval records; duplicate, dropped, or field-crossed entries are all
plausible and none are currently detected.

The consequence is a limit, not a defect introduced by this work: **the applied log is reliable
per-machine and unreliable across a Send/Receive boundary**, so approval continuity cannot today be
shared between collaborators through the project file. That is precisely the gap
[receipts syncing to Lexbox](#receipts-and-sync) is meant to close — receipts held outside the
`.fwdata`, in a store that understands what they are, rather than merged blindly inside it.

Worth testing with a disposable project to learn exactly how it fails, but the fix is the Lexbox
receipt store either way, not a Chorus merge driver.
