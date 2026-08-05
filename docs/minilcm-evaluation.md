# MiniLcm vs. Motif — decision report

> **HISTORICAL — a path not taken.** This document evaluates routing Motif's changes through a CRDT merge
> layer, or inventories the repositories that would have been involved. **That design was assessed and
> rejected** ([adoption report](harmony-adoption-report.md)); operations target LibLCM directly and there is
> no merge layer. Kept as the evidence behind that decision. **It is not a plan, and nothing in it is
> scheduled.** For the live plan see [Plan A](plan-motif.md).

> **SUPERSEDED (2026-07-27) by [ADR 0013](adr/0013-harmony-is-the-change-mechanism.md).** This
> document was written without `SIL.Harmony` having been read — it was not checked out, and the
> analysis reasoned around the gap instead of closing it. Harmony already provides semantic change
> objects, hash-chained commits, per-object snapshots, before/after state at any commit, validation,
> and `OpaqueChange` for changes a client cannot yet interpret. **The architectural recommendations
> below do not stand.** The platform findings do.


*Prepared 2026-07-27. Scope: can MiniLcm be the single API over LibLCM for everyone, should Motif be,
or are both needed. Written against source in both repositories plus `liblcm` (LibLCM itself, checked
out as a sibling repo and read directly for the referential-integrity claim).*

## Verdict

**Move to Motif's contract as the one change-authoring/assessment/approval API over LibLCM.
MiniLcm does not get extended to grammar and does not become a second change-authoring vocabulary.
It continues, unabsorbed for now, as the interactive lexical CRUD + multi-device sync surface it
already is — a client of the record, not a competing record.** This is verdict 2 as posed, not a
disguised verdict 3: there is one canonical way to describe, review, and approve a change; MiniLcm
keeps its live-query and live-edit job, which is a different job.

### The two or three facts that decide it

1. **MiniLcm has no grammar model at all, and its own maintainers say so.** Zero classes for
   phonological rules, natural classes, features, affix processes, compound rules, templates/slots,
   or MPR/inflection classes exist anywhere in `MiniLcm`, `LcmCrdt`, or `FwDataMiniLcmBridge` (verified
   by exhaustive grep, zero hits). `PartOfSpeech` — the one grammar-adjacent class MiniLcm has — is a
   bare `{Id, Name}` record whose own source says `// TODO: Probably need Abbreviation in order to
   match LCM data model` (`MiniLcm/Models/PartOfSpeech.cs:7`). Motif's own ADR already recorded this
   after reading the same code: *"MiniLcm's lexicon-only model, its CRDT/Harmony sync, update-proxies,
   and media/search/sorting are explicitly not reused"* (`docs/adr/0003-feasibility-findings.md:49`).
   Grammar is not a small gap: it is **230 of 473 in-scope LibLCM fields and 30 of 54 constructs**
   (computed from `manifest/liblcm-inventory.tsv`), and it is the entire reason Motif exists
   (ADR 0010).
