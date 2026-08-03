# Research findings — bidirectional diff and the test/coverage model

*2026-08-03. Grounding for
[the proposal](../proposal-2026-08-03-bidirectional-and-test-coverage.md) and the `[R]` items in
[grill-plan-a.md](../grill-plan-a.md). Three independent source investigations; claims below were
spot-verified against the cited code rather than taken on the reports' word, and one report's figure
is corrected here.*

---

## Thread 1 — bidirectional diff feasibility

### The substrate does not exist yet, and the gap is total

`SIL.Motif.Model/Snapshot/ObjectSnapshot.cs:24-30` is, in its entirety,
`{ CanonicalId, MultiUnicodeFields }`. `SnapshotFields.cs:16` declares exactly one field constant,
`lexical/sense/gloss`. `LexSenseSnapshotter` is the only snapshotter in the codebase.

So the Canonical Semantic Snapshot cannot currently represent ownership, references, sequence
position, scalars, rich text, `GenDate`, or `Binary` — which means **of the ten primitive verbs, the
snapshot substrate supports `set` on one field and none of `create`, `delete`, `addRef`, `removeRef`,
`move`, `reparent`, `merge`, or `replace`**, because their preconditions are absent from the type.

`Grep` for `TwoWayDiff|ThreeWayAssessment|LIS|MechanicalDiff` across `src/` returns zero hits outside
documentation. Bidirectional diff is not partly built; **1 of 473 in-scope rows** has a snapshotter.

The algorithm side is in better shape: `change-set-contract.md` §"Mechanical diff" specifies
exact-GUID two-way diff, three-way comparison producing a `ThreeWayAssessment`, and an O(n log n) LIS
with frozen tie-breaking and explicit move/edit-count formulas. It is specified in enough detail to
implement.

### Two `LcmCache` instances do coexist — proven, with a scale caveat

`liblcm/tests/SIL.LCModel.Tests/Infrastructure/Impl/PersistingLayerTests.BEPPortTests.cs:166-191`
holds `sourceCache` and `targetCache` live in nested `using` scopes, including `kMemoryOnly` on both
sides, via `LcmCache.CreateCacheCopy`. This is running NUnit coverage, and it partially answers
grill `A2`.

**Caveat the report did not flag:** the source is `CreateCacheWithNewBlankLangProj` — a blank project
created ex nihilo. Coexistence is proven; coexistence *at project scale, inside a live FieldWorks
process* is not. `implementation-plan.md:360` separately lists "in-memory parallel cache cloning"
under explicitly deferred.

> **Correction.** The report cites `CmObjectIdentityMap.cs:42-44` as "the identity map dictionary alone
> costs 12M for a middling-large project." Reading the comment, **12M is the memory *saved*** by a
> deliberate SRP violation — using one dictionary for two purposes — not the map's cost. The map is
> described only as "very large." Do not carry 12M forward as a cost estimate.

### GUID identity survives only same-lineage forks

`liblcm/src/SIL.LCModel/LcmGenerate/factory.vm.cs:67-70` mints a fresh GUID whenever the caller does
not supply one, and every ordinary `.Create()` call site uses that overload. `ConvertLexEntryType`
(`OverridesLing_Lex.cs:9731`) is `factory.Create()` → copy fields → `old.Delete()`.

