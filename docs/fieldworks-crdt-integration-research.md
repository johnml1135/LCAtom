# FieldWorks as a CRDT participant

Status: research synthesis and decision framing, not an approved implementation design.

Update, 2026-07-28: FieldWorks is actively migrating from WinForms to Avalonia on dedicated
migration branches/worktrees, with an eventual move from `net48` to `net10.0`; the default release
checkout remains WinForms/net48 today. The grammar, corpus, comparison, and adjudication UI described
here will be native Avalonia components. Web/React/WebView UI is not the planned FieldWorks surface.

## Decision in one sentence

Do not choose between “CRDT in FWLite” and “rewrite FieldWorks.” Add a narrow, versioned semantic
command boundary to FieldWorks, initially for explicit approved inbound changes, and keep the modern
Harmony store/synchronizer in an out-of-process companion. Expand bidirectional authority only one
domain at a time after conformance proves that domain safe. Build the new review and editing surfaces
as FieldWorks-owned Avalonia modules on the existing migration seams.

## The option that was missing

The initial three options were:

1. put grammar UI in FWLite while texts and analyses remain in FieldWorks;
2. move FieldWorks functionality into FWLite;
3. put CRDT behavior into FieldWorks.

There is a useful fourth architecture:

4. **FieldWorks plus a CRDT companion.** FieldWorks remains the only owner and writer of the live
   `LcmCache`/`.fwdata`. A modern process owns Harmony history, synchronization, projections, and
   review queues. A thin FieldWorks adapter accepts versioned semantic commands and applies them
   through normal LibLCM units of work.

This is still a form of option 3, but avoids loading the modern synchronization stack and its
dependencies into the predominantly `net48` FieldWorks process. It also avoids opening the same
project independently in two processes.

The current repositories contain two relevant precedents:

- FieldWorks' parser client isolates HermitCrab loading/parsing behind parser interfaces and portable
  results, although the checked-out FieldWorks source still runs `HCParser` in-process. Recent
  HCWorker work should be treated as an evolving implementation, not a settled deployed boundary.
- LexBox `FwHeadless` already performs a snapshot-mediated, bidirectional synchronization of the
  supported MiniLcm lexical surface between Harmony CRDT state and a headless `.fwdata` cache.

The second precedent means the fourth option is not merely hypothetical. What remains new is applying
the pattern safely to an **already open, interactively edited FieldWorks cache**, then extending the
semantic surface beyond MiniLcm into grammar, text, and analysis.

## UI and runtime migration boundary

The temporary `net48` runtime does not require a temporary web UI. FieldWorks' active Avalonia
migration already establishes a native coexistence architecture:

- `FwAvalonia` targets `net48` for in-process hosting beside WinForms.
- Avalonia 11.x is intentionally pinned because that line still supplies assemblies loadable by the
  current runtime.
- a typed, deterministic view-definition IR separates the new UI from legacy XML layout execution;
- the typed IR/region-model Avalonia path bypasses the frozen legacy `DataTree`, while
  `RecordEditView` selects the active host;
- explicit surface selection chooses Avalonia, explicit legacy fallback, or blocked behavior;
- FieldWorks-owned virtualized controls replace dense legacy surfaces;
- plugin registration covers special interlinear and grammar-rule editors;
- the preview host supplies fast, LCModel-free UI development and snapshot/parity testing;
- projectors and LibLCM write-back stay outside the presentation assembly, at the FieldWorks/xWorks
  integration seam;
- new components preserve the established FieldWorks edit-session, UOW/undo-redo, command/focus,
  UI-scheduler, lifetime, localization, accessibility, automation, and parity-manifest contracts.

The grammar-change and word-evidence UI should follow those same rules. It should not create a
parallel React, Blazor, WebView, or browser component model as the FieldWorks UI surface. Existing
FwLite web/MAUI and Platform.Bible products remain independent and are not deprecated by this choice.

