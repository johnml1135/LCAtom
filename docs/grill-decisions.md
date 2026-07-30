# Grill decision log — grammar on Harmony

*Live document. Decisions are recorded as they are made, in order. Rationale is compressed; the
supporting research is in the sibling `grill-*.md` and `declarative-commands-vs-crdt.md` files.*

Context: [ADR 0013](adr/0013-harmony-is-the-change-mechanism.md) settled that Harmony's
`Commit`/`IChange` is the change mechanism. This log covers what gets built on that basis.

---

## D1 — Grammar entities live in the CRDT store, staged

**Decided.** Grammar becomes Harmony entities. `.fwdata` is a generated artifact, not a source of
truth for grammar.

- Costs: 30 constructs, 75 reference fields across 38 classes, referential integrity implemented in
  the CRDT store, and the ordering question becomes live.
- Buys: grammar merges via CRDT rather than Chorus; matches the stated destination that CRDTs replace
  Send/Receive.
- Staged: one construct proves the whole path end to end before the other 29.

**Correction absorbed during the grill.** An earlier framing asked whether FieldWorks becomes
read-only for grammar. It does not, and this is already solved — **FieldWorks interop runs through
Chorus**, not through anything new. `FwHeadless/Services/SyncHostedService.cs` does
`SendReceive` (`:290`) → `syncService.Sync(miniLcmApi, fwdataApi, …)` (`:216`) → `SendReceive`
(`:229`). FieldWorks edits arrive by Mercurial, are reconciled into the CRDT store, and CRDT edits
go back out the same way. Grammar extends the existing `Sync` step; it does not need a new bridge.

Consequence: the per-construct `XxxSync` reconciler pair is **not a design choice**, it is the cost of
the bridge that already exists and runs.

## D2 — Grammar gets its own interface, in the same repo

**Decided.** `IMiniLcmGrammarApi` alongside `IMiniLcmApi`, in the same projects and release train.

- Backends **declare** grammar support rather than being forced to implement 30 constructs. `LcmCrdt`
  and `FwDataMiniLcmBridge` implement both; the MAUI mobile target takes lexical only, which is
  consistent with the earlier decision that grammar is not edited on a phone.
- One repo, one CI, one team. The interface boundary does the work a repo boundary would, without the
  release-cadence cost.
- Rejected: extending `IMiniLcmApi` directly (every backend implements or throws — the "one ring with
  a hole" shape); separate `.csproj` projects (stronger boundary than the problem warrants, revisit
  if file count justifies it).

## D3 — The manifest is a code generator for the mechanical layer

**Decided.** `manifest/liblcm-inventory.tsv` generates the mechanical layer; humans write the rest.

**Generated** from `Kind` / `Card` / `Sig` / `Verbs` / `ComparisonClass` / `Construct`:

- model classes and their properties;
- `GetReferences()` — every `rel` field's id;
- `RemoveReference(id, time)` — three fixed rules keyed on shape: **owner → delete self**,
  **`rel/atomic` → null it**, **`rel/col` → filter it**;
- sync helpers, dispatched by `ComparisonClass`;
- `JsonPatchChange`-based edit changes, and `DeleteChange` registrations.

**Hand-written**, because the manifest cannot know it: HCLoader validation rules, the 2 `feeding`
fields, `CreateChange` bodies (they must construct a valid entity), and EF relationship configuration.

**Evidence this is generable.** Real `MiniLcm/Models/Sense.cs:30-46` implements
`GetReferences`/`RemoveReference` as exactly those three shape rules. Nothing in it requires domain
knowledge the manifest lacks. This contradicts the earlier research note claiming referential policy
was not generable; that note was wrong.

Turns 30 hand-built constructs into 30 reviewed diffs.

## D4 — The CRDT layer is generated from `MasterLCModel.xml`, not hand-written

**Decided.** See [ADR 0014](adr/0014-generate-the-crdt-layer-from-masterlcmodel.md) for the full
record and evidence.

- `manifest/liblcm-inventory.tsv` is a **1:1 projection** of LibLCM's own model file: 445 `basic` +
  235 `owning` + 218 `rel` = 898 field declarations across 193 classes, against 898 manifest rows.
  The join key already exists.
