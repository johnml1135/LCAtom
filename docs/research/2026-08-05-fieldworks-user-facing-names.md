# Can motif mechanically harvest FieldWorks' own user-facing vocabulary for its operation names?

*Research of 2026-08-05, against the `liblcm` and `FieldWorks` checkouts, asking whether the "class"
segment of motif's `lexical/sense/setGloss`-style operation names — currently invented by hand — could
instead be pulled mechanically from FieldWorks' own linguist-facing UI strings. Every claim below carries
a citation. Nothing was modified; the manifest TSV was read only.*

**Verdict: partially, and only after accepting that the source vocabulary is per-view, incomplete, and in
one important family of classes not resolvable from the schema at all.** FieldWorks does carry real,
mechanically-readable English vocabulary — three independent, overlapping mechanisms (§1) — but it was
built to label *screens*, not *(class, field) pairs*. A meaningful share of motif's 473 in-scope fields
have no discoverable label anywhere in the config tree (§2), the same field can carry two or three
different labels depending on which view or which owning list it's reached through (§2, §3), and for a
specific, enumerable family of classes — the "generic-CmPossibility" list kinds such as Confidence Level,
Restriction, Status, Education Level — **the display name is not a property of the class or the schema at
all; it is a runtime fact about which list (i.e., which owning field on `LangProject`/`LexDb`) the object
was filed under** (§3). That is exactly the crux the requester flagged, and it is confirmed, not merely
suspected: `BootstrapNewLanguageProject.cs:80` creates the Confidence Levels list with
`ItemClsid = CmPossibilityTags.kClassId` — the bare base class, not a distinct subtype — so no amount of
reading `MasterLCModel.xml` can produce "Confidence Level" for such an object; only its list membership
can. For the other half of the `CmPossibility` family — `PartOfSpeech`, `CmSemanticDomain`, `CmLocation`,
`MoMorphType`, and seven more — the class itself is a real, distinct `<class>` entry and the class name
alone is enough. Motif's manifest already draws exactly this line (its `Construct` column separates
`possibility` from `partOfSpeech|lexRefType|lexEntryType|lexEntryInflType|morphType|phonRuleFeat`), so the
manifest's existing classification is not naïve — it is already tracking the fault line this research was
asked to locate. The practical recommendation: harvest what's harvestable (§1–§2) as a *seed vocabulary*
to cross-check hand-invented names against, not as an authoritative source to substitute for them; do not
attempt to derive a name for a bare-`CmPossibility` object from class metadata alone.

## 1. Where FieldWorks' user-facing labels for classes/fields actually live

`MasterLCModel.xml` itself carries no label concept: `grep -n 'label=' src/SIL.LCModel/MasterLCModel.xml`
returns **zero matches** (**VERIFIED**, full-file grep). The schema is unlabeled; every display string
below lives in either the FieldWorks UI configuration tree or in FieldWorks C# code, never in the LCM
model source. Three independent, overlapping mechanisms carry English vocabulary:

### 1.1 `strings-en.xml` — class names and list-purpose names, not field names

`FieldWorks/DistFiles/Language Explorer/Configuration/strings-en.xml` (495 lines, **VERIFIED** read in
full) is a flat `<strings><group id="…"><string id="…" txt="…"/></group></strings>` document. Its
`ClassNames` group (`strings-en.xml:8-81`) maps a **class id** to one English string, e.g.:

```
<string id="LexEntry" txt="Entry"/>                         strings-en.xml:15
<string id="LexSense" txt="Sense"/>                         strings-en.xml:16
<string id="CmSemanticDomain" txt="Semantic Domain"/>       strings-en.xml:30
<string id="PartOfSpeech" txt="Category (or Part of Speech)..."/>   strings-en.xml:62
<string id="MoInflAffixSlot" txt="Inflectional Affix Slot"/>       strings-en.xml:71
```