The runtime boundary is:

```text
FieldWorks net48 during coexistence
  ├─ native Avalonia review/edit modules
  │    ├─ LCModel-free view models and projections
  │    ├─ proposal, evidence, history, and adjudication views
  │    └─ explicit legacy fallback only where required
  ├─ xWorks/FieldWorks integration
  │    ├─ loaded LcmCache and UOW
  │    ├─ semantic lowering and read-back
  │    └─ parser/UI invalidation and save
  └─ versioned local protocol
       ↕
.NET 10 companion
  ├─ Harmony store and synchronization
  ├─ PanGloss orchestration
  ├─ durable inbox/outbox
  └─ query projections
```

After FieldWorks moves to `net10.0`, the Avalonia components remain the product UI. The companion
boundary may remain out of process for fault isolation and synchronization lifecycle, or selected
services may move in process. That later deployment choice must not change the semantic command,
receipt, projection, or view-model contracts.

This divides interim work cleanly:

1. Build framework-neutral semantic and projection contracts now.
2. Build the Harmony/PanGloss companion on `net10.0`.
3. Build the actual FieldWorks UI now in `net48` `FwAvalonia` modules using Avalonia 11.x.
4. Keep UI projects LCModel-free; put projectors and mutations in the established FieldWorks
   integration layer.
5. Retarget and update Avalonia only as part of the FieldWorks runtime migration, without rewriting
   the feature UI or wire contract.

## What “FieldWorks speaks CRDT” can mean

The phrase spans several materially different products.

| Level | FieldWorks capability | Authority | Rough effort |
|---|---|---|---:|
| 0 | Preview a proposed semantic change and its evidence in Avalonia | `.fwdata` | 3–6 weeks |
| 1 | Explicitly apply/reject an approved change from Avalonia | `.fwdata` | 6–10 weeks for a production lexical vertical slice |
| 2 | Synchronize selected, instrumented domains both ways | Per-domain, explicitly assigned | 3–6 months for an initial lexical subset |
| 3 | Collaborate on grammar, texts, and analyses | Per aggregate/change contract | 12–24+ engineer-months before broad production use |
| 4 | Treat Harmony as authority for nearly all FieldWorks state | Harmony; `.fwdata` becomes a projection | 20–50 engineer-years, with high program risk |

These estimates are order-of-magnitude planning ranges, not commitments. The first proof gates below
are intended to replace uncertainty with measurements before a larger estimate is accepted.

## Minimum: “apply this now”

The smallest credible write path is:

```text
approved Harmony change
  → validated semantic command
  → active FieldWorks window/controller
  → already-loaded LcmCache on FieldWorks' UI thread
  → one outer LibLCM undoable unit of work
  → read-back and affected-object receipt
  → normal FieldWorks save path
  → parser/UI invalidation
```

The FieldWorks adapter, not Harmony, must own:

- access to the already-loaded cache;
- UI-thread marshalling;
- LibLCM object factories, ownership, and validation;
- the complete atomic unit of work;
- FieldWorks undo/redo integration;
- save policy and project lifecycle;
- parser invalidation and UI refresh;
- user-visible conflict/error handling.

Harmony changes should not directly mutate LibLCM objects. The wire contract must contain closed,
portable semantic commands. Unknown kinds are rejected; generated mutation plans remain output-only.
Operation order remains authoritative.

The first milestone should be deliberately small and explicit about what it proves:

> Apply one approved sense-gloss change to the active FieldWorks project, undo it, redo it, save it,
> reload it, and prove that the UI and receipt agree.

That lexical slice proves the integration mechanics only; it should declare parser invalidation as
none unless source/experiment shows otherwise. The next, domain-relevant slice should set the
accepted analysis of one text occurrence through its segment analysis sequence. Grammar ordering,
parser-created analyses, broad deletion, moves, and custom fields remain poor first operations.

## Why inbound apply is easier than outbound capture