2. **MiniLcm's CRDT ordering primitive is verified unsound for exactly the case that matters most —
   semantically-ordered grammar.** `LcmCrdt`'s `OrderPicker` assigns each ordered item an independent
   floating-point `Order`, and `SetOrderChange.ApplyChange` merges it as a plain last-writer-wins scalar
   assignment — `entity.Order = Order` (`LcmCrdt/Changes/SetOrderChange.cs:14-18`). Position is stored as
   an absolute number, never as a relation to its neighbours; the edit-time picker is explicitly
   comfortable with that, commenting *"there's about a 50/50 chance that that's what actually should
   happen"* about its own placement heuristic (`LcmCrdt/OrderPicker.cs:24-27,49-52` — note this comment
   is about local placement, not about merge resolution; the merge evidence is `SetOrderChange`). That is
   an acceptable cost for sense order or example order, where a slightly-off position is cosmetic. It is
   not acceptable for phonological rule order, where order **is** meaning (feeding/bleeding) and for
   alpha-variable arrays, where **index is identity** with a hard 24-per-rule ceiling
   (`docs/hc-grammar-map.md:40-42`, `docs/api-surface-layer1.md:122-131`). Linguistic Assistant reached
   the same conclusion independently before this repository existed (`README.md:110-111`, *"no CRDT for
   ordered grammar"*). This is a verified structural mismatch, not a hunch.
3. **Referential integrity — MiniLcm's stated selling point — is LibLCM's, not MiniLcm's, and it is
   more complete than this report originally claimed.** *(Corrected on review — see the correction
   note below.)* `CmObject.Delete()` → `ClearIncomingReferences()` (`liblcm/src/SIL.LCModel/DomainImpl/CmObject.cs:1728-1733`)
   walks the incoming-reference bag and calls `RemoveAReference` on **every** referrer. Atomic
   references are handled by the generated `RemoveAReferenceCore`; **reference collections and
   sequences are handled too**, because `LcmReferenceCollection<T>` and `LcmReferenceSequence<T>` are
   themselves `IReferenceSource` implementations that remove the item
   (`liblcm/src/SIL.LCModel/DomainImpl/Vectors.cs:664,782-785` and `:1653,1836`). MiniLcm's FwData
   backend inherits all of this by delegating to `.Delete()`
   (`FwDataMiniLcmBridge/Api/FwDataMiniLcmApi.cs:348,416,494,1388,1646`). Its own
   `GetReferences`/`RemoveReference` contract (`MiniLcm/Models/IObjectWithId.cs:24-26`,
   `Models/Sense.cs:35-45`) exists because the **CRDT** backend has no LibLCM underneath it to do the
   job — it is a re-implementation for ~13 lexical types, not an addition on top of LibLCM.
   **Decision consequence:** referential integrity is an argument for writing through LibLCM, which
   both candidates do. It does not discriminate between them.
4. **MiniLcm's CRDT sync is not a replacement for Chorus/Mercurial merging — it is a second store that
   still needs a bespoke 3-way reconciliation against `.fwdata`, exactly as Motif's own design already
   assumes.** `FwLiteProjectSync/CrdtFwdataProjectSyncService.cs` diffs a saved snapshot against both the
   CRDT store and the live `.fwdata`, then applies each side's delta to the other
   (`CrdtFwdataProjectSyncService.cs:115-146`). Harmony's commit-log CRDT only replaces Chorus for
   CRDT-to-CRDT sync (device-to-device, or device-to-cloud); it does not touch `.fwdata`, which is still
   Chorus/Mercurial's job. That is Motif's S2 position — *"two merges; only one is forbidden"*
   (`docs/stage2-change-management.md:27-33`) — independently confirmed from the Lexbox source rather
   than assumed.

### Correction note (2026-07-27, on review)

Two claims in the draft of this report were checked against source and did not survive:

- **Deciding fact 3 was backwards.** The draft read only the generated `RemoveAReferenceCore.vm.cs`
  template — which does iterate `AtomicRefProperties` only — and concluded that reference collections
  get no cleanup on delete. That skipped the other half of the mechanism: collections and sequences
  are separate `IReferenceSource` implementations registered in the same incoming-reference bag, and
  they *do* remove the deleted item (`Vectors.cs:782-785`, `:1836`). It also mapped the finding onto
  Motif's issues C4 and C6, which are about different things: **C4** is the *opposite* direction
  (clearing a reference orphans an owned target, `docs/issues.md:59`), and **C6** is HCLoader crashing
  at *grammar-load* time on a dangling reference (`docs/issues.md:61`), not a LibLCM write-path gap.
  C4 and C6 remain open and real; they are simply not evidence about MiniLcm.
- **The Harmony caveat is weaker than the draft claimed.** The draft named Harmony's uninspected
  internals as "the single fact most worth checking" before treating the verdict as final. But the
  order model is decided *above* Harmony: `SetOrderChange.ApplyChange` assigns `entity.Order = Order`
  (`LcmCrdt/Changes/SetOrderChange.cs:14-18`), a plain scalar. Harmony's log decides *which*
  `SetOrderChange` wins; it cannot make a scalar carry a relation. Harmony's internals could change
  the convergence story, not the representation story.

### Confidence

**Medium-high on the technical findings, medium on the organizational recommendation.** Every claim
above is cited to source in this repository, the Lexbox repository, or LibLCM itself; none is asserted
from memory of what these projects are "supposed" to do. The parts I could not verify are listed at the
end and would, if they turned out differently, change the sizing but not the direction of the verdict —
except the one flagged in "What would change this verdict" below, which could.

---

## Method note: verified vs. inferred

Everything in the "verified" column below was read directly:

- LibLCM's own delete/reference-cleanup code, from the `liblcm` sibling checkout
  (`src/SIL.LCModel/DomainImpl/CmObject.cs`, `src/SIL.LCModel/LcmGenerate/RemoveAReferenceCore.vm.cs`).
- MiniLcm's full write API (`IMiniLcmWriteApi.cs`, `IMiniLcmReadApi.cs`), its model classes (`Models/*.cs`),
  its FwData-backed delete paths (`FwDataMiniLcmBridge/Api/FwDataMiniLcmApi.cs`), its CRDT delete/order
  paths (`LcmCrdt/CrdtMiniLcmApi.cs`, `LcmCrdt/OrderPicker.cs`, `LcmCrdt/Changes/*.cs`), and its
  shared cross-backend conformance tests (`MiniLcm.Tests/*.cs`, inherited by both
  `LcmCrdt.Tests` and `FwDataMiniLcmBridge.Tests`).
- The FwData↔CRDT sync tool (`FwLiteProjectSync/CrdtFwdataProjectSyncService.cs`, `EntrySync.cs`).
- Motif's own docs, ADRs, and coverage manifest, already-verified per the task brief.

What I could **not** verify from source, because the code is not checked out locally, and is marked as
inference below: the internals of `SIL.Harmony` itself (the generic CRDT/commit-log engine MiniLcm's
CRDT backend is built on — it lives in `github.com/sillsdev/harmony`, referenced only as a NuGet package
or an optional sibling-repo project reference, `backend/Harmony.Linq2db.References.props`, neither of
which is present in this environment). Where a claim depends on Harmony's internals rather than on
LcmCrdt's own code, it is flagged.

---

## The use case, mapped to both systems today

The product is: a list of **changes** proposed by people or AI, each carrying a **PanGloss report** and
**word-list analysis** answering "is it better?", plus a **conversation** and an **approval**.

| Product ingredient | MiniLcm today | Motif today |
| --- | --- | --- |
| A **change** as a first-class, reviewable object | No such concept. Writes are direct method calls (`CreateEntry`, `UpdatePartOfSpeech`, …) or CRDT `Change` objects that are committed immediately — there is no "draft, then review, then apply" object. | Yes — the entire point. Canonical Change Set / Assessment / Receipt are three separate artifacts by design (`docs/architecture.md:100-131`). |
| **Approval / review gate** before a change lands | Not present. `CommentThread`/`UserComment` exist (see next row) but there is no `status: proposed/approved/rejected` on anything resembling a change. | Yes — per-package `status` (proposed/approved/applied/rejected), effect-digest-scoped approval, drift-invalidated (`docs/stage2-change-management.md:51-59`, S4). Designed, not yet built as an app — but it is the shape of the contract already, not a bolt-on. |
| **Conversation** attached to a change/object | Yes, and it's real and shipping: `CommentThread`/`UserComment`, CRDT-native, soft-delete cascade on subject deletion, fork-warning UX for concurrent replies (`CONTEXT.md`, `LcmCrdt/Changes/Comments/*.cs`). This is ahead of Motif. | Not built. Attachments/metrics are designed to carry provenance and be listed/diffed (S5), but no comment-thread concept exists. |
| **PanGloss report** and **word-list analysis** as attached, provenance-stamped evidence | Not modeled at all — MiniLcm has no concept of an external tool's report attached to a change, because it has no concept of grammar or of a change-as-object. | Designed in detail: labelled, typed-metric, intent-digest-bound attachments (`docs/stage2-change-management.md:61-86`, ADR 0011 §4). Not yet implemented (`export` doesn't exist in the shipped CLI). |
| **Grammar** as an editable, versioned surface | None. | The primary purpose (ADR 0010); 230/473 in-scope fields mapped in detail, zero yet implemented. |
| **Lexical** CRUD, multi-device live sync, offline editing | Ships today, in production, on Windows/Android/iOS/macOS + web (`FwLiteMaui.csproj`, `FwLiteWeb`), with 431 `[Fact]`/`[Theory]` tests and real CI (`fw-lite.yaml`, `develop-fw-headless.yaml`). | Out of scope by design (`docs/architecture.md:329-337`; "It does not own: … hosting, Git history, or database storage"). One operation implemented end to end, 82/82 tests, no CI. |

