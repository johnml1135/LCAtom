# LibLCM code-generation machinery: inventory

Status: factual inventory, evidence-based. No design or recommendation beyond the assessment in
§9, which is scoped to obstacles actually observed. All paths are relative to
`C:\Users\johnm\Documents\repos\liblcm` unless stated otherwise. Line numbers cited are as of the
repo's current `main` checkout (HEAD `af79e6c` in the sibling LCAtom repo's log; liblcm itself was
read directly from the working tree, not from a pinned commit hash — see individual `git log`
calls below for the one file whose history I checked).

## 1. Authoritative model definition

The model is `src/SIL.LCModel/MasterLCModel.xml`, schema-validated against
`src/SIL.LCModel/MasterLCModel.xsd` (both live in the same directory, `MasterLCModel.xml:22`
tells editors so explicitly).

- Size: 424,797 bytes, 5,368 lines (`MasterLCModel.xml`, confirmed via `wc -l`/`ls -la`).
- Schema: `MasterLCModel.xsd`, 120 lines, plain XSD (`xs:schema`, no external namespace,
  `elementFormDefault="qualified"`).
- Top element is `EntireModel` (`MasterLCModel.xsd:3-10`), carrying a required integer
  `version` attribute. The live document is currently at version `7000072`
  (`MasterLCModel.xml:207`: `<EntireModel ... version="7000072" ...>`).
- `EntireModel` contains 1+ `CellarModule` elements (`MasterLCModel.xsd:11-18`), each with an
  `id`/`num` pair (module namespacing: Cellar=0, Scripture=3, Notebk=4, Ling=5, LangProj=6, etc.,
  inferred from `class.Number = Parent.Number*1000 + num`, see `Class.cs:118-124`).
- Each `CellarModule` contains 0+ `class` elements. The live file has 193 `<class num=...>`
  elements (`grep -c` count).
- Each `class` contains one `props` element containing 0+ of `basic` / `owning` / `rel` (a
  `choice`, `MasterLCModel.xsd:35-45`). Live counts: 445 `<basic>`, 235 `<owning>`, 218 `<rel>` —
  898 field declarations total across 193 classes.
- The file opens with ~200 lines of a mandatory changelog (`MasterLCModel.xml:39-206`) — every
  model change requires a matching entry and a version bump, enforced only by convention/comment,
  not tooling (`MasterLCModel.xml:2-37`, the WARNING 0–4 block, explicitly a hand-authored
  discipline, not a generator-enforced one).

This is a hand-authored XML file, not XMI/UML-tool output, despite internal C# variable names
(`m_Document`, doc comments) still calling it "the XMI file" (e.g. `Model.cs:38`,
`LcmGenerateImpl.cs:38`) — a naming fossil from an earlier UML-tool-based era, not evidence of an
actual XMI/UML source today.

## 2. Metadata carried per class and per field

### Per-class attributes (from `MasterLCModel.xsd:19-34` and `Class.cs`)

| Attribute | XSD | Consumer |
|---|---|---|
| `num`, `id` | required | `Class.cs:118-124` (numeric class ID), `Base.cs:52-55` (`Name`) |
| `abbr` | required NCName | `Class.cs:148-151` (`Abbreviation`) |
| `abstract` | required boolean | `Class.cs:134-140` (`IsAbstract`; `CmObject` is hard-forced abstract regardless of the XML value, `Class.cs:138`) |
| `base` | optional NCName | `Class.cs:159-189` (`BaseClass`, resolved by searching all modules) |
| `depth` | optional integer | `Class.cs:225-232` (`Depth`, defaults 0) |
| `singleton` | optional boolean, default false | `Class.cs:70-77` (`IsSingleton`) |
| `owner` | optional enum `none`\|`required`\|`optional`, default `required` | `Class.cs:103-110` (`OwnerStatus`) |
| `generateBasicCreateMethod` | optional boolean, default true | `Class.cs:88-95` (`GenerateFullCreateMethod`) |
| `<comment>`/`<notes>` (child elements, each 1+ `<para>`) | optional | `Class.cs:197-217`, rendered as XML-doc comments via `Base.AsMSString` (`Base.cs:78-87`) |

`Class.cs` also derives collections not present as literal attributes: `SubClasses` (reverse
lookup by `base`, `Class.cs:285-300`) and `Superclasses` (walks `BaseClass` to the root,
`Class.cs:716-727`).

### Per-field attributes (from `MasterLCModel.xsd:46-71` and `Property.cs`/`RelationalProperty.cs`)

