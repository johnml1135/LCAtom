# Cross-repo plan — Motif, Harmony, LcmCrdt

*The shared milestone ladder. This file is the **only** place milestones are defined; the three repo
plans reference them and never redefine them.*

The ladder serves the [product architecture](plan-product-architecture.md): a PR-like semantic
collaboration system for humans and AI agents, with grammar as the first customer. Motif owns
Proposal/Check/Review/Decision meaning; Harmony owns domain-neutral replicated history and
materialization primitives; LcmCrdt is the generated collaborative project state; LibLCM remains the
FieldWorks invariant, lifecycle, and compatibility boundary.

LcmCrdt is the target authority for domains promoted to CRDT-native operation. During the
FieldWorks-hosted transition, the process owning the loaded `LcmCache` is the sole live apply
authority. Every cross-boundary record declares `authorityKind` and authority epoch; Chorus and
Harmony never independently merge the same field.

| Plan | Repository | Owns |
| --- | --- | --- |
| [plan-motif.md](plan-motif.md) | **motif** (this repo) | Manifest, classification columns, the name map, the generator, semantic + lowering layers |
| [plan-harmony.md](plan-harmony.md) | **harmony** | CRDT primitives only — never domain vocabulary |
| [plan-lcmcrdt.md](plan-lcmcrdt.md) | **languageforge-lexbox** (`backend/FwLite/LcmCrdt`) | Generated entities, change classes, EF config, registrations, migrations |

Basis: [ADR 0013](adr/0013-harmony-is-the-change-mechanism.md) (Harmony is the change mechanism),
[ADR 0014](adr/0014-generate-the-crdt-layer-from-masterlcmodel.md) (the LibLCM-shaped layer is
generated), and [grill-decisions](grill-decisions.md) D1, D2, D4, D6, D7. The product-level pitch these
plans serve is [motif-overall-plan.md](motif-overall-plan.md).

## Why three plans and not one per repo, independently

Three couplings make independent planning wrong. Each is the reason a milestone below spans repos.

1. **The name map gates every generated line.** The manifest is keyed on *LibLCM* class names; the
   generation target uses *MiniLcm* type names, and they do not correspond — `MorphType` is
   `MoMorphType`, `ComplexFormType` is `LexEntryType`, `SemanticDomain` is `CmSemanticDomain`. The map
   is hand-maintained and derivable from neither source (ADR 0014 decision 3). Until `MOT-1` exists,
   nothing can be generated *into* LcmCrdt at all.
2. **The generator has nothing correct to emit for ordered fields until Harmony has a primitive.**
   `SetOrderChange<T>` assigns a last-writer-wins `double`, so the 2 `feeding` and 3
   `index-as-identity` fields have no convergent target. `MOT-4` cannot emit them before `HAR-3`
   exists, and emitting them wrongly is worse than not emitting them.
3. **A package pin sits between harmony and lexbox.** Lexbox consumes `SIL.Harmony`,
   `SIL.Harmony.Core`, and `SIL.Harmony.Linq2db` pinned at `0.2.1-rc.225`
   (`backend/Directory.Packages.props:112-114`, verified). A new Harmony primitive is invisible to
   LcmCrdt until an rc is cut and the pin bumped. During development,
   `Harmony.{App,Core,Linq2db}.References.props` swap the `PackageReference` for a `ProjectReference`
   under `UseHarmonySource`; `LcmCrdt.csproj:35` imports the Linq2db variant.

## The milestone ladder

