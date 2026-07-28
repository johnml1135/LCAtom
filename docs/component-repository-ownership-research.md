# Component and repository ownership research

Status: research and placement recommendation; not an approved implementation plan.

Date: 2026-07-28.

## Question

The proposed grammar-assessment loop has more than one product boundary: real
FieldWorks text and analysis; a versioned word-evidence corpus; PanGloss parser
execution and comparison; Harmony history and synchronization; native FieldWorks
Avalonia views; and a separately deployable `net10.0` AI operator CLI. This note
asks which existing repository should own each part, and whether the application
layer warrants a new repository.

The answer must not recreate LCAtom's withdrawn runner. ADR 0013 makes Harmony's
`IChange`/`Commit` the change mechanism and reserves LCAtom for research,
coverage, and integration design.

## Runtime decision update

Decision, 2026-07-28: the independent AI reviewer/operator CLI will target `net10.0`, not `net8.0`.
“Independent” means a separately installable process and automation surface; it does not require an
older target framework. Matching the companion and Harmony removes an unnecessary compatibility
boundary and permits direct reuse of application-domain and Harmony integration packages. Only the
contract consumed by current net48 FieldWorks must retain a netstandard2.0/schema-compatible surface.
## Evidence checked

| Repository | Revision checked | Direct evidence | Boundary implied by source |
| --- | --- | --- | --- |
| `FieldWorks` | `b8a2dd123aa6a5d0b95774ae74daa50e852932f8` | Main product projects target `net48`; ParserUI owns `ParserReport`, `ParserListener`, `WfiWordSet` import, parser opinions, undo, save, and navigation. | Owns the live project, texts, occurrence selection, manual analysis, parser integration, and desktop user experience. |
| FieldWorks Avalonia worktree | `010-advanced-entry-view` working branch | `Src/Common/FwAvalonia/FwAvalonia.csproj` targets `net48`, hosts beside WinForms, and is explicitly a UI foundation; `FwAvaloniaDialogs` is the separate XAML/MVVM dialog project. | Native assessment/review UI belongs here, but must remain LCModel-free and call FieldWorks projectors/services for mutations. |
| `liblcm` | `d564a719b1cce16c25ebea53a537393cb757f5d1` | `SIL.LCModel`, Core, Utils and FixData multi-target `netstandard2.0;net462;net8.0`; the model provides `WfiWordSet`, `WfiAnalysis`, agent opinions, text/segment graph, persistence and UOW. | Owns generic model invariants and persistent linguistic project concepts, not application workflow/run history. |
| `harmony` | `c858cb429231298aef564354b8ec2d5c87507287` | `src/Directory.Build.props` targets `net10.0`; `DataModel.AddChange/AddChanges` records typed `IChange`s into commits, snapshots, and sync transactions; `CommitMetadata.ExtraMetadata` is application metadata. | Owns generic CRDT mechanics and releaseable framework behavior, not a FieldWorks grammar schema, review queue, AI policy, or UI. |
| `languageforge-lexbox` / FWLite | `da284fa8e628a7acfa76a080dabfc324272ce64e` | `LcmCrdt`, `MiniLcm`, `FwDataMiniLcmBridge`, and `FwLiteProjectSync` already model lexical state and snapshot sync; `FwLiteProjectSync` is a `System.CommandLine` tool; its model has no text/segment/wordform/analysis surface. | Owns the existing product's lexical CRDT application and reusable bridge patterns, but grammar/text-analysis is a new bounded context rather than a small extension. |
| `PanGloss` | `2639067a18a2d49b3a9f38018469125df5d312f4` | Rust `pg-fwdata` imports `.fwdata` into a grammar snapshot; `pg-cli` supplies `import`, `parse`, `batch`, `diagnose`, traces, FST health, report and pack commands. Its C# smoke host targets `net8.0`; production C# harness/tool projects target `net10.0`. | Owns grammar import, parser execution, deterministic output, structural comparison primitives and traces—not reviewer identity, approval or CRDT persistence. |
| `LCAtom` | `8fa1e5ec4d0183020de2554e5254356e0e4d77bc` | README product boundary excludes UI, review queues, approval, hosting and storage; ADR 0013 withdraws the competing contract/runner. | Owns this research and the coverage/contract findings only; it is not the companion product's home. |
| `linguistic-assistant` | `2bfe5e97e302d1e983c685f9613b9ba4ff9a978e` | README describes a Python research/exploration QA assistant and its proposal/review/gold concepts; the working tree has active unrelated changes. | Strong conceptual predecessor, but not yet a stable release/runtime home for the proposed `net10.0` tool. |

