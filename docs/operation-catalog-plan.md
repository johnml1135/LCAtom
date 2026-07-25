# Operation-catalog plan — lexical & grammar completeness

The roadmap from the one-operation skeleton (`lexical/sense/setGloss`) to lexical + grammar
completeness.

**The API surface is designed, not transcribed.** The catalog is **~9 primitive verbs over a generated
per-field kind namespace**, plus a layer of composers — see
[ADR 0009](adr/0009-layered-api-primitives-and-composers.md). The Flexicon inventory below (~150
methods) is an **inventory of construct coverage and gotchas, not a design target**: Flexicon is
AI-generated and its per-field method surface is an implementation artifact.

Source hierarchy: **FieldWorks** (shipping, 20 years, 1000+ languages) is the authority on how to do a
thing properly; **LibLCM + its tests** are ground truth for engine semantics; **Flexicon** maps which
constructs matter and contributes scar tissue. LCAtom re-implements rather than ports
(Python/LGPL — [ADR 0003](adr/0003-feasibility-findings.md)).

**Full coverage is "C# `HCLoader` complete"** — every construct `HCLoader` can produce from a FieldWorks
project must be authorable here in a friendly way ([ADR 0010](adr/0010-hermitcrab-experimentation-is-the-primary-purpose.md),
[HC surface scope](hc-surface-scope.md)). LibLCM's surface must still be 100% classified for write
safety, but it is the storage target, not the yardstick. Consequences: a member inert to `HCLoader`
earns no priority however prominent it looks in the model; constructs HC supports but `HCLoader` cannot
produce (multi-stratum, realizational morphology, multiple phoneme sets) are out of scope; and a
consumer compiling less than `HCLoader` produces is a sequencing signal plus a reporting obligation, not
a narrower scope.

