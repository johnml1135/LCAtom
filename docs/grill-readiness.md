# Readiness — what to grill, and in what order

*Triage of 2026-08-03, updated as research lands. **Not everything here is a question for you.** Most
of what looked like a decision was a fact nobody had gone and read.*

## Status

| | Count | Meaning |
| --- | --- | --- |
| ✅ **Closed** | 20 | Answered from source. Do not grill; read the answer. |
| 📐 **Needs a spike** | 1 | Cannot be read from source — must be built and measured. |
| 🔴 **Escalated to a task** | 1 | `E19` — research found a real unclosed gap, not a question. |
| ❓ **Yours** | 34 | Genuinely a decision. This is the grill. |

**All research has landed.** Nothing is in flight.

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
| `H31` | **No durable occurrence identity exists.** `AnalysisOccurrence` is a plain C# class, not a `CmObject`. Re-segmentation deletes `Segment` objects; re-attachment is a string+position heuristic. Systemic across interlinear and discourse. |
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

## 📐 Needs a spike, not research

**`A1` — what does `CreateCacheCopy` cost?** Cannot be read from source. Needs a build against a real
project, timing one copy from a hot cache and one from a pristine scratch. Half a day. **Nothing in M2
should be designed before this number exists**, and `A2`'s remaining half rides on it.

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

### Gate 2 — `G28`: what is a change class *for*?

Review routing, permissions, risk tiering, which diff operations are coverable, coverage
requirements? The taxonomy's shape follows its purpose, and `G27` and `G29` cannot be answered before
this is.

### Then, roughly in this order

| | Item | Decision |
| --- | --- | --- |
| **Bidirectional** | `F22` | `merge`, `replace`, index-as-identity `move` are unrecoverable from a state delta; `reparent` is recoverable. Refuse loudly, degrade to delete-plus-create, or accept an external identity mapping? |
| | `F24` | Should a reviewer be able to tell a diff-derived Proposal from an authored one? |
| | `F26` | Is diff the primary human authoring path, making authored Proposals an AI/CLI path? |
| **Classes** | `G29` | Does ordering deserve its own class? 54 of 56 `positional` rows are display order; 2 are grammatical meaning. |
| | `B5` | Which family is M2's first, now that the acceptance test is a LibLCM round trip rather than regenerating LcmCrdt? |
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
| **Contract** | `J41` | Confirm Layer 0 = diff's output vocabulary, Layer 1 = agent's input vocabulary, as the *stated* rationale for ADR 0009's split. |
| | `J43` | Removing an operation moves the intent digest and can orphan a dependent. Refuse, cascade, or warn? |
| | `J44` | What is the unit of splitting a change set, given the `requires` DAG and atomic groups? |
| **Ratify** *(cheap — pick one of two written positions)* | `F23a` | Adopt the four canonical-diff freezing rules as drafted, or contest one? |
| | `B9a` | Adopt the k8s-shaped versioning policy, calibrating the dual floor when a real cadence exists? |
| | `J42a` | Force a resolved batch re-applied against a moved baseline through the existing pre-flight drift path, Terraform-style? *(Recommend yes.)* |
| **Manifest** *(all now bounded — the counts exist)* | `B7a` | Review all **61** non-default rows before the generator ships, and spot-audit the other 412? One sitting, not a re-audit. |
| | `B8a` | ADR 0012's L0 query yields **13 phantom fields** and understates the grammar dependency. Re-scope L0 to the confirmed 24, or fix `HcReachable` first? |
| | `C10a` | **4 rows.** Keep, retire, or repoint `AssessPoisonsCache`? |

---

## Recommended session shape

**Two spikes, then two gates, then the design.**

1. **`A1` spike** — what does `CreateCacheCopy` cost? Half a day; unblocks the M2 design.
2. **`E19` experiment** — half a day; closes a Phase 0 item that was recorded as closed and is not.
   Independent of `A1`, so they can run together.
3. **Gate 1 (`H30`)** — one answer removes or admits ten questions.
4. **Gate 2 (`G28`)** — one answer unblocks two more.
5. **The bidirectional block** (`F22`, `F24`, `F26`) — the largest single design commitment.
6. **The three ratifications** (`F23a`, `B9a`, `J42a`) — minutes each, since the alternatives are
   written down. Good filler when the harder questions stall.
7. **The three manifest follow-ups** (`B7a`, `B8a`, `C10a`) — each is now a choice over a known number
   rather than an open worry.

**Do not grill the 20 closed items.** That is 20 of 56 — over a third — that would otherwise have cost
you decisions you did not need to make. The eight that landed last were the five mislabelled
"decisions" that were really counts, plus the three manifest-confidence questions.

## What the research changed, not just answered

Worth reading before the session, because three of these move the plan rather than a grill row:

- **`E19` was recorded as closed and is not.** Phase 0 item 8's FLExBridge re-confirmation never
  happened. Caveats now in ADR 0003 and the implementation plan.
- **ADR 0012's L0 definition is unsound.** 13 of its 37 fields are never read by `HCLoader`, two via the
  exact bare-name false-positive that `HcReachable` was introduced to fix.
- **`issues.md` B18 understated itself by 106 rows** — but the risk turned out to be concentrated in 61
  reviewable rows rather than spread across 473, which makes it cheaper, not more expensive.