Read plainly: **the "change with report, conversation, approval" product does not exist in either
system today.** MiniLcm has the conversation half; Motif has the change/assessment/approval half.
Building the product means building the missing half onto whichever base is structurally capable of
carrying grammar — which the next sections show is not MiniLcm.

---

## Point-by-point comparison

| Dimension | MiniLcm (+ LcmCrdt + FwDataMiniLcmBridge) | Motif |
| --- | --- | --- |
| **Maturity** | Ships in production. `languageforge-lexbox` has 3,911+ commits, active CI (`fw-lite.yaml`, `develop-fw-headless.yaml`), a MAUI app on 4 platforms plus a web server, 431 test methods. | 40 commits, one operation (`lexical/sense/setGloss`) implemented end to end, 82/82 tests green, **no CI workflow exists** (`ls .github/workflows` → not found). Design docs are extensive and precede the code. |
| **Domain coverage** | Lexical only: `Entry`, `Sense`, `ExampleSentence`, `Picture`, `Publication`, `SemanticDomain`, `ComplexFormType`/`Component`, `MorphType`, a name-only `PartOfSpeech`, `WritingSystem`. Zero phonology/morphology/feature-system classes. | Model surface is 100% classified (898 raw rows → 473 in-scope, 100% classified — `manifest/README.md`). Grammar (`Group=grammar`) is 230 fields / 30 constructs; lexical is 157 fields / 23 constructs. Only 1 field is actually wired to an operation today. |
| **Referential integrity — mechanism** | Two different mechanisms in two backends. FwData backend: delegates to LibLCM's own `CmObject.Delete()`, no MiniLcm-level guard code found (grep for "ReferringObjects/dangling/orphan/referential" across `MiniLcm`, `LcmCrdt`, `FwDataMiniLcmBridge`: zero hits). CRDT backend: an explicit, systematic per-type contract — every `IObjectWithId` declares `GetReferences()`/`RemoveReference(id, time)` (`MiniLcm/Models/IObjectWithId.cs:19-29`), invoked (per `CONTEXT.md` and code inspection of `Sense.RemoveReference`) when a referenced object is deleted, covering **both** atomic references and collection membership (e.g. `Sense.RemoveReference` strips a deleted item out of `SemanticDomains`, a collection — `MiniLcm/Models/Sense.cs:36-46`). | Referential integrity is LibLCM's own engine plus Motif's *disclosure* obligation, not an independent guarantee: composers emit an explicit `delete` when they can prove an orphan (ADR 0009 §6), `delete`-with-referrers is discovered-footprint and forces full re-assessment (ADR 0009 §5), and the issues register documents the exact upstream gaps (C4, C6) that must be defended against by validation before write, not fixed. |
| **Referential integrity — actual coverage, verified against LibLCM source** *(corrected on review)* | LibLCM's `CmObject.Delete()` calls `ClearIncomingReferences()` (`liblcm/src/SIL.LCModel/DomainImpl/CmObject.cs:1728-1733`), which walks the incoming-reference bag and calls `RemoveAReference` on every referrer. Referrers come in three kinds and **all three are covered**: the referring object itself, for atomic properties, via the generated `RemoveAReferenceCore` (`LcmGenerate/RemoveAReferenceCore.vm.cs:11-25`); `LcmReferenceCollection<T>`, which implements `IReferenceSource.RemoveAReference` as `Remove((T)target)` (`Vectors.cs:664,782-785`); and `LcmReferenceSequence<T>`, likewise (`Vectors.cs:1653,1836`). So `rel/col` and `rel/seq` references **are** cleaned up on delete. MiniLcm's FwData backend inherits this by delegating to `.Delete()` (`FwDataMiniLcmApi.cs:348,416,494,1388,1646`) and adds no guard code of its own (grep for "ReferringObjects/dangling/orphan/referential" across `MiniLcm`, `LcmCrdt`, `FwDataMiniLcmBridge`: zero hits — it does not need any). The CRDT backend's `GetReferences`/`RemoveReference` contract re-implements the same semantics, collections included (`Models/Sense.cs:35-45`), because that backend has no LibLCM underneath. **This dimension does not discriminate between the candidates.** What remains genuinely unguarded is different and narrower: C4's *orphaned owned target* case, and C6's HCLoader load-time crash on a stale reference — neither addressed by either system. | Designed to treat both gaps as hazards to detect and disclose, not silently trust: pre-apply MPR referential-integrity validation and 24-alpha-variable-ceiling validation are both named obligations (`docs/hc-grammar-map.md:111-121`), not yet implemented. |
| **Grammar/lexical structural fit** | Lexical: senses/examples/pictures are unordered-content, positionally-ordered lists — exactly what fractional order (`LcmCrdt/OrderPicker.cs`) is good at. Grammar: never modeled, so this has not been tested against feeding-ordered rules, index-as-identity alpha variables, or the 5-parallel-slot-sequences-over-one-pool shape at all. | Explicitly designed around the distinction: `ComparisonClass` in the manifest is `unordered`/`positional`/`feeding`/`index-as-identity`, with feeding and index-as-identity called out as needing neighbour-content-aware (not just neighbour-identity-aware) diffing (`docs/api-surface-layer1.md:106-140`, `docs/conflicts-and-rebase.md:159-160`). |
| **Sync / "modern merge" story** | Two separate mechanisms, not one: (a) Harmony commit-log CRDT sync between CRDT replicas (device↔device, device↔cloud) — real, shipping, HTTP-based (`LcmCrdt/RemoteSync/CrdtHttpSyncService.cs`); (b) a bespoke, hand-written 3-way reconciliation between the CRDT store and `.fwdata` (`FwLiteProjectSync/CrdtFwdataProjectSyncService.cs:115-146`, `EntrySync.cs`), which diffs a saved snapshot against both live states and applies each side's delta to the other — this is not CRDT machinery, it is ordinary diff/patch code, and it still runs **beside** Chorus/Mercurial's own `.fwdata` Send/Receive, not instead of it. | No merge implementation exists yet (0 commits). Design position (S2) is explicit: `.fwdata` keeps merging via Chorus 3-way on every Send/Receive — that is correct and desired; Motif never merges change-set histories or three-way-merges proposed intent (`docs/stage2-change-management.md:27-33`). |
| **Approval / change-review model** | None. Comments exist (see product-mapping table); "propose, review effect, approve, apply" does not. | The contract's central shape: Change Set / Assessment / Receipt are separate, hashed artifacts; approval is per-effect-digest and drift-invalidated (S4). Designed in detail, unimplemented as an app. |
| **Cross-language / cross-process access** | Refit-based HTTP API (`FwLiteWeb`) plus in-process C# calls; no documented framing for a Python/Rust consumer analogous to Flexicon/PanGloss's needs, though the HTTP surface could serve that role for lexical data. | Explicitly designed for Python (Flexicon, Linguistic Assistant) and Rust (PanGloss) consumers via a CLI/JSON protocol — still open (issue B13) but a first-class design goal, not an afterthought. |
| **Test discipline** | 431 `[Fact]`/`[Theory]` methods across `MiniLcm.Tests`/`LcmCrdt.Tests`/`FwDataMiniLcmBridge.Tests`, many run as shared base classes against **both** backends (e.g. `PartOfSpeechTestsBase`, `BasicApiTestsBase`) — a genuine conformance-suite pattern Motif does not yet have. | 82/82 green, but against one operation and one backend; no shared multi-implementation conformance harness exists (there is only one implementation). |
| **Extensibility posture** | Adding a field means: model class change, validator change, both backends' Create/Update/Sync helpers, EF Core migration, CRDT `Change` class, JSON schema — evidently workable (it has happened repeatedly: morph types, custom views, comments were all added this way) but each is hand-written, not generated. | Kinds are meant to be generated from the coverage manifest (ADR 0009 §3, ADR 0012) — 332 kinds / 12 handlers for the HC-reachable surface — but the generator does not exist yet (issue: "nothing yet generates operation kinds from it"). |

