# What is a "proper" word analysis, and how should motif compare one to a parser's output?

*Research of 2026-08-05, against local checkouts of `FieldWorks`, `liblcm`, `PanGloss`, and
`linguistic-assistant`, in support of motif's planned test suite that treats a human-approved
FieldWorks word analysis as ground truth and a parser run as the thing under test. Every claim
below is tagged **VERIFIED** (I read the exact cited line) or **INFERRED** (reasoning from related
but not identical evidence). Nothing outside `docs/research/2026-08-05-what-is-a-proper-word-analysis.md`
in the `motif` repo was modified.*

## Verdict

FieldWorks does not have one settled notion of "the same analysis" — it has **at least two**, used
for different purposes, and they disagree on exactly the two fields the task asked about.
`ParseAnalysis.MatchesIWfiAnalysis` (`FieldWorks/Src/LexText/ParserCore/ParseResult.cs:102-133`),
which is what `NumUserApprovedAnalysesMissing` and every other parser/human comparison in FieldWorks
actually uses, compares each morph bundle's `MorphRA`/`MsaRA`/`InflTypeRA` (plus a guessed-string
special case) and **never compares `SenseRA` or `WfiAnalysis.CategoryRA`**. Separately,
`WfiWordformServices.DuplicateAnalyses`/`DuplicateBundles`
(`liblcm/src/SIL.LCModel/DomainServices/WfiWordformServices.cs:318-345`), used by the "Merge
Duplicate Analyses" cleanup utility, compares `SenseRA` and `CategoryRA` too (and full multi-writing-system
`Form` string equality) but **ignores `InflTypeRA` entirely**. These are not a bug in one file — the
first omission is inherited, verbatim comment and all, from a 2002-era SQL Server stored procedure
(`UpdWfiAnalysisAndEval$`, ported in FieldWorks' 2024 C# rewrite) whose data contract
(`DistFiles/MSSQLMigration/OldMigrationScripts/200259To200260.sql:60-120`) never had a slot for Sense
or Category, and direct evidence that FieldWorks' own parser filer **never sets** `SenseRA` or
`CategoryRA` on parser-created analyses (`ParseFiler.cs:263-298`) — only a human, through the
Sandbox/Approve UI (`SandboxBase.GetRealyAnalysisMethod.cs:383,440,445`,
`FocusBoxController.ApproveAndMove.cs:145`), ever populates them. So the parser-vs-human comparison
is asymmetric by construction: comparing Sense/Category would compare a real human judgment against
a field the parser structurally cannot fill in. **For motif's test suite, the right design is to
keep FieldWorks' `MatchesIWfiAnalysis` shape (morph count, per-morph root/allomorph identity, per-morph
MSA, InflType, guessed-string handling) as the pass/fail gate, but report Sense and word-level Category
as a separate, non-gating diagnostic** — surfaced when present on the human side, never required from
the parser side — because (a) that mirrors what FieldWorks itself gates on for its own
`NumUserApprovedAnalysesMissing` metric, (b) the parser genuinely cannot produce Sense, and its
Category is not always well-defined either, and (c) PanGloss's own `AnalysisIdentity` (the identity
motif's Rust-side parser would produce, see §6) has no field at all for per-morpheme sense — building
a sense-sensitive test would be unimplementable against PanGloss's current data model as it stands,
not merely undesirable.

---

## 1. What is an analysis, conceptually?

FieldWorks' own schema comment is the most direct primary-source answer available, and it is
consistent with the two-part shape ("morpheme cuts" + "part of speech" + "interpretations") the task
asked about, minus a distinct "sense per morph" as a *constitutive* part of the top-level definition:

> "The WfiAnalysis class contains an analysis for a given wordform, which is defined as how to make
> morpheme cuts on the wordform, a part of speech for the wordform, plus a sequence of one or more
> corresponding interpretations of te [sic] analysis." — `liblcm/src/SIL.LCModel/MasterLCModel.xml:3911`
> (**VERIFIED**, `WfiAnalysis` class comment).

That definition names three things: (1) morpheme segmentation ("morpheme cuts"), (2) a word-level
category, and (3) a set of "interpretations" (glosses, `WfiGloss`, `MasterLCModel.xml:3946-3953`) —
notably *not* per-morph sense as a top-level constitutive element; sense lives one level down, on
each `WfiMorphBundle` (§2).

H. Andrew Black's *Conceptual Introduction to Morphological Parsing for FieldWorks Language
Explorer* (extracted at `linguistic-assistant/docs/reference/flex-conceptual-intro-fulltext.txt`)
frames the whole document around two decision layers — morphotactics (§2, where morphemes go) and
morphophonemics (§3, what shape they surface as) — with lexical-entry considerations (§4, allomorphs,
morpheme types, senses/glosses) treated separately (table of contents,
`flex-conceptual-intro-fulltext.txt:38-137`, **VERIFIED**). Its short §4.4 "Senses/Glosses" section
treats gloss/sense primarily as a *labeling* concern, not a structural one: "FieldWorks Language
Explorer uniquely identifies every gloss internally" even when two glosses are spelled identically,
so "confusion" from identical-looking glosses is a presentation problem, not an identity problem
(`flex-conceptual-intro-fulltext.txt:4247-4253`, **VERIFIED**). This is consistent with FieldWorks'
implementation choice (§3, §7 below) of comparing morphs by *object identity* (GUID/reference
equality: `mb.MorphRA == current.Form`), not by surface string, except in the one case (guessed
roots) where no real object exists to compare.

