# Readiness — what to grill, and in what order

*Triage of 2026-08-03, updated as research lands. **Not everything here is a question for you.** Most
of what looked like a decision was a fact nobody had gone and read.*

## Status

| | Count | Meaning |
| --- | --- | --- |
| ✅ **Closed** | 20 | Answered from source. Do not grill; read the answer. |
| ✅ **Decided** | 24 | `H30`, `G28`, `G27`, `G29`, `F26`/`F22`, `J43`, `J44`, `B5`, `B7a`, `B18`, `B19`, `B20`, `Group`, `F23a`, `B9a`, `B9b`, `J42a`, `I35a`, `I35b` — ADRs 0017–0027. |

**The contract is now fully mechanical.** No hand-authored value feeds a hashed identifier: verbs, comparison
behaviour (five cited exceptions), construct and group are all derived and build-checked. The manifest's
remaining authorities are `Scope`, `Construct` (what ships together) and `domain` (who reviews) — none hashed.
| 📐 **Spike** | 1 active, 2 deferred | `A1` is in this repo and on the path. `E19` and `F26a` are other-repo and scope 2. |
| ❓ **Yours** | ~29 | Genuinely a decision. This is the grill. |

**All research and the one on-path spike have landed.** `A1` was measured against real Sena 3 on 2026-08-05
and closed; `E19` and `F26a` are deferred by decision.

**Sequencing changed, 2026-08-05** ([ADR 0020](adr/0020-cli-first-fieldworks-planned-not-built.md)):
scope 1 is the LibLCM seams proved through the CLI with an AI agent as author; scope 2 is the FieldWorks
integration, planned and not built. Items below inherit that — anything FieldWorks- or Chorus-shaped is
still worth deciding on paper, but it is not what unblocks work this month.

Three of the twelve closures (`F23`, `B9`, `J42`) did not vanish — they turned into **concrete
proposals with named alternatives**, each leaving one narrow residual (`F23a`, `B9a`, `J42a`). That is
the point of the exercise: a decision between two documented positions costs a minute; an open design
question costs an hour.

## ✅ Closed by research — read, don't grill

