# FieldWorks label harvest

Built by `spikes/SIL.Motif.Spikes.LabelHarvest` per
[ADR 0023](../docs/adr/0023-derived-kind-names-required-descriptions.md) decision 5, implementing what
[the research note](../docs/research/2026-08-05-fieldworks-user-facing-names.md) found. This document does
not redo that investigation — it reports what the tool actually produced when run against it.

## Sources read

All three paths the research note cited were verified to exist, unmoved, against
`C:\Users\johnm\Documents\repos\FieldWorks` (checked out at the time of this run):

| # | Path | Role |
| --- | --- | --- |
| 1 | `DistFiles/Language Explorer/Configuration/strings-en.xml` | Class names (`ClassNames`), possibility-list "kind" names (`PossibilityListItemTypeNames`), plural/view titles (`AlternativeTitles`) |
| 2 | `DistFiles/Language Explorer/Configuration/Parts/*.fwlayout` (8 files) | Per-layout field labels: `<layout class="C"><part ref="F" label="L"/></layout>` |
| 2 | `DistFiles/Language Explorer/Configuration/Parts/*Parts.xml` (8 files) | The canonical per-(class,field) registry: `<bin class="C"><part id="…"><slice field="F" label="L" tooltip="T"/></part></bin>` |
| 3 | `DistFiles/Language Explorer/Configuration/Lists/areaConfiguration.xml` | `(tool, className, ownerClass, ownerField)` |
| 3 | `DistFiles/Language Explorer/Configuration/Lists/Edit/toolConfiguration.xml` | `(tool, label)` |

None had moved. No fourth source was added.

## What the tool does that a literal read of the three files does not

Two indirections turned out to matter enough to handle explicitly, both discovered while building this,
not anticipated from the research note alone:

- **A `.fwlayout` `ref=` is not always a bare field name.** `MoInflAffixSlot`'s `EditSlot` layout labels
  `ref="NameAllA"` as "Slot Name" — `NameAllA` is a composite `Parts.xml` part id suffix
  (`MoInflAffixSlot-Detail-NameAllA`, which wraps `<slice field="Name">`), not a field called `NameAllA`.
  `PartIdFieldResolver` builds a `(class, id-suffix) -> field` table from every `Parts/*.xml` part id (all
  1,210 multi-hyphen ids sampled split cleanly into exactly three segments: `Class-Type-Suffix`) and resolves
  through it before falling back to the literal `ref`. Without this, the harvest would have missed the
  exact `MoInflAffixSlot.Name` "Name" vs. "Slot Name" conflict ADR 0023 itself cites as the demonstration
  that labels are per-view — the naive scrape records two unrelated pseudo-fields instead of one field with
  two labels.
- **`CellarParts.xml` has a real typo:** `<bin class="CmSemanticDomain>">` — a stray `>` baked into the
  attribute value. Trimmed defensively so the row still lands on `CmSemanticDomain` instead of silently
  losing that class's coverage to an upstream data bug.

## Raw counts

- **2,280** raw label facts harvested before de-duplication.
- **768** output rows in `fieldworks-labels.tsv` after merging repeated `(class, field, label)` sightings
  into one row each (per the "one row per distinct label" rule).
  - By `Source`: `slice`=627, `strings-en`=113, `tool-config`=26, `strings-en+tool-config`=2 (the two
    mechanisms independently agreed on the same label).
  - By `Confidence`: `exact`=516, `class-only`=40, `ambiguous`=212.

## Coverage against the 494 in-scope rows

The research note estimated **"roughly a third to under half."** The measured number:

| | rows | % of 494 |
| --- | --- | --- |
| Field-level label, unambiguous (`exact`) | 177 | 35.8% |
| Field-level label, but disagreeing across views (`ambiguous`) | 16 | 3.2% |
| **Field-level label, any** | **193** | **39.1%** |
| Class-only label only (no field-level hit) | 128 | 25.9% |
| No label of any kind | 173 | 35.0% |

**39.1% lands inside the estimated range, close to its midpoint** — neither the "closer to a third" nor
"closer to under half" end. Adding the 128 rows that get at least a class-level name (useful context for a
description even without a field-specific label) covers **321 of 494 (65.0%)** in some form; **173 rows
(35.0%) get nothing from any of the three mechanisms** and still need a hand-written description from
scratch.

Coverage by manifest `Classification` (field-level, any):

| Classification | covered / total | % |
| --- | --- | --- |
| `supporting-detail` | 48 / 107 | 44.9% |
| `semantic-operation` | 140 / 322 | 43.5% |
| `derived-read-only` | 3 / 14 | 21.4% |
| `internal` | 2 / 16 | 12.5% |
| `unsupported` | 0 / 30 | 0.0% |
| `observable-not-authorable` | 0 / 2 | 0.0% |
| `runner-bookkeeping` | 0 / 3 | 0.0% |

This confirms the research note's shape: coverage concentrates in `semantic-operation` and
`supporting-detail` (the two buckets that touch actual linguistic content), and is essentially absent from
the bookkeeping/internal/unsupported buckets FieldWorks' own UI never exposes a control for.

Of the 100 distinct in-scope classes, **34 get no label of any kind** — most of them the rule-context family
the research note flagged (`PhPhonContext`, `PhSimpleContextSeg`/`Bdry`/`NC`, `PhSequenceContext`,
`PhIterationContext`, `PhFeatureConstraint`, `PhPhonRuleFeat`) plus container/infrastructure classes
(`LangProject`, `LexDb`, `CmAgent`, `CmResource`, `CmPossibilityList`, `MoMorphData`, `MoStratum`,
`FsFeatureSystem`, `WfiWordSet`, `StPara`, and a few more).

