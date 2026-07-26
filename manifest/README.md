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
| `Scope` | `in` for the lexical/grammar surface (`Lex*`, `Mo*`, `Ph*`, `Fs*`, `Reversal*`, `PartOfSpeech`, `LangProject` — 102 classes, 442 props); `out` otherwise (scripture, interlinear, notebook, discourse charts, publication layout) |
| `Field` | property name |
| `Kind` | `basic` (value) / `owning` / `rel` (reference) |
| `Sig` | value type, or destination class for relations |
| `Card` | `atomic` / `col` / `seq` |

Distribution — basic: Integer 106, MultiUnicode 76, Unicode 74, Boolean 66,
MultiString 64, String 30, Time 17, Guid 5, GenDate 4, TextPropBinary 2,
Binary 1. Relations: rel/atomic 117, owning/atomic 111, owning/col 66,
rel/col 63, owning/seq 58, rel/seq 38.

**This file is the drift-detection artifact.** Regenerating it after a LibLCM
package bump must produce no diff; any diff is a model change requiring review
and (re)classification.

## What is still to come

Classification (per [ADR 0009](../docs/adr/0009-layered-api-primitives-and-composers.md)):
each in-scope field gains a `classification`, `comparisonClass`, `construct`,
`hcReachable` (does `HCLoader` read it — see [the HC grammar map](../docs/hc-grammar-map.md)),
`assessPoisonsCache`, and a required `rationale`. Classification is enforced
per-operation — CI fails if an operation touches an unclassified field — with
100% classification as the v1.0 release gate.
