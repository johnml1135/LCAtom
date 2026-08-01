# Plan — work in `LcmCrdt` (languageforge-lexbox)

*Eight items. Milestones are defined in [plan-cross-repo.md](plan-cross-repo.md); this file owns
`CRDT-*` item status and evidence.*

**Where this plan lives and where the work lands.** The plan is authored here; the **code** lands in
`languageforge-lexbox`, under `backend/FwLite/LcmCrdt` (and, for `CRDT-8`, `backend/FwLite/FwLiteProjectSync`).
That means **not our repo, not our review, not our release train** (ADR 0014, Consequences). Every item
below is ultimately someone else's pull request, which is why `CRDT-1` is scheduled before the volume
arrives rather than after.

**Numbering note.** `CRDT-3` and `CRDT-8` are items 4 and 8 of
[harmony-additions-needed.md](harmony-additions-needed.md), reassigned here from that document's Harmony
list. See the numbering rule in [plan-cross-repo.md](plan-cross-repo.md).

## Status summary

| Item | Milestone | Where | Size | Status |
| --- | --- | --- | --- | --- |
| `CRDT-1` — accept generated possibility-list output, diff against shipped code | **M2** | `LcmCrdt` | Medium — mostly review | Not started |
| `CRDT-2` — bump the `SIL.Harmony` pin to an rc carrying `HAR-3/5/6` | **M3** | `backend/Directory.Packages.props` | Trivial, then blocking | Not started |
| `CRDT-3` — model the 3 alpha-variable fields as keyed maps | **M3** | `LcmCrdt` | None — a modelling decision | Not started |
| `CRDT-9` — authority/fencing plus baseline-bound save/read-back/recovery host | **M4** | `FwLiteProjectSync` / FieldWorks adapter | Large, correctness-critical | Not started |
| `CRDT-4` — `IMiniLcmGrammarApi` | **M5** | `MiniLcm` + backends | Medium | Not started |
| `CRDT-5` — construct-1 selective LcmCrdt ↔ LibLCM reconciler pair | **M5** | `FwLiteProjectSync` | Medium | Not started |
| `CRDT-6` — generated output for the remaining 29 constructs | **M6** | `LcmCrdt` | Large, but reviewed diffs | Not started |
| `CRDT-7` — EF migrations for the generated entities | **M6** | `LcmCrdt` | Medium, and hand-written | Not started |
| `CRDT-8` — CRDT → brand-new full `.fwdata` materialization | Conditional export gate | `FwLiteProjectSync` | **Large** | Not started |

Nothing here has started. No branch, issue, or PR exists in `languageforge-lexbox` for any item.

## The cost this plan exists to remove

Measured, not estimated. Under `backend/FwLite/LcmCrdt/Changes/`: **38 `.cs` files, of which 2 are
generic over `T`** (`JsonPatchChange<T>`, a local `SetOrderChange<T>`) **and 36 are concrete one-offs** —
`AddSemanticDomainChange`, `RemoveSemanticDomainChange`, `ReplaceSemanticDomainChange`,
`CreatePartOfSpeechChange`, and so on. (Verified 2026-07-30: 38 files recursively, 26 entries at the top
level plus the `Comments/`, `Entries/`, `ExampleSentences/`, and `CustomJsonPatches/` subdirectories.)

Registration is entirely explicit and hand-typed in one method, `LcmCrdtKernel.ConfigureCrdt` (declared
at `LcmCrdtKernel.cs:190`): 13 entity types, then the change list — including one `JsonPatchChange<X>`
line and one `DeleteChange<X>` line per entity. No attributes, no reflection, no convention.

One new entity touches ~14 files and 350–450 lines, cross-checked against the real historical
`MorphType` addition (29 files repo-wide). **13 entities have cost 36 bespoke change classes.** Thirty
grammar constructs at that ratio is roughly 80 more classes plus ~1,400 hand-typed registration and
configuration lines — all of it derivable from `Kind` / `Card` / `Sig` / `Verbs` / `ComparisonClass` for
rows that already exist in a file LibLCM ships.

**This is not a lexbox failing.** `SIL.Harmony.Sample`, the library's own reference consumer, has 10
hand-written change classes registered the same way. The generation layer is missing everywhere.

**Build-time generation is, however, already accepted practice here** — `BeaKona.AutoInterfaceGenerator`
(`MiniLcm.csproj:7`, `FwLiteShared.csproj:9`, `FwLiteProjectSync.csproj:10`) and `Reinforced.Typings`
(`FwLiteShared.csproj:17`, which already auto-exports every registered entity to TypeScript). Neither
does what is needed here. They establish that adding a generator is normal, not that the tooling exists.

---

## `CRDT-1` — accept the generated possibility-list output — M2