Its `PossibilityListItemTypeNames` group (`strings-en.xml:82-108`) is a *second* independent map, keyed
not by class name but by the **owning-field name** of a `CmPossibilityList` (`ConfidenceLevels`,
`DomainTypes`, `Education`, `Positions`, …) — this is the key that resolves the generic-`CmPossibility`
problem in §3:

```
<string id="ConfidenceLevels" txt="Confidence Level"/>      strings-en.xml:83
<string id="DomainTypes" txt="Academic Domain"/>            strings-en.xml:84
<string id="Positions" txt="Position"/>                     strings-en.xml:91
```

`AlternativeTitles` (`strings-en.xml:113-159`) is a *third* map — plurals and view-specific titles, e.g.
`PartOfSpeech-Plural -> "Categories (or Parts of Speech)"` (`:115`), and `LexEntryType`-derived lists get
*different* names depending on which owning field files them: `ComplexEntryTypes -> "Complex Form Type"`
(`:98`) vs. `VariantEntryTypes -> "Variant Type"` (`:99`) — same class, two names, disambiguated only by
list membership. This file has **no per-field group at all** — nothing named `Fields` or `Properties`
exists among its 34 `<group>` elements (`strings-en.xml:5-491`, enumerated in full). Field-level text
lives in mechanism 1.2.

### 1.2 The `.fwlayout` / `Parts/*.xml` "slice" system — the real per-(class, field) label registry

Under `Language Explorer/Configuration/`, 50 files under `Parts/` (plus the `.fwlayout` files themselves)
carry a `label=` attribute (**VERIFIED**, `grep -rl 'label='` count). Two related element types carry it:

- **`<part ref="FieldName" label="…">`** inside a `<layout class="ClassName" type="…" name="…">` block —
  a *view-specific* label for a field, scoped to one named layout of one class. Example:
  `Parts/LexEntry.fwlayout:7`: `<part label="Lexeme Form" ref="LexemeForm"/>`.