---

## Question 1 — What would it take to make MiniLcm the LibLCM API?

Concrete, sized, drawn from the coverage manifest rather than vibes:

1. **A grammar domain model, from zero.** MiniLcm's `Models/` directory has no phonology or morphology
   classes at all. Building HC-reachable grammar coverage (Motif's own completeness bar, ADR 0010) means
   new model types, validators, sync-helpers, and CRDT `Change` classes for roughly **30 new constructs**
   covering **230 fields** (`manifest/liblcm-inventory.tsv`, `Group=grammar`, `Scope=in`): natural classes
   (`PhNCSegments`/`PhNCFeatures`), phonological rules with structural RHS plus the separate metathesis
   shape, affix process rules with 4 output-action subtypes, compound rules (endo/exo), affix templates +
   slots (with the two-of-five-slot-sequences complication already documented in
   `docs/hc-grammar-map.md:36-39`), co-occurrence/adhoc-prohibition rules, MPR/inflection-class families,
   feature systems (`FsClosedFeature`/`FsComplexFeature`), and stem names. For comparison, MiniLcm's entire
   *existing* lexical model is 23 constructs / 157 fields — this is a bigger build than everything MiniLcm
   has shipped to date, in a domain its team has not worked in.
2. **A new write path in (at least) the FwData backend**, since that is the one that touches real LibLCM.
   Each of the ~12 `(Kind, Card, Sig)` shapes Motif identifies (`docs/api-surface-layer1.md:69-93`) needs
   a correct LibLCM lowering — factories, owning-slot semantics, the "create-into-occupied implies detach"
   rule for `owning/atomic` replacement, `reparent` for sequences. None of this exists in
   `FwDataMiniLcmBridge` today for grammar classes; it would be new code, not adapted code.
2b. **A hand-written `Sync` pair per construct in the FwData↔CRDT reconciler.** This cost is visible in
   the shape of `CrdtFwdataProjectSyncService.SyncInternal` (`:115-146`): it is a literal, hand-enumerated
   list — `WritingSystemSync`, `PublicationSync`, `PartOfSpeechSync`, `SemanticDomainSync`,
   `ComplexFormTypeSync`, `MorphTypeSync`, `EntrySync` — each invoked twice, once per direction, against a
   saved snapshot. Seven types today; every one of the ~30 new grammar constructs would add another such
   pair, plus a CRDT `Change` class and an EF Core migration. The scaling is linear and manual by
   construction. That is not a criticism of the design — it is entirely reasonable for a 23-construct
   lexical model — but it is the specific curve that a 30-construct grammar addition would have to ride,
   and it is the curve Motif's generated-kinds bet (332 kinds / ~12 handlers, ADR 0012) exists to avoid.
   **Caveat in fairness: MiniLcm's linear approach demonstrably works and has shipped; Motif's generator
   does not exist yet and its leverage is projected, not measured.**

3. **Semantically-aware ordering and identity, not fractional order.** Three specific hazards the fractional
   `OrderPicker` scheme cannot represent correctly, all independently documented and verified against
   HCLoader by Motif: phonological rule order is feeding/bleeding (a neighbour's *content*, not just its
   presence, changes the meaning of your edit — `docs/api-surface-layer1.md:116-119`); alpha-variable names
   are assigned by first-appearance scan with a **hard 24-per-rule ceiling**, so index *is* identity and a
   `move` silently renames every later variable (`docs/hc-grammar-map.md:40-42,74-79`); `MoAffixProcess.Output`
   resolves against `Input` by **position**, so reordering `Input` silently renumbers every `Output` mapping
   (`docs/api-surface-layer1.md:133-137`). A CRDT ordering scheme whose own author's comment admits a "50/50
   chance" of correct resolution on concurrent edits (`LcmCrdt/OrderPicker.cs:26-27`) is not a safe substrate
   for any of these without new, non-CRDT-native conflict logic — which is most of the engineering Motif's
   `Diff`/`Runner` packages already exist to do.
4. **MPR/collection referential-integrity validation that does not exist anywhere in this codebase today.**
   As shown above, LibLCM's own generated cascade only covers atomic references; MiniLcm adds nothing for
   collections in the FwData backend. Grammar's MPR surface is exactly the shape that is unguarded
   (`docs/hc-grammar-map.md:76-79`, issue C6) and crashes on real data (`GenerateHCConfig.exe` on the Amharic
   project). This has to be built new, in either system.
5. **A change/review/approval layer**, since none exists in MiniLcm — Change Set, Assessment, Receipt,
   effect-digest-scoped approval, drift handling, labelled provenance-stamped attachments for PanGloss
   reports and word-list metrics. This is not a small addition; it is most of what Motif's `Contract`,
   `Runner`, and (unbuilt) `Diff` packages are for.
6. **A cross-process protocol for PanGloss (Rust) and Linguistic Assistant (Python)** comparable to what
   Motif is already designing (issue B13) — MiniLcm's access story today is C#-in-process or HTTP-for-the-
   MAUI-frontend, neither aimed at this.

**Net size estimate, from the manifest:** roughly **230 fields / 30 constructs** of net-new grammar surface,
on top of a change/review/approval layer that does not exist, on top of an ordering primitive that would need
to stop being CRDT-native for the ordered-grammar cases. This is not "extend MiniLcm a bit" — it is building
Motif's grammar half and its contract half, inside MiniLcm's codebase, while discarding MiniLcm's one
genuine advantage for this workload (CRDT sync) for the cases that matter most.

---

## Question 2 — What would we irrevocably lose?

Distinguishing **irrevocable** (permanently gone) from **merely deferred** (postponed, recoverable later):

**Irrevocable if MiniLcm is extended to grammar and made the sole API:**

- **The generic, offline-first CRDT sync story for grammar edits.** If grammar has to bypass fractional
  ordering for feeding-ordered rules and index-as-identity alpha pools (point 3 above), grammar edits stop
  benefiting from Harmony's device-to-device sync story that lexical edits enjoy today. You would be
  running two different consistency models in one API depending on which field you touched — a worse
  outcome than either system alone, and it cannot be un-happened once users depend on it either way.
- **A clean separation between "query/interactive-edit API" and "change-authorship/review API."** Once a
  change/approval layer is bolted onto MiniLcm's direct-mutation model, undoing that coupling later (to
  adopt a purpose-built one) means a second migration for every consumer that adopted the bolted-on version.

