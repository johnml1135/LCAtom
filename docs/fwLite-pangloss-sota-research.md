# FWLite grammar + PanGloss: SOTA research and evidence ledger

Status: integrated research record for the decision grill.

Date: 2026-07-28.
Local source revisions used in this pass:

| Repository | Revision |
| --- | --- |
| `Motif` | `3f0dfadc5acc179adeb946e46c7a62be02b749c4` plus this uncommitted research update |
| `PanGloss` | `82ca3c0fbd4ad62a88139ca075d71d7d21319c94` |
| `FieldWorks` | `b8a2dd123aa6a5d0b95774ae74daa50e852932f8` |
| `machine` | `4c79ed0e055bb553e68359bcb81a8ad711134944` |
| `harmony` | `c858cb429231298aef564354b8ec2d5c87507287` |
| `languageforge-lexbox` | `da284fa8e628a7acfa76a080dabfc324272ce64e` |
| `liblcm` | `d564a719b1cce16c25ebea53a537393cb757f5d1` |

The relevant inspected source files were committed at those revisions. Dirty sibling repositories
contained unrelated untracked session exports, samples, backups, or experimental directories; those
were not treated as evidence unless explicitly named above.

This extends `fwLite-pangloss-verification-synthesis.md`, records the evidence gathered after that
checkpoint, and separates current capability, proposed work, owner decisions, and proof obligations.

## Method and evidence discipline

Eight independent Luna tracks examined PanGloss comparison seams; the FieldWorks-to-HermitCrab
pipeline; John Maxwell's visualization work; current Motif/Harmony/FWLite change formats; layered
contracts; SOTA grammar regression; human/AI review; and every grill question's researchability.
They were asked to prefer local source, specifications, official documentation, and primary papers.
Their reports were treated as leads, not authority, and local claims were spot-checked.

Two recurring agent conclusions were rejected:

- ADR 0013 remains authoritative. The cancelled Motif runner is not the implementation path.
  Motif's earlier assessment/effect designs are evidence, not permission to build a third mechanism.
- `OpaqueChange` retention is not forward-compatible application. Unknown changes are inert, and
  dependent later-change behavior remains unproved.

## New local primary-source findings

### FieldWorks already compares parser runs

John Maxwell's recent FieldWorks work is a direct precedent for the desired review experience:

- `ParserReport` is JSON-serialized and stores one `ParseReport` per word plus aggregate parse,
  zero-parse, error, timing, and user/parser-opinion counts
  (`FieldWorks/Src/LexText/ParserCore/ParserReport.cs`).
- `DiffParserReports` compares two saved reports by exact word key (`ParserReport.cs:212-273`).
- Run Tests now counts analyses changed since the prior parser-approved state per word and in total
  (commits `6ef9a2ccb`, `dfac02b9a`, and `9ac58c032`).
- Matching uses `ParseAnalysis.MatchesIWfiAnalysis`
  (`FieldWorks/Src/LexText/ParserCore/ParseResult.cs:102`).
- The UI selects reports, shows differences, navigates to analyses, and reparses a word
  (`ParserReportsDialog.xaml.cs` and `ParserReportDialog.xaml.cs`).

Reuse the workflow—saved runs, changed-word triage, drill-in, reparse—not the format unchanged. It
compares parser results with persisted `IWfiAnalysis` and report counts, not pinned grammar artifacts
with a versioned PanGloss cross-run analysis identity. It does not bind corpus, import loss, engine
profile, proposal, or reviewer decision.

### FieldWorks has complementary outcome and explanation surfaces

The likely “visualize what is happening” references are three related surfaces:

1. Parser Reports provide whole-run and per-word outcome comparison.
2. The XAmple Word Grammar Debugger transforms parser XML through XSLT into staged navigable HTML
   (`XAmpleWordGrammarDebugger.cs`, `WebPageInteractor.cs`, and
   `FormatXAmpleWordGrammarDebuggerResult.xsl`).
3. The HermitCrab trace viewer transforms a structured trace tree into expandable HTML with rules,
   strata, templates, success/failure, and failure reasons (`HCTrace.cs`, `FwXmlTraceManager.cs`, and
   `FormatHCTrace.xsl`).

The Grammar Debugger UTF-8 commit `d3ccdc2d6` was authored by Jason Naylor. Maxwell's direct
precedents are the three 2026 ParserReport changes and the improved HermitCrab partial-parse
explanation in `036592bb5`. The reusable design remains two-level: outcome delta, then optional trace.

### PanGloss has newer structured traces

PanGloss contains trace work not covered by the first synthesis:

