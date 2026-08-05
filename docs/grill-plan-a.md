# Grill queue — Plan A

*Created 2026-08-01, replacing the five grill queues deleted with the Harmony-routed plan. Questions
carried forward from those files are marked **(carried)**; the rest came out of adopting
[Plan A](plan-motif.md) and cross-reviewing it against
[plan-cross-repo.md](plan-cross-repo.md), [plan-lcmcrdt.md](plan-lcmcrdt.md),
[plan-product-architecture.md](plan-product-architecture.md), and
[motif-overall-plan.md](motif-overall-plan.md).*

**Ordering rule:** measurements first, because three later answers depend on them. Then the questions
that block M2, then M3, then M4. IDs are stable; do not renumber.

> **Read [grill-readiness.md](grill-readiness.md) before grilling.** It triages every item into
> answered / being researched / needs a spike / genuinely yours. Of 48 items, **9 are already closed
> by research and 11 are being investigated now** — grilling those would spend decisions you do not
> need to make. It also identifies two **gate** questions (`H30`, `G28`) whose answers determine
> whether twelve other items matter at all, and recommends the session order.

---

## A — Measure before deciding (blocks M2)

**A1. What does `CreateCacheCopy` actually cost?**
[ADR 0016](adr/0016-scratch-cache-copy-not-undo.md)'s entire value is the ratio between one copy from
a hot live cache and N copies from a pristine scratch. Both are asserted from the code path
(`ToXmlString()` per reconstituted object versus a surrogate copy-construct), neither is measured, and
`CreateCacheCopy` has **zero callers** in liblcm or FieldWorks. If a copy from a hot Sena-3-scale cache
takes ten seconds, the warm-scratch strategy changes shape. *Half-day spike. Nothing in M2 should be
designed before this number exists.*

**A2. Do two `LcmCache` instances coexist in one FieldWorks process?**
The service locator is per-cache and `InitializeWritingSystemManager` runs on the copy, which looks
sound. ICU initialisation is more global and was not traced. If they do not coexist, ADR 0016 needs a
different home for the scratch.