- **`<slice field="FieldName" label="…" tooltip="…">`** inside a `<part id="ClassName-Detail-PartName">`
  block in a `*Parts.xml` file — the more fundamental definition, often carrying a full sentence of
  end-user-facing help text. Example, `Parts/LexSenseParts.xml:526-527`:

  ```xml
  <part id="LexSense-Detail-GlossAllA" type="detail">
      <slice field="Gloss" label="Gloss" editor="multistring" ws="all analysis" menu="mnuDataTree-Help"
          sideEffectMethod="AdjustDerivedAnalysis"
          tooltip="Short translation equivalent for this lexeme.  Used in interlinear texts, and in the
          dictionary article when Definition is empty."/>
  </part>
  ```

  This is the closest thing in the whole tree to a canonical, machine-readable "(class, field) -> English
  label + description" registry, and it is genuinely mechanical to scrape: `slice field="X" label="Y"
  tooltip="Z"` is a stable, greppable pattern. **VERIFIED**: 302 total `slice field=` occurrences across
  the `Configuration/` tree; 158 distinct field-name tokens (see §2 for what that count does and doesn't
  mean).

  **The nuance the task asked about is real and directly observed.** The literal string `ref="Gloss"` /
  `field="Gloss"` is *not* unique to `LexSense.Gloss`. `LexEntry.fwlayout:79` uses the same ref name
  `Gloss` with the same label `"Gloss"`, but inside `<layout class="LexEtymology" …>`
  (`LexEntry.fwlayout:72`) — it is `LexEtymology.Gloss`, a different class entirely, reached only by
  reading which `<layout class="…">` block encloses the `<part>`. A naive scrape keyed on the bare string
  `ref="Gloss"` would conflate the two. Similarly `ref="Form"` at `LexEntry.fwlayout:78` inside the same
  `LexEtymology` block is labeled `"Source Form"` — not `MoForm.Form`'s label at all.

### 1.3 Tool/area configuration — labels tied to `(ownerClass, ownerField)`, independent of runtime class

`Lists/Edit/toolConfiguration.xml:62`: `<tool label="Confidence Levels" value="confidenceEdit"
icon="SideBySideView">` and `Lists/areaConfiguration.xml:90`: `<parameters tool="confidenceEdit"
className="CmPossibility" ownerClass="LangProject" ownerField="ConfidenceLevels"/>` — the tool's label is
keyed by the *tool*, which is in turn keyed by `(ownerClass, ownerField)`, and `className` here is the
generic base class. This is mechanism §1.1's `PossibilityListItemTypeNames` key (`ConfidenceLevels`)
showing up a second time, independently, in a different subsystem — good redundant confirmation, not a
new source of information.

### 1.4 Dead ends checked and ruled out

- **`.resx` files** (453 found under `FieldWorks/Src`, **VERIFIED** via glob) are conventional WinForms
  designer resources — dialog strings, icons, per-control text — keyed by internal message IDs like
  `ksWhatIsSetPartOfSpeechGUIDsToGold` (`Src/LexText/Lexicon/LexEdStrings.resx:584`), not a class/field ->
  label dictionary. Spot-checked the two `.resx` files that mention `PartOfSpeech`/`LexemeForm` by name;
  neither is a systematic map.
- **`LcmMetaDataCache.GetFieldLabel(int flid)`** (`liblcm/src/SIL.LCModel/Infrastructure/Impl/
  LcmMetaDataCache.cs:361-366`) looked like exactly the right API — a "user label" concept living on the
  metadata cache itself, addressable by the same `flid` motif already resolves fields by. **It is a dead
  end for built-in fields, verified by reading the registration path, not assumed:** the reflection-driven
  loader that registers every ordinary model property passes the label parameter as a literal `null`
  (`LcmMetaDataCache.cs:188-195`, inside `InitializeMetaDataCache`, iterating `ModelPropertyAttribute`/
  `VirtualPropertyAttribute` on every generated domain class). The `m_fieldLabel` slot is populated for
  exactly one case: **custom (user-created) fields**, where it starts out equal to the label the user typed
  when creating the field (`LcmMetaDataCache.cs:960`: `mfr.m_fieldLabel = fieldName; // user label is
  original proposed name.`) and can later be edited (`:1101`, `UpdateCustomField`). So `GetFieldLabel` on
  any of the 473 in-scope built-in fields returns `null` at runtime — **VERIFIED** by the registration
  code, not merely inferred. This is worth flagging precisely because it is the most attractive-looking
  API and the one most likely to be reached for first; it does not do what its name suggests for the
  built-in schema.

## 2. Is the mapping complete and mechanical? — sampled pairs and coverage

Sixteen `(class, field)` pairs, chosen per the task's required spread plus extras, checked against every
mechanism in §1:

| Class.Field | Label(s) found | Citation | Note |
| --- | --- | --- | --- |
| `CmPossibility.Name` | "Name"; also "Vernacular"/"Analysis" in a bilingual variant | `Parts/CmPossibilityParts.xml:6,21,24` | Three labels for one field, chosen by view |
| `LexSense.Gloss` | "Gloss" + full tooltip sentence | `Parts/LexSenseParts.xml:526-527` | Canonical slice definition |
| `LexEntry.LexemeForm` | "Lexeme Form" | `Parts/LexEntry.fwlayout:7` | Clean 1:1 hit |
| `MoForm.Form` | **No isolated label** — absorbed into composite parts `FormAllV`/`FormAllVAffix` labeled "Stem Allomorph"/"Affix Allomorph" | `Parts/Morphology.fwlayout:66,79` | The raw field has no standalone UI label; only a *different* class's reuse of the ref (`LexEtymology.Form` -> "Source Form", `LexEntry.fwlayout:78`) is labeled at all |
| `PhSegRuleRHS.StrucChange` | **Not found** in any config file (`grep -ri strucchange` over all of `Configuration/` returns nothing) | — | Rendered by a bespoke C# control (`Src/LexText/Morphology/RegRuleFormulaControl.cs`), addressed only by the flid constant `PhSegRuleRHSTags.kflidStrucChange`; no literal label string found in that control either |
| `PhSegRuleRHS.LeftContext` / `RightContext` | Not found | — | Same rule-formula-control family as `StrucChange` |
| `MoInflAffixSlot.Name` | "Name" generically; **"Slot Name"** in the `EditSlot` layout | `Parts/MorphologyParts.xml:976` vs. `Parts/Morphology.fwlayout:205` | Same field, two labels, chosen by layout context — the exact "per-view" nuance the task asked to confirm |
| `MoInflAffixSlot.Description` | "Description" (generic, no override in `EditSlot`) | `Parts/MorphologyParts.xml:978-979`; ref used plain at `Morphology.fwlayout:206` | |
| `MoInflAffixSlot.Optional` | "Optional" as an editable slice; but the *dominant* use of this field in the config tree is as a conditional gate, not a labeled value | `Morphology.fwlayout:207` (label) vs. ~50 occurrences of `<if class="MoInflAffixSlot" field="Optional" boolequals="…">` in `MorphologyParts.xml` | A field can be config-visible without ever being "labeled" in the ordinary sense |
| `FsFeatureSpecification.Value` / `FsClosedFeature.Value` | "Value" atomically; **"Nested Features"** when the value is itself a complex feature structure | `Parts/MorphologyParts.xml:1922,1932,1953` vs. `:1943` | A third confirmed case of one field, two labels, chosen by the *shape of the data*, not the view name |
| `PartOfSpeech` (class) | "Category (or Part of Speech)..." / plural "Categories (or Parts of Speech)" | `strings-en.xml:62,115` | Class-level, not field-level |
| `CmSemanticDomain` (class) | "Semantic Domain" | `strings-en.xml:30` | |
| `LexRefType` (class) | "Lexical Relation" — **collides** with `LexReference` (a different class), which maps to the same English string | `strings-en.xml:39` vs. `:22` | Two distinct classes, one label — a real collision risk for a name-collision-sensitive vocabulary |
| `MoMorphType` (class) | "Morpheme Type" | `strings-en.xml:37` | |
| `LexEntryType` (class), filed as `ComplexEntryTypes` vs. `VariantEntryTypes` | "Complex Form Type" vs. "Variant Type" — **same class, two names**, disambiguated only by owning field | `strings-en.xml:98,99` | A distinct-class member of the `CmPossibility` family *still* gets a list-dependent override in some contexts |
| `ConfidenceLevels` list (bare `CmPossibility` items) | "Confidence Level" (item-kind name) / "Confidence Levels" (tool name) | `strings-en.xml:83`; `Lists/Edit/toolConfiguration.xml:62` | The generic-class case; name is a runtime/list fact — see §3 |

**Coverage, quantified honestly.** The manifest has 473 in-scope rows (**VERIFIED**,
`awk -F'\t' '$4=="\"in\""' manifest/liblcm-inventory.tsv | wc -l`), split by `Classification` as
307 `semantic-operation`, 105 `supporting-detail`, 30 `unsupported`, 16 `internal`, 10 `derived-read-only`,
3 `runner-bookkeeping`, 2 `observable-not-authorable`. Across the entire `Configuration/` tree there are
**158 distinct field-name tokens** that appear in at least one `slice field="…"` (**VERIFIED** count). That
number is not directly comparable to 473, in both directions at once: it *undercounts* true coverage
because one generic name like `Name` or `Description` is reused across dozens of classes and so counts
once while covering perhaps 30+ manifest rows; it *overcounts* relative to the in-scope set because it
includes fields on out-of-scope classes (`CmPicture`, `ScrCheckRun`, notebook/checking-tool classes) that
never appear in the 473. Given the sampled pattern above — `semantic-operation` "core content" fields on
lexical/grammar/lists classes (`Name`, `Gloss`, `Form`-as-part-of-a-composite, `LexemeForm`, `Definition`)
are reliably labeled; the entire rule-context/feature-structure-formula family
(`StrucChange`/`LeftContext`/`RightContext` and their `PhPhonContext`/`PhSimpleContext*` siblings) is
**not** labeled anywhere in the slice system at all, being handled by bespoke graphical rule-builder
controls instead; and most `supporting-detail`/`derived-read-only`/`internal`/`runner-bookkeeping` fields
(timestamps, colors, sort keys, help IDs) are simply absent from the UI config tree — a defensible rough
estimate is that **well under half, plausibly on the order of a third to just under half, of the 473
in-scope rows would yield a discoverable label** by scraping this mechanism, concentrated almost entirely
in the `semantic-operation` bucket. **This is an estimate from a sample and two blunt counts, not a
measurement**; I did not build a full crosswalk of all 473 rows against all 302 slice occurrences.

**Labels are per-view, not per-field, as a matter of design.** Every one of the three "same field, two
labels" cases above (`MoInflAffixSlot.Name`, `FsClosedFeature.Value`, `CmPossibility.Name`) was found
without deliberately searching for divergence — it is the norm, not an edge case, whenever a field is
reused across multiple layouts or multiple owning contexts.

## 3. The inheritance question — the crux

### 3.1 The `CmPossibility` family, fully enumerated from `base=` chains

Following every `base="…"` attribute in `MasterLCModel.xml` (**VERIFIED**, full-file greps, not the
`depth=` attribute, which does not reliably encode transitive inheritance distance — e.g. `PartOfSpeech`
and `LexEntryInflType` both carry `depth="1"` despite being one and two inheritance steps from
`CmPossibility` respectively, so `depth` was not trusted for this count):

`CmPossibility` itself: `<class num="7" id="CmPossibility" abstract="false" abbr="pss" base="CmObject"
depth="0">` (`MasterLCModel.xml:306`).

**12 direct subclasses** (`base="CmPossibility"`):

| Class | Citation |
| --- | --- |
| `CmLocation` | `MasterLCModel.xml:460` |
| `CmPerson` | `MasterLCModel.xml:465` |
| `CmAnthroItem` | `MasterLCModel.xml:710` |
| `CmCustomItem` | `MasterLCModel.xml:713` |
| `CmAnnotationDefn` | `MasterLCModel.xml:816` |
| `CmSemanticDomain` | `MasterLCModel.xml:1490` |
| `MoMorphType` | `MasterLCModel.xml:3600` |
| `PartOfSpeech` | `MasterLCModel.xml:3763` |
| `LexEntryType` | `MasterLCModel.xml:4796` |
| `LexRefType` | `MasterLCModel.xml:4810` |
| `ChkTerm` | `MasterLCModel.xml:4937` |
| `PhPhonRuleFeat` | `MasterLCModel.xml:5161` |

**1 transitive subclass**: `LexEntryInflType` (`base="LexEntryType"`, `MasterLCModel.xml:5176`) — checked
and confirmed there is nothing deriving from `LexEntryInflType` in turn (`grep -n '<class[^>]*
base="LexEntryInflType"'` returns no matches), and nothing derives from any of the other 11 direct
subclasses either (checked all 12 by name; only the one hit above). **Total: 13 concrete classes plus the
base — 14 in the family.**