The falsifiable gate of ADR 0014, seen from the receiving repo. `MOT-4` produces the output; this item
is the diff, the review, and the merge.

**Scope: exactly three entities**, the only ones the `(Class, Field)` join can reach.

| MiniLcm entity | LibLCM class | Manifest rows | In scope |
| --- | --- | ---: | ---: |
| `PartOfSpeech` | `PartOfSpeech` | 13 | 13 |
| `MorphType` | `MoMorphType` | 3 | 3 |
| `ComplexFormType` | `LexEntryType` | 2 | 2 |
| ~~`SemanticDomain`~~ | `CmSemanticDomain` | 5 | **0 — all out of scope** |
| ~~`Publication`~~ | `Publication` | 16 | **0 — all out of scope** |

`IPossibility` (`backend/FwLite/MiniLcm/Models/IPossibility.cs:3`) marks five entities; verified
2026-07-30 that its implementors are `ComplexFormType`, `MorphType`, `PartOfSpeech`, `Publication`, and
`SemanticDomain`. Only the first three are reachable.

**Acceptance**

- Generated entity, change, EF-configuration, and registration code **replaces** the shipped hand-written
  versions for those three entities, and `LcmCrdt`'s existing test suite passes **unmodified**. A test
  edited to accommodate the generator is a failed gate.
- Every difference between generated and shipped output is either explained or fixed. Cosmetic
  differences are explained; behavioural ones are fixed.
- `CreateChange` bodies stay hand-written — they must construct a *valid* entity, and validity is domain
  knowledge the model file does not carry.

**What passing does not license.** 37 in-scope rows: 34 `unordered`, 3 `positional`, zero `feeding`,
zero `index-as-identity`, zero `AssessPoisonsCache=yes`. It proves possibility-list CRUD regenerates.
It says nothing about any HC-reachable grammar construct. Do not cite this gate in support of `CRDT-6`
beyond the mechanical majority.

**Process risk, and the reason for the schedule.** This is a pull request into a repo with its own
reviewers and cadence. Socialise it before 30 constructs arrive.

---

## `CRDT-2` — bump the Harmony pin — M3

Verified pin, 2026-07-30: `SIL.Harmony`, `SIL.Harmony.Core`, and `SIL.Harmony.Linq2db` at
`0.2.1-rc.225` (`backend/Directory.Packages.props:112-114`).

Trivial as a change, blocking as a dependency: `HAR-3`, `HAR-5`, and `HAR-6` are invisible to LcmCrdt
until an rc carrying them is published and pinned here. Until then, develop against source —
`Harmony.{App,Core,Linq2db}.References.props` swap the `PackageReference` for a `ProjectReference` to
`$(HarmonySourcePath)` when `UseHarmonySource` is set, erroring if the clone is absent;
`LcmCrdt.csproj:35` imports the Linq2db variant.

**Acceptance:** the full lexbox backend test suite passes on the new pin before any generated grammar
output depends on it.

---

## `CRDT-3` — keyed maps for the 3 alpha-variable fields — M3

**Not a gap; a modelling choice**, and Harmony has already made it. `LcmCrdt/Changes/JsonPatchChange.cs`
— `JsonPatchValidator` — **already rejects index-based patch paths**, with the comment *"prevents the use
of indexes in the path, as this will cause major problems with CRDTs."*

Alpha variables use position as an identifier. The correct response is to model those 3 fields as a
**keyed map**, not an indexed array. No core feature required — arguably a bug fix.

**Acceptance:** the 3 `index-as-identity` fields have a keyed representation, and `MOT-5`'s generator
mapping emits it rather than an ordered collection.

---

## `CRDT-4` — `IMiniLcmGrammarApi` — M5

Per D2: a **separate interface alongside `IMiniLcmApi`, in the same projects and release train.**

- Backends **declare** grammar support rather than being forced to implement 30 constructs. `LcmCrdt`
  and `FwDataMiniLcmBridge` implement both; the MAUI mobile target takes lexical only — consistent with
  grammar not being edited on a phone.
- One repo, one CI, one team. The interface boundary does the work a repo boundary would, without the
  release-cadence cost.
- **Rejected:** extending `IMiniLcmApi` directly (every backend implements or throws — the "one ring
  with a hole" shape); separate `.csproj` projects (a stronger boundary than the problem warrants —
  revisit if file count justifies it).

**Acceptance:** a backend that does not support grammar compiles, ships, and is queryable for that fact
without throwing.

---

## `CRDT-5` — the construct-1 selective bridge/reconciler pair — M5

**This is not a design choice; it is the cost of a bridge that already exists and runs.**
`FwHeadless/Services/SyncHostedService.cs` does `SendReceive` (`:290`) →
`syncService.Sync(miniLcmApi, fwdataApi, …)` (`:216`) → `SendReceive` (`:229`). FieldWorks edits arrive
by Mercurial, are reconciled into the CRDT store, and CRDT edits go back out the same way. Grammar
**extends the existing `Sync` step**; it does not need a new bridge.