**A3. [R→answered: yes, write ~15 lines]**
[Findings](research/2026-08-03-five-computable-grill-items.md#a3). `IProjectIdentifier` is fully public
with **7 trivial members**, none touching an internal type. `MemoryOnlyBackendProvider` being internal
is irrelevant — `LcmServiceLocatorFactory.cs:151-156` wires it *inside* `SIL.LCModel` by switching on
`projectId.Type`; the caller only has to report `kMemoryOnly`. liblcm's only public implementation
(`TestProjectId`) is packable and already referenced by FieldWorks, but it is test infrastructure that
drags NUnit and Moq along. **Write the class. The scratch does not have to live on disk, and `A1`'s
numbers stand.**

**A4. [R→answered: clean, and the premise was wrong]** *(`MOT-13`)*
[Findings](research/2026-08-03-five-computable-grill-items.md#a4). **FieldWorks already has
`System.Text.Json` in its resolved `net48` graph** — at **9.0.14**, above Motif's 8.0.5 floor, arriving
transitively through `Microsoft.Extensions.DependencyModel`, which `Directory.Packages.props:44` pins
for an unrelated ICU reason and `CentralPackageTransitivePinningEnabled` propagates to every project.
Every floor in Motif's net462 dependency group is already met or exceeded, and NuGet resolves to the
highest. `AutoGenerateBindingRedirects` is on repo-wide, covering the assembly-version gap by the same
mechanism as the documented `System.Drawing.Common` fix (LT-22382). **No new pins required; M3 does not
need a different answer.**

## B — Scope and vocabulary (blocks M2)

**B5. Which family is M2's first generated family, and on what criterion?**
Plan A says "one family" without naming it. The possibility-list family is the obvious candidate — 37
in-scope rows, all `unordered` or `positional`, zero `AssessPoisonsCache=yes` — but that was chosen to
prove *generation into LcmCrdt*, and the target has changed. Is the cheapest family still the right
one when the acceptance test is now a LibLCM round trip?

**B6. [R→sharpened] Construct naming is not mechanical, and 17 manifest rows are multi-construct.**
**(carried, B19/B20)** [Audit](research/2026-08-03-manifest-trust-audit.md#6-construct-naming-b19-is-understated-not-overstated).

**B19 is understated.** Only **26.4%** of the 53 construct names are `lowerFirst(Class)`; 32.1% need a
`Cm`/`Mo`/`Ph`/`Fs` **prefix table** that is a lookup, not a transform, and is nowhere in the data; and
**41.5% have no mechanical relationship to any class** — `featureStructure` spans 16 classes,
`ruleContext` 11, `msa` 9. That grouping exists *only* in the hand-authored column. Worse, even the
exact-match bucket is unsafe: B19's own `LexSense.Gloss` has `Construct=lexSense` yet ships as `sense`,
a **second undocumented normalization** with no stated rule.

**B20's 17 reconciles exactly** — 19 raw multi-construct rows minus 2 `derived-read-only`. But the
ambiguity is not what it looks like: all 19 are plain structural fields with **one** meaning each.
`CmPossibility` is one generic class FieldWorks reuses as storage for seven lists, so the ambiguity is
*which list instance an object belongs to at runtime* — determined by its owner, a runtime fact.
**B20's "fan out to one kind per construct" cannot be done from `Class`/`Field` alone.**

**B7. [R→answered: the risk is 61 rows, not 473]** **(carried, B17/B18)**
[Audit](research/2026-08-03-manifest-trust-audit.md). Better than feared in one way, worse in another.

- **Trust the structural columns.** 22 of 22 direct `Kind`/`Sig`/`Card` checks against
  `MasterLCModel.xml` matched exactly; all five Tier-A citations were byte-accurate.
- **`ComparisonClass` is almost entirely mechanical** — derived from `Card` alone (405 of 412
  `unordered` rows), with **7 hand-written overrides**.
- **B18's number is wrong, pessimistically.** Not ~300 of 473 uncited but **406 (85.8%)**; even
  counting named-source-without-line as evidence leaves 94.3% without a pinpoint citation.
- **But the errors are concentrated in the 61 non-default rows**, where sampling 22 found ~5 wrong or
  incomplete (~23%) — extrapolating to **12–15 rows**. Zero errors in 13 mechanical-default rows
  checked, and the generating rule is trivial.

**Decision (`B7a`) [D]:** review **all 61 non-default rows** before the generator ships and spot-audit
the other 412? That is a bounded one-sitting task, not a manifest re-audit.

**B8. [R→answered: 24 fields / 10 classes, not 37 / 19] (carried, B21 — now closed)**
[Findings](research/2026-08-03-five-computable-grill-items.md#b8). The ADR 0012 filter reproduces
exactly (37 rows / 19 classes), **but 13 of those 37 fields are not read by `HCLoader.cs` at all** —
`ReversalIndex*`, `LexEtymology.*`, `LexPronunciation.Form`, `LexRefType.Members`, `LexSense.Senses`,
`MoMorphType.Prefix`, `StText.RightToLeft`, and others. Two are bare-name false positives
(`MoMorphTypeTags.kguidMorphPrefix`; HermitCrab's own `Direction.RightToLeft`) — **the exact failure
mode `HcReachable` exists to correct.** All 13 carry Tier-C boilerplate rationale, corroborating `B7`.

**Closure:** a minimal valid `LexEntry` needs **4 classes** — `LangProject` → `LexDb` → `MoMorphType`
→ `LexEntry`, which cascades `LexSense` and `MoForm`. But fully populating the confirmed L0 field set
reaches into **`PhEnvironment`, `MoInflClass`, `PartOfSpeech`, `MoInflAffixSlot`, `FsFeatStruc`** — all
G0/G1 in ADR 0012's own build order, and pulled in by two `Group≠grammar` classes (`LexEntryRef`,
`LexEntryInflType`). **A second cost ADR 0012 does not state.**

**Consequence (`B8a`) [D]:** ADR 0012's L0 definition-by-query yields 13 phantom fields and understates
the grammar dependency. Re-scope L0 to the confirmed 24, or fix `HcReachable` first?

**B9. [R→named options] What is the versioning contract for the public intent surface?**
`contractVersions` maps group → major/minor, but nothing yet says what a minor bump may change, what
forces a major, or how long a runner must accept an older group version. This is now more urgent, not
less: the intent contract is the public surface and the lowered plan is private, so the public half
carries all the compatibility obligation.

[Prior art](research/2026-08-03-prior-art-canonical-diff-versioning-batch.md#b9) surveyed SemVer,
protobuf, Avro, JSON Schema, REST practice, and Kubernetes. **Kubernetes is the closest match** — and
motif already took `group/construct/verb` from k8s's `(apiGroup, resource, verb)` in ADR 0009 §1, so
versioning per group continues a precedent already adopted. Proposed policy: **minor-safe** = new
`kind`, or a new *optional* field (safe because the contract already guarantees omission means leave
untouched); **major-forcing** = removing/renaming a `kind`, changing a field's type or meaning, or
anything that silently changes what a **previously hashed intent digest** means; **window** = a k8s-style
**dual floor** (N minor versions *or* M months, whichever is longer) rather than one number, because
motif has three runtimes on independent cadences plus agent callers who never read release notes;
**refusal** = a structured `{group, requiredVersion, carriedVersion}` payload, not prose.

**Decision (`B9a`) [D]:** adopt this, and calibrate N and M when a real release cadence exists?

## C — Engine behaviour (blocks M2/M5)

**C10. [R→counted: 4 rows] Does `AssessPoisonsCache` still have a consumer?**
ADR 0016 retires `DerivedCachePoisoningOperationKinds`, which was the column's only reader — confirmed:
`DryRun/DerivedCachePoisoningOperationKinds.cs`, read by `DryRun/ProposalDryRunner.cs`, is the single
production consumer. **The in-scope population is exactly four rows**, all `Group=lexical`:
`LexEntry.CitationForm`, `LexEntry.LexemeForm`, `MoForm.Form`, `MoForm.MorphType`. (Whole file: 4 `yes`,
469 `no`, 425 blank.) Note `MoForm.Form`/`MorphType` carry it because of the **C15 correction** — they
were originally missed and later added, so the column has a track record of being fixed rather than
guessed.

**Decision (`C10a`) [D]:** four rows is small enough that keep / retire / repoint is now cheap. Which?

**C11. Is the liblcm `Rollback`/`Undo` hook asymmetry worth an upstream PR?**
Not blocking — ADR 0016 routes around it by never reverting. It is still the correct fix, it would let
C10 resolve cleanly, and the Avalonia/`net10.0` migration already has people in that codebase. Raise
it now or accept the workaround permanently?

**C12. Does a reviewer actually see that a phonological reorder changed the grammar's meaning?**
*(`MOT-8`, re-scoped)* Effects carry identity-keyed moves rather than positional rewrites, which is
necessary but may not be sufficient. This is the surviving half of the old ordered-grammar question,
and it is a **review** question now, not a convergence one.

**C13. [R→answered: hand-written, and it already exists]**
[Findings](research/2026-08-03-five-computable-grill-items.md#c13). **Not manifest-derivable.**
`IPhRegularRule.FeatureConstraints` is a synthetic `[VirtualProperty]`
(`OverridesLing_Lex.cs:7536`), and its traversal is not a flat scan of the four documented roots — it
dispatches on `ClassID` into `PhSequenceContext.MembersRS` and `PhIterationContext.MemberRA`, and for
`PhSimpleContextNC` collects **`PlusConstrRS` before `MinusConstrRS`**, deduplicating by reference so
first appearance wins (`:7595-7626`). Three classes and two fields the manifest never names, with an
ordering rule flat `(Kind, Card, Sig)` columns cannot encode.

**liblcm already centralizes this walk**, and two consumers treat its order as canonical —
`GrammarJsonServices.cs:650` (`ordered: true`) and `M3ModelExportServices.cs:578,588`. **The pre-apply
check should call `rule.FeatureConstraints`, not regenerate the traversal** — a direct liblcm call from
the dry-run path, or a byte-for-byte port of `CollectVars` if liblcm cannot be referenced there.

## D — Product and boundaries (blocks M4)

**D14. Two review surfaces, or one?**
FwLite already ships comment threads in `LcmCrdt/Changes/Comments/`. `MOT-10` builds a review domain.
Is that a deliberate second surface for a different audience, or duplication we should notice now?

**D15. Does review state need to work offline?**
Proposals and Receipts are immutable and need only an object store. Review state — comments,
approvals, decisions — is mutable. If offline review is required, that is the one place a CRDT would
genuinely earn its cost, and the answer changes `MOT-14`'s shape.

**D16. What does "optional per project" mean operationally?**
An unshared project never leaves the machine. Who flips the switch, can it be flipped back, and what
happens to Receipts already pushed?

**D17. Is grammar authoring genuinely desktop-only?**
Plan A puts grammar on the FieldWorks/LibLCM path, which cannot reach Android because LibLCM's native
ICU dependency has never been cross-compiled. That is a product decision falling out of an
architecture choice. Make it explicitly.

**D18. Who owns keeping the two vocabularies aligned?**
The [adoption report](harmony-adoption-report.md) recommends one intent vocabulary and two lowerings.
Nothing mechanical enforces that. Is a generated cross-check worth building, or is human review of the
correspondence sufficient — and whose review?

## E — Standing risks, not blockers

**E19. [R→escalated] Chorus merges the applied log and does not understand it.**
[Findings](research/2026-08-03-chorus-applied-log-merge.md). Research **raised** this rather than
closing it. Three results:

- **Phase 0 item 8 was never closed.** `implementation-plan.md:49-52` says the union behaviour was
  *"confirmed at the LibChorus level, to be re-confirmed in FLExBridge."* The LibChorus half is real
  (verified: `ChorusNotesAnnotationMergingStrategy.cs:24-27`). **The FLExBridge half never happened** —
  no test, spike, or artifact exists, and `SIL.ChorusPlugin.LfMergeBridge` / `SIL.Chorus.ChorusMerge`
  are not even in the local NuGet cache.
- **The common case is safe either way.** Distinct-GUID additions — every reviewer's independent apply
  — are never dropped by the generic algorithm. Worst case is a spurious `.ChorusNotes` order note.
- **The documented failure mode is understated.** Chorus's *default* strategy is `FindByEqualityOfTree`
  with order relevant (`ElementStrategy.cs:33-36`), matching only on exact recursive XML equality. If
  the guid-keyed registration is missing, two replicas writing the **same** `proposalId` differently
  produce **two `<rt>` elements sharing one GUID** — a `.fwdata` anomaly LibLCM's loader was never
  designed to see, not the benign one-wins overwrite `applied-log.md:101-105` describes.

Strong indirect evidence says the registration exists (`.fwdata` is flat, so one generic `rt`-by-`guid`
rule covers every class; a decade of FieldWorks Send/Receive would otherwise corrupt constantly) — but
that is **inference from necessity, not observation**.

**`MOT-14` does not resolve this.** Moving Receipts to Lexbox fixes the product consequence; the log
still lives in `.fwdata` and still goes through Chorus.

**Action (`E19a`) — not a grill item, a task:** run the section-4 experiment. It needs no FLExBridge
source — drive the real merge through `FwHeadless`'s own `SendReceiveHelpers.CallLfMergeBridge`. Until
it runs, ADR 0003 decision 2 should carry a caveat rather than be cited as settled.

**E20. PanGloss has no release pipeline at all.**
CI is `ubuntu-latest` only, no artifact upload, no publish job, no binary for any OS. This blocks
`MOT-15` step 2 and, separately, the smallest clean local install. Not our repo.

**E21. `motif` CLI is process-per-command.**
`dry-run` then `apply` pays two full project loads. Nothing structural prevents a long-lived session
holding one cache and one pristine scratch — the Runner already takes a cache it does not own. Worth
doing once A1 says what a load costs.

---

## Deliberately not asked

These were live questions under the previous plan and are moot under Plan A. Recorded so they are not
rediscovered as gaps:

- how two people concurrently reordering phonological rules converge — single writer, Chorus between
  people, so it is Chorus's question;
- what the MiniLcm↔LibLCM crosswalk should contain — no crosswalk;
- whether `SetOrderChange` can carry feeding order — nothing rides on it;
- when to bump the `SIL.Harmony` pin — no Harmony dependency;
- whether Harmony's hash should cover the payload — Motif's intent digest already does, for Motif's
  own contract. It remains a real gap *in Harmony*, for FwLite, if FwLite ever needs tamper-evidence.

---

# Added 2026-08-03 — the bidirectional / test-coverage proposal

*From [proposal-2026-08-03-bidirectional-and-test-coverage.md](proposal-2026-08-03-bidirectional-and-test-coverage.md),
recorded to be challenged. Items marked **[R]** need research and grounding before a decision is
possible; items marked **[D]** are owner decisions that can be taken now.*

## F — Bidirectional encoding

**F22. [D] What is the covered surface of diff-to-operations, and what happens outside it?**
`merge`, `replace`, and `reparent` are discovered-footprint by design, and from a state delta alone
"merged A into B" is indistinguishable from "deleted A and edited B". Draw the line explicitly. Is an
uncovered edit **refused loudly** — "this change cannot be encoded, author it instead" — or **silently
degraded** to delete-plus-create? Silent degradation double-counts in the effect model and loses the
author's meaning; loud refusal makes FieldWorks-authored proposals fail on edits a linguist considers
ordinary.

**F23. [R→answered in half; the rest is now a concrete proposal, not an open question]**
[Prior art](research/2026-08-03-prior-art-canonical-diff-versioning-batch.md#f23). No standard exists —
RFC 6902 and 7386 both specify *application* only, never *generation*. But the question splits:

- **Content equality is already canonical.** Key it on the **effect** digest, not the intent digest.
  The contract already makes effects state-based, read-back, identity-keyed, and *stable under lowering
  optimization* (`change-set-contract.md:548`) — the Git/Dolt property of hashing the result, not the
  path. Nothing to build; it needs **stating** as the dedup key.
- **The intent digest still needs freezing beyond LIS**, because it hashes the chosen decomposition
  into Layer-0 verbs. Four proposed rules: one total order across *all* operations (byte-ordinal by
  canonical ID, then manifest field order, `move` keeping frozen LIS order inside its bucket); one fixed
  decomposition per comparison class, with `feeding` **never** claiming a static anchor result; a fixed
  dispatch for discovered-footprint operations (the contract already forecloses this — *never
  delete-plus-create*); and normalize **before** diffing, not after.

**Residual decision (`F23a`) [D]:** adopt those four rules as written, or contest one?

**F24. [D] Does a diff-derived Proposal carry a distinguishable provenance?**
Its effects equal the observed delta by construction, where an authored Proposal's effects are read
back from the engine per ADR 0006. Not a contradiction, but a second provenance class the review model
does not currently distinguish. Should a reviewer be able to tell?

**F25. [R→partly answered] What does diffing two projects actually cost, and can two caches be open at once?**
Two caches **do** coexist — `PersistingLayerTests.BEPPortTests.cs:166-191` holds two live, including
`kMemoryOnly` on both sides. But the source there is a *blank* project, so scale and
inside-FieldWorks coexistence remain open. Cost is unmeasured and dominated by two cache loads plus a
doubled `EnsureCompleteIncomingRefs` whole-project force-fluff. **The real blocker is upstream of
diff**: `ObjectSnapshot` is `{CanonicalId, MultiUnicodeFields}` and cannot represent ownership,
references, or sequence position, so 1 of 473 in-scope rows is snapshottable. See
[findings](research/2026-08-03-bidirectional-and-test-coverage-findings.md).

**F26. [D] Does diff replace or complement authored proposals as the primary human path?**
If normal FieldWorks editing becomes the main way proposals get made, the authored-Proposal path
becomes an AI/CLI path almost exclusively. That is a product decision with UI consequences.

## G — Change classes

**G27. [R→the data says no; the decision is what to do about it]**
[Audit §8](research/2026-08-03-manifest-trust-audit.md#8-does-the-proposed-change-class-taxonomy-partition-the-manifest-g27).
The proposal's five row counts are **all verified exact**. But the table assumes `Verbs` alone
determines class membership, and cross-tabulating against `Group` shows **only 52% of in-scope rows land
unambiguously in one class**:

| Bucket | Rows | % |
| --- | ---: | ---: |
| Class 1/2 clean | 246 | **52.0%** |
| Same verbs but `Group ∈ {lists, system}` — **no home** | 73 | 15.4% |
| Class 5 clean | 34 | 7.2% |
| Straddles class 5 + ordering | 27 | 5.7% |
| Straddles class 1/2 + ordering + reparenting | 32 | 6.8% |
| Not authorable (`Verbs: n/a`) | 61 | 12.9% |

Three specific breaks: the **73 `lists`/`system` rows have no bucket** (their homes are labelled
*candidate*, not class); the **schema-and-metadata candidate describes the wrong rows** — it is defined
around custom fields and writing systems, which per B12 have **zero manifest rows**, while the actual
`system` group is 47 `LangProject` config rows; and **classes 3 and 4 have no data at all** — all 48
text rows are `Scope=out`, so the cut cannot be tested for them, which is `H30`'s gate as a data fact.

Candidates still open for class 6+: **ordering** (56 `positional` + 2 `feeding`), **reparenting** (32),
**schema and metadata**, **shared vocabulary**, **compound graph operations** (`merge` / `replace`).
**But the answer depends on `G28`** — a taxonomy for review routing can tolerate a row in two buckets;
one for permissions cannot.

**G28. [D] What is a change class *for*?**
Review routing? Permissions? Risk tiering? Which diff operations are coverable? Coverage
requirements? The taxonomy's shape depends on its purpose, and the purpose is not yet stated.

**G29. [D] Does ordering deserve to be its own class?**
For 54 of 56 `positional` rows order is display order; for the 2 `feeding` rows it is grammatical
meaning. Same verb, categorically different review stakes.

## H — Text and analysis as a bounded context (reverses a committed scope decision)

**H30. [D→DECIDED 2026-08-05 — in the destination, staged out of v1]**
[ADR 0017](adr/0017-text-and-analysis-destination-scope.md). Gate 1 is closed.

The governing argument, accepted: **coverage gaps are the feeding ground for new and refined rules** —
raw material, not a reporting metric. That retires the "3% on day one" objection, which assumed
coverage is a *score*; as a **work queue**, 3% coverage means 97% backlog.

Cost of deferring, checked against the code: **roughly 70% additive.** Manifest re-scoping is
mechanical; `ObjectSnapshot` is documented additive-stable; adding a `kind` is minor-safe under `B9`;
and the ten verbs already cover analyses (`WfiAnalysis` is a real `CmObject`, approval is a reference,
`Segment.Analyses` is a ref seq). **The 30% that is not additive is the hashed part** — `CanonicalId`
is 16 bytes and GUID-derived, an occurrence has no GUID, and the effect tuple
`(canonicalId, field, before, after)` is the digest atom.

**Hence the one time-sensitive consequence (`H30a`) — decisions 3 and 4 of ADR 0017 must be taken
before M3 freezes the canonical JSON form.** `CanonicalId`'s prefix already *"carries no structural
meaning"*, so reserving non-object targets costs ~0 today and is a major bump later.

**Ten items are admitted, not deferred:** `H32a` `H33a` `H34` `I35a` `I35b` `I36` `I37` `I39` `I39a`
`I40`. Most are not v1. **`H34` splits** — text *import* is ordinary GUID-bearing object creation that
fits the contract today; only occurrence anchoring is hard.

**H31. [R->answered: no, and it is systemic]**
`AnalysisOccurrence` is a plain C# class, **not a `CmObject`** - no GUID, never persisted, `Equals` is
`(Segment, Index)`. On any edit the paragraph re-segments, leftover `Segment` objects are *deleted*,
and analyses are re-attached by a best-effort heuristic on lowercased word string plus position whose
own comment says *"Apply various heuristics."* `TextTag` and the discourse chart use the same scheme.
**A durable occurrence anchor must be built; nothing in the model can be repurposed.**

**H32. [R->answered: BOTH, on two separate axes - the most consequential finding]**
`ApproveAnalysis(occ, allOccurrences, ...)` gates *repointing other occurrences* on `allOccurrences`,
but `FinishSettingAnalysis` sits **outside** that branch and always sets
`DefaultUserAgent.SetEvaluation(newWa, Opinions.approves)`. So a manual analysis is **two facts**:
**A** - this `WfiAnalysis` is human-approved (global, durable `WfiAnalysis` GUID); **B** - this
occurrence uses it (`Segment` + index, no durable identity).

**Consequence, now `H32a` [D]:** tests hang on Fact A and are viable *now*; coverage hangs on Fact B
and is blocked on `H31`. **Should the test half be sequenced first and coverage treated as a research
track**, rather than carrying classes 3 and 4 as one body of work?

**H33. [R->answered: cleanly, with one provenance gotcha]**
`CmAgent.Human` plus owned `Approves`/`Disapproves` singletons referenced from the analysis's
`Evaluations`. `Opinions` is tri-state (`disapproves=0, approves=1, noopinion=2`), so "disapproved" is
distinct from "no opinion" for humans *and* parsers. Fixed GUIDs exist -
`kguidAgentDefUser = 9303883A-AD5C-4CCF-97A5-4ADD391F8DCB`, plus XAmple, HermitCrab, and Computer.

**Gotcha, now `H33a` [D]:** `DefaultParserAgent` switches GUID based on `ActiveParser`, so "the parser
agent" is not one identity across a project's history if the engine changes. Does provenance record
the agent GUID, the engine, or both?

**H34. [D] Are text edits themselves in scope, or only analyses attached to text?**
Class 3 says "Texts". Adding, editing, and deleting *text content* is a much larger surface than
attaching analyses to existing text — and it is what breaks occurrence anchors. These may need to be
separate classes.

## I — Tests and coverage

**I35. [R→answered: yes, with a caveat that becomes a new decision]**
**Yes.** `ParserReport.cs:380-390` already computes exactly this in production —
`NumUserApprovedAnalysesMissing` counts human-approved analyses the parser cannot produce. On the
`.fwdata` path PanGloss morpheme identity **is** the LibLCM MSA GUID (`lexicon.rs:301,309`), and
`pg-assess` already has digest-keyed exact-structural set comparison.

**The replacement risk — now `I35a` [D]:** PanGloss's `AnalysisIdentity` carries no **allomorph** and
no **sense** identity, where `WfiMorphBundle` carries `MorphRA`, `MsaRA`, *and* `SenseRA`. Two
analyses differing only in allomorph or sense collapse to one PanGloss identity — **false agreement**,
the unsafe direction. Accept as a declared limitation, or build a richer identity?

**I35b. [D] Whose analysis-equality definition wins?**
FieldWorks already ships **two disagreeing** implementations: `WfiWordformServices.DuplicateAnalyses`
checks `Sense`/`Msa`/`Morph` **plus category** and requires several fields empty;
`ParseAnalysis.MatchesIWfiAnalysis` checks `Morph`/`Msa`/`InflType` only and **ignores category and
glosses**. Neither is documented as canonical. An analysis-identity profile has to reconcile them, on
top of PanGloss's own allomorph- and sense-blindness (`I35a`).

**I36. [D] Is "one authoritative analysis per occurrence" linguistically defensible?**
Genuine ambiguity exists, and the proposal's own disambiguation requirement implies ambiguity is a
real state rather than a failure. Forcing one analysis may encode false certainty. Note the model
already distinguishes **disapproved** from **no opinion**, so a three-state answer is representable
without new modelling.

**I37. [D] What is the coverage ramp?**
Most text in most projects is unanalyzed, so this metric reads near zero on day one. A number that
starts at 3% with no defined trajectory gets ignored. Absolute target, per-text target, or delta-only
("this change did not reduce coverage")?

**I38. [R→answered: no, and this is the weakest leg]**
Rules, strata, and templates have **no retained GUID** — `handoff.rs:28-33` states stable FieldWorks
IDs survive import for lexical entries only. So a mismatch caused by which rule fired is not nameable
in FieldWorks terms. There is also no *sentence* concept (`AssessmentCase.input` is one word), and
`pangloss coverage` today is capability coverage over **synthetic fixtures only**, explicitly never
real-language data. Branch coverage is a build, not an integration: it needs durable rule identity, a
per-word construct-provenance ledger, and a sentence grouping — none of which exist.

**I39. [D] Do donated tests need review before they count?**
A wrong donated analysis becomes a permanently failing test that blocks unrelated work. Reviewed,
trusted, or quarantined? Sharpened by `H32`: a donation sets a **global** approval flag on a
`WfiAnalysis`, so a bad donation is not scoped to the donor's occurrence.

**Related, now `I39a` [D]:** computer guesses are created *outside the undo stack*
(`GenerateEntryGuesses` uses `NonUndoableUnitOfWorkHelper`) and approved by the Computer agent. Do
machine guesses count as assertions, tests, neither?

**I40. [D] What happens when a rule change is correct but breaks an old analysis?**
In software this is "update the test". Here the old analysis may have been a native speaker's
judgement. Who may overrule it, and is that itself a reviewable change?

## J — Authoring, editing, portability

**J41. [D] Confirm the Layer 0 / Layer 1 rationale.**
The proposal says the semantic layer is unnecessary for human diff-based authoring and meaningful only
for AI and CLI. That matches ADR 0009's split and supplies its missing rationale: **Layer 0 is the
diff's output vocabulary; Layer 1 is the agent's input vocabulary.** Worth adopting as the stated
reason, because it makes the split load-bearing rather than stylistic.

**J42. [R→already decided; one residual]**
[Prior art](research/2026-08-03-prior-art-canonical-diff-versioning-batch.md#j42). Resolved operations
at rest with the query kept as **non-hashed provenance** is **verbatim ADR 0009 §1** (`adr/0009:38-40`:
*"the composer and its parameters ride as provenance on the emitted Change Set — non-hashed,
re-runnable"*). It matches Terraform, EF Core, and Sourcegraph. The query-as-truth alternative is the
Kubernetes server-side-apply pattern, which **motif already rejected one layer down** (ADR 0009 §1 on
`managedFields`) for the same reason: a reviewer cannot approve effects for an unresolved query.
**No new machinery — this is an instance of a decision already taken.**

**Residual (`J42a`) [D]:** Terraform hard-errors when a saved plan's state lineage has moved. Motif has
the identical mechanism already — the pre-flight footprint-digest-plus-engine-version check. **Should
re-reviewing or applying a resolved batch against a moved baseline be forced through that same drift
path**, rather than silently re-resolving? (Recommend yes.)

**J43. [D] What are the rules for removing an operation from a change set?**
Trivial mechanically. But it moves the intent digest while `proposalId` stays frozen, and it can
orphan a dependent operation or break a `requires` edge. Refuse, cascade, or warn?

**J44. [D] What is the unit of splitting a change set?**
Portability is nearly free — `ProposalStore` is already content-addressed objects plus manifests. The
constraint is the `requires` DAG: a split must not sever a prerequisite edge, and splitting a
multi-operation atomic group breaks all-or-none application.