- Structure comes from `MasterLCModel.xml` (tracks LibLCM upgrades); policy (`Scope`, `Construct`,
  `ComparisonClass`, `Verbs`) comes from the manifest (human judgement, exists nowhere else). Joined
  on `(Class, Field)`; a key in one and not the other **fails the build**.
- LibLCM already generates ~154,000 of its own lines this way — more than it hand-writes. LcmCrdt
  generates none: 38 change files, 2 generic, 36 one-offs, and every registration typed by hand.
- **Acceptance gate:** regenerate the five `IPossibility` entities (`ComplexFormType`,
  `SemanticDomain`, `PartOfSpeech`, `MorphType`, `Publication`) and diff against the shipped, tested
  implementations. The generator earns the 30 unwritten constructs by reproducing the ones that work.

This supersedes D3's framing. D3 said the manifest *is* a code generator for the mechanical layer;
D4 fixes what it generates *from* and *into*, and adds a falsifiable acceptance test.

## D5 — Preconditions live in the proposal, never in the change

**Decided.** Baseline evidence ("I observed gloss = 'dog' when I wrote this") is an **observation
carried by the proposal envelope**, evaluated at review/apply time and surfaced as drift. It is never
a guard inside an `IChange`.

- A precondition inside a merging change makes the outcome depend on evaluation position; two
  replicas that resolve it differently diverge permanently.
- This is why `OperationEnvelope` has no `Before` (`Contract/Model/OperationEnvelope.cs:62-99`), and
  why that absence is what lets 412 of 473 fields fold into `IChange` natively.
- The PR system is the machine that resolves preconditions before merge — same as a stale base branch
  in git. What crosses into history is unconditional.
- Rejected: `baseline:` as an operation input with a `BaselineMismatch` error, as proposed in the
  Codex IDL sketch. Correct for an RPC API, wrong for anything that merges.

## D6 — Three repos, and the generated output is not ours

**Decided.** Recorded because it changes who reviews what, not just where files sit.

| Artifact | Repo |
| --- | --- |
| Generated entities, change classes, EF config, registrations, migrations | **languageforge-lexbox** (`backend/FwLite/LcmCrdt`) |
| Converging sequence, reference-set policy, cross-owner move, diagnostic channel | **harmony** (consumed as NuGet, pinned `0.2.1-rc.225`) |
| Manifest, classification, the generator, semantic + lowering layers | **this repo** |

- Cross-repo development is already supported: `Harmony.*.References.props` swap `PackageReference`
  for `ProjectReference` under `UseHarmonySource`.
- Build-time generators are already accepted practice in lexbox (`BeaKona.AutoInterfaceGenerator`,
  `Reinforced.Typings`) — and are **absent from the harmony repo entirely**.
- Consequence: the D4 acceptance gate is a **lexbox pull request**, and is worth socialising there
  before 30 grammar constructs arrive rather than after.

---

## Open, in grill order

- Ordering: what happens when two people concurrently reorder phonological rules (2 `feeding` fields).
- Approval tamper-evidence: `CommitBase.GenerateHash` covers `Id` + `parentHash` only, not the payload.
- Where proposal / review / approval state lives.
- Naming: what, if anything, remains called LCAtom. (A Codex thread on 2026-07-30 proposed
  *Language Change Workbench* / `langchange`, keeping LCAtom for the semantic-operation layer only;
  an earlier turn in the same thread proposed *Grammar Workbench* / `gbench`, which is what
  `grammar-workbench-overall-plan.md` still says. Unresolved.)
- Reference-set policy (add-wins vs remove-wins) before 38 classes replicate it.
- Cross-owner move / reparent cycle rule.
- **Delete is not final.** `SnapshotWorker.cs:87-91` resurrects a tombstoned entity when a
  later-timestamped change supports creation. Defensible as add-wins; currently implicit; decide it.
- Who maintains what — **narrowed by D6**, but the D6 table says where code lands, not who staffs it.
  ADR 0013's closing concern stands.
- Whether `linguistic-assistant`'s in-flight LIFT+HC change-set vocabulary gets reconciled.
- Whether an IDL (Smithy / TypeSpec) is adopted at all, or whether annotated C# records plus a
  generated JSON Schema carry the contract. Gate on a three-operation spike; see
  [idl-contract-notes.md](idl-contract-notes.md).
