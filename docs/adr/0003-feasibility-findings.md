# ADR 0003 — Feasibility findings: effect capture, applied-log sync, adapter reuse

Status: accepted (2026-07-24)

## Context

Three assumptions in the design were validated directly against source before committing the spec to
them: `liblcm`, `chorus` (LibChorus), and `languageforge-lexbox` (MiniLcm / `FwDataMiniLcmBridge`).

## Decisions

### 1. Effects are captured by snapshot diff, not by a LibLCM change feed

LibLCM's UnitOfWork does record `(object, field, before, after)` internally, but those records live
in `internal` classes whose `InternalsVisibleTo` is granted only to LibLCM's own test assemblies. The
only public notification, `IVwNotifyChange.PropChanged`, carries no values — for scalars, not even a
count. Reflection into the internal records would be brittle, unversioned surface contrary to the
stable-API philosophy.

Therefore effect capture is Motif's own **before/after semantic-snapshot diff**, scoped to the
comparison footprint plus the delete/reference closure. Two public `ICmObject` primitives make the
scoping tractable without a whole-project scan: `AllOwnedObjects` (the owned subtree, i.e. the delete
cascade) and `ReferringObjects` (inbound references, i.e. reference cleanup). This confirms the "read
back, not replay" decision and makes footprint scoping load-bearing for cost, not only for drift.
`UndoableUnitOfWorkHelper.Do(undoText, redoText, actionHandler, task)` is the public commit/rollback
boundary.

### 2. The applied-change log unions under Chorus S/R for distinct GUIDs

LibChorus never discards an unmatched inserted element; two entries with distinct `Version` GUIDs
always both survive its 3-way merge. A clean, note-free union additionally requires the `CmResource`
element to be registered GUID-keyed and order-irrelevant — the exact strategy Chorus's own
append-only `.ChorusNotes` annotation log uses (`ElementStrategy.CreateForKeyedElement("guid",
false)`). That registration is FieldWorks-model-specific and lives in FLExBridge, not LibChorus, so
it is confirmed at the engine level and re-verified in FLExBridge during Phase 0. The sole collision —
the same GUID with differing `Name` — costs provenance only: the GUID remains present exactly once,
so the idempotence check (which reads only the GUID) is unaffected, and the record was never
authoritative.

> **Caveat added 2026-08-03 — the Phase 0 FLExBridge re-verification has not happened**, and the
> FieldWorks-model registration is absent from every locally available source. The distinct-GUID union
> claim holds regardless: additions are never dropped by the generic algorithm. **The collision
> sentence above is the part at risk.** Chorus's *default* strategy is `FindByEqualityOfTree` with
> order relevant, so absent the guid-keyed registration the same GUID with differing `Name` yields
> **two `<rt>` elements sharing one GUID**, not one — worse than "costs provenance only." Indirect
> evidence strongly suggests the registration exists; it has not been observed. See
> [E19 findings](../research/2026-08-03-chorus-applied-log-merge.md).

### 3. Reuse the FwData/LibLCM adapter plumbing by copy-and-adapt, under MIT

`FwDataMiniLcmBridge` (in languageforge-lexbox; MIT, SIL Global) contains roughly 1,000–1,200 lines
of model-agnostic LibLCM plumbing worth reusing: project load to `LcmCache` (`ProjectLoader`), cache
lifecycle with dispose-before-unlock (`FwDataFactory`), the "wrap in a new UOW only if not already
inside one" idiom (`ActionHandlerHelpers`), headless `ILcmUI`/progress shims, MultiString/writing-
system helpers, and the ~650-line rich-text property mapping (`RichTextMapping`). It is taken by
copy-and-adapt into the host layer, not as a shared package — a NuGet dependency would couple
Motif's release train to FwLite's, which the architecture forbids. MiniLcm's lexicon-only model, its
CRDT/Harmony sync, update-proxies, and media/search/sorting are explicitly not reused.

## Consequences

- Phase 0 gains explicit spikes for snapshot-diff read-back and the FLExBridge merge-strategy check;
  Phase 3 defines effect capture as a footprint-scoped snapshot diff over the two public primitives.
- The applied-log carries a documented sync-behavior section and one known, bounded collision.
- The host layer starts from adapted, battle-tested plumbing rather than a rewrite, keeping the core
  contract independent of FwLite.