| Attribute | Applies to | Consumer |
|---|---|---|
| `num`, `id` | all | `Property.cs:84-90` (flid = `Parent.Number*1000+num`), `Base.cs:52-55` (name) |
| `sig` | all (`coreGroup`) | `Property.cs:98-108` — target type name for `rel`/`owning` (a class name), or scalar type tag for `basic` |
| `card` | `owning`/`rel` only | required enum `atomic`\|`seq`\|`col` (`MasterLCModel.xsd:106-109`), read in `RelationalProperty.cs:60-78` |
| `internalSetter` | `basic` only (`coreGroup`) | optional boolean, `Property.cs` — read directly off the XML node elsewhere (see `internalSetter="true"` on `Segment.BeginOffset`, `MasterLCModel.xml:264`); consumed as `IsSetterInternal` |
| `min`/`max` | `basic` only | declared in XSD (`MasterLCModel.xsd:52-53`) but **not read by any `.cs` file** in `SIL.LCModel.Build.Tasks` (grep found no reference) — schema-legal, generator-dead |
| `big` | not in XSD at all | read ad hoc via `m_node.Attributes["big"]` (`Property.cs:242-255`, `IsBig`) — an undeclared/legacy attribute the schema doesn't know about |
| element name (`basic` vs `owning` vs `rel`) | structural | distinguishes owning-vs-reference: `RelationalProperty.IsOwning` is literally `m_node.Name == "owning"` (`RelationalProperty.cs:38-41`) |

Cardinality/ownership/vector-shape is a derived cross-product exposed as LINQ-filtered
collections on `Class`, not stored directly: `AtomicProperties`, `AtomicRefProperties`,
`AtomicOwnProperties`, `VectorProperties`, `OwningProperties`, `CollectionOwnProperties`,
`SequenceOwnProperties`, `CollectionRefProperties`, `SequenceRefProperties`, `ReferenceProperties`,
`CollectionProperties`, `SequenceProperties` (`Class.cs:307-521`), plus by-scalar-type buckets
(`IntegerProperties`, `BooleanProperties`, `GuidProperties`, `DateTimeProperties`,
`GenDateProperties`, `BinaryProperties`, `StringProperties`, `MultiProperties`,
`UnicodeProperties`, `TextPropBinaryProperties`, `Class.cs:528-681`, all keyed off
`Property.Signature` string equality, not an enum).

### What is absent from the model source

- **No enum value declarations.** `sig="Integer"` is the only type tag for what are semantically
  enum-backed fields (e.g. `StStyle.Type`). The actual C# enum type name is supplied out-of-band
  by `src/SIL.LCModel/LcmGenerate/IntPropTypeOverrides.xml` (43 lines, 10 classes, e.g.
  `<Class id="StStyle"><property name="Type" type="StyleType"/>...`,
  `IntPropTypeOverrides.xml:5-9`), read by `LcmGenerate.cs:139-167` and applied via
  `Property.OverridenType` (`Property.cs:218-236`). The enum's actual members are defined nowhere
  the model or generator can see — they live in hand-written C# elsewhere in the codebase.
- **No custom-field support.** FieldWorks/LCM custom fields are a pure runtime concept
  (`IFwMetaDataCacheManaged.AddCustomField`, `src/SIL.LCModel/Infrastructure/IFwMetaDataCacheManaged.cs:86,134`;
  `FieldDescription.cs:26,77,336-428`) — added to a live project's metadata cache at runtime, with
  no representation in `MasterLCModel.xml` and no interaction with the code generator at all.
- **A second, effectively dead schema file**: `src/SIL.LCModel/LcmGenerate/NonModelPropertiesAndClasses.xml`
  (own 120-line pseudo-model, references a different, non-existent `DomainModel.xsd` in the same
  folder) declares "non-model" properties like `WfiWordform.ParserCount` and a `VirtSegment`
  class. `grep -rn "NonModelPropertiesAndClasses"` across all `.cs`/`.proj`/`.csproj` files found
  **zero references** — this file is not wired into `GenerateModel.proj`, `LcmGenerate.cs`, or any
  template. It is vestigial. Likewise `src/SIL.LCModel/LcmGenerate/ModuleLocations.xml` (references
  a `ScrFDO` assembly split that predates this repo) has zero `.cs` references anywhere in the tree.
  `src/SIL.LCModel/LcmGenerate/DomainModel.xsd`/`.xsx` are unreferenced Visual-Studio
  XSD-designer artifacts (the `.xsx` file's own comment says so:
  `LcmGenerate/DomainModel.xsx:2`, `"auto-generated by the XML Schema Designer"`).

### Representative class, quoted verbatim (`MasterLCModel.xml:259-305`)

