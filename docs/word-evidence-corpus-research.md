# Curated word-evidence corpus research

Status: research and domain analysis; not an approved design.

Date: 2026-07-28.

## Why this changes the project

The grammar-assessment loop assumes a maintained body of words that are known to parse correctly,
known not to parse, known to permit particular ambiguities, or known to expose a regression. That
body is not merely a word list. It needs real text context, manual analysis, executable parser
expectations, parser-run evidence, review judgments, revision history, and semantic commands.

The central product object is therefore a **curated word-evidence corpus**.

## Research method

Eight Luna tracks were requested across FieldWorks, LibLCM, PanGloss, Machine, FWLite/LexBox,
Harmony, LCAtom, and primary external systems. Seven returned usable research. The repository-
ownership task was blocked by the missing Windows sandbox helper and contributed no evidence.
Agent claims were checked against local source where possible.

Relevant local revisions are recorded in `fwLite-pangloss-sota-research.md`; this pass used the same
FieldWorks, LibLCM, Machine, Harmony, and FWLite revisions and PanGloss at
`82ca3c0fbd4ad62a88139ca075d71d7d21319c94`.

## Existing FieldWorks concepts

### `WfiWordSet` already names the original need

LibLCM owns `MorphologicalData.TestSets`, a collection of `WfiWordSet`. The model comment says:

> WfiWordSets are a collection of words that the user might want to use to test his word grammar as
> he adjusts morphological rules and the lexicon.

Source: `liblcm/src/SIL.LCModel/MasterLCModel.xml:3530-3534`.

A `WfiWordSet` has a name, description, and `Cases` reference collection of `WfiWordform`
(`MasterLCModel.xml:4595-4610`; generated `IWfiWordSet.CasesRC`). FieldWorks can import plain word
files into a set (`FieldWorks/Src/LexText/ParserUI/WordImporter.cs` and `ImportWordSetDlg.cs`).

This is a useful precedent and selection mechanism, but it cannot be the richer corpus authority:

- membership is by wordform/type, not occurrence;
- the same surface form in two contexts collapses to one wordform;
- it contains no expected, forbidden, allowed, or unresolved analysis policy;
- it has no parser/grammar/run identity;
- it does not retain gold revisions or adjudication; and
- deleting/merging wordforms affects membership independently of source occurrences.

### Text occurrences are positional, not durable entities

The text-analysis graph is:

```text
StText
  -> StTxtPara
     -> Segment
        -> ordered Analyses
           -> WfiWordform | WfiAnalysis | WfiGloss
```

`AnalysisOccurrence` identifies an occurrence operationally as a `Segment` plus an index into
`Segment.AnalysesRS` (`liblcm/.../DomainServices/AnalysisOccurrence.cs:25-76`). The segment is a
GUID-bearing object; the occurrence itself has no independent GUID.

Consequences:

- source span or `(segment GUID, index)` is not automatically stable across text edits/reparsing;
- the same `WfiAnalysis` may be referenced by many occurrences;
- replacing one occurrence's analysis reference need not mutate other occurrences; and
- historical evidence must never silently reattach to the next matching surface form.

A portable evidence case therefore needs its own case identity plus an **occurrence anchor** containing
at least the source text/paragraph/segment identities, index/span, surface and context fingerprint,
and anchoring status. After edits it is `resolved`, `orphaned`, or `ambiguous`; automatic reanchoring
is evidence to review, not identity.

### Manual and parser judgments already coexist

A `WfiWordform` owns alternative `WfiAnalysis` objects. Each analysis can carry independent opinions
from human and computer agents through `SetAgentOpinion`/`GetAgentOpinion`
(`liblcm/.../InterfaceAdditions.cs:2624-2636`). `LangProject.DefaultParserAgent` is a distinct agent.
`AnalysisGuessServices` prioritizes and distinguishes human approval, parser approval, and
human/parser disagreement.

This supports the state:

```text
manual analysis: correct and human-approved
parser output: missing it or producing a different analysis
```

However, parser-agent opinion is **observed parser state**, not automatically gold policy. The corpus
must distinguish:

- a parser produced or approved an analysis;
- a human accepts an analysis;
- a regression case requires an analysis;
- a regression case forbids an analysis; and
- an alternative is legitimate but not required.

Reusing `DefaultParserAgent` approval as “expected” would erase that distinction.

## Maxwell/Naylor workflow

FieldWorks already contains much of the interaction pattern:

- Run Tests extracts unique wordforms from the current text, a genre, or all texts
  (`ParserListener.cs:569-655`).
- Parser results are matched structurally to existing analyses and classified against user opinions
  (`ParserReport.cs` and `ParseAnalysis.MatchesIWfiAnalysis`).
- Saved JSON `ParserReport`s contain per-word and aggregate counts, timing, errors, comments, and
  opinion mismatches (`ParserReport.cs:145-206`).
- Two reports can be selected and arithmetically diffed (`ParserReportsDialog.xaml.cs:115-152`).
- Recent John Maxwell commits `6ef9a2ccb`, `dfac02b9a`, and `9ac58c032` add/fix changed-analysis
  counting and update state.
- Report UI can navigate to the wordform's analyses and reparse under current project state
  (`ParserReportDialog.xaml.cs:60-92`).
- HermitCrab/XAmple traces explain rule paths, success, and failure.
- Jason Naylor authored the Grammar Debugger UTF-8 commit `d3ccdc2d6`; Maxwell's direct trace
  precedent includes `036592bb5`.

What is missing:

- exact text occurrence linkage—the report links to a wordform GUID, not an occurrence;
- grammar, importer, parser configuration, and project revision identity;
- persistent structural identities of the actual analyses;
- explicit required/forbidden/allowed policies;
- curated-case and gold-revision identity;
- reproducible historical reparse;
- trace linkage to a saved run; and
- semantic reasons for changed results.

The existing `ParserReport` boundary is an excellent adapter seam, not the final corpus schema.

## FWLite and Harmony reality

FWLite's MiniLcm, CRDT, bridge, routes, snapshot, and history surfaces are currently lexical. They
cover entries, senses, examples, translations, POS, semantic domains, publications, morph types,
and related metadata. They do not model texts, segments, wordforms, analyses, glosses, parser agents,
word sets, or parser runs. The `.fwdata` synchronization service has no text-analysis phase.

Therefore adding `AddCorpusOccurrence`, `SetManualAnalysis`, `AddExpectedAnalysis`,
`MarkParserMismatch`, and `AddToRegressionSet` is not a small extension of the lexical API. It is a
new analysis-domain surface with ownership, ordered occurrence anchoring, analysis identity,
provenance, deletion, repair, bridge, and conformance requirements.

Harmony can synchronize typed entities and give accepted state history, but it must not collapse the
layers into one “word analysis” object. Raw parser outputs, traces, timings, cached diffs, and derived
mismatch status are artifacts or attachments, not user-authored canonical semantic changes.

## Domain model

Use these terms precisely:

- **Surface form** — normalized orthographic string.
- **Wordform** — FieldWorks type-level object grouping occurrences of a form.
- **Occurrence** — one anchored appearance in a versioned text context.
- **Manual analysis** — human-authored FieldWorks linguistic analysis.
- **Analysis candidate** — one parser- or human-produced structural interpretation.
- **Analysis identity profile** — versioned rules for structural equivalence.
- **Gold case** — curated occurrence/test input plus correctness policy.
- **Required analysis** — must be present.
- **Forbidden analysis** — must not be present.
- **Allowed alternative** — legitimate but not required.
- **Preferred analysis** — annotation preference; not necessarily exclusive.
- **Unresolved case** — recognized disagreement or missing judgment.
- **Parser run** — immutable output under pinned grammar/engine/import/budget inputs.
- **Comparison** — derived relation between gold policy and a parser run.
- **Gold revision** — explicit reviewed successor to prior gold policy.

The dangerous term is “word analysis”; it conflates the occurrence, reusable FieldWorks analysis,
parser candidate, and correctness judgment.

## Invariants

