# Grilling the grammar work, on the basis that Harmony is the change mechanism

Status: research note for a design discussion. Not an ADR. Written after ADR 0013 was accepted —
this document does not re-argue ADR 0013; it scopes the grammar work on top of it.

Repos read (commit pinned at time of writing):
- `harmony` — HEAD `c858cb4` (`c858cb429231298aef564354b8ec2d5c87507287`, 2026-07-23)
- `languageforge-lexbox` — HEAD `da284fa8e628a7acfa76a080dabfc324272ce64e` (2026-07-23)
- `LCAtom` — working tree at time of writing (docs/manifest as committed through `8337060`)
- `FieldWorks` — `Src/LexText/ParserCore/HCLoader.cs`, read in full (2,837 lines)
- `liblcm` — not re-read; relied on LCAtom's existing manifest/maps, cross-checked against HCLoader directly

Every claim below is marked **VERIFIED** (read the code/history myself, cited `path:line` or a commit
SHA) or **INFERRED** (reasoned from verified facts but not directly observed). Where I found evidence
that revises or sharpens the ground truth supplied in the prompt, I say so explicitly.

---

## 1. What a construct actually costs, measured

### 1.1 The four priced examples

| Construct | Commit(s) | Files | Insertions/Deletions | Touches `.fwdata`/LibLCM? |
|---|---|---|---|---|
| `Publication` | `4c3e5d51` (2025-02-25) | 42 | +1290 / −37 | **Yes** — `FwDataMiniLcmApi.cs` +111 |
| `MorphType` | `13eabbb5` (2025-08-14) initial; `649fb2c0` partial revert; `0cdc4a07` re-add | 31 / 12 / 16 | +1394/−16, then +42/−142, then +1664/−171 | **Yes** — `FwDataMiniLcmApi.cs` +67, `LcmHelpers.cs` +102 |
| `Comments` | `915ca19d` (2026-07-06) | 64 | +5743 / −96 | **No** — CRDT-only, zero `FwData*` files touched |
| `CustomView` | `3f8a338a` (2026-04-07) | 109 | +4478 / −857 | **No** — CRDT-only, zero `FwData*` files touched |

All four numbers are **VERIFIED** by `git show --shortstat <sha>` and `git show --name-only <sha> | grep -i fwdata` in `languageforge-lexbox`.