```xml
	<class num="6" id="Segment" abstract="false" abbr="seg" base="CmObject" depth="0">
	  <comment>
		<para>Represents a sentence or segment length part of a paragraph.</para>
	  </comment>
	  <props>
		<basic num="1" id="BeginOffset" sig="Integer" internalSetter="true"/>
		<basic num="2" id="FreeTranslation" sig="MultiString"/>
		<basic num="3" id="LiteralTranslation" sig="MultiString"/>
		<owning num="4" id="Notes" sig="Note" card="seq"/>
		<rel num="5" id="Analyses" sig="IAnalysis" card="seq"/>
		<basic num="6" id="Reference" sig="String">
		  <comment>
			<para>This string can be used to store a user specified reference for a segment.</para>
			<para>This is displayed in the Ref (short for reference) column in the Concordance view.</para>
			<para>As of Aug 2011 this can not be specified in FLEx, but can be imported. -NaylorJ</para>
		  </comment>
		</basic>
		<rel num="7" id="MediaURI" sig="CmMediaURI" card="atomic">
		  <comment>
			<para>This references a media file in the interlinear text.</para>
		  </comment>
		</rel>
		<basic num="8" id="BeginTimeOffset" sig="Unicode">
		  <comment>
			<para>This string can be used to store the time offset into the mediaFile for the beginning of this segment.</para>
			<para>Currently intended to hold ELAN information, not modified by FLEx as of Nov 2011 -NaylorJ.</para>
		  </comment>
		  <notes>
<para>Type is string so ELAN could optionally store their timeslot concept: http://flexelan.blogspot.com/2011/11/proposed-schema.html#comment-form</para>
</notes>
		</basic>
		<basic num="9" id="EndTimeOffset" sig="Unicode">...</basic>
		<rel num="10" id="Speaker" card="atomic" sig="CmPerson">
		  <comment>
			<para>The person who spoke this sentence, ELAN info.</para>
		  </comment>
		</rel>
	  </props>
	</class>
```

This exercises: basic scalar (`Integer`, `Unicode`, `String`), multi-lingual string
(`MultiString`), owning sequence (`owning`/`seq`), atomic reference (`rel`/`atomic`), sequence
reference (`rel`/`seq`), an `internalSetter` override, and both `comment`/`notes` documentation.

Other attested class shapes: `abstract="true"` with no concrete instances allowed
(`MasterLCModel.xml:240` `CmMajorObject`), `singleton="true"` (`MasterLCModel.xml:1661` `Scripture`,
`2295` `RnResearchNbk`, `2715` `LexDb`, `3509` `MoMorphData`), `owner="none"`
(`MasterLCModel.xml:1894` `ScrRefSystem`), `generateBasicCreateMethod="false"`
(`MasterLCModel.xml:731` `CmTranslation`, `1661` `Scripture`), and `card="col"` owning collections
(`MasterLCModel.xml:222,224,246` on `CmFolder`/`CmMajorObject`).

## 3. Generator and templating system

Confirmed by direct import statement: `LcmGenerateImpl.cs:9-11` —
```csharp
using NVelocity;
using NVelocity.App;
using NVelocity.Runtime;
```
The generator is **Apache Velocity ported to .NET (NVelocity)**, version pinned in
`src/SIL.LCModel.Build.Tasks/SIL.LCModel.Build.Tasks.csproj:16`:
`<PackageReference Include="NVelocity" Version="1.2.0" PrivateAssets="All" />`.

The `.vm.cs` files under `src/SIL.LCModel/LcmGenerate/` **are the templates**, not generated
output (despite the extension suggesting compiled C#; the csproj explicitly excludes them from
compilation: `SIL.LCModel.csproj:11-17`, `<Compile Remove="LcmGenerate\*.vm.cs" /> <None
Include="LcmGenerate\*.vm.cs" />`). 33 `.vm.cs` template files exist, ranging from 376 bytes
(`HandGenerated.xml` — not a template, an override list) to 16,912 bytes
(`propaccessors_simple.vm.cs`). Template entry point is `main.vm.cs`, which iterates
`$lcmgenerate.Modules` (`main.vm.cs:27-29`) and drives 8 further top-level generation passes
(constants, interfaces, factories, repositories, backend provider, DI bootstrapper —
`main.vm.cs:32-56`), each via `$lcmgenerate.SetOutput(...)` / `$lcmgenerate.Process(...)`.

Template chain per class: `module.vm.cs` (loop over `$module.Classes`) → `class.vm.cs` (per-class
shell: namespace, XML-doc header, `[ModelClass(...)]` attribute, base-class/interface list) →
`datamembers.vm.cs` (dispatches per property on `$prop.Cardinality` to `datamembers_simple.vm.cs`
/ `datamembers_atomic.vm.cs` / `datamembers_rel.vm.cs`) → `Constructors.vm.cs` →
`propertyAccessors.vm.cs` (not read directly by me but referenced at `class.vm.cs:51`, dispatches
similarly to `propaccessors_simple.vm.cs` / `propaccessors_atomic.vm.cs` /
`propaccessors_rel.vm.cs`) → `OtherMethods.vm.cs`, which in turn `#parse`s
`RemoveAReferenceCore.vm.cs`, `RemoveOwneeMethod.vm.cs`, `DeleteMethod.vm.cs`,
`AllReferencedObjects.vm.cs`, `ClearIncomingRefsOnOutgoingRefs.vm.cs`,
`RestoreIncomingRefsOnOutgoingRefs.vm.cs`.

