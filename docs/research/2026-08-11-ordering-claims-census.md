# Which "order carries meaning" claims can be checked, and which 32 still need a person?

The [manifest trust audit](2026-08-03-manifest-trust-audit.md) of 2026-08-03 ended with a recommendation:
*"100%-review the 61 non-default rows before the generator ships from them; spot-audit the other 412. That is
a bounded, one-sitting task — not a re-audit of the manifest."* This is the half of that a machine can do.
**64 in-scope rows claim order carries meaning; 32 of them have an explicit statement about ordering in
`MasterLCModel.xml` that nobody had ever cited, and 32 rest on `card=seq` alone.** The 32 with evidence are
now quoted, cited and digested in [`manifest/ordering-evidence.tsv`](../../manifest/ordering-evidence.tsv).
The other 32 are listed in §3 and are the review.

Nothing here changes a classification. `ComparisonClass` stays derived from `Card` with a closed exception
table ([ADR 0022](../adr/0022-structure-is-derived-policy-is-five-rows.md) decision 2); this is evidence
about whether that derivation is right per row, which is the one question the derivation cannot answer about
itself.

## 1. First, a correction to the register

`docs/issues.md` **B17 and B18 are both marked `fixed`**, and `HANDOFF.md` was still listing them as one of
the two genuinely open questions. That bullet was two weeks stale; it has been corrected. B17 was closed
2026-08-03 (the guidance it reported as absent is at `MasterLCModel.xml:3578-3584`) and B18 was largely
retired 2026-08-05 by ADR 0022, which made the columns the generator reads *derived*, so a missing citation
on a computed value stopped being a risk.

What B18's own closing note said survives is: *"citations for the five order-carries-meaning rows and for
`Scope` decisions."* This document measures the first half. (The count in that note is also stale: the
exception table has held **seven** rows since the ADR was amended, and the surrounding claim covers 64.)

## 2. The census

| | Count |
| --- | ---: |
| In-scope rows claiming order carries meaning | **64** |
| — `positional` (`card=seq`, the derived default) | 59 |
| — `index-as-identity` (cited exception) | 3 |
| — `feeding` (cited exception) | 2 |
| Of those 64, carrying **any** citation in their manifest `Rationale` before this pass | **1** |
| Now carrying a quoted, cited, digested ordering statement from the model | **32** |
| Still resting on `card=seq` alone | **32** |

The harvested statements are not paraphrases. A representative sample, each with its own line citation in the
TSV:

- `MoInflAffixTemplate.PrefixSlots` — *"The order is from the innermost affix out."*
- `PhPhonData.PhonRules` — *"They are given in the order in which they are to be applied."*
- `MoMorphData.Strata` — *"owns a col of MoStratum objects, in order from shallowest to deepest."*
- `MoInflAffixTemplate.Slots` — *"during the course of a derivation, each Affix Slot is applied in sequence,
  beginning with the top-most slot."*
- `FsFeatStrucType.Features` — *"The order of features will be used in the Gloss Assistant system to put
  feature values in the correct glossing order."*

Each row also records **which ordering words the selection matched**, so the filter is auditable rather than
magic, and a `sha256:` digest of the statement, so a reworded upstream sentence is reported rather than
silently replacing the one a reviewer read. That last property is what makes this durable: an ordering
statement is exactly the kind of sentence that gets edited without ceremony, and a wrong `ComparisonClass`
fails silently in both directions — the mechanical diff either reports a harmless reordering as meaningful,
or misses one that was.

## 3. The 32 that need a person — split by whether HermitCrab reads them

These have no ordering statement in the model. That is not evidence against `positional`; it means the
classification rests on `card=seq` and nothing else, which is what the audit flagged as the risky stratum
(it sampled 22 of them and found ~23% wrong or incomplete).

**The split that matters is `HcReachable`.** Motif is built to feed HermitCrab, so for a field HC reads, the
authority on "does order matter" is what HC does with it — not the model comment, and not the FieldWorks UI.
For a field HC never reads, the ordering claim is about FieldWorks' own presentation, which is a real but
different and much lower-stakes question: it affects how Motif reports a diff to a lexicographer, not what
the parser produces.

### 3a. `HcReachable=yes` — 14 rows, where HC is the authority

| Class.Field | ComparisonClass |
| --- | --- |
| `CmPossibility.SubPossibilities` | positional |
| `LexEntry.AlternateForms` | **feeding** |
| `LexEntry.Senses`, `LexSense.Senses` | positional |
| `MoAlloAdhocProhib.RestOfAllos`, `MoMorphAdhocProhib.RestOfMorphs` | positional |
| `PhPhonData.Environments`, `.NaturalClasses`, `.PhonemeSets` | positional |
| `PhRegularRule.RightHandSides` | positional |
| `PhSegRuleRHS.LeftContext`, `.RightContext`, `.StrucChange` | **index-as-identity** |
| `ReversalIndexEntry.Senses` | positional |