**Irrevocable if Motif is adopted as the sole API and MiniLcm is fully absorbed/discontinued (the harder
version of verdict 2, which this report does *not* recommend doing on the current timeline):**

- **FieldWorks Lite's shipping multi-device product**, including comments/conversation, offline
  editing, and the MAUI apps on four platforms — none of that exists in Motif, and Motif explicitly
  does not want to own it ("It does not own: … hosting … UI", `README.md:88-93`). Rebuilding it would cost
  more than everything sized in Question 1.
- **431 tests' worth of accumulated conformance knowledge** about how CRUD-shaped lexical edits actually
  behave across two backends — not literally lost (the tests are read-only historical evidence either way)
  but the *running, continuously-verified* guarantee they provide would stop being exercised in production.

**Deferred, not irrevocable, either way:**

- Grammar-experimentation and reviewable-change tooling (Motif's job) not existing yet — recoverable by
  building it, on either substrate, later.
- The cross-process protocol for Python/Rust consumers (issue B13) — open in both systems, not a sunk loss.
- PanGloss/report-attachment tooling — designed but unbuilt in Motif; entirely absent in MiniLcm; buildable
  in either.

The recommended verdict (Motif is the change-authorship API, MiniLcm keeps its current job) loses
**nothing irrevocably** — it is the option that keeps both investments intact and adds the missing piece
(a reviewable grammar-change layer) to the system that can structurally hold it.

---

## Question 3 — What gets more complicated, and what gets easier?

**More complicated, honestly, under the recommended verdict:**

- **Two systems to run, deploy, and reason about**, for as long as both exist. A developer touching lexical
  data asks "which API," and the honest answer is "MiniLcm for live editing, Motif if the edit needs
  review/approval/a PanGloss report attached" — a real cognitive cost, and precisely the shape the prompt
  flags as undesirable if landed on carelessly.
- **Any workflow that spans both** (e.g., a reviewed lexical change that should also show up live in
  FieldWorks Lite) needs an explicit reconciliation step, on top of the one that already exists between
  `.fwdata` and the CRDT store. That is a second seam, not a first one — but see "easier" below: the seam
  between Motif and `.fwdata` is the *same* seam FwLiteProjectSync already has to cross, not a new kind
  of seam.
- **Motif's own grammar build is not simplified by any of this** — none of MiniLcm's code is reusable for
  it (confirmed, ADR 0003), so the 230-field/30-construct build sized above still has to happen entirely
  inside Motif, on its own timeline, with its current resourcing (one operation, no CI).

**Easier, honestly:**

- **Motif does not have to build a multi-device sync story, comment threads, or mobile/web clients** —
  those keep being MiniLcm's job, and MiniLcm is good at it (production-shipping, tested, real CI).
- **MiniLcm does not have to solve semantically-ordered grammar merge** — a problem its own ordering
  primitive is verified not to solve today, and one Linguistic Assistant already independently concluded
  needs a non-CRDT approach.
- **The referential-integrity validation Motif already scoped (C4/C6 defenses, 24-alpha-variable
  pre-check) does not have to be re-derived** — it was already worked out by reading HCLoader directly, and
  it is validation logic, not a data model, so it ports independent of which system ships it.
- **Reuse continues to flow one direction cleanly**: Motif already copy-adapted ~1,000-1,200 lines of
  MiniLcm's project-load plumbing under MIT (`FwDataProjectLoader.cs:1-2`, `HeadlessLcmUi.cs:1`) — a real,
  working precedent for "MiniLcm's engineering investment keeps paying off inside Motif" without a runtime
  coupling between the two projects' release trains.
- **The manager's Chorus-migration goal is served honestly rather than by a false promise.** Harmony's CRDT
  sync is real modernization for the CRDT-to-CRDT case; Motif's effect-comparison design is real
  modernization for "did my reviewed change still mean what I approved" (replacing hand-verified diffs);
  neither claims to replace Chorus/Mercurial's `.fwdata` merge outright, and the evidence (S2, and
  `CrdtFwdataProjectSyncService`) shows nobody who has actually built a working system claims otherwise
  either.