The **generator project/tool** is the MSBuild task assembly `SIL.LCModel.Build.Tasks`
(`src/SIL.LCModel.Build.Tasks/SIL.LCModel.Build.Tasks.csproj`), targeting `net462;netstandard2.0`
and packed as a NuGet tool (`IsPackable=true`, output under `tools/$(TargetFramework)`,
`SIL.LCModel.Build.Tasks.csproj:5-10`). Its C# surface (all in
`src/SIL.LCModel.Build.Tasks/`, none in `obj/`):

| File | Lines | Role |
|---|---|---|
| `LcmGenerate.cs` | 224 | The public MSBuild `Task` (`XmlFile`, `OutputDir`, `OutputFile`, `TemplateFile`, `BackendTemplateFiles`, `WorkingDirectory`, `HandGeneratedDir` parameters) |
| `LcmGenerateImpl.cs` | 226 | Wraps the NVelocity engine/context, exposes `$model`/`$lcmgenerate` to templates |
| `Model.cs` | 69 | Parses `EntireModel` → `Modules` |
| `CellarModule.cs` | 64 | Parses one `CellarModule` → `Classes` |
| `Class.cs` | 729 | Parses one `class`, exposes all derived property-bucket collections (§2) |
| `Property.cs` | 290 | Parses `basic` |
| `RelationalProperty.cs` | 152 | Parses `owning`/`rel` |
| `TypeInfo.cs` | 102 | Fixed table mapping `sig` string → C# scalar type (§6) |
| `IClass.cs` | 260 | Interface abstraction over `Class` |
| `DummyClass.cs` | 405 | Null-object fallback (`GetClass` returns this when a `sig` target class isn't found, `LcmGenerateImpl.cs:188-202`) |
| `IdlImp.cs` | 180 | Unrelated: a separate MSBuild task that imports COM IDL files (kernel interop), not the model |
| `StringKeyCollection.cs`, `Base.cs` | 23, 89 | Utility base classes |

Note: `src/CSTools/{lg,pg,Tools}` (lexer/parser generator sources, `lg.cs`, `pg.cs`,
`cs0.lexer.cs`, etc.) is an unrelated legacy code-generation toolchain — no reference to
`MasterLCModel.xml`, `LcmGenerate`, or `EntireModel` found in that tree; it is not part of the
model-driven pipeline.

## 4. Generated vs hand-written files

**Generated (all confirmed gitignored — see `.gitignore` excerpt and `git status
--porcelain --ignored=matching` output below):**

```
src/SIL.LCModel/Infrastructure/Impl/Generated*.cs
src/SIL.LCModel/Generated*.cs
src/SIL.LCModel/DomainImpl/Generated*.cs
src/SIL.LCModel/IOC/Generated*.cs
```

Actual files present on disk (built locally) and their line counts:

| File | Lines |
|---|---|
| `src/SIL.LCModel/DomainImpl/GeneratedClasses.cs` | 113,638 |
| `src/SIL.LCModel/GeneratedInterfaces.cs` | 14,549 |
| `src/SIL.LCModel/DomainImpl/GeneratedFactoryImplementations.cs` | 11,232 |
| `src/SIL.LCModel/GeneratedConstants.cs` | 4,703 |
| `src/SIL.LCModel/Infrastructure/Impl/GeneratedRepositoryImplementations.cs` | 4,567 |
| `src/SIL.LCModel/GeneratedRepositoryInterfaces.cs` | 2,159 |
| `src/SIL.LCModel/GeneratedFactoryInterfaces.cs` | 2,038 |
| `src/SIL.LCModel/IOC/GeneratedServiceLocatorBootstrapper.cs` | 1,473 |
| `src/SIL.LCModel/Infrastructure/Impl/GeneratedBackendProvider.cs` | 18 |
| **Total** | **154,377** |

All nine are marked `!!` (ignored, present) by `git status --porcelain --ignored=matching`, and
none appear in `git ls-files`. They are **not committed**; they exist on disk only because a
local build has already run (confirmed present under `artifacts/{Debug,Release}/{net462,
netstandard2.0,net8.0}/` as build-output copies too).

**Hand-written, for comparison**: 283 non-generated `.cs` files directly under `src/SIL.LCModel/`
(292 total minus the 9 generated ones, excluding `obj/`), totaling 148,947 lines. So the
generated code (~154K lines / 9 files) is comparable in volume to — slightly larger than — the
entire hand-written `SIL.LCModel` project's non-generated code (~149K lines / 283 files).

**One deceptive naming exception**: `tests/SIL.LCModel.Tests/DomainImpl/GeneratedPropertyAccessorTests.cs`
(556 lines) is named "Generated" but **is** committed (`git ls-files` confirms it,
`git log --oneline -1` shows it dates to the `Rename FDO to LCM` commit) and **is** hand-written —
its own header says "Responsibility: Randy regnier" and its docstring explains it hand-tests "each
flid type, since the generator works the same for each flid type"
(`GeneratedPropertyAccessorTests.cs:1-13`). No `.vm.cs` template targets it. The filename is a
false positive for "generated."

## 5. Build wiring

The full chain, read directly from `src/SIL.LCModel/SIL.LCModel.csproj:98-119`:

```xml
<ItemGroup>
  <GeneratedFiles Include="GeneratedConstants.cs" />
  <GeneratedFiles Include="GeneratedInterfaces.cs" />
  <GeneratedFiles Include="GeneratedFactoryInterfaces.cs" />
  <GeneratedFiles Include="GeneratedRepositoryInterfaces.cs" />
  <GeneratedFiles Include="DomainImpl\GeneratedClasses.cs" />
  <GeneratedFiles Include="DomainImpl\GeneratedFactoryImplementations.cs" />
  <GeneratedFiles Include="Infrastructure\Impl\GeneratedRepositoryImplementations.cs" />
  <GeneratedFiles Include="Infrastructure\Impl\GeneratedBackendProvider.cs" />
  <GeneratedFiles Include="IOC\GeneratedServiceLocatorBootstrapper.cs" />
  <Clean Include="@(GeneratedFiles)" />
</ItemGroup>

<Target Name="GenerateModel" Inputs="MasterLCModel.xml" Outputs="@(GeneratedFiles)" BeforeTargets="BeforeCompile">
  <Exec Command="$(MsbuildCommand) GenerateModel.proj /p:Configuration=$(Configuration) /p:OutDir=$(OutDir)" />
  <ItemGroup>
      <Compile Remove="@(GeneratedFiles)" />
      <Compile Include="@(GeneratedFiles)" />
  </ItemGroup>
</Target>
```

`GenerateModel` is an MSBuild `Inputs`/`Outputs`-gated target (incremental: only reruns if
`MasterLCModel.xml` is newer than the outputs) hooked `BeforeTargets="BeforeCompile"`, i.e. it
runs automatically on every ordinary build — confirmed CI does nothing special, just
`dotnet build --configuration Release` (`.github/workflows/ci-cd.yml:70-71`).

It shells out to a **separate** MSBuild invocation of `src/SIL.LCModel/GenerateModel.proj`
(comment explains why: "so it doesn't lock the SIL.LCModel.Build.Tasks.dll in VS",
`SIL.LCModel.csproj:112-113`). That project is minimal, 8 lines:

```xml
<Project ... DefaultTargets="GenerateModel">
	<UsingTask TaskName="LcmGenerate" AssemblyFile="$(OutDir)..\netstandard2.0\SIL.LCModel.Build.Tasks.dll" />
	<Target Name="GenerateModel">
		<LcmGenerate XmlFile="MasterLCModel.xml" OutputDir="." OutputFile="DomainImpl/GeneratedClasses.cs" TemplateFile="LcmGenerate/main.vm.cs" />
	</Target>
</Project>
```
(`GenerateModel.proj:1-8`, verbatim)

**Standalone runnability**: yes, by evidence, not by having actually executed it in this session.
`LcmGenerate` is an ordinary `Microsoft.Build.Utilities.Task` (`LcmGenerate.cs:19`) with four
`[Required]` string parameters (`XmlFile`, `OutputDir`, `OutputFile`, `TemplateFile`) and no
dependency, in its own code, on any `SIL.LCModel.dll` runtime assembly — only on
`Microsoft.Build.Framework`/`Utilities` and NVelocity (`LcmGenerate.cs:5-10`,
`LcmGenerateImpl.cs:5-11`). `GenerateModel.proj` itself is a template for how to invoke it
directly with `msbuild GenerateModel.proj /p:Configuration=... /p:OutDir=...`, or the task could be
`UsingTask`-declared and invoked from any other `.proj`/`.csproj` pointing `XmlFile` at a different
XML document, provided that document parses to the same `EntireModel`/`CellarModule`/`class`
shape the `Model`/`Class`/`Property` C# classes expect (they do no schema validation themselves —
they just navigate `XmlElement`/attributes by name and throw `NullReferenceException` on
unexpected shapes).