The FieldWorks/LibLCM facts above are also documented in
`word-evidence-corpus-research.md`; the live-cache/companion constraints are in
`fieldworks-crdt-integration-research.md`.

## Ownership matrix

“Candidate homes” lists plausible locations, not equal recommendations. A component
should have exactly one code owner, although it can expose a versioned contract to
the others.

| Component | Candidate homes | Recommended owner | Why that home fits | Why the tempting alternatives do not |
| --- | --- | --- | --- | --- |
| Live texts, paragraphs, segments, wordforms, `WfiAnalysis`, human/parser opinions and `WfiWordSet` membership | FieldWorks + LibLCM | LibLCM model; FieldWorks services/UI | These are the existing project facts. They need LibLCM ownership, persistence, invariants and FieldWorks UOW/undo behavior. | Harmony and LexBox would become a duplicate project model; PanGloss snapshots are read-side parser inputs, not the editable source of truth. |
| Occurrence projection and anchor resolution | FieldWorks adapter plus companion contract | FieldWorks owns resolution against the live cache; companion owns the portable anchor record/status | `AnalysisOccurrence` is positional, so only FieldWorks can authoritatively resolve a current occurrence. The corpus needs a durable, reviewable record of `resolved/orphaned/ambiguous`. | Putting the whole object in LibLCM prematurely makes external review/run lifecycle model baggage; putting it only in the companion hides actual edit/reparse behavior. |
| Curated case, gold revision, required/forbidden/allowed policy, adjudication and reviewer rationale | New grammar-assessment companion application | New product/application repository | These are application review semantics, not universal linguistic objects. They need independent release, migration, audit, storage, and policies for human and AI reviewers. | `WfiWordSet` is only a wordform collection. Harmony is a framework, not the schema owner. PanGloss should not decide gold. FieldWorks can display/edit them, but should not become the only authority for a collaborative audit trail. |
| Parser-run manifest, deterministic comparison, structural analysis-identity profile, traces and immutable run artifacts | PanGloss; companion | PanGloss defines executable format/identity/comparison; companion stores run references and review state | The parser author is best placed to make output reproducible and traces truthful. The application decides which run is reviewed and retained. | FieldWorks's current ParserReport is an excellent navigation adapter but is wordform-level and lacks occurrence/gold/run identity. Harmony should synchronize metadata/links, not raw large artifacts. |
| Grammar importer, grammar snapshot, parsing, realization, FST health and machine-generated diagnosis | PanGloss | PanGloss | `pg-fwdata` and `pangloss` already accept `.fwdata`, JSON snapshots, XML and produce batch/trace/diagnose outputs. This avoids a second parser or exporter. | LibLCM owns model integrity, not parser engine execution. LexBox is lexical and should not fork a Rust parser. |
| Grammar and lexical semantic changes, commits, history, sync and opaque unknown changes | Application-specific Harmony changes in the companion, using Harmony | Companion owns grammar/evidence change classes; Harmony owns framework mechanics | The change type must express product semantics and its validation/ordering rules; `DataModel` supplies commit/snapshot/sync mechanics. | Adding language-specific change types to generic Harmony creates upstream coupling. LCAtom must not revive its runner. LexBox's lexical changes are useful examples but are not the grammar/text schema. |
| Ordered grammar conflict contract | Companion domain + PanGloss validation, with an upstream Harmony defect/extension only if a generic sequence primitive is missing | Companion initially; Harmony only for a reusable primitive | Ordering is semantically meaningful and must be validated through the parser/evidence gate. The application must define what can be concurrently merged or requires review. | Do not encode ordered grammar in existing LWW `SetOrderChange` semantics merely because Harmony is the transport. Do not make PanGloss a CRDT database. |
| `.fwdata`/Harmony import-export choreography and snapshot/recovery conformance | Companion adapter; reuse LexBox patterns | Companion-specific adapter, initially copied/adapted from LexBox with shared improvements proposed upstream when genuinely model-agnostic | LexBox's `FwDataMiniLcmBridge`, `FwLiteProjectSync`, `ProjectSnapshotService`, and CLI prove lifecycle/snapshot choreography. The new domain has materially different data. | Directly extending existing lexical MiniLcm would force text-analysis into a deliberately bounded lexical API. FieldWorks cannot safely share simultaneous low-level writers with an outside process. |
| Live “preview/apply approved command” adapter, UI-thread marshaling, a single undoable UOW, save/invalidation/read-back | FieldWorks | FieldWorks | The loaded `LcmCache`, `ActionHandlerAccessor`, undo labels, UI dispatcher, parser invalidation and project save all live here. | An out-of-process companion cannot safely mutate the live cache. LibLCM cannot own FieldWorks UI scheduling or application command routing. |
| Native grammar/evidence/comparison/adjudication views | FieldWorks Avalonia projects | FieldWorks (`FwAvalonia` / `FwAvaloniaDialogs` and product-specific modules) | The active migration deliberately establishes net48 Avalonia co-hosting now, with native controls and FieldWorks navigation/accessibility/localization. It is the future net10 desktop surface. | FWLite's web/MAUI UI is a separate product. A browser/React view would create a parallel UI stack the stated migration rejects. PanGloss and Harmony are not UI products. |
| View models, wire DTOs and query projections used by both Avalonia and CLI | New companion contract package in the application repository, TFM chosen for consumers | New product/application repository, with a narrow compatibility package | The contract needs to evolve with review workflow but must remain independent of `LcmCache`, Avalonia and EF. A netstandard-compatible DTO assembly can be consumed by a net48 FieldWorks adapter; net10 CLI can use the same schema. | Sharing FieldWorks internal classes leaks UI/runtime constraints. Sharing LexBox MiniLcm types would falsely promise a text-analysis model it does not yet have. |
| AI reviewer/operator CLI (discover cases, get evidence, call a configured AI endpoint, emit proposed judgments/changes, never directly mutate `.fwdata`) | New companion repo; LexBox; PanGloss; FieldWorks; Harmony | A separately deployable `net10.0` executable in the new grammar-assessment companion repository | It is an application client with its own secrets, provider configuration, rate/budget limits, resumable jobs, policy, reviewer identity and approval semantics. It should use PanGloss as a subprocess/library boundary and talk to the companion/Harmony through the versioned contract. | Harmony is a generic library. PanGloss's Rust CLI should remain a deterministic parser/diagnostic tool, not absorb LLM credentials or approval policy. The CLI remains out of process while FieldWorks is net48; after migration, separate deployment still provides fault and automation isolation. LexBox would couple an AI grammar-review product to lexical web/mobile release trains. |
| AI model-provider adapters, prompt/version provenance, safety policy, budget/rate limits and tool sandbox | Same new companion repository, behind interfaces | New companion application | These are operational policy and data-governance concerns, distinct from parser correctness. Persist model/prompt/evidence hashes as review provenance, not “human” authorship. | Do not put cloud credentials or model policy in PanGloss, Harmony, LibLCM, or the FieldWorks desktop process. |
| Reports and exports for people/CI | PanGloss plus companion | PanGloss for parser-native report portions; companion for cross-run/case/review report assembly | PanGloss already creates diagnosis, health and report artifacts. The companion can join those to cases, history, proposals and decisions. | A raw Harmony history is not a linguistically legible report; FieldWorks should render/navigate reports rather than own a second export pipeline. |
| Generic model additions such as a GUID-bearing occurrence-test or test-set concept | LibLCM/FieldWorks, but only after a demonstrated stable core | Defer; propose upstream only when multiple FieldWorks workflows require it | A generalized model object may eventually be justified, especially if it is useful without PanGloss/Harmony. | Do not add parser-run, AI provenance, reviewer queues, or gold-release machinery to the core model merely to avoid a companion database. |
| LCAtom coverage inventory, migration decisions, independent evidence and architecture ADRs | LCAtom | LCAtom | That is the repository's surviving role after ADR 0013. | It should not own new runtime state, CLI delivery, UI, or a rival semantic change pipeline. |

