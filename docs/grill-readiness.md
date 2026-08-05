# Readiness — what to grill, and in what order

*Triage of 2026-08-03, updated as research lands. **Not everything here is a question for you.** Most
of what looked like a decision was a fact nobody had gone and read.*

## Status

| | Count | Meaning |
| --- | --- | --- |
| ✅ **Closed** | 20 | Answered from source. Do not grill; read the answer. |
| ✅ **Decided** | 8 | `H30`, `G28`, `G27`, `G29`, `F26`/`F22`, `J43`, `J44` — ADRs 0017–0019, 0021. |
| 📐 **Spike** | 1 active, 2 deferred | `A1` is in this repo and on the path. `E19` and `F26a` are other-repo and scope 2. |
| ❓ **Yours** | ~29 | Genuinely a decision. This is the grill. |

**All desk research has landed** except `A1`, which is in flight as of 2026-08-05: its git history and
correctness hazards are being read before any timing harness is written.

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
| `B7` | **The risk is 61 rows, not 473.** Structural columns checked 22/22 clean; `ComparisonClass` is mechanical for 405 of 412; errors concentrate in the 61 hand-classified rows (~23% flawed when sampled). **B18's ~300 is really 406.** → `B7a`. |
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
| **Next up** | `B5` **[1]** | Which family is M2's first? The criterion changed twice: acceptance is no longer regenerating LcmCrdt, and under ADR 0020 it is now an **AI-authored, CLI-driven round trip** — which favours a family an agent has a real reason to touch over the mechanically cheapest one. |
| ~~Bidirectional~~ | ~~`F22`~~, ~~`F26`~~ | **Decided** — [ADR 0019](adr/0019-observed-intent-and-proposal-edit-mode.md). Observe intent in a constrained proposal-edit mode; diff refuses loudly on the unrecoverable set. |
| | `F24` **[1]** | Three provenance classes now, not two: **observed**, **diffed**, **authored**. Should a reviewer see which? Scope 1 produces *authored* only, which is exactly when the field is cheapest to add. |
| ~~Classes~~ | ~~`G29`~~ | **Resolved** by [ADR 0018](adr/0018-change-class-is-two-axes-not-one.md) — ordering is a shape, and `ComparisonClass` already separates display order (56 `positional`) from meaning (2 `feeding`). |
| **Text** *(gated on `H30`)* | `H34` | Text *edits* in scope, or only analyses attached to existing text? Editing is what breaks anchors. |
| | `I35a` | Accept PanGloss's allomorph- and sense-blindness as a declared limitation, or build a richer identity? It causes **false agreement**, the unsafe direction. |
| | `I35b` | FieldWorks ships two disagreeing equality definitions. Which wins? |
| | `I36` | Is "one authoritative analysis per occurrence" linguistically defensible? The model already distinguishes disapproved from no-opinion. |
| | `I37` | What is the coverage ramp? Absolute, per-text, or delta-only? |
| | `I39` / `I39a` | Are donated tests reviewed, trusted, or quarantined — noting a donation sets a *global* flag? Do machine guesses count as assertions? |
| | `I40` | When a rule change is correct but breaks an old analysis, who may overrule a native speaker's judgement? |
| | `H33a` | Does provenance record the agent GUID, the engine, or both? `DefaultParserAgent` switches GUID with `ActiveParser`. |
| **Product** | `D14` | Two review surfaces, or one? FwLite already ships comment threads. |
| | `D15` | Must review state work offline? This is the one place a CRDT would earn its cost. |
| | `D16` | What does "optional per project" mean operationally? |
| | `D18` | Who owns keeping Motif's and FwLite's vocabularies aligned? |
| **Engine** | `C11` | Raise the liblcm `Rollback`/`Undo` hook fix upstream now, or accept ADR 0016's workaround permanently? |
| | `C12` | Does a reviewer actually see that a phonological reorder changed meaning? |
| **Contract** | `J41` **[1]** | Confirm Layer 0 = diff's output vocabulary, Layer 1 = agent's input vocabulary. **Now the guard on ADR 0021's deliberate Layer 1 churn**, not a stylistic preference — the mechanical test is whether a change alters hashed bytes. |
| | `B9b` **[1]** | When does the churn window end and the intent surface declare itself stable? Recommended trigger: the first human approval recorded against a stored Proposal. Until then, say *unstable by declaration* rather than implying stability by omission. |
| ~~`J43`~~ | | **Decided** — [ADR 0021](adr/0021-cli-is-the-full-surface-layer-1-churns.md) §6: warn and enumerate, force to proceed, refuse when consequences are unenumerable. |
| ~~`J44`~~ | | **Answered** — the individual operation, subject to `requires`; atomic groups stay indivisible. |
| **Ratify** *(cheap — pick one of two written positions)* | `F23a` | Adopt the four canonical-diff freezing rules as drafted, or contest one? |
| | `B9a` | Adopt the k8s-shaped versioning policy, calibrating the dual floor when a real cadence exists? |
| | `J42a` | Force a resolved batch re-applied against a moved baseline through the existing pre-flight drift path, Terraform-style? *(Recommend yes.)* |
| **Manifest** *(all now bounded — the counts exist)* | `B7a` | Review all **61** non-default rows before the generator ships, and spot-audit the other 412? One sitting, not a re-audit. |
| | `B8a` | ADR 0012's L0 query yields **13 phantom fields** and understates the grammar dependency. Re-scope L0 to the confirmed 24, or fix `HcReachable` first? |
| | `C10a` | **4 rows.** Keep, retire, or repoint `AssessPoisonsCache`? |

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
