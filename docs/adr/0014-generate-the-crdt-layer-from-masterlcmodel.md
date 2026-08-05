# ADR 0014 — Generate the CRDT layer from MasterLCModel.xml; Harmony gains primitives only

Status: accepted (2026-07-30). **Retitled in effect: the generation target is LibLCM, not a CRDT layer.**

> **Read this first.** The *method* this ADR settles — generate the LibLCM-shaped layer from
> `MasterLCModel.xml` joined to the manifest, rather than hand-authoring it — is live and is `MOT-3`/`MOT-4`.
> Two things in it are not:
>
> - **The target.** Operations are generated against **LibLCM objects directly**. The merge layer this ADR
>   was written to feed is not on Motif's path; the crosswalk it called "required and non-existent" is
>   withdrawn with it. The filename is kept because ADRs are immutable records and many documents link to it.
> - **Decision 2's line between structure and policy.** It said `Scope`, `Construct`, `ComparisonClass` and
>   `Verbs` are all human judgement. Measured, only the first two are:
>   [ADR 0022](0022-structure-is-derived-policy-is-five-rows.md) derives the other two and keeps five cited
>   exceptions.

Builds on [ADR 0013](0013-harmony-is-the-change-mechanism.md), which settled that Harmony's
`Commit`/`IChange` is the change mechanism. This ADR settles **how the LibLCM-shaped part of that
mechanism gets built** — and answers ADR 0013's closing question about who maintains what.

Evidence: [inventory-liblcm-codegen.md](../inventory-liblcm-codegen.md),
[inventory-harmony-generation-surface.md](../inventory-harmony-generation-surface.md),
[inventory-harmony-conflict-reporting.md](../inventory-harmony-conflict-reporting.md). Claims below
that were re-verified directly against source are cited `path:line`.

## Context — three findings that change the build strategy

### 1. The manifest is a projection of LibLCM's own model file

`liblcm/src/SIL.LCModel/MasterLCModel.xml` is a hand-authored, XSD-validated XML file: 424,797 bytes,
5,368 lines, model version `7000072`, **193 classes**. Counting field declarations:

```
445 <basic>  +  235 <owning>  +  218 <rel>  =  898
manifest/liblcm-inventory.tsv               =  899 lines  =  898 rows + header
```

Exact 1:1. `manifest/liblcm-inventory.tsv` is not an independent inventory that can drift from
LibLCM — it is `MasterLCModel.xml` with classification columns added. The join key already exists.

It is also obtainable without the liblcm source tree: `SIL.LCModel.csproj:125` packs
`MasterLCModel.*` into the NuGet package under `contentFiles/`. (That is not the conventional
`contentFiles/{lang}/{tfm}/` layout, so it may not flow automatically into a `PackageReference`
consumer; it can be read out of the package or the global package cache.)

### 2. LibLCM already generates the majority of itself from that file