---

## Is this a false trichotomy?

Partially, and it is worth saying so rather than quietly picking a fourth option. The three verdicts as
posed treat "the API over LibLCM" as one thing. The evidence says there are structurally two different
kinds of API in play:

1. **A query/interactive-CRUD API** — "list entries," "get this sense," "set this gloss right now,"
   with live multi-device sync. MiniLcm does this well and there is no reason to replace it.
2. **A change-authorship/reasoning/review API** — "here is a proposed change, here is its exact effect,
   here is external evidence about whether it's good, review and approve it, then apply it atomically."
   Nothing in MiniLcm does this; it is what Motif's contract is built for.

Requiring one artifact to be both is not obviously the right frame, and the report's recommended verdict
is really: *pick one API for each of those two jobs, and make sure the change-authorship one — which
must cover grammar — is Motif's contract, not a new one grown inside MiniLcm.* This is not proposed as a
silent fourth verdict; it is offered as the reasoning underneath why verdict 2 is correct rather than
verdict 3, and it does not change the recommendation.

---

## What would change this verdict

- ~~**If Harmony's actual commit-log/merge internals turned out to handle feeding-ordered and
  index-as-identity sequences correctly**, the CRDT-unsuitability argument would weaken substantially.~~
  **Downgraded on review.** LcmCrdt decides the order *representation* above Harmony:
  `SetOrderChange.ApplyChange` is `entity.Order = Order` (`LcmCrdt/Changes/SetOrderChange.cs:14-18`).
  Harmony's log determines which such change wins, not whether a scalar can encode a feeding relation.
  Reading Harmony would refine the convergence story; it cannot rescue the representation. Still worth
  reading before a final commitment, but it is no longer the load-bearing unknown.
