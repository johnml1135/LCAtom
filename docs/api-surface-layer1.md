# API surface, layer 1 — the LibLCM primitive surface

The reconciled output of two independent reviews of `manifest/liblcm-inventory.tsv` — one grouping by
**semantic authoring intent**, one by **syntactic/structural shape**. Semantics decides *what the
constructs are*; structure decides *whether the result is total and unambiguous*. Implements
[ADR 0009](adr/0009-layered-api-primitives-and-composers.md).

## Scope, computed rather than assumed

Scope is **owning-edge reachability** from the domain roots (`LexDb`, `MoMorphData`, `PhPhonData`,
`FsFeatureSystem`, `PartOfSpeech`, `LexEntry` — the last because `LexDb` only *references* entries),
plus the declared `CmPossibility` subclasses in-scope lists use, minus the derivation-trace family.

**473 in-scope properties across 95 classes.** Basic: MultiUnicode 52, MultiString 51, Unicode 35,
Integer 28, Boolean 25, String 10, Time 5, Guid 2, TextPropBinary 1. Relations: owning/atomic 69,
rel/atomic 57, owning/col 39, rel/col 38, owning/seq 33, rel/seq 28. (An earlier count of 478/96
included 5 `TextTag` rows later demoted as a scope over-inclusion — [issue D5](issues.md).)

A naming heuristic got this wrong in **both** directions, which is why it is computed:

- **False positives** — the derivation-trace family (`MoDeriv`, `MoDerivTrace`, `MoDerivAffApp`,
  `MoDerivStepMsa`, `MoCompoundRuleApp`, `MoInflAffixSlotApp`, `MoInflTemplateApp`, `MoPhonolRuleApp`,
  `MoStratumApp` — 9 classes, 28 props) *looks* like grammar but is reachable only from
  `WfiAnalysis.Derivation`, an out-of-scope interlinear class. It is analyzer output, never authored.
  Tagged `Scope=trace`.
- **False negatives** — `CmPossibility`/`CmPossibilityList` (load-bearing for nearly every in-scope
  list), `CmMedia`, `CmPicture`, `CmTranslation`, `CmResource` (the applied-log), `StText`/`StPara`.
  These carry no domain prefix but back in-scope fields.

`Scope=in` is therefore *necessary and sufficient by construction*, and each row records why.

## The collapse criterion

Collapse a set of concrete classes into one construct when **the concrete class is an implementation
choice the runner can infer or the payload selects**. Keep them separate when the author must
deliberately choose. Two amendments, both earned by hard cases:

1. **Payload-selectable is necessary but not sufficient.** When the discriminant *is* the primary
   linguistic assertion, name it — even though a payload could technically select it.
2. **Construct identity is a function of `(class, owning field)`, not class alone.** The
   `PhSimpleContext*`/`PhSequenceContext`/`PhIterationContext` classes mean two different things by
   reachability path: via `PhEnvironment.Left/RightContext` they are **dead** (HCLoader reads only
   `StringRepresentation`), while via `PhSegRuleRHS.Left/RightContext`, `PhSegmentRule.StrucDesc`, and
   `MoAffixProcess.Input` they are **read structurally**. One class set, two constructs: `environment`
   (string-authored, no context kinds at all) and `ruleContext` (the real structural surface).

Applied to the hard cases:

| Case | Decision |
| --- | --- |
| `PhNCSegments` / `PhNCFeatures` | **One** `naturalClass` — segments-vs-features is means-selection, same linguistic claim |
| The four MSA classes | **One construct family and namespace, four distinct `create*` kinds.** Payload shapes are nearly disjoint, and MSA subtype *is* the primary grammatical assertion. Shared `MoMorphSynAnalysis` setters generate once. This refines ADR 0009's illustrative "three MSA classes are all `msa`" |
| `PartOfSpeech` vs `CmPossibility` | **Stands alone** — 12 grammar-engineering fields, and a POS author is not tagging a list |
| Other named `CmPossibility` subclasses | **Each gets its own construct**, however few extra fields. Once LibLCM *names* a subclass, validation and the runner pattern-match on class identity — an `addRef` into `LexDb.MorphTypes` must create a `MoMorphType`, never a bare `CmPossibility`. Generic `possibility` is the fallback only for lists with no dedicated subclass |
| `MoStemAllomorph` / `MoAffixAllomorph` | **One** `allomorph` — the concrete is fully determined by the chosen morph type |
| `MoAffixProcess` | **Separate.** Despite sharing `MoAffixForm`, it is a *rule* (`Input` pattern + `Output` action list), not a form with different fields |
| The four `MoRuleMapping` concretes | **Collapse** into `addOutputStep(kind: copy \| insertNC \| insertPhones \| modify)` — the action *is* the payload discriminant, and the shapes are small and interchangeable |
| `PhRegularRule` / `PhMetathesisRule` | **Separate** — structural RHS vs a compact string. But they share one ordering sequence, so `move` is family-wide across both |
| `MoEndoCompound` / `MoExoCompound` | **Two named creates** under one `compoundRule` construct — endo-vs-exo is the defining fact, not an implementation detail |
| Abstract bases | **Never constructs** — and this is a data fact, not a policy: a class with no own fields produces no inventory row at all (`PhContextOrVar`, `PhSimpleContext`, `FsAbstractStructure`, `MoRuleMapping` appear only as `Base`/`Sig`) |

**Inherited-field policy.** A kind is owned by the most specific *merged* construct the field is
reachable through — never the bare base, never duplicated across already-merged concretes. Proof that
this is required, not stylistic: `CmPossibility.Name` reached via `PartOfSpeech` must yield
`grammar/partOfSpeech/setName`, and via `LexRefType` must yield `lexical/lexRefType/setName`. One
manifest row, two constructs, two kind strings — a single shared kind would be ambiguous about which
validation applies.

## Structure: 25 handlers behind ~1,100 kinds

**15 distinct `(Kind, Card, Sig-category)` shapes** occur in-scope (21 across the whole model).
Sig-category is *specific* (one declared destination class) or *CmObject* (heterogeneous).

| Shape | Verbs |
| --- | --- |
| `basic` scalar (Integer, Boolean, Time, Guid) | `set`, `clear` |
| `basic` textual (Unicode, MultiUnicode, String, MultiString) | `set`, `clear` |
| `owning/atomic` | `create` (incl. into-occupied), `delete` |
| `owning/col` | `create`, `delete` |
| `owning/seq` | `create`, `delete`, `move`, `reparent` |
| `rel/atomic` | `set` (attach), `clear` (detach) |
| `rel/col` | `addRef`, `removeRef` |
| `rel/seq` | `addRef`, `removeRef`, `move` |
| entity-level | `delete`, `merge`, `replace` |

**25 handlers** implement roughly **1,100 generated kind strings** — the handler count is what drives
implementation effort; the kind count is only surface area. The basic-type half is exactly **8**
handlers (4 shape families × set/clear), matching ADR 0009's "~8 type handlers" estimate; the
relation/owning half adds ~17 that estimate did not cover.

Only **9 of 11 basic sigs occur in-scope**, and just 7 materially (Guid 2, TextPropBinary 1 are
marginal): `GenDate` and `Binary` occur **only** out-of-scope, so their handlers are held in reserve.

### Totality: owning/atomic replacement resolved

All 69 in-scope `owning/atomic` fields are reachable via **`create`-into-occupied, with implicit
detach** — closing the critical gap the adversarial review raised. The argument is structural, not
preference: `set` is barred from owning slots; a whole-object `replace` verb is the Kubernetes
`managedFields` anti-pattern ADR 0009 rejects; `reparent` moves an *existing* object cross-owner and has
nothing to move here; and `delete`-then-`create` would trigger a full ownership cascade when LibLCM's
own overwrite semantics are a **detach, not a cascade** — destroying more than the engine does. `create`
already carries `owner`/`ownerField`/`placement`, so extending it to detach the incumbent is the minimal
change and mirrors the engine. The displaced occupant is a **disclosed orphan effect**; a composer that
can prove it has no other referent emits an explicit `delete` (ADR 0009 §6).