Per the inventory: NVelocity templates (the 33 `LcmGenerate/*.vm.cs` files, explicitly
`<Compile Remove>`'d at `SIL.LCModel.csproj:12`), driven by an MSBuild task in
`SIL.LCModel.Build.Tasks`. The `GenerateModel` target declares `Inputs="MasterLCModel.xml"` and
shells to a standalone `GenerateModel.proj` (`SIL.LCModel.csproj:111-119`). Output: 9 gitignored
files, ~154,000 lines — more generated code than the ~149,000 hand-written lines in the same project.

Model-driven generation of a LibLCM-shaped C# layer is not speculative. It is how LibLCM exists.

### 3. The CRDT layer does none of it, and the cost is measurable

Under `LcmCrdt/Changes/`: **38 files, of which 2 are generic over `T`** (`JsonPatchChange<T>`,
a local `SetOrderChange<T>`) **and 36 are concrete one-offs** — `AddSemanticDomainChange`,
`RemoveSemanticDomainChange`, `ReplaceSemanticDomainChange`, `AddTranslationChange`, and so on.
(The inventory reports 34/32 by counting `public class` declarations rather than files; the ratio is
the same.)

Registration is entirely explicit and hand-typed in one method, `LcmCrdtKernel.ConfigureCrdt`:
13 entity types at `:195-310`, and the change list at `:330-378` — including one
`JsonPatchChange<X>` line and one `DeleteChange<X>` line per entity. No attributes, no reflection to
build the list, no convention.

Per the inventory, one new entity touches ~14 files and 350–450 lines, cross-checked against the
real historical `MorphType` addition (29 files repo-wide). **13 entities have cost 36 bespoke change
classes.** Thirty grammar constructs at that ratio is roughly 80 more, plus ~1,400 hand-typed
registration and configuration lines — all of it derivable from `Kind` / `Card` / `Sig` / `Verbs` /
`ComparisonClass`, for rows that already exist in a file LibLCM ships.

This is not a lexbox failing. `SIL.Harmony.Sample`, the library's own reference consumer, has 10
hand-written change classes registered the same way. The generation layer is missing everywhere.

## Decision

1. **Generation, not hand-authoring, is the build strategy for the LibLCM-shaped CRDT layer.**
2. **`MasterLCModel.xml` is the structural source; the manifest supplies policy.** Structure
   (class, field, kind, cardinality, signature) is read from the model file so it tracks LibLCM
   upgrades. Policy (`Scope`, `Construct`, `ComparisonClass`, `Verbs`) comes from the manifest, which
   is human judgement and exists nowhere else. They are joined on `(Class, Field)`, and **a key
   present in one and absent from the other fails the build.** Verified: diffing the actual
   `(Class, Field)` key sets between the two files yields **zero keys present in one and absent from
   the other, and no duplicates in either.** A matching count of 898 alone would not have shown this;
   the key sets were compared.
3. **A third artifact is required and does not yet exist: the MiniLcm ↔ LibLCM name and shape map.**
   The manifest is keyed on *LibLCM* class names; the generation target uses *MiniLcm* type names,
   and they do not correspond by name. `MorphType` is `MoMorphType`; `ComplexFormType` is
   `LexEntryType`; `SemanticDomain` is `CmSemanticDomain`. A MiniLcm type is also not always one
   LibLCM class. This map is hand-maintained, is not derivable from either source, and is a
   prerequisite for decisions 1 and 2 — the model file does **not** connect directly to the
   generation target.
4. **Generated output targets `LcmCrdt`, never `SIL.Harmony` core.** Harmony is domain-free and stays
   that way. It knows about commits, snapshots, and changes — not about `IFsFeatStruc`.
5. **Harmony core gains primitives only**, never domain vocabulary: the converging sequence type,
   an explicit reference-set policy, the cross-owner move rule, and the deferred-diagnostic channel
   (item 7 in [harmony-additions-needed.md](../harmony-additions-needed.md)).
6. **Acceptance gate: regenerate what already ships, and diff.** `IPossibility`
   (`MiniLcm/Models/IPossibility.cs:3`) marks five entities. **Only three are reachable by the
   join in decision 2**, and the gate is scoped to those:

   | MiniLcm entity | LibLCM class | Manifest rows | In scope |
   | --- | --- | ---: | ---: |
   | `PartOfSpeech` | `PartOfSpeech` | 13 | 13 |
   | `MorphType` | `MoMorphType` | 3 | 3 |
   | `ComplexFormType` | `LexEntryType` | 2 | 2 |
   | ~~`SemanticDomain`~~ | `CmSemanticDomain` | 5 | **0 — all out of scope** |
   | ~~`Publication`~~ | `Publication` | 16 | **0 — all out of scope** |

   The generator must reproduce these entities' shipped, tested implementations before it is trusted
   with a construct that has never been written. Whether `CmSemanticDomain` and `Publication` should
   be in scope at all is a manifest question, not a generator question, and is open.

Correctness here is not established by the design being elegant. It is established by regenerating
code that already passes its tests.

### What the gate does not prove

Scoped honestly, so it is not oversold. Across the three reachable classes plus `CmPossibility`,
the gate covers **37 in-scope rows: 34 `unordered`, 3 `positional`, and zero `feeding`, zero
`index-as-identity`, zero `AssessPoisonsCache=yes`.** It does exercise `set|clear` (20),
`create|delete` (8), `addRef|removeRef` (4), and `create|delete|move|reparent` (3).

So it proves the generator reproduces possibility-list CRUD. It says nothing about the two `feeding`
fields, the three `index-as-identity` fields, or any HC-reachable grammar construct — which is
precisely the residue ADR 0013 flagged as the real problem. **Passing this gate licenses generating
the mechanical majority; it does not license the ordered-grammar minority**, which needs its own
proof once the Harmony sequence primitive exists.

## What stays hand-written

- **`CreateChange` bodies**, because they must construct a *valid* entity, and validity is domain
  knowledge the model file does not carry.
- **HCLoader validation rules.**
- **EF migrations.** Regeneration is free for source and not free for a linguist's existing SQLite
  file. This is the one cost generation does not absorb.
- **Enum members**, which live outside `MasterLCModel.xml` (only a type-name override file exists),
  and **custom fields**, which are a pure runtime concept (`AddCustomField`) absent from the model.
- The **semantic and lowering layers** — the actual design work, per ADR 0013.

## Consequences

**This is a three-repo change with a package chain between two of them.**

| Artifact | Repo | Notes |
| --- | --- | --- |
| Generated entities, change classes, EF config, registrations, migrations | **languageforge-lexbox** (`backend/FwLite/LcmCrdt`) | Not our repo, not our review, not our release train |
| Converging sequence, reference-set policy, cross-owner move, diagnostic channel | **harmony** | Consumed by lexbox as NuGet, pinned `SIL.Harmony 0.2.1-rc.225` (`backend/Directory.Packages.props:112-114`) |
| Manifest, classification columns, the generator, semantic + lowering layers | **this repo** | |

Developing across the harmony/lexbox boundary is already supported: `Harmony.App.References.props`,
`Harmony.Core.References.props`, and `Harmony.Linq2db.References.props` swap the `PackageReference`
for a `ProjectReference` to `$(HarmonySourcePath)` when `UseHarmonySource` is set, erroring if the
clone is absent. `LcmCrdt.csproj:35` imports the Linq2db variant.

**Build-time code generation is already accepted practice in lexbox** — `BeaKona.AutoInterfaceGenerator`
(`MiniLcm.csproj:7`, `FwLiteShared.csproj:9`, `FwLiteProjectSync.csproj:10`) and `Reinforced.Typings`
(`FwLiteShared.csproj:17`, which already auto-exports every registered entity to TypeScript). Neither
does what is needed here, and **neither is in the harmony repo** (verified: zero matches). They
establish that a generator is a normal thing to add, not that the tooling exists.

**Consequently the `IPossibility` experiment is a lexbox pull request**, and is worth socialising
there before 30 grammar constructs arrive rather than after.

**LibLCM upgrades become a bounded review task rather than silent drift.** Because the policy columns
are ours and the structural columns are LibLCM's, a new LibLCM field produces a row with structure
and no policy, and decision 2 fails the build until a human classifies it. For a system where a wrong
merge policy corrupts a language project quietly, visible churn beats minimal churn.