These are the rows worth spending verification on, and they are being verified against `HCLoader.cs` and
HermitCrab's own source rather than against prose. Note that `ReversalIndexEntry.Senses` being marked
`HcReachable=yes` is itself worth a second look — a reversal index is a dictionary-output structure, and if
HC does not in fact read it, the flag is wrong rather than the ordering.

### 3b. `HcReachable=no` — 18 rows, where the question is about FieldWorks, not the parser

| Class.Field | ComparisonClass |
| --- | --- |
| `CmPossibilityList.Possibilities` | positional |
| `LexEntry.DialectLabels`, `.Etymology`, `.MainEntriesOrSenses` | positional |
| `LexEntryRef.ComplexEntryTypes`, `LexEtymology.Language` | positional |
| `LexExtendedNote.Examples`, `LexPronunciation.MediaFiles`, `LexReference.Targets` | positional |
| `LexSense.DialectLabels`, `.Examples`, `.ExtendedNote`, `.Pictures` | positional |
| `MoGlossItem.GlossItems` | positional |
| `ReversalIndexEntry.Subentries`, `StText.Paragraphs` | positional |
| `WfiAnalysis.InflTemplateApps`, `.MorphBundles` | positional |

Most of this table is right for reasons no model comment will ever state, because they are too obvious to
write down: paragraphs are a document, a list's items are shown in the order a linguist set them, and a
sense's examples are ordered by the person who wrote them. A reviewer should clear most of it quickly; the
value of the list is that it says exactly where to stop.

Three of them are worth more than a glance, and none is an ordering question:

- `LexEntry.MainEntriesOrSenses` and `WfiAnalysis.InflTemplateApps` are `derived-read-only`, and
  `MoGlossItem.GlossItems` is `unsupported` — all three carry a `ComparisonClass` that no verb can invoke.
  That is dead metadata or a missing schema rule, exactly as the 2026-08-03 audit said.
- `WfiAnalysis.MorphBundles` is `positional` and is genuinely load-bearing, but for analysis comparison
  rather than for HC: ADR 0027's bundle-by-bundle comparison is what makes two stored analyses equal, and
  that comparison is positional by construction.

## 4. The rows the audit named, and what is verified now

The audit's §3 named five genuine problems. One of them touches the generator's own exception table, so it
was checked against the FieldWorks source this pass:

- **`LexEntry.AlternateForms` → `feeding`.** The manifest's rationale says the parser tries alternates in the
  order listed and stops at the first match. `HCLoader.cs` iterates `AlternateFormsOS` in exactly two places
  — line 263, which *collects* forms into allomorph lists, and line 522, which *detects* whether a prefix and
  a suffix are both present for a circumfix. Neither depends on order encoding disjunctive blocking. And the
  mechanism the rationale cites is marked unimplemented in the same file: `// TODO: irregularly inflected
  forms should be handled by rule blocking in HC` (line 744). **The cited justification does not hold.**
  Whether the correct value is `positional` depends on whether HermitCrab's own allomorph selection is
  first-match-wins, which is a question about the HC engine rather than about `HCLoader`, and was not settled
  here. Flagged, not changed — reclassifying on half the evidence is the failure this repository keeps
  hitting.
- `MoAlloAdhocProhib.Allomorphs` — the model says order matters *only when* the sibling
  `MoAdhocProhib.Adjacency ≠ "Anywhere"`. Its statement is harvested (it is one of the 32 with evidence), so
  the conditionality is now visible in the manifest rather than only in the audit. **A flat per-field label
  still cannot express a condition on a sibling field**, which is a schema question, not a data fix.
- `MoAlloAdhocProhib.RestOfAllos` — presumed to inherit the same conditionality; no model statement.
- `LexEntry.MainEntriesOrSenses` and `MoGlossItem.GlossItems` — carry a `ComparisonClass` with `Verbs=n/a`.
  Dead metadata or a missing schema rule; both are in the 32.

## 5. What this does not do

It does not review the 412 mechanical-default (`unordered`) rows — the audit found 0 errors in 13 checked and
rates the generating rule trivial. It does not touch `Scope`/`ScopeReason`, the other half of what B18 says
survives: all 495 in-scope rows carry one of two boilerplate reasons, and whether *that* deserves the same
treatment is a separate question. And it does not decide any of the 32 — deciding is what a person is for
here, and the point of the exercise was to make that a bounded reading task instead of an investigation.