1. The same surface form in different contexts may have different cases and accepted analyses.
2. Parser output is evidence, never gold solely because it was produced.
3. Manual correctness and parser coverage are independent dimensions.
4. Gold cases and parser runs are immutable revisions; correction creates successors.
5. Required, forbidden, allowed, preferred, and unresolved are distinct.
6. Empty expected output must distinguish ungrammatical, uncovered, out-of-scope, invalid, and
   unresolved.
7. Text edits never silently retarget a historical case.
8. Analysis identity-profile changes produce migration/reidentification, not fake regressions.
9. Running tests never rewrites gold.
10. A word-level correction that changes project state is an explicit semantic proposal, not a side
    effect of approving a grammar proposal.

## Semantic operation families

### Project/text operations

- `CreateCuratedTestText`
- `AddTextOccurrence`
- `ChangeOccurrenceAnalysis`
- `RetireOccurrence`
- `ReanchorOccurrence`
- `AddWordformToTestSet`
- `RemoveWordformFromTestSet`

These mutate FieldWorks project state and must lower through an analysis-aware FieldWorks adapter.
`AddTextOccurrence` includes text placement, writing system, wordform linkage, and the selected manual
analysis. It is not merely “create WfiWordform.”

### Gold-policy operations

- `CreateGoldCaseFromOccurrence`
- `AddRequiredAnalysis`
- `RemoveRequiredAnalysis`
- `AddForbiddenAnalysis`
- `RemoveForbiddenAnalysis`
- `AddAllowedAlternative`
- `SetCaseScope`
- `MarkCaseUnresolved`
- `ProposeGoldRevision`
- `AcceptGoldRevision`
- `RejectGoldRevision`

A gold revision must link predecessor, rationale, evidence, reviewer, and identity profile.

### Evidence/review operations

- `RecordParserRun` references an immutable run artifact and its exact inputs.
- `RecordCaseJudgment`
- `ClassifyMismatch`
- `AddEvidenceAnnotation`
- `AdjudicateDisagreement`

`ClassifyMismatch` is review state over a derived comparison. Recomputing a comparison is not a
semantic user change.

## SOTA practices

The strongest pattern is layered rather than one universal gold file:

1. immutable/versioned source occurrences;
2. manual linguistic analyses;
3. executable expectations;
4. generated run profiles;
5. append-only review/release history.

