# Readiness — what to grill, and in what order

*Triage of 2026-08-03. **Not everything here is a question for you.** Of 48 items, 9 are already
answered by research, 11 are being researched now, 1 needs a measurement rather than reading, and
**27 are genuinely yours to decide.** Only that last group belongs in a grill.*

## Status

| | Count | Meaning |
| --- | --- | --- |
| ✅ **Closed** | 9 | Answered from source. Do not grill; read the answer. |
| 🔬 **Researching** | 11 | Investigation in flight. Do not grill until it lands. |
| 📐 **Needs a spike** | 1 | Cannot be read from source — must be built and measured. |
| ❓ **Yours** | 27 | Genuinely a decision. This is the grill. |

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

## 🔬 Researching now

`A3` `kMemoryOnly` constructibility · `A4` STJ on `net48` · `B8` L0 creation closure ·
`C10` `AssessPoisonsCache` row count · `C13` alpha-variable traversal — *all five were mislabelled as
decisions; they are countable.*

`B6` construct naming · `B7` classification evidence quality · `G27` does the data support the
proposed taxonomy — *the generator reads these columns directly, so this is the biggest hidden risk in
Plan A.*

`E19` Chorus applied-log merge — *the standing risk.*

`F23` canonical diff · `B9` contract versioning policy · `J42` batch storage — *prior art, to turn
open questions into named options.*

## 📐 Needs a spike, not research

**`A1` — what does `CreateCacheCopy` cost?** Cannot be read from source. Needs a build against a real
project, timing one copy from a hot cache and one from a pristine scratch. Half a day. **Nothing in M2
should be designed before this number exists**, and `A2`'s remaining half rides on it.

---

## ❓ The grill — 27 decisions, in dependency order

Two of these are **gates**: answering them changes whether other questions matter at all. Ask them
first.

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
| **Quality** | `B7` follow-up | Once the classification census lands: verify-lazily or dedicated audit? |
| | `C10` follow-up | Once the count lands: keep, retire, or repoint `AssessPoisonsCache`? |

---

## Recommended session shape

1. **`A1` spike first** — half a day, and it unblocks the M2 design.
2. **Gate 1 (`H30`)** — one answer removes or admits ten questions.
3. **Gate 2 (`G28`)** — one answer unblocks two more.
4. Then the bidirectional block (`F22`, `F24`, `F26`), which is the largest single design commitment.
5. Everything else can follow research landing.

**Do not grill the 9 closed items or the 11 in flight.** That is 20 of 48 — over 40% — that would
otherwise have cost you decisions you did not need to make.