## 6. What generated code actually contains

### Constants (`GeneratedConstants.cs`, template `Constants.vm.cs`)

Generated per-class `...Tags` static classes with flid constants
(`GeneratedConstants.cs:146-151`, for `Segment`):
```csharp
/// <summary>BeginOffset</summary>
public const int kflidBeginOffset = 6001;
...
/// <summary>FreeTranslation</summary>
public const int kflidFreeTranslation = 6002;
...
/// <summary>LiteralTranslation</summary>
public const int kflidLiteralTranslation = 6003;
```
Flid numbering scheme, from the template (`Constants.vm.cs:51-54`) and `Property.Number`
(`Property.cs:84-90`): `flid = (module.Number*1000 + class.num)*1000 + prop.num`. Base `CmObject`
also gets fixed system flids `kflidHvo=100`, `kflidGuid=101`, `kflidClass=102`,
`kflidOwnFlid=104`, `kflidOwnOrd=105` (`Constants.vm.cs:39-50`).

### Interfaces (`GeneratedInterfaces.cs`, template `Interfaces.vm.cs`)

`ISegment : ICmObject` (`GeneratedInterfaces.cs:303-311` onward), scalar properties as
get-only C# properties even when the underlying field is settable at the impl level (a comment
explains multi-string/vector properties intentionally expose no setter — "One 'gets' the accessor
and uses that to work with the property", `GeneratedInterfaces.cs:330-333`), vector properties
typed as `ILcmOwningSequence<INote> NotesOS { get; }` and
`ILcmReferenceSequence<IAnalysis> AnalysesRS { get; }` (`GeneratedInterfaces.cs:352-369`) — note
the Niuginian suffix convention (`O`/`R` for owning/reference + `A`/`S`/`C` for
atomic/sequence/collection) baked directly into the generated member name
(`RelationalProperty.NiuginianPropName`, `RelationalProperty.cs:86-110`).

