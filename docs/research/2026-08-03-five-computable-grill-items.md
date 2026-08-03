# Five grill items that were never decisions — `A3` `A4` `B8` `C10` `C13`

*Research of 2026-08-03. All five were mislabelled as owner decisions in the first pass of
[grill-plan-a.md](../grill-plan-a.md). They are countable, and here are the counts.*

**Verdicts at a glance:**

| Item | Answer |
| --- | --- |
| `A3` | **No blocker, but no production-grade type either.** `IProjectIdentifier` is 7 trivial public members; write ~15 lines. |
| `A4` | **Clean — and the premise was wrong.** `System.Text.Json` is *already* in FieldWorks' resolved net48 graph at 9.x, above Motif's 8.0.5 floor. |
| `B8` | **24 fields / 10 classes, not 37 / 19.** 13 of the 37 rows ADR 0012 treats as ground truth are not read by `HCLoader` at all. |
| `C10` | **Exactly 4 rows**, all `Group=lexical`. |
| `C13` | **Hand-written, and it already exists** — call `IPhRegularRule.FeatureConstraints`, do not regenerate the walk. |

---

## `A3` — is `IProjectIdentifier` publicly constructible with `kMemoryOnly`?

`BackendProviderType` and `IProjectIdentifier` are both fully public
(`liblcm/src/SIL.LCModel/IProjectIdentifier.cs:14-56, :64-120`). The interface is **7 trivial members**
— `UiName`, `Path`, `Handle`, `PipeHandle`, `Name`, `ProjectFolder`, `Type` — all string/enum getters,
none referencing anything internal.

Two implementations exist, and neither is ideal:

| Type | Accessibility | Location |
| --- | --- | --- |
| `SimpleProjectId` | **internal** | `Infrastructure/Impl/SimpleProjectId.cs:21` |
| `TestProjectId` | **public** | `tests/SIL.LCModel.Tests/TestProjectId.cs:12` |

`TestProjectId` is exactly what liblcm's own tests use (`LcmCacheTests.cs:200`:
`new TestProjectId(BackendProviderType.kMemoryOnly, null)`), and its project sets
`<IsPackable>true</IsPackable>`, so it *is* consumable — FieldWorks already references the package
(`Directory.Packages.props:67`). But it is test infrastructure that drags NUnit and Moq along, not
something to put in a production runner path.

**Why `kMemoryOnly` works without touching internals.** `MemoryOnlyBackendProvider` is `internal
sealed`, but the consumer never constructs it. `LcmServiceLocatorFactory.cs:151-156` switches on
`projectId.Type` and wires `.Use<MemoryOnlyBackendProvider>()` *inside* `SIL.LCModel`, which has access
to its own internals. The caller's `IProjectIdentifier` only has to report `Type == kMemoryOnly`.

**Answer: no blocker.** Write the ~15-line class rather than depending on a Tests package.

---

## `A4` — does `System.Text.Json` land cleanly in FieldWorks' `net48` graph?

**The premise in the question was wrong: it is already there, before Motif is added.**

From a real restored lockfile — `FieldWorks/Obj/AlloGenModel/project.assets.json`, target
`.NETFramework,Version=v4.8`:

```
Microsoft.Extensions.DependencyModel/9.0.14 ->
  {"System.Buffers":"4.5.1","System.Memory":"4.5.5",
   "System.Text.Encodings.Web":"9.0.14","System.Text.Json":"9.0.14"}
```

