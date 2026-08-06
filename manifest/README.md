# Coverage manifest

## `liblcm-inventory.tsv` — the raw inventory (generated, checked in)

Every property of every LibLCM class, generated mechanically from
`MasterLCModel.xml` as shipped inside the pinned `SIL.LCModel` NuGet package
(`contentFiles/MasterLCModel.xml`) — so the inventory is version-locked to
exactly the LibLCM assembly Motif references, with no dependency on a sibling
repository checkout.

898 rows, 19 columns. The first nine are the raw inventory, generated mechanically; the other ten
are added by [`classify.ps1`](classify.ps1) (see "The manifest is now the type system" below) and are
blank/`n/a` for every row where `Scope != in`.

| Column | Meaning |
| --- | --- |
| `Class` | LibLCM class name |
| `Base` | its base class |
| `Abstract` | `true` if the class cannot be instantiated |
| `Scope` | `in` (domain-reachable, authorable), `trace` (derivation-trace — analyzer output, never authored), `out` |
| `ScopeReason` | why that scope was assigned |
| `Field` | property name |
| `Kind` | `basic` (value) / `owning` / `rel` (reference) |
| `Sig` | value type, or destination class for relations |
| `Card` | `atomic` / `col` / `seq` |
| `HcReferenced` | raw name-only join against [`hcloader-surface.tsv`](hcloader-surface.tsv): `name-referenced` or `no`. Precursor to `HcReachable`, which corrects the false positives a bare-name join produces (see issue D1 in [issues.md](../docs/issues.md)). |
| `Construct` | the layer-1 construct this field belongs to (`lexEntry`, `msa`, `phoneme`, `rewriteRule`, …; the possibility family fans out to a multi-construct string) — see [ADR 0009](../docs/adr/0009-layered-api-primitives-and-composers.md) and [API surface layer 1](../docs/api-surface-layer1.md). 54 distinct constructs across the in-scope rows. |
| `Group` | `grammar` / `lexical` / `system` / `lists` / `analysis` — **the editorial domain: who should review a change**, per [ADR 0024](../docs/adr/0024-group-is-derived-domain-is-editorial.md). It is *not* the first segment of an operation's name; that segment is derived from the declaring class and the two deliberately disagree on 53 rows. `analysis` was added by [ADR 0025](../docs/adr/0025-parser-first-build-order.md). |
| `Classification` | what kind of field this is for kind-generation purposes: `semantic-operation` (core authorable content), `supporting-detail` (secondary/administrative), `unsupported` (explicitly not offered as a control), `internal` (import residue, singleton roots), `derived-read-only` (engine-computed), `runner-bookkeeping` (the applied-log's reuse of `CmResource`), `observable-not-authorable` (engine-maintained reverse index). |
| `ComparisonClass` | how instances of this field compare for drift/effect purposes: `unordered`, `positional`, `index-as-identity` (alpha-variable pools — [issue B8](../docs/issues.md)), `feeding` (order encodes rule interaction — [issue B8](../docs/issues.md)). |
| `Verbs` | the CRUD verb subset this field's `(Kind, Card)` shape generates (`set|clear`, `create|delete`, `create|delete|move|reparent`, `addRef|removeRef`, `addRef|removeRef|move`), or `n/a` when `Classification` marks the field non-authorable. |
| `HcReachable` | does `HCLoader` actually read this field — `yes` / `no` / `unconfirmed`, corrected from `HcReferenced` against the curated surface map (see [the HC grammar map](../docs/hc-grammar-map.md)). |
| `AssessPoisonsCache` | `yes` for the 4 fields whose mutation poisons a derived LibLCM cache that `Rollback` cannot repair ([issue A4](../docs/issues.md), [issue C15](../docs/issues.md)); `no` otherwise. |
| `EnumValues` | for the 28 in-scope `Integer` fields only: a confirmed `value=Name;...` mapping, `unknown` (named-enum-shaped but no confirming code found), or blank (a magnitude, not an enumeration) — [issue B7](../docs/issues.md). |
| `Rationale` | required prose for every in-scope row, citing the classification decision and folding in any `ComparisonClass`/`HcReachable`/`EnumValues`/`AssessPoisonsCache` notes. Distinguishes a cited source from a generic field-name-heuristic default — see [issue B18](../docs/issues.md). |

### `Scope` is computed, not guessed

Scope is **owning-edge reachability** from the domain roots — `LexDb`, `MoMorphData`, `PhPhonData`,
`FsFeatureSystem`, `PartOfSpeech`, and `LexEntry` (the last because `LexDb` only *references* entries,
never owns them) — plus the declared `CmPossibility` subclasses that in-scope lists use, minus the
derivation-trace family. Polymorphic expansion is deliberately blocked at broad bases (`CmObject`,
`CmPossibility`, `CmPossibilityList`, …), since a list's legal item class is governed per-instance by
`ItemClsid` at runtime rather than by the schema.

A naming heuristic was wrong in **both** directions, which is why this is computed:

- **Admitted what it shouldn't:** the `MoDeriv*` / `*App` derivation-trace family (9 classes, 28 props)
  looks like grammar but is reachable only from `WfiAnalysis.Derivation`, an out-of-scope interlinear
  class. It is analyzer output. Tagged `Scope=trace`.
- **Excluded what it shouldn't:** `CmPossibility`, `CmPossibilityList`, `CmMedia`, `CmPicture`,
  `CmTranslation`, `CmResource` (the applied-log), `StText`/`StPara` — no domain prefix, but they back
  in-scope fields.

**In scope: 494 properties across 100 classes** (494 = 214 `basic` + 148 `owning` + 132 `rel`). Was 473
across 95 until [ADR 0025](../docs/adr/0025-parser-first-build-order.md) brought in 21 analysis rows —
the approval half of word analysis, which has durable identity. Occurrence assignment
(`Segment.Analyses`) remains out. Basic —
MultiUnicode 52, MultiString 51, Unicode 35, Integer 28, Boolean 25, String 10, Time 5, Guid 2,
TextPropBinary 1. Relations — owning/atomic 69, rel/atomic 57, owning/col 39, rel/col 38, owning/seq 33,
rel/seq 28. Excluded as `trace`: 28 props across 9 classes.

An earlier count of 478 properties across 96 classes included 5 `TextTag` rows later demoted as a
scope over-inclusion — see [issue D5](../docs/issues.md). The numbers above are post-D5 and were
recomputed directly from the TSV, not adjusted by hand from the old figures.

**This file is the drift-detection artifact.** Regenerating it after a LibLCM package bump must produce
no diff; any diff is a model change requiring review and (re)classification.

## The manifest is now the type system

Classification has shipped (commit `66fe792`). Every in-scope row carries all ten classification
columns — **zero in-scope rows are unclassified** — turning `liblcm-inventory.tsv` from a raw
inventory into the type system the (not-yet-written) kind generator is meant to read: per
[ADR 0009](../docs/adr/0009-layered-api-primitives-and-composers.md) and
[API surface layer 1](../docs/api-surface-layer1.md), each field's `Construct`, `Classification`,
`ComparisonClass`, `Verbs`, `HcReachable` (does `HCLoader` read it — see
[the HC grammar map](../docs/hc-grammar-map.md)), `AssessPoisonsCache`, `EnumValues` (for the 28
in-scope `Integer` fields), and `Rationale` are all populated. See the column table above for what
each one means and the value set it takes.

[`classify.ps1`](classify.ps1) is the mechanized, checked-in, re-runnable producer of these columns —
rerun it after any inventory regeneration rather than hand-editing the TSV.

### What genuinely still remains

- **No kind generator yet.** The manifest is the type system a generator would read, but nothing in
  `src/` reads `Construct`/`Classification`/`Verbs` to emit operation kinds. Today's runner still
  implements exactly one hand-written operation, `lexical/lexSense/setGloss`.
- **`manifest/generate-inventory.ps1` does not exist.** The raw first-nine-column inventory was
  produced by ad-hoc commands rather than a committed script, so the drift gate this file's opening
  section promises ("regenerating it after a LibLCM package bump must produce no diff") cannot
  actually be re-run yet. Tracked as [issue D6](../docs/issues.md).
- **Confidence caveats.** `HcReachable` is `unconfirmed` for 7 in-scope rows pending an HCLoader-read
  citation ([issue B17](../docs/issues.md) calls out `MoMorphSynAnalysis.Components`/`GlossBundle`
  specifically). Most rows carry no citation — **but this stopped being a risk on 2026-08-05.** The
  generator no longer trusts these columns: `Verbs` and `ComparisonClass` are **derived** from LibLCM's own
  structural declarations with five cited exceptions, and the build fails on any unexplained departure
  ([ADR 0022](../docs/adr/0022-structure-is-derived-policy-is-five-rows.md)); the operation name is derived
  from the declaring class ([ADR 0023](../docs/adr/0023-derived-kind-names-required-descriptions.md)). A
  missing citation on a computed value is not a defect. What still rests on human judgement, and where a
  citation therefore earns its place: `Scope`, `Construct` (what ships together), `Group` (who reviews), and
  the five order-carries-meaning rows.