## The independent `net10.0` CLI

The user-facing role is not “a second parser.” It is a **non-human reviewer/operator**
that follows the same state machine as a human reviewer:

```text
select pinned case/revision
  -> obtain immutable FieldWorks/PanGloss evidence bundle
  -> optionally ask configured AI provider for a proposed judgment
  -> run deterministic validations and parser comparison
  -> emit a signed/provenanced proposal, abstention, or request for speaker review
  -> never apply to FieldWorks without the normal approval/apply path
```

Its minimum commands should be application verbs, not raw mutation verbs:

| CLI area | Example responsibilities | Owner/dependency direction |
| --- | --- | --- |
| `cases` | select tagged cases, materialize a redacted/pinned evidence bundle, show anchoring state | calls companion query contract; no direct `.fwdata` writes |
| `assess` | request a PanGloss run, compare baseline/candidate, emit an evidence receipt | invokes PanGloss through a stable process/API contract |
| `review` | propose required/forbidden/allowed judgment, abstain, flag insufficiency, attach rationale | writes a proposal through the application/Harmony contract |
| `propose` | draft bounded grammar or occurrence-analysis proposal from evidence | emits typed proposal only; does not forge human acceptance |
| `jobs` | resumable batches, budgets, model/provider selection, deterministic retry and audit | local operational state; durable server/companion receipt IDs |
| `apply` | deliberately absent from the first AI CLI, or restricted to submitting an approved command to FieldWorks' adapter | FieldWorks retains the live-cache apply authority |