LibLCM provides a strong common structural mutation seam. `DomainDataByFlid` centralizes scalar and
reference writes, vector changes, ownership moves, creation, and owning deletion. Unit-of-work hooks
can buffer those primitive mutations and emit one atomic journal record.

That is enough for a trustworthy structural audit such as:

```text
object G changed field F from X to Y
object H moved from owner A to owner B at position 2
```

It is not enough to recover canonical semantic intent such as:

```text
approve this parser analysis
repair this word's analysis
add this word occurrence to the evaluation corpus
move this grammar rule and preserve its linguistic ordering contract
```

`PropChanged` carries notification coordinates, not old/new semantic values or the user's command.
One UI intent may generate many mutations; parser and derived services can add changes during or
after notification; a bulk import can produce thousands of mutations in one UOW. Conversely, the
same primitive write can result from several different intents.

Therefore:

- a shared LibLCM recorder is valuable as structural evidence and reconciliation input;
- it must not be promoted to canonical semantic CRDT history;
- high-value FieldWorks workflows need explicit semantic command scopes at shared service seams;
- uninstrumented edits may be recorded as observed state changes or classified as inferred intent,
  with provenance and confidence, never silently relabeled as authored semantic intent.

This distinction is the main cost driver for bidirectional FieldWorks synchronization.

## Recommended process and authority boundary

```text
FieldWorks UI and commands
  │
  ├─ native Avalonia module plus thin net48 semantic adapter
  │    ├─ owns active LcmCache access
  │    ├─ applies one LibLCM UOW
  │    ├─ emits structural audit evidence
  │    └─ refreshes/saves through FieldWorks
  │
  └─ authenticated local IPC
       │
       ▼
modern Harmony companion
  ├─ durable command IDs and commit history
  ├─ synchronization
  ├─ approval/review queue
  ├─ PanGloss assessment artifacts
  └─ query/read-model projections
```

Recommended first-deployment ownership rule:

> While a project is open interactively, the live FieldWorks process is the only writer of that
> loaded cache and `.fwdata`.

This is a deployment choice, not a universal LibLCM limitation. Existing FwHeadless code opens and
saves `.fwdata`, and LibLCM has a `SharedXMLBackendProvider` intended for simultaneous processes. The
first live integration should route writes through FieldWorks or run headless projection only while
FieldWorks is closed. A later shared-backend mode is admissible only after proving lock, UOW, UI,
undo, save, and crash behavior. If FieldWorks is closed, the companion may queue commands or invoke
the existing headless protocol according to an explicit project-mode lease.

A write protocol needs at least:

- project identity and adapter/schema version;
- stable command and commit IDs;
- expected FieldWorks/Harmony revision;
- one closed semantic command or atomic ordered change set;
- explicit `queued`, `accepted`, `applied-in-cache`, `saved`, `synchronized`, `rejected`, and
  `needs-reconciliation` states;
- affected entities and invalidation scopes;
- a read-back receipt;
- idempotent replay;
- crash recovery for every transition.

The LibLCM UOW and the durable Harmony commit are separate persistence boundaries. `applied-in-cache`,
`saved`, and `recorded-in-Harmony` can diverge after a crash; there is no exactly-once distributed
transaction. Correctness therefore requires a durable inbox/outbox, idempotent command ID, read-back
or receipt hash, and reconciliation that can safely finish or supersede an interrupted transition.

A parser worker can safely retry pure parsing or reload derived grammar state. A write-capable
companion cannot reuse that retry model unless command IDs and state transitions are durable. Its
local IPC must be restricted to the current user and authenticate the session/caller.

## Coexistence with Chorus/Send-Receive

CRDT and Chorus must not independently merge the same fields and then feed each other. That produces
two merge authorities, causal loops, and edits whose provenance cannot be explained.

During migration, choose one mode per domain:

1. **Chorus-authoritative projection:** Chorus/`.fwdata` remains authority; Harmony receives
   versioned projections and review history.