Consequence, stated plainly: 30 constructs eventually means 30 reconciler pairs. Sync helpers are
generable, dispatched by `ComparisonClass` (D3) — that is what keeps this from being 30 hand-written
files, and it is `MOT-4`'s output shape that decides whether it works.

**Acceptance (this is also M4's gate):** one grammar construct merges across two CRDT replicas **and**
round-trips through Chorus Send/Receive without loss. Both halves required.

---

## `CRDT-6` — the remaining 29 constructs — M6

30 constructs, 75 reference fields, 38 classes. D1 staged this deliberately: **one construct proves the
whole path end to end before the other 29.** Referential integrity is implemented in the CRDT store.

Generated, per D3/D4: model classes and properties; `GetReferences()`; `RemoveReference(id, time)` under
three fixed shape rules (**owner → delete self**, **`rel/atomic` → null it**, **`rel/col` → filter it**);
sync helpers dispatched by `ComparisonClass`; `JsonPatchChange`-based edit changes; `DeleteChange`
registrations.

Hand-written, because the manifest cannot know it: HCLoader validation rules, the 2 `feeding` fields,
`CreateChange` bodies, and EF relationship configuration.

Evidence this is generable rather than hoped: `MiniLcm/Models/Sense.cs:30-46` already implements
`GetReferences`/`RemoveReference` as exactly those three shape rules, and nothing in it needs domain
knowledge the manifest lacks. An earlier research note in this repo claimed referential policy was not
generable; **that note was wrong.**

**Acceptance:** 30 reviewed diffs rather than 30 hand-built constructs, and the ordered residue
explicitly excluded from this item — it belongs to `MOT-8`.

---

## `CRDT-7` — EF migrations — M6

**The one cost generation does not absorb.** Regeneration is free for source and not free for a
linguist's existing SQLite file. Migrations stay hand-written, and they are the reason `CRDT-6` cannot
be merged as one 29-construct commit.

**Acceptance:** an existing project database opens after each construct lands, and a downgrade path is
either provided or explicitly refused in writing.

---

## `CRDT-8` — CRDT → brand-new full `.fwdata` materialization — conditional export gate

**The biggest concrete build item in this whole programme, and it is nowhere near Harmony.**

`CrdtFwdataProjectSyncService` *reconciles two existing projects* — `Sync`, `Import`, `SyncDryRun`,
`ImportDryRun` (`:22-37`) — all of which assume a `.fwdata` already exists. Producing a **complete
`.fwdata` from a CRDT project** is a different operation, and the write path has known holes: e.g.
`FwDataMiniLcmApi.cs:615` throws `NotSupportedException("Morph types cannot be created in fwdata; they
are predefined")`.

It belongs in `FwLiteProjectSync`, alongside the existing dry-run infrastructure. **Useful precedent:**
`DryRunMiniLcmApi` already exists and records what *would* have been written — the right shape to reuse.

**Why unscheduled rather than in M5.** It is required by the *export* workflow ("make these changes,
then give me the full fwdata"), not by any milestone in the generation ladder. Scheduling it inside M5
would make the generation gate depend on a large unrelated build. It is listed here so it is not
mistaken for a small follow-on.


---

## `CRDT-9` — authority, fencing, and recovery host — M4

Implement the host-side half of controlled apply in `FwLiteProjectSync` or the FieldWorks adapter:
private baseline-bound workspaces; authority kind/epoch; a short exclusive capability with fencing;
final token comparison; one outer LibLCM unit of work; save/read-back; after-token; Receipt handoff;
and crash reconciliation.

**Acceptance**

- no writer can alter the authoritative projection between final comparison and save;
- stale holders fail a fencing/version check;
- cross-store stages are explicit and idempotent;
- ambiguous failure enters `NeedsReconciliation` and never replays blindly;
- the host accepts semantic Proposal intent, never a generated Mutation Plan;
- the control slice passes with `setGloss` before the first grammar Construct.

CRDT-9 does not imply text/occurrence/analysis support. Each promoted domain requires its own
approved operation-family contract and selective LcmCrdt ↔ LibLCM round-trip proof.
---

## Cross-links

- Milestones, dependency edges, alignment rules: [plan-cross-repo.md](plan-cross-repo.md)
- The primitives this consumes: [plan-harmony.md](plan-harmony.md)
- The generator that feeds this: [plan-motif.md](plan-motif.md)
- Decisions D1–D4, D6: [grill-decisions.md](grill-decisions.md) ·
  ADR: [0014](adr/0014-generate-the-crdt-layer-from-masterlcmodel.md)
