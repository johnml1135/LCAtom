# ADR 0025 — Build the parsing slice first, including grammar and approved analyses

**Status:** accepted, 2026-08-05. Supersedes [ADR 0012](0012-build-order-hc-spine-first-kinds-generated.md)'s
build order (its generation decisions stand). Amends [ADR 0017](0017-text-and-analysis-destination-scope.md):
the approval half of analysis comes into v1. Answers `B8a` and closes `B21`.

## Context

ADR 0012 ordered the work as **L0 → G0–G2 → backfill**: first the parser-relevant fields that are *not*
grammar (a manifest query yielding 37 fields), then grammar in three stages, then the remaining lexical
volume. The reasoning was walk-before-run.

**Two things are wrong with that, and the second is the decisive one.**

**The query is unsound.** Of its 37 fields, **13 are never read by `HCLoader`** — the list was built by
searching the parser loader's source for field names, and some matches were coincidences: `MoMorphType.Prefix`
matched `MoMorphTypeTags.kguidMorphPrefix`, and `StText.RightToLeft` matched HermitCrab's own
`Direction.RightToLeft` enum. That is the exact bare-name false-positive class the `HcReachable` column was
introduced to correct. The confirmed set is **24 fields across 10 classes**. Additionally, populating those 24
requires creating objects of five *grammar* classes (`PhEnvironment`, `MoInflClass`, `PartOfSpeech`,
`MoInflAffixSlot`, `FsFeatStruc`), so "non-grammar first" was never an achievable boundary.

**And the owner's framing replaces the cut entirely:**

> *We want all of liblcm that we scoped, while the first most important slice are things that can be used for
> parsing, both creating the rules, the lexemes, and analyzing texts.*

That is not "non-grammar first." It is "everything the parser touches, plus the material you judge it
against" — grammar included, by design, because grammar is where the value is.

Measured against the manifest:

| | Fields |
| --- | ---: |
| **Read by the parser loader** | **150** |
| — grammar (the rules) | 113 |
| — lexical (the lexemes) | 32 |
| — other | 5 |
| Not read by the parser | 323 |
| Text and analysis | 48, all currently `Scope=out` |

## Decision

### 1. The build order is parser-first, in one slice

Replace L0 → G0–G2 → backfill with:

```
slice 1   the 150 parser-read fields (113 grammar + 32 lexical + 5)
          + the analysis fields that carry a human judgement
slice 2   the 323 fields no parser reads — bibliographies, pictures,
          pronunciations, publication settings: real dictionary work,
          and none of it blocks a parse
```

Everything scoped is still destined to be built. What changes is that **grammar is not deferred behind a
lexical warm-up**, because the warm-up's boundary did not exist.

### 2. `HcReachable` is the authority; the 13 phantom fields are dropped from the slice

They are not removed from scope — they are ordinary fields that no parser reads, so they belong to slice 2.
The unsound query is replaced by the column built to answer this question.

### 3. Analysis comes in; occurrence assignment stays out

This is the line, and it falls exactly where identity is durable:

| Comes in now | Stays out |
| --- | --- |
| `WfiWordform.Form`, `.Analyses` (a **collection** — no order) | `Segment.Analyses` (a **sequence** — position in a sentence) |
| `WfiAnalysis.Category`, `.MorphBundles`, `.MsFeatures`, `.Meanings` | `Segment.BeginOffset` and the other text-structure fields |
| `WfiAnalysis.Evaluations` — **the approval itself** | `WfiWordform.Checksum`, `.SpellingStatus` — engine state |
| `WfiMorphBundle.Form`, `.Morph`, `.Msa`, `.Sense`, `.InflType` | |
| `CmAgent.Human`, `.Name`, `.Approves`, `.Disapproves` | |

**Why the line is here and not somewhere else.** A wordform, its analyses, and who approved them all hang off
GUID-bearing objects in unordered collections — durable, addressable, unaffected by editing a sentence. *Which
word position in which sentence uses an analysis* is a sequence index into a `Segment`, and that breaks when
the text is edited. So:

- **"this analysis is human-approved"** → available now. This is the test suite.
- **"every word in this text has an analysis"** → needs anchoring the model does not provide. This is the
  coverage metric, and it remains a research track.

`WfiAnalysis.Stems`, `.Derivation`, `.CompoundRuleApps` and `.InflTemplateApps` record *which rules the parser
applied*. They are brought in-scope but must be classified before any kind is generated for them; several are
expected to land read-only, since they are engine output rather than human intent.

### 4. What the slice is for, stated as the acceptance test

An agent creates a grammar rule and a lexeme, a human-approved analysis exists for a word, and PanGloss is
asked whether it can produce that analysis. **The answer is mechanically decidable** — FieldWorks already
computes it as `NumUserApprovedAnalysesMissing`. That is a failing test in the ordinary sense, and it is the
first point at which Motif tells a linguist something they could not previously see.

## Consequences

- **48 text and analysis rows need scoping and classification.** Flipping `Scope` is mechanical; deciding
  `Construct` and reviewing the read-only calls is real work, and it is the first genuine addition this
  decision makes.
- **`B21` is closed** (L0's creation closure is computed: 4 classes for a minimal entry, 5 grammar classes to
  populate the confirmed 24) and **`B8a` is answered** — re-scope rather than patch, because the query is
  being retired rather than corrected.
- **ADR 0017's "text and analysis are staged out of v1" is now half-true and should be read through this
  ADR.** Its reasoning was right and its conclusion was drawn before the correction establishing that Motif
  addresses a `Segment` rather than an occurrence. Approval was always the durable half; this ADR acts on that.
- **ADR 0012's generation decisions are untouched** — kinds generated from the manifest from day one, and the
  grammar-leads observation (113 of 150 parser-read fields are grammar) is *reinforced*: it is the argument for
  this ordering rather than against it.
- **The 323 deferred fields are a promise, not a discard.** Everything scoped gets built; the dictionary
  apparatus simply cannot block a parse, so it goes second.
- **Risk accepted:** slice 1 is roughly 170 fields against the single shipped operation, so it is a large first
  bite. The mitigation is that generation makes volume cheap and the acceptance test is reachable long before
  all 170 exist — one rule, one lexeme and one approved analysis is enough to prove the loop.