### 3.2 How FieldWorks decides which "kind" a `CmPossibility` object is — and the split is real

`CmPossibilityList.ItemClsid` is a schema field on the *list*, not the item: `<basic num="14" id="ItemClsid"
sig="Integer"><comment><para>This is the clsid of the items that can be inserted into this list.</para>
</comment></basic>` (`MasterLCModel.xml:404-408`). It is set **per list instance**, at data-creation time,
in code — this is the runtime fact the crux hinges on. `BootstrapNewLanguageProject.cs` sets it
differently for different lists (**VERIFIED**, all citations read directly):

```
lp.AnnotationDefsOA.ItemClsid = CmAnnotationDefnTags.kClassId;   // BootstrapNewLanguageProject.cs:74 — distinct class
lp.AnthroListOA.ItemClsid    = CmAnthroItemTags.kClassId;       // :76                  — distinct class
lp.PartsOfSpeechOA.ItemClsid = PartOfSpeechTags.kClassId;       // :78                  — distinct class
lp.ConfidenceLevelsOA.ItemClsid = CmPossibilityTags.kClassId;   // :80                  — GENERIC, bare CmPossibility
lp.LocationsOA.ItemClsid     = CmLocationTags.kClassId;         // :82                  — distinct class
lp.SemanticDomainListOA.ItemClsid = CmSemanticDomainTags.kClassId; // :84               — distinct class
lp.MorphologicalDataOA.ProdRestrictOA.ItemClsid = CmPossibilityTags.kClassId;   // :43   — GENERIC
lp.PhonologicalDataOA.PhonRuleFeatsOA.ItemClsid = PhPhonRuleFeatTags.kClassId;  // :46   — distinct class
lp.TranslationTagsOA.ItemClsid = CmPossibilityTags.kClassId;    // :102                 — GENERIC
```

