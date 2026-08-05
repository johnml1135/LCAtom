# Inventory: how much of SIL.Harmony per-entity work is boilerplate vs. design

> **HISTORICAL — a path not taken.** This document evaluates routing Motif's changes through a CRDT merge
> layer, or inventories the repositories that would have been involved. **That design was assessed and
> rejected** ([adoption report](harmony-adoption-report.md)); operations target LibLCM directly and there is
> no merge layer. Kept as the evidence behind that decision. **It is not a plan, and nothing in it is
> scheduled.** For the live plan see [Plan A](plan-motif.md).

Scope: `C:\Users\johnm\Documents\repos\harmony` (`src\SIL.Harmony`, `src\SIL.Harmony.Core`, `src\SIL.Harmony.Sample`)
and `C:\Users\johnm\Documents\repos\languageforge-lexbox\backend\FwLite\{LcmCrdt,MiniLcm}`. All claims below are
sourced from reading the files at the cited path:line; where I could not verify something from code I say so
explicitly rather than relying on README/doc-comment prose.

---

## 1. Every `IChange` / `Change<T>` implementation, generic vs. hand-written

### 1a. Harmony core (`SIL.Harmony`) — the generic, reusable machinery

| Path | Type | Reusable? |
|---|---|---|
| `harmony/src/SIL.Harmony/Changes/Change.cs:39` | `abstract class Change<T> : IChange` | generic base, reused by every change |
| `harmony/src/SIL.Harmony/Changes/CreateChange.cs:5` | `abstract class CreateChange<T> : Change<T>` | generic base |
| `harmony/src/SIL.Harmony/Changes/EditChange.cs:5` | `abstract class EditChange<T> : Change<T>` | generic base |
| `harmony/src/SIL.Harmony/Changes/DeleteChange.cs:5` | `class DeleteChange<T> : EditChange<T>, IPolyType` | **concrete, generic, reusable for any `T`** — one class covers delete for all entities |
| `harmony/src/SIL.Harmony/Changes/SetOrderChange.cs:11` | `class SetOrderChange<T> : EditChange<T>, IPolyType` (constrained to `IOrderableCrdt`) | **concrete, generic, reusable** |
| `harmony/src/SIL.Harmony/Changes/OpaqueChange.cs:9` | `sealed class OpaqueChange : IChange` | not per-entity; fallback for unknown `$type` (`OpaqueChange.cs:9-27`) |
| `harmony/src/SIL.Harmony/Resource/DeleteRemoteResourceChange.cs:6` | `class DeleteRemoteResourceChange<TMetadata> : EditChange<RemoteResource<TMetadata>>` | generic over metadata type, reusable |
| `harmony/src/SIL.Harmony/Resource/CreateRemoteResourceChange.cs:6` | `class CreateRemoteResourceChange<TMetadata>` | generic, reusable |
| `harmony/src/SIL.Harmony/Resource/CreateRemoteResourcePendingUpload.cs:6` | `class CreateRemoteResourcePendingUploadChange<TMetadata>` | generic, reusable |
| `harmony/src/SIL.Harmony/Resource/RemoteResourceUploadedChange.cs:9` | `class RemoteResourceUploadedChange<TMetadata>` | generic, reusable |
| `harmony/src/SIL.Harmony/Resource/SetRemoteResourceMetadataChange.cs:6` | `class SetRemoteResourceMetadataChange<TMetadata>` | generic, reusable |

**Count: 5 concrete generic reusable change types** (`DeleteChange<T>`, `SetOrderChange<T>`, plus 3 resource-metadata generics) that any new entity gets "for free" simply by being registered as `T`, **plus** 2 abstract generic bases (`CreateChange<T>`, `EditChange<T>`) that hand-written changes inherit from, **plus** the fallback `OpaqueChange`.

### 1b. `SIL.Harmony.Sample` (the library's own reference consumer)

10 hand-written, single-type change classes, one per behavior (not one per field): `NewWordChange`, `SetWordTextChange`, `SetWordNoteChange`, `SetAntonymReferenceChange`, `AddWordImageChange`, `NewDefinitionChange`, `SetDefinitionPartOfSpeechChange`, `NewExampleChange`, `EditExampleChange`, `SetTagChange`, `TagWordChange` (paths under `harmony/src/SIL.Harmony.Sample/Changes/*.cs`). This is Harmony's own worked example of "how much is hand-written" — it confirms the pattern is inherent to the library, not an LcmCrdt idiosyncrasy: `harmony/src/SIL.Harmony.Sample/CrdtSampleKernel.cs:39-56` registers each one explicitly via `config.ChangeTypeListBuilder.Add<T>()`, identical in shape to LcmCrdt's registration (see §4).

### 1c. LcmCrdt (`backend/FwLite/LcmCrdt/Changes/**`)

34 files total under `Changes/`. Of these:

- **2 are generic/reusable**: `JsonPatchChange<T> : EditChange<T>, IPolyType` (`LcmCrdt/Changes/JsonPatchChange.cs:12`) — a generic "apply a JSON‑Patch document" change usable for any `T`, registered once per entity in `LcmCrdtKernel` (7 registrations: Entry, Sense, WritingSystem, PartOfSpeech, SemanticDomain, ComplexFormType, MorphType, Publication — `LcmCrdtKernel.cs:330-337`); and a local `SetOrderChange<T> : EditChange<T>, IPolyType` (`LcmCrdt/Changes/SetOrderChange.cs:7`, distinct from Harmony's own `SetOrderChange<T>` and constrained to `IOrderableNoId` instead of `IOrderableCrdt`).
- **32 are hand-written for exactly one entity/operation**, e.g. `CreateSenseChange.cs:10`, `CreateSemanticDomainChange.cs:9`, `CreatePartOfSpeechChange.cs:8`, `SetPartOfSpeechChange.cs:7`, `AddSemanticDomainChange.cs:7`, `RemoveSemanticDomainChange.cs:7`, `ReplaceSemanticDomainChange.cs:7`, `MoveSenseToEntryChange.cs:10`, `CreateMorphTypeChange.cs:11`, the whole `Entries/`, `ExampleSentences/`, `Comments/` subfolders, etc. (full listing via `grep -n "^public class" LcmCrdt/Changes/**/*.cs`, 34 hits enumerated above).

**Net for Q1**: harmony ships **5 concrete generic change types + 2 abstract bases** that any new entity reuses automatically once registered. Everything beyond delete / set-order / JSON-patch — i.e. anything expressing a domain-specific create signature or a domain-specific mutation (add/remove/replace an item in a collection, move an entity to a new parent, set a typed reference) — is hand-written, one class per operation, in both the sample and LcmCrdt. **32 of LcmCrdt's 34 change files are one-off.**

---

## 2. The per-entity checklist — traced for `Sense` (rich) and `SemanticDomain`/`PartOfSpeech` (simple)

For each artifact: file path, and what the per-entity edit actually looks like.

### 2.1 Model / POCO class

- Simple: `MiniLcm/Models/SemanticDomain.cs:1-31` — 4 data fields (`Id`, `Name`, `Code`, `DeletedAt`, `Predefined`), `GetReferences()` returns `[]` (line 13), `RemoveReference` is a no-op body (line 18), `Copy()` is a 6-line record-style copy (lines 20-30).
- Simple: `MiniLcm/Models/PartOfSpeech.cs:1-31` — nearly identical shape (`Id`, `Name`, `DeletedAt`, `Predefined`), same no-op `GetReferences`/`RemoveReference`.
- Rich: `MiniLcm/Models/Sense.cs:9-65` — 9 data fields, one owned reference (`PartOfSpeech`/`PartOfSpeechId`), one collection-of-references (`SemanticDomains: IList<SemanticDomain>`), two owning collections (`ExampleSentences`, `Pictures`), a shadow query-rewrite property (`SemanticDomainRows`, lines 25-26), a custom `JsonConverter` for legacy-string backward compat (`SensePoSConverter`, lines 67-80). `GetReferences()` (lines 30-34) and `RemoveReference()` (lines 36-46) are non-trivial: they must special-case the owning parent (`EntryId` → cascades delete), the nullable single reference (`PartOfSpeechId`), and the reference collection (`SemanticDomains`).

### 2.2 `IObjectWithId` implementation

Defined once, generically, in `MiniLcm/Models/IObjectWithId.cs:19-35`: `GetReferences()`, `RemoveReference(Guid, DateTimeOffset)`, `Copy()`. Every model hand-implements the three members itself (no default-interface-method sharing beyond the `Copy()` covariance trick at `IObjectWithId.cs:31-35`). For `SemanticDomain`/`PartOfSpeech` this is boilerplate (empty bodies). For `Sense` it requires judgement about which fields are owned vs. referenced (see 2.1).

### 2.3 JSON polymorphic `$type` registration

One central list: `MiniLcm/Models/IObjectWithId.cs:5-18` —
```csharp
[JsonPolymorphic]
[JsonDerivedType(typeof(Entry), nameof(Entry))]
[JsonDerivedType(typeof(Sense), nameof(Sense))]
...
[JsonDerivedType(typeof(MorphType), nameof(MorphType))]
public interface IObjectWithId
```
13 entries today (Entry, Sense, ExampleSentence, WritingSystem, PartOfSpeech, Publication, SemanticDomain, ComplexFormType, ComplexFormComponent, CustomView, CommentThread, UserComment, MorphType) — every new entity is **one added attribute line** here. This is the *model-level* JSON discriminator; there is a second, independent JSON registration at the Harmony-adapter level (§2.6/§4).

### 2.4 EF Core entity configuration

There is **no per-entity `IEntityTypeConfiguration<T>` file**. All EF configuration lives inline as lambdas passed to `ObjectTypeListBuilder.Add<T>()` calls inside `LcmCrdt/LcmCrdtKernel.cs:190-328` (method `ConfigureCrdt`):

- `SemanticDomain`: `.Add<SemanticDomain>()` — **zero-argument call, no lambda** (`LcmCrdtKernel.cs:263`). Nothing to configure beyond defaults.
- `PartOfSpeech`: `.Add<PartOfSpeech>()` — same, **zero-argument** (`LcmCrdtKernel.cs:261`).
- `Sense`: `.Add<Sense>(builder => { ... })`, 22 lines (`LcmCrdtKernel.cs:220-241`) configuring: `HasMany<ComplexFormComponent>().WithOne().HasForeignKey(...).OnDelete(Cascade)`; `HasOne<Entry>().WithMany(e => e.Senses).HasForeignKey(...)`; `HasOne<PartOfSpeech>(...).WithMany().HasForeignKey(...).OnDelete(SetNull)`; a `jsonb` value-converter for the `SemanticDomains` collection column; a `jsonb` value-converter for `Pictures`.
- Additional Sense-only config sits in `LcmCrdtDbContext.OnModelCreating` (`LcmCrdt/LcmCrdtDbContext.cs:57-58`: `senseModel.Property(s => s.Pictures).HasColumnType("jsonb").HasDefaultValueSql("'[]'")`).
- `LcmCrdtDbContext.cs` also hand-declares one `IQueryable<T>` accessor per entity, e.g. `IQueryable<SemanticDomain> SemanticDomains => Set<SemanticDomain>().AsNoTracking();` (`LcmCrdtDbContext.cs:29`), `IQueryable<PartOfSpeech> PartsOfSpeech => Set<PartOfSpeech>().AsNoTracking();` (`LcmCrdtDbContext.cs:30`), `IQueryable<Sense> Senses => Set<Sense>().AsNoTracking();` (`LcmCrdtDbContext.cs:27`) — one line each, purely mechanical shape (name + type).
- linq2db-specific mapping/association overrides for query-time computed columns and cross-entity joins live in `LcmCrdtKernel.ConfigureDbOptions` (`LcmCrdtKernel.cs:141-155`), e.g. the `Sense.SemanticDomainRows` → `Json.Query(...)` rewrite (line 145, helper at `LcmCrdtKernel.cs:180-183`). This exists **only** for Sense's JSON-array-as-relation trick; simple entities need none of it.

### 2.5 Object-adapter registration

Also inside `ConfigureCrdt`, same call: `.Add<Sense>(...)`, `.Add<PartOfSpeech>()`, `.Add<SemanticDomain>()` (§2.4) are simultaneously the EF-config call **and** the adapter/JSON registration, because `CustomAdapterProvider<TCommonInterface,TAdapter>.Add<T>()` (`harmony/src/SIL.Harmony/Adapters/CustomAdapterProvider.cs:31-46`) does both: it builds the EF `EntityTypeBuilder<T>` (lines 39-42) *and* — via the constructor at `CustomAdapterProvider.cs:17-21` and `JsonTypes.AddDerivedType(typeof(IObjectBase), typeof(TCustomAdapter), ...)` — feeds the Harmony-level (not MiniLcm-level) JSON polymorphism for `IObjectBase`.

Critically, LcmCrdt registers **one single adapter for all 13 entity types**: `config.ObjectTypeListBuilder.CustomAdapter<IObjectWithId, MiniLcmCrdtAdapter>()` (`LcmCrdtKernel.cs:194`), and `MiniLcmCrdtAdapter` (`LcmCrdt/Objects/MiniLcmCrdtAdapter.cs:8-60`) is a **generic wrapper class that is not touched when a new entity is added** — it just forwards `Id`/`DeletedAt`/`GetReferences`/`RemoveReference`/`Copy` to whatever `IObjectWithId` it wraps (`MiniLcmCrdtAdapter.cs:26-39`), and derives the JSON type name from `.GetType().Name` (`MiniLcmCrdtAdapter.cs:46-51`). **This file requires zero per-entity edits.** (Contrast with `SIL.Harmony.Sample`, which uses `DefaultAdapterProvider` instead — `CrdtSampleKernel.cs:57` — where each model directly implements `IObjectBase<T>`, e.g. `Word : IObjectBase<Word>` at `harmony/src/SIL.Harmony.Sample/Models/Word.cs:5`; LcmCrdt chose the custom-adapter path specifically so MiniLcm models don't take a Harmony dependency.)

### 2.6 Change-class registration

Also centralized in `LcmCrdtKernel.ConfigureCrdt`, `config.ChangeTypeListBuilder` (`LcmCrdtKernel.cs:330-395`):
- Simple entities: `.Add<JsonPatchChange<PartOfSpeech>>()` (line 333), `.Add<JsonPatchChange<SemanticDomain>>()` (334), `.Add<DeleteChange<PartOfSpeech>>()` (341), `.Add<DeleteChange<SemanticDomain>>()` (342), `.Add<CreatePartOfSpeechChange>()` (368), `.Add<CreateSemanticDomainChange>()` (369) — 3 lines each.
- Sense needs, in addition to `JsonPatchChange<Sense>` (331) / `DeleteChange<Sense>` (339) / `CreateSenseChange` (352): `SetPartOfSpeechChange` (346), `MoveSenseToEntryChange` (347), `AddSemanticDomainChange` (348), `RemoveSemanticDomainChange` (349), `ReplaceSemanticDomainChange` (350), `Changes.SetOrderChange<Sense>` (390), `CreateSensePictureChange`/`UpdateSensePictureChange`/`ReorderSensePictureChange`/`RemoveSensePictureChange` (364-367) — 11 registration lines total.
- A load-bearing comment at `LcmCrdtKernel.cs:393-394` — `// When adding anything other than a Delete or JsonPatch change, you must add an instance of it to UseChangesTests.GetAllChanges()` — documents a **manual, unenforced** step (see §7).

### 2.7 Sync helper (`MiniLcm/SyncHelpers`)

- `PartOfSpeechSync.cs` (60 lines) and `SemanticDomainSync.cs` (60 lines) are near-identical: a `Sync(T[] before, T[] after, IMiniLcmApi)` collection overload delegating to `DiffCollection.Diff` with a private nested `*DiffApi : ObjectWithIdCollectionDiffApi<T>` (3 methods: `Add`/`Remove`/`Replace`, each 1-3 lines forwarding to `api.CreateX`/`api.DeleteX`/recursive `Sync`), plus a `Sync(T before, T after, IMiniLcmApi)` single-object overload building a `JsonPatchDocument<T>` from `MultiStringDiff.GetMultiStringDiff<T>(nameof(T.Name), ...)` (`PartOfSpeechSync.cs:30-32`, `SemanticDomainSync.cs:30-32`).
- `SenseSync.cs` (68 lines) additionally has to: propagate to `ExampleSentenceSync.Sync(...)` and `PictureSync.Sync(...)` (lines 19-28), diff the `PartOfSpeechId` scalar reference directly (not via the generic collection diff, lines 15-18), and run a bespoke `SenseSemanticDomainsDiffApi` whose `Replace` is a no-op returning 0 (line 65) because semantic-domain identity for a sense is add/remove-only, unlike the top-level list which supports in-place edits.

### 2.8 EF migrations

Two files per migration (`*.cs` hand-authored diff + `*.Designer.cs` machine-generated snapshot) plus a shared, regenerated `LcmCrdtDbContextModelSnapshot.cs` (989 lines, wholly EF-tool output — `LcmCrdt/Migrations/LcmCrdtDbContextModelSnapshot.cs`). Real precedent: the migration that created the `MorphType` table, `20260512104332_AddMorphTypeTable.cs` (60 lines) — hand-inspectable `CreateTable`/`CreateIndex`/`Down` calls, e.g. columns literally mirroring the POCO's public properties 1:1 (`Kind`, `Name`, `Abbreviation`, `Description`, `Prefix`, `Postfix`, `SecondaryOrder`, `DeletedAt`, `SnapshotId`). Both files are produced by running `dotnet ef migrations add` — not hand-typed from scratch, but the CLI has to be invoked and the result reviewed/renamed each time (no evidence of this being wired into an automated build step; confirmed absent, see §5).

### 2.9 Tests

For `PartOfSpeech`/`SemanticDomain`: `MiniLcm.Tests/PartOfSpeechTestsBase.cs` (142 lines), `MiniLcm.Tests/SemanticDomainTestsBase.cs` (131 lines), thin per-project subclasses `LcmCrdt.Tests/MiniLcmTests/PartOfSpeechTests.cs` (19 lines) / `SemanticDomainTests.cs` (20 lines) that just wire the base class to a live `CrdtMiniLcmApi`, plus a validator test and entries in the shared reflection tests below.
For `Sense`: `MiniLcm.Tests/SenseTestsBase.cs` (93 lines), `LcmCrdt.Tests/MiniLcmTests/SenseTests.cs` (19 lines), `LcmCrdt.Tests/Changes/SenseChangeTests.cs` (28 lines), plus a dedicated `MiniLcm.Tests/Validators/SenseValidatorTests.cs`.

Three **shared, reflection-driven regression tests** exist that every new entity/change interacts with without being edited directly, but that will fail loudly if registration is forgotten (this is the closest thing to a generator-adjacent safety net in the codebase — see §5/§7):
- `LcmCrdt.Tests/ConfigRegistrationTests.cs:36-49` (`AllObjectsAreRegistered`) — reflects over the `MiniLcm` assembly for every non-abstract `IObjectWithId` and asserts it appears in `_config.ObjectTypes` (i.e., was passed to `.Add<T>()`), else the test fails asking the author to register it or add it to an exclusion list (`_excludedChangeTypes`/`ExcludedObjectTypes`, lines 12-24).
- `LcmCrdt.Tests/ConfigRegistrationTests.cs:52-88` (`AllChangesAreRegistered`) — same idea for every `IChange` type found in the `LcmCrdtConfig` assembly, including closing open generics (`DeleteChange<>`) over every registered object type (lines 70-86).
- `LcmCrdt.Tests/DataModelSnapshotTests.cs` — four Verify-snapshot tests (`VerifyDbModel` line 59, `VerifyChangeModels` line 75, `VerifyIObjectBaseModels` line 82, `VerifyIObjectWithIdModels` line 89) that dump the EF model / polymorphic type lists to `.verified.txt` approval files; a new entity changes these files and the diff must be manually approved. A fifth test, `VerifyIObjectWithIdsMatchAdapterGetObjectTypeName` (lines 94-107), Auto-Bogus-fakes an instance of every registered `IObjectWithId` derived type and asserts the JSON discriminator string matches what `MiniLcmCrdtAdapter.GetObjectTypeName()` would compute from `.GetType().Name` — this is a real cross-check that the JSON-attribute name (§2.3) and the adapter's reflection-based name (§2.5) haven't drifted apart.
- `LcmCrdt.Tests/Changes/UseChangesTests.cs:160-onward` (`GetAllChanges()`) is **not** reflection-based — it's a manually maintained `IEnumerable<ChangeWithDependencies>` yielding one hand-constructed instance of every non-Delete/non-JsonPatch change type (e.g. `CreatePartOfSpeechChange` at line 167, `CreateSenseChange` at line 171, `CreateSemanticDomainChange` at line 205), used by `CanAddAllChangeTypes`/`CanSyncAllChangesWithDuplicates` (lines 31, 57) to smoke-test that every change can actually be applied end to end. This is exactly the manual step flagged by the `LcmCrdtKernel.cs:393-394` comment.

---

## 3. Quantification for one new entity

Using the two real, simple entities already in the codebase as the model (`PartOfSpeech`/`SemanticDomain`) and cross-checking against the actual historical commits that added `MorphType` (a genuinely new entity added to this exact codebase — see git log below), a new **simple, non-collection-bearing** entity touches:

| # | File | Approx. new/changed lines |
|---|---|---|
| 1 | `MiniLcm/Models/<Entity>.cs` (POCO + `IObjectWithId` impl) | ~25-30 (cf. `SemanticDomain.cs` 31, `PartOfSpeech.cs` 31, `MorphType.cs` 67 w/ enum) |
| 2 | `MiniLcm/Models/IObjectWithId.cs` (`[JsonDerivedType]` line) | 1 |
| 3 | `LcmCrdt/LcmCrdtKernel.cs` `ConfigureCrdt`: `.Add<T>()` (object), `.Add<JsonPatchChange<T>>()`, `.Add<DeleteChange<T>>()`, `.Add<CreateTChange>()` | 4 |
| 4 | `LcmCrdt/Changes/CreateTChange.cs` (hand-written) | ~18-20 (cf. `CreatePartOfSpeechChange.cs` 18, `CreateSemanticDomainChange.cs` 20) |
| 5 | `LcmCrdt/LcmCrdtDbContext.cs` (`IQueryable<T>` accessor) | 1 |
| 6 | `MiniLcm/IMiniLcmReadApi.cs` + `IMiniLcmWriteApi.cs` (method signatures: get-all, get-one, create, update×2, delete) | ~10 |
| 7 | `LcmCrdt/CrdtMiniLcmApi.cs` (CRUD block implementation) | ~30-45 (cf. the `PartOfSpeech` block is 43 lines, `CrdtMiniLcmApi.cs:128-170`; `SemanticDomain` block is 47 lines, `:242-288`) |
| 8 | `MiniLcm/SyncHelpers/TSync.cs` | ~60 (both existing examples are exactly 60) |
| 9 | `MiniLcm/Validators/TValidator.cs` | ~15-20 (`PartOfSpeechValidator.cs` 20, `SemanticDomainValidator.cs` 22) |
| 10 | `MiniLcm/Validators/MiniLcmValidators.cs` (record param + 2 registration lines) | 3 |
| 11 | EF migration pair (`*.cs` + `*.Designer.cs`, tool-generated) + `LcmCrdtDbContextModelSnapshot.cs` diff | ~30 hand-relevant + several hundred machine-generated |
| 12 | Test files: `MiniLcm.Tests/TTestsBase.cs`, `LcmCrdt.Tests/MiniLcmTests/TTests.cs`, validator test | ~150-200 (cf. `PartOfSpeechTestsBase.cs` 142 + `PartOfSpeechTests.cs` 19) |
| 13 | `.verified.txt` snapshot diffs (`DataModelSnapshotTests.*`, regenerated not hand-typed) | mechanical regen, human approval only |
| 14 | `UseChangesTests.GetAllChanges()` — one new `yield return` | ~2 |

**Total: roughly 14 distinct files, on the order of 350-450 touched/added lines** for a *simple* new entity with no owned collections and no custom mutation verbs — consistent with the real `MorphType` addition, whose founding commit `13eabbb5` ("Add morph types to MiniLcm (#1857)") touched **29 files** repo-wide (`git show --stat 13eabbb5`; includes the FieldWorks bridge and frontend TypeScript, which are outside Harmony/LcmCrdt/MiniLcm proper but are real consumers that also had to change), and whose later "give MorphType its own EF table" commit `dc779e88` ("Sync morph type data") touched **19 files**, adding a 729-line/838-line EF-generated Designer file in the process (`git show --stat dc779e88`, `git show --stat 4abf849c`).

Of those ~350-450 lines:
- **Mechanical / pure shape-repetition** (would need only class name + field list + cardinality to generate): the model POCO's data fields and `Copy()` (item 1, when `GetReferences`/`RemoveReference` are no-ops as they are for both `PartOfSpeech` and `SemanticDomain`); the `[JsonDerivedType]` line (item 2); all 4 kernel registration lines (item 3); the DbContext accessor (item 5); the interface method signatures (item 6); the bulk of the CRUD block in `CrdtMiniLcmApi.cs` (item 7 — every method is `AddChange(new CreateTChange(...))` / `repo.Ts.SingleOrDefaultAsync(...)` / `AddChange(new DeleteChange<T>(id))`, differing only in the entity name); the `MultiStringDiff`-based sync helper skeleton (item 8, when the only diffable field is a `MultiString Name`); the validator record/registration (item 10); the migration's `CreateTable` column list (item 11, mirrors the POCO 1:1 as seen in `20260512104332_AddMorphTypeTable.cs`); the `GetAllChanges()` line (item 14).
- **Requiring a judgement call**: anything about relationships — is the field owned (cascade-delete, `HasForeignKey(...).OnDelete(Cascade)`) or a reference (`OnDelete(SetNull)`, needs `RemoveReference` logic, needs entry in `GetReferences()`)? Is a collection field a true owned/embedded JSON column (`jsonb` converter as for `Sense.SemanticDomains`) or a real child table (`HasMany().WithOne()` as for `Entry.Senses`)? Does the entity need bespoke mutation verbs beyond create/patch/delete (add-to-collection, replace-in-collection, move-between-parents, reorder) — each such verb is a hand-written `Change` class *and* a hand-written API method *and* a hand-written sync branch, none of which is derivable from field-shape alone. Whether an entity is a closed/"predefined" vocabulary (canonical GUID list, `MiniLcm/Validators/CanonicalGuidsPartOfSpeech.cs:9-20`, seeded via `LcmCrdt/Objects/PreDefinedData.cs:8-11`) is also a judgement call, not derivable from field shape.

For `Sense` specifically (the rich case), the incremental cost over the simple-entity baseline is concentrated entirely in the "judgement call" bucket: 5 extra change classes (`SetPartOfSpeechChange`, `MoveSenseToEntryChange`, `AddSemanticDomainChange`, `RemoveSemanticDomainChange`, `ReplaceSemanticDomainChange`, plus 4 picture-ordering changes), 7 extra kernel registration lines, a 22-line EF configuration lambda instead of zero, a linq2db association/expression override (`LcmCrdtKernel.cs:145,180-183`) for the `SemanticDomainRows` query-rewrite shadow property, and a `SenseSync.cs` that fans out into two other sync helpers (`ExampleSentenceSync`, `PictureSync`) plus a custom `DiffApi` with a non-generic `Replace` (§2.7).

---

## 4. Registration mechanism

**Explicit, imperative builder calls at startup — no attributes, no reflection scanning, no source generator drives the Harmony/EF/Change layer.** The single call site is `LcmCrdt/LcmCrdtKernel.cs`, method `ConfigureCrdt` (`LcmCrdtKernel.cs:190-403`), invoked from `AddLcmCrdtClientCore` (`LcmCrdtKernel.cs:65-67`):

```csharp
config.ObjectTypeListBuilder
    .CustomAdapter<IObjectWithId, MiniLcmCrdtAdapter>()
    .Add<Entry>(builder => { ... })
    .Add<Sense>(builder => { ... })
    ...
    .Add<PartOfSpeech>()
    .Add<Publication>()
    .Add<SemanticDomain>()
    .Add<ComplexFormType>()
    ...
    .Add<MorphType>()
    .Add<ComplexFormComponent>(builder => { ... });

config.ChangeTypeListBuilder.Add<JsonPatchChange<Entry>>()
    .Add<JsonPatchChange<Sense>>()
    ...
    .Add<DeleteChange<Entry>>()
    .Add<DeleteChange<Sense>>()
    ...
    .Add<SetPartOfSpeechChange>()
    .Add<MoveSenseToEntryChange>()
    ...
```
(`LcmCrdtKernel.cs:193-395`, elided for length — the real file has ~50 chained `.Add<T>()` calls.)

The builders themselves (`harmony/src/SIL.Harmony/Config/ObjectTypeListBuilder.cs:9-93`, `harmony/src/SIL.Harmony/Config/ChangeTypeListBuilder.cs:8-35`) are plain C# classes with `CheckFrozen()`/`Freeze()` guards (`ObjectTypeListBuilder.cs:16-37`) — a `Freeze()` is called once the config is turned into JSON serializer options, after which further `.Add<T>()` calls throw (`ChangeTypeListBuilder.cs:20-23`, `ObjectTypeListBuilder.cs:34-37`). There is no `[CrdtEntity]`-style attribute anywhere in either repo (grep for `Attribute` in `harmony/src/SIL.Harmony/**` turns up none related to entity/change registration), and no assembly scan for "every class implementing `IObjectWithId`" is used for *registration* (only for *verification*, see §2.9's `ConfigRegistrationTests`).

`IPolyType` (`harmony/src/SIL.Harmony/Entities/IPolyType.cs:6-14`) is a static-abstract-member interface (`static abstract string TypeName { get; }`) used as a compile-time contract for "this type can name itself for JSON discrimination" — `ISelfNamedType<T>` (`IPolyType.cs:11-14`) is a convenience default (`TypeName => typeof(T).Name`) used by nearly every hand-written `LcmCrdt` change class (e.g. `CreateSenseChange : ..., ISelfNamedType<CreateSenseChange>` at `CreateSenseChange.cs:10`). This is a mild form of convention (name defaults to the CLR type name) but each type must still explicitly opt in by declaring the interface — nothing scans for it.

`CustomAdapterProvider<TCommonInterface, TAdapter>.Add<T>()` (`harmony/src/SIL.Harmony/Adapters/CustomAdapterProvider.cs:31-46`) is the one place object registration and JSON-derived-type registration happen together (see §2.5).

**Convention-based/bulk registration that *does* exist, but only downstream of the central list, not for the list itself:**
- `LcmCrdtKernel.AllObjectTypes()` / `AllChangeTypes()` (`LcmCrdtKernel.cs:405-417`) build a throwaway `CrdtConfig`, run `ConfigureCrdt` against it, and return `config.ObjectTypes`/`config.ChangeTypes` — i.e., **the canonical list is reflected back out** for consumers, rather than the list itself being reflection-derived.
- The TypeScript type generator (`FwLiteShared/TypeGen/ReinforcedFwLiteTypingConfig.cs:83-86`) does exactly this: `var config = new CrdtConfig(); LcmCrdtKernel.ConfigureCrdt(config); builder.ExportAsInterfaces([..config.ObjectTypes, ...], ...)` — every entity added to `ConfigureCrdt`'s object list is **automatically** exported as a TypeScript interface with zero additional registration (see §5).

---

## 5. Does code generation already exist?

**Yes, in two places — but neither touches the Harmony/EF/Change-registration layer itself.** No Roslyn source generator, T4 template, or MSBuild hook exists in the `harmony` repo (`harmony/src/Directory.Build.targets` is 20 lines of NuGet packaging metadata only — `Directory.Build.targets:1-19` — no codegen). In `languageforge-lexbox/backend/FwLite` there is no `Directory.Build.targets`/`.props` at all (searched, none found) and no `.tt`/`.ttinclude` files anywhere in either repo (searched, none found). Concretely:

1. **`BeaKona.AutoInterfaceGenerator`** — a real Roslyn incremental source generator, referenced in `MiniLcm/MiniLcm.csproj:7`, `FwLiteShared/FwLiteShared.csproj:9`, `FwLiteProjectSync/FwLiteProjectSync.csproj:10`. Used via the `[BeaKona.AutoInterface]` attribute on a private field, e.g. `MiniLcm/Validators/MiniLcmApiValidationWrapper.cs:19-24`:
   ```csharp
   public partial class MiniLcmApiValidationWrapper(IMiniLcmApi api, MiniLcmValidators validators) : IMiniLcmApi
   {
       [BeaKona.AutoInterface(IncludeBaseInterfaces = true, MemberMatch = BeaKona.MemberMatchTypes.Any)]
       private readonly IMiniLcmApi _api = api;
       // ********** Overrides go here **********
       public async Task<Publication> CreatePublication(Publication pub) { ... }
   ```
   The generator emits, at compile time, forwarding implementations for **every** `IMiniLcmApi` member not explicitly overridden below. This means the decorator/wrapper classes (`MiniLcmApiValidationWrapper`, `MiniLcmApiQueryNormalizationWrapper`, `MiniLcmApiWriteNormalizationWrapper`, `MiniLcmApiNotifyWrapper`, `DryRunMiniLcmApi`, `ResumableImportApi`) need **zero edits** when a new entity's CRUD methods are added to `IMiniLcmApi`, unless that wrapper needs to add cross-cutting behavior for the new methods (validation, normalization, etc.) — in which case a hand-written override is still required. This is genuine, working code generation, but it operates one layer above Harmony/EF and does not generate the change classes, EF config, or sync helpers.
2. **Reinforced.Typings** — an MSBuild-time C#→TypeScript generator, referenced in `FwLiteShared/FwLiteShared.csproj` and configured by `FwLiteShared/Reinforced.Typings.settings.xml` (imported into every build via the NuGet package's own `.targets` file, confirmed by the settings file's own comment at lines 4-11) with `RtConfigurationMethod` pointed at `FwLiteShared.TypeGen.ReinforcedFwLiteTypingConfig.Configure` (`Reinforced.Typings.settings.xml:37`, method at `FwLiteShared/TypeGen/ReinforcedFwLiteTypingConfig.cs:38-71`). Output lands in `frontend/viewer/src/lib/dotnet-types/generated-types/` (`Reinforced.Typings.settings.xml:57`). As noted in §4, this reflects `LcmCrdtKernel.ConfigureCrdt`'s object-type list (`ReinforcedFwLiteTypingConfig.cs:83-86`) to auto-export every registered entity as a TS interface — real, working, reflection-driven bulk generation, but it generates **client-side DTO shapes**, not any part of the Harmony persistence/CRDT layer.

**Negative result, explicitly**: no generator produces `Change<T>` subclasses, EF `EntityTypeBuilder` configuration, `SyncHelpers`, `IMiniLcmApi` method bodies, or EF migrations. `dotnet ef migrations add` is a developer-invoked CLI command (evidenced by the migration `.Designer.cs` files' generated-code headers and their large, mechanically-uniform diffs in the `MorphType` commits, §2.8/§3) — it is real code generation for the migration/snapshot files specifically, but it is not wired into any automated build target (no `Directory.Build.targets`/pre-build hook invokes it), so a human must remember to run it.

---

## 6. `SyncHelpers` structure and repetition

`MiniLcm/SyncHelpers/` — 14 files, 1383 total lines (`wc -l`):

| File | Lines | Role |
|---|---|---|
| `DiffCollection.cs` | 270 | generic engine: `CollectionDiffApi<T,TId>`/`ObjectWithIdCollectionDiffApi<T>` abstract bases (lines 10-33), `Diff`/`DiffAndGetAdded` (order-agnostic set diff, lines 64-115), `DiffOrderable`/`DiffPositions` (position-aware diff using `JsonDiffPatcher`, lines 117-215) — reused by every entity |
| `MultiStringDiff.cs` | 60 | generic: `GetMultiStringDiff<T>(path, MultiString before, MultiString after)` and the `RichMultiString` overload — reused by every localized-name field |
| `IntegerDiff.cs` | 16 | generic scalar diff helper |
| `SimpleStringDiff.cs` | 16 | generic scalar diff helper |
| `EntrySync.cs` | 337 | entity-specific (Entry: the aggregate root, fans out to Sense/ComplexForm/Publication sync) |
| `SenseSync.cs` | 68 | entity-specific |
| `ExampleSentenceSync.cs` | 121 | entity-specific |
| `PictureSync.cs` | 79 | entity-specific |
| `ComplexFormTypeSync.cs` | 56 | entity-specific |
| `MorphTypeSync.cs` | 76 | entity-specific |
| `PartOfSpeechSync.cs` | 60 | entity-specific |
| `PublicationSync.cs` | 64 | entity-specific |
| `SemanticDomainSync.cs` | 60 | entity-specific |
| `WritingSystemSync.cs` | 100 | entity-specific |

The 4 generic files (`DiffCollection.cs`, `MultiStringDiff.cs`, `IntegerDiff.cs`, `SimpleStringDiff.cs` — 362 lines) are written once and reused by all 10 entity-specific files. Within an entity-specific file, the repeated shape (verified by direct comparison of `PartOfSpeechSync.cs` and `SemanticDomainSync.cs`, both exactly 60 lines) is:
1. A `Sync(T[] before, T[] after, IMiniLcmApi)` collection overload — 5 lines, always `DiffCollection.Diff(before, after, new TDiffApi(api))`.
2. A `Sync(T before, T after, IMiniLcmApi)` single-object overload — 7 lines, always build a `JsonPatchDocument<T>`, call `api.SubmitUpdateT` if non-empty.
3. A `TDiffToUpdate(...)` method building the patch from `MultiStringDiff.GetMultiStringDiff<T>(nameof(T.Name), ...)` — mechanical for entities whose only diffable field is a name; **not mechanical** the moment an entity has more than one diffable scalar/multi-string field (nothing generalizes "diff every property"; each field needing patch-diffing is a hand-added `patchDocument.Operations.AddRange(...)` call).
4. A private nested `TDiffApi : ObjectWithIdCollectionDiffApi<T>` with `Add`/`Remove`/`Replace` — mechanical when `Replace` just recurses into step 2, **not mechanical** when replace needs special-casing (e.g. `SenseSync.cs:63-66`'s `SenseSemanticDomainsDiffApi.Replace` returns `Task.FromResult(0)` unconditionally because in-place replace isn't meaningful for a sense's attached semantic domains — a modeling decision, not a shape fact).

`EntrySync.cs` at 337 lines is the outlier: it is not "one entity, one file" so much as "the orchestrator that walks the whole aggregate," recursively invoking `SenseSync`, `ExampleSentenceSync`, `PictureSync`, `ComplexFormTypeSync`, `PublicationSync` for nested collections. Adding a new top-level (non-nested) entity does not touch `EntrySync.cs`; adding a new field/collection *on* `Entry` or `Sense` does.

**Estimate**: for a simple, name-only new entity, `TSync.cs` is ~90% boilerplate (only the entity name and `nameof(T.Name)` change vs. `SemanticDomainSync.cs`/`PartOfSpeechSync.cs`). For a rich entity, the fraction requiring judgement rises with (a) how many distinct fields need independent patch-diff logic and (b) how many nested collections need their own diff API with non-trivial `Add`/`Remove`/`Replace` semantics.

---

## 7. Assessment: what's mechanically derivable, what isn't

**Mechanically derivable from a field-level shape description (class, field, type, owning-vs-reference, atomic/collection/sequence):**
- The model POCO's public properties, and `Copy()` (a straight field-by-field constructor call — every observed `Copy()` implementation is exactly this, e.g. `SemanticDomain.cs:20-30`, `PartOfSpeech.cs:21-30`, `MorphType.cs:52-66`).
- `GetReferences()`/`RemoveReference()` **for entities with no references** (empty body — true for `PartOfSpeech`, `SemanticDomain`, `MorphType`, `ComplexFormType`). For entities *with* references, the shape (which fields are FK-like) plus a declared owning/reference/cascade policy is enough — no additional judgement beyond what "owning vs. reference" already encodes.
- The `[JsonDerivedType]` line, the `ObjectTypeListBuilder.Add<T>()` call (when no custom `EntityTypeBuilder` lambda is needed), the `ChangeTypeListBuilder.Add<JsonPatchChange<T>>()`/`Add<DeleteChange<T>>()` lines, the `LcmCrdtDbContext` `IQueryable<T>` accessor, the `IMiniLcmReadApi`/`IMiniLcmWriteApi` CRUD signatures, the `CrdtMiniLcmApi` CRUD method bodies (get-all/get-one/create/delete are 100% pattern-following once the model shape and a "create" constructor signature are known), the `MiniLcmValidators` record parameter + 2 registration lines, and the `TSync.cs` collection-overload + `TDiffApi` skeleton **when `Replace` is "recurse into single-object sync"** and the only diffable field is a name.
- The EF migration's `CreateTable` column list (1:1 with POCO properties, confirmed by `20260512104332_AddMorphTypeTable.cs` mirroring `MorphType.cs`'s properties exactly) — though the migration file itself is normally produced by the `dotnet ef` CLI reading the already-updated model, not hand-typed.
- `UseChangesTests.GetAllChanges()`'s one new `yield return` for a create-change with no dependencies.

**Genuinely not derivable from field shape alone — concrete obstacles actually observed:**
1. **Owning vs. reference is a modeling decision with different code on each branch, not a boolean flag with one template.** Owning children need `HasMany().WithOne().HasForeignKey().OnDelete(Cascade)` (`LcmCrdtKernel.cs:222-225` for Sense→ComplexFormComponent) *and* cascading `DeletedAt` propagation inside `RemoveReference` (`Sense.cs:38-39`); references need `OnDelete(SetNull)` (`LcmCrdtKernel.cs:229-232`) *and* null-out logic (`Sense.cs:40-44`); reference-collections need array-filter logic (`Sense.cs:45`) *and* a dedicated Add/Remove/Replace `Change` class each (`AddSemanticDomainChange.cs`, `RemoveSemanticDomainChange.cs`, `ReplaceSemanticDomainChange.cs`) because CRDT semantics require each mutation to be independently commit-able and replay-safe — a plain "update the whole list" JSON patch is unsafe for concurrent edits (this is *why* `JsonPatchChange<T>` is explicitly excluded for collection-shaped or cascade-relevant types, e.g. `ConfigRegistrationTests.cs:16-23`'s exclusion list, and why `JsonPatchValidator.ValidatePatchDocument` (`LcmCrdt/Changes/JsonPatchChange.cs:32-55`) actively rejects index-based array operations).
2. **Whether a collection is a real child table or an embedded `jsonb` array is a storage decision that changes both the EF config shape and the query layer.** `Sense.SemanticDomains` is `jsonb` with a hand-written linq2db expression rewrite for queryability (`LcmCrdtKernel.cs:145,180-183`) specifically because it's a denormalized reference list, not a true 1:many; `Entry.Senses` is a real FK-based child table. No field-shape description ("collection of X") disambiguates these two without an explicit storage-strategy decision.
3. **Domain-specific mutation verbs cannot be derived from shape at all.** "Move a sense to a different entry" (`MoveSenseToEntryChange.cs`), "set the main publication" (`SetMainPublicationChange.cs`), "reorder a picture" (`ReorderSensePictureChange.cs`) are business operations, not CRUD-on-a-field; each needs its own `Change` subclass, its own `IMiniLcmApi` method, and its own sync-side call. A generator driven only by field metadata has no way to know these verbs are needed, let alone their semantics (e.g. `SetPartOfSpeechChange.ApplyChange` at `SetPartOfSpeechChange.cs:11-29` has to re-fetch and null-check the referenced `PartOfSpeech` from `IChangeContext.GetCurrent<T>`, silently dropping the reference if the target was deleted concurrently — a CRDT-conflict policy decision, not a shape fact).
4. **`Replace` semantics inside a nested collection diff are not uniform** — `SenseSync.cs:63-66`'s `SenseSemanticDomainsDiffApi.Replace` deliberately no-ops, while `PartOfSpeechSync.cs`'s top-level `Replace` recurses into a real patch-diff. Getting this wrong changes CRDT convergence behavior, not just output shape.
5. **Two manual, unenforced-by-the-compiler bookkeeping steps exist** that a generator would have to know to also emit: the `UseChangesTests.GetAllChanges()` entry (flagged by comment only, `LcmCrdtKernel.cs:393-394`), and approving the regenerated `DataModelSnapshotTests.*.verified.txt` files. Both are currently caught, if forgotten, only by CI test failures (`ConfigRegistrationTests.AllObjectsAreRegistered`/`AllChangesAreRegistered` reflect over the assemblies and fail with a message pointing back at `LcmCrdtKernel.ConfigureCrdt` — `ConfigRegistrationTests.cs:46-47,66-67,79-80`), not by any static/generated guarantee.
6. **Possibility-list entities (`PartOfSpeech`, `SemanticDomain`, `MorphType`, `ComplexFormType` all implement `IPossibility`, `MiniLcm/Models/IPossibility.cs:3-6`) share an obvious common shape, but that shape is not exploited anywhere to reduce per-entity code** — `IPossibility` is a bare marker (one `Name` property, no default interface methods used for CRUD/sync/validation), and `CrdtMiniLcmApi.cs`/`*Sync.cs`/`*Validator.cs` still hand-duplicate the identical logic per type (confirmed: `PartOfSpeechSync.cs` and `SemanticDomainSync.cs` are both exactly 60 lines and structurally identical apart from type name and the `Code` field). This is the single clearest evidence in the codebase that **a generator targeting "possibility-list-shaped" entities specifically would eliminate the largest already-duplicated block** (Sync + CRUD-API + Validator, roughly 130 lines/entity) without needing to solve the harder owning/reference/mutation-verb problems in point 1-4 above, which remain out of reach for anything short of a human encoding CRDT-safety judgement calls per relationship.

**Where I could not verify a claim from code** (flagged per the task's instruction rather than asserted): I did not find, and explicitly searched for, any generator/build-hook that runs `dotnet ef migrations add` automatically — absence is based on a negative grep across both repos for `Directory.Build.targets`/`.props` and any `Target Name=` blocks referencing `ef` or `migrations`; it remains possible such a hook exists in a CI YAML file outside the two repos' source trees, which was out of scope for this inventory (no CI config directories were inspected).
