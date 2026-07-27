# ADR 0012 — Build order: the HC-reachable spine first, kinds generated from day one

Status: accepted (2026-07-27)

Amends [ADR 0010](0010-hermitcrab-experimentation-is-the-primary-purpose.md)'s build-order claim.
Sets the timing for [ADR 0009](0009-layered-api-primitives-and-composers.md) §3's generated kind
namespace. Reorders the staged roadmap in
[operation-catalog-plan.md](../operation-catalog-plan.md).

## Context

Two questions were open before thickening the walking skeleton past its single operation: **what
order**, and **hand-written or generated**.

ADR 0010 answered the first — *"the lexical surface stays in scope and first in the build order …
lexical completeness is a prerequisite for grammar experimentation, not a parallel track"* — and the
operation catalog encoded it as L1–L5 before G0–G3.

That claim was made before the coverage manifest was classified. Now that it is, the manifest
contradicts it.

## Evidence

Measured from `manifest/liblcm-inventory.tsv` (473 in-scope rows, 100% classified):

| | count |
| --- | --- |
| In-scope fields `HCLoader` actually reads (`HcReachable=yes`) | **150** |
| — of those, `grammar` | **113** |
| — `lexical` | **32** |
| — `lists` | **4** |
| — `system` | **1** |
| `LexEntry` fields read, of 23 in scope | **5** |
| Distinct `(Kind, Card, Sig)` shapes across all 150 | **12** |
| — shapes present in the 37 non-grammar fields | **10** |
| — shapes grammar adds | **2** (`basic/Integer`, `basic/String`) |
| Classes touched by the 150 | **70** |
| Generated kinds for the 150 | **332** |
| Generated kinds for all 412 authorable in-scope rows | **915** |

The non-grammar surface HermitCrab touches is 37 fields, concentrated in `allomorph` (10) and
`lexSense` (3 — `Gloss`, `MorphoSyntaxAnalysis`, `Senses`). L4 as specified would deliver 3 reversal
fields and **zero** publication fields to the parser; L5 delivers about one. Most of L1–L5 feeds a
parser that never reads it.

## Decision

### 1. Build the HC-reachable spine (L0), then grammar, then backfill

```
L0   entry skeleton, allomorph, morphType, MSA link, sense gloss     ~37 fields
      ↓
G0   POS, features, strata, phonemes, natural classes, environments
      ↓
G1   grammar-side MSAs, affix templates, slots, compound rules
      ↓
G2   phonological rules, contexts, feeding order
      ↓
     the loop closes — export .fwdata → PanGloss → attachments back
      ↓
L1–L5  backfilled on demand, driven by non-HC consumers
```

**L0 is defined by query, not by taste:** the in-scope rows where `HcReachable = yes` and
`Group != grammar`, plus whatever object-creation closure LibLCM forces on them.

That closure is **not yet known** and is the first piece of work: a field being *read* by `HCLoader`
does not mean an object can be *created* with only those fields set. LibLCM factories and model
invariants will require fields the parser never reads, and the manifest carries no `required` column
to derive them from. L0's true field list is the HC-reachable 37 plus that closure — compute it
before committing to a scope.

ADR 0010's "lexical completeness is a prerequisite" is amended to: **a thin lexical spine is a
prerequisite.** The rest of the lexical catalog remains fully in scope and fully intended — it is
simply not on the critical path to the primary purpose, and it serves the non-HC consumers
(Flexicon, FlexToolsMCP, GramTrans, Linguistic Assistant) whose needs should sequence it.

**Honest cost:** L0 pulls `allomorph` forward from L2, so sequence operations and `reparent` arrive
earlier than the catalog planned. L0 is roughly L1 plus the HC-reachable half of L2 and L3, not a
cheap subset of L1.

### 2. Kinds are generated from the manifest, from day one

[ADR 0009](0009-layered-api-primitives-and-composers.md) §3 already decided generated kinds with
handler dispatch. This sets the timing: **the generator is built before L0, not retrofitted after.**

The ratio decides it. **332 kinds against 12 handlers** for the HC target; **915 against 12** for the
full surface. Kinds are data — regenerated whenever the manifest moves. Handlers are code — one per
`(Kind, Card, Sig)` shape, written by hand, twelve of them. Hand-maintaining hundreds of kind
registrations is precisely the thing that rots, and building L0 by hand would mean writing its kinds
twice.

`lexical/sense/setGloss` stops being a special case and becomes the first *generated* kind. Its
manifest row is the generator's Rosetta stone:

```
LexSense.Gloss │ Kind=basic │ Sig=MultiUnicode │ Construct=lexSense
               │ Group=lexical │ Verbs=set|clear
                              ↓
                    lexical/sense/setGloss
```

Because L0 covers 10 of the 12 shapes, building L0 *is* building the handler set; grammar adds only
`basic/Integer` and `basic/String`.

**Two prerequisites before the generator can run unattended**, both small, both latent defects
regardless:

1. **Construct-to-kind-segment naming is not mechanical.** The row above has `Construct=lexSense`
   but the shipped kind segment is `sense`. Either normalize the manifest's construct names or
   commit a mapping.
2. **17 authorable rows carry a multi-construct string** (`possibility|partOfSpeech|lexRefType|…`)
   with no single kind namespace. They need a resolution rule — most likely fan-out to one kind per
   construct — before generation is unambiguous.

## Consequences

- The staged roadmap in [operation-catalog-plan.md](../operation-catalog-plan.md) is reordered:
  L0 → G0 → G1 → G2, with L1–L5 backfilled.
- "Minimum lexical-complete" is no longer the first milestone. The first milestone is **the loop
  closing**: author a grammar change, export, parse externally, attach the report.
- The manifest becomes a build-time input to compilation, not just a review artifact and a CI gate.
  A manifest change becomes a code change.
- Nothing about coverage scope changes. Full coverage remains "C# `HCLoader` complete" (ADR 0010),
  and LibLCM's surface must still be 100% classified for write safety. Only the *order* moved.

## What this does not change

The lexical catalog is not descoped, deprioritized in principle, or made optional — it is
resequenced. Non-HC consumers remain first-class, and their requirements are the right driver for
when L1–L5 land.

[Issue B18](../issues.md) — that roughly 300 of the 473 in-scope rows were classified by field-name
heuristic rather than an explicit citation — is **not resolved by this ADR** and becomes materially
more load-bearing under it, because generation now reads those classifications directly. Whether to
verify them lazily per operation or in a dedicated audit pass remains open.