**This is the single most important citation for the crux: `BootstrapNewLanguageProject.cs:80`.** It shows,
in the same file and the same pattern as the "real subclass" cases, that the Confidence Levels list is
explicitly populated with the *base* class, `CmPossibilityTags.kClassId` — not a `ConfidenceLevel` class,
because no such class exists. An object in that list is, at the CLR and CmObject level, indistinguishable
from an object in the Restrictions list, the Roles list, or the Status list; all are bare `CmPossibility`.
**VERIFIED** further, at the config layer, that FieldWorks resolves the display "kind" for these generic
objects by `(ownerClass, ownerField)`, not by the object's own class:
`Lists/areaConfiguration.xml:90`: `<parameters tool="confidenceEdit" className="CmPossibility"
ownerClass="LangProject" ownerField="ConfidenceLevels"/>`, and the same pattern repeats for
`DomainTypes`/`LexDb` (`:78`), `DialectLabels`/`LexDb` (`:93`), `ChartMarkers`/`ConstChartTempl` on
`DsDiscourseData` (`:96,99`), `Education`/`LangProject` (`:102`) — `className="CmPossibility"` in every
one of these, with the distinguishing information carried entirely by `ownerField`. Compare the
distinct-class cases, where `className` *is* the real subtype: `anthroEdit` uses
`className="CmAnthroItem"` (`Lists/areaConfiguration.xml:81,84,87`).