### Comparison class

```
basic | atomic | col  → Unordered
seq                   → Positional, unless overridden
```

Override list — `seq` alone does **not** imply positional:

| Field | Class | Why |
| --- | --- | --- |
| `PhPhonData.PhonRules` | **Feeding** | a neighbour rule's *content* edit changes what this rule produces |
| `LexEntry.AlternateForms` | **Feeding** | allomorph order encodes disjunctive elsewhere-blocking |
| `PhRegularRule.StrucDesc`, `PhSegRuleRHS.{StrucChange,LeftContext,RightContext}` | **a third mode** | index *is* identity for alpha variables (α/β/γ) — see below |
| `MoAffixProcess.Input` | **discovered footprint** | `Output` mappings resolve by position, not identity — see below |

**Correction to an earlier claim.** The index-as-identity mode does **not** live on
`PhPhonData.FeatConstraints`. Alpha-variable names come from `IPhRegularRule.FeatureConstraints`, a
*per-rule virtual* that scans `StrucDescOS` and then each `RightHandSidesOS[i]`'s
`StrucChange`/`LeftContext`/`RightContext` in fixed order and collects distinct constraints in
**first-appearance order**; `HCLoader` then assigns `VariableNames[i]`. So a `move` on the
`FeatConstraints` *pool* is semantically **inert**, while a `move`, mid-sequence `create`, or content
edit on `StrucDesc`/`StrucChange`/the context slots is what silently renames every later variable. Two
consequences: the third mode belongs to those rule-internal sequences, and the **24-variable ceiling is
per-rule**, so a pre-apply check must simulate that exact traversal rather than counting distinct
constraints anywhere in the rule.

**~~A second discovered-footprint case.~~ Withdrawn 2026-07-27 — this hazard is not real.**
`MoAffixProcess.Output` mappings (`MoCopyFromInput`, `MoModifyFromInput`) hold a `rel/atomic`
reference into `Input`, and `HCLoader` renders it as `ContentRA.IndexInOwner + 1`
(`HCLoader.cs:1383`, `:1416`). The original claim — that reordering `Input` "silently renumbers every
`Output` mapping" — mistook a *rendering* for a *binding*. `ContentRA` is an object reference; the
index is computed at export time solely to produce HermitCrab's `partName` string. Reorder `Input`
and the reference still resolves to the same part, so the exported name changes **correctly**,
tracking its referent. There is no silent breakage and no discovered footprint here. `move` on `Input`
keeps a static footprint.

This matters beyond this paragraph: it removes one of the three mechanisms cited as evidence that
ordered grammar cannot ride on a scalar-order CRDT (see
[ADR 0013](adr/0013-harmony-is-the-change-mechanism.md)). Two survive — feeding/bleeding rule order
and index-as-identity alpha variables — and they reduce to a single requirement, a sequence that
converges correctly.

Checked and left positional: `MoInflAffixTemplate.PrefixSlots`/`SuffixSlots` (neighbour *identity*
matters; a neighbour's internal edits don't).

### Naming

`kind = {group}/{construct}/{verb}{Noun}` — `Noun` is the field in lowerCamel, except: `create`/`delete`
drop it when the construct implies one owning field; `addRef`/`removeRef` singularise; and the reviewed
map may substitute a domain-meaningful synonym (`SubPossibilities` → `addSubcategory`).

## Payload disambiguation rules

1. **Always envelope rich types.** `String` vs `Unicode`, and `MultiString` vs `MultiUnicode`, collide
   whenever the rich value degenerates to a single unformatted run. The rich form never omits its run
   envelope, keeping the shapes disjoint by construction.
2. **`placement` is required on every `seq` verb** — optional-with-default-append would make `rel/seq`
   and `rel/col` byte-identical.
3. **Heterogeneous `Sig=CmObject` requires an explicit type discriminator** (7 in-scope fields:
   `LexEntry.MainEntriesOrSenses`, `LexEntryRef.{ComponentLexemes,PrimaryLexemes,ShowComplexFormsIn}`,
   `LexReference.Targets`, `MoPhonolRuleApp.Rule`, `PhPhonRuleFeat.Item`).