- `pg-rules/src/trace.rs` defines trace types, sources, reasons, nodes, handles, and sinks;
- `pg-cli/src/trace_render.rs` renders them;
- `rust/tools/trace_diff.py` compares selected trace tuples; and
- `rust/docs/p12-tracemanager-design.md` documents fidelity gaps.

This strengthens explanation but does not fill the principal gap: no authoritative baseline/candidate
assessment with canonical added and removed analysis sets per occurrence. Trace differences must
explain outcome deltas, not define them. The source also warns that traced and untraced execution can
differ because tracing disables some equivalent-analysis merging; the comparison contract must name
the outcome-defining execution mode.

### The grammar pipeline is direct and testable

```text
LibLCM LcmCache
  -> FieldWorks HCLoader
  -> SIL.Machine HermitCrab Language
  -> Morpher.ParseWord
  -> Machine Word/WordAnalysis + optional Trace
  -> separate FieldWorks persistence into WfiAnalysis/MorphBundles
```

`HCLoader.Load(LcmCache, IHCLoadErrorLogger)` is the authoritative adapter
(`FieldWorks/Src/LexText/ParserCore/HCLoader.cs:30-35`). A successful Machine parse does not prove a
persisted FieldWorks `IWfiAnalysis`; an end-to-end proof needs parser-output and LibLCM read-back
assertions.

### `Optional` is real but needs controls

`MoInflAffixSlot.Optional` has two parser effects:

- HCLoader adds irregular null-affix rules only when a referring irregular type exists and the slot
  is required (`HCLoader.cs:1725-1729`);
- it copies the bit into the HermitCrab template (`HCLoader.cs:1731`); and
- Machine permits slot skipping only when optional
  (`machine/.../AnalysisAffixTemplateRule.cs:66-79,102-119`).

The proof therefore needs an ordinary item, an irregular item dependent on the null rule, required
and optional variants, untraced outcome comparison, and trace only as explanation.

Two complementary slices are needed:

- `NoDefaultCompounding` is a cleaner scalar control (`HCLoader.cs:92-96,235-249`).
- Phonological `Disabled` plus `OrderNumber` is the essential ordered control: HCLoader filters and
  orders the rules (`HCLoader.cs:302-307`) and Machine retains ordered phonological rules
  (`machine/.../Stratum.cs:100-105`). A feeding/bleeding pair should inform the sequence decision.

## SOTA practices worth adopting

No surveyed system supplies the whole workflow.

### GiellaLT/HFST

GiellaLT uses named YAML morphology cases with analyzer/generator artifacts, expected mappings, and
explicit forbidden forms. It supports analysis, generation, positive, and negative tests.

