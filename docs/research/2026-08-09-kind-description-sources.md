# Where do the 93 kind descriptions actually come from, and what's left with no source at all?

This is the follow-up to issue D8: four of the first batch of AI-written operation descriptions said the
exact opposite of what the model means, and nothing mechanical caught it because a backwards sentence reads
just as fluently as a correct one. The fix isn't a smarter check — it's not writing the sentence at all when
a real source already says it, and being honest about the fifteen cases (of ninety-three) where no such
source could be found by anyone who worked on this. Short version: **73 of 93 rows are now copied and cited
from `MasterLCModel.xml` or FieldWorks' own help text, 5 are the hand-corrected `ProdRestrict` rows preserved
untouched, and 15 have no source found yet** — eight of those fifteen have a strong, named lead (a compiled
FieldWorks help page this checkout doesn't contain the source of), and six of the fifteen came up genuinely
empty everywhere searched. Nobody had to write a new sentence to get here; every "sourced" row is a citation,
not a paraphrase.

## 1. What was searched, and what each source yielded

### 1.1 `liblcm` — `src/SIL.LCModel/MasterLCModel.xml`

This is the file the generator already parses for class/field shape, and D8's own note said 60 of 92
described fields had a substantive `<comment>` here. Measured directly (a small harvester walks every
`<basic|owning|rel>` element and reads its first `<para>`): **73 of the 93 fields in
`kind-descriptions.tsv` have some comment text, but 3 of those are not real content** —
`MoAlloAdhocProhib.RestOfAllos` and `MoMorphAdhocProhib.RestOfMorphs` say only "Changed from
RestOfAllomorphs."/"Changed from RestOfMorphemes." (an editorial rename note, not a description), and
`PhCode.Representation` says literally "Put something here." — the original modeler's own placeholder,
never filled in. Net: **66 fields got their description replaced by a cited `MasterLCModel.xml` paragraph**
(plus the 5 hand-corrected `ProdRestrict` rows, which also have a real comment here but keep their
hand-written text — see §3). **19 fields have no `<comment>` element at all** (self-closing declarations),
confirmed by direct inspection, not just a missing-tag guess — e.g. `LexSense.MorphoSyntaxAnalysis`,
`ReversalIndexEntry.PartOfSpeech`/`.Senses`, all five `Abbreviation`-family fields not already covered,
`FsFeatureSpecification.Feature`, `MoInflAffMsa.PartOfSpeech`/`.Slots`, `MoUnclassifiedAffixMsa.PartOfSpeech`,
`PhSimpleContextBdry`/`PhSimpleContextNC.FeatureStructure`.

Other files in the same repo were checked and yielded nothing useful:

- `src/SIL.LCModel/MasterLCModel.xsd` and `LcmGenerate/DomainModel.xsd` — schema shape only, no prose.
- `LcmGenerate/{HandGenerated,IntPropTypeOverrides,ModuleLocations,NonModelPropertiesAndClasses}.xml` —
  build plumbing for the code generator, not documentation.
- `DomainImpl/GeneratedClasses.cs` (the NVelocity-templated C# classes) — checked directly for the fields
  with no `MasterLCModel.xml` comment, expecting the generated XML-doc comment might carry something extra.
  It never does: `FsFeatureSpecification.FeatureRA` gets `/// Gets or sets the Feature`, `LexEtymology.Form`
  gets `/// Gets the Form Accessor.` — both are the template's generic fallback when the source XML has no
  comment, so this is not an independent source, just a mechanical echo of the same gap.

### 1.2 FieldWorks — `manifest/fieldworks-labels.tsv` (already harvested, re-checked here)

Read in full and cross-checked against all 22 fields that had no `MasterLCModel.xml` comment or only a
placeholder one. It covers every one of them with a bare `Label` (all `source=slice`, `confidence=exact`),
confirming the label harvest is thorough — but **zero of the 22 have a non-empty `Tooltip`**. This matches
what the file's own header already says: of 768 harvested rows, only 20 anywhere carry prose, and none of
those 20 land on our fields (they're `CmPicture.PublishIn`, `LexSense.Definition`/`Gloss`/`Exemplar`, and
similar — a different part of the schema). So this file was the right thing to check but had nothing left
to give.

### 1.3 FieldWorks — `DistFiles/Language Explorer/Configuration/ContextHelp.xml` (not previously harvested)

This is a genuinely new source for this project: a flat list of `<item id="...">balloon-help text</item>`
strings FieldWorks shows for dialog controls, in a different file from anything `strings-en.xml`/`.fwlayout`
harvesting touches. Built a small parser plus a **curated** id-to-(Class, Field) map (curated because several
ids are qualified per class — `NaturalClassAbbreviation`, not `Abbreviation` — and some ids are shared
generically across dialogs for unrelated classes, so assuming `id == Field` would silently import the wrong
sentence for the wrong class, the same shape of mistake D8 exists to prevent). Every mapping was checked
against a concrete `field="..."` slice binding in `Parts/*.xml` before being trusted. Yield: **7 fields
sourced this way** — `MoAlloAdhocProhib.FirstAllomorph`/`.RestOfAllos`, `MoMorphAdhocProhib.FirstMorpheme`/
`.RestOfMorphs`, `LexSense.MorphoSyntaxAnalysis`, `PhCode.Representation` (replacing the liblcm placeholder),
and `PhNaturalClass.Abbreviation` (flagged `ambiguous-wiring` in its citation — the string is translated
into 5 locales, so it is almost certainly live, but no `field="Abbreviation"` slice binding naming this
specific help id was found by static search, only strong circumstantial evidence).

### 1.4 FieldWorks — `.fwlayout`/`Parts/*.xml` `helpstring`/tooltip attributes

Checked directly per the task's instruction to look here. `grep -rl helpstring= DistFiles/Language
Explorer/Configuration` returns **zero files** — this attribute does not exist anywhere in the config tree.
Field-level help lives in `ContextHelp.xml` (§1.3) and the compiled help system (§1.5), not inline in the
layout XML.

### 1.5 FieldWorks — `Src/**` and `DistFiles/**` `.resx` files, and the compiled help system

This is the most important new finding, and it directly speaks to the owner's instinct — **"I would assume
first that it is SOMEWHERE."** `Src/LexText/LexTextDll/HelpTopicPaths.resx` (and two sibling
`HelpTopicPaths.resx` files) map a `khtpField-{Class}-{Field}` (or similarly-named) key to a **relative path
into a `User_Interface/Field_Descriptions/...` help-page tree**, e.g.:

```
khtpField-LexEtymology-Gloss        -> User_Interface/Field_Descriptions/Lexicon/Lexicon_Edit_fields/
                                        Entry_level_fields/Gloss_field_Etymology.htm
khtpField-SourceForm                -> .../Entry_level_fields/Source_Form_Etymology.htm   (LexEtymology.Form,
                                        confirmed by matching Label "Source Form")
khtpField-FsFeatStrucType-Abbreviation -> .../Lists/Feature_Types_fields/abbreviation_field_feature_types.htm
khtpField-FsSymFeatVal-Abbreviation    -> .../Grammar/Features_fields/abbreviation_field_value_features.htm
khtpField-MoInflAffMsa-Slots           -> .../Grammatical_Info_Details_fields/Slots_field.htm
khtpField-lexiconEdit-MoInflAffMsa-CategoryInfo -> .../Grammatical_Info_Details_fields/Category_Info_field.htm
khtpField-ReversalIndexEntry-PartOfSpeech -> .../Reversal_Indexes_fields/category_field.htm
khtpField-ReversalIndexEntry-ReferringSenses -> (path exists; key name differs from the model's "Senses" —
                                        probably the same field shown to the user, not confirmed)
```

**That page tree is not in this checkout as HTML.** The actual page content is compiled into
`DistFiles/Helps/FieldWorks_Language_Explorer_Help.chm`, a Windows compiled-help file. `DistFiles/Helps`
is itself a separate git checkout (`git remote -v` → `https://github.com/sillsdev/FwHelps.git`), but it is
**shallow** (`git log` shows exactly one commit) and its working tree contains only 2 `.htm` files total —
none under `Field_Descriptions`. So the pointer is real and precise, but the prose behind it could not be
read in this session: no CHM-extraction tool (`7z`, `hh.exe`, a Python `chm`/`olefile` module) was available
to open the `.chm`, and un-shallowing the `FwHelps` submodule needs a network fetch this session did not
attempt, since the remaining scope for this pass was research and reporting, not further generation.

**This changes the picture for 8 of the 15 "unsourced" rows**: they are not sources that don't exist, they
are sources this session could name exactly but not open. See §3 and §5 for exactly which ones and what
extracting the `.chm` (or un-shallowing `FwHelps`) would need to confirm.

Beyond `HelpTopicPaths.resx`, a broader `.resx` sweep (453 files under `Src/**`) was only spot-checked for
the residual field names, not read file-by-file — that breadth was not repeated once the `HelpTopicPaths.resx`
vein was found, since it already explains where the real text lives for most of the remaining gaps. `FwCoreDlgs`
strings specifically were not searched beyond the earlier `helpstring=` grep.

### 1.6 Everything else checked

- `Localizations/l10ns/*/strings-*.xml` — confirms `NaturalClassAbbreviation` is translated into all 5
  shipped locales (es, fr, pt, ru, zh-CN), corroborating (without proving) that the ContextHelp string in
  §1.3 is live UI text rather than dead.
- `MasterLCModel.xml`'s own class-level comment on the abstract `PhSimpleContext` (line 4258): *"All
  subclasses define a featureStructure attr, but they do so differently for each class."* This is the
  strongest single piece of evidence in the whole pass for a duplicate-of-another-entry recommendation —
  see §3, `PhSimpleContextBdry`/`PhSimpleContextNC.FeatureStructure`.
- `Templates/GOLDEtic.xml`, `Templates/POS.xml`, `Templates/SemDom.xml` (liblcm) — bootstrap data for
  default lists (parts of speech, semantic domains), not field documentation; not useful here.

## 2. Counts

| Bucket | Count | Source file |
| --- | --- | --- |
| `hand-corrected` (preserved verbatim) | 5 | citation added to `MasterLCModel.xml`, text untouched |
| `sourced` — from `MasterLCModel.xml` | 66 | `liblcm/MasterLCModel.xml` |
| `sourced` — from FieldWorks | 7 | `FieldWorks/.../ContextHelp.xml` |
| `unsourced` | 15 | none |
| **Total** | **93** | |

93, not 92, because another agent appended one row (`WfiWordform.SpellingStatus`) while this work was in
progress; it happened to land in the `sourced`-from-`MasterLCModel.xml` bucket on its own, since the field
does carry a real comment (`MasterLCModel.xml:3965`).

The 5 `hand-corrected` rows are exactly the `ProdRestrict`-exception-feature family D8 names:
`MoStemMsa.ProdRestrict`, `MoCompoundRule.ToProdRestrict`, `MoDerivAffMsa.FromProdRestrict`,
`MoDerivAffMsa.ToProdRestrict`, `MoInflAffMsa.FromProdRestrict`. Their text is untouched from before this
pass; only a citation was added, since the source comment (which does exist for all five) uses different
wording from the corrected sentences and overwriting a human's fix with a fresh mechanical paraphrase of the
same source is exactly the risk this task was told to guard against.

## 3. The 15 unsourced kinds — one row per field, evidence, and a recommendation

None of the recommendations below involve writing new prose. Where a lead exists, it's a named pointer;
where nothing was found, that is reported as-is rather than filled in.

| Class.Field | Do we need it? | Found elsewhere? | Defined by another entry? | Recommendation |
| --- | --- | --- | --- | --- |
| `CmPossibility.Abbreviation` | Yes — public-facing, edited by linguists in every possibility list. | `HelpTopicPaths.resx` has **21 separate** per-list help pages for this one field (`ProdRestrictEdit`, `confidenceEdit`, `statusEdit`, `genresEdit`, …), each presumably near-identical ("the short form of this item's name"), never one canonical page. | Not a duplicate of one other entry — this *is* the base-class field 13 possibility-list descendants share (ADR 0023 decision 2/4). Its meaning is genuinely per-list, not per-schema. | (a) **Arguably not a gap at all.** ADR 0023 already decided this field's identity comes from its target's owning list, not its name — the same reasoning applies to its description. No single sentence can be "the" description across 21 different lists without either genericizing it (which one of the per-list `.htm` pages would confirm, once read) or writing 21 near-duplicate sentences by hand, which nobody wants. Recommend: leave unsourced until one of the 21 `.htm` pages is pulled to confirm they really do say the same generic thing; if so, use that generic sentence for the base-class row exactly as ADR 0023 already treats every other property of this field. |
| `CmPossibilityList.Abbreviation` | Uncertain — this is the *list's own* abbreviation, not an item's. No `HelpTopicPaths.resx` entry was found for it at all, for any list, which is itself informative: it may not be a field users are ever shown a dialog for. | Nothing found: no `MasterLCModel.xml` comment, no `ContextHelp.xml` entry, no `HelpTopicPaths.resx` key. | No sibling field found that obviously defines it. | (c) Genuinely nothing found. Before treating this as a hand-write case, check whether this field is reachable from any FLEx dialog at all (it may be `internal`/import-only) — that is a `HCLoader`/FLEx-UI question this pass didn't answer, not a documentation one. |
| `FsFeatDefn.Abbreviation` | Yes — plausibly public-facing (feature abbreviations like "pl" appear in glosses). | Nothing found in any source, including `HelpTopicPaths.resx` (unlike its two siblings below, which *were* found). | **Yes — the sibling fields `FsFeatStrucType.Abbreviation` and `FsSymFeatVal.Abbreviation` have near-identically-worded `HelpTopicPaths.resx` targets** (`abbreviation_field_feature_types.htm`, `abbreviation_field_value_features.htm`), and `MoInflClass.Abbreviation`/`MoStemName.Abbreviation`/`MoStratum.Abbreviation` (already sourced) share **the same boilerplate `MasterLCModel.xml` paragraph almost verbatim**: *"a multiUnicode string, storing an abbreviated form of the Name... defaults to the first eight or so chars."* | (b) Pointer candidate: once one of the sibling `.htm` pages is pulled and confirms the generic wording, point `FsFeatDefn.Abbreviation` at it (or at the `MoInflClass`-style boilerplate paragraph) with a check that fails if the sibling's text changes without this one being re-checked. Not (a): the field is real and edited. |
| `FsFeatStrucType.Abbreviation` | Yes. | **Found — `HelpTopicPaths.resx:680`**, `khtpField-FsFeatStrucType-Abbreviation` → `Lists/Feature_Types_fields/abbreviation_field_feature_types.htm`. Text not extracted (§1.5). | N/A — it has its own named page. | Pull the `.htm` (or the `.chm`) and cite it directly; this is the strongest "somewhere, just not read yet" case in the table. |
| `FsFeatureSpecification.Feature` | Uncertain — `FsFeatureSpecification` is `abstract="true"`, so nothing is ever directly created as one; per ADR 0023 decision 2 the concrete classes (`FsClosedValue`, `FsComplexValue`, …) redeclare `Value`, not `Feature`, so `Feature` really is only ever touched through this abstract declaration. | Nothing found anywhere: no `MasterLCModel.xml` comment (checked both the abstract class and all 6 concrete subclasses for a redeclaration — none redeclares `Feature`), no `ContextHelp.xml`, no `HelpTopicPaths.resx` key, and the generated C# doc comment is the template's generic "Gets or sets the Feature" fallback. | Structurally the closest analog is `PhFeatureConstraint.Feature` (same shape — "which feature does this thing constrain" — cited from `MasterLCModel.xml`, 627 chars), but it is a *different* class in a *different* subsystem (phonological rule contexts vs. feature-structure values), not a formal duplicate. | (c), with a soft lead: if `PhFeatureConstraint.Feature`'s cited text turns out to describe the same relationship in different words, that could become a (b)-style pointer with an explicit "different class, same relationship" caveat — but that is a judgement call for whoever reads both texts side by side, not something to bake in now. |
| `FsSymFeatVal.Abbreviation` | Yes. | **Found — `HelpTopicPaths.resx:671`**, → `Grammar/Features_fields/abbreviation_field_value_features.htm`. Text not extracted. | N/A. | Same as `FsFeatStrucType.Abbreviation` — pull and cite. |
| `LexEtymology.Form` | Yes — the source-language word form, shown in the Etymology section of the entry editor. | **Found — `HelpTopicPaths.resx:440`**, key `khtpField-SourceForm` (not class-qualified, but its target label matches this field's harvested `Label`, "Source Form", exactly) → `Entry_level_fields/Source_Form_Etymology.htm`. Text not extracted. | N/A. | Pull and cite; the key-naming mismatch (`SourceForm` vs. `LexEtymology.Form`) should be double-checked against the dialog once the page is opened, since a same-named coincidence is possible in principle even though the label match makes it very likely correct. |
| `LexEtymology.Gloss` | Yes. | **Found — `HelpTopicPaths.resx:314`**, `khtpField-LexEtymology-Gloss` → `Entry_level_fields/Gloss_field_Etymology.htm`. Text not extracted. | N/A. | Pull and cite. |
| `MoInflAffMsa.PartOfSpeech` | Yes. | **Found — `HelpTopicPaths.resx`**, `khtpField-lexiconEdit-MoInflAffMsa-CategoryInfo` → `Grammatical_Info_Details_fields/Category_Info_field.htm` ("Category" matches this field's harvested Label exactly). A FieldWorks `ContextHelp.xml` entry `PartOfSpeechOrSlot` ("An inflectional affix slot to which this affix belongs. It can also refer to a category, if you do not yet know what slot the affix belongs in.") is a second, weaker lead pointing at the same UI area, but its slice binding could not be confirmed, so it is named here as corroboration only, not used as a citation. | N/A once the `.htm` is pulled. | Pull and cite the `Category_Info_field.htm` page. |
| `MoInflAffMsa.Slots` | Yes. | **Found — `HelpTopicPaths.resx:488`**, `khtpField-MoInflAffMsa-Slots` → `Grammatical_Info_Details_fields/Slots_field.htm`. Text not extracted. | N/A. | Pull and cite. |
| `MoUnclassifiedAffixMsa.PartOfSpeech` | Uncertain — the class itself is documented nowhere (no `MasterLCModel.xml` class-level comment either, unusually), and the field is a placeholder ("affix not yet fully analysed", per the field name and this class's role relative to `MoStemMsa`/`MoDerivAffMsa`/`MoInflAffMsa`). | Nothing found in any source, including `HelpTopicPaths.resx`. | Structurally the same relationship as `MoStemMsa.PartOfSpeech` and `MoDerivAffMsa.FromPartOfSpeech`/`.ToPartOfSpeech` (all "the category of this morpheme," all cited from `MasterLCModel.xml`) — same field name, same declaring family (`MoMorphSynAnalysis` subclasses), just on the "not yet classified" branch. | (b) Pointer candidate: point at the shared "category of this morpheme" meaning already cited on `MoStemMsa.PartOfSpeech`, with a note that this class exists specifically for morphemes not yet assigned a fuller analysis — that qualifier is the one thing that isn't a duplicate and would need to survive in whatever check guards the pointer. |
| `PhSimpleContextBdry.FeatureStructure` | Yes. | Nothing in `ContextHelp.xml` or `HelpTopicPaths.resx`. | **Yes, explicitly, in the source itself.** The abstract parent class `PhSimpleContext`'s own `MasterLCModel.xml` comment (line 4258) says: *"The subclasses define simple contexts in terms of natural classes..., phonemes, and boundary markers... All subclasses define a featureStructure attr, but they do so differently for each class."* Sibling `PhSimpleContextSeg.FeatureStructure` **is already sourced** (123 chars, cited): *"Allows the user to write phonological rules that are sensitive to a particular phoneme (as opposed to a class of phoneme)."* `PhSimpleContextBdry`'s version is the same relationship for a boundary marker instead of a phoneme. | (b) — the clearest case in this table. Point at `PhSimpleContextSeg.FeatureStructure`'s cited text with "...for a boundary marker, not a phoneme" substituted, and add a check that fails if `PhSimpleContextSeg.FeatureStructure`'s `MasterLCModel.xml` citation line/text ever changes without this pointer being re-verified. |
| `PhSimpleContextNC.FeatureStructure` | Yes. | Same as `PhSimpleContextBdry` above. | **Yes, same evidence** — the natural-class variant of the same parent-declared attribute. | (b), same pointer target and same check, substituting "for a natural class of phonemes, not one specific phoneme." |
| `ReversalIndexEntry.PartOfSpeech` | Yes. | **Found — `HelpTopicPaths.resx:500`**, `khtpField-ReversalIndexEntry-PartOfSpeech` → `Reversal_Indexes_fields/category_field.htm`. Text not extracted. | N/A. | Pull and cite. |
| `ReversalIndexEntry.Senses` | Yes. | **Probably found** — `HelpTopicPaths.resx:506`, `khtpField-ReversalIndexEntry-ReferringSenses`. The key says "ReferringSenses," the model field is named "Senses" — plausibly the same field under its UI name, not confirmed. Text not extracted either way. | N/A. | Pull the `.htm`; while there, confirm the key really refers to this field (compare against the dialog or the `.fwlayout` slice for `ReversalIndexEntry`) before citing it, since the name mismatch is exactly the kind of assumption that produced the original polarity bug. |

**Summary of the 15:** 2 are the clearest possible duplicate-of-another-entry case
(`PhSimpleContextBdry`/`NC.FeatureStructure`, backed by the model's own class comment); 1 more is a
reasonable duplicate lead (`MoUnclassifiedAffixMsa.PartOfSpeech`); 1 more is a plausible sibling-boilerplate
lead (`FsFeatDefn.Abbreviation`); 8 have a named, unopened FieldWorks help page; 1 (`CmPossibility.Abbreviation`)
is arguably not a single-sentence question at all, by ADR 0023's own logic; and 2
(`CmPossibilityList.Abbreviation`, `FsFeatureSpecification.Feature`) turned up nothing anywhere and have no
strong duplicate candidate either — the closest thing to a genuine "nothing exists" result in this pass, and
even those come with a next step (confirm `CmPossibilityList.Abbreviation` is reachable in the UI at all;
compare `FsFeatureSpecification.Feature` against `PhFeatureConstraint.Feature` by hand).

## 4. What this means for the count the owner cares about

**Zero of the fifteen needed a hand-written sentence to close**, and none got one. Eight have an exact
citation pulled up in this pass that only needs the target `.htm` opened. Three have a specific pointer
target named and a specific check shape recommended, pending the owner's decision on wording. Two are
"arguably not a single-field question" by the project's own prior decisions. Only two came back with
nothing at all — and even those have one further thing to try before concluding the description doesn't
exist anywhere.

## 5. Exactly what to pull from the FieldWorks git repo, if the owner wants the remaining 8+ closed

Two independent ways to get the actual page text, either would work:

1. **Extract `DistFiles/Helps/FieldWorks_Language_Explorer_Help.chm`** (present in this checkout, 
   `C:\Users\johnm\Documents\repos\FieldWorks\DistFiles\Helps\FieldWorks_Language_Explorer_Help.chm`) with
   any CHM reader/extractor (Windows' own `hh.exe`, `7-Zip`, or a `chm`/`olefile`-capable Python library —
   none of which were available in this session) and pull out the pages named in §3's table, all under
   `User_Interface/Field_Descriptions/...` inside the archive.
2. **Un-shallow the `DistFiles/Helps` submodule**, or clone `https://github.com/sillsdev/FwHelps` fully —
   the current checkout is a 1-commit shallow clone with only 2 `.htm` files in its working tree, none under
   `Field_Descriptions`, so either the source `.htm` tree lives on a different branch/tag, was restructured
   out of this repo at some point, or needs a full (non-shallow) fetch to reveal.

Either would let the 8 "found a pointer, haven't read it" rows in §3 move straight to `sourced`, and would
let `CmPossibility.Abbreviation` be checked for whether its 21 per-list pages really do share one sentence.

## What was not touched

`manifest/liblcm-inventory.tsv` was read only (another agent owns edits to it). No hand-written prose was
added anywhere in this pass — every `sourced`/`hand-corrected` row in `manifest/kind-descriptions.tsv` is
either a citation copied from source or a previously-corrected sentence with a citation attached to it, and
all 15 `unsourced` rows keep whatever text they already had, unchanged, exactly as `DescriptionCheck`
requires for a kind still being emitted.

## Addendum, 2026-08-10 — the help file was extractable, and what that changed

§1.5 concluded the `.chm` could not be opened "with tools available this session". It could:
`hh.exe -decompile <dir> "DistFiles/Helps/FieldWorks_Language_Explorer_Help.chm"` ships with Windows and
finished in seconds, producing 2,183 files including 636 pages under `User_Interface/Field_Descriptions`.
Reading a report of an absence the same way as a report of a fact is the lesson; the rest of this section is
what the pages actually said.

**Nine rows moved to `sourced`, not eight.** The eight §3 named, minus one, plus two the resx did not name:

- `FsFeatDefn.Abbreviation` — §3 rated this a pointer candidate on sibling-boilerplate grounds. It has its
  own page (`Grammar/Inflection_Features_fields/abbreviation_field_feature_features.htm`), found by search
  rather than by key, so no pointer was needed.
- `CmPossibility.Abbreviation` — §3 recommended checking whether the 21 per-list pages really say the same
  generic thing. All 38 `Abbreviation` pages under `Field_Descriptions` were read: they do, each naming its
  own list ("of the current academic domain name", "of the name of the current confidence level"). The row
  now cites the one page whose wording carries no list-specific noun, marked `class-generic` so a reader
  sees it is a representative page rather than a dedicated one.
- **`MoInflAffMsa.PartOfSpeech` was dropped**, and this is the one place reading the page overturned the
  research. §3 proposed `Category_Info_field.htm` on the strength of `khtpField-lexiconEdit-MoInflAffMsa-`
  `CategoryInfo`. The page describes a prose summary area whose label is `Category Info.` — which is
  `MoInflAffMsa.MainEdit`. This field's label is `Category`. A page that does not describe the field is not
  a source for it, so the row stays unsourced.
- `FsSymFeatVal.Abbreviation`'s resx path is stale: `Grammar/Features_fields/abbreviation_field_value_`
  `features.htm` no longer exists, and the page is now `Grammar/Inflection_Features_fields/Abbr_field_`
  `value_features.htm`. Matched on title, and the citation records both paths.
- `ReversalIndexEntry.Senses` — §3's caution about the `ReferringSenses`/`Senses` name mismatch is resolved
  in favour of the mapping: the page's full name is "Referenced Senses", which is exactly this field's
  harvested label.

**Two rows became exemptions rather than gaps.** `CmPossibilityList.Abbreviation` and
`FsFeatureSpecification.Feature` now carry `Reviewed=no-source-exists`, citing the search. The second one's
evidence is a rule the build re-derives — the field is declared once on an abstract class and no concrete
subclass redeclares it — so the exemption lapses by itself if that ever stops being true.

**Two rows became `adapted`.** §3 rates `PhSimpleContextBdry`/`PhSimpleContextNC.FeatureStructure` the
clearest pointer candidates in the table, and they are — but the obvious way to close them, copying the
sibling's sentence and editing the clause that differs, produces prose that is correct today and frozen
forever. What is stored instead is the *substitution*: target field, sibling field, one `(find, replace)`
pair. The sentence is re-derived from `PhSimpleContextSeg.FeatureStructure`'s current source text on every
refresh, and the run fails if the replaced clause is no longer present or is no longer unique. Both rows
carry the sibling's fragment digest, so the three cannot drift apart unnoticed.

**Two remain unsourced**, with the search recorded and no invented text:
`MoInflAffMsa.PartOfSpeech` and `MoUnclassifiedAffixMsa.PartOfSpeech` — no page, no comment, no context
help.