DELPH-IN `[incr tsdb()]` distinguishes skeleton inputs, generated profiles, and treebank decisions;
its result comparison separates shared/current-only/gold-only analyses.
[TSDB schema](https://delph-in.github.io/docs/tools/TsdbSchemaRfc/) and
[PyDelphin](https://pydelphin.readthedocs.io/en/latest/guides/itsdb.html).

GiellaLT maintains reference text, missing-form regression, YAML morphology/paradigm tests, negative
cases, and visible expected failures.
[GiellaLT testing](https://giellalt.github.io/lang/common/developingwork.html).

Universal Dependencies contributes token/sentence metadata, validation, guideline versioning, and
release discipline, but CoNLL-U alone does not cover alternate analyses, provenance, or adjudication.
[UD guidelines](https://universaldependencies.org/guidelines.html).

Keep a full permanent suite plus tagged smoke, change-focused, phenomenon, and rotating subsets.
Selection optimizes execution; it never deletes canonical cases.

## Repository-placement options

### Option A — extend FieldWorks/LibLCM as the complete authority

Add a first-class GUID-bearing test-case/gold model beside `WfiWordSet`, tied to text occurrences and
analyses. Extend Parser Reports and traces to use it.

Advantages:

- strongest integration with real texts/manual analysis;
- native John Maxwell/Jason Naylor workflow;
- one `.fwdata` authority; and
- direct offline FieldWorks use.

Costs/risks:

- LibLCM model evolution and FieldWorks release coupling;
- gold/run/workflow concepts may overburden the linguistic project model;
- external PanGloss portability requires export; and
- FWLite still needs the entire text-analysis bridge.

Best long-term if FieldWorks itself must provide the complete authoring/review product.

### Option B — layered across existing repos (recommended)

Use real FieldWorks texts and manual analyses as project authority. Keep `WfiWordSet` as a type-level
selection/index. Define a portable gold-case/run contract in PanGloss. Add an application analysis
domain in FWLite/LexBox for semantic commands, anchoring, review, and Harmony synchronization once
text analysis is in scope.

Advantages:

- respects existing boundaries;
- keeps parser comparison and identity near PanGloss;
- grounds every case in FieldWorks without forcing run history into LibLCM;
- permits FieldWorks-native and web review surfaces; and
- no new repository is required initially.

Costs/risks:

- cross-artifact identity and recovery are difficult;
- offline behavior spans project and evidence records;
- FWLite analysis support is substantial new work; and
- FieldWorks-only users need an adapter/UI path to the portable records.

Recommended ownership allocation, subject to design approval:

- FieldWorks/LibLCM: text, wordform, manual analysis, human/parser opinions, test-set membership.
- PanGloss: analysis identity profile, portable gold/run schemas, execution, comparison, traces.
- FWLite/application: semantic command orchestration, case anchoring/reanchoring, review, history UI.
- Harmony: accepted synchronized application/domain records, not raw run blobs.
- LCAtom: cross-repository contract research and ADRs, not a revived runner.

### Option C — standalone corpus repository/files

Store source excerpts, cases, gold revisions, and run manifests in a dedicated version-controlled
corpus repository; link back to FieldWorks identities and import/export to PanGloss.

Advantages:

- strongest portability, review diffs, releases, and CI;
- independent schema evolution; and
- language data can have its own governance/access policy.

Costs/risks:

- duplicates or snapshots FieldWorks content;
- anchors drift as texts change;
- normal FieldWorks editing can bypass the corpus; and
- synchronizing manual analysis back into `.fwdata` becomes a two-authority problem.

Useful as an export/release format, not recommended as the initial operational authority.

## Recommended staged approach

### Stage 0 — model and export, no new project authority

- designate one or more real FieldWorks texts as curated test sources;
- use existing `WfiWordSet` for convenient type-level selection;
- extract occurrence context plus human analyses;
- create portable PanGloss gold cases with explicit anchors and anchoring status;
- run comparisons without writing back to FieldWorks; and
- prove required/forbidden/allowed/unresolved semantics and history.

### Stage 1 — FieldWorks integration

- add a corpus adapter at the completed `ParserReport` boundary;
- persist exact run inputs and structural outputs;
- navigate from changed case to text occurrence and wordform analysis;
- capture traces on demand; and
- require explicit gold revisions.

### Stage 2 — semantic project edits

Add one analysis-aware FieldWorks command path:

```text
ChangeOccurrenceAnalysis
  -> isolated candidate `.fwdata`
  -> PanGloss corpus comparison
  -> review
  -> accepted project change
```

Then add `AddTextOccurrence` and `CreateGoldCaseFromOccurrence`. Do not begin with arbitrary text
CRUD or the entire interlinear model.

### Stage 3 — FWLite/Harmony collaboration

Only after occurrence anchoring, LibLCM round-trip, parser-agent coexistence, unknown-client behavior,
and recovery are proven should FWLite/Harmony become authoritative for collaborative corpus edits.

## Research questions still requiring experiments

1. How often do segment GUIDs survive the specific FieldWorks edits and reparses used by this flow?
2. Can a case be reanchored deterministically using segment GUID, analysis GUID, surface/context
   fingerprint, and lineage without false matches?
3. Does `ParseAnalysis.MatchesIWfiAnalysis` provide enough structural identity for gold, or only for
   current FieldWorks report comparison?
4. Which user-agent opinion represents the project's manual gold when multiple humans disagree?
5. Are `ProjectReports` synchronized by the real FieldWorks/Chorus route, and with what merge/history
   behavior?
6. Can PanGloss import every analysis feature needed to compare manual `WfiAnalysis` with parser
   output, or must the gold contract preserve additional FieldWorks structure?
7. What is the smallest FieldWorks test text/fixture that demonstrates same-form contextual
   ambiguity and parser/manual disagreement?

## Provisional conclusion

Do not create a separate repository yet and do not overload `WfiWordSet`. Use the layered existing-
repo option for the first design: real FieldWorks occurrences and manual analyses, portable PanGloss
gold/run artifacts, and an application-level orchestration/history layer. Treat the corpus as a
first-class bounded context whose contract includes project edits as well as parser expectations.
