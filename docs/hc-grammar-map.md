# The HC grammar map — Motif's normative grammar write-surface

Reverse-engineered from `FieldWorks/Src/LexText/ParserCore/HCLoader.cs` (2837 lines, read in full),
`HCParser.cs`, `IHCLoadErrorLogger.cs`, `GenerateHCConfig/`, cross-checked against
`liblcm/MasterLCModel.xml`, `machine/src/SIL.Machine.Morphology.HermitCrab/`, and PanGloss's
independent Rust port.

**This map is the requirement.** Per [ADR 0010](adr/0010-hermitcrab-experimentation-is-the-primary-purpose.md),
Motif's grammar API is exactly the set of LibLCM inputs `HCLoader` consumes — nothing less, nothing
pointless. It is a **versioned dependency**: when HCLoader or HC changes, this map is re-derived and the
API re-checked.

## The write-surface (complete)

Writing these — and only these — gives full control of the projected grammar.

| Area | LibLCM class + fields |
| --- | --- |
| Project | `LangProject.PartsOfSpeechOA` (hierarchy, `Name`/`Abbreviation`/`SubPossibilitiesOS`/`DefaultInflectionClassRA`/`InflectionClassesOC`); `.MsFeatureSystemOA`, `.PhFeatureSystemOA` (`FsClosedFeature`/`FsComplexFeature` + `ValuesOC` + `Abbreviation`) |
| Phonology | `PhonologicalDataOA.PhonemeSetsOS[0]` **only** → `PhPhoneme.{FeaturesOA, CodesOS→Representation}`; `.BoundaryMarkersOC`; `.NaturalClassesOS` (`PhNCSegments.SegmentsRC` / `PhNCFeatures.FeaturesOA`, `Abbreviation`); `.PhonRulesOS` (`PhRegularRule`/`PhMetathesisRule` + `PhSegRuleRHS`, `Disabled`, sequence position) |
| Parser params | `MorphologicalDataOA.ParserParameters` XML — `<HC>`: `NoDefaultCompounding`, `NotOnClitics`, `AcceptUnspecifiedGraphemes`, `Strata`, `DelReapps`, `GuessRoots`, `MergeAnalyses`, `MaxRoots`; `<CompoundRules>` per-GUID `maxApps` |
| Compounding | `MorphologicalDataOA.CompoundRulesOS` (`MoEndoCompound`/`MoExoCompound`; `Disabled`, `HeadLast`, `Left/RightMsaOA`, `OverridingMsaOA`/`ToMsaOA`) |
| Lexicon | `LexEntry.{AlternateFormsOS, LexemeFormOA, MorphoSyntaxAnalysesOC, SensesOS, EntryRefsOS}`; `MoStemAllomorph.{Form, PhoneEnvRC, StemNameRA, MorphTypeRA}`; `MoAffixAllomorph.{Form, PhoneEnvRC, PositionRS, MorphTypeRA, MsEnvFeaturesOA, InflectionClassesRC}`; `MoAffixProcess.{InputOS, OutputOS, MorphTypeRA}` + `MoInsertNC`/`MoCopyFromInput`/`MoInsertPhones`/`MoModifyFromInput`; `MoStemName.RegionsOC` |
| MSAs | `MoStemMsa`, `MoDerivAffMsa` (all `From*`/`To*`), `MoInflAffMsa` (`PartOfSpeechRA`, `InflFeatsOA`, `SlotsRC`, `FromProdRestrictRC`), `MoUnclassifiedAffixMsa` |
| Templates | `MoInflAffixTemplate` (`Name`, `Final`, `Disabled`, `SuffixSlotsRS`, `PrefixSlotsRS`); `MoInflAffixSlot` (`Name`, `Optional`, `Affixes`); `ILexEntryInflType` (`GlossPrepend`, `GlossAppend`, `InflFeatsOA`, slot refs) |
| Co-occurrence | `MoAlloAdhocProhib` / `MoMorphAdhocProhib` (`First*RA`, `RestOf*RS`, `Adjacency`, `Disabled`) |
| MPR sources | `MoInflClass` hierarchy; `MorphologicalDataOA.ProdRestrictOA` list; `ILexEntryInflType` set |

## Read from LibLCM but *not* structurally — the traps

- **Environments are parsed from `PhEnvironment.StringRepresentation.Text`, never from the structured
  `LeftContextOA`/`RightContextOA` graph** — unlike phonological-rule LHS/RHS, which *is* read
  structurally. Environment authoring must therefore target the **string grammar**
  (`/left_right`, `#`, `[NC]`, `(optional)`, literal graphemes) that `PhonEnvRecognizer` implements.
- **Slot order** is `SuffixSlotsRS.Concat(PrefixSlotsRS.Reverse())` — closest-to-stem first in both. And
  **only two of the five slot sequences are read at all**: an exhaustive grep of `HCLoader.cs` finds zero
  references to `Slots`, `ProcliticSlots`, or `EncliticSlots`. Those three are FieldWorks UI/legacy
  surface, invisible to the parser — so "the five parallel sequences" describes the *model*, not the
  grammar-relevant write-surface, which is `PrefixSlots` + `SuffixSlots` only.
- **Rule order** is `PhonRulesOS` sequence position via the *virtual* `OrderNumber` (= `IndexInOwner + 1`).
- **Alpha variables** come from the *virtual* `IPhRegularRule.FeatureConstraints`, ordered by
  first-appearance scan of `InputOS` then `OutputOS` — so reordering without changing meaning renames α/β.
