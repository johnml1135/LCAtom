# API surface, the HC half — the grammar-facing surface

The reconciled output of two independent reviews of `manifest/hcloader-surface.tsv` — one grouping HC
constructs by **semantic authoring intent**, one analysing **structural shape and totality**. Companion
to [API surface layer 1](api-surface-layer1.md) (the LibLCM half) and grounded in the field-level
[HC grammar map](hc-grammar-map.md). Coverage target is **T2, "C# `HCLoader` complete"**
([ADR 0010](adr/0010-hermitcrab-experimentation-is-the-primary-purpose.md)), assuming PanGloss reaches
parity — so metathesis, reduplication, circumfixes, and clitic-as-affix are all in scope.

## The surface, as data

`manifest/hcloader-surface.tsv` — extracted from all 2,837 lines of `HCLoader.cs`: **235 `(loader
method, LibLCM field)` reads across 105 distinct fields**, each row carrying its source line.
`manifest/liblcm-inventory.tsv` now carries the join: **of 478 in-scope properties, 152 are
name-referenced by `HCLoader` and 326 are never referenced at all.**

The negative direction is precise — a field whose name never appears anywhere in the loader cannot be
read, so **those 326 are a machine-checked set of changes that provably cannot affect a parse.** The
positive 152 is a deliberate over-approximation: the extractor matches field *names* and cannot resolve
which class an accessor was invoked on. Per-class precision comes from the curated map. Two known
extractor blind spots, both patched by hand: `PhMetathesisRule.LeftSwitchIndex`/`RightSwitchIndex` are
read through `GetStrucChangeIndices()` rather than as properties.

## The corrected construct list

My regex guess from loader-method names was wrong four ways. Corrections:

**Missed entirely** (stranded in `(orchestration)`/`(other)`):

| Construct | Loader evidence | Author intent |
| --- | --- | --- |
| `partOfSpeech` | `LoadAllPartsOfSpeech`:2588, `LoadLanguage`:172/223/299 | define the POS hierarchy and what each POS owns |
| `stemName` | `LoadLanguage`:210/223 (`Regions`) | region-gated suppletive stem sets |
| `irregularlyInflectedForm` | `LoadLexEntryOfVariant`:741–802 | list an irregular form (*went*) directly, rather than derive it |
| `parserParameters` | `Load`:92 | HC-level global switches |

**Split — one provisional construct was two:**

- **`ruleContextOrEnvironment` → `environment` + `ruleContext`.** Exactly layer-1's amendment that
  construct identity is `(class, owning field)`: `environment` is string-authored
  (`GetValidEnvironments`:1184, `SplitEnvironment`:2264), `ruleContext` is structural
  (`LoadPatternNode`:2325–2350, `TryLoadSimpleContext`:2747).
- **`affixProcessRule` → rule shell + `affixProcessAllomorph`.** The bucket conflated *which
  grammatical rule this is* with *what one realization does*.

**Reclassified as a non-construct:** `stratum`. `CreateStrata`:376/448/465 builds HCLoader's own **three
internal strata** and buckets templates into them; it never reads a user `MoStratum`. This confirms the
"never read" finding at the loader-method level, independently.

**`featureSystem` needs a note, not a split:** `LoadFeatureStruct`:2505 is not a second construct but a
**cross-cutting value type** (`FsFeatStruc`) reused by `naturalClass`, every MSA, `affixTemplate` slots,
`partOfSpeech`, and `stemName` — so one feature-value edit ripples into every construct owning one.

**Confirmed not a construct:** reduplication has no field anywhere in the TSV. It is an emergent pattern
inside `affixProcessAllomorph` — two `Output` steps referencing the same `Input` part — which is
precisely why it needs a composer (below).

## Totality: 15 of 16 constructs build from the nine layer-1 verbs

Every construct decomposes into `create`/`set`/`clear`/`addRef`/`removeRef`/`move` over shapes layer-1
already handles. **The grammar layer adds zero new verbs and zero new handlers — it is composers over
the same 25.** What it adds is construct-level validation and comparison-class refinement.

**The one genuine gap is `stratum` assignment.** No LibLCM field says "this rule is in stratum X" —
`CreateStrata` reassigns rules by **matching their `Name` string** against tokens in the
`ParserParameters` `<Strata>` blob (`MoveMatchingItems`:484–494). So it is *mechanically* one `set` on a
string field, but its **addressing scheme is name-matching, not reference-based**, which no `addRef`/`move`
can express. It must be a composer that string-builds the `<Strata>` grammar and keeps authored names in
sync — a name-collision hazard rather than a referential-integrity one.

## Composers required, by measured step count