2. **Harmony-authoritative subset:** selected semantic domains are authored through Harmony and
   projected into FieldWorks; Chorus transports the resulting `.fwdata` compatibility state but
   does not independently merge those domains.
3. **LibLCM-only domain:** unsupported or high-risk state remains outside Harmony authority.

Do not use an uncoordinated “dual peer” mode for the same property set. Every projected change needs
origin, baseline, and loop-suppression evidence. Send/Receive is a lifecycle boundary:
synchronization must quiesce, reconcile, or invalidate the companion projection before and after
Chorus operations.

There is already a source-backed choreography in LexBox `FwHeadless/Services/SyncHostedService.cs`:

1. perform Send/Receive first when Mercurial has pending commits;
2. open the headless `.fwdata` and Harmony projects;
3. compare both current states with a last-successful project snapshot;
4. apply changes in both directions through `CrdtFwdataProjectSyncService`;
5. save `.fwdata`;
6. Send/Receive again if CRDT projection changed `.fwdata`;
7. regenerate the snapshot only after successful transport;
8. then synchronize Harmony again for changes learned from FieldWorks.

Rollback detection blocks further synchronization, and comments explicitly guard against publishing
partial or rolled-back state. This is stronger evidence than the abstract single-authority rule:
snapshot-mediated dual synchronization already works for the bounded MiniLcm surface. The live
FieldWorks problem is to reuse or adapt that protocol without a second process opening the same
project and without losing interactive UOW/undo/UI semantics.

## Grammar, texts, analyses, and the curated word corpus

The reason to integrate FieldWorks is precisely that the evaluation corpus cannot be a detached word
list. Its evidence must point into FieldWorks texts and distinguish at least:

- the text occurrence and segmentation;
- the manually accepted analysis;
- the parser analysis under a specific grammar revision;
- the expected pass/fail or adjudication status;
- who/what made each judgment;
- history and supersession;
- whether a discrepancy is bad grammar, bad lexical data, bad gold analysis, or stale parser output.

The data contract must not collapse three different scopes:

1. **Occurrence selection:** which analysis is accepted for this token in `Segment.AnalysesRS[index]`;
2. **Agent opinion:** a human or parser agent's evaluation of a reusable `WfiAnalysis`;
3. **Corpus judgment:** an external, versioned expectation about whether a grammar/parser result is
   acceptable for the enrolled occurrence.

A wordform-level or analysis-level opinion is not evidence that every occurrence has that accepted
analysis. Conversely, changing the selected analysis at one occurrence must not silently rewrite the
agent's global opinion or the corpus oracle.

The change vocabulary consequently needs aggregate operations, not just lexical property edits:

- change the accepted analysis of this occurrence;
- add this word/occurrence to a FieldWorks text with this analysis;
- enroll or remove an occurrence from an evaluation set;
- record that manual analysis is correct but parsing is incorrect;
- attach parser output and grammar revision;
- adjudicate competing analyses;
- supersede an earlier judgment without erasing its history.

These operations should initially be applied by FieldWorks while Harmony records their semantic
history and review state. The hard part is stable occurrence identity: current occurrence identity is
derived from a segment plus index, so text edits can move or invalidate it. A text-specific anchor
contract must be designed and tested before text/analysis becomes collaboratively authoritative.

Grammar adds another constraint: ordinary LWW fields and generic order sets are insufficient where
rule order, positional references, shared feature structures, or parser configuration change
linguistic meaning. Grammar changes need layered, validated change contracts and parser/evaluation
effects. A whole grammar change set may need to be the atomic semantic unit even though it lowers to
many LibLCM mutations.

## Three viable planning approaches

### A. Inbound apply only

An Avalonia module in FieldWorks previews, applies, and records approved external changes. Ordinary
FieldWorks edits remain
ordinary `.fwdata` edits and do not automatically enter Harmony as semantic changes.