**So the answer bifurcates cleanly, and motif's manifest has already drawn this line correctly.** For the
13 classes in §3.1, the class name alone (via `strings-en.xml`'s `ClassNames` group, keyed by class id) is
sufficient — no runtime/data fact is needed, only the schema. For the generic-`CmPossibility` list kinds
— **confirmed for Confidence Levels, Product-Restriction features, and Translation Types; strongly
inferred for the many other `className="CmPossibility"` tool definitions in `areaConfiguration.xml`
(Restrictions, Roles, Status, Education, Positions, DomainTypes, DialectLabels, and more), though I did not
individually re-verify each one's `ItemClsid` assignment against `BootstrapNewLanguageProject.cs` line by
line** — the display name is a fact about *which list an actual data instance is owned by*, not a fact
recoverable from `MasterLCModel.xml` or any static field metadata. Motif's manifest's own `Construct`
column for `CmPossibility`'s fields already lists exactly `possibility|partOfSpeech|lexRefType|
lexEntryType|lexEntryInflType|morphType|phonRuleFeat` (`manifest/liblcm-inventory.tsv`, `CmPossibility`
rows, `Construct` column) — i.e., six of the thirteen §3.1 classes (the ones reachable from motif's
lexical/grammar scope) plus a residual bare `possibility` bucket. That residual bucket is precisely the
generic-class case this section confirms cannot be named from schema alone.

## 4. Does `MasterLCModel.xml` carry usable documentation via `<comment>`?

752 `<comment>` elements exist in the file (**VERIFIED** count). Eleven sampled below, spanning class-level
and field-level, chosen to spread across the file rather than cluster:

| Location | Text (as written) |
| --- | --- |
| `CmAnnotation.DateCreated`, `:804-807` | "Date stamp for when this annotation was created, for display to the user. This may differ from the internally generated timestamp that is used for record locking and conflict detection." |
| `CmAnnotation.DateModified`, `:809-812` | "Date and time that this annotation was last modified, for display to the user and for use by automated checking tools…" |
| `CmPicture.LocationMin`, `:1285-1288` | "Depending on the value of LocationRangeType, specifies the minimum Scripture reference or the number of paragraphs before the paragraph containing the ORC reference at which this picture can be laid out." |
| `ScrCheckRun` (class), `:2253-2256` | "Records the date and results of a run of a check." |
| `ScrCheckRun.CheckId`, `:2258-2260` | "A GUID uniquely identifying the check." |
| `MoForm` (class), `:3282-3288` | Four `<para>`s of morphological theory, e.g. "...the English noun plural suffix... has the form /z/ after strident sounds, /s/ after (non-strident) voiceless sounds, and /z/ elsewhere. The latter environment can most easily be represented as 'otherwise,' a characterization which requires disjunctive ordering..." |
| `MoForm.Form`, `:3290-3292` | "It is not clear to me what is supposed to go into this attribute for process affixes (MoAffixProcess), since these do not have a form per se..." — a first-person developer note, not end-user prose |
| `PartOfSpeech` (class), `:3763-3765` | A single dense paragraph citing Chomsky 1970 and discussing "the stealth-to-wealth approach" and phrase-structure theory |
| `PhTerminalUnit` (class), `:4326-4328` | "...assumption here is that there is a way to convert between the representation of the form of a WfiWordform as a string in some encoding, and a sequence of phonemes..." |
| `ChkSense.Explanation`, `:4872-4875` | "A short explanation of the relationship of the lexical sense to the check item... For example, in the Key Terms list, may add an additional explanation of why the lexical item is chosen..." |
| `ChkSense.Sense`, `:4878-4880` | "In Scripture, a target lexical sense for a key term. More generically, a lexical sense associated with a checklist item. With the LexSense as a starting point, the stem, citation form, literal meaning, definition, gloss and likely inflectional word forms for the rendering can be determined." |

**Assessment, falsifiable against the quotes above: inconsistent, and predominantly developer/theory-facing,
not end-user-facing.** A few comments (`CmAnnotation.DateCreated`, `ChkSense.Explanation`) are short,
plain, and would not embarrass a linguist reading them. Most are not: some are first-person implementation
uncertainty (`MoForm.Form`: "It is not clear to me..."), some are dense academic-linguistics argument
citing named theorists (`PartOfSpeech`), some describe internal enumeration encodings rather than meaning
(`CmPicture.LocationRangeType`, sampled but not quoted above, spells out numeric codes 0-6). None read as
copy a product would show a user. **These are not a usable label source**, though a handful could
plausibly seed a *tooltip*-style secondary description for fields that already got a label from §1.2.

## 5. Precedent inside the org for a stable public field vocabulary

### 5.1 LIFT export — genuine, external, standardized precedent

`Src/LexText/LexTextControls/LiftExporter.cs` (2790 lines, **VERIFIED** spot-read) maps LCM fields to the
LIFT XML vocabulary, which is a cross-tool interchange standard, not an SIL-internal invention:

```
WriteAllFormsWithMarkers(w, "lexical-unit", null, "form", entry.LexemeFormOA);   // LiftExporter.cs:244 — LexEntry.LexemeForm -> "lexical-unit"
WriteAllForms(w, null, null, "gloss", sense.Gloss);                              // LiftExporter.cs:776 — LexSense.Gloss -> "gloss"
public const string sPartsOfSpeechOA = "grammatical-info";                      // LiftExporter.cs:2516 — PartOfSpeech family -> "grammatical-info"
```

This is the strongest precedent found: an established, external, tool-independent vocabulary already
mapped from exactly the fields motif cares about. Its coverage is bounded by what LIFT itself models
(lexicon-focused: entries, senses, forms, glosses, grammatical info, relations) — it does not cover
phonology/morphology-rule internals (nothing analogous to `PhSegRuleRHS.StrucChange` exists in LIFT), so it
is a partial vocabulary, strongest exactly where §1's slice system is also strongest.

### 5.2 `GrammarJsonServices` / `M3ModelExportServices` — real code, but authored by the requester, not inherited precedent

Both classes exist, in `liblcm/src/SIL.LCModel/DomainServices/` (not in FieldWorks proper, which explains
why the original FieldWorks-only grep missed them):
`M3ModelExportServices.cs` (1070 lines) and `GrammarJsonServices.cs` (1576 lines).
**Important provenance finding, worth being direct about**: `git log --diff-filter=A -- src/SIL.LCModel/
DomainServices/GrammarJsonServices.cs` in the `liblcm` checkout shows exactly one commit, `d564a719`,
*"Add LCM Grammar JSON: a deterministic grammar export with its contract"*, authored by **John Lambert
<john_lambert@sil.org>** on **2026-07-17** — this is the requester's own prior work in this same
repository, roughly three weeks before this research request, not inherited SIL/FieldWorks legacy code.
`M3ModelExportServices.cs` (copyright 2015) is the older, genuine upstream precedent that
`GrammarJsonServices.cs`'s own commit message explicitly says it "sits next to… and follows the same
pattern."

That provenance doesn't make the vocabulary inside `GrammarJsonServices.cs` useless — it is a real,
deterministic, camelCase JSON field vocabulary already exercised against the domain (`"citationForm"`,
`"lexemeMorphType"`, `"allomorphs"`, `"msas"`, `"senses"`, `"entryRefs"`, `"gloss"`, `"definition"`,
`"partOfSpeech"`, `"partsOfSpeech"`, `"affixSlots"`, `"affixTemplates"`, `"compoundRules"` — all
**VERIFIED** via `WritePropertyName`/`WriteProp`/`WriteWsForms` call sites, e.g.
`GrammarJsonServices.cs:1267-1268` for `citationForm`/`lexemeMorphType`, `:1535-1536` for `gloss`/
`definition`, `:1035` for `partOfSpeech`) — but it should be understood as **the requester's own earlier
design decision**, available to reuse or extend, not as independent third-party validation that a
particular name is "the" FieldWorks-blessed term.

### 5.3 The flid/metadata-cache registry

Covered in §1.4: `IFwMetaDataCache`/`LcmMetaDataCache` does give every field a stable string identity —
`GetFieldName(flid)` (`LcmMetaDataCache.cs:352-357`) reliably returns the bare C# property name
(`"LexemeForm"`, `"Gloss"`) — but this is exactly motif's existing raw-name problem, not a solution to it;
the parallel `GetFieldLabel` slot that could have carried a nicer string is unpopulated for built-in
fields (§1.4).

### 5.4 Recommendation on precedent

None of the four is complete or authoritative enough to *substitute* for motif's hand-invented names.
LIFT (§5.1) is the most credible as an external check for the lexicon-shaped half of the manifest.
`GrammarJsonServices` (§5.2) is worth treating as **the requester's own prior naming decisions**, useful
for consistency with that other artifact, but carries no more authority than any other hand-invented
vocabulary — it is one. The `.fwlayout`/slice labels (§1.2) are the richest source of genuinely
FieldWorks-authored, linguist-facing text, and are the one worth mechanically scraping as a cross-check —
with the explicit caveat, demonstrated three separate times in §2, that the scrape must key on
`(layout-class, field)`, not on the bare field name, or it will silently conflate unrelated fields that
happen to share a `ref=`/`field=` string.
