# Grill queue — Plan A

*Created 2026-08-01, replacing the five grill queues deleted with the Harmony-routed plan. Questions
carried forward from those files are marked **(carried)**; the rest came out of adopting
[Plan A](plan-motif.md) and cross-reviewing it against
[plan-cross-repo.md](plan-cross-repo.md), [plan-lcmcrdt.md](plan-lcmcrdt.md),
[plan-product-architecture.md](plan-product-architecture.md), and
[motif-overall-plan.md](motif-overall-plan.md).*

**Ordering rule:** measurements first, because three later answers depend on them. Then the questions
that block M2, then M4, then M5. IDs are stable; do not renumber.

> **Read [grill-readiness.md](grill-readiness.md) before grilling.** It triages every item into
> answered / decided / needs a spike / genuinely yours. **20 items are closed by research and 5 are
> decided** (ADRs 0017, 0018, 0019) — grilling those would spend decisions you do not need to make.
> Both gate questions (`H30`, `G28`) are now closed.

> **Sequencing, 2026-08-05** — [ADR 0020](adr/0020-cli-first-fieldworks-planned-not-built.md). **Scope 1
> is the LibLCM seams proved through the CLI, with an AI agent as author. Scope 2 is the FieldWorks
> integration: planned, not built.** FieldWorks- and Chorus-shaped questions below are still worth
> answering on paper, but they no longer gate work. `A1` is the only spike on the critical path and the
> only one in this repository; `E19` and `F26a` are deferred by owner decision.

---

## A — Measure before deciding (blocks M2)

**A1. [CLOSED 2026-08-05 — measured against real Sena 3; ADR 0016 amended]**
Harness: `spikes/SIL.Motif.Spikes.ScratchCache` plus equivalence assertions in
`tests/SIL.Motif.Tests/Runner/ScratchCacheEquivalenceTests.cs` (5 tests, passing; suite now 86/86).
[Results](research/2026-08-05-createcachecopy-provenance-and-hazards.md#10-measured--the-spike-was-built-and-run).

| | Sena 3 (152,222 objects) |
| --- | ---: |
| file copy (control) | 49 ms |
| in-memory copy, cold live cache | 209 ms |
| **derived copy from pristine scratch** | **140 ms** |
| in-memory copy, fully hot live cache | 4,445 ms |
| **file copy + open (XML path)** | **580 ms** |
| in-memory copy **of the file-loaded scratch** | 78 ms |

- **Fan-out is genuinely fast** — 31.8× cheaper than re-copying from a hot cache.
- **"In-memory is cheaper" DEPENDS** — break-even at ~**9% of objects fluffed**. In a live session the XML
  path is likely cheaper, the opposite of what ADR 0016 assumed.
- **Equivalence is the decider: 0 of 4 writing systems value-equal** on *every* in-memory variant (valid
  character sets 2 → 0, fonts replaced, `seh` lost its collation rules); the file path returned **4 of 4**
  and no findings. Text, object counts, entry counts and custom-field flids matched on both paths.
- **The hybrid does not exist.** An in-memory copy *of the file-loaded scratch* — whose writing systems are
  provably intact — still came back 0 of 4. The loss belongs to the `kMemoryOnly` target, not the source
  (`useMemoryWsManager` is hardwired, `BackendProvider.cs:263-265`). Cheap fan-out and lossless cannot both
  be had.
- **Hazard (b) did not reproduce** — both fixtures carry two custom fields on `LexEntry`, the required
  condition, and flids matched. Not disproven; the resolve-by-name invariant stays as cheap insurance.

**Amendment: one canonical path — the XML path.** `CreateCacheCopy` is withdrawn from the Dry Run design and
marked non-canonical in code. The ~5× speed is given up deliberately, because keeping both would require every
future operation's author to judge "does this depend on collation?" correctly, forever, with silence as the
failure mode. Uncommitted live edits are the one loss and they fail closed — apply refuses on drift, so the
precondition is simply *save before dry-running*.

*Original framing:*

**A1. What does `CreateCacheCopy` actually cost — and can it be trusted?**
[ADR 0016](adr/0016-scratch-cache-copy-not-undo.md)'s entire value is the ratio between one copy from
a hot live cache and N copies from a pristine scratch. Both are asserted from the code path
(`ToXmlString()` per reconstituted object versus a surrogate copy-construct), neither is measured, and
`CreateCacheCopy` has **zero callers** in liblcm or FieldWorks. If a copy from a hot Sena-3-scale cache
takes ten seconds, the warm-scratch strategy changes shape. **Nothing in `MOT-11` should be designed
before this is settled.**

**[R→research landed 2026-08-05; the spike survives but is now narrow and sharper]**
[Findings](research/2026-08-05-createcachecopy-provenance-and-hazards.md). ADR 0016 is
[amended](adr/0016-scratch-cache-copy-not-undo.md).

- **Cost model confirmed from source** — hot source pays `ToXmlString()` per object, dormant source is a
  byte-array reference copy, work is O(n objects), `kMemoryOnly` does zero disk I/O.
- **Provenance: SDK-sample infrastructure for XML ↔ Db4o backend porting.** Both repos' histories are
  truncated at 2012 synthetic roots, so original intent is **unattributable** — no `git blame` claim about
  it is supportable. Db4o was removed in 2015, the sample deleted in 2017. But the primitive underneath
  (`RegisterInactiveSurrogate`) runs on every object of every project open, so only the *cache-to-cache
  port path* is untested.
- **The harness already exists.** Recoverable at
  `git -C ../FieldWorks show f0d837288^:Samples/ImportExport/ImportExport.cs`, with an average-of-N timing
  mode. The people who built the API measured it; the numbers are gone. Start there.
- **Two silent correctness hazards ADR 0016 had not anticipated**, and they matter more than milliseconds:
  a memory-only scratch's **writing systems are synthesized from the bare language tag** (no custom
  collation, sort rules, or valid characters), and **custom-field `flid`s are re-derived** through a
  `HashSet` whose enumeration order is not contractual. Motif is safe on both today — no `flid` anywhere in
  `src/`, and writing systems resolved per cache by tag — and ADR 0016 now states those as invariants
  rather than luck.