### Implementation classes (`GeneratedClasses.cs`, templates `class.vm.cs`+`datamembers*.vm.cs`+`propaccessors*.vm.cs`)

For `Segment` (`GeneratedClasses.cs:2001-2400`, excerpted):

```csharp
[ModelClass(6, "ISegment")]
internal partial class Segment : CmObject,  ISegment
{
	#region Data Members
	private int m_BeginOffset;
	private IMultiString m_FreeTranslation;
	private IMultiString m_LiteralTranslation;
	private ILcmOwningSequence<INote> m_NotesOS = null;
	private ILcmReferenceSequence<IAnalysis> m_AnalysesRS = null;
	private ITsString m_Reference;
	private object m_MediaURIRA;     // atomic ref: stored as boxed object until resolved
	private string m_BeginTimeOffset;
	private string m_EndTimeOffset;
	private object m_SpeakerRA;
	#endregion Data Members
	...
	[ModelProperty(CellarPropertyType.Integer, 6001, "int")]
	public int BeginOffset
	{
		get { return m_BeginOffset;}
		internal set { ... ValidateBeginOffset(ref newValue); ... }
	}
```
(`GeneratedClasses.cs:2009-2160`, verbatim except elision noted)

Backing store for vectors is lazily constructed on first access
(`propaccessors_rel.vm.cs:60-77`, e.g. `m_NotesOS = new LcmOwningSequence<INote>(unitOfWork,
repository, this, flid)`), i.e. every owning/reference vector field is one of
`LcmOwningVector`/`LcmOwningCollection<T>`/`LcmOwningSequence<T>`/
`LcmReferenceCollection<T>`/`LcmReferenceSequence<T>`, chosen by
`RelationalProperty.CSharpType` (`RelationalProperty.cs:118-131`).

**Reference-removal logic** — templated in `RemoveAReferenceCore.vm.cs`, materialized for
`Segment` (`GeneratedClasses.cs:1034-1065`):
```csharp
internal override void RemoveAReferenceCore(ICmObject target)
{
	if (m_MediaURIRA == target) { MediaURIRA = null; return; }
	if (m_SpeakerRA == target) { SpeakerRA = null; return; }
	base.RemoveAReferenceCore(target);
}
internal override void ReplaceAReferenceCore(ICmObject target, ICmObject replacement)
{
	if (m_MediaURIRA == target) { MediaURIRA = (CmMediaURI)replacement; return; }
	if (m_SpeakerRA == target) { SpeakerRA = (CmPerson)replacement; return; }
	base.ReplaceAReferenceCore(target, replacement);
}
```
Owning-side removal (`RemoveOwneeMethod.vm.cs:13-56`) is a `switch (owningFlid)` dispatch, one
`case` per atomic/collection/sequence owning property, that nulls the backing field, fires
`RemoveObjectSideEffects`, and records the change with the unit-of-work service (guid-array
before/after for vectors, single guid for atomics).

Cascade delete (`DeleteMethod.vm.cs:21-46`) walks `$class.AtomicProperties` (null out via the
setter, suppressing notification/validation) then `$class.VectorProperties`
(`((ILcmClearForDelete)vector).Clear(true)`), then calls `base.DeleteObjectBasics()`.

### Factories / repositories

`FactoryImplementations.vm.cs` → generated `SegmentFactory : ISegmentFactory, ILcmFactoryInternal`
with `Create()`/`Create(Guid)`/`CreateInternal()`, singleton-guard branches gated by
`$class.IsSingleton` (`factory.vm.cs:47-50,63-66`). `RepositoryImplementations.vm.cs` generates
per-class repositories implementing lookup/enumeration over `LcmCache`.

## 7. Standalone consumability of the model source

**As plain XML**: yes, structurally. `MasterLCModel.xml` is well-formed XML validated against a
plain, dependency-free XSD (`MasterLCModel.xsd`, no imports, no external namespace). Nothing in
the XSD or the file itself requires a .NET/LibLCM runtime to parse; any XML/XSD-aware tool in any
language can load and validate it. What an external tool would be reimplementing, not reusing, is
the derived semantics (cardinality buckets, flid numbering, Niuginian naming, enum-type overrides)
currently computed only by the C# classes in `SIL.LCModel.Build.Tasks` (§2/§3) — none of that
derivation is expressed as a second, machine-readable artifact; it exists only as C# LINQ
expressions over the XML.