| Milestone | motif | harmony | LcmCrdt (lexbox) | Gate |
| --- | --- | --- | --- | --- |
| **M0** — the missing artifact | `MOT-1` name map | — | — | Map covers every class the join reaches for M2's three entities |
| **M1** — two independent tracks | `MOT-2` join build gate, `MOT-3` generator skeleton | `HAR-7` deferred diagnostic channel | — | Injected unmatched `(Class, Field)` key fails the build; one bad change no longer poisons a replay |
| **M2** — regenerate what already ships | `MOT-4` emit possibility-list CRUD | — | `CRDT-1` accept generated output, diff against shipped code | Generated `PartOfSpeech` / `MorphType` / `ComplexFormType` pass LcmCrdt's existing tests unmodified |
| **M3** — primitives, then pin | `MOT-5` map ordered + reference kinds onto the new primitives | `HAR-3` sequence, `HAR-5` reference-set policy, `HAR-6` cross-owner move | `CRDT-2` pin bump, `CRDT-3` keyed-map modelling (item 4) | Each primitive has a two-replica convergence test in harmony; harmony gains zero domain types |
| **M4** — reviewed world equals applied world | `MOT-9` baseline-bound apply, `MOT-10` Proposal/Check/Review/Decision lifecycle | `HAR-7` deterministic refusal + strict atomic-group disposition; `HAR-2` payload binding | `CRDT-9` authority, fencing, save/read-back, recovery | Two agents start at one baseline: one applies atomically, the other gets Drift before mutation; changed inputs invalidate checks and approval |
| **M5** — one grammar construct end to end | `MOT-6` lowering + semantics for construct 1 | — | `CRDT-4` `IMiniLcmGrammarApi`, `CRDT-5` selective bridge/reconciler pair | One complete grammar Proposal converges, round-trips, passes checks and controlled apply, and produces a Receipt |
| **M6** — the rest and ordered residue | `MOT-7` remaining 29 constructs, `MOT-8` ordered-grammar proof | — | `CRDT-6` generated output, `CRDT-7` migrations | Ambiguous feeding order is retained and refused; unambiguous order and keyed alpha variables survive real-project round trips |
| **Conditional export** | — | `HAR-1` commit removal (may need no code) | `CRDT-8` CRDT → brand-new full `.fwdata` | Triggered by a product export requirement; selective compatibility is already mandatory in M5 |

Dependencies as an explicit edge list, so nothing is inferred from the table's reading order. Read
`A → B` as *B cannot start until A is delivered*:

| Edge | Why |
| --- | --- |
| `MOT-1` → `MOT-2` | The join needs the name map to reach a MiniLcm target at all |
| `MOT-2` → `MOT-4`, `MOT-3` → `MOT-4` | Emit only from a joined model that is known complete |
| `MOT-4` → `CRDT-1` | Nothing to diff until output exists |
| `CRDT-1` → `MOT-6` | Do not design construct semantics on an unproven generator |
| `HAR-3`, `HAR-5`, `HAR-6` → `CRDT-2` | The pin can only bump to an rc that carries them |
| `HAR-3`, `HAR-5`, `HAR-6` → `MOT-5` | The generator must target a primitive that exists |
| `HAR-7` → `HAR-6` | A refused cross-owner move needs somewhere to be recorded |
| `CRDT-2`, `MOT-5` → `MOT-6` | Construct 1 has reference fields; they need a settled policy |
| `MOT-6`, `CRDT-4`, `CRDT-5` → `MOT-7`, `CRDT-6` | One construct proves the path before 29 follow (D1) |
| `HAR-3`, `CRDT-3` → `MOT-8` | The ordered residue has nothing to be proven against otherwise |

`HAR-7` has **no upstream dependency** and a live failure mode today; it is in M1 because it can start
now, not because M0 unblocks it. `HAR-1`, `HAR-2`, and `CRDT-8` sit outside the ladder — see the
Unscheduled row above and each item's owning plan.

## Per-milestone detail

### M0 — the missing artifact

The one thing ADR 0014 named as *required and non-existent*. Scope it to what M2 needs rather than all
193 classes: the three entities the join can reach, plus `CmPossibility`. Deliverable is a checked-in,
reviewable file in this repo, not a code path.

**Not in scope:** whether `CmSemanticDomain` and `Publication` belong in manifest scope at all. Both are
entirely out of scope today (all 5 and all 16 rows), that is a manifest question, and it is open.

### M1 — two independent tracks

The generator track and the Harmony-robustness track share a milestone because they share no
dependency and both should be in flight before M2.

`HAR-7` is not a new feature. `SnapshotWorker.cs:84-85` already throws loudly and names the change
type, `CommitId`, and `EntityId` — but it throws inside a transaction during a replay that a late,
unrelated commit may have triggered, so one bad change fails **every** commit in the batch on **every**
snapshot regeneration, permanently. Converting abort-the-replay into apply-what-you-can-plus-a-recorded
diagnostic is the work.

### M2 — regenerate what already ships

The falsifiable gate. Correctness is not established by the design being elegant; it is established by
regenerating code that already passes its tests.

Honest scope, so it is not oversold: 37 in-scope rows — 34 `unordered`, 3 `positional`, **zero**
`feeding`, **zero** `index-as-identity`, **zero** `AssessPoisonsCache=yes`. It exercises `set|clear`
(20), `create|delete` (8), `addRef|removeRef` (4), and `create|delete|move|reparent` (3). Passing it
licenses generating the mechanical majority. It licenses **nothing** about ordered grammar.

M2's deliverable in lexbox is a **pull request in someone else's repo, review, and release train**.
Socialise it there before 30 grammar constructs arrive, not after.