Advantages:

- fastest path to a useful PanGloss → human review → FieldWorks loop;
- builds the eventual product UI on the existing Avalonia migration architecture immediately;
- minimal authority ambiguity;
- validates semantic lowering, undo, save, and UI seams;
- compatible with keeping FWLite lexeme-focused.

Limit:

- collaboration is asymmetric;
- FieldWorks-authored corrections require an explicit “publish as semantic change” action.

### B. Companion plus instrumented bidirectional subsets

After A works, instrument selected shared FieldWorks services with semantic scopes. Harmony becomes
authoritative only for named domains that pass round-trip and concurrency conformance.

Advantages:

- preserves the mature FieldWorks GUI and data model;
- allows incremental CRDT adoption;
- structural recorder detects missed mutations and supports reconciliation;
- the curated word-evidence workflow can become a first-class cross-application domain.

Costs:

- semantic instrumentation and provenance are substantial;
- mixed authority must be visible to users and developers;
- every promoted domain needs conflict, undo, parser, migration, and Chorus behavior.

### C. Harmony-authoritative FieldWorks projection

Nearly all project state is authored in Harmony; LibLCM/`.fwdata` becomes a compatibility projection.

Advantages:

- one collaborative history in the end state;
- offline/concurrent semantics can be designed consistently.

Costs:

- effectively a second implementation of LibLCM semantics and FieldWorks compatibility;
- hundreds of operation families and thousands of conformance fixtures;
- grammar, interlinear anchors, custom fields, rich strings, media, derived state, undo, and old
  project compatibility require domain-specific treatment;
- estimated as a multi-year, multi-team program with an uncertain payoff.

Recommendation: adopt A as the proof and B as the product direction. Treat C as a research horizon,
not the current program.

## Proof gates before approving implementation

### Gate 0: Avalonia coexistence surface

Before coupling the feature UI to synchronization, prove one native analysis-review or
grammar-assessment region in the net48 FieldWorks Avalonia host:

- it uses LCModel-free view models and the established typed-IR/region seams;
- `RecordEditView` selects it explicitly, with no silent legacy fallback;
- it obeys edit-session, UOW/undo-redo, UI-scheduler, focus/command, and lifetime rules;
- localization, accessibility/automation IDs, and visual/behavioral parity evidence pass;
- it can consume a fake companion projection in the preview host without opening an LCM project;
- the same view-model contract is viable after the eventual net10 retarget.

### Gate 1: FieldWorks apply seam

Prove one lexical command, with no claimed parser effect:

- is previewed and invoked through a FieldWorks-owned Avalonia component;
- executes on the UI thread against the caller-owned cache;
- is one undoable UOW;
- rejects stale/missing/wrong-type targets;
- can be replayed idempotently;
- updates the visible UI;
- declares and verifies its invalidation scope (for sense gloss, normally UI/read-model only);
- survives save/reload;
- reconciles crashes before and after save.

### Gate 2: structural capture audit

Install a prototype recorder at the shared LibLCM mutation seam and measure, across representative
FieldWorks workflows:

- what persisted mutations it captures;
- what bypasses it;
- nested and non-undoable UOW behavior;
- undo/redo representation;
- parser/derived side effects;
- delete closure and custom-field identity;
- whether before/after normalized snapshots reconcile exactly.

This gate validates audit coverage, not semantic capture.

### Gate 3: one semantic outbound workflow

Instrument one existing FieldWorks correction workflow, preferably changing/approving the analysis
of a selected word occurrence. Prove:

- explicit semantic intent plus structural evidence;
- stable target/occurrence identity or a clearly reported stale anchor;
- no duplicate feedback when the same command returns from Harmony;
- useful conflict UX;
- provenance separating human, parser, import, and AI actions.

### Gate 4: Chorus lifecycle

With a disposable project and two replicas, prove one declared authority mode through:

- local FieldWorks edit;
- Harmony-originated edit;
- Send/Receive;
- offline concurrent edit;
- crash/restart at each state transition;
- no causal loop, silent overwrite, or unexplained third state.

### Gate 5: grammar evaluation slice

Apply one bounded grammar change, run the parser against a versioned evaluation set, and show:

- baseline and candidate grammar revisions;
- words whose analyses changed;
- manual gold versus parser output;
- pass/fail/ambiguous/unadjudicated states;
- native-speaker or AI decisions with provenance;
- safe rollback or supersession.

Only after these gates should a grammar domain be considered for bidirectional authority.

## Kill criteria

Stop or reduce scope if:

- the adapter cannot apply a remote change as one FieldWorks UOW with deterministic read-back;
- project save and durable Harmony status cannot be reconciled after crashes;
- Chorus and Harmony cannot be assigned non-overlapping authority epochs;
- stable text-occurrence anchors cannot survive the editing operations users actually perform;
- grammar operations cannot preserve domain invariants and parser behavior under concurrency;
- more than a small, explicit share of mutations bypasses structural capture without detection;
- semantic instrumentation requires pervasive UI-handler rewrites instead of shared service seams;
- projected round trips change normalized meaning, parser output, or user-visible analyses silently;
- FieldWorks UI latency becomes non-interactive on representative projects.

## Questions research answered versus decisions still needed

Research can answer:

- where the cache/UOW/UI/process boundaries are;
- whether structural mutation capture is possible;
- whether an out-of-process modern companion is feasible;
- which domains are unsafe under generic CRDT primitives;
- the rough relative cost of inbound, subset-bidirectional, and full-authority paths.

Product decisions still needed after the proof gates:

- Is “apply approved external changes” already valuable without automatic outbound capture?
- Which user workflow earns the first semantic outbound scope?
- Which system is authoritative for each domain during migration?
- Must Send/Receive coexist indefinitely, or is it eventually replaced for promoted domains?
- Is the curated word-evidence corpus part of project data, collaborative review data, or a layered
  aggregate spanning both?
- What user-visible promise does “synchronized” make: present in Harmony, applied in the live cache,
  saved to `.fwdata`, transported by Chorus, or all four?

## Evidence base and confidence

High-confidence local evidence:

- Motif contract and coverage documents, including ADR 0013;
- LibLCM `DomainDataByFlid`, decorators, UOW helpers, action-handler callbacks, and normalization
  requirements;
- FieldWorks `FwApp`, main-window, mediator, refresh, parser-listener, and error-reporting seams;
- FieldWorks parser abstraction/change-listener behavior;
- FieldWorks `phase1-base` Avalonia migration spine: the net48 `FwAvalonia` project, typed
  view-definition IR, region composer, explicit surface selection, plugin registry, and preview host;
- FieldWorks interlinear-analysis and grammar-rule Avalonia follow-up branches (active migration
  evidence, not claims of merged/released functionality);
- LexBox `FwHeadless` synchronization choreography, project snapshots, rollback blocking, and
  `CrdtFwdataProjectSyncService` for the bounded MiniLcm surface;
- the measured Motif LibLCM inventory and grammar maps;
- earlier curated word-evidence research in this repository.

Medium-confidence planning inference:

- the effort ranges;
- the likely amount of shared-service semantic instrumentation;
- performance of incremental grammar/text projections at production scale.

Explicit evidence gaps:

- exact integration hooks for pausing/reconciling a **live** FieldWorks UI around Send/Receive;
- whether the recent HCWorker implementation will ship in the form inspected by delegated research;
- performance and failure behavior when the existing headless snapshot protocol is adapted to an
  already-loaded cache rather than a separately opened project.

Four of eight delegated Luna investigations failed because their local sandbox helper could not
start. Their unsupported generalities were excluded. Findings above incorporate only source-based
reports that completed successfully and locally inspected repository evidence.