## Ambiguity: 16 in-scope (class, field) pairs carry disagreeing labels

Every one of these has at least two distinct labels found across different views, and the tool records all
of them rather than picking one:

`CmPossibility.Description`, `CmPossibility.Name`, `FsFeatDefn.ShowInGloss`, `FsFeatStruc.FeatureSpecs`,
`FsSymFeatVal.Name`, `LexEntry.CitationForm`, `LexEntry.EntryRefs`, `LexEntryRef.ComponentLexemes`,
`LexPronunciation.CVPattern`, `LexSense.ScientificName`, `MoAdhocProhibGr.Description`,
`MoAdhocProhibGr.Name`, `MoInflAffixSlot.Name`, `MoInflAffixTemplate.Name`, `MoInflClass.Name`,
`MoStemName.Name`.

A further **38 rows are `CmPossibility` class-only labels, and every one of them is `ambiguous`** —
"Confidence Level", "Restriction", "Status", "Position", "Academic Domain", … — which is not scraper noise
but exactly ADR 0023's own finding: the "kind" name for a bare `CmPossibility` object is a runtime fact
about which list owns it, not a class fact, so *of course* every list's tool/strings-en label disagrees with
every other list's. The harvest reproduces that finding as data instead of asserting it. (101 class-level rows across
all classes are flagged `ambiguous` in total; the other 63 belong to classes whose `ClassNames` label
disagrees with a label found some other way — e.g. `LexRefType` is "Lexical Relation" in `strings-en.xml`
but "Lexical Relations" — plural — in its tool label.)

## The most useful examples found

- `LexSense.Gloss` → **"Gloss"**, with tooltip *"Short translation equivalent for this lexeme. Used in
  interlinear texts, and in the dictionary article when Definition is empty."* — a clean, single, reviewed
  label plus a genuinely usable description sentence, no ambiguity.
- `LexEntry.LexemeForm` → **"Lexeme Form"**, single label, no ambiguity — about as good as this vocabulary
  gets.
- `MoInflAffixSlot.Name` → **"Name"** (generic layout) vs. **"Slot Name"** (the `EditSlot` layout) — the
  exact case ADR 0023 cites; both are recorded, `ambiguous`.
- `LexEntry.CitationForm` → **"Citation Form"** (the entry-editing layout) vs. **"Lexeme"** (the field's own
  default `Parts.xml` slice, which also carries the tooltip *"The Citation Form is used to override the
  Lexeme Form as the headword in the printed dictionary."*) — a real, previously-undocumented instance of
  the same per-view conflict, found only because of the `ref=` resolution work above.
- `LexEtymology.Gloss` vs. `LexSense.Gloss` — both literally `ref="Gloss"`/`field="Gloss"` in the source
  files, correctly kept as two separate rows because each is keyed by its enclosing class, not the bare ref
  string. This was the specific naive-scrape failure mode the research note warned about; the harvester
  keys on `(layout-class, field)` throughout and does not exhibit it.

## What's missing, plainly

- `PhSegRuleRHS.StrucChange`, `.LeftContext`, `.RightContext` — **zero rows**, confirmed absent, exactly as
  the research note predicted. Their siblings on the same class (`ExclRuleFeats`, `InputPOSes`,
  `ReqRuleFeats`) *are* labeled ("Excluded Properties", "Required Categories", "Required Properties"), so
  this is not a file-reading gap — FieldWorks genuinely renders these three fields with a bespoke
  rule-formula control that carries no label string anywhere in the config tree.
- The entire `PhPhonContext`/`PhSimpleContext*`/`PhSequenceContext`/`PhIterationContext` family: no labels at
  all, for the same reason.
- Container/infrastructure classes (`LangProject`, `LexDb`, `CmAgent`, `CmResource`, `MoMorphData`,
  `MoStratum`, `FsFeatureSystem`) never appear as the *subject* of a slice or class name — they only ever
  show up as the `ownerClass` of some other list, so they get no label of their own from any of the three
  mechanisms.
- **35% of in-scope rows (173) get nothing.** The remaining hand-written work is real, not a rounding error.

## A note on vocabulary fit

The harvested vocabulary is strong exactly where the manifest's `semantic-operation` bucket needs it —
lexicon and grammar content fields reviewed and phrased for linguists — and silent exactly where the
generated/internal/bookkeeping fields live, which is the right shape for a *seed*, not a concern. The one
real fitness problem the harvest surfaces on its own: FieldWorks' own vocabulary disagrees with itself
constantly (212 of 768 rows, 27.6%, are flagged `ambiguous`), so this data cannot be substituted for
descriptions mechanically — a human still has to pick, per ADR 0023's decision 5 design.

## Output

`manifest/fieldworks-labels.tsv` — tab-separated, every field double-quoted, CRLF line endings, matching
`manifest/liblcm-inventory.tsv`'s dialect exactly. Columns: `Class`, `Field`, `Label`, `Tooltip`, `Source`,
`SourceDetail`, `Confidence`.

Regenerate with:

```
dotnet run --project spikes/SIL.Motif.Spikes.LabelHarvest -- <FieldWorks checkout root> manifest/liblcm-inventory.tsv manifest/fieldworks-labels.tsv
```