**The load-bearing finding the prompt's framing doesn't say out loud: two of the four priced
examples never reach LibLCM at all.** `Comments`' own PR description says "Introduce CRDT-only
comment threads" and touches no file under `FwDataMiniLcmBridge/` or `FwLiteProjectSync/`.
`CustomView` is the same — 109 files, largest of the four by file count, and none of them is a
`.fwdata` bridge file. Their cost (front-end UI, feature flags, i18n `.po` files across 8 locales,
approval-test snapshots) is real but it is **a different kind of cost than grammar's**, because
grammar's entire reason for existing is to reach LibLCM/HermitCrab. The two examples that *do* cross
into LibLCM — `Publication` and `MorphType` — are both simple flat lists (`IPossibility`, no
hierarchy, few or no internal references), and MorphType's LibLCM crossing was cheap only because
FieldWorks morph types are a **fixed, closed inventory** (`FwDataMiniLcmApi` "rejects [CreateMorphType]
since FieldWorks morph types are a fixed inventory" — commit message of `0cdc4a07`) — i.e. the
hardest direction of the sync (create-on-FwData-side) was defined away, not solved. **No priced
example has yet paid the cost of writing to LibLCM through a hierarchy, a set of cross-references,
or an ordering constraint** — which is what all 30 grammar constructs require (§4).

### 1.2 Artifact checklist, built from the diffs

Reading the four file lists (not the summaries) gives a checklist of artifact *kinds* a construct
addition touches. I list them once, generically, then say which ones each construct actually needed.

| # | Artifact kind | Publication | MorphType | Comments | CustomView |
|---|---|---|---|---|---|
| 1 | MiniLcm model class(es) | `Models/Publication.cs` (new, 30) | `MorphType` enum + `MorphTypeData` (new) | `Models/Comments.cs` (new, 79) | `Models/CustomView.cs` (new) |
| 2 | Read/Write API surface (`IMiniLcmReadApi`/`WriteApi`) | +2 / +8 | new get/write methods | +28 / +39 | new methods |
| 3 | Aggregate wiring (e.g. `Entry.cs`) | +5 (`Entry.PublishIn`) | set-on-create in `CreateEntryChange.cs` | n/a | n/a |
| 4 | `IObjectWithId`/referential-integrity plumbing | +3 | n/a (embedded, not its own entity) | +2 | (own entity) |
| 5 | CRDT `Change<T>` class(es) | 1 (`CreatePublicationChange`) + 3 more in `Entries/` (Add/Remove/Replace) + `SetMainPublicationChange` | `CreateMorphTypeChange` (added later, `0cdc4a07`) | **4**: Create(Thread), Create(Comment), Edit(Comment), SetStatus | `CreateCustomViewChange`, `EditCustomViewChange` |
| 6 | `CrdtMiniLcmApi.cs` implementation | +46 | +30 | +171 | (present) |
| 7 | DI/kernel registration (`LcmCrdtKernel.cs`) | +10 | (config registration test) | +22 | (present) |
| 8 | EF Core migration (3 files: migration, Designer, ModelSnapshot) | 634+59+36 | 729+29+3 | **two** migrations: 969+134, 989+38, +161 snapshot | (present) |
| 9 | `JsonPatchChangeExtractor` / diff plumbing | +5 | `IntegerDiffTests`, `SimpleDiffTests` (+102 total, fixing a real bug in `SimpleStringDiff`/`IntegerDiff`) | n/a | n/a |
| 10 | `FwDataMiniLcmApi.cs` (LibLCM bridge) | **+111** | **+67**, plus `LcmHelpers.cs` +102, `UpdateMorphTypeDataProxy.cs` (new, 53) | **0** | **0** |
| 11 | `XxxSync` reconciler (`MiniLcm/SyncHelpers/`) | `PublicationSync.cs` present, `EntrySync.cs` +1 | `MorphTypeSync.cs` present | n/a (no `.fwdata` side) | n/a |
| 12 | `CrdtFwdataProjectSyncService.cs` wiring | +6 | (implicit via MorphTypeSync call site) | n/a | n/a |
| 13 | `DryRunMiniLcmApi.cs` | +37 (new) | +25 | n/a | n/a |
| 14 | JS interop surface (`MiniLcmJsInvokable.cs`) | +8 | n/a | +112 | n/a |
| 15 | Generated TS types (`ReinforcedFwLiteTypingConfig.cs` + consumer `.ts`) | +12 config | +1 config | +2 config, 3 new `.ts` model files | (present) |
| 16 | Conformance test base (`MiniLcm.Tests/XxxTestsBase.cs`) | `PublicationsTestsBase.cs` (new, 94) | (existing pattern used) | n/a — 480-line dedicated `CommentTests.cs` in `LcmCrdt.Tests` instead | n/a |
| 17 | Serialization regression fixtures + approval snapshots | `RegressionDeserializationData.json` +19, 3 `.verified.txt` | 1 `.verified.txt` | **4** regression `.verified.txt` files (latest+legacy ×2), 3 more model snapshots | present |
| 18 | Third API implementation (`LfClassicMiniLcmApi.cs`) | +12 | n/a | n/a | n/a |
| 19 | Front-end UI (Svelte components, feature flags) | in-memory demo data +5 | n/a | **347-line `CommentDialog.svelte`**, 6 new `input-group` components, feature-service wiring | full custom-view dialog UI (majority of the 109 files) |
| 20 | i18n (`.po` across every locale) | none | none | **8 locale files**, +72 lines each | (not itemized here) |
| 21 | Validator/normalization wrapper | none | none | `MiniLcmApiWriteNormalizationWrapper.cs` +57 | none observed |

**Reading the table**: rows 1–9 and 16–17 are the *irreducible* cost of adding any new type to the
CRDT store regardless of whether it ever touches LibLCM — call this the "CRDT-only floor." Rows
10–13 are the *LibLCM-crossing* cost, paid only by Publication and MorphType. Rows 14–15, 19–20 are
UI/product cost that scales with whether the construct needs a human-facing editor (grammar mostly
won't, at least not initially — HC parameters are not primarily hand-typed by end users the way
comments and custom views are).

**Applying this to grammar's 30 constructs**: every one of them needs rows 1–9 (CRDT-only floor) *and*
rows 10–13 (LibLCM-crossing, because grammar's entire value proposition is reaching HCLoader — unlike
Comments/CustomView, "CRDT-only" is not an option ADR 0013 or `hc-grammar-map.md` leaves open for
grammar). None of the four priced examples paid both the LibLCM-crossing cost *and* a nontrivial
referential/hierarchical shape at the same time — Publication and MorphType are flat; Comments and
CustomView never crossed. **Grammar's 30 constructs will be the first to have to pay both bills at
once, on every single one of them.** That is where this report says plainly: harder than the priced
examples, not by degree but by kind — see §4.

### 1.3 MorphType's partial revert is a warning specific to grammar

`649fb2c0` "Remove CreateMorphType and DeleteMorphType from MiniLcm API" (−142/+42, 12 files) removed
the write path for *creating new* morph types 3 months after `13eabbb5` shipped, because a closed,
enum-like list turned out not to actually need general create/delete. `0cdc4a07` (2026-06-09) then
re-added `CreateMorphType`, but redefined its semantics — not "author a new morph type" but "make an
idempotent CreateMorphTypeChange available so missing canonical morph types can be created during
import/sync" (commit message, verbatim). **This 3-round oscillation over ~10 months, on the simplest
possible grammar-adjacent list (a closed enumeration), is direct evidence that even
"finished and shipped" MiniLcm constructs get their write-API shape wrong on the first attempt when
sync semantics (create-during-merge vs. author-directed create) aren't separated up front.** Every one
of grammar's 30 constructs will face this same question — most acutely `partOfSpeech`,
`inflectionClass`, `naturalClass` and `phonemeSet`, which (like morph types) are simultaneously
authorable objects *and* referential-integrity anchors other constructs point into.

---

## 2. How does a grammar change reach LibLCM?

This is the crux question and the one the prompt is most explicit about. I read the Harmony adapter
code directly rather than trusting the summary.

### 2.1 What "EF Core-bound" means precisely — and one exception worth naming

**VERIFIED**, `harmony/src/SIL.Harmony/Adapters/IObjectAdapterProvider.cs:5`:

```csharp
internal record AdapterRegistration(Type ObjectDbType, Func<ModelBuilder, EntityTypeBuilder> EntityBuilder);
```

Both `DefaultAdapterProvider.Add<T>` (`Adapters/DefaultAdapterProvider.cs:18`) and
`CustomAdapterProvider<TCommonInterface,TCustomAdapter>.Add<T>` (`Adapters/CustomAdapterProvider.cs:26`)
require an `EntityTypeBuilder<T>` — i.e., every registered object type must be a class EF Core can map
as an entity, full stop. `LcmCrdtKernel.ConfigureCrdt` (`languageforge-lexbox/backend/FwLite/LcmCrdt/LcmCrdtKernel.cs:190-`)
uses `CustomAdapter<IObjectWithId, MiniLcmCrdtAdapter>()` (line 194) precisely so MiniLcm's own model
classes don't have to implement Harmony's `IObjectBase<T>` directly — but each type is still
registered with `.Add<Entry>(builder => { builder.HasMany(...)... })` etc. (lines 195-296+), with
real EF relationship configuration: foreign keys, cascade deletes, `jsonb` column conversions for
embedded lists. This is **not** boilerplate-free — `Sense`'s registration alone (lines 220-241) hand-writes
three FK relationships and two jsonb conversions. Grammar's classes are far more cross-referential
(§4), so this per-type EF configuration burden scales with grammar, not away from it.

**The one genuine escape hatch, and it is a global on/off switch, not a per-type one**:
`HarmonyConfig.EnableProjectedTables` (`harmony/src/SIL.Harmony/Config/HarmonyConfig.cs:18`, default
`true`). `ObjectTypeListBuilder.Freeze()` (`Config/ObjectTypeListBuilder.cs:18-27`) only invokes each
registration's `EntityBuilder` — the thing that requires a real EF entity — `if
(config.EnableProjectedTables)`. `CrdtRepository.GetCurrentObjects<T>()` (`Db/CrdtRepository.cs:284-291`)
throws `NotSupportedException` when projected tables are off; objects are then reachable only via
`ObjectSnapshot.Entity` (generic JSON-deserialized `IObjectBase`), never via a typed
`dbContext.Set<T>()` LINQ query. **VERIFIED**: `languageforge-lexbox` sets
`config.EnableProjectedTables = true` unconditionally (`LcmCrdtKernel.cs:192`), and this is the only
call site (`grep -rn EnableProjectedTables backend/FwLite/` returns exactly that one assignment plus
the Harmony-internal references). So today, in this codebase, the escape hatch is not exercised, and
it is **all-or-nothing per `HarmonyConfig` instance, not per object type** — you cannot have lexical
types projected and grammar types snapshot-only under one `CrdtConfig` without either (a) losing typed
querying for *everything*, or (b) running two separate `HarmonyConfig`/`DbContext` instances, which
Harmony does not appear to support within one project (**INFERRED** — I did not find a multi-config
pattern in the codebase, and `CrdtProject`/`LcmCrdtDbContext` wiring assumes one config).

**Correction to the ground truth as stated**: "Harmony's adapters are EF Core-bound" is true as
implemented and exercised today, but it is not an inherent limit of the `IObjectAdapterProvider`
*interface* — `EnableProjectedTables=false` already lets a registered type skip EF entity mapping
entirely, at the cost of losing `GetCurrentObjects<T>()` (typed queries) for the whole config. This
matters for §2.3 below.

### 2.2 `FwDataMiniLcmBridge` confirmed to bypass Harmony entirely

**VERIFIED**, `FwDataMiniLcmApi.cs:26` — `public class FwDataMiniLcmApi(...)` implements
`IMiniLcmApi` directly against `LcmCache`. Its own commit boundary is `Cache.ActionHandlerAccessor.Commit()`
(`FwDataMiniLcmApi.cs:85`) — LibLCM's native Unit-of-Work commit, not Harmony's `Commit`/`IChange`.
Grepping the file for `IChange`/`Harmony` (`grep -n "IChange\|Harmony\|Commit\\b"`) turns up only that
one UOW call. **There is no code path today, anywhere in `languageforge-lexbox`, where a Harmony
`IChange` is applied to a LibLCM object.** Every LibLCM write goes through hand-written
imperative C# against `LcmCache`/`ILexEntryFactory`/etc.

### 2.3 The reconciliation pattern in production today

**VERIFIED**, `CrdtFwdataProjectSyncService.SyncOrImportInternal`
(`languageforge-lexbox/backend/FwLite/FwLiteProjectSync/CrdtFwdataProjectSyncService.cs:113-143`):
for each of `WritingSystem`, `Publication`, `PartOfSpeech`, `SemanticDomain`, `ComplexFormType`,
`MorphType`, `Entry`, it calls `XxxSync.Sync(...)` **twice** — once diffing `(snapshot, currentFwData)
→ crdtApi`, once diffing `(currentFwData, currentCrdt) → fwdataApi` — i.e. a bidirectional,
generic-`IMiniLcmApi`-driven diff-and-apply. Neither direction touches Harmony's `IChange`/`Commit`
types directly; `XxxSync` classes (`MiniLcm/SyncHelpers/*.cs`) operate purely on in-memory MiniLcm
model snapshots and call `IMiniLcmApi` methods (`CreatePartOfSpeech`, `UpdatePartOfSpeech`, etc.),
which is the *same* interface both `CrdtMiniLcmApi` (Harmony-backed) and `FwDataMiniLcmApi`
(LibLCM-direct) implement. This is genuinely option **(a)** from the prompt, already shipping, for 7
of the current 13 CRDT object types.

### 2.4 Assessing the four options against this evidence

**(a) Grammar lives only in the CRDT store; `.fwdata` gets it via an `XxxSync` reconciler, as today.**
Feasible — it is the only pattern with production mileage (§2.3), and it requires zero changes to
Harmony itself. Cost: one `XxxSync.cs` class per construct (as today), *and* a real
`FwDataMiniLcmApi` implementation for get/create/update/delete against LibLCM for every construct —
this is exactly the `+111`/`+67` lines Publication/MorphType paid, but for 30 constructs with real
hierarchy and cross-references instead of 2 flat lists. What breaks: the diff-and-apply model is
**value-level**, not operation-level — it reconstructs "what changed" from two full snapshots rather
than replaying authored intent. For grammar's order-sensitive constructs (feeding/bleeding rule order,
index-as-identity alpha variables, `MoAffixProcess.Output` positional resolution — all named in
`hc-grammar-map.md` and `api-surface-layer1.md`), a snapshot diff cannot generally recover "this is a
reorder" vs. "this is N deletes and N creates," and LWW-scalar ordering (§2.5) makes the CRDT side's
own history unreliable as an oracle for order changes independent of the sync layer. This is solvable
but is new engineering, not a rerun of the existing pattern.

**(b) Harmony gains a non-EF adapter so changes can target `LcmCache` directly.** Not really
distinct from (a) at the architecture level — `EnableProjectedTables=false` (§2.1) already gives
non-EF *storage*, but Harmony's `IChange.ApplyChange` (`Changes/Change.cs:56-`) applies to
`IObjectBase`/`T` — an in-process CLR object — regardless of how it's persisted. There is no
adapter-shaped way to make an `IChange` apply directly to an `LcmCache`-backed `ILexEntry` unless
someone writes `LcmCache`-facing `IObjectBase`/`Change<T>` implementations, at which point this
collapses into "write a `FwDataMiniLcmApi`-equivalent that happens to be invoked from inside
`Change.ApplyChange` instead of from a sync loop" — the same amount of hand-written LibLCM-facing code
as (a), just relocated. **INFERRED**: I found nothing in Harmony suggesting a design partway between
"EF entity" and "arbitrary object the adapter owns" that would make LibLCM integration meaningfully
cheaper than (a). This option does not appear to buy anything (a) doesn't already have, and adds the
risk of coupling Harmony's own commit/apply pipeline to `LcmCache`'s lifecycle (transactions, UOW,
undo stack) which today it has zero awareness of.

**(c) Grammar is CRDT-only; `.fwdata` export is one-way.** Feasible and cheapest in the near term —
matches Comments/CustomView's actual shipped pattern (§1.1) — but directly contradicts the stated
long-term direction ("CRDTs replace Send/Receive") and, more concretely, contradicts
`hc-grammar-map.md`'s premise that HCLoader (which only ever reads `.fwdata`/`LcmCache`, never the
CRDT store — **VERIFIED**, `HCLoader.cs` constructor takes `LcmCache cache`, `HCLoader.cs:75`) is the
consumer grammar must reach. One-way CRDT→nothing means HermitCrab experimentation (ADR 0010's stated
purpose) never sees a CRDT-authored grammar change unless a human re-imports through FLEx by hand.
This option is a valid *sequencing* choice (ship CRDT-side grammar first, defer the LibLCM crossing)
but not a valid *end state* given ADR 0010's purpose and the grammar map's own framing.

**(d) Something else — worth naming: HC XML as the crossing point, bypassing `.fwdata` entirely for the
parser-facing half.** `hc-grammar-map.md:94-109` already documents that `GenerateHCConfig` calls the
*same* `HCLoader.Load` the interactive parser calls, and that PanGloss's `pg-fwdata` path "bypasses
HCLoader, XmlLanguageWriter, and GenerateHCConfig entirely," reading `.fwdata` directly. That means a
change could, in principle, be judged by structural equivalence against HC XML without ever writing
back into a live `.fwdata`/`LcmCache` — i.e., grammar experimentation (the *stated primary purpose*,
ADR 0010) could be served by CRDT→HC-XML export alone, with `.fwdata` reconciliation staying an
optional, later, second track for FLEx-editability. This narrows the *first* problem considerably: you
do not need `FwDataMiniLcmApi` support for a construct to prove it drives HermitCrab correctly, only
to prove it round-trips through FLEx. **This is the most important scoping lever in this document** —
see §5.

### 2.5 A concrete Harmony defect that grammar will hit immediately

**VERIFIED**, `harmony/src/SIL.Harmony/Changes/SetOrderChange.cs:1-42`: `IOrderableCrdt.Order` is a
`double`, and `SetOrderChange<T>.ApplyChange` does `entity.Order = Order;` — a plain last-writer-wins
scalar assignment (fractional-index reordering, `Between`/`After`/`Before` at lines 15-27). This is
adequate for the manifest's 30 "positional" fields where order is *just* display/iteration order. It
is **not** adequate for the 3 "index-as-identity" fields (`PhRegularRule.StrucDesc`,
`PhSegRuleRHS.{StrucChange,LeftContext,RightContext}`) where — per `api-surface-layer1.md:122-131` —
reordering silently renames every later alpha variable, nor for `MoAffixProcess.Input` (a "feeding"
field per the same doc) where `Output` mappings resolve by position and a `move` "cannot claim a
static footprint." ADR 0013 already names this as "a defect report against Harmony," not against
LCAtom; this report confirms the defect by reading `SetOrderChange.cs` directly and confirms it is
unmitigated as of `harmony@c858cb4`.

---

## 3. Can the LCAtom manifest generate Harmony change classes?

I tested this against real generated code rather than assuming.

### 3.1 What the manifest's columns give you, mapped to what `Change<T>` needs

A hand-written `Change<T>` (e.g. `CreatePartOfSpeechChange.cs`, `SetPartOfSpeechChange.cs`, both read
in full above) needs: a constructor parameter list, a `NewEntity`/`ApplyChange` body, and — critically
— *what to do when a referenced entity is missing or deleted* (see `SetPartOfSpeechChange.ApplyChange`,
which silently nulls out the reference rather than throwing, a policy decision, not a mechanical one).

| Manifest column | Maps to | Sufficient alone? |
|---|---|---|
| `Kind` (basic/owning/rel) + `Card` (atomic/col/seq) | Which of the 25 handlers/verb-family applies (`set`/`clear`, `create`/`delete`, `addRef`/`removeRef`/`move`) | **Yes**, this is exactly what `api-surface-layer1.md`'s 25-handler table already derives, and it matches the verb sets actually used by hand-written changes (`Verbs` column literally lists them, e.g. `set|clear`, `create|delete|move|reparent`) |
| `Sig` | The referenced/contained type, i.e. the change's generic parameters | **Yes** for `specific` sigs; **no** for the 7 heterogeneous `Sig=CmObject` fields (`api-surface-layer1.md:155-157`), which need a hand-picked discriminator the manifest doesn't encode |
| `ComparisonClass` | Whether order carries meaning, and which of 4 modes | **Partially** — tells you *that* `SetOrderChange<T>`'s LWW-scalar semantics are wrong for `index-as-identity`/`feeding` fields, but not *what to do instead* (the fix is a new Harmony-level ordering type, not something the manifest can generate) |
| `Construct` | Grouping of manifest rows into an authoring-level object | **Not the same granularity as a `Change<T>` class.** One `Class` maps to *many* `Construct`s (e.g. `PartOfSpeech` class rows are split across `partOfSpeech`, `affixTemplate`, `featureStructure`, `inflectionClass`, `referralRule`, `stemName` — **VERIFIED**, `grep -n "PartOfSpeech" manifest/liblcm-inventory.tsv` shows exactly this split) — a naive "one `Change<T>` per LibLCM class" generator would produce a nonsensical object graph; the manifest's `(Class, Field)→Construct` join is necessary but the *runtime CRDT model classes* (what `MiniLcm/Models/*.cs` actually persists) do not exist in the manifest at all — they must still be hand-designed to match Constructs, not Classes |
| `Verbs` | Method names on `IMiniLcmWriteApi` | **Yes**, string-concatenatable, and this really is close to codegen-ready — see §3.2 |
| — (nothing in the manifest) | `ApplyChange`'s handling of dangling/deleted references (silent-null vs. throw vs. reject) | **No.** This is exactly the class of decision `hc-grammar-map.md`'s own "Silent-loss surface" section (lines 57-70) documents as *HCLoader's* behavior, which LCAtom must **predict**, not just mirror. A generator cannot infer "MPR referential integrity is unforgiving... raw dictionary indexers... throw `KeyNotFoundException`" (`hc-grammar-map.md:76-78`) from `(Kind, Card, Sig)` alone; that's domain knowledge about HCLoader's specific implementation, sourced from reading `HCLoader.cs`, not from LibLCM's model shape. |
| — (nothing in the manifest) | EF `EntityTypeBuilder` configuration (FKs, cascade behavior, jsonb conversions) — required by every registered CRDT type (§2.1) | **No.** `Sense`'s own registration (`LcmCrdtKernel.cs:220-241`) needed 3 hand-written relationship declarations the manifest's columns don't distinguish (e.g. `OnDelete(DeleteBehavior.SetNull)` vs `.Cascade` is a policy call about what "referential integrity" means for *that* edge, and the manifest doesn't carry it — `AssessPoisonsCache` and `Rationale` are the closest columns and they're prose, not structured deletion policy) |

### 3.2 What generation could plausibly do

**INFERRED, but grounded in the table above**: the manifest is sufficient to generate the *mechanical
skeleton* — constructor signatures, the `basic set/clear` and `rel/atomic set/clear` handler bodies (8
of the 25 handlers, per `api-surface-layer1.md`'s own count, "the basic-type half is exactly 8
handlers"), and the `IMiniLcmWriteApi` method stubs with correct parameter types. That is a real
payoff and is the strongest form in which "LCAtom's surviving work pays off" (per ADR 0013 §"What
survives"). It is **not** sufficient to generate: (1) `ApplyChange` bodies for anything beyond
trivial scalar set/clear (owning/seq `create`-into-occupied-with-implicit-detach,
`api-surface-layer1.md:94-104`, is described as needing composer-level reasoning, not a
mechanical rule); (2) the CRDT model classes themselves (nested-vs-referenced shape, jsonb vs. FK,
`IObjectWithId` membership — a design decision, not a derivation); (3) `GetReferences`/`RemoveReference`
bodies, which require knowing *which* of a class's rel fields are cascade-relevant and which are
soft-nullable (compare `Sense.RemoveReference`'s three different policies for three different fields,
`Sense.cs:36-46` — EntryId cascades to delete, PartOfSpeechId nulls out, SemanticDomains filters a
list — three distinct policies on three fields of the *same* class, not inferable from `Kind`/`Card`
alone); (4) anything touching the HCLoader-specific silent-loss/crash surface, because that surface
lives in `HCLoader.cs`'s implementation, several steps removed from LibLCM's declared model shape.

**Bottom line on Q3**: the manifest earns its keep as a **coverage and skeleton-generation index**,
not as a `Change<T>`-class compiler. Per-construct semantics — especially the referential-integrity
policy and the HCLoader-specific validation/silent-loss rules that `hc-grammar-map.md` itself
insists must be predicted before apply — defeat full generation. This is consistent with, not
contrary to, ADR 0013's own claim that the manifest "survives" while the *mechanism* built on top of
it does not.

---

## 4. Referential integrity for 30 grammar constructs

### 4.1 Measured density, from the manifest itself

**VERIFIED**, computed directly against `manifest/liblcm-inventory.tsv` (`Scope=in`):

| | Lexical | Grammar |
|---|---|---|
| In-scope rows | 157 | 230 |
| `Kind=rel` rows (reference fields) | 42 | **75** |
| Distinct constructs | 23 | 30 |
| Distinct classes carrying a `rel` field | (not isolated, see note) | **38** |
| `ComparisonClass` breakdown | — | `unordered` 196, `positional` 30, `index-as-identity` 3, `feeding` 1 |
| Heterogeneous `Sig=CmObject` fields | (0 in-scope for lexical `PhPhonRuleFeat.Item`/`MoPhonolRuleApp.Rule` are grammar) | **1** in-scope grammar row (`PhPhonRuleFeat.Item`) plus the doc-level total of 7 across the whole in-scope model — most heterogeneous refs are lexical (`LexEntry.MainEntriesOrSenses`, `LexEntryRef.*`, `LexReference.Targets`) |

Grammar's reference density (75/230 = 32.6% of fields are `rel`) is meaningfully higher than
lexical's (42/157 = 26.8%), and it is spread across **38 distinct classes** — nearly 3× the **13**
types MiniLcm currently implements `IObjectWithId` for (`IObjectWithId.cs:5-18`, the
`[JsonDerivedType]` list: `Entry`, `Sense`, `ExampleSentence`, `WritingSystem`, `PartOfSpeech`,
`Publication`, `SemanticDomain`, `ComplexFormType`, `ComplexFormComponent`, `CustomView`,
`CommentThread`, `UserComment`, `MorphType` — **VERIFIED**, exactly 13). Some grammar classes carry
several `rel` fields each on their own: `MoDerivAffMsa` 9, `MoInflAffixTemplate` 6, `MoStemMsa` 6,
`MoInflAffMsa` 4. That is a materially denser reference graph than any of the 13 existing types
(`Sense`, the densest today, has 3 references — `EntryId`, `PartOfSpeechId`, `SemanticDomains`).

### 4.2 What has to be hand-written vs. what could be generated

Every current `GetReferences`/`RemoveReference` pair is hand-written *and* carries a distinct removal
*policy* per field, not just a list of IDs (§3.2, `Sense.RemoveReference`). That policy-per-field
authorship does not shrink with better tooling; it's exactly the domain knowledge `hc-grammar-map.md`
demands ("MPR referential integrity is unforgiving" — dangling refs *crash HCLoader*, not just look
stale). What *can* plausibly be generated: the `Guid[] GetReferences()` **collection** step itself
(walk all `rel` fields per the manifest and emit their IDs) is mechanical and low-risk to generate.
The **removal policy** (cascade-delete vs. null-out vs. filter-from-list vs. — new for grammar —
*reject the whole change* because HCLoader would crash on the dangling ref) is not mechanical, and for
grammar specifically the manifest doesn't currently carry the crash-vs-tolerate-vs-widen-silently
distinction `hc-grammar-map.md` documents (24-alpha-variable ceiling, invalid-environment-widens,
dangling-MPR-throws) — that lives in prose in a *different* LCAtom doc, not in the TSV's columns.

**Sizing the work, concretely**: 38 grammar classes need referential-integrity authorship (≈3× the
13 done so far), several with 2-3× the reference count of `Sense` (today's densest case). If `Sense`'s
3-field `GetReferences`/`RemoveReference` pair (`Sense.cs:30-46`, 17 lines including the two method
bodies) is a rough per-field unit cost, `MoDerivAffMsa` alone (9 `rel` fields) is plausibly 3× that
one class's cost — and unlike `Sense`'s fields, several of grammar's `rel` fields are MPR-shaped
(pointing into `MoInflClass`/`ProdRestrict`/`ILexEntryInflType` hierarchies that HCLoader indexes with
raw dictionary lookups and crashes on when dangling, `hc-grammar-map.md:76-79`), which is a strictly
harder removal-policy question than anything `Sense` has ever had to answer (none of `Sense`'s three
policies is "reject the whole change to avoid producing a state HCLoader cannot load").

---

## 5. Sequencing — the smallest first grammar construct

### 5.1 What HCLoader actually loads first, and why it matters

**VERIFIED**, `HCLoader.LoadLanguage()` (`FieldWorks/Src/LexText/ParserCore/HCLoader.cs:164-`):
the very first domain data read (line 170) is `m_cache.LanguageProject.AllPartsOfSpeech`, building
`posSymbols` and, for each POS, walking `InflectionClassesOC` (line 173). Line 194:
`m_posFeature = m_language.SyntacticFeatureSystem.AddPartsOfSpeech(posSymbols)` — this feature is then
threaded into essentially everything downstream: MSAs (`HCLoader.cs:932,987,1018,1036`), affix
templates (line 1682), compound rules (lines 1851-1864), and stem-name regions
(`LoadLanguage`, lines 206-224, calls `LoadAllPartsOfSpeech(pos)` per stem name). `partOfSpeech` is
not merely "one of 30 constructs" — it is the one every other construct's HCLoader read is gated
through.

### 5.2 PartOfSpeech is also the cheapest grammar construct to start from, because it isn't starting from zero

This is the load-bearing correction to the prompt's "MiniLcm has zero grammar model classes today"
framing, found while investigating: **a `PartOfSpeech` model class already exists in MiniLcm**
(`MiniLcm/Models/PartOfSpeech.cs`, **VERIFIED**), and it is already wired end-to-end — `IPossibility`,
`IObjectWithId<PartOfSpeech>`, a `PartOfSpeechSync.cs` reconciler
(`MiniLcm/SyncHelpers/PartOfSpeechSync.cs`), `CreatePartOfSpeechChange.cs` and
`SetPartOfSpeechChange.cs` in `LcmCrdt/Changes/`, and a full get/create/update/delete implementation
in `FwDataMiniLcmApi.cs` (lines 288-348, 662-669) that already reads/writes real LibLCM
`IPartOfSpeech` objects, including attaching a POS to a `Sense` via its MSA (lines 1604-1635).

But it is a **stub**, not the grammar-engineering construct `hc-grammar-map.md`/`api-surface-layer1.md`
mean by "partOfSpeech": `PartOfSpeech.cs` has exactly `Id`, `Name`, `DeletedAt`, `Predefined` — no
`Abbreviation` (there's a literal `// TODO: Probably need Abbreviation in order to match LCM data
model` comment on line 7), no hierarchy (`SubPossibilitiesOS`), no `InflectionClassesOC`, no
`DefaultInflectionClassRA`. `PartOfSpeechSync.PartOfSpeechDiffToUpdate` has a matching commented-out
TODO for `Abbreviation` (`PartOfSpeechSync.cs:33-36`). The manifest's `partOfSpeech` construct itself
is 6 rows (`BearableFeatures`, `CatalogSourceId`, `DefaultInflectionClass`, `InflectableFeats`, plus
`Name`/`Abbreviation` inherited from `CmPossibility`), and the class's other fields feed 5 more
constructs (`affixTemplate`, `featureStructure`, `inflectionClass`, `referralRule`, `stemName`) — so
"finish PartOfSpeech" is not free, but it reuses a proven scaffold end-to-end (model → sync → CRDT
change → EF registration → LibLCM bridge) rather than building all six layers from nothing, the way
`Publication` had to (§1.1).

### 5.3 Recommendation

**Extend `PartOfSpeech`, specifically its `SubPossibilitiesOS` hierarchy (owning/seq, self-referential)
and `InflectionClassesOC`/`DefaultInflectionClassRA` (a brand-new referenced type, `MoInflClass`), as
the first real grammar construct** — not `naturalClass` or `environment`, which `api-surface-hc.md`
correctly identifies as structurally smaller ("3 fields," "1 field," "direct primitives, no
composer") but which are both **greenfield** (no existing MiniLcm scaffold at all) and **peripheral**
in HCLoader's load order (natural classes and environments are consumed well after POS, and gate less
of the rest of the load). PartOfSpeech justifies itself on three grounds, all from evidence above:

1. **HCLoader-load-order**: it is the first thing read (`HCLoader.cs:170`) and gates the largest
   number of downstream reads of any single construct (§5.1) — proving grammar arrives correctly here
   de-risks more of the rest of the grammar surface than any other single construct would.
2. **Cost asymmetry**: it is the only grammar construct that reuses a full, proven, six-layer scaffold
   (model → `IObjectWithId` → sync helper → CRDT `Change` → EF registration → LibLCM bridge) instead
   of paying Publication's/Comments'-scale greenfield cost (§1.1-1.2). The *incremental* work — a
   self-referential owning/seq field and one new referenced type — is exactly the minimum needed to
   prove §4's referential-integrity generalization (a new `IObjectWithId` type, `MoInflClass`, with a
   `rel/atomic` pointer back into `PartOfSpeech` via `DefaultInflectionClassRA`) without also paying
   for five other constructs' worth of greenfield plumbing at the same time.
3. **It forces the real open questions early, cheaply**: a self-referential hierarchy exercises
   Harmony's `owning/seq` + `reparent` handling (flagged as "structurally plausible but unevidenced"
   by `api-surface-layer1.md:199-201`) on a small, well-understood shape, and `DefaultInflectionClassRA`
   pointing at a not-yet-existing type forces an explicit decision on §4's removal-policy question
   (what happens when the referenced `MoInflClass` is deleted — same shape as `Sense.PartOfSpeechId`'s
   already-solved null-out policy, but on a *new* type, proving the pattern transfers) before any of
   the harder cases (`MoDerivAffMsa`'s 9 references, ordered rule constructs) are attempted.

What it deliberately does **not** prove: ordering semantics (§2.5's `SetOrderChange` defect),
MPR-crash-on-dangling-reference behavior (§4.2), or the HC-XML-vs-`.fwdata` crossing choice (§2.4(d)).
Those are real, and larger, and should be sequenced next — but attempting them first, on a construct
with no existing scaffold, would conflate "grammar is hard" with "everything is hard the first time,"
which PartOfSpeech's existing stub lets this project avoid.

---

## Three questions a human must decide

1. **Does grammar's first shipped version need to reach `.fwdata`/`LcmCache` at all, or can "reaches
   HermitCrab" be satisfied by CRDT→HC-XML export alone (§2.4(d))?** This is the single highest-leverage
   scoping decision in this document — it determines whether `FwDataMiniLcmApi` (2,016 lines today,
   `+111`/`+67` lines per construct so far) needs to grow by 30 constructs' worth of hand-written
   LibLCM-facing code before grammar is useful for its *stated primary purpose* (ADR 0010,
   HermitCrab experimentation), or whether that cost can be deferred to a second track (FLEx
   editability) after the CRDT-side and HC-XML-side halves already work.
2. **Who owns fixing `SetOrderChange`'s LWW-scalar semantics (§2.5) before grammar's 3
   index-as-identity and 1 feeding-class fields are authored?** This is a Harmony-level change (or a
   documented, tested workaround built on top of it), not a LCAtom-manifest or MiniLcm-model problem,
   and per ADR 0013 it's explicitly out of LCAtom's remit to build a competing mechanism — so it needs
   a Harmony-team-facing decision, not a unilateral one.
3. **Is "generate the mechanical skeleton, hand-write the referential-integrity and HCLoader-specific
   validation logic" (§3.2's actual finding) an acceptable division of labor, or does the org expect
   full generation from the manifest?** If the latter, the manifest needs new columns (removal policy
   per `rel` field, HCLoader crash/widen/tolerate classification per field) that do not exist today and
   are not mechanically derivable from LibLCM's model shape — they come from reading `HCLoader.cs`
   line-by-line, which is exactly how `hc-grammar-map.md` was produced in the first place.

## Confidence

**Medium-high** on §1 (measured, not estimated — every number is `git show --shortstat` or a direct
file grep) and §2.1-2.3 (read the actual adapter/repository/sync code). **Medium** on §2.4's option
comparison and §3 (grounded in verified code but the "what a generator could plausibly do" claims are
reasoned, not built and tested — I did not attempt to actually generate a `Change<T>` class from the
manifest). **Medium** on §4's sizing (the reference-density numbers are exact; the "3× cost" estimate
for referential-integrity authorship is an analogy from `Sense`, not a built prototype). **Medium-high**
on §5 (HCLoader load order is directly read; the PartOfSpeech-scaffold finding is a direct code read
that revises the prompt's framing).

## What I could not verify

- Whether `EnableProjectedTables=false` (§2.1) has ever been exercised in any Harmony consumer, in
  this codebase or elsewhere — I found the flag and its code paths, not evidence of it being used or
  tested end-to-end. Its practical viability as an escape hatch is therefore partly speculative.
- Whether liblcm's `MoInflClass` (the type §5.3 recommends adding) has any surprising cross-references
  beyond what the manifest's 5 `rel` rows for that class show — I read the manifest and HCLoader's use
  of it (`GetDefaultInflClass`, `HCLoader.cs:2629-`) but did not read `MoInflClass.cs` in `liblcm`
  directly.
- Actual effort (hours/days) for any of this — I measured historical diffs, not implementation time,
  and the four priced examples were built by people already fluent in this codebase.
- Whether PanGloss's `pg-fwdata`/`compile_project` path (cited from `hc-grammar-map.md`, not
  independently re-read here) is mature enough today to be the primary target for option (d) in §2.4,
  or is itself experimental — I relied on LCAtom's existing doc for this claim rather than reading
  PanGloss source, which wasn't in the repo list provided.