PanGloss's own design vocabulary (`PanGloss/CONTEXT.md:233-235`, **VERIFIED**) states the same
distinction in its own terms, explicitly modeled on the analogous concept in SIL's C# HermitCrab
engine (SIL.Machine, not present in this checkout — see "What I could not verify"):

> "**Structured analysis identity**: The versioned canonical projection of C# Machine
> `WordAnalysis.Equals`: ordered stable morpheme identities, root-morpheme position, and
> category/POS. … Gloss, surface shape, properties, duplicate counts, discovery order, paths/traces,
> timing, counters, prose, and serialization formatting are diagnostic or presentation evidence, not
> core identity." — `PanGloss/CONTEXT.md:234` (**VERIFIED**).

So both FieldWorks' schema comment and PanGloss's design vocabulary converge on the same shape for
"what is constitutive of an analysis": an ordered sequence of morpheme identities (which lexical
item/allomorph, with which MSA), a root/attachment structure, and (separately, and more weakly
attested as *constitutive* rather than *derived*) a word-level category. Per-morph **sense**, in both
sources, is treated as one level removed from the core identity — present in the data model (§2) but
not named as part of either's headline definition of "an analysis."

## 2. What does the FieldWorks data model actually hold?

All three classes were read directly from `liblcm/src/SIL.LCModel/MasterLCModel.xml` (the canonical
schema source; **VERIFIED** for every field below by direct read of the class blocks).

### `WfiWordform` (`MasterLCModel.xml:3954-3976`, class 62, `owner="none"`)

| Field | Constitutive or incidental | Notes |
|---|---|---|
| `Form` (MultiUnicode) | Constitutive (identifies *which word*) | The wordform string, possibly in multiple encodings. |
| `Analyses` (owning col, `WfiAnalysis`) | Constitutive (container) | The set of candidate analyses. |
| `SpellingStatus` (Integer) | Incidental | UI concordance-view status flag. |
| `Checksum` (Integer) | Incidental/provenance | "calculated based on the result string returned from the parser… to know whether to file new results" (`MasterLCModel.xml:3970-3973`, **VERIFIED**) — parser re-run bookkeeping, not linguistic content. |

### `WfiAnalysis` (`MasterLCModel.xml:3909-3945`, class 59)