4. Per-field kinds are what make a `Guid`-sig field distinguishable from GUID-shaped text — the
   structural reason ADR 0009 rejects a runtime field-name parameter.

## Not authorable vs. authorable-but-HC-inert

These are **two different things** and were previously conflated under one heading, which produced
inconsistent classification. Full coverage is HCLoader-complete, but LCAtom is still a general LibLCM
change-set runner: a field can be perfectly legitimate to author for a human dictionary while having no
effect whatever on a parse.

**Generates no kind — not authorable at all:**

- engine-computed: `LexEntry.HomographNumber`, `MoMorphSynAnalysis.GlossString`,
  `LexEntry.MainEntriesOrSenses`, `FsFeatureSpecification.RefNumber`/`ValueState`, all
  `DateCreated`/`DateModified`;
- import residue: `LiftResidue` ×8, `ImportResidue` ×2;
- `Scope=trace` — the derivation-trace family (28 props), analyzer output;
- provably inert *and* meaningless to author: `PhEnvironment.AMPLEStringSegment`, the `<XAmple>` half of
  `ParserParameters`, the six stratum **reference** fields
  (`MoCompoundRule`/`MoDerivAffMsa`/`MoStemMsa`/`MoInflAffixTemplate.Stratum`,
  `PhSegmentRule.InitialStratum`/`FinalStratum`) — the model comment promises per-rule stratum scoping
  and it is silently ignored, so offering the control would be a lie.

**Authorable, but `HcReachable=no` — real LibLCM surface that cannot affect a parse:** every grammar
`Description`, `MoMorphData.TestSets`/`AnalyzingAgents`, and the whole lexicographic apparatus
(`LexSense`'s descriptive fields, `LexEtymology`, publication flags). These keep their kinds and are
marked `HcReachable=no`, so a grammar-experimentation workflow can filter them out while a dictionary
workflow still reaches them.

`MoStratum` itself keeps `create`/`delete`/`setName`: a **dangling** `StratumRA` causes silent parser
failure, so the objects' existence and referential integrity are real even though both their content
and every reference to them are inert.

## Gaps recorded, not papered over

1. **Fixed — Integer enum table now exists.** The `(Kind, Card, Sig)` triple alone could not
   distinguish a closed enumeration (`PhSegmentRule.Direction`, `MoAdhocProhib.Adjacency`) from a
   magnitude (`CmPicture.ScaleFactor`); typing all 28 in-scope `Integer` fields as bare integers
   would have invited magic-number authoring. `classify.ps1` added the manifest's `EnumValues`
   column (issue B7): a confirmed `value=Name` mapping for 11 fields, `unknown` for 2 more pending
   a citation, and blank (magnitude, deliberately not an enum) for the remaining 15.
2. **`reparent` is confirmed only for `owning/seq`.** All three ADR 0008 examples are sequences;
   atomic and collection reparent are structurally plausible but unevidenced — treat as unconfirmed
   pending a conformance vector.
3. **Fixed — the third ordering mode has a home.** `classify.ps1` added the manifest's
   `ComparisonClass` column (issue B8) with `index-as-identity` as one of its four values, alongside
   `unordered`, `positional`, and `feeding`.
4. **Writing systems and custom fields have no inventory row at all** — `LangProject.*Wss` are
   space-joined ID strings. Both families are real but not derivable from this manifest; they are the
   open field space (ADR 0009 §4).
5. **Needs HCLoader-read confirmation before shipping kinds:** `FsFeatStruc.FeatureDisjunctions`,
   `FsDisjunctiveValue`, `FsNegatedValue`, `FsSharedValue`, `MoAdhocProhibGr`.
6. **Coordinated multi-field rules invisible in any single row** — `LexReference`'s floor-of-2, the five
   slot sequences over one pool, `MoDerivAffMsa`'s paired feature trees, `LexEntryRef`'s
   attach-before-set plus first-component→Primary. These are exactly why the `class → construct` map is
   reviewed rather than mechanical.