Contract groups: **`lexical`**, **`lists`**, **`system`**, **`grammar`**. Comparison classes (from
[comparison footprint](change-set-contract.md#comparison-footprint)): **U** unordered, **P**
positionally ordered, **F** feeding (semantically ordered). Authoring: **raw** object-ops vs reverse
**`Expand`** (structured HC-intent, [ADR 0001](adr/0001-hermitcrab-projection-not-canonical.md)).

## The machinery the skeleton lacks

`setGloss` needed only *declared-footprint* effect capture on an existing object. Completeness needs,
in rough order of first use:

1. **The generated kind namespace + manifest as type system.** Per-field kinds generated from the
   coverage manifest, which must carry field type, comparison class, and the reviewed
   `class → construct` map ([ADR 0009](adr/0009-layered-api-primitives-and-composers.md) §3).
2. **Create + identity mapping.** A create proposes a new entity by canonical id; the runner mints a
   storage GUID, records the `canonicalId → GUID` mapping in the Assessment/Receipt, and later ops in
   the same change set resolve that entity through the mapping. `create` carries `owner`, `ownerField`
   where ambiguous, an initial value map, and `placement`. First used: `lexical/entry/create`.
3. **Discovered-footprint effect capture.** One mechanism, three triggers: `delete` with referrers,
   `merge`, `convert`. Reach is found by evaluating the baseline (`AllOwnedObjects` +
   `ReferringObjects`), so effects are read-back-derived and full re-assessment is forced; no static
   footprint may be claimed ([ADR 0009](adr/0009-layered-api-primitives-and-composers.md) §5). Requires
   the load-time incoming-ref warm-up ([ADR 0006](adr/0006-engine-reality-apply-readback-preflight.md))
   and must distinguish "the engine will clean this reference" from "this will be left dangling."
4. **Sequence ops + positional comparison.** `move` with identity-relative anchors (never an index);
   footprint reaches neighbor *identity* only (class P).
5. **Reparent.** Owned-subtree move to a *different* owner — confirmed across pictures,
   pronunciation/example media, possibility items ([ADR 0008](adr/0008-operation-model-reparent-and-compound-ops.md) §1).
6. **Two field spaces.** Closed/generated for model fields; open/runtime-validated for custom fields
   via the `(class, name)` locator ([ADR 0009](adr/0009-layered-api-primitives-and-composers.md) §4).
7. **Custom-field non-undoable phase.** `AddCustomField` in its own non-undoable unit of work, first,
   one-way ([ADR 0005](adr/0005-schema-operations-non-undoable-uow.md)); `define` is digest-neutral by
   additive-stability, and schema changes are their own effect category.
8. **Writing-system family.** Two-step `Create(tag)` then `Set(ws)`, current-vs-full-list sync via
   `AddToCurrent*` — never raw string-list assignment.
9. **Feeding-order comparison (class F).** `PhPhonData.PhonRules` only — every insert/reorder
   re-derives surrounding rules' effects.
10. **Per-construct validation.** `LexReference` floor-of-2 + target homogeneity; typed-collection
    destinations (`TypesOC` vs `SubPossibilitiesOS`, `IFsFeatStrucTypeFactory` not `ICmPossibilityFactory`);
    the five parallel slot sequences over one unordered pool; heterogeneous `sig="CmObject"` collections.
    Attaches to the `class → construct` map.
11. **Delete policy.** Honor the engine's own `ICmObject.CanDelete` and `CmPossibility.IsProtected`
    **explicitly** — they are advisory in LibLCM and LCAtom is exactly the programmatic path that would
    bypass them. Disclose the full closure with a warning by default; hard-refuse the protected set with
    an actionable message ("use FieldWorks directly"); writing-system delete is out of v1 scope.
12. **The composer layer.** `Expand`, find-and-replace, batch update, duplicate, `setPartOfSpeech` —
    each emitting Change Sets of primitives, with the composer riding as provenance. Compensating
    sweeps are usually explicit composer-emitted `delete`s rather than hidden machinery
    ([ADR 0009](adr/0009-layered-api-primitives-and-composers.md) §6).
13. **HC round-trip.** Forward projection (harvest `HCLoader`) + reverse `Expand`
    ([hermitcrab-projection](hermitcrab-projection.md#authoring-input-and-round-trip)) — the flagship
    composer.
14. **Apply hardening.** Bind `apply` to a prior Assessment's footprint digest (ADR 0004 §3 — the
    shipped `ChangeSetApplier.Apply` takes no Assessment and is always bare) and provide the
    exclusive-write guarantee ([ADR 0006](adr/0006-engine-reality-apply-readback-preflight.md) §4).

## Catalog by group (condensed — full `path:line` inventory is the Flexicon harvest reference)

### `system` (bootstrap first)
| Family / kind | Machinery | Notes |
| --- | --- | --- |
| `system/writingSystem/{create,delete,setDefaultVern,setDefaultAnalysis,setFont…}` | WS family | two-step create/set; delete orphans in-WS string data; font props are LDML not `.fwdata` |
| `system/customField/{define,delete}` | **custom-field non-undoable phase** | mandatory separate phase; Flexicon refuses in-UoW |
| `system/customField/{setValue,clearValue}` | create/set/clear, dispatched by field type | ordinary data op, hard-sequenced after the schema phase; object-valued fields blur schema/data |

### `lists`
| Family / kind | Machinery | Notes |
| --- | --- | --- |
| `lists/possibilityList/create` | create | custom lists may be unowned |
| `lists/possibilityList/delete` | delete-cascade | **Flexicon refuses to implement** — needs a system-list denylist; is list-delete even in v1 scope? |
| `lists/possibility/{create,delete,setName,setAbbrev,setDescription}` | create/delete-cascade/set | right factory/collection per destination (`PossibilitiesOS` vs `SubPossibilitiesOS`) |
| `lists/possibility/move` | **reparent** | `MoveItem` |
| `lists/publication/*` | possibility-item family + publication setters | must exist before `lexical/publication/*` flags |

### `lexical`
| Construct → kinds | Family | Machinery | Cmp |
| --- | --- | --- | --- |
| entry: `create`,`delete`,`setLexemeForm`,`setCitationForm`,`set{Comment,Bibliography,…}`,`setMorphType`,scalar setters | create / delete-cascade / set | create+identity-mapping; delete-cascade | U/P |
| entry: `mergeInto` | **compound/graph** | read-back footprint | — |
| sense: `create`,`delete`,`setGloss`(done),`setDefinition`,`add/removeSemanticDomain`,note setters,`setStatus`,`setSenseType` | create/delete/set/collection-ref | delete-cascade | U/P |
| sense: `setPartOfSpeech`,`setGrammaticalInfo` | compound (owned MSA) | **MSA-orphan compensating sweep** | U |
| sense: `add/remove/movePicture` | create/delete/**reparent** | reparent | P |
| sense: `mergeInto` | **compound/graph** | read-back footprint | — |
| allomorph (`MoForm`): `create`,`delete`,`setForm`,`setMorphType`,`add/removePhoneEnv` | create/delete/set/collection-ref | first-form-becomes-LexemeForm special case; PhoneEnv add permits duplicates (multiset) | P/U |
| etymology / pronunciation(+media) / example(+translations,+media): `create`,`delete`,`reorder`,`set…` | create/delete/sequence/set | delete-cascade; media `move` = **reparent** | P |
| MSA (lexical side): `createStem/DerivAff/InflAff/Unclassified`,`setPos` | create/set | — | U |
| MSA: `changeAffixVariant`,`removeOrphaned` | **compound/graph** + sweep | read-back footprint; project-wide orphan test | — |
| variant/complex-form (`LexEntryRef`): `create`,`delete`,`setType`,`add/removeComponent` | create/delete/collection-ref | **attach-before-set**; **first-component→Primary** (atomic 3-collection multi-write, must not split); `sig="CmObject"` heterogeneous targets | P |
| lexical relation (`LexRefType`/`LexReference`): type `create/delete/setName`, `lexreference/create/delete/addTarget/removeTarget` | create/delete/collection-ref | **floor-of-2 + target homogeneity** per-construct validation | U |
| publication flags (entry/sense/example/pronunciation) | add/remove collection ref | needs publication types to exist | U |
| reversal index/entry: `create`,`delete`,`setForm`,`add/removeSense`,`createSubentry` | create/delete/collection-ref | coverage gap now specified; sense link is a pure **reference** — deleting the sense orphans the reversal entry (no cascade) | U/P |

### `grammar` (dependency-ordered; see ordering below)
| Construct → kinds | Family | Machinery / gotcha | Cmp | Authoring |
| --- | --- | --- | --- | --- |
| POS: `create`,`delete`,`setName/Abbrev`,`add/removeSubcategory`,`createWithGuid` | create/delete/set | subcategory ordered; catalog vs plain path | U/P | raw (Frame) |
| feature system: `createInfl/PhonFeature`,`createType`,`createValue`,`createClosedWithValues` | create (+compound) | `IFsFeatStrucTypeFactory` not generic; `TypesOC` vs `FeaturesOC` | U | raw |
| `featureStructure/makeFeatStruc`,`clear` | compound (owned container + ref pairs) | **canonical Expand-Fill primitive**; owner=None NPEs; typed-owner cast | U | **Expand-Fill** |
| MSA (grammar side): `createStem/InflAff/DerivAff/Unclassified`,`setPos`,`changeAffixVariant`,`removeOrphaned` | create/compound/sweep | `CreateInflAff` never populates `InflFeatsOA` — Expand must add `makeFeatStruc`; convert = read-back | U | Expand + raw |
| morph type, allomorph, environment (`create/delete/set…`) | create/delete/set | environment `setStringRepresentation` never parses to a context tree; delete has no referrer check | P/U | raw |
| affix template: `create`,`delete`,`setStratum`,`set…`,`duplicate` | create/delete/set/compound | — | P | raw (Frame) |
| **slot ops** `slot/{create,delete,setOptional}`, `template/addSlotToSequence` | create/sequence | **NO Flexicon precedent — design fresh**; five parallel `seq` over one unordered pool, zero cross-sequence validation | U/P×5 | raw (Frame) |
| compound rule: `create`,`delete`,`setStratum`,`set…`,`duplicate`; owned `Left/Right/ToMsa` fill | create/compound/**owned-MSA fill (fresh)** | Flexicon never populates the owned MSAs | P | raw |
| adhoc prohibition | create/delete/set | **NO create in Flexicon — design fresh**; Flexicon's dispatch strings are buggy (don't mirror) | U/P | raw |
| stratum: `create`,`delete`,`set…` | create/delete/set | **delete has 5 named dangling referrers** (`MoInflAffixTemplate`,`MoDerivAffMsa`,`MoStemMsa`,`MoCompoundRule`,`PhSegmentRule`) → delete-cascade closure | P | raw |
| phoneme set/phoneme/code: `create`,`delete`,`add/removeCode`,`set…`,`makeFeatStruc` | create/delete/sequence/set | phoneme-set lifecycle **not in Flexicon — design fresh**; `makeFeatStruc` fails open (fix to fail-closed) | U/P | raw + Expand-Fill |
| natural class: `create`,`createFeatureBased`,`delete`,`setName`,`add/removePhoneme`,`setFeatures` | create/delete/collection-ref/set | **`addPhoneme` = ADR 0001's literal Fill step, fail-closed** | U/P | **Expand** |
| phon rule + contexts: `create`(append),`delete`,`setDirection`,`wireRule`,`makeConstraint`,`duplicate`(only true insert-after) | create/**compound wireRule**/sweep | `setLeft/RightContext` disabled in Flexicon (wrong owner bug); correct owner = `PhSegRuleRHS.{Left,Right}ContextOA`; **context-leak sweep mandatory**; positional-insert-of-fresh-rule **design fresh** (model on `Duplicate`'s `.Insert(idx+1,…)`); `PhIterationContext` **unimplemented anywhere — design fresh** | **F** | **Expand** |

## Dependency ordering (merged)

`system/writingSystem` → `lists` (morph types, POS-external, publication types, semantic domains,
`LexRefType`) and `system/customField/define` (own phase) → **lexical:** entry → sense →
allomorph/etymology/pronunciation/example → MSA → relations/variants → reversal → publication flags →
compound (merge/convert). **grammar:** feature leaves → POS → (inflection class, slot pool) → templates
→ MSAs → morph types before allomorphs → strata before any `StratumRA` holder → phoneme set → phonemes
→ natural classes → environments → phon rules (feeding graph, re-derived on every insert/reorder).
Compound/graph ops are always assessed last with a fresh read-back.

## Staged roadmap

Each stage adds operations **and** the machinery they first require; each is sonnet-built, opus-reviewed,
verified against a real project, committed. Interleave the cross-cutting hardening (apply↔Assessment
binding per ADR 0004/0006; exclusive-write coordination; the 100%-coverage manifest; per-op conformance
vectors) as the operations that need them arrive.

- **L1 — Simple lexical core.** entry create/delete, sense create + setters, WS-alt/rich-string/scalar
  setters, collection-ref add/remove. *Introduces:* **create + identity mapping**, **delete-cascade
  closure**, operation-dispatch generalization. → *minimum-lexical-complete* headword+meaning+publish.
- **L2 — Owned children & sequences.** allomorph, etymology, pronunciation, example, translations,
  pictures/media. *Introduces:* **sequence ops (class P)**, **reparent**.
- **L3 — References & relations.** MSA create/set + orphan sweep, `LexReference` (floor-of-2/homogeneity),
  variants/complex-forms. *Introduces:* **per-construct validation**, **atomic multi-field writes**,
  **de-reference compensating sweep**.
- **L4 — Reversal, publication, lists.** reversal index/entries, publication types + flags, possibility
  lists/items, system-list delete guard. *Introduces:* the `lists` group.
- **L5 — System & lexical compound.** writing-system lifecycle, custom-field define + data ops,
  entry/sense merge, MSA convert. *Introduces:* **WS family**, **custom-field non-undoable phase**,
  **compound/graph machinery**.
- **G0 — Grammar prerequisites.** POS, feature system, inflection classes, strata, phoneme sets/phonemes,
  natural classes, environments. *Introduces:* the `grammar` group + its dependency chain.
- **G1 — Morphology.** grammar-side MSAs + `makeFeatStruc`, affix templates + **slot ops (design fresh)**,
  compound rules + **owned-MSA fill (design fresh)**.
- **G2 — Phonology & feeding.** phonological rules (`create`/`wireRule`/positional-insert-fresh/contexts),
  feature constraints, context-leak sweep. *Introduces:* **feeding comparison class F**, the `wireRule`
  Fill mechanism.
- **G3 — HC round-trip.** forward projection (harvest `HCLoader`) + reverse `Expand` first cut:
  natural-class + phon-rule + affix-in-slot + `makeFeatStruc` (ADR 0001's worked examples). Forward is
  the round-trip oracle.

## Where Flexicon gives no precedent (LCAtom designs from the model alone)

Slot & affix-template write ops (the five-parallel-sequence pool); phoneme-set lifecycle; adhoc
prohibitions end-to-end (Flexicon's dispatch is buggy — do not mirror); `PhIterationContext`
(Kleene-star); compound-rule owned-MSA fill; positional-insert of a *fresh* phon rule; metathesis/
reduplication fresh-create. These get extra scrutiny and their own conformance fixtures.

## Milestones

- **Minimum lexical-complete** (end of L1, partial L2/L3): author/round-trip a basic dictionary entry —
  headword, meaning, grammatical category, one example, publish/hide.
- **Lexical-complete** (through L5).
- **Grammar first-cut** (G0–G2 + G3 reverse-`Expand` worked examples): the killer workflow — author a
  natural class / phonological rule / affix via one structured command, project to HC, review via
  PanGloss, apply.
- **Grammar-complete** (all of G, including the design-fresh gaps).