**Shipped in the NuGet package**: yes, confirmed from the packaging target itself
(`SIL.LCModel.csproj:121-132`):
```xml
<Target Name="CollectRuntimeOutputs" BeforeTargets="_GetPackageFiles">
  <ItemGroup>
      <None Include="MasterLCModel.*" Pack="true" PackagePath="contentFiles/" />
      <None Include="Styles.dtd" Pack="true" PackagePath="contentFiles/" />
      <None Include="LcmGenerate\HandGenerated.xml" Pack="true" PackagePath="contentFiles/LcmGenerate/" />
      <None Include="LcmGenerate\IntPropTypeOverrides.xml" Pack="true" PackagePath="contentFiles/LcmGenerate/" />
      <None Include="Templates\*.*" Pack="true" PackagePath="contentFiles/Templates/" />
      ...
  </ItemGroup>
</Target>
```
`MasterLCModel.xml`/`.xsd`, the hand-generated override lists, and the template files are all
packed as NuGet `contentFiles`. A companion `src/SIL.LCModel/SIL.LCModel.props` (auto-imported by
consumers of the package) defines MSBuild items pointing at these content files
(`SilLCModelFile`, `SilLCModelXsdFile`, `SilLCModelTemplates`, etc.,
`SIL.LCModel.props:6-12`), evidently so a downstream project can re-run generation against the
packaged model without checking out this source repo. I did not build a `.nupkg` in this session
(none exists under `artifacts/`) or fetch the published package from nuget.org to confirm final
contents; this claim rests on the `.csproj`/`.props` source, not on an observed package artifact.
`SIL.LCModel.Build.Tasks` (the generator itself) is packed separately (own `IsPackable=true`,
`tools/$(TargetFramework)/`, `SIL.LCModel.Build.Tasks.csproj:9`), bundling `NVelocity.dll` as a
tool dependency (`SIL.LCModel.Build.Tasks.csproj:43`).

## 8. Existing non-codegen consumers of the model source