So diff's sole matching key holds for Send/Receive-style divergence from a common ancestor, and **not**
for two independently-built projects. The contract already disclaims this ("Unrelated projects may
therefore produce delete/create operations for semantically similar objects"), but it bounds
"two projects can merge" more tightly than the proposal reads.

### What cannot be recovered from a state delta — corrected list

| Operation | Recoverable? | Evidence |
| --- | --- | --- |
| `set` / `clear` / `addRef` / `removeRef` | yes | value or reference delta is unambiguous |
| **`reparent`** | **yes** — *correcting an earlier claim of mine* | the GUID survives, so owner+position change is identifiable |
| `move` in an unordered or positional sequence | yes | the specified LIS handles it |
| **`merge`** | **no** | `CmObject.cs:701-715` deletes the source after copying and redirecting referrers — bit-for-bit the same delta as "delete A, edit B, redirect referrers" |
| **`replace` / subclass-convert** | **no** | literally create(new GUID) + copy + delete(old) + redirect |
| **`move` on an index-as-identity field** | **no, and worst of the three** | a move silently renames every later alpha variable, so the delta is indistinguishable from a bulk content edit to every subsequent rule — diff cannot tell "moved" from "N rules each edited" |

The contract's own ban on similarity inference removes the only tool that could disambiguate these.

### ADR 0006 — reinforced, with one real seam

"Read back, do not replay" is *reinforced*: a general `Diff(A,B)` is the same mechanism with scope
widened from one operation's footprint to a whole project, and ADR 0006 already anticipates two-way
diff and carves it out of the near-instantaneous promise.

The genuine seam is the one the proposal identified: a diff-derived Proposal's effects are
tautologically equal to the delta they were reverse-engineered from, so on first appearance it has no
independent expected-effects to drift *from*. The drift model was designed around authored intent
measured against observed effect. This is a second provenance class, not a contradiction.

### Cost

Unmeasured. Dominated by (1) loading two caches, (2) `EnsureCompleteIncomingRefs` — a first-touch
whole-project force-fluff, now unavoidable and doubled, and (3) snapshot construction across ~473 rows
where one exists today. LIS is cheap by comparison and will not dominate.

---

## Thread 2 — can a PanGloss result be compared to a `WfiAnalysis`?

**Yes — and this substantially de-risks `I35`, the proposal's highest-risk assumption.**

### FieldWorks already computes the failing-test definition, in production

`FieldWorks/Src/LexText/ParserCore/ParserReport.cs:380-390`:

```csharp
var opinion = wfAnalysis.GetAgentOpinion(userAgent);
if (opinion == Opinions.approves) {
    foreach (ParseAnalysis pAnalysis in result.Analyses)
        if (pAnalysis.MatchesIWfiAnalysis(wfAnalysis)) found = true;
    if (!found) NumUserApprovedAnalysesMissing++;
}
```

`NumUserApprovedAnalysesMissing` (`:349`) *is* "human-approved analyses the parser cannot produce."
The concept is not speculative; it ships. `ParseAnalysis.MatchesIWfiAnalysis`
(`ParseResult.cs:102-133`) does the structural comparison, over ordered `WfiMorphBundle`s compared by
`MorphRA` / `MsaRA` / `InflTypeRA`.

**Its two limits.** It is a *count*, discarding which analysis was missing. And it matches on live
in-process object references resolved by `Hvo` — session-scoped integers — so it cannot survive
serialization or comparison across runs.

### The GUID correspondence holds on the `.fwdata` path

`pg-grammar/src/compile/lexicon.rs:301,309` — `authored_id: entry.guid.clone()` and
`xml_key: guid.clone()`. On the `.fwdata`/snapshot path, PanGloss morpheme identity **is** the LibLCM
MSA GUID that a `WfiMorphBundle.MsaRA` points at.

PanGloss also already has the comparison machinery Motif would otherwise have to build:
`AnalysisIdentity` (`pg-assess/src/identity.rs:36-44`) resolving dense ordinals to stable source keys,
`identity_digest` as SHA-256 over JCS-canonical JSON, `AnalysisSet` deduplicating by digest with
proven discovery-order invariance, and `compare`/`GrammarDelta` joining set-wise into
added/removed/retained/annotation-changed. Digest collision on unequal identities is an integrity
error, never a match.

Notably, PanGloss's comparison is **strictly better than FieldWorks' own**:
`ParserReport.DiffParseReport` (`ParserReport.cs:418-428`) subtracts integer counts, so two analyses
replaced by two entirely different analyses diffs to zero. PanGloss reports that correctly as `Mixed`.

### The new risk: PanGloss identity is *coarser*, and that is the unsafe direction

`AnalysisIdentity` carries morphemes, root index, and category. A `WfiMorphBundle` carries **three**
references: `MorphRA` (allomorph), `MsaRA`, and `SenseRA`. PanGloss's identity has **no allomorph and
no sense identity**.

So two `WfiAnalysis`es differing only in which allomorph or which sense realized a morpheme **collapse
to the same PanGloss identity**. That produces *false agreement* — a manual analysis can be reported
as covered when the parser actually produced a different one. False disagreement would be safe; this
is not.

### Rules, strata, and templates have no retained GUID at all

`pg-assess/src/handoff.rs:28-33` states it as a design fact: stable FieldWorks IDs survive import for
lexical entries only. A mismatch caused by *which phonological or morphological rule fired* is not
nameable in FieldWorks terms from the report alone.

This lands directly on `I38`. "Every grammar feature has one word that parses in a sentence" needs
stable rule identity that does not exist, a per-word construct-provenance ledger that does not exist,
and a notion of "sentence" that does not exist — `AssessmentCase.input` is one word.

`pangloss coverage` today is **capability** coverage over *synthetic conformance fixtures only*
("never real-language data, per this repo's own hard rule"), not corpus coverage. It is not the
metric the proposal describes.

### Ambiguity is handled well

`diff_sets` categorizes a 1→2 analysis change as `AddedOnly` or `Mixed`, both `is_changed()`. Two
occurrences of the same surface form stay distinct cases — there is a test named
`two_cases_sharing_a_surface_form_stay_distinct`. So *"these 5 occurrences now need disambiguation"*
is directly enumerable as 5 stable case IDs.

**Conditional on the binding that does not exist:** occurrence → case ID. `AnalysisOccurrence` has no
independent GUID; it is a `Segment` GUID plus an index.

### Determinism is genuinely well handled

`model_fingerprint` pins canonical source plus compiler version; formatting changes do not move it,
though HC-XML attribute order does (a conservative false-different, never a false-same). A
deterministic logical-budget stop is distinguished from a machine-dependent wall-clock timeout, and a
report carrying any wall-clock stop is flagged `reproducible: false` rather than hiding it.
Memoization is order-invariant by construction; thread count does not perturb content. Engine
selection (`default` vs `foma`) genuinely can differ and is recorded as context rather than gated.

---

## Thread 3 — the LibLCM text and analysis model

### There is no durable occurrence identity, and the fragility is systemic

`AnalysisOccurrence` (`liblcm/src/SIL.LCModel/DomainServices/AnalysisOccurrence.cs:25-78`) is a plain
C# class, **not a `CmObject`**. No GUID, never persisted, constructed on demand. `Equals` and
`GetHashCode` are `(Segment, Index)`. `Segment.Analyses` is a *reference* sequence of the polymorphic
`IAnalysis` (`MasterLCModel.xml:268`).

On any edit `StTxtPara.ParseIsCurrent` goes false and `ParagraphParser` re-segments. Leftover
`Segment` objects are deleted (`ITextUtils.cs:565`), so segments are not stable across a reparse
either. Analyses are re-attached by a **best-effort heuristic** matching lowercased word string plus
positional index (`TryReuseAnalysis`, `ITextUtils.cs:910-1000`), whose own comment reads: *"Did we
find it at the exact expected place in the sequence? No... Apply various heuristics."*

`TextTag` and the discourse chart use the identical `BeginSegment`/`BeginAnalysisIndex` scheme
(`MasterLCModel.xml:657-664`). Positional identity is **systemic across the interlinear and discourse
model**, not a quirk of one helper class.

### The finding that restates the unit-test analogy

`FocusBoxController.ApproveAndMove.cs:374-409` — `ApproveAnalysis(occ, allOccurrences, fSaveGuess)`:

- repointing *other* occurrences is gated on `allOccurrences`, wired to an explicit *"Approve for
  Whole Text"* command;
- but `FinishSettingAnalysis` sits **outside** that branch and always runs (`:132-147`), doing
  `Cache.LangProject.DefaultUserAgent.SetEvaluation(newWa, Opinions.approves)`.

**So a "manual analysis" is two independent facts, not one:**

| | Fact | Identity | Durable? |
| --- | --- | --- | --- |
| **A** | *This `WfiAnalysis` is human-approved* | `WfiAnalysis` GUID, global to the project | **yes** |
| **B** | *This occurrence uses that analysis* | `Segment` + index | **no** |

Ordinary "Approve and Move Next" sets **A globally** while changing **B for one occurrence only**.

This is the most consequential result of the three investigations, and it splits the proposal cleanly:

- **"Unit tests" hang on Fact A**, which has a stable GUID and is already queryable. *That half is
  viable now, without solving the anchor problem.*
- **"Coverage" needs Fact B**, which has no durable identity. *That half is blocked on the occurrence
  anchor contract.*

### Agent opinions, with the GUIDs

`CmAgent` has `Human` (bool) plus owned `Approves`/`Disapproves` singletons; approval is expressed by
an analysis's `Evaluations` collection referencing that agent's singleton
(`OverridesCellar.cs:491-516`). `Opinions` is tri-state — `disapproves=0, approves=1, noopinion=2`
(`Enumerations.cs:761-769`) — so "disapproved" is distinct from "no opinion", for both humans and
parsers.

Well-known fixed GUIDs, created once per project (`BootstrapNewLanguageProject.cs:139-164`) and
declared at `ConstantAdditions.cs:32,83`: `kguidAgentDefUser = 9303883A-AD5C-4CCF-97A5-4ADD391F8DCB`,
plus XAmple, HermitCrab, and Computer agents.

**Provenance gotcha:** `LangProject.DefaultParserAgent` (`OverridesLangProj.cs:218-272`) switches GUID
based on `MorphologicalDataOA.ActiveParser` — so "the parser agent" is *not one identity* across a
project's history if the active engine changes.

### FieldWorks already has two disagreeing notions of analysis equality

1. `WfiWordformServices.DuplicateAnalyses` (`WfiWordformServices.cs:311-345`) — checks
   `Sense`/`Msa`/`Morph` per bundle **plus `CategoryRA`**, and requires `CompoundRuleApps`, `Stems`,
   `Derivation`, and `MsFeatures` all empty.
2. `ParseAnalysis.MatchesIWfiAnalysis` (`ParseResult.cs:102-133`) — checks
   `MorphRA`/`MsaRA`/`InflTypeRA` only, and **does not check category or glosses at all**.

Neither is a documented canonical contract. Any analysis-identity profile must reconcile them, and
this compounds the coarseness problem in thread 2 rather than being independent of it.

### Nothing computes occurrence-level coverage today

The primitives exist — `AnalysisGuessServices.IsHumanApproved` and friends
(`AnalysisGuessServices.cs:273-331`), `IWfiWordform.OccurrencesInTexts` — but no shipped feature uses
them that way:

- FieldWorks' **Statistics** tab (`StatisticsView.cs:94-209`) counts tokens, types, and segments, and
  displays **no analyzed-coverage figure at all**.
- **Check Parser** collapses the text to `IStText.UniqueWordforms()` (`StText.cs:654-662`) —
  deduplicated by wordform, **discarding occurrence context entirely**.

> **Correction to `word-evidence-corpus-research.md`.** It states the parser report "links to a
> wordform GUID". `ParserReport.ParseReports` is a dictionary keyed by **word string**
> (`ParserReport.cs:91`) — one level looser again. Two distinct wordform objects sharing a surface
> form collide in a report.

### Mutation cost, and a non-undoable path

Human approval is one `UndoableUnitOfWorkHelper.Do` from the caller's side
(`FocusBoxController.ApproveAndMove.cs:38-53`), but underneath it may build a whole
`WfiAnalysis`/`WfiMorphBundle`/`WfiGloss` graph, delete an obsolete analysis, and delete an orphaned
wordform.

**Computer guesses are deliberately outside the undo stack**: `GenerateEntryGuesses`
(`AnalysisGuessServices.cs:1216-1263`) creates analyses and calls
`computerAgent.SetEvaluation(..., approves)` inside `NonUndoableUnitOfWorkHelper`, justified by
*"Trying to generate guesses during PropChanged when we can't save them."* Another confirmed instance
of the ADR 0005 pattern, and one that matters for deciding what counts as an assertion.

### Verdict against existing motif research

`word-evidence-corpus-research.md` and the corpus section of
`fieldworks-crdt-integration-research.md` are **confirmed on every substantive claim checked**, with
code now backing each. The claim *"a wordform-level or analysis-level opinion is not evidence that
every occurrence has that accepted analysis"* is not merely plausible — it is the literal branching
logic of `ApproveAnalysis`. One wording correction, above.

---

## Consequences for the proposal

1. **`I35` is substantially de-risked.** The comparison is computable, the machinery exists on both
   sides, and FieldWorks already ships the concept as `NumUserApprovedAnalysesMissing`. What is
   missing is a converter from `WfiAnalysis` to `AnalysisIdentity`, and the occurrence→case-ID
   binding.
2. **A new risk replaces it: coarser identity causes false agreement.** PanGloss identity is
   allomorph- and sense-blind, and FieldWorks itself carries two disagreeing equality notions. An
   analysis-identity profile must be authored and declared, not assumed.
3. **The proposal splits cleanly along the two-fact finding.** Tests hang on human approval, which has
   a durable GUID and is queryable today. Coverage hangs on occurrence assignment, which has no
   durable identity. **Recommend sequencing the test half first and treating coverage as a research
   track**, rather than carrying classes 3 and 4 as one body of work.
4. **Branch coverage by grammar feature is the weakest leg.** Rules have no durable identity, there is
   no sentence concept, and existing coverage is synthetic-fixture capability coverage. A build, not
   an integration.
5. **Bidirectional diff is a snapshot problem before it is a diff problem.** The algorithm is
   specified; the substrate it would read is 1 of 473 rows.
6. **`F22` is sharper now**: `merge`, `replace`, and index-as-identity `move` are unrecoverable;
   `reparent` is recoverable. Refuse loudly, degrade to delete-plus-create, or accept an externally
   supplied identity mapping.
