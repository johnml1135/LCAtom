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

---

## Open, in grill order

- Ordering: what happens when two people concurrently reorder phonological rules (2 `feeding` fields).
- Approval tamper-evidence: `CommitBase.GenerateHash` covers `Id` + `parentHash` only, not the payload.
- Where proposal / review / approval state lives.
- Naming: what, if anything, remains called LCAtom.
- Reference-set policy (add-wins vs remove-wins) before 38 classes replicate it.
- Cross-owner move / reparent cycle rule.
- Who maintains what.
- Whether `linguistic-assistant`'s in-flight LIFT+HC change-set vocabulary gets reconciled.