**None found.** Grepping for `MasterLCModel` across every `.cs`/`.proj`/`.csproj`/`.props`/
`.targets` file in the repo (excluding `obj`/`artifacts`) returns exactly three hits, all inside
the generation pipeline itself: `GenerateModel.proj`, `SIL.LCModel.csproj`, `SIL.LCModel.props`
(the packaging/consumption plumbing described in §7). `SIL.LCModel.FixData` (the project one would
guess might validate/repair data against the model) never references `MasterLCModel`,
`CellarModule`, or `EntireModel` — its fixers (`BasicCustomPropertyFixer.cs`,
`DuplicateStyleFixer.cs`, etc.) operate on project data files, using hand-written knowledge of
specific classes, not a schema walk. `IdlImp.cs` is a same-directory, same-assembly but functionally
unrelated tool (COM IDL → C# interop stubs, not the LCM model).

**Closest analogue, and instructive precedent**: `src/SIL.LCModel/DomainServices/GrammarJsonServices.cs`
(1,576 lines, added in a single recent commit `d564a719`, documented in `doc/lcm-grammar.md` +
`doc/lcm-grammar.schema.json`). This exports a deterministic JSON projection
("LCM Grammar JSON") of the phonology/morphology/lexicon subset of a project, explicitly intended
to let external parser tooling consume LCM data "without LibLCM" (`doc/lcm-grammar.md:1-17`). It
is directly relevant to the CRDT-retargeting question in spirit — but it is **not** a model-source
reader: it is `public static class GrammarJsonServices { public static string ExportGrammar(LcmCache
cache, ...) }` (`GrammarJsonServices.cs:40,51-67`), a hand-written walk of **live, resolved domain
object instances** (`ILexEntry`, `IPhPhoneme`, etc.), not of `MasterLCModel.xml`. It does not use
NVelocity, does not run at build time, and required 1,576 lines of bespoke per-field mapping code
that duplicates domain knowledge already implicit in the model rather than deriving from it. That
this recent, closely-analogous effort was built by hand rather than by retargeting the existing
generator is itself evidence about how load-bearing the current generator's model-to-code path is
for anything other than emitting `SIL.LCModel.DomainImpl` classes.

## 9. Assessment: retargeting for a different output

Evidence-grounded obstacles actually observed (not hypothetical):

1. **The metadata layer (`Class`/`Property`/`RelationalProperty`) is reusable in principle** — it
   is a clean, ~1,700-line, dependency-light (`System.Xml` + LINQ only) façade over the raw XML
   that already answers exactly the questions a different generator would need (owning vs
   reference, atomic/collection/sequence, base class, abstract, singleton, scalar type). This part
   could plausibly be lifted with modest changes. But it is compiled as `internal` classes inside
   `SIL.LCModel.Build.Tasks` and is not published as its own package or public API — an external
   generator would need to either fork this assembly's source or convince upstream to expose it.

2. **The type system is FieldWorks/Cellar-specific and not a clean scalar set.** `TypeInfo.cs:69-90`
   hard-codes eleven scalar signatures, several of which resolve to FieldWorks-only types with no
   obvious EF Core/CRDT/JSON equivalent without a translation layer: `String` → `TsStringAccessor`
   (rich text with per-run formatting, backed by `ITsString`), `MultiString`/`MultiUnicode` →
   per-writing-system accessor classes (not a scalar at all — a keyed collection), `GenDate` →
   FieldWorks' partial/uncertain-date type, `Image` is present but literally unimplemented
   (`{"Image", new TypeInfo("???", "???", ...)}`, `TypeInfo.cs:80`). Any retargeted generator has
   to either reimplement these semantics or explicitly punt on them.

3. **Enum-typed fields are unrecoverable from the model alone.** As shown in §2, `sig="Integer"`
   plus a side-file (`IntPropTypeOverrides.xml`) gives you an enum **type name**
   (e.g. `StyleType`) but not its members — those live in ordinary hand-written C# elsewhere in
   `SIL.LCModel`. A different-target generator emitting, say, a Postgres CHECK constraint or a
   JSON-schema enum would need a second data source (the actual enum definitions) that isn't part
   of this model-generation pipeline at all.

4. **Templates contain hard-coded, class/field-name-keyed special cases**, not purely
   data-driven logic — meaning "the model + generic template" story has real leaks and any
   retargeted generator inherits (or must independently rediscover) these exceptions:
   - `propaccessors_rel.vm.cs:47-53,89-99`: `Segment.Analyses` is special-cased to emit
     `ILcmReferenceSequence<IAnalysis>` / `IAnalysisRepository` instead of the generic
     `$propTypeClass`-derived type, because `sig="IAnalysis"` (`MasterLCModel.xml:268`) names an
     interface, not a model class, and the generic path can't resolve it.
   - `factory.vm.cs:14-18`: `LgWritingSystem`'s factory is suffixed `FactoryLcm` instead of
     `Factory`, an arbitrary naming carve-out.
   - `Constructors.vm.cs:26-30` and `DeleteMethod.vm.cs:23-25`: `LgWritingSystem` (and, for the
     constructor, `LangProject`) get class-name-gated special construction/deletion behavior
     (`LgWritingSystem` deletion is unconditionally blocked: `throw new
     InvalidOperationException("Don't even think of nuking a WS.")`).
   
   These are evidence that at least a handful of the 193 classes cannot be regenerated correctly
   by a naive "read the XML, apply the generic template" reimplementation — the current templates
   encode institutional knowledge the model itself doesn't carry.

5. **No prior art in this repo for redirecting this exact pipeline to a second output.** §8 found
   zero other consumers of `MasterLCModel.xml`. The one closely analogous effort
   (`GrammarJsonServices`) to produce a non-LCM serialization from LCM data was built as 1,576
   lines of hand-written instance-walking code, not as a new NVelocity template set against the
   model. That is a real, observed data point against assuming the template layer generalizes
   cheaply — the team that would most plausibly have reused it, apparently didn't, for a
   similarly-shaped problem (projecting model-described data to an external format).

6. **The generator is architecturally separable from the LCM runtime** (§5) — `LcmGenerate.cs`/
   `LcmGenerateImpl.cs` depend only on `Microsoft.Build.Framework/Utilities` and NVelocity, not on
   `SIL.LCModel.dll`. This is the strongest point in favor of retargetability: nothing structurally
   prevents pointing the same `LcmGenerate` MSBuild task at a different `TemplateFile` set to
   produce different output (e.g. EF Core `IEntityTypeConfiguration<T>` classes or C# records)
   from the same `MasterLCModel.xml`, provided the new templates only rely on the metadata already
   exposed by `Class`/`Property`/`RelationalProperty`, and provided the ~5 class/field-specific
   template exceptions (point 4) are either re-encoded in the new templates or the corresponding
   fields are special-cased out of scope.

Net: the model source itself (XML + XSD) is small, parseable standalone, and already shipped as
NuGet content — a reasonable starting point. The generator's *engine* (NVelocity via a thin
MSBuild task) is reusable machinery independent of LCM's runtime assemblies. What would actually
need to be built, not reused, is: (a) a public-facing version of the `Class`/`Property` metadata
façade (currently `internal` and buried in a build-tasks assembly), (b) a resolution path for the
handful of enum-typed and otherwise special-cased fields that the model alone can't describe, and
(c) an entirely new template set, written from scratch, since none of the 33 existing `.vm.cs`
templates target anything but `SIL.LCModel.DomainImpl`/interfaces/factories/repositories — there is
no partial or experimental EF-Core/CRDT/JSON template anywhere in this repo to extend.