| Composer | Why |
| --- | --- |
| `addAffixAllomorph` | the headline case — `LoadAffixProcessAllomorph`:1338–1428 touches 9 fields; authoring one realization by primitives is **15–25 ops** for a single linguistic statement |
| `addReduplicationAllomorph` | no field signals reduplication; a primitives-only author cannot discover the two-Output-steps-share-one-Input idiom |
| `addCircumfixAllomorph` | `LoadCircumfixAffixProcessAllomorph`:1309 builds two correlated halves that must stay consistent; dropping one side silently deletes the whole rule |
| `setSlotOrder` | the slot-sequence collapse means a single `addRef` isn't self-contained |
| `addIrregularlyInflectedForm` | 8 fields across a child `LexEntry` + `LexEntryRef` + `LexEntryInflType` |
| `setParserOption` | one string field encodes 8+ independent settings plus per-rule `maxApps`; round-trip the `<HC>` element only and never touch `<XAmple>` |

**Direct primitives, no composer:** `naturalClass` (3 fields), `environment` (1), `metathesisRule` (3),
`coOccurrenceRule` (3), `stemName`, `partOfSpeech`, `compoundRule` (borderline), the `rewriteRule` and
`affixProcessRule` shells.

## Three string-authored surfaces

1. **`PhEnvironment.StringRepresentation`** — the `PhonEnvRecognizer` mini-language (`/left_right`, `#`,
   `[NC]`, `(optional)`, literal graphemes). Known trap: an invalid string becomes *"applies
   everywhere"* rather than failing.
2. **`PhMetathesisRule.StrucChange`** — a compact position-swap notation. **Not** the same field as
   `PhSegRuleRHS.StrucChange`, which is structural; the mechanical extraction itself couldn't
   disambiguate them, which is the trap in miniature. No named recogniser — validation is inline at
   `LoadMetathesisRule`:2126, and unset switch indices make the rule vanish silently.
3. **`MoMorphData.ParserParameters`** — XML with an `<HC>` element carrying 6 booleans/enums, 2 integers,
   and per-rule `maxApps`, plus a wholly inert `<XAmple>` sibling. Schema enforcement unconfirmed;
   verify before shipping a `set` against it.

**Near-miss worth a warning:** `PhEnvironment.AMPLEStringSegment` is another `Unicode` string on the
*same class* as the real one, and is never read.

## Where the two reviews disagreed — and how it resolved

The semantic review found `ProcliticSlots`/`EncliticSlots` in the never-referenced set and inferred a
gap in my extractor, since the settled docs claimed HCLoader "reads all five sequences." The syntactic
review ran an **exhaustive grep and found zero references** to `Slots`, `ProcliticSlots`, or
`EncliticSlots`.

**The evidence wins: the settled doc was wrong.** Only `PrefixSlots` and `SuffixSlots` are read,
collapsed as `SuffixSlotsRS.Concat(PrefixSlotsRS.Reverse())`. The other three are FieldWorks UI/legacy
surface, invisible to the parser, and the API must not promise them.
[HC surface scope](hc-surface-scope.md) and the [grammar map](hc-grammar-map.md) are corrected.

## What an author cannot reach — the 326, by cause

- **Lexicographic apparatus HC ignores** (largest cluster): essentially all of `LexSense`'s descriptive
  fields (30), `LexEtymology` (7), most of `LexEntry` (18), `LexPronunciation` metadata. This is a
  human-dictionary-reader surface, not a word-structure surface.
- **Publication/display plumbing:** `CmPossibility` cosmetics (17), `CmPossibilityList` UI config (15),
  `CmPicture` (9).
- **Project administration:** 43 `LangProject` fields, including all writing-system and locale
  bookkeeping.
- **Derived/engine-computed:** `GlossString`, `RefNumber`/`ValueState`, `MainEntriesOrSenses`, all
  `DateCreated`/`DateModified`.
- **Genuinely inert despite looking grammar-shaped — the boundary an author most needs warned about:**
  every `Description` on all 13 grammar classes; `MoStratum` entirely, cascading to every `*.Stratum`
  reference; **`PhSegmentRule.InitialStratum`/`FinalStratum`**, which the model comment *promises* gives
  per-rule stratum scoping and which is silently ignored; **`MoStemName.DefaultAffix`/`DefaultStem`** —
  only `Regions` drives suppletion, so a fallback form is inexpressible; and **`MoGlossItem`'s entire
  10-field gloss-abbreviation system**, never consulted, because the gloss HCLoader uses comes from
  `LexSense.Gloss` instead — a different field on a different class serving the same need.

## The ceiling

`LoadInflAffixProcessRule`:982–1002 always builds a plain `AffixProcessRule`, carrying an inline
`// TODO: use realizational affix process rules`. Consequently an author cannot express **paradigmatic
blocking** (realize an unrealized feature bundle by one of several competing forms, elsewhere-conditioned)
or **`LexFamily` suppletive-stem selection** (*go/went*, *feet*). The only reachable substitute,
`irregularlyInflectedForm`, is a *per-entry* lexical override, not a paradigm-wide rule — it cannot
generalise. This is a ceiling in `HCLoader.cs` itself, not a LibLCM data gap: `LexEntryInflType` and
`InflFeats` exist and are populated; nothing in the loader ever feeds a realizational rule.