- [Morphological test data](https://giellalt.github.io/infra/infraremake/AddingMorphologicalTestData.html)
- [HfstTester](https://giellalt.github.io/tools/HfstTester.html)
- [Testing overview](https://giellalt.github.io/ling/testing.html)

Adopt phenomenon tags, positive and forbidden cases, bidirectional tests, and explicit—not default—
waivers for additional analyses. Keep corpus coverage separate from correctness.

### Apertium and Foma

Apertium/lttoolbox separates compile, analyze, generate, and exhaustive expansion. Foma exposes
finite-state lookup and path enumeration.

- [lttoolbox](https://github.com/apertium/lttoolbox)
- [Foma](https://fomafst.github.io/)

Adopt bounded paradigm expansion, analyzer/generator round trips, and raw engine paths as diagnostic
artifacts rather than canonical identities.

### DELPH-IN and Grammar Matrix

DELPH-IN profiles compare result sets as shared, current-only, and gold-only. This directly models
added analyses, removed analyses, overgeneration, undergeneration, and ambiguity change. Gold
replacement is a deliberate reviewed action.

- [Grammar Matrix regression testing](https://delph-in.github.io/docs/matrix/MatrixRegressionTesting/)
- [pyDelphin profiles](https://pydelphin.readthedocs.io/en/latest/guides/itsdb.html)

Adopt the set-comparison shape. Do not label candidate-only “worse” or baseline-only “better” without
a phenomenon-specific oracle or review.

## Recommended PanGloss artifacts

Recommended allocation, pending the ownership decision: PanGloss owns analysis computation and identity; the application owns proposals,
judgments, authorization, and canonical application.

The authoritative assessment should be versioned canonical data containing:

- pinned baseline and candidate artifact/import records;
- engine, importer, budget, normalization, and identity profiles;
- word-set digest, tokenization, and occurrence policy;
- asymmetric warnings and unsupported/dropped constructs;
- per-side errors, caps, timeouts, and incomplete status;
- per-occurrence baseline and candidate analysis sets;
- shared, candidate-only, and baseline-only identities; and
- a factual classification that does not silently decide “better.”

The first review view should combine FieldWorks' saved-run/drill-in workflow, DELPH-IN result sets,
optional FieldWorks/PanGloss traces, and language-facing context/segmentation/gloss layers.

Keep gold and triage separate: positive/forbidden fixtures, reviewer judgments, uncertainty,
regression/progression/unresolved labels, accepted alternatives, variety/register scope, explicit
gold updates, and AI provenance. Running comparison must never rewrite gold.

## Layered contracts

```text
proposal
  -> candidate materialization
  -> PanGloss assessment
  -> word judgments
  -> authenticated proposal decision
  -> accepted Harmony change
  -> separately recorded/retriable .fwdata materialization
```

Supporting standards have narrow roles:

- [RFC 8785 JCS](https://www.rfc-editor.org/rfc/rfc8785.html) gives deterministic JSON bytes but no
  Unicode normalization.
- [DSSE](https://github.com/secure-systems-lab/dsse) binds typed bytes to a signature but does not
  define authorization.
- [in-toto](https://in-toto.io/docs/specs/) supplies digest-addressed attestation subjects.
- [W3C PROV-O](https://www.w3.org/TR/prov-o/) can export lineage but does not define approval or sync.

Use separate proposal, candidate, assessment, judgment, decision, and accepted-commit identities. A
valid signature proves authenticity, not authority. Application policy decides who may approve.
Changed material inputs make evidence stale or incomparable while preserving its audit value.

Do not create another general event log. Harmony remains accepted-state change/sync. Where drafts,
evidence, and decisions synchronize remains open until rejection, offline, older-client, dependent-
change, and recovery behavior are tested.

## Human, linguist, and AI review

Speakers are best asked about contextual acceptability, meaning, naturalness, contrast, variety, and
register. Linguists are better positioned for segmentation, morpheme identity, features, rules, and
paradigm consistency. Present context, form, segmentation, gloss, translation, and optional features;
the [Leipzig Glossing Rules](https://www.eva.mpg.de/lingua/resources/glossing-rules.php) are a useful
baseline. Preserve uncertainty, alternatives, escalation, variety, and disagreement.

AI may retrieve evidence, explain, cluster, and prioritize. It must remain visibly AI-originated and
must not impersonate a speaker or authorize v1 changes. External processing is denied by default and
requires project/data policy. The [CARE Principles](https://www.gida-global.org/careprinciples) are
especially relevant to community-governed language data.

## What research answered before the grill

Answered or converted to facts:

- The proposed PanGloss v1 contract should compare two pinned artifacts, not accept a Harmony proposal.
- The proposed contract should make structured assessment authoritative and Markdown/HTML derived.
- Readiness and linguistic correctness remain separate.
- Harmony's hash does not bind approval to payload or actor.
- Cross-store atomicity is absent; separately durable, visible, idempotently retriable `.fwdata` materialization is a requirement, not a current capability.
- FieldWorks offers reusable run-diff and trace precedents.
- Broad generated grammar CRUD is not the first proof.

Must be answered by experiments:

- canonical analysis and occurrence identity;
- import-loss and incomplete-result invalidation;
- a correctness oracle with a deliberate regression;
- `NoDefaultCompounding`, controlled `Optional`, and feeding/bleeding ordering effects;
- accepted state through restart, two clients, `.fwdata`, read-back, FieldWorks edit, reverse sync;
- unknown-client/dependent-change behavior; and
- recovery after Harmony acceptance but `.fwdata` failure.

Residual owner decisions:

- whole-proposal decision unit and authorization;
- scorecard and protected-regression policy;
- reviewer roles and review accessibility bar;
- privacy, consent, community governance, and AI policy;
- draft/rejected record storage and synchronization;
- `.fwdata` authority/interoperability role;
- approval authentication and offline policy;
- ownership/maintainers; and
- v1 scope and release bar.

## Revised proof order

1. Complete R1–R4: define and fixture occurrence/analysis identity, import loss, incomplete
   outcomes, and a predeclared language-facing correctness oracle.
2. Only after R1–R4 pass, emit deterministic shared/candidate-only/baseline-only sets with one
   oracle-declared improvement and one deliberate regression.
3. Run FieldWorks controls: `NoDefaultCompounding`, controlled `Optional`, and feeding/bleeding
   `Disabled`/`OrderNumber`.
4. Implement one proven FWLite/Harmony value path and demonstrate restart, two-client sync, scratch
   `.fwdata`, reopen/read-back, reverse sync, and failure recovery.
5. Prove reject, revise, stale evidence, authenticated acceptance, canonical apply, retriable
   materialization, and offline behavior.

Only after these proofs should manifest expansion or a sequence primitive be selected.