- Other virtuals depended on: `AllPartsOfSpeech`, `ReallyReallyAllPossibilities`, `AllAffixSlots`,
  `SenseWithMsa`, `IsCircumfix`, and `MoInflAffixSlot.ReferringObjects` (the incoming-reference
  first-touch cost, paid per slot on every grammar load).

## Never read — do not offer these as controls

`MoStratum` entirely, and `MoMorphData.Strata`. Every `Stratum` reference: MSA/compound-rule
`StratumRA`, and `PhSegmentRule.InitialStratum`/`FinalStratum` (despite the model comment promising
per-rule stratum scoping). `PhPhonemeSet` beyond index 0. The `<XAmple>` half of `ParserParameters`.
Every `Description` field on every grammar class. Derivation-trace classes (`MoStratumApp`,
`MoDerivTrace`, `MoPhonolRuleApp`, `MoAffixTemplateApp`). `MoAffixAllomorph.PositionRS` except for
infixes. All writing systems other than vernacular-default (forms/graphemes) and best-analysis
(names/glosses).

## Silent-loss surface — what "the grammar quietly lost your change" looks like

Motif must be able to **predict and report** these, because HCLoader mostly will not:

- An **invalid environment string becomes "applies everywhere"** rather than being dropped — invalid
  data silently becomes *more* permissive.
- A natural class containing **one** phoneme that failed grapheme checks becomes **entirely unusable**,
  cached as null, and every later reference to it fails silently.
- An adhoc prohibition whose "rest" list contains one unloadable form **drops the whole rule**.
- Entries, morphological rules, and templates with zero surviving allomorphs/slots **vanish silently**.
- A stem name with zero non-empty regions produces nothing, no diagnostic.
- Natural-class abbreviations collide silently (last one wins) in the lookup used for environment parsing.
- Degenerate phonological rules and metathesis rules with unset switch indices are skipped silently.
- `IHCLoadErrorLogger` has 10 callbacks; `InvalidRewriteRule` is **declared but never invoked**.

## Hard crash points — validate before writing

- **24 alpha variables maximum per rule.** `VariableNames` is a fixed 24-entry array; a 25th distinct
  feature constraint in one rule throws `IndexOutOfRangeException` and **kills the whole grammar load**.
- **MPR referential integrity is unforgiving**: raw dictionary indexers at ~16 sites mean any dangling
  inflection-class / prod-restrict / `ILexEntryInflType` reference throws `KeyNotFoundException` and kills
  the load. **This happens in the wild** — PanGloss documents `GenerateHCConfig.exe` crashing on the
  Amharic sample project via a stale `MoMorphAdhocProhib`.
- `PhonemeSetsOS[0]` on a project with zero phoneme sets; `CodesOS[0]` on a code-less terminal unit;
  affix-allomorph casts gated only by morph-type GUID.
- The CLI exporter's `ConsoleLogger` throws `NotImplementedException` on
  `UnmatchedReduplicationIndexedClass` — so a bad reduplication index crashes the exporter while loading
  fine inside FLEx.

## The ceiling: one HC capability FieldWorks cannot produce

`RealizationalAffixProcessRule` exists in HermitCrab, but `HCLoader` never builds one —
`LoadInflAffixProcessRule` carries `// TODO: use realizational affix process rules` and always emits a
plain `AffixProcessRule`. **Realizational morphology is therefore not achievable through the FieldWorks
path, whatever Motif writes to LibLCM.** ADR 0010's "everything HC supports must be authorable" has this
one documented exception, and Motif must not promise it.

## Two ingestion paths — and which semantics are authoritative

1. **HC XML** — `GenerateHCConfig` is three lines: load the project, `HCLoader.Load(cache, logger)`,
   `XmlLanguageWriter.Save(...)`. It calls the *same* loader the interactive FLEx parser calls; there is
   no separate export mode. PanGloss's `pg_grammar::load` parses this XML.
2. **Direct `.fwdata`** — PanGloss now also has `pg-fwdata` → `pg_snapshot::Snapshot` →
   `pg_grammar::compile_project`, documented as *"a Rust port of FieldWorks' HCLoader.cs"*, bypassing
   HCLoader, `XmlLanguageWriter`, and `GenerateHCConfig` entirely. A structural-equivalence gate
   cross-checks the two paths (passing on the larger project and Amharic, modulo Hvo-vs-GUID identity), and reaching
   parity required fixing five real importer defects against HCLoader's *actual* behavior.

**Consequence:** the authoritative semantics are **HCLoader.cs's behavior**, not the HC XML DTD's
generosity. A change Motif makes is judged against this map — and, when PanGloss is on the direct path,
against `compile_project`'s parity with it. It also means the killer workflow may not need Motif to
emit HC XML at all: Motif applies to `.fwdata`, PanGloss reads `.fwdata`. HC XML remains needed for the
C# HermitCrab oracle path.

## Validation obligations this creates for Motif

1. Pre-validate the 24-alpha-variable ceiling per rule.
2. Pre-validate MPR referential integrity (inflection classes, prod-restrictions, `ILexEntryInflType`)
   before writing — HCLoader will crash rather than diagnose.
3. Author environments as strings against the `PhonEnvRecognizer` grammar, and validate them, since an
   invalid environment silently widens rather than fails.
4. Predict and report the silent-loss cases above as part of assessment, so a change that would vanish
   from the grammar is surfaced *before* apply.
5. Keep entry and rule **names** stable, or stratum assignment (name-matched) and slot scope
   (name-matched) break with at most an advisory diagnostic.