- **If someone demonstrates that grammar ordering can ride on a fractional scalar in practice** — e.g.
  because grammar editing is single-writer in every real workflow (see the last bullet in "what I could
  not determine"), so concurrent reorders never arise — deciding fact 2 becomes a structural objection
  without an operational cost, and the calculus shifts toward the cheaper option.
- **If the organization's real near-term need is overwhelmingly lexical** (e.g., grammar experimentation
  turns out to be a niche workflow for a handful of linguists rather than a broad need), the cost of
  running two systems might outweigh the benefit of a purpose-built grammar layer, and "just MiniLcm,
  skip grammar entirely" becomes a live option this report did not fully cost out.
- **If Motif's build stalls indefinitely** (it is currently one operation, no CI, 40 commits), the
  practical recommendation degrades to "MiniLcm is what actually exists," regardless of which is
  structurally cleaner. A decision to adopt Motif's contract should come with a commitment to actually
  resourcing it past the walking skeleton it is today.

## What I could not determine from source

- **Harmony's (`github.com/sillsdev/harmony`) own internals** — the commit-log data structure, causal/
  hybrid-logical-clock ordering, and exactly how/when `RemoveReference` is invoked engine-side. Not
  checked out in this environment (only referenced via NuGet package or an optional sibling-repo path,
  `backend/Harmony.Linq2db.References.props`); LcmCrdt's own code and `CONTEXT.md`'s explicit description
  of "Harmony reference cascade" are strong indirect evidence but not a source read of the mechanism
  itself.
- **Whether `FwLiteProjectSync`'s bidirectional diff-and-apply genuinely resolves same-field concurrent
  edits or just has the second-applied side win** — read enough to characterize it as a two-way diff run
  in both directions against a shared snapshot (not a CRDT merge), but did not trace a concrete conflicting-
  edit scenario end to end.
- **Actual production usage scale/numbers for FieldWorks Lite** (how many projects, users, or languages)
  — inferred "in production" from CI, multi-platform build targets, and commit volume, not from a usage
  dashboard.
- **Whether the grammar surface would, in practice, need CRDT sync at all** — it is plausible that grammar
  editing in practice happens single-writer, single-session (matching Motif's own single-writer
  assumption, C2), in which case the CRDT-unsuitability argument matters less operationally than it does
  structurally. Motif's own docs assume single-writer for its current apply model; whether that is a
  permanent design choice or a v1 simplification was not settled in what I read.