| Item | Answer |
| --- | --- |
| `A2` | Two `LcmCache` instances **do** coexist (liblcm's own `BEPPortTests`) — but proven with a *blank* source project. Scale and in-FieldWorks coexistence still open under `A1`. |
| `D17` | Answered by your own delivery statement: Motif ships a CLI and a FieldWorks integration, so grammar authoring is desktop-only by construction. |
| `F25` | The blocker is **upstream of diff**: `ObjectSnapshot` is `{CanonicalId, MultiUnicodeFields}` and supports 1 of 473 rows. Cost is unmeasurable until a snapshot substrate exists. |
| `H31` | **Corrected 2026-08-05 — the original answer overstated it.** True of the `AnalysisOccurrence` *class*; false of what Motif addresses. Motif addresses a `Segment` (a GUID-bearing `CmObject`) and edits `Segment.Analyses` (`rel`/`seq`); the index lives inside the value. liblcm's `AnalysisAdjuster` preserves segments whose text is unaffected. Narrow, detectable drift class — not a systemic identity failure. This withdrew ADR 0017 decisions 3 and 4. |
| `H32` | **Both, on two axes.** Approval is global to the `WfiAnalysis` (durable GUID); occurrence assignment is positional (no identity). → spawns `H32a`. |
| `H33` | Tri-state `Opinions` per agent, fixed well-known GUIDs. → spawns `H33a`. |
| `I35` | **Yes, computable.** FieldWorks already ships it as `NumUserApprovedAnalysesMissing`; PanGloss morpheme identity is the LibLCM MSA GUID on the `.fwdata` path. → spawns `I35a`, `I35b`. |
| `I38` | **No.** Rules/strata/templates have no retained GUID, there is no sentence concept, and existing coverage is synthetic-fixture capability coverage. Branch coverage is a build. |
| `E20`, `E21` | Facts, not questions. PanGloss has no release pipeline; the CLI is process-per-command. |
| `F23` | **Splits.** Content equality is *already* canonical if keyed on the **effect** digest, which the contract already requires stable under lowering (`change-set-contract.md:548`). The intent digest needs four more freezing rules. → residual `F23a`. |
| `B9` | Kubernetes is the closest precedent — and ADR 0009 §1 already borrowed its `group/construct/verb` naming. Policy drafted: additive-minor, digest-meaning-changes-major, **dual-floor** window. → residual `B9a`. |
| `J42` | **Already decided.** Resolved operations at rest with the query as non-hashed provenance is verbatim ADR 0009 §1 (`adr/0009:38-40`). The query-as-truth alternative is the `managedFields` pattern motif already rejected. → residual `J42a`, a Terraform-style staleness gate. |
| `A3` | **Yes.** `IProjectIdentifier` is 7 trivial public members; `MemoryOnlyBackendProvider` being internal is irrelevant because liblcm wires it itself from `projectId.Type`. Write ~15 lines. The scratch need not live on disk. |
| `A4` | **Clean, premise wrong.** FieldWorks *already* resolves `System.Text.Json 9.0.14` on net48, above Motif's 8.0.5 floor, via a `Directory.Packages.props` pin made for ICU reasons. No new pins. |
| `B8` | **24 fields / 10 classes, not 37 / 19** — 13 of the ADR 0012 rows are never read by `HCLoader`. Minimal `LexEntry` needs 4 classes; full L0 pulls 5 grammar classes forward. → `B8a`. |
| `C10` | **Exactly 4 rows**, all `Group=lexical`. Small enough that the decision is now cheap. → `C10a`. |
| `C13` | **Hand-written, and already written.** Call `IPhRegularRule.FeatureConstraints`; the traversal dispatches on `ClassID` through three classes the manifest never names. |
| `B6` | **B19 understated, B20 reconciled.** Only 26.4% of construct names are mechanical; 41.5% have no relationship to any class. B20's fan-out cannot be done from `Class`/`Field` alone — it needs runtime owner identity. |
| `B7` / `B7a` | **Dissolved by [ADR 0022](adr/0022-structure-is-derived-policy-is-five-rows.md).** The audit premise was that the generator trusts these rows. It does not any more: `Verbs` is a pure function of `Kind`/`Card` (7 combinations, zero exceptions, 412 rows) and `ComparisonClass` is `seq`→`positional` with **five** cited exceptions. Both are derived; the build fails on unexplained departures. No 61-row audit. |
| `G27` | **The data says no.** Only 52% of rows land in exactly one proposed class; 73 `lists`/`system` rows have no bucket, and classes 3/4 have zero rows to test against. |

## 🔴 Escalated — research raised it, not closed it

**`E19` — Chorus and the applied log.**
[Findings](research/2026-08-03-chorus-applied-log-merge.md). **Phase 0 item 8 was never closed**: the
LibChorus half is verified, the FLExBridge half never happened, and the packages that contain the
FieldWorks-model merge registration are not even in the local NuGet cache. The common case
(distinct-GUID additions) is safe regardless. But the *documented* failure mode is understated —
Chorus's default `FindByEqualityOfTree` strategy means a same-`proposalId` collision without the
guid-keyed registration yields **duplicate GUIDs in `.fwdata`**, not a benign overwrite.

**Not a grill item — a half-day experiment**, runnable through `FwHeadless`'s own
`SendReceiveHelpers.CallLfMergeBridge` without needing FLExBridge source. `implementation-plan.md` and
ADR 0003 now carry the caveat. **`MOT-14` does not resolve this** — Lexbox fixes the product
consequence, but the log still lives in the `.fwdata`.

## 📐 Spikes — and which repository each one lives in

Owner decision, 2026-08-05 ([ADR 0020](adr/0020-cli-first-fieldworks-planned-not-built.md)): only `A1`
is on the critical path, and it is the only one in this repository.

| Spike | Whose code | Needs | Scope | Status |
| --- | --- | --- | --- | --- |
| `A1` — `CreateCacheCopy` cost **and equivalence** | **motif** — `spikes/SIL.Motif.Spikes.ScratchCache` | Sena 3 from the FieldWorks checkout | 1 | ✅ **DONE 2026-08-05 — measured; ADR 0016 amended** |
| `A3` — `IProjectIdentifier` for `kMemoryOnly` | **motif** — ~15 lines | nothing | 1 | `A1`'s prerequisite |
| `E19` — Chorus applied-log merge | languageforge-lexbox (`FwHeadless`) + Chorus packages **absent from the local NuGet cache** | a restored feed, a disposable project | 2 | **Deferred by decision** |
| `F26a` — FieldWorks command-layer seam | FieldWorks | a FieldWorks checkout | 2 | **Deferred by decision** |

**`A1` is closed — measured, not inferred, and it settled on one path.** Sena 3, 152,222 objects. The
in-memory fan-out is fast (140 ms vs 4,445 ms from a hot cache) but **measurably lossy: 0 of 4 writing systems
value-equal**, including when copied from a file-loaded scratch whose writing systems were intact — the loss
belongs to the `kMemoryOnly` target, not the source. The file path returned **4 of 4** and no findings at all.
ADR 0016 is [amended](adr/0016-scratch-cache-copy-not-undo.md): **one canonical path — copy the files, open
the copy** (~600 ms). `CreateCacheCopy` is withdrawn from the Dry Run design, giving up ~5× speed to remove a
per-operation judgement call about whether collation matters.
[Full results](research/2026-08-05-createcachecopy-provenance-and-hazards.md#10-measured--the-spike-was-built-and-run).

*The research that scoped the spike, kept because it is why the spike looked for equivalence and not just
speed:*

- **Two silent correctness hazards** the ADR had not anticipated. A memory-only scratch synthesizes writing
  systems **from the bare language tag** (no custom collation, sort rules, valid characters), and
  custom-field **`flid`s are re-derived** through non-contractual `HashSet` order. Motif is safe on both
  today, and the ADR now states the reasons as invariants rather than luck.
- **Provenance:** SDK-sample infrastructure for XML ↔ Db4o porting; both repos' histories truncate at 2012
  synthetic roots, so original intent is **unattributable**. The primitive underneath it runs on every
  object of every project open, so only the cache-to-cache path is untested.
- **The harness already exists** — `git -C ../FieldWorks show f0d837288^:Samples/ImportExport/ImportExport.cs`,
  average-of-N mode included. Don't write it from scratch.
- **Fixture trap:** real Sena 3 (152,222 objects) is in the FieldWorks checkout; a 50-object `%TEMP%` stub
  reuses the name. The only existing test used 688 objects.

`A2` is largely answered (two caches coexist, no unsafe statics, and the scratch must keep
`Path`/`ProjectFolder` empty to avoid the one global singleton) — only scale remains, and it rides along.

**`E19` and `F26a` are deferred, not resolved.** `E19` in the owner's words: *"we don't care about Chorus
right now. It's not great, and we know it will fail in some ways."* The caveats stay in ADR 0003 and
`implementation-plan.md`; a single-machine CLI never triggers a Chorus merge, and **that silence is not
evidence.** `F26a` waits with scope 2 but must run before any `MOT-12` code, since ADR 0019's authoring
path depends on it.

---

## ❓ The grill — 27 decisions, in dependency order

Two of these are **gates**: answering them changes whether other questions matter at all. Ask them
first.

### ~~Gate 1~~ — `H30`: **CLOSED 2026-08-05.** In the destination, staged out of v1

[ADR 0017](adr/0017-text-and-analysis-destination-scope.md). The ten gated items are **admitted, not
deferred** — but the deferral cost turned out to be ~70% additive, with the non-additive 30% sitting
entirely in the hashed layer. **One time-sensitive item falls out: `H30a`** — reserve non-object
targets in the canonical-id space *before M3 freezes the canonical JSON form*, where it costs ~0
rather than a major version bump.

*Original framing, kept for the record:*

### Gate 1 — `H30`: does text and analysis come into scope?

Currently `out` / `not-domain-reachable` in the manifest, and both Plan A and the README say so.
Bringing it in is a new bounded context, not extra volume.

**If no**, the following are deferred wholesale and should not be grilled now:
`H32a` `H33a` `H34` `I35a` `I35b` `I36` `I37` `I39` `I39a` `I40` — ten items.

**If yes**, `H32a` is the immediate follow-up: research showed tests hang on durable global approval
and coverage hangs on fragile positional occupancy, so **sequence the test half first and treat
coverage as a research track?**

### ~~Gate 2~~ — `G28`: **CLOSED 2026-08-05.** Two orthogonal axes, not one label

[ADR 0018](adr/0018-change-class-is-two-axes-not-one.md). A change class is a `(domain, shape)` pair —
21 cells, all 473 rows, zero straddle — and **both axes are already segments of ADR 0009's kind name**,
so nothing new becomes versioned contract. **`G27` dissolves and `G29` resolves** as consequences: the
six classes were two axes conflated, and ordering is a shape whose display-vs-meaning split
`ComparisonClass` already carries.

*Original framing, kept for the record:*

### Gate 2 — `G28`: what is a change class *for*?

Review routing, permissions, risk tiering, which diff operations are coverable, coverage
requirements? The taxonomy's shape follows its purpose, and `G27` and `G29` cannot be answered before
this is.

### Then, roughly in this order

Scope tags added 2026-08-05: **[1]** blocks scope-1 work now, **[2]** is worth deciding on paper but
gates only the FieldWorks integration.

| | Item | Decision |
| --- | --- | --- |
| ~~`B5`~~ | | **Decided 2026-08-05 — the lexical entry**, on the criterion *start where an agent's work starts*. The slice is the `lexEntry` construct **plus** `LexemeForm`/`MoForm`, because `lexEntry` alone has zero `create|delete` and cannot create an entry; `AlternateForms` (a `feeding` row) is excluded. Poison flags stopped being a selection criterion once the scratch became throwaway. |
| ~~`B7a`~~ | | **Dissolved** by [ADR 0022](adr/0022-structure-is-derived-policy-is-five-rows.md) — derived, not audited. |
| ~~`B6`/`B19`/`B20`~~ | | **Resolved 2026-08-05** by [ADR 0023](adr/0023-derived-kind-names-required-descriptions.md): the identifier is `lowerFirst(DeclaringClass)`, meaning moves to a required description, and B20's ask was impossible — 11 of 20 possibility lists have no concrete subclass to name. |
| ~~`B8a`~~ / ~~`B21`~~ | | **Answered 2026-08-05** by [ADR 0025](adr/0025-parser-first-build-order.md): the L0 query is retired rather than corrected. Build order is now **parser-first in one slice** — 150 parser-read fields (113 grammar) plus the analysis fields carrying a human judgement; the 323 fields no parser reads are slice 2. |
| ~~re-scope the analysis rows~~ | | **Done 2026-08-05** — 21 rows in, manifest now 494 in-scope across 100 classes; the four parser-output fields classified `derived-read-only`, which liblcm's own "currently unused" comment corroborates. |
| ~~`I35a`/`I35b`~~ | | **Resolved** by [ADR 0027](adr/0027-what-counts-as-the-same-word-analysis.md): the gate is morphology; sense and word category are reported, not gating. |
| ~~descriptions for `MOT-4`'s family~~ | | **Done 2026-08-06** — 14 drafted in `manifest/kind-descriptions.tsv`, and the check now fails on a description that merely restates its label, which is the bar presence alone was missing. |
| ~~Bidirectional~~ | ~~`F22`~~, ~~`F26`~~ | **Decided** — [ADR 0019](adr/0019-observed-intent-and-proposal-edit-mode.md). Observe intent in a constrained proposal-edit mode; diff refuses loudly on the unrecoverable set. |
| | `F24` **[1]** | Three provenance classes now, not two: **observed**, **diffed**, **authored**. Should a reviewer see which? Scope 1 produces *authored* only, which is exactly when the field is cheapest to add. |
| ~~Classes~~ | ~~`G29`~~ | **Resolved** by [ADR 0018](adr/0018-change-class-is-two-axes-not-one.md) — ordering is a shape, and `ComparisonClass` already separates display order (56 `positional`) from meaning (2 `feeding`). |
| **Text** *(gated on `H30`)* | `H34` | Text *edits* in scope, or only analyses attached to existing text? Editing is what breaks anchors. |
| | ~~`I35a`~~ | **Resolved** — [ADR 0027](adr/0027-what-counts-as-the-same-word-analysis.md). Correctly scoped: false agreement only about *sense*, which is not under test. A sense-sensitive gate is unimplementable anyway. |
| | ~~`I35b`~~ | **Resolved** — neither; they answer different questions. The gate is `MatchesIWfiAnalysis`'s shape; sense and word category are reported, not gating. |
| | `I36` | Is "one authoritative analysis per occurrence" linguistically defensible? The model already distinguishes disapproved from no-opinion. |
| | `I37` | What is the coverage ramp? Absolute, per-text, or delta-only? |
| | `I39` / `I39a` | Are donated tests reviewed, trusted, or quarantined — noting a donation sets a *global* flag? Do machine guesses count as assertions? |
| | `I40` | When a rule change is correct but breaks an old analysis, who may overrule a native speaker's judgement? **Less urgent than it looked** — the two sampled projects hold 8 human evaluations between them against 7,646 wordforms, so the body of approved analysis being protected is currently almost empty (ADR 0031). Thin sample; recheck before relying on it. |
| | `H33a` | Does provenance record the agent GUID, the engine, or both? `DefaultParserAgent` switches GUID with `ActiveParser`. |
| **Product** | ~~`D14`~~ | **Decided** — [ADR 0031](adr/0031-collaboration-follows-the-data-not-the-surface.md): neither. Review lives with the project, so there is no second comment system and no dependency on another team's roadmap. |
| | ~~`D15`~~ | **Decided** — it works offline because it never needed a network. No replicated store, so it never has to earn its cost (ADR 0031). |
| | ~~`D16`~~ | **Decided** — sharing is a deliberate export of an immutable document, not a mode the system runs in; a project that never exports is unaffected (ADR 0031). |
| ~~`D18`~~ | | **Retired** — it asked who keeps two change vocabularies aligned, which only mattered while operations were routed through a second model. They are not. |
| ~~`C12`~~ | | **Decided** — a `feeding` reorder requires a Grammar Delta before approval ([ADR 0028](adr/0028-feeding-reorders-require-a-grammar-delta.md)); the anchor machinery already covered staleness, but not comprehension when nothing drifted. |
| **Engine** | `C11` | Raise the liblcm `Rollback`/`Undo` hook fix upstream now, or accept ADR 0016's workaround permanently? |
| | ~~`C12`~~ | **Decided** — [ADR 0028](adr/0028-feeding-reorders-require-a-grammar-delta.md): a `feeding` reorder requires a Grammar Delta before approval. |
| **Contract** | ~~`J41`~~ | **Decided** — [ADR 0029](adr/0029-agents-address-layer-1-only.md): agents address Layer 1 only, no field-level escape hatch, and an unreachable field is a Layer 1 requirement to be logged rather than routed around. |
| | ~~`B9b`~~ | **Decided** — it ends when the owner declares a version. My objection (nothing forces it) is recorded as overruled, with two mitigations: declare instability explicitly, and report the count of stored artifacts authored against the unstable vocabulary. |
| ~~`J43`~~ | | **Decided** — [ADR 0021](adr/0021-cli-is-the-full-surface-layer-1-churns.md) §6: warn and enumerate, force to proceed, refuse when consequences are unenumerable. |
| ~~`J44`~~ | | **Answered** — the individual operation, subject to `requires`; atomic groups stay indivisible. |
| ~~Ratify~~ | ~~`F23a`~~ | **Decided** — rule 1 replaced: order is *declared*, not positional ([ADR 0026](adr/0026-order-is-declared-not-positional.md)). It contradicted `AGENTS.md` rule 5, which is now amended. |
| | ~~`B9a`~~ | **Decided** — adopt the digest-meaning rule; defer the support window, since nothing lags. |
| | ~~`J42a`~~ | **Decided** — refuse on drift, *and* record what the query matched, because drift protects the operations and not the query's intent. |
| **Manifest** | ~~`B7a`~~ | **Dissolved** — [ADR 0022](adr/0022-structure-is-derived-policy-is-five-rows.md) derives the columns instead of auditing them. |
| | ~~`B8a`~~ | **Answered** — [ADR 0025](adr/0025-parser-first-build-order.md) retires the L0 query rather than patching it; build order is parser-first in one slice. |
| | ~~`C10a`~~ | **Retired 2026-08-06** — [ADR 0016](adr/0016-scratch-cache-copy-not-undo.md) as amended: the only rollback left is a failed atomic apply, so nothing needs to know which fields poison. The column, the field list, `RollbackCacheInvalidator` and `CacheReusability` are all deleted rather than repointed. |

---

## Recommended session shape

Revised 2026-08-05. Both gates are closed and the bidirectional block is decided, so what remains is
one spike and a short run of scope-1 decisions.

1. **`A1`** — research first (git history, correctness hazards), then a falsifying timing test only if
   that is inconclusive. The one thing `MOT-11` should not be designed ahead of.
2. **`B5`** — M2's first generated family, on the new AI-authored-round-trip criterion.
3. **The three ratifications** (`F23a`, `B9a`, `J42a`) — minutes each, since the alternatives are
   written down. Good filler when the harder questions stall.
4. **The three manifest follow-ups** (`B7a`, `B8a`, `C10a`) — each is now a choice over a known number
   rather than an open worry, and `B7a`/`B8a` gate the generator.
5. **`F24` and `J41`** — cheap now, and scope 1 is when provenance and the Layer 0/1 split are easiest
   to get right rather than retrofit.
6. **The text block** (`H32a`, `H34`, `I35a`–`I40`) — admitted by ADR 0017, but not v1. Grill when the
   test half is actually being built.
7. **Scope-2 decisions** (`D14`, `D15`, `D16`, `C11`, `C12`) — worth answering on paper before
   FieldWorks work starts, not before scope 1 does.

**Do not grill the 20 closed or 5 decided items.** That is 25 of ~56 — nearly half — that would
otherwise have cost you decisions you did not need to make.

## What the research changed, not just answered

Worth reading before the session, because three of these move the plan rather than a grill row:

- **`E19` was recorded as closed and is not.** Phase 0 item 8's FLExBridge re-confirmation never
  happened. Caveats now in ADR 0003 and the implementation plan.
- **ADR 0012's L0 definition is unsound.** 13 of its 37 fields are never read by `HCLoader`, two via the
  exact bare-name false-positive that `HcReachable` was introduced to fix.
- **`issues.md` B18 understated itself by 106 rows** — but the risk turned out to be concentrated in 61
  reviewable rows rather than spread across 473, which makes it cheaper, not more expensive.
