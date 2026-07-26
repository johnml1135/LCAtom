# Coverage manifest

## `liblcm-inventory.tsv` — the raw inventory (generated, checked in)

Every property of every LibLCM class, generated mechanically from
`MasterLCModel.xml` as shipped inside the pinned `SIL.LCModel` NuGet package
(`contentFiles/MasterLCModel.xml`) — so the inventory is version-locked to
exactly the LibLCM assembly LCAtom references, with no dependency on a sibling
repository checkout.

898 rows. Columns:

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

**In scope: 478 properties across 96 classes.** Basic — MultiUnicode 52, MultiString 51, Unicode 35,
Integer 30, Boolean 25, String 10, Time 5, Guid 2, TextPropBinary 1. Relations — owning/atomic 69,
rel/atomic 60, owning/col 39, rel/col 38, owning/seq 33, rel/seq 28. Excluded as `trace`: 28 props
across 9 classes.

**This file is the drift-detection artifact.** Regenerating it after a LibLCM package bump must produce
no diff; any diff is a model change requiring review and (re)classification.

## What is still to come

Classification (per [ADR 0009](../docs/adr/0009-layered-api-primitives-and-composers.md), grouped per
[API surface layer 1](../docs/api-surface-layer1.md)): each in-scope field gains a `construct`,
`classification`, `comparisonClass`, `hcReachable` (does `HCLoader` read it — see
[the HC grammar map](../docs/hc-grammar-map.md)), `assessPoisonsCache`, an `enumValues` column for the
30 in-scope `Integer` fields that are closed enumerations, and a required `rationale`. Classification is
enforced per-operation — CI fails if an operation touches an unclassified field — with 100%
classification as the v1.0 release gate.