`Directory.Packages.props:44` pins `Microsoft.Extensions.DependencyModel` to `9.0.16` — for an
unrelated ICU reason (the comment cites a `TypeInitializationException` at 9.0.17, PR #1000) — and
`CentralPackageTransitivePinningEnabled=true` (`:11`) injects that as a floor into **every** project.
That is why `System.Text.Json` appears even in projects whose source never mentions it.

Motif requests `System.Text.Json 8.0.5`. Every floor in its net462 dependency group is already met or
exceeded in FieldWorks' live graph — `System.Memory` pinned at `4.6.3`, `System.Buffers` `4.6.1`,
`System.Runtime.CompilerServices.Unsafe` `6.1.2`, `Microsoft.Bcl.AsyncInterfaces` `9.0.14`,
`System.ValueTuple` `4.6.1`. NuGet resolves to the highest asserted floor, so **Motif's requirement is
subsumed, not in conflict.** `AutoGenerateBindingRedirects` is on repo-wide
(`Directory.Build.props:133-134`), covering the 8.0.0.0-vs-9.0.x assembly-version gap by the same
mechanism already relied on for the documented `System.Drawing.Common` fix (LT-22382).

**Answer: clean. No new pins required.** Newtonsoft is correctly ruled out by ADR 0007, but the
question is moot — there is nothing to escape from.

*One thing not directly read:* whether `Microsoft.Extensions.DependencyModel` **9.0.16** specifically
still declares the `System.Text.Json` dependency. Only the 8.0.0 and 9.0.9 nupkgs were in the local
cache; both do, as does the 9.0.14 resolved in the lockfile. Very likely, not directly observed.

---

## `B8` — L0's object-creation closure, and a manifest defect found on the way

**The filter reproduces exactly.** `Scope=in`, `HcReachable=yes`, `Group≠grammar` gives **37 rows
across 19 classes**, matching ADR 0012 (`adr/0012:33-45`). *Independently verified.*

**But 13 of those 37 fields are not read by `HCLoader.cs` at all.** Direct grep of each field's real
property name against the 2,837-line file finds **24 fields across 10 classes** confirmed. The 13
absent:

`CmPossibilityList.Abbreviation` · `LangProject.Annotations` · `LexDb.VariantEntryTypes` ·
`LexEtymology.Form` · `LexEtymology.Gloss` · `LexPronunciation.Form` · `LexRefType.Members` ·
`LexSense.Senses` · `MoMorphType.Prefix` · `ReversalIndex.Entries` ·
`ReversalIndexEntry.PartOfSpeech` · `ReversalIndexEntry.Senses` · `StText.RightToLeft`

*Spot-verified independently:* `ReversalIndex`, `LexEtymology`, `LexPronunciation`, `LexRefType`,
`SubSensesOS`, `AnnotationsOC` all return **zero** hits. `MoMorphType.Prefix`'s ten apparent hits are
`MoMorphTypeTags.kMorphPrefix` / `kguidMorphPrefix` — class GUID constants, not the `Prefix` field. The
`StText.RightToLeft` hits are HermitCrab's own `Direction.RightToLeft` enum. Both are false positives
on a bare-name match — **exactly the failure mode `HcReachable` was introduced to correct** (manifest
`README.md:26`, issue D1).

Widening the search across `ParserCore/` finds these terms only in `XAmplePropertiesPreparer.cs` and
`XAmplePropertiesXAmpleDataFilesAugmenter.cs` — the **XAmple** pipeline, not HermitCrab's.

**And all 13 carry Tier-C boilerplate rationale** — *"Core authorable content of the X construct
(kind/sig)"* — with zero citation. This is the same defect the
[classification audit](2026-08-03-manifest-trust-audit.md) found, showing up inside the 37-row set ADR
0012 treats as ground truth. **The two investigations corroborate each other.**

### The closure itself

From liblcm's factory code (`DomainImpl/FactoryAdditions.cs`):

```
Tier 0 (bootstrap, pre-exists)
  LangProject                       (singleton root)
   └─ LexDb                         (LangProject.LexDbOA — HCLoader.cs:256)
       └─ MoMorphType instances     fixed GUIDs, under LexDb's morph-types list

Tier 1  LexEntryFactory.Create(morphType, lexemeForm, gloss, sandboxMSA)
        FactoryAdditions.cs:470-504 — requires a Tier-0 MoMorphType.
        Cascades atomically:
         ├─ LexSense   (FactoryAdditions.cs:104-115, owned)
         └─ MoForm     (OverridesLing_MoClasses.cs:3274-3317 — picks Stem vs Affix
                        allomorph by morphType.Guid, sets entry.LexemeFormOA, and
                        sets allomorph.MorphTypeRA unconditionally at :3308)

Tier 2  extra MoForm/MoStemAllomorph/MoAffixAllomorph → LexEntry.AlternateFormsOS
        LexSense.MorphoSyntaxAnalysis → an MSA under LexEntry.MorphoSyntaxAnalysesOC
        Optional rel fields are zero-cardinality-valid, so not creation-blocking —
        but each pulls a grammar object forward the moment it is populated.

Tier 3  LexEntryRef      → ≥2 existing LexEntry/LexSense + seeded LexEntryType
        LexEntryInflType → owned FsFeatStruc; target of MoInflAffixSlot via reverse ref
```

**A minimal valid `LexEntry` needs 4 classes** — `LangProject`, `LexDb`, `MoMorphType`, `LexEntry` —
with `LexSense` and `MoForm` auto-created.

**Fully populating the confirmed L0 field set reaches into grammar.** `PhEnvironment`, `MoInflClass`,
`PartOfSpeech`, `MoInflAffixSlot`, and `FsFeatStruc` are all G0/G1 in ADR 0012's own build order. That
is a **second cost the ADR does not state**, beyond its admitted "L0 pulls allomorph/sequence/reparent
forward." Two of the confirmed classes — `LexEntryRef` and `LexEntryInflType` — do this despite being
`Group≠grammar`.

**This closes issue B21** (*"L0's object-creation closure is uncomputed"*), and reopens the count.

---

## `C10` — how many in-scope rows are `AssessPoisonsCache`?

**Exactly 4.** *Independently reproduced from the TSV; the whole file has 4 `yes`, 469 `no`, 425 blank.*

| Class | Field | Verbs |
| --- | --- | --- |
| `LexEntry` | `CitationForm` | `set\|clear` |
| `LexEntry` | `LexemeForm` | `create\|delete` |
| `MoForm` | `Form` | `set\|clear` |
| `MoForm` | `MorphType` | `set\|clear` |

All `Group=lexical`. The single production reader is confirmed:
`DryRun/DerivedCachePoisoningOperationKinds.cs`, consumed by `DryRun/ProposalDryRunner.cs` — matching
ADR 0016's description of what it retires.

**Four rows is small enough that "keep, retire, or repoint" is now a cheap decision.**

---

## `C13` — where must the alpha-variable 24-per-rule check live?

`HCLoader.cs:37-41` declares the fixed 24-entry Greek `VariableNames` array. The assignment
(`HCLoader.cs:2003-2011`) is **unindexed and throws past 24**:

```csharp
foreach (IPhFeatureConstraint var in prule.FeatureConstraints)
{
    variables[var] = VariableNames[i];   // throws at the 25th
    i++;
}
```

`FeatureConstraints` is **not a stored field**. It is a synthetic `[VirtualProperty]` on
`IPhRegularRule` (`OverridesLing_Lex.cs:7536`), implemented as `GetFeatureConstraintsExcept(null)`
(`:7550-7561`), which walks the four documented roots — `StrucDescOS`, and per RHS `StrucChangeOS`,
`LeftContextOA`, `RightContextOA`.

**But it is not a flat scan of those four fields.** *Verified at `OverridesLing_Lex.cs:7595-7626`:* the
recursion switches on `ClassID` into `PhSequenceContext.MembersRS` and `PhIterationContext.MemberRA`,
and for `PhSimpleContextNC` collects **`PlusConstrRS` before `MinusConstrRS`**, deduplicating by
reference identity so first appearance wins.

**Not manifest-derivable.** The four named fields are only entry points. The recursion runs through
three classes and two fields (`PlusConstrRS`, `MinusConstrRS`) named nowhere in the manifest, with an
ordering rule and a `ClassID` dispatch that flat `(Kind, Card, Sig)` columns cannot encode. A generator
reading manifest rows would see `owning seq` fields of type `PhSimpleContext` and have no signal to
recurse into sibling classes at all, let alone in the right order.

**Where it should live: call the existing property.** liblcm already centralizes this walk once, and
two other consumers treat its enumeration order as canonical — `GrammarJsonServices.cs:650`
(`WriteGuidArray("featureConstraintVariables", rule.FeatureConstraints, ordered: true)`) and
`M3ModelExportServices.cs:578,588`. A pre-apply check should call `rule.FeatureConstraints`, not
regenerate the traversal. Hand-written per construct by nature — either a direct liblcm call from the
dry-run path, or a deliberate byte-for-byte port of `CollectVars` if liblcm cannot be referenced there.
