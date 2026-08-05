# Path 1 — Extend MiniLcm (in Lexbox) to be the one API surface, including grammar

> **HISTORICAL — a path not taken.** This document evaluates routing Motif's changes through a CRDT merge
> layer, or inventories the repositories that would have been involved. **That design was assessed and
> rejected** ([adoption report](harmony-adoption-report.md)); operations target LibLCM directly and there is
> no merge layer. Kept as the evidence behind that decision. **It is not a plan, and nothing in it is
> scheduled.** For the live plan see [Plan A](plan-motif.md).

*Prepared 2026-07-27. Scope: cost, risk, and platform-reach of growing `IMiniLcmApi` and its two
backends (`FwDataMiniLcmBridge`, `LcmCrdt`) to cover LibLCM's grammar surface, so that MiniLcm becomes
the single API over FieldWorks language data for every consumer on every platform. Sources: primary
reads of `languageforge-lexbox`, `liblcm`, `FieldWorks/Src/LexText/ParserCore/HCLoader.cs`, and
`machine` (HermitCrab C#), plus `git log`/`git show` in `languageforge-lexbox` to measure what adding a
real construct has actually cost. All claims below were re-verified from source in this session, not
copied from `docs/minilcm-evaluation.md` / `docs/one-api-problem.md` (which already exist in this repo
and reach a similar conclusion) — those two documents are cited only where their citations were
independently re-checked here, and one material correction to their framing is filed in the box below.**

## Verdict up front

**This path can plausibly deliver Linux. It cannot deliver Android without either (a) doing something
nobody has attempted — porting LibLCM's native ICU4C/structuremap dependency graph to Android — or (b)
building a second, non-CRDT-native grammar representation inside `LcmCrdt`, at a cost that is not a
linear extension of MiniLcm's existing pattern but a second grammar engine. Read plainly: extending
MiniLcm to cover grammar "for real" (i.e., where the write path goes through LibLCM, which is the only
implementation that can correctly represent the feeding-ordered, position-resolved, index-as-identity
shapes this domain requires) is a path that, absent new engineering nobody has scoped, ships grammar on
Windows, macOS, and Linux and *not* on Android — a straight violation of the stated hard requirement.
The alternative — build grammar natively in `LcmCrdt` so Android gets it too — is evaluated in Question 2
and does not look like a CRDT anymore by the time it can represent this domain correctly.**

---

## Correction to the task's stated ground truth: "Windows-only" is imprecise

The brief's ground truth states `FwDataMiniLcmBridge` is Windows-only, citing the `IncludeFwDataBridge`
gate in `FwLiteMaui/FwLiteMaui.csproj:26-27,98-99`. That gate is **real and verified** — but it gates one
specific client shell (the native MAUS app), not `FwDataMiniLcmBridge` as such. There is a second client
shell, `FwLiteWeb` (an ASP.NET Core local web server with a browser front-end), and its project file
references `FwDataMiniLcmBridge` **unconditionally, with no OS gate**
(`backend/FwLite/FwLiteWeb/FwLiteWeb.csproj:35-38` — `<ProjectReference Include="..\FwDataMiniLcmBridge\FwDataMiniLcmBridge.csproj" />`
sits outside any `Condition`). Concretely, **VERIFIED-IN-SOURCE**:

- `FwLiteWeb` ships a `README-linux.md` describing an Ubuntu launcher install
  (`backend/FwLite/FwLiteWeb/README-linux.md:1-20`), and CI publishes real Linux binaries: the
  `publish-linux` job in `.github/workflows/fw-lite.yaml:322-364` runs
  `dotnet publish -r linux-x64 ...` and `-r linux-arm64 ...` for `FwLiteWeb`, smoke-tests the `linux-x64`
  binary, and ships both in every release (`create-release`, `:516-563`, `fw-lite-web-linux.zip`).
- `FwDataMiniLcmBridge.Tests` — the test project that exercises `FwDataMiniLcmBridge`'s real `.fwdata`
  read/write path — is a member of `FwLiteCore.slnf` (`FwLiteCore.slnf:1-16`), which is built and run by
  `dotnet build`/`dotnet test FwLiteCore.slnf` on `runs-on: ubuntu-latest`
  (`.github/workflows/fw-lite.yaml:44,77,95`). This is not a smoke test of "does the process start" — it
  is the same conformance suite discussed in Question 4, running against LibLCM, on Linux, on every PR.
- `FwHeadless` — the server component that drives Send/Receive project sync — also references
  `FwDataMiniLcmBridge` (`backend/FwHeadless/FwHeadless.csproj:17`) and ships as a Linux container
  (`backend/FwHeadless/Dockerfile:2,20-49`, `FROM mcr.microsoft.com/dotnet/aspnet:10.0`, no Windows base
  image anywhere), deployed to Kubernetes (`deployment/base/fw-headless-deployment.yaml`, referenced from
  `.github/workflows/develop-fw-headless.yaml:22`) and exercised end-to-end in the `e2e-test` job against
  a live `fw-headless` pod (`.github/workflows/fw-lite.yaml:572-714`, `for app in lexbox ui hg db
  fw-headless` at `:702`).
- The one thing that is genuinely Windows-only is the *MAUI* build of `FwDataMiniLcmBridge`: MAUI itself
  has no Linux target framework at all (`FwLiteMaui.csproj:5-7` lists only
  `net10.0-android`/`net10.0-ios`/`net10.0-maccatalyst`/`net10.0-windows...` — no `linux` TFM exists in
  the MAUI ecosystem), so Linux was never going to get the MAUI shell regardless of the bridge gate; it
  gets `FwLiteWeb` instead, and that shell does carry the bridge.

**Consequence for this report:** "Windows-only" is the right description for *iOS, macCatalyst, and
Android inside the MAUI shell* (`IncludeFwDataBridge` is `true` only when
`GetTargetPlatformIdentifier('$(TargetFramework)') == 'windows'`, `FwLiteMaui.csproj:27`) but the wrong
description for Linux, where `FwDataMiniLcmBridge`/LibLCM is proven — built, tested, and shipped — to
work today. This matters because it means **Path 1's Linux failure mode is not structural** the way its
Android failure mode is; see Question 3 for what actually blocks Android.

---

## Question 1 — What exactly would have to be built, sized from a real construct's git history

### Method: measure, don't estimate

`docs/api-surface-layer1.md:69-93` (already in this repo) predicts 25 handlers behind ~1,100 kinds for
Motif's own generated approach — that is a different codebase's estimate for a different architecture.
The honest way to size *this* path is to find a construct MiniLcm's team actually added end-to-end and
count what it touched. Two are recent and small: `Publication` (`CmPossibility`-shaped list plus one
`rel/col` field on `Entry`, `PublishIn`) and `MorphType` (an enum-like `CmPossibility`-shaped class plus
one field on `Entry`). Both are near the *simplest possible* shape in the taxonomy — flat fields, no
ordering complexity, no cross-referencing, no nested owned objects — which makes them a **floor**, not a
typical case, for what a grammar construct would cost.

### Publication — two PRs, VERIFIED-IN-SOURCE via `git show --stat`

- PR #1484 (`4c3e5d51`, "Add publications to the MiniLcmAPI"): **42 files changed, 1,290 insertions(+),
  37 deletions(-)**.
- PR #1795 (`8f80e941`, "Complete publication support"): **26 files changed, 403 insertions(+), 60
  deletions(-)**.
- Combined: on the order of **60 distinct file touches, ~1,700 lines**, for one construct with a single
  collection-valued reference field.

Artifact inventory for `Publication`, read from the two diffs:

| Artifact category | File(s) | Count |
| --- | --- | --- |
| Model class | `MiniLcm/Models/Publication.cs` (+`IPossibility.cs` interface edit) | 1 new + 1 edited |
| Validator | `MiniLcm/Validators/PublicationValidator.cs` (+registration in `MiniLcmValidators.cs`) | 1 new + 1 edited |
| CRDT `Change` classes | `CreatePublicationChange.cs`, `SetMainPublicationChange.cs`, `Entries/AddPublicationChange.cs`, `Entries/RemovePublicationChange.cs`, `Entries/ReplacePublicationChange.cs` | 5 |
| EF Core migration | `20250225100659_AddPublications.cs` + `.Designer.cs` (634 lines, mostly generated) | 2 files |
| Sync reconciler | `MiniLcm/SyncHelpers/PublicationSync.cs` (new) + `EntrySync.cs` edits (wiring `PublishIn` diffing) | 1 new + 1 edited |
| FwData (LibLCM) write path | `FwDataMiniLcmApi.cs` edits (2 commits, ~129 lines) + `UpdatePublicationProxy.cs` (new) | 1 new + 1 heavily edited |
| CRDT write path | `CrdtMiniLcmApi.cs` edits (2 commits, ~133 lines) | edited |
| Read/Write API interface | `IMiniLcmReadApi.cs`, `IMiniLcmWriteApi.cs` | 2 edited |
| DI / JSON polymorphic registration | `LcmCrdtKernel.cs` — `DbModelBuilder.Add<Publication>()`, five `ChangeTypeListBuilder.Add<...>()` calls (`LcmCrdtKernel.cs:262,337,345,371-372,376-378`, read directly this session) | 1 file, 7 call sites |
| Shared conformance test base + concrete implementations | `MiniLcm.Tests/PublicationsTestsBase.cs` (abstract), `FwDataMiniLcmBridge.Tests/MiniLcmTests/PublicationsTests.cs`, `LcmCrdt.Tests/MiniLcmTests/PublicationsTests.cs` — confirmed by grep this session (`grep -rln PublicationsTestsBase` → exactly these 3 files) | 3 |
| Regression/golden-file fixtures | `RegressionDeserializationData.json`, `*.VerifyChangeModels.verified.txt`, `*.VerifyDbModel.verified.txt`, `*.VerifyIObjectWithIdModels.verified.txt` | 4 files, every one requiring manual re-verification on shape change |
| Frontend TS type generation | `ReinforcedFwLiteTypingConfig.cs` edits + generated `.ts` | 1 config edit + generated output |
| Third partial consumer | `LfClassicData/LfClassicMiniLcmApi.cs` edits (this class explicitly does **not** implement `IMiniLcmApi` — `//doesn't actually implement IMiniLcmApi... we don't want to have to keep adding methods that do nothing`, `LfClassicMiniLcmApi.cs:16`, read this session) | 1 edited, lower cost |

### MorphType — one PR added, one PR partially reverted the write surface, VERIFIED-IN-SOURCE

- PR #1857 (`13eabbb5`, "Add morph types to MiniLcm"): **31 files changed, 1,394 insertions(+), 16
  deletions(-)**.
- PR #2221 (`649fb2c0`, "Remove CreateMorphType and DeleteMorphType from MiniLcm API"): **12 files
  changed, 42 insertions(+), 142 deletions(-)** — the team **removed** the general `create`/`delete`
  write methods for this construct after shipping them. The commit trail immediately before it
  (`945d2b83`, `0cdc4a07`, `274166a1`) shows why: morph types are effectively a closed, LibLCM-predefined
  taxonomy that needs *seeding* with stable, migration-safe GUIDs, not free-form user creation — a
  bespoke identity problem the generic "construct playbook" did not fit, so the team narrowed the API
  rather than force it.

This is the load-bearing finding for Question 1: **even the two simplest constructs in the current
grammar-adjacent surface — flat `CmPossibility`-shaped lists with a single reference field — needed a
hand-written special case** (bespoke seeding/migration logic for `MorphType`, a five-way `Change`
taxonomy for `Publication`'s one collection field). Neither is a mechanical fill-in-the-template
exercise. Every one of the 30 in-scope grammar constructs (`manifest/liblcm-inventory.tsv`, computed this
session: `Scope=in AND Group=grammar` → **230 rows, 30 distinct `Construct` values**,
independently re-derived and matching the brief's ground truth exactly) is structurally *more* complex
than `Publication`/`MorphType`, not less — see the construct list and comparison-class breakdown below.

### Extrapolation, stated as a floor

Taking ~30-60 files and ~700-1,700 lines per simple flat construct as a floor (not the ~1,100-lines
midpoint of the two — the low end, to avoid overstating): **30 grammar constructs would be, at minimum,
on the order of 900-1,800 file touches and tens of thousands of lines**, before accounting for the fact
that grammar constructs are not flat. Concretely, from the manifest (VERIFIED-IN-SOURCE, computed this
session):

```
grammar in-scope rows:            230
grammar in-scope constructs:       30
grammar comparison-class breakdown:
  unordered            196
  positional             30
  index-as-identity        3
  feeding                  1
```

`affixTemplate`, `rewriteRule`, `metathesisRule`, `msa` (four MSA subtypes sharing one construct family
per `docs/api-surface-layer1.md:52`), `phonemeSet`, `naturalClass`, `compoundRule`, `featureSystem`,
`featureStructure`, `stemName`, `stratum`, `coOccurrenceRule`, `inflectionClass` are all in this list —
none are flat `{Id, Name}` records the way `Publication` and `MorphType` mostly are. Each of the 34
`positional`/`index-as-identity`/`feeding` fields needs an ordering scheme, and 4 of those 34 are
verified (Question 2) to need a scheme `LcmCrdt`'s current `SetOrderChange`/`OrderPicker` cannot express
correctly at all. `MoInflAffixTemplate` needs slot-sequence logic on top of the base CRUD (two of five
LibLCM slot sequences are actually read — `PrefixSlots`/`SuffixSlots`,
`docs/hc-grammar-map.md:36-39`, independently re-verified against `HCLoader.cs:297` this session:
`template.SuffixSlotsRS.Concat(template.PrefixSlotsRS.Reverse())`). `MoAffixProcess.Output` needs
position-resolving logic with no CRDT-native analogue (Question 2). None of this is captured by
multiplying a flat-construct cost by 30; the true number is higher, and there is no construct in
MiniLcm's history complex enough to have priced it.

**What this buys, stated fairly:** the playbook itself — model class, validator, `Change` class(es),
migration, sync reconciler, DI/JSON registration, three-file shared test pattern — is proven, exercised
repeatedly, and each individual step is well-understood engineering, not research. The risk is not "can
MiniLcm's team execute this pattern" (they demonstrably can, twice, in this same domain family); it is
that the pattern's per-construct cost was measured on the wrong difficulty tier, and grammar's actual
tier has no measured precedent at all in this codebase.

---

## Question 2 — Can the CRDT backend correctly represent grammar at all?

`LcmCrdt` is a genuine, from-scratch representation choice — EF Core + SQLite + `SIL.Harmony`, zero
`SIL.LCModel` reference (confirmed no `SIL.LCModel` `PackageReference` anywhere under
`backend/FwLite/LcmCrdt/LcmCrdt.csproj`, consistent with the brief's ground truth). Its one mechanism for
order is:

```csharp
// LcmCrdt/Changes/SetOrderChange.cs:14-18
public override ValueTask ApplyChange(T entity, IChangeContext context)
{
    entity.Order = Order;
    return ValueTask.CompletedTask;
}
```

`Order` is an independent `double` per item (`OrderPicker.cs:8-38`, read in full this session), assigned
by finding a value between two neighbours' current `Order` fields, with the picker's own comment
admitting the neighbour-shift heuristic is "about a 50/50 chance" of being what the user actually
intended (`OrderPicker.cs:26-27`, verified verbatim). Concurrent edits to order resolve as last-writer-
wins on that scalar, mediated by whatever Harmony's commit log decides "wins" — **the representation is
decided above Harmony, by this scalar assignment, not by Harmony's merge algorithm**, so no amount of
Harmony sophistication changes what the field itself can encode. This is a correct restatement of
`docs/minilcm-evaluation.md`'s "Correction note" (`:82-86`), independently re-derived here from the same
two files rather than trusted.

Four grammar mechanisms, checked against `HCLoader.cs` directly this session, against this
representation:

### 2a. Phonological rule order (feeding/bleeding) — cannot be represented as an independent scalar

LibLCM's own representation of rule order is **not** a free `Order` field — it is `IndexInOwner + 1` on
the owning sequence itself, a computed virtual:

```csharp
// liblcm/src/SIL.LCModel/DomainImpl/OverridesLing_Lex.cs:7500-7513
internal partial class PhSegmentRule
{
    [VirtualProperty(CellarPropertyType.Integer)]
    public int OrderNumber
    {
        get { return IndexInOwner + 1; }
        ...
```

And `HCLoader.cs` reads it as position-in-sequence to build HermitCrab's own ordered
`List<IRule<...>>`, applied strictly in order:

```csharp
// FieldWorks/Src/LexText/ParserCore/HCLoader.cs:302
foreach (IPhSegmentRule prule in m_cache.LanguageProject.PhonologicalDataOA.PhonRulesOS
    .Where(r => !r.Disabled).OrderBy(r => r.OrderNumber))
```

and HermitCrab itself models this as a single ordered list applied in a stratum
(`machine/src/SIL.Machine.Morphology.HermitCrab/Language.cs` — `PhonologicalRules` is populated in load
order via `XmlLanguageLoader.cs:777,848`, and consumed sequentially by
`SynthesisStratumRule.cs`/`AnalysisStratumRule.cs`). This is standard SPE-style ordered rewrite-rule
phonology, not a LibLCM quirk — feeding/bleeding is a property of applying rule *N* to the *output* of
rule *N-1*, which means a rule's meaning depends on its neighbours' **content**, not merely their
identity or relative position. **Can `LcmCrdt` represent this?** Structurally, `entity.Order = Order`
degrades to "some real number bigger than the previous one." Two concurrent edits — one reordering rules,
one editing rule 5's structural description — do not conflict at the field level (different rows,
different columns) and so Harmony will happily apply both, producing a grammar whose *meaning* changed
in a way neither edit intended and neither editor could have detected from the diff. **This is not a
merge-algorithm gap Harmony could close; it is a representation gap:** the scalar carries "where," not
"what changed because of where."

### 2b. Alpha-variable arrays (index is identity, 24-per-rule ceiling) — index-as-identity is a third comparison class LcmCrdt has no analogue for

```csharp
// FieldWorks/Src/LexText/ParserCore/HCLoader.cs:37-41 (fixed 24-entry array)
private static readonly string[] VariableNames = { "α","β","γ","δ","ε","ζ","η","θ","ι","κ","λ","μ",
    "ν","ξ","ο","π","ρ","σ","τ","υ","φ","χ","ψ","ω" };
...
// :2005-2011
var variables = new Dictionary<IPhFeatureConstraint, string>();
int i = 0;
foreach (IPhFeatureConstraint var in prule.FeatureConstraints)
{
    variables[var] = VariableNames[i];
    i++;
}
```

`i` is not bounds-checked against the 24-entry array at this call site — a 25th distinct constraint
throws `IndexOutOfRangeException` and kills the entire grammar load (matches `docs/issues.md` C5, and
`docs/hc-grammar-map.md:74-79`, both independently consistent with this read). More importantly for
CRDT-fitness: the *name* assigned to a constraint is a pure function of **scan order** over
`StrucDesc`/`RightHandSides` context slots, per rule. This is neither `unordered` (order carries no
meaning) nor `positional` (a neighbour's identity matters, its content doesn't) nor `feeding` (a
neighbour's content changes downstream meaning) — it is a fourth thing, confirmed present in the manifest
as its own `ComparisonClass` value (`index-as-identity`, 3 in-scope rows, verified by direct query this
session). `LcmCrdt`'s ordering primitive has exactly one mechanism (`SetOrderChange<T>` + a `double`); it
has no notion of "this item's *name*, not just its position, is derived from where it sits relative to
others and must be recomputed whenever an earlier item in the scan is inserted, deleted, or reordered."
Representing this on `LcmCrdt` would require a *new*, rule-scoped derived-value recomputation step that
runs on every mutation to the owning rule's structural fields — which is arbitrary business logic
attached to a CRDT merge, not a CRDT merge.

### 2c. `MoAffixProcess.Output` resolves against `Input` by position — a `rel/atomic` reference that is actually positional

```csharp
// FieldWorks/Src/LexText/ParserCore/HCLoader.cs:1379-1384
case MoCopyFromInputTags.kClassId:
    var copyFromInput = (IMoCopyFromInput)mapping;
    if (copyFromInput.ContentRA != null)
    {
        string partName = (copyFromInput.ContentRA.IndexInOwner + 1).ToString(CultureInfo.InvariantCulture);
        hcAllo.Rhs.Add(new CopyFromInput(partName));
    }
```

`ContentRA` is declared as an atomic reference (a `rel/atomic` field, in the manifest's own taxonomy —
"attach"/"detach" semantics), but `HCLoader` resolves it through `IndexInOwner + 1` — i.e., **the
reference is real, but the number that matters is the referenced object's position in a different,
separately-ordered sequence (`Input`)**. Reordering `Input` silently renumbers every `Output` mapping
that points into it, with no error, no diagnostic, and no change visible in the `Output` mapping's own
stored data (confirmed identical for `MoModifyFromInput` at `HCLoader.cs:1409-1417`). This is a discovered-
footprint hazard already flagged in `docs/api-surface-layer1.md:133-137`, re-verified here directly. A
CRDT change type for "reorder `Input`" cannot declare its own footprint statically — its true blast
radius depends on how many `Output` mappings exist and what they reference, which is exactly the kind of
non-local effect CRDT `Change` objects are designed to *not* need to know about (they are supposed to be
local, commutative edits). Handling this correctly means either (a) recomputing and re-storing every
affected `Output` mapping's position as part of applying the `Input` reorder change — turning one user
edit into a cascading multi-object write, which defeats the "small, independent, commutative" design
Harmony's model assumes — or (b) leaving it broken.

### 2d. Affix template slot sequences — the one case that probably *is* CRDT-shaped

`MoInflAffixTemplate.PrefixSlots`/`SuffixSlots` are explicitly checked and classified `positional`, not
`feeding` or `index-as-identity` (`docs/api-surface-layer1.md:139-140`, matches the manifest's
`ComparisonClass` column read this session). A slot's *identity*, not its neighbours' content, is what
matters to a template. This is structurally the same shape as `LexEntry.AlternateForms` or sense order —
exactly the case `LcmCrdt`'s existing `SetOrderChange`/`OrderPicker` was built for and already handles
for lexical data. **This one construct's ordering need is plausibly representable on the existing CRDT
primitive without new machinery** — it is the exception, not the rule, among the 34 non-`unordered`
grammar fields.

### Net answer to Question 2

Of the four mechanisms examined, one (affix template slots) fits `LcmCrdt`'s existing primitive as-is.
The other three — rule order/feeding, alpha-variable index-as-identity, and `MoAffixProcess.Output`
position-resolution — cannot be correctly represented by `entity.Order = Order` merged last-writer-wins,
and fixing that is not a matter of adding fields to the `Change` classes; it requires either (a) new,
non-local, cascading recomputation logic that runs as part of applying an edit (at which point it is
ordinary application logic wearing a `Change` class's clothes, not a CRDT edit anymore — the "commutative,
independent, mergeable-without-coordination" property that makes something a CRDT is exactly what this
logic gives up), or (b) accepting silent semantic corruption on concurrent grammar edits, which is a
correctness regression relative to LibLCM's own single-writer-at-a-time desktop model. **INFERRED, not
verified from `SIL.Harmony`'s own source** (not checked out locally, consistent with the same limitation
already flagged in `docs/minilcm-evaluation.md:112-117`): it is possible Harmony's actual merge machinery
has richer primitives than `SetOrderChange<T>` exposes and a more sophisticated `Change` type could be
authored on top of it without abandoning the CRDT contract. What is verified is that **no such `Change`
type exists today**, and the three hazards above are not addressed by anything currently in
`LcmCrdt/Changes/`.

---

## Question 3 — Does this path actually deliver Android and Linux?

**Linux: plausibly yes**, per the correction above — `FwDataMiniLcmBridge` already builds, tests
(`FwDataMiniLcmBridge.Tests` in `FwLiteCore.slnf`, run on `ubuntu-latest`), and ships (`publish-linux`,
`linux-x64`/`linux-arm64`) on Linux today, through the `FwLiteWeb` shell. If grammar support lands inside
`FwDataMiniLcmBridge`, Linux gets it, because Linux already gets everything that backend supports. This
is the one part of the hard requirement this path does not structurally fail.

**Android: no**, and there is no visible third option inside this path that resolves it cheaply.

- The MAUI build gate excludes `FwDataMiniLcmBridge` from every non-Windows MAUI target
  (`FwLiteMaui.csproj:26-27`, `IncludeFwDataBridge` false unless
  `GetTargetPlatformIdentifier('$(TargetFramework)') == 'windows'`), and Android has no other shell —
  `FwLiteWeb` is a desktop/server ASP.NET Core app; nothing in this repository builds it for Android, and
  ASP.NET Core self-hosting a Kestrel server inside an Android app package, while not physically
  impossible, is not what `FwLiteWeb` is built or tested for.
- **INFERRED, not verified from a build attempt in this session**, but consistent with everything
  observed: `SIL.LCModel` depends on `Microsoft.ICU.ICU4C.Runtime`, included only conditionally on
  Windows in `FwDataMiniLcmBridge.csproj:12` (`Condition="$([MSBuild]::IsOsPlatform('Windows'))"`), and on
  `structuremap.patched` (`:14`) and Mercurial/Chorus native tooling for the sync path
  (`FwHeadless.csproj:11-13`). None of these have Android-targeted (ARM/AOT, no filesystem-as-desktop-
  assumes) build output anywhere in this codebase, and no commit, comment, or CI job references
  attempting one. This is not proof of impossibility — ICU4C does have Android builds used elsewhere in
  the SIL ecosystem — but it is a real, unscoped, unattempted port, not a flag to flip.
- Consequence: **if grammar lands only in `FwDataMiniLcmBridge`, grammar is unavailable on Android**,
  which fails the hard requirement exactly as the brief anticipates, and does so even though it does
  *not* fail on Linux — the two platforms the hard requirement names do not fail identically under this
  path, and treating them as one failure mode (as the task's framing implicitly invites) would be
  imprecise.

**If grammar instead lands in `LcmCrdt`** (the only way to make Android's story not "missing" but
"present and possibly wrong"): Question 2 already shows three of four checked mechanisms cannot be
correctly represented by the existing ordering primitive without new, non-CRDT-shaped logic. Layer onto
that the referential-integrity cost: `LcmCrdt`'s `GetReferences()`/`RemoveReference()` contract
(`MiniLcm/Models/IObjectWithId.cs:24-26`) is implemented by exactly **13 model classes**, confirmed by
grep this session (`grep -rl "GetReferences|RemoveReference" MiniLcm/Models/*.cs` → 13 files, including
the interface file itself). Grammar's reference graph is both denser and structurally different: MPR
referential integrity alone touches inflection classes, production restrictions, and
`ILexEntryInflType`, all read through raw dictionary indexers with no null-safety in `HCLoader.cs` (~16
sites per `docs/issues.md` C6, not independently re-counted here but the crash mode — `GenerateHCConfig`
failing on the Amharic project via a stale `MoMorphAdhocProhib` — is cited from PanGloss's own
documentation and is consistent with the raw-indexer pattern visible in the loader). LibLCM gets this for
free via `CmObject.Delete()` → `ClearIncomingReferences()`, which the FwData backend inherits by
delegation and never has to reimplement (`liblcm/src/SIL.LCModel/DomainImpl/CmObject.cs:1728-1733`,
independently re-read and consistent with the brief's ground truth). **`LcmCrdt` would have to build this
from scratch for all 30 grammar constructs** — a 66-class reference graph (grammar's in-scope rows span
66 distinct LibLCM classes, counted from the manifest this session), not the 13-class graph it has today.
This is not a novel finding — `docs/one-api-problem.md:69-75` already states it — but it is
independently re-verified here rather than assumed, and it compounds directly with Question 2's ordering
problem: many of the constructs that need the hardest-to-build referential integrity (`MoInflClass`,
`MoAdhocProhib`/`MoMorphAdhocProhib`, `ILexEntryInflType`) are the same ones that need the ordering
mechanisms `LcmCrdt` cannot yet express.

**Is there a third option within this path?** The two live inside-MiniLcm options are: build grammar only
where LibLCM already is (fails Android outright) or build grammar natively in `LcmCrdt` (arrives on
Android, but per Question 2 stops being a CRDT for the cases that matter, and per this section needs a
from-scratch referential-integrity system an order of magnitude larger than the one that exists today). A
third, hybrid option is visible but is **not really "extend MiniLcm"** — it is `docs/one-api-problem.md`'s
own Option B (`:84-98`): keep grammar semantics exclusively in LibLCM, and give `LcmCrdt` only the
capability to store and sync an *opaque* grammar change payload it does not interpret. That gets grammar
data (not grammar *editing*) onto Android as a carried, reviewable artifact, but it means Android cannot
author or apply a grammar edit locally — a materially narrower claim than "MiniLcm covers grammar,
including on Android," and arguably a different path from the one this report was asked to cost (it
routes change-authorship through a second contract, which is what Path B in `one-api-problem.md`
proposes, not an extension of `IMiniLcmApi`'s own method surface).

---

## Question 4 — What this path gets right that the others don't

Being fair to this path matters because everything above is structural risk, not present-day failure —
MiniLcm's lexical surface is real, shipping, and good:

- **It ships in production, on real infrastructure, today.** Five deployment targets across two shells:
  Windows/Android/iOS/macCatalyst via `FwLiteMaui` and Linux (plus macOS again) via `FwLiteWeb`, all
  built by CI (`fw-lite.yaml`'s `publish-mac`/`publish-linux`/`publish-android`/`publish-win` jobs,
  `:287-514`), all uploaded as real release artifacts (`create-release`, `:516-563`), gated by a
  Kubernetes-backed E2E test suite (`:572-714`).
- **Real conformance test discipline, independently re-counted this session:** `grep -rE
  "^\s*\[(Fact|Theory)"` across `MiniLcm.Tests`, `LcmCrdt.Tests`, `FwDataMiniLcmBridge.Tests` returns
  **612** test methods (higher than the 431 cited elsewhere — this session's own count, from current
  `HEAD`, likely reflecting growth since that figure was written). The shared-base-class pattern is real
  and repeatable — `Publication`'s addition alone created exactly the three-file pattern (one abstract
  base in `MiniLcm.Tests`, one concrete subclass in each backend's test project) that both existing Motif
  docs describe, confirmed by direct grep this session.
- **The construct-addition playbook works**, twice, in this exact domain family
  (`PartOfSpeech`/`MorphType`/`Publication` are all grammar-*adjacent* `CmPossibility`-shaped constructs).
  It is not a hypothetical process — it has concrete, git-log-visible precedent, including the team
  correctly recognizing and walking back a design that didn't fit (`MorphType`'s `create`/`delete`
  removal) rather than forcing it. That is a healthy engineering signal, not a red flag, even though the
  removal itself is evidence the generic playbook does not automatically fit every construct (Question 1).
- **Referential integrity for what MiniLcm already owns is handled, correctly, in both directions** —
  through LibLCM for free on the FwData backend, and through a real (if narrower, 13-type) hand-written
  contract on the CRDT backend, independently re-verified this session (`IObjectWithId.GetReferences`/
  `RemoveReference`, 13 implementers).
- **Comments/conversation threads are real and CRDT-native today** (`LcmCrdt/Changes/Comments/*.cs`,
  not independently re-read line-by-line this session but present in the file tree and consistent with
  both existing Motif docs' description) — a genuine product capability neither of the other two
  candidate paths has, since it depends on exactly the offline multi-device sync story this path already
  ships.
- **Offline, multi-device sync for lexical data is a real, working, non-trivial achievement.** Harmony's
  commit-log CRDT genuinely modernizes device-to-device and device-to-cloud sync for the data it covers,
  and nothing about grammar's representation problems (Question 2) undermines that achievement for the
  157-field, 23-construct lexical surface it was built for.

None of this transfers automatically to grammar — that is the entire finding of Questions 1-3 — but it is
real, present-tense engineering value, and a fair report has to say so plainly rather than only in a
concessive clause.

---

## Cost, risk, and failure modes at 18 months

1. **The linear-scaling cost is real and probably worse than a naive per-construct multiply suggests.**
   Question 1's own floor (30-60 files, ~700-1,700 lines per *simple* construct) already implies
   thousands of files and tens of thousands of lines for 30 grammar constructs; grammar constructs are
   structurally harder than the two priced examples (nested owned objects, nontrivial ordering, MSA
   subtype dispatch), so the true cost is higher, and there is no way to know how much higher without
   pricing at least one genuinely hard construct (e.g. `PhRegularRule` or `MoInflAffixTemplate`) the same
   way `Publication` was priced here — that pricing exercise has not been done, for either this path or
   any other, and is the single most valuable next measurement.
2. **The most likely failure mode is silent, not loud: grammar edits that apply cleanly, sync cleanly,
   and change meaning.** Every hazard in Question 2 is a *silent* one — no exception, no validation
   error, no merge conflict — because the representation (a scalar `Order`, a positional reference
   resolved by index) cannot distinguish "well-formed and meant" from "well-formed and accidentally
   different." A team that ships this without independently rebuilding HCLoader's own validation logic
   (the 24-alpha-variable ceiling, MPR referential integrity, environment-string validation — all
   currently unbuilt anywhere, per `docs/issues.md` C5/C6/C7) inherits FieldWorks' own crash-on-load
   behavior as the first line of defense, which is exactly the failure mode Motif's own `hc-grammar-map.md`
   was written to avoid.
3. **Android is the concrete 18-month risk, not an abstract one.** The realistic failure trajectory is:
   grammar ships first in `FwDataMiniLcmBridge` (fastest path, LibLCM does the hard work), Android is
   deferred "for now," and eighteen months later Android still does not have it, because closing that gap
   requires either the unscoped native-porting project (Question 3) or the non-CRDT `LcmCrdt` grammar
   engine (Question 2) — neither of which is a small follow-up once the FwData-side surface is the one
   real consumers depend on. "For now" tends not to resolve itself without a forcing function, and there
   is no forcing function visible in this codebase today (no open issue, ADR, or CI job gates grammar
   parity across platforms).
4. **A two-representation system is a plausible, worse-than-either-alone outcome.** If some grammar
   constructs (affix template slots) land safely in `LcmCrdt` while others (rule order, alpha variables,
   `MoAffixProcess.Output`) do not, the result is an API where *which fields are safe to edit offline on
   Android* is a per-field fact a caller has to know, not a property of the interface — the same "narrow-
   waist" failure `docs/one-api-problem.md:40-45` already names as the risk of extending `IMiniLcmApi`
   itself rather than unifying at a different layer.
5. **Golden-file/regression-fixture churn is a real, compounding maintenance tax**, visible in both priced
   PRs (`RegressionDeserializationData.json`, three `*.verified.txt` snapshot files each). Thirty
   constructs' worth of `Change`-shape and DB-model snapshots is a lot of hand-reviewed diff noise for
   reviewers to actually read carefully, and "the diff looked plausible" is a weaker guarantee for a
   phonological-rule `Change` type than for a `Publication` one.
6. **Organizational risk:** this domain (phonology/morphology) is one MiniLcm's team has not worked in
   (confirmed zero grammar model classes anywhere in `MiniLcm`/`LcmCrdt`/`FwDataMiniLcmBridge` by
   exhaustive grep, consistent with the brief's ground truth and both existing Motif docs). The 612 tests
   and two-PR precedent above are real skill in *this construct-addition playbook*, not evidence of
   HermitCrab/phonology domain expertise on the team executing it.

---

## Verdict, confidence, and what could not be verified

**Verdict:** Extending MiniLcm to cover grammar is executable engineering — the construct-addition
playbook is real and has shipped twice in an adjacent domain — but it is priced against the wrong
difficulty tier if `Publication`/`MorphType` are the yardstick, and it does not resolve the hard
Android/Linux requirement by default. It resolves Linux today, almost by accident, because `FwLiteWeb`
already carries `FwDataMiniLcmBridge` there. It does not resolve Android under either sub-option examined:
grammar-in-`FwDataMiniLcmBridge` leaves Android without grammar at all, and grammar-in-`LcmCrdt` requires
inventing a non-scalar, non-local ordering mechanism and a 66-class referential-integrity system that
does not exist today, at which point it is fair to ask whether what remains is still a CRDT or a second
grammar engine wearing Harmony's `Change` interface. Neither sub-option, nor any hybrid found in this
path's own design space, delivers "grammar, correctly, on Android" without work that has not been scoped
by anyone, in this codebase, as of this session.

**Confidence: medium-high on the technical findings** (every load-bearing claim above is a direct
`path:line` read from this session, not inherited from the existing Motif docs, except where explicitly
marked INFERRED), **medium on the cost extrapolation** (the per-construct floor is measured; the
multiplier to "hardest grammar constructs" is not, because no one has priced a hard construct the same
way).

**What I could not verify:**

- `SIL.Harmony`'s own internals (commit-log structure, causal ordering, how/when its generic merge engine
  actually resolves competing `Change` objects) — not checked out in this environment, same limitation
  already flagged in `docs/minilcm-evaluation.md:112-117`. Everything about ordering in Question 2 is
  argued from `LcmCrdt`'s own code, which is sufficient to show the *representation* problem but not to
  rule out a cleverer `Change` type built on primitives Harmony exposes that `SetOrderChange<T>` does not
  use.
- Whether `SIL.LCModel`/ICU4C/`structuremap` can, with real engineering effort, be ported to Android — I
  found no attempt, no scoping document, and no CI job targeting it anywhere in this codebase, but absence
  of an attempt is not proof of infeasibility.
- The actual cost of pricing a *hard* grammar construct (e.g. `PhRegularRule` or
  `MoInflAffixTemplate`) end-to-end in this codebase's style, since no one has built one; the Question 1
  extrapolation is explicitly a floor, not a measurement of the hard case.
- Real-world concurrency patterns for grammar editing specifically (single-writer-per-session vs. genuine
  concurrent multi-device grammar edits) — if grammar editing turns out to be single-writer in practice,
  several of Question 2's hazards become structural-but-operationally-rare rather than live risks, a
  caveat already noted in `docs/minilcm-evaluation.md`'s "what would change this verdict" section and not
  independently resolved here.