### M3 — primitives, then pin

Harmony gains four things and no domain vocabulary (ADR 0014 decisions 4 and 5). The acceptance test
for "no domain vocabulary" is mechanical: after M3, grepping harmony for LibLCM or MiniLcm type names
returns nothing.

`CRDT-3` (keyed-map modelling for the 3 alpha-variable fields) is listed here rather than under
harmony deliberately: it is a modelling decision, not a feature. `JsonPatchValidator` in
`LcmCrdt/Changes/JsonPatchChange.cs` **already rejects index-based patch paths** — Harmony has ruled
this out, so the response is to model those fields as keyed maps.

### M4 — reviewed world equals applied world

This is the PR-like control gate defined by [the product architecture](plan-product-architecture.md):
immutable Proposal revisions, exact-input Check Runs, typed Reviews, policy Decisions, payload-bound
approval, Baseline Tokens, private workspaces, fenced final comparison, one atomic LibLCM unit of
work, save/read-back, Receipts, and crash reconciliation. It uses `setGloss` only as a lifecycle
control; grammar remains the first product family.


### M5 — one construct, end to end

D1's staged path: one construct proves the whole route before the other 29. The `XxxSync` reconciler
pair is **not a design choice** — it is the cost of the FieldWorks bridge that already exists and runs
(`FwHeadless/Services/SyncHostedService.cs`: `SendReceive` → `syncService.Sync(miniLcmApi, fwdataApi, …)`
→ `SendReceive`). Grammar extends that `Sync` step.

The gate has two halves and both are required: CRDT convergence across replicas, and a `.fwdata`
round trip through Chorus. Passing only the first proves half a bridge.

### M6 — the rest, and the residue

Two different kinds of work, deliberately in one milestone so the second is not forgotten once the
first starts producing volume:

- **Mechanical volume** — 29 constructs, 75 reference fields across 38 classes. Reviewed diffs, not
  hand-built classes.
- **The residue** — 2 `feeding` fields (phonological rule order, where order encodes feeding/bleeding)
  and 3 `index-as-identity` fields (alpha variables). ADR 0013 flagged these as the real problem, and
  M2's gate says nothing about them. They need their own proof once `HAR-3` exists.

EF **migrations stay hand-written**. Regeneration is free for source and not free for a linguist's
existing SQLite file. This is the one cost generation does not absorb.

## What these plans do not cover

Stated so the absence is deliberate rather than discovered:

- **Proposal, review, approval, and permission storage** — application layer, in Motif and Lexbox, not
  in Harmony. See [motif-overall-plan.md](motif-overall-plan.md) phases 2–3 and `HAR-9`'s reassignment
  in [plan-harmony.md](plan-harmony.md).
- **CRDT → full `.fwdata` materialization** — the largest single build item, and it lives in
  `FwLiteProjectSync`, not in LcmCrdt or Harmony. Tracked as `CRDT-8`.
- **Who staffs any of this.** D6 says where code lands, not who writes it. ADR 0013's closing concern
  stands.
- **The NuGet and npm namespaces** for the Motif name — unchecked, and gating publication (D7).

## How these four documents stay aligned

The alignment is mechanical on purpose; a convention nobody can verify is not alignment.

1. **Milestones are defined here and only here.** A repo plan may reference `M3`; it may not describe
   what `M3` means.
2. **Every work item has exactly one owning plan.** `MOT-*` in plan-motif, `HAR-*` in plan-harmony,
   `CRDT-*` in plan-lcmcrdt. Status and evidence live with the item, never here.
3. **`HAR-*` numbers are inherited** from [harmony-additions-needed.md](harmony-additions-needed.md)'s
   items 1–9, so the evidence trail behind each one survives. The gaps are meaningful: **there is no
   `HAR-4`, `HAR-8`, or `HAR-9`** because those three items were reassigned — 4 to `CRDT-3`, 8 to
   `CRDT-8`, 9 to the application layer. Do not renumber to close the gaps.
4. **This file carries milestone-level status; the repo plans carry item-level status.** Two places, two
   granularities, no duplicated facts.
5. **Item counts are stated in both places.** The ladder table above names every item id; each repo
   plan states its own total. A mismatched count is visible without reading prose:
   **MOT 8 items · HAR 6 items (1, 2, 3, 5, 6, 7) · CRDT 8 items.**
6. **Moving an item between milestones touches this file and the owning plan in the same commit.**
   Nothing else needs to change, and if something else does, the coupling was undocumented.