| Field | Constitutive or incidental | Who sets it |
|---|---|---|
| `Category` (rel, atomic, `PartOfSpeech`) | Constitutive (word-level category) but **derived/optional in practice** — see §3 | Human, via Sandbox/Approve UI (`SandboxBase.GetRealyAnalysisMethod.cs:383`, `:1093`) or interlinear import (`BIRDInterlinearImporter.cs:898,922`). **Never set by `ParseFiler.ProcessAnalysis`** (`ParseFiler.cs:263-298`, **VERIFIED** — no `CategoryRA` assignment anywhere in that method). |
| `MsFeatures` (owning atomic, `FsFeatStruc`) | Constitutive (inflectional features when there's no morpheme to carry them, e.g. suppletion) | Comment: lets a linguist "know that it's plural without knowing what the plural morpheme is" (`MasterLCModel.xml:3917`, **VERIFIED**). Human-only in practice; not touched by `ParseFiler`. |
| `Stems` (rel, col, `LexEntry`) | Incidental/escape-hatch | "if the linguist wishes to only analyze stems this is an easy way to accomplish this" (`:3922`) — a shortcut, not the primary representation. |
| `Derivation` (owning atomic, `MoDeriv`) | Constitutive when populated | Not touched by `ParseFiler`; not compared by `MatchesIWfiAnalysis`. |
| `Meanings` (owning col, `WfiGloss`) | Constitutive (word-level gloss) — but a *label*, not structure | "the senses in the parent interpretation might be 3S-Carry; the meanings could then be 'he carries'…" (`:3948`). |
| `MorphBundles` (owning seq, `WfiMorphBundle`) | **Constitutive — the core of an analysis** | Set by both parser (`ParseFiler.cs:280-291`) and human (`SandboxBase.GetRealyAnalysisMethod.cs:389-451`). |
| `CompoundRuleApps` (rel, seq) | Incidental (denormalized index) | "Allows Morph Sketch Generator to extract example wordforms…" (`:3931`) — a lookup convenience, not primary content. |
| `InflTemplateApps` (rel, seq) | Incidental (denormalized index) | Same rationale as above (`:3936`). |
| `Evaluations` (rel, col, `CmAgentEvaluation`) | Provenance, not content | This is *approval metadata about* the analysis, not part of what was analyzed (§4). |

### `WfiMorphBundle` (`MasterLCModel.xml:4713-4726`, class 112)

| Field | Constitutive or incidental | Who sets it |
|---|---|---|
| `Form` (MultiString) | Constitutive when there is no real `Morph`; otherwise a display cache | Parser sets it only to store a guessed-root surface string (`ParseFiler.cs:286-291`, "Override default Form with GuessedString"). |
| `Morph` (rel, atomic, `MoForm`) | **Constitutive — the allomorph chosen** | Both parser (`ParseFiler.cs:282`) and human (`SandboxBase.GetRealyAnalysisMethod.cs:428`). |
| `Msa` (rel, atomic, `MoMorphSynAnalysis`) | **Constitutive — the morphosyntactic analysis chosen** | Both parser (`ParseFiler.cs:283`) and human (`:440`). |
| `Sense` (rel, atomic, `LexSense`) | Constitutive (which meaning of the lexeme) | **Human only.** `SandboxBase.GetRealyAnalysisMethod.cs:445`; never assigned in `ParseFiler.cs` (**VERIFIED**, `grep -n Sense ParseFiler.cs` → no matches). |
| `InflType` (rel, atomic, `LexEntryInflType`) | Constitutive (irregular-inflection gloss link) | Both: "a place for both the manual interlinearizer and the parser filer to store the link to the gloss in the inflection type… optional" (`MasterLCModel.xml:4719-4723`, **VERIFIED**); parser sets it at `ParseFiler.cs:284-285`. |

### `CmAgentEvaluation` (`MasterLCModel.xml:753-758`, class 32)

Confirmed exactly as the prior pass claimed: `<class num="32" id="CmAgentEvaluation" abstract="false"
abbr="caev" base="CmObject" depth="0">` with `<props/>` — **no fields of its own** (**VERIFIED**,
`MasterLCModel.xml:753,757`). It is a pure marker object: "indicates that an object is approved or
disapproved by the agent that owns it. Approval is indicated by referring to the instance in
`CmAgent.Approves`, disapproval by referring to the one in `CmAgent.Disapproves`"
(`MasterLCModel.xml:754-756`, **VERIFIED**). `CmAgent` (`MasterLCModel.xml:672-709`, class 23) is the
class that actually distinguishes agents — `Human` (Boolean, `:687-691`, "T = human, F = computational
agent") and `Name` ("Larry, AMPLE, XEROXParser, HermitCrab", `:677-681`) — so "the parser" and "the
user" in FieldWorks are both just `CmAgent` instances distinguished at the `Human` flag, referenced
from `LangProject.DefaultParserAgent`/`DefaultUserAgent` (used at `ParseFiler.cs:227,297`,
`ParserReport.cs:376`, `FocusBoxController.ApproveAndMove.cs:145`).

**Summary of who-sets-what** (the crux for §3 and §7): `ParseFiler.ProcessAnalysis`
(`ParseFiler.cs:263-298`) — the *only* code path that turns a raw parser result into `WfiAnalysis`/
`WfiMorphBundle` objects — sets exactly `MorphRA`, `MsaRA`, `InflTypeRA` (conditionally), and a `Form`
override for guessed roots. It **never sets `SenseRA`, `CategoryRA`, `MsFeatures`, `Derivation`,
`Stems`, `CompoundRuleApps`, or `InflTemplateApps`** (**VERIFIED**, full read of the method). The
Sandbox/interlinear human-approval path (`SandboxBase.GetRealyAnalysisMethod.cs:383,428,440,445,448`)
sets all of `CategoryRA`, `MorphRA`, `MsaRA`, `SenseRA`, `InflTypeRA` directly from user UI selections.

## 3. Why does `MatchesIWfiAnalysis` compare what it compares?

The rationale is recoverable, and it settles the "deliberate vs. oversight" question with unusually
direct evidence — a verbatim-preserved comment plus a 2002-era stored-procedure lineage.

`ParseAnalysis.MatchesIWfiAnalysis` (`ParseResult.cs:102-133`, **VERIFIED**, full read) carries this
docstring:

```
/*
    A "match" is one in which:
    (1) the number of morph bundles equal the number of the MoForm and
        MorphoSyntaxAnanlysis (MSA) IDs passed in to the stored procedure, and
    (2) The objects of each MSA+Form pair match those of the corresponding WfiMorphBundle.
*/
```
— `ParseResult.cs:104-109`.

That comment is not new prose written for the C# port. It is a **near-verbatim copy** of the comment
in the legacy SQL Server stored procedure `UpdWfiAnalysisAndEval$`:

```
-- Try to find match(es) that already exist.
-- A "match" is one in which
-- (1) the number of morph bundles equal the number of the MoForm and
--  MorphoSyntaxAnanlysis (MSA) IDs passed in to the stored procedure, and
-- (2) The IDs of each MSA+Form pair match those of the corresponding WfiMorphBundle.
```
— `FieldWorks/DistFiles/MSSQLMigration/OldMigrationScripts/200259To200260.sql:84-87` (**VERIFIED**).

The SP's actual data contract confirms this is structural, not incidental: the table variable it
matches against, `@Pair`, is declared with exactly three columns — `MsaId`, `FormId`, `Ord`
(`200259To200260.sql:68-71`, **VERIFIED**) — and the XML the parser sends in
(`@ntXmlFormMsaPairIds`, `:60-61`) likewise has no slot for a sense or category ID. **The original
2002-era parser/database interface simply never had anywhere to put "which sense" or "what word-level
category" — it only ever spoke MSA+Form pairs.** `ParseFiler.cs`'s own doc-comment says as much: "This
method contains the port of the `UpdWfiAnalysisAndEval$` SP. The SP was about 220 lines of code…
The C# version is about 60 lines long" (`ParseFiler.cs:258-261`, **VERIFIED**). `git log -p` on
`ParseResult.cs` (`FieldWorks` repo) shows `MatchesIWfiAnalysis` itself was only added to the C# tree
in commit `7b5808321ca2365faba1ba4577cbc82eedd2c254` ("Add ParserReport and ParserReportTest",
2024-05-16, **VERIFIED**), i.e. the SP's shape was carried forward into 2024 C# code unchanged in
this respect, twenty-two years after the original comment was written. `InflType` comparison,
notably, is *not* in the SP contract at all (**VERIFIED**, `grep -rn InflType` over every file in
`DistFiles/MSSQLMigration/OldMigrationScripts/` finds no hits) — it was added later, in the C# era,
once `LexEntryInflType` existed as a concept, confirming the comparison's field list has evolved by
deliberate addition (InflType) rather than by uniform legacy inertia.

**Does the parser ever set Sense or Category at all?** No — confirmed directly in §2's "who sets
what" summary: `ParseFiler.ProcessAnalysis` (`ParseFiler.cs:263-298`) never assigns `SenseRA` or
`CategoryRA` on any analysis or morph bundle it creates. This is not merely "the comparison ignores
a field that happens to be null on both sides" — the parser's C# `ParseMorph` value type
(`ParseResult.cs:154-227`) **has no `Sense` property at all**, and `ParseAnalysis` has no `Category`
property either; the omission is baked into the wire/value-type shape upstream of the comparison
function, not just into the comparison function's field list. Comparing `SenseRA` in
`MatchesIWfiAnalysis` would therefore be comparing a real human judgment against a structurally-absent
parser field — always false whenever a human had recorded a sense, which would make
`NumUserApprovedAnalysesMissing` overcount by exactly the number of Sense/Category choices a human had
made, on every single word the parser otherwise analyzed correctly. FieldWorks itself is aware the
parser can't supply a sense and papers over it for *display* purposes with a separate, explicitly
best-effort virtual property, `WfiMorphBundle.DefaultSense`: "If we have a sense return it; if not,
and if we have an MSA, return the first sense (with the right MSA if possible) from the indicated
entry" (`liblcm/src/SIL.LCModel/DomainImpl/OverridesLing_Wfi.cs:818-839`, **VERIFIED**) — a fallback
never used by `MatchesIWfiAnalysis` or `ParseFiler`, only by UI code that needs to show *something*
(e.g. `MorphologyListener.cs:635`, `mb.SenseRA = mb.DefaultSense`, in a different, human-editing
context).

**Conclusion for Q3: ignoring Sense is deliberate and structural, inherited from a 62-year-old-by-2024
interface contract that never carried a sense field, and independently confirmed by the fact the
parser's own C# value types have no field to hold one.** Ignoring Category is the same story but
slightly weaker: Category is nominally settable on `WfiAnalysis` (and the human UI does set it,
`SandboxBase.GetRealyAnalysisMethod.cs:383`), but the parser never sets it either, and — see §5 below
— word-level Category is *also* not compared by the *other* FieldWorks equality routine's sibling
concept in the same way, so there's no internal FieldWorks precedent treating word-level Category as
parser-derivable content at all.

## 4. How does approval actually work, mechanically?

"Approve and Move Next" resolves to `FocusBoxController.ApproveAnalysis`
(`FieldWorks/Src/LexText/Interlinear/FocusBoxController.ApproveAndMove.cs:374-409`, **VERIFIED**),
reached from `OnApproveAndMoveNext` → `ApproveAndMoveNext` → `ApproveAnalysis(SelectedOccurrence,
false, fSaveGuess)` (`:38-46,526-529`). Inside `ApproveAnalysis`:

1. `InterlinWordControl.GetRealAnalysis(fSaveGuess, out obsoleteAna)` (`:380`) builds a fresh
   `AnalysisTree` from whatever is currently selected in the Sandbox — this is
   `SandboxBase.GetRealAnalysis`, whose body is `SandboxBase.GetRealyAnalysisMethod.cs`, and which is
   exactly where `CategoryRA`, `MorphRA`, `MsaRA`, `SenseRA`, and `InflTypeRA` all get set from the
   user's UI selections (§2, §3).
2. `SaveAnalysisForAnnotation` (`:149-165`) attaches the resulting analysis to the text occurrence.
3. `FinishSettingAnalysis` (`:132-147`) does the actual approval: **`Cache.LangProject.DefaultUserAgent.SetEvaluation(newWa, Opinions.approves);`** (`:145`, **VERIFIED**).

`CmAgent.SetEvaluation` (`liblcm/src/SIL.LCModel/DomainImpl/OverridesCellar.cs:491-516`, **VERIFIED**)
is the C# port of the `SetAgentEval` stored procedure (doc-comment at `:487-488`): it removes any
existing `Approves`/`Disapproves` evaluation by this agent from `analysis.EvaluationsRC`, then adds
the agent's singleton `ApprovesOA` (or `DisapprovesOA`) `CmAgentEvaluation` object to
`EvaluationsRC` if the opinion is approve (or disapprove).

**What does a human actually assert?** A single boolean claim — "this whole `WfiAnalysis` object, as
it currently stands, is correct" — applied to `newWa` as one indivisible unit
(`FocusBoxController.ApproveAndMove.cs:145`). There is no per-field or per-morph-bundle approval
primitive anywhere in this path: whatever `GetRealAnalysis` happened to populate (segmentation, MSAs,
sense choices, category, features) is what gets approved together, in one `SetEvaluation` call.
`FocusBoxController.IsFullyAnalyzed` (`:239-300`) independently confirms that Sense
(`InterlinLineChoices.kflidLexGloss` → `WfiMorphBundleTags.kflidSense`, `:271-274`) and word-level
Category (`kflidWordPos` → `wa.CategoryRA == null`, `:287-289`) are each optional, separately-displayed
UI *line choices* the analyst can enable or disable independently of approval — reinforcing that they
are additional judgments layered on top of the core segmentation/MSA judgment, not automatically
implied by it.

## 5. Settling the "how many comparisons" question

**There are at least two**, and this pass found the second one the prior pass missed. Both are real,
both are live production code, and they weight fields differently:

**(A) `ParseAnalysis.MatchesIWfiAnalysis`** (`ParseResult.cs:102-133`) — compares a *parser-produced*
`ParseAnalysis`/`ParseMorph` value (which structurally cannot carry Sense or Category, §3) against an
*existing* `IWfiAnalysis`. Per-bundle test: `mb.MorphRA == current.Form && mb.MsaRA == current.Msa &&
mb.InflTypeRA == current.InflType && (current.GuessedString == null ||
EquivalentFormString(mb.Form, current.GuessedString))`. **Ignores Sense and Category** (cannot do
otherwise — the parser side has no such fields). All four confirmed call sites, at the exact lines the
prior pass alleged: `ParseFiler.cs:269`, `ParserReport.cs:385` and `:398`,
`ParserListener.cs:879` (**VERIFIED**, all four read directly).

**(B) `WfiWordformServices.DuplicateAnalyses` / `DuplicateBundles`**
(`liblcm/src/SIL.LCModel/DomainServices/WfiWordformServices.cs:318-345`, **VERIFIED**, full read) —
compares two *existing* `IWfiAnalysis` objects (both potentially human-created, both potentially
carrying Sense/Category) for the "Merge Duplicate Analyses" cleanup utility
(`FieldWorks/Src/LexText/Interlinear/DuplicateAnalysisFixer.cs:15,67`, wired to a
`Tools/Utilities` menu entry per its own commit message "LT-13869 Tools/Utilities/Merge Duplicate
Analyses", `liblcm` commit `1271d5ba6f467106aabd8a006f72b50e3ac3800e`, 2013-01-09, **VERIFIED**
commit metadata; the ticket's own reasoning is not recoverable locally — see "What I could not
verify"). Its per-bundle test (`DuplicateBundles`, `:338-345`) is `bundle1.SenseRA != bundle2.SenseRA
|| bundle1.MsaRA != bundle2.MsaRA || bundle1.MorphRA != bundle2.MorphRA` → **fail**, else requires full
`Form` MultiString equality across every writing system either side has. Its per-analysis test
(`DuplicateAnalyses`, `:318-336`) additionally requires `wa1.CategoryRA == wa2.CategoryRA` (`:335`)
and bails out (returns *not-duplicate*, conservatively) if either analysis has any
`CompoundRuleApps`/`InflTemplateApps`/`Stems`/`Derivation`/`MsFeatures` content (`:329-334`,
comment: "these fields are currently unused, but play safe and don't merge if at some point they have
data"). **It does not compare `InflTypeRA` at all** — not present anywhere in `DuplicateBundles`
(**VERIFIED** by full read).

**These two routines disagree on exactly the two fields the task flagged**: (A) ignores Sense/Category
and checks InflType; (B) checks Sense/Category and ignores InflType. They are not really in tension as
*implementations* — they answer different questions (A: "does this cross-representation parser guess
correspond to an existing human/parser analysis," where one side structurally cannot carry Sense/
Category; B: "are these two already-real `IWfiAnalysis` objects exact duplicates of each other,"
where both sides can carry everything) — but they are direct evidence that **FieldWorks itself has
never converged on a single answer to "what makes two analyses the same,"** and a maintainer picking
up (B)'s field list as "the more complete/correct" definition and applying it to the parser-comparison
use case (A) would silently break `NumUserApprovedAnalysesMissing` by requiring the parser to match a
Sense value it never produces. The prior investigation that reported "only found `MatchesIWfiAnalysis`"
did not search deeply enough for a second comparison, but was not wrong that (A) is the one actually
used for parser-vs-human agreement; (B) is the one it missed, and it is used for a genuinely different
purpose (dedup of existing objects, not parser-result matching).

## 6. What can PanGloss express?

PanGloss's `AnalysisIdentity` (`PanGloss/rust/crates/pg-assess/src/identity.rs:36-44`, canonical
definition; re-exported and duplicated verbatim in the `pg-parse` crate at
`PanGloss/.claude/worktrees/crp-analysis-identity/rust/crates/pg-parse/src/identity.rs:47-55`, with a
doc-comment there explaining the module moved from `pg-assess` to `pg-parse` so the recipe runtime
could share one projection rather than let "the one failure this module exists to prevent" — two
definitions of "the same analysis" drifting apart — happen inside PanGloss itself, `identity.rs:16-25`,
**VERIFIED**) is:

```rust
pub struct AnalysisIdentity {
    pub morphemes: Vec<MorphemeKey>,   // Option<String>, keyed by MorphemeInfo::xml_key
    pub root_index: i32,
    pub category: Option<String>,
}
```

`morphemes` is a per-morpheme stable key which the doc-comment states directly is **"the MSA GUID on
the LibLCM path"** (`identity.rs:37-38`, **VERIFIED**) — i.e. the identity of each morpheme slot is
its `MoMorphSynAnalysis`, the same object `WfiMorphBundle.Msa` points at, not its `LexSense`. `category`
is present — PanGloss **can** express a word-level category, contrary to a naive reading of the task's
hypothesis; it is the stable symbol id of the compiled part-of-speech feature value
(`identity.rs:118-134`, `category_key`).

**Per-morpheme sense is the field PanGloss genuinely cannot express in this struct**, and this is not
a gap that could be quietly closed — it is downstream of a real ambiguity FieldWorks itself never
resolves. `MoMorphSynAnalysis` can be referenced by more than one `LexSense`
(`LexSense.MorphoSyntaxAnalysis` is a `rel`, i.e. a reference, not an owning link:
`MasterLCModel.xml:2913`, **VERIFIED**; a live FieldWorks test exercises exactly this, comment "Add
second sense; same msa", `FieldWorks/Src/xWorks/xWorksTests/ConfiguredXHTMLGeneratorTests.cs:796`,
**VERIFIED**). FieldWorks' own HermitCrab loader resolves an MSA back to *a* sense (for gloss display)
by taking the **first** matching sense and discarding the rest:

```csharp
public ILexSense SenseWithMsa(IMoMorphSynAnalysis msa)
{
    return (from sense in AllSenses where sense.MorphoSyntaxAnalysisRA == msa select sense).FirstOrDefault();
}
```
— `liblcm/src/SIL.LCModel/DomainImpl/OverridesLing_Lex.cs:1054-1057` (**VERIFIED**), consumed by
`HCLoader.GetGloss` (`FieldWorks/Src/LexText/ParserCore/HCLoader.cs:910-914`, **VERIFIED**) —
FieldWorks' *own* HermitCrab integration, not just PanGloss's port. PanGloss's `sense_gloss`
(`PanGloss/rust/crates/pg-grammar/src/compile/lexicon.rs:165-171`, **VERIFIED**) is an explicit,
documented port of exactly this behavior — `.find(|s| s.msa.as_deref() == Some(msa))`, first match,
same collapse. So the underlying data model, in FieldWorks itself, already treats "which sense goes
with this MSA" as ambiguous whenever an entry has two senses sharing one MSA, and resolves it with a
non-authoritative, order-dependent default (`WfiMorphBundle.DefaultSense`, §3) rather than a stored
fact. PanGloss's `AnalysisIdentity` inherits this collapse structurally: its `morphemes` vector is keyed
on the MSA, so two morph choices that a human would record as different (`SenseRA` = sense 1 of an
entry vs. sense 2 of the same entry, same MSA) are **the same key in PanGloss's identity** and
indistinguishable by any comparison built on top of it.

**Conclusion for Q6: PanGloss can express word-level category (the task's hypothesis that it could not
is not borne out), but it cannot express per-morpheme sense as a distinct identity field, and this is
not merely an omission in `AnalysisIdentity` — it reflects a genuine sense/MSA ambiguity that already
exists in the FieldWorks data model and that FieldWorks itself never fully resolves.** Any motif test
that wanted to assert "the parser chose the same *sense*, not just the same MSA, as the human" would be
unimplementable against PanGloss's current identity type without a model change on PanGloss's side
first (adding a stable sense key alongside the MSA key in `morphemes`) — and even then, an artifact
that FieldWorks corpora may not always resolve (multiple senses can validly share the same MSA with no
signal distinguishing which one a given token intends).

## 7. The practical recommendation for motif

Field-by-field, following `MatchesIWfiAnalysis`'s shape as the baseline and stating the cost of each
deviation:

| Field | Include in the pass/fail gate? | Cost of including | Cost of excluding |
|---|---|---|---|
| Morph count / segmentation | **Yes** | None — this is the floor of any analysis and both sides always have it. | N/A |
| Per-morph root/allomorph identity (`MorphRA`/PanGloss morpheme key resolving to the same `MoForm`) | **Yes** | None. | Excluding this would make the gate vacuous. |
| Per-morph MSA (`MsaRA`/PanGloss morpheme key) | **Yes** | None — both sides always produce this; it is the field FieldWorks' own 2002-era contract was built around. | Excluding it collapses category-bearing distinctions the parser is specifically supposed to get right (e.g. is this affix analyzed as derivational-MSA-X or inflectional-MSA-Y). |
| `InflType` | **Yes**, matching FieldWorks | Small false-negative risk: an irregular-form gloss link the parser sets correctly but that differs cosmetically from a human's choice would fail the gate. | Excluding it silently accepts an analysis that used the wrong irregular-inflection variant as if it were the same analysis — a real linguistic difference (`MasterLCModel.xml:4719-4723`: "the link to the gloss in the inflection type" is itself content, not decoration). |
| Guessed-string handling (`EquivalentFormString`) | **Yes, and needed** — without it, guessed-root analyses that differ only in the guessed surface string would spuriously collide, since the parser reuses one generic placeholder `MoForm` object across guesses (`HCParser.cs:413-415`, `allomorph.Guessed`) and only the string disambiguates them. | Minor false-negative risk: a guess that is linguistically equivalent to the human's real root but rendered with different orthography/casing would fail purely on string mismatch (`EquivalentFormString` does an exact `.Text ==` compare, `ParseResult.cs:135-143`, no normalization). | Excluding the check entirely (comparing only `mb.MorphRA == current.Form`, which for two different *guesses* both point at the same shared placeholder) would make every guessed analysis for a given word "the same," a real false positive. |
| Per-morph `Sense` (`SenseRA`) | **No — report as a diagnostic, do not gate on it** | Gating on it would fail every word where the parser produced a structurally correct segmentation/MSA/InflType but (inevitably) supplied no sense, since `ParseFiler` never sets `SenseRA` (`ParseFiler.cs:263-298`) — this would make the "parser found what the human approved" metric measure something the parser cannot do by design, exactly the failure mode `NumUserApprovedAnalysesMissing` was built to avoid. It is also unimplementable against PanGloss's `AnalysisIdentity` (§6) without a model change, and even with one, FieldWorks' own sense/MSA ambiguity (§6) means the "correct" sense is sometimes genuinely unrecoverable from the data. | Excluding it means motif's test suite would call "the same" two analyses that a linguist glossing the text would call meaningfully different if they chose distinct senses of a polysemous root — this is the one place where FieldWorks' own `MatchesIWfiAnalysis` calls two analyses "the same" that a linguist would not. Surfacing it as a non-gating diagnostic (report which sense, if any, the human recorded, and whether the parser's chosen entry has only one candidate sense for that MSA — a case where the "correct" sense actually is recoverable) captures the useful signal without a false-failing gate. |
| Word-level `Category` (`WfiAnalysis.CategoryRA`) | **No — report as a diagnostic, do not gate on it** | Same reasoning as Sense: `ParseFiler` never sets `CategoryRA` (**VERIFIED**, §2/§3), so gating on it fails every parser analysis unconditionally whenever a human recorded a category, which would be indistinguishable from a real parser defect in aggregate counts. FieldWorks' own dedup routine (B, §5) treats Category as constitutive when *both* sides are real `IWfiAnalysis` objects, but that is a same-representation comparison, not the parser-vs-human case motif is building. | Excluding it risks missing a genuine mismatch in the rarer case where the parser's morph-by-morph MSAs *do* let you derive a word-level category (e.g. no category-changing derivation occurred, so the category is just the root's own MSA category) but the parser's derived category differs from the human's recorded one. Recommend deriving a "computed category" from the morph chain purely as a diagnostic annotation, not as a gate, until there is a documented, tested rule in FieldWorks for how `CategoryRA` should relate to the morph bundle's MSAs (not found in this pass — see "What I could not verify"). |
| `MsFeatures` | **No** | `ParseFiler` never sets it; not compared by (A) at all. | Low — it is documented as a rare escape-hatch for suppletive/exceptional words (`MasterLCModel.xml:3917`), not a typical field either side populates. |
| `Derivation` / `Stems` / `CompoundRuleApps` / `InflTemplateApps` | **No** | Never set by `ParseFiler`; described in-schema as denormalized indices/shortcuts, not primary content (`MasterLCModel.xml:3922,3931,3936`). | Negligible for the parser-vs-human comparison motif is building. |

**Net recommendation**: adopt `MatchesIWfiAnalysis`'s field set (morph count, per-morph `Morph`/`Msa`/
`InflType` identity, plus guessed-string equivalence) as motif's pass/fail equality, because it is the
one comparison FieldWorks actually uses for the exact metric motif wants to reproduce
(`NumUserApprovedAnalysesMissing`), it is the one the parser's own output shape supports, and it is the
one PanGloss's `AnalysisIdentity` can actually express end to end (morpheme keys = MSA keys, root
position, category). Layer Sense and word-level Category on top as **reported-but-non-gating**
diagnostics, precisely because they are the two places this research found a genuine, sourced tension
between "what FieldWorks' gate accepts as the same" and "what a linguist would insist on distinguishing"
— motif should not silently inherit FieldWorks' blind spot, but it also should not build a gate around
data neither the parser nor (for Sense specifically) PanGloss's identity model can supply.

## What I could not verify

- **The actual maintainer rationale behind LT-13869** ("Tools/Utilities/Merge Duplicate Analyses",
  `liblcm` commit `1271d5ba6f467106aabd8a006f72b50e3ac3800e`, 2013-01-09). The commit message is one
  line plus a Gerrit Change-Id (`Change-Id: I8614ceeae242bc001a4de6bf7d4e947df9a24c45`) with no
  discussion of *why* `DuplicateAnalyses` chose to compare Sense/Category but not InflType; the
  original Jira ticket (LT-13869) is not present in this checkout and I did not have access to SIL's
  Jira to read it. I did not find any code comment addressing this specific asymmetry with (A).
- **SIL.Machine's actual `WordAnalysis.Equals` implementation.** PanGloss's own doc-comments
  (`identity.rs:5`, `CONTEXT.md:234`) describe `AnalysisIdentity` as "the versioned canonical
  projection of C# Machine `WordAnalysis.Equals`" and cite `Morpher.cs:637`, but SIL.Machine is a
  separate upstream repository not checked out on this machine (`grep`/`find` for `Morpher.cs` or any
  `*Machine*` path in both `FieldWorks` and `liblcm` found nothing). I could not independently confirm
  that C# method's exact field list; everything I say about it is **INFERRED** from PanGloss's port,
  not independently read from its source.
- **A documented rule for deriving `WfiAnalysis.CategoryRA` from the morph bundle chain.** I searched
  for a canonical "compute the word's category from its morphs" function (the kind of thing that would
  justify treating Category as fully redundant with the MSAs) and did not find one in either
  `FieldWorks` or `liblcm`; the closest evidence is that the human Sandbox UI sets `CategoryRA`
  directly from a UI selection (`SandboxBase.GetRealyAnalysisMethod.cs:383`) rather than computing it,
  and interlinear import sets it from imported data (`BIRDInterlinearImporter.cs:898,922`) — I found
  no evidence either way of an authoritative derivation rule, so my §7 recommendation to treat computed
  category as diagnostic-only is a conservative default, not a confirmed absence of such a rule
  elsewhere in the codebase I did not search (e.g. dictionary/publication-view code, which I did not
  audit for this).
- **Whether `EquivalentFormString`'s exact-text comparison (`ParseResult.cs:135-143`) has ever caused
  real false negatives in production.** I found the code and its plain semantics (no normalization,
  no case-folding) but no test, bug report, or comment discussing observed failures from it.
- **PanGloss's non-worktree main-tree state for `pg-parse`.** The main (non-worktree) `PanGloss`
  checkout has `identity.rs` only under `pg-assess`, not `pg-parse`
  (`find rust/crates -iname identity.rs` returns exactly one hit outside `.claude/worktrees/`); the
  `pg-parse`-owns-it version I also read and quoted lives only in the `crp-analysis-identity` worktree
  (`.claude/worktrees/crp-analysis-identity/rust/crates/pg-parse/src/identity.rs`), which per its own
  doc-comment (`identity.rs:16-25`) is mid-migration work moving the module from `pg-assess` to
  `pg-parse`. I could not determine from the repository state alone whether that worktree's change has
  since landed on PanGloss's trunk, is still in flight, or was abandoned; I treated both versions as
  representative of the same design (their field lists and logic are identical) but flag that the
  crate-ownership question is unresolved as of this research.