The CLI should target **`net10.0`**, matching the companion, Harmony, and current LexBox backend.
It remains independently installable and out of process from net48 FieldWorks, but it can directly
reuse the companion's application-domain, Harmony, persistence, and client packages instead of
maintaining an artificial net8 compatibility layer. The FieldWorks-facing DTO/wire package must still
remain netstandard2.0-compatible (or schema-generated) until FieldWorks itself retargets.

PanGloss already has a valuable CLI, but it is intentionally deterministic: `pangloss import`,
`parse`, `batch`, `diagnose`, `fst-health`, and `make-report` establish parser inputs/outputs and
diagnostics. Extending it with an AI operator would couple Rust parser releases to credential
storage, data-egress consent, LLM provider churn and human-review policy. The new CLI should call
those commands or a future stable PanGloss library/FFI contract instead.

LexBox likewise has useful precedents (`FwLiteProjectSync` and `LcmDebugger` are existing command
line applications) but its CLI programs manipulate the lexical MiniLcm/CRDT synchronization
surface. Reusing their hosting, logging and test patterns is sensible; owning grammar-review
workflow there is not.

## Should there be a new repository?

### Recommendation: one new application repository, not a new repository per layer

Create a new repository only for the **grammar assessment and review companion product** after a
short design gate establishes its stable bounded context. Put the net10 CLI there from the first
deliverable. Its responsibility would be the portable contracts, evidence/case/gold/review domain,
Harmony integration, PanGloss orchestration, durable inbox/outbox, CLI, and (if it proves useful)
a service host. It does **not** absorb LibLCM, FieldWorks UI, Harmony framework code, or the parser.

This is valuable because it gives the product a coherent lifecycle separate from all three existing
release trains:

1. FieldWorks must remain `net48` in-process for a significant transition and is desktop/project
   lifecycle software.
2. LexBox is a lexical web/MAUI product with a deliberately limited MiniLcm surface.
3. PanGloss is a Rust engine whose correctness/release cadence should not depend on AI provider or
   review-workflow changes.