- **Fixture trap:** the genuine Sena 3 (152,222 objects, 55.9 MB) is in the FieldWorks checkout; a 50-object
  stub in `%TEMP%` reuses the same name. The only test ever to exercise this path used a 688-object blank
  project — 221× smaller.

**What the spike must now do:** measure the hot-copy and scratch-derived-copy ratio at Sena-3 scale, **and**
round-trip a project with ≥2 custom fields on one class plus a customized writing system, comparing flids
and writing-system state live vs. scratch. The three falsification criteria are in the findings note §9.
Criterion 3 is the one that would change the architecture rather than its parameters.

**A2. [R→largely answered 2026-08-05; only scale remains]**
[Findings](research/2026-08-05-createcachecopy-provenance-and-hazards.md#3-a2--two-live-caches-coexist-and-the-scratch-avoids-the-one-shared-singleton).
Two caches **do** coexist — `BEPPortTests` holds both live — and **no unsafe shared state was found**: ICU
init is idempotent by its own documented design (`CustomIcu.cs:208-210`), `CmObjectId` interning is
per-cache not static, and `CmObjectSurrogate`'s two statics are lock-guarded reflection caches. The one
genuinely shared singleton, `CoreGlobalWritingSystemRepository`, is only touched when
`ProjectId.ProjectFolder` is non-empty — so **a memory-only scratch must keep `Path`/`ProjectFolder`
empty**, which is now a design requirement rather than a detail (see `A3`).

What remains is scale only: coexistence is proven with a 688-object blank project, not at Sena-3 scale
inside a live FieldWorks process. That rides along with `A1`'s spike.

**A3. [R→answered: yes, write ~15 lines]**
[Findings](research/2026-08-03-five-computable-grill-items.md#a3). `IProjectIdentifier` is fully public
with **7 trivial members**, none touching an internal type. `MemoryOnlyBackendProvider` being internal
is irrelevant — `LcmServiceLocatorFactory.cs:151-156` wires it *inside* `SIL.LCModel` by switching on
`projectId.Type`; the caller only has to report `kMemoryOnly`. liblcm's only public implementation
(`TestProjectId`) is packable and already referenced by FieldWorks, but it is test infrastructure that
drags NUnit and Moq along. **Write the class. The scratch does not have to live on disk, and `A1`'s
numbers stand.**

**Refined 2026-08-05:** liblcm's internal convenience implementation is `SimpleProjectId`
(`Infrastructure/Impl/SimpleProjectId.cs:21`) and it is `internal`, so motif writes its own regardless. Two
constraints on that class: `Type => kMemoryOnly`, and **`Path`/`ProjectFolder` null or empty** — the latter
is what keeps the scratch clear of the process-global `CoreGlobalWritingSystemRepository` singleton (`A2`).
Not cosmetic.

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

**B5. [D→DECIDED 2026-08-05 — the lexical entry, plus the minimum that can create one]**
Owner decision: *"starting with lexemes sounds intelligent."* Criterion recorded: **start where an agent's
work starts — the dictionary entry** — rather than at the mechanically cheapest family. The old
possibility-list default is explicitly rejected: those 19 rows are B20's multi-construct set, whose fan-out
*cannot* be derived from `Class`/`Field` alone, so it would front-load the manifest's hardest naming problem.

**The slice is not simply the `lexEntry` construct, because that construct cannot create a lexeme.** All 16
in-scope `lexEntry` rows are `set|clear` (7), `addRef|removeRef` (3), or `n/a` (6) — **zero `create|delete`.**
`LexEntry.LexemeForm` is classified under `allomorph` and `LexEntry.Senses` under `lexSense`, so a generator
built from `lexEntry` alone could edit entries and never make one. That is a concrete instance of `B8`'s
object-creation closure: construct boundaries do not match object boundaries.

**M2's slice, therefore:**

- the `lexEntry` construct's 10 authorable rows (`set|clear` ×7, `addRef|removeRef` ×3);
- **plus** `LexEntry.LexemeForm` and the `MoForm` rows needed to bring an entry into existence
  (`MoForm.Form`, `MoForm.MorphType`, `MoForm.IsAbstract`), which is what makes `create|delete` — the verb
  shape `setGloss` does not exercise — part of the gate;
- **excluding `LexEntry.AlternateForms`**, a `feeding` row belonging to `MOT-8`;
- 6 `n/a` rows (`HomographNumber`, the dates, residue, `MainEntriesOrSenses`) generate no kinds, per `B16`.

**Cache poisoning is no longer a selection criterion.** `LexEntry.CitationForm`, `LexEntry.LexemeForm`,
`MoForm.Form` and `MoForm.MorphType` are all `AssessPoisonsCache=yes`, and under
[ADR 0016 as amended](adr/0016-scratch-cache-copy-not-undo.md) a Dry Run runs on a throwaway file-loaded
scratch while Apply commits without rollback — so the flag has no bearing on this choice. That strengthens
the case for retiring the column (`C10a`).

*Original framing:*

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

**New: `B9b` [D] — when does the intent surface declare itself stable, and what ends the churn window?**
[ADR 0021](adr/0021-cli-is-the-full-surface-layer-1-churns.md) decisions 3 and 5 accept deliberate churn
in Layer 1 while FieldWorks is not yet depending on it, and accept its one real cost: **a Proposal
authored during the window may not replay later.** That is tolerable while the author is an agent that can
re-author on demand on one machine. It stops being tolerable the moment a *human's approval* is recorded
against a stored Proposal, because `MOT-9`'s premise is that a Receipt binds what was approved.

**Narrower than it first looked.** The question applies only to **stored, hashed artifacts** — Proposals
and Receipts. The ephemeral read surface (an agent asking *"what is true now"*) stores nothing, replays
nothing, and therefore needs no stability declaration at all, permanently. The boundary where an ephemeral
answer becomes durable is precise: **citing it as evidence turns it into a Check Run.**

So: does the churn window end at the first human approval (recommended), at scope 2, or at a declared
version? And until it ends, the contract-version metadata should say **unstable by declaration** rather
than implying stability by omission.

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

> **Deferred by owner decision, 2026-08-05.** *"We don't care about Chorus right now. It's not great, and
> we know it will fail in some ways."* The experiment stays specified and unscheduled; it is scope 2 and
> lives in another repository ([ADR 0020](adr/0020-cli-first-fieldworks-planned-not-built.md)). The
> caveats remain in ADR 0003 and `implementation-plan.md`. A single-machine CLI never triggers a Chorus
> merge — **that silence is not evidence.**

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

**F22. [D→LARGELY DISSOLVED 2026-08-05 by [ADR 0019](adr/0019-observed-intent-and-proposal-edit-mode.md)]**
For the **authoring** path there is nothing to recover: `merge`, `replace`, and index-as-identity
`move` are **observed at edit time**, not inferred from a delta. (`reparent` was always recoverable —
the GUID survives the cross-owner move.)

For **project-to-project** diff, where no edit history is shared, the answer is **refuse loudly**.
Silent degradation to delete-plus-create was never available anyway: `change-set-contract.md` forbids
it outright, because LibLCM's overwrite is a detach and delete-plus-create would trigger a full
ownership cascade.

*Original framing:*

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

**F24. [D — sharpened by [ADR 0019](adr/0019-observed-intent-and-proposal-edit-mode.md)]**
There are now **three** provenance classes, not two: **observed** (drafted in proposal-edit mode, intent
recorded), **diffed** (derived from a state delta between two projects), and **authored** (built
operation by operation, the AI/CLI path). The edit mode is what makes the first distinguishable at all.
Question unchanged: should a reviewer see which?

*Original framing:*

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

**F26. [D→DECIDED 2026-08-05 — observe intent, in a constrained proposal-edit mode]**
[ADR 0019](adr/0019-observed-intent-and-proposal-edit-mode.md). Editing in FieldWorks **is** the
primary human path, but FieldWorks **records what the user did** rather than Motif inferring it from
the delta. Diff keeps the job it is good at — comparing two projects with no shared edit history.

**The keystone is the constraint, not the recording:** drafting happens in an explicit *edit to create
a proposal* mode that **bounds the edit surface to the in-scope domains**. That makes the observation
problem finite, turns refusal into a design-time property (an unobservable edit is never offered
rather than rejected at encode time), and bounds drift because out-of-domain edits cannot occur in the
session at all.

**Open risk (`F26a`) — needs a spike, deferred 2026-08-05.** This requires a seam in FieldWorks' command
layer. ADR 0003 deliberately avoided liblcm's *internal undo records*; observing FieldWorks' own commands
is a different seam whose existence and stability are **unverified**. It is scope 2 and in the FieldWorks
repository ([ADR 0020](adr/0020-cli-first-fieldworks-planned-not-built.md)), so it waits — but it must run
**before any `MOT-12` code**, since this ADR's authoring path depends on it.

**Side effect:** the 473-row snapshot substrate is **de-urgentized, not cancelled** — drafting no
longer depends on diff, so it can be built incrementally behind the domains that need it.

## G — Change classes

**G27. [R→DISSOLVED by [ADR 0018](adr/0018-change-class-is-two-axes-not-one.md)]**
*"Are the six classes the right cut"* was the wrong question — **the cut was two cuts.** The six
classes conflated a **domain** axis (classes 1–4) with an **operation-shape** axis (class 5), which is
exactly why 59 rows straddled and 73 had no home. Split into `(domain, shape)`, all 473 rows land in
exactly one of 21 cells. The row counts below were all verified exact; it is the grouping that changed.

*Original finding, kept because it is what forced the diagnosis:*
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

**G28. [D→DECIDED 2026-08-05 — two orthogonal axes, not one label]**
[ADR 0018](adr/0018-change-class-is-two-axes-not-one.md). Gate 2 is closed.

A change class is a **`(domain, shape)` pair**. Verified across all 473 in-scope rows: **21 populated
cells, every row in exactly one, zero straddle.** Domains are `grammar` 230 / `lexical` 157 / `system`
47 / `lists` 39; shapes are the six `Verbs` values.

**And it is not a new vocabulary.** ADR 0009's kind namespace is already `group/construct/verb`
(`lexical/lexSense/setGloss`), so **domain is the kind's first segment and shape derives from its verb
segment**. A change class is a *projection* of an identifier that already exists — nothing new becomes
versioned contract, so the major-forcing rename risk that made this gate urgent never arises. This also
discharges ADR 0017 decision 5: adding a `text` domain is adding a `group`, which is minor-safe.

**Risk tier is derived, not an axis** — a function of `(domain, shape)` plus `ComparisonClass`. The
39 `lists` rows' project-wide blast radius falls out of the domain axis without a third classification.

**G29. [D→RESOLVED by ADR 0018 — ordering is a shape, not a class]**
Ordering needs no class of its own. And the substantive worry — 54 of 56 `positional` rows are display
order while 2 are grammatical meaning — **is already carried by a column that exists**:
`ComparisonClass` separates `positional` (56) from `feeding` (2). The manifest already distinguishes
"order is presentation" from "order is meaning"; nothing needed building.

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

**H31. [R→answered, then CORRECTED 2026-08-05 — the conclusion below overstates the problem]**

> **Correction.** Everything below is true of the `AnalysisOccurrence` *class* and **false of what
> Motif actually addresses.** Motif never addresses an occurrence; it addresses a `Segment` and edits a
> field on it. **`Segment` is a `CmObject` with a GUID** (`MasterLCModel.xml:259`), `Segment.Analyses`
> is `rel`/`seq` — structurally identical to in-scope rows like `LexEntryRef.ComponentLexemes` — and
> `WfiAnalysis.Evaluations` is `rel`/`col`. The index lives **inside the value**, not in the target.
>
> Durability is also much better than stated: liblcm's `AnalysisAdjuster`
> (`DomainImpl/AnalysisAdjuster.cs:16-60`) exists precisely to preserve analysis across edits — *"Any
> segment whose text is unaffected by edits should be unmodified in every other way, except that its
> begin offset should be adjusted."* Only segments overlapping the edited range split, merge, or
> vanish, under specified rules. That is a **narrow, detectable drift class**, not a systemic identity
> failure.
>
> This withdrew ADR 0017 decisions 3 and 4 and removed the plan's only time-sensitive item. The
> original text stands below because it is accurate about the class, and about the full-reparse path.

*Original finding:*
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

**J41. [D — and now load-bearing rather than tidy]**
The proposal says the semantic layer is unnecessary for human diff-based authoring and meaningful only
for AI and CLI. That matches ADR 0009's split and supplies its missing rationale: **Layer 0 is the
diff's output vocabulary; Layer 1 is the agent's input vocabulary.** Worth adopting as the stated
reason, because it makes the split load-bearing rather than stylistic.

**Raised in stakes by [ADR 0021](adr/0021-cli-is-the-full-surface-layer-1-churns.md)**, which makes
Layer 1 deliberately churn-tolerant while Layer 0 stays hashed. The split is now the *guard* on that
churn, not a stylistic preference: the mechanical test is **whether a change alters the bytes that get
hashed.** Confirming this rationale in writing is what stops "churn is fine" from reaching a `kind`
string or the canonical form.

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

**J43. [D→DECIDED 2026-08-05 — warn, enumerate, force; refuse only when consequences are unenumerable]**
[ADR 0021](adr/0021-cli-is-the-full-surface-layer-1-churns.md) decision 6. The owner's requirement was
per-item removal — *"don't add that lexeme"*, *"only rules 1 and 4, not 5"* — with dependencies warned and
a force required when removal causes further deletes.

So the answer is none of refuse / cascade / warn alone:

1. no dependents → it just happens;
2. severs a `requires` edge or orphans a dependent → **warn, naming every consequence**, then require an
   explicit force;
3. **force never means "guess"** — it means an enumerated consequence set was accepted. Unenumerable
   consequences are refused, not forced;
4. `proposalId` frozen, intent digest moves, new revision — never a mutation of an approved one.

**Not free for `delete`:** deleting an owner cascades and de-referencing does not (LibLCM leaves an
orphan), so a removal whose consequence set depends on *discovered* reach must recompute it rather than
reason from the declared footprint.

*Original framing: trivial mechanically, but it moves the intent digest while `proposalId` stays frozen,
and it can orphan a dependent operation or break a `requires` edge — refuse, cascade, or warn?*

**J45. [D] Do field-agnostic placement primitives exist alongside per-field kinds?**
Surfaced 2026-08-05 while renaming the shipped kind. The frozen conformance vector
`002-requires-placement-dependencies` contains `sequence/move` — **two segments, not three**, whose first
segment (`sequence`) is not one of the four domains, and whose payload is `target` + `placement` rather than a
field. Every other kind is `group/construct/verb` over a named field.

So either there is a second, field-agnostic family of primitives that moves an object within whatever sequence
owns it — in which case the naming rule in
[ADR 0023](adr/0023-derived-kind-names-required-descriptions.md) needs a stated exemption for it — or the
vector predates the vocabulary and a move should be `lexical/lexEntry/moveSenses`, in which case the vector's
*shape* is wrong and not merely its name.

Not urgent, and deliberately not guessed at: it changes the payload schema, not just a string. The vector was
left untouched.

**J44. [D→ANSWERED 2026-08-05 — the individual operation, subject to `requires`]**
Falls out of `J43`: removal and splitting are the same mechanism, and *"only rules 1 and 4, not 5"*
requires operation-level granularity. An atomic group stays indivisible — a split that would break one is
a `J43` case, so it warns and forces, or refuses. See
[ADR 0021](adr/0021-cli-is-the-full-surface-layer-1-churns.md) decision 6 and `MOT-18`.

*Original framing:*

**J44. [D] What is the unit of splitting a change set?**
Portability is nearly free — `ProposalStore` is already content-addressed objects plus manifests. The
constraint is the `requires` DAG: a split must not sever a prerequisite edge, and splitting a
multi-operation atomic group breaks all-or-none application.