4. Harmony is a reusable framework; application schemas should not be upstream framework features.

The cost is a sixth integration boundary. Avoid making it a second project authority: FieldWorks
remains authoritative for live linguistic data, PanGloss for deterministic execution, and Harmony
for synchronized application state. The companion owns only the review/evidence domain and its
orchestration.

### When not to create it yet

Do **not** create an empty architecture repository before proving one vertical slice. The design
gate should specify and test:

1. a portable `GoldCase` and occurrence-anchor schema;
2. a PanGloss run/compare receipt with pinned inputs;
3. a FieldWorks adapter that previews one case without mutation; and
4. one net10 CLI command that produces a proposal or abstention with full provenance.

If that slice turns out to be only a PanGloss batch/report wrapper with no durable review state,
put it in PanGloss instead. If it becomes merely a lexical FWLite feature with no FieldWorks text
or grammar scope, put it in LexBox. The evidence so far points the other way: occurrence-bound
manual analysis, gold revision and AI/human adjudication are their own application domain.

## Rejected placements for the AI CLI

| Home | Why it is attractive | Decisive reason to reject as primary home |
| --- | --- | --- |
| Harmony | It already records commits and syncs. | A generic CRDT library should not own provider credentials, prompts, adjudication policy or a language-specific CLI. |
| PanGloss | It already parses `.fwdata`, produces traces, and has a CLI. | Parser correctness must remain deterministic and provider-independent; the AI agent is a consumer of PanGloss evidence. |
| FieldWorks | It has all project context and the future native UI. | It is currently in-process net48 and must retain human-controlled UOW/undo/apply authority; batch AI operation needs independent deployment and fault isolation. |
| LexBox/FWLite | It already has Harmony, MiniLcm, snapshot sync and CLI precedents. | Current scope is lexical. Adding texts, analyses, grammar and AI review would impose a new model/release/support burden on a separate web/mobile product. |
| LCAtom | It contains prior CLI/semantic change work and the current research. | ADR 0013 rejects restoring a competing runner, and LCAtom explicitly excludes UI, review, hosting and storage. |
| `linguistic-assistant` today | It has the closest research concepts: gold, propose, review, speaker questions and AI judgment. | It is explicitly a Python research/exploration repo with active unrelated work; it may later provide algorithms or fixtures, but is not yet a stable `net10.0` operational product home. |

## Consequences and guardrails

- One canonical live-data mutation path: approved command → FieldWorks adapter → loaded `LcmCache`
  → one undoable UOW → save/read-back. The CLI never writes `.fwdata` or LibLCM directly.
- One canonical parser result: PanGloss's pinned output/trace. An LLM may interpret evidence but
  cannot overwrite deterministic execution facts.
- One canonical review history: product-specific Harmony changes in the companion. A reviewer is
  typed (`human`, `AI`, `import`, `system`) and an AI decision is never represented as native-speaker
  confirmation.
- Keep raw run blobs, model transcripts and traces out of Harmony object payloads; store them as
  content-addressed artifacts and synchronize immutable references/digests.
- Do not make a generic LibLCM model change until a FieldWorks-only workflow demonstrates the
  concept is useful without the companion. That avoids permanently coupling parser/AI workflow to
  the project database.
- Do not present the companion UI in a browser as the FieldWorks product surface. The desktop
  views are FieldWorks Avalonia modules; the CLI is the separate non-interactive/agent surface.

## Bottom line

Use the existing repositories for their established responsibilities and create **one** new
application repository when the vertical slice proves the need:

```text
FieldWorks + LibLCM: live linguistic project and native Avalonia review surface
PanGloss: import, parse, identity/comparison primitives, trace and diagnostics
Harmony: generic commit/snapshot/sync framework
LexBox: lexical product; reusable bridge/snapshot patterns only
new grammar-assessment companion: cases/gold/review/orchestration/Harmony schema
  └─ independent net10 AI reviewer CLI
LCAtom: cross-repository research, decisions and coverage evidence
```

This preserves the requested independent net10 CLI without turning it into a new source of linguistic
truth or a fork of any of the existing products.
