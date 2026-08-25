# CLI → worker API surface

*2026-08-25. Research and design for CLI plan Task 1, step 3 — "move state changes and queries behind
explicit worker commands". Written because the plan gives that sentence one paragraph, and the shape of the
wire it implies is a decision that outlives every other choice in the task.*

## Why this document exists

The CLI has 30 verbs. Exactly one (`store-cutover`) goes through the worker. The plan says the rest must
follow, and says almost nothing about what that means on the wire.

The naive reading is one wire command per verb: ~29 new closed discriminators, each with a request and
response record, each frozen the moment a second client speaks it. That is the largest single commitment in
this plan and the least reversible. This document establishes what is actually there, and what the wire
surface has to be to cover it.

## 1. What the CLI actually does

Every verb, by what it touches. This is the inventory the design has to cover — not a summary of it.

| Verb | Proposal store | Project DB | LibLCM cache | Writes project |
| --- | --- | --- | --- | --- |
| `new` | draft | — | — | — |
| `add-set-gloss` | draft | — | — | — |
| `add-delete-lexeme-form` | draft | — | — | — |
| `promote-gloss` | draft | corpus read | — | — |
| `label` | draft | — | — | — |
| `comment` | draft | — | — | — |
| `remove-operations` | draft | — | — | — |
| `compose-author-lexeme-form` | draft | — | **read** | — |
| `compose-author-feature-structure` | draft | — | **read** | — |
| `finalize` | draft → proposal | — | — | — |
| `reopen` | proposal → draft | — | — | — |
| `duplicate` | proposal → draft | — | — | — |
| `split` | proposal → drafts | — | — | — |
| `defer` | decision | — | — | — |
| `approve` | decision | — | — | — |
| `reject` | decision | — | — | — |
| `supersede` | proposal | — | — | — |
| `list` | read | — | — | — |
| `show` | read | — | — | — |
| `add-corpus` | — | corpus write | — | — |
| `add-document` | — | corpus write | — | — |
| `add-corpus-bundle` | — | corpus write | — | — |
| `corpora` | — | corpus read | — | — |
| `show-corpus` | — | corpus read | — | — |
| `open` | — | — | **read** | — |
| `analyses` (plain) | — | — | **read** | — |
| `analyses --assessment` | — | assessment read | **read** | — |
| `log` | — | — | **read** | — |
| `dry-run` | read | — | **read** | — |
| `apply` | read | — | **read** | **yes** |

Three groups fall out, and they have different costs:

- **21 verbs touch only durable store state.** No LibLCM, no project file. These are pure database work the
  worker is already built to do.
- **7 verbs need an `LcmCache`.** These are the ones the plan means by "cannot read around the lock".
- **1 verb writes the project.** `apply` is its own plan (`apply-reconciliation`, 0/20) and is out of scope
  here.

## 2. What already exists (and is better than expected)

**The schema is complete.** Every table these verbs need is already in `MotifSchema`, created and validated:

```
Proposals   ProposalRevisions   Decisions   Drafts
Corpora     CorpusDocuments     Assessments AssessedWords
ParsedAnalyses  AssessmentPins  Reports     Receipts
AppliedIndex    Jobs            Baselines   MigrationLedger
```

`Drafts (DraftName PRIMARY KEY, ProposalId, DraftJson)` is the important one: **drafts were always meant to
live in the worker**, not stay CLI-local. The migration already imports them. The `ProposalStore` doc comment
still says drafts "never leave this machine" — that describes the file store being replaced, and will be
wrong the moment drafts move.

**`ProposalRepository` already covers the proposal lifecycle**: `Get`, `List`, `SaveRevision`, `SaveDecision`,
plus archive support. The lifecycle verbs need almost no new persistence code.

**The projections are already the response shapes.** `ProposalListProjection`, `ProposalDetailProjection`,
`CorpusProjection`, `DryRunProjection`, `ApplyProjection`, `AppliedLogProjection`, `AnalysisAggregateProjection`,
`ProjectSummary` — ADR 0021 decision 2 already forces every read surface to render text and JSON from one
projection. A wire response does not need inventing; it needs *moving*.

**One portability catch.** `SIL.Motif.Projection` targets `net10.0`; `SIL.Motif.Contract` targets
`netstandard2.0`. If projections become wire payloads, the FieldWorks-side `netstandard2.0` client cannot see
them. `SIL.Motif.Runner` already multi-targets `netstandard2.0;net10.0`, so the precedent for fixing this
exists — but it is work nobody has scheduled, and it is a prerequisite for Task 5, not for the CLI.

## 3. The decision: how many commands?

This is the question the plan does not answer. Three honest options.

### Option A — thin storage seam (~6 commands)

`draft.get`, `draft.save`, `proposal.get`, `proposal.save`, `corpus.get`, `corpus.save`. The worker stores
bytes with compare-and-swap; the CLI keeps every authoring rule.

- Smallest wire surface, fastest to build.
- **Rejected.** It does not do what the plan asks. "Move state changes behind explicit worker commands"
  means the worker decides, and here it would not: a second client would have to reimplement 59 distinct
  refusals to behave the same. It also makes the worker unable to reject a draft it should never have
  stored, which is precisely the class of bug this session has been fixing all week.

### Option B — one command per verb (~26 commands)

Direct translation. Every CLI verb gets a discriminator, a request record, and a response record.

- Most explicit; each command's capability and payload are independently reviewable.
- **Rejected.** 26 frozen discriminators is a large, permanent surface, and most of them differ only in
  their payload. Adding a 31st authoring verb would mean a new wire command, a new capability decision, and
  a new round of registry tests — for what is really a new *payload*, not a new *interaction*.

### Option C — closed payload unions, grouped by interaction (~10 commands) — **recommended**

One command per *kind of interaction*, each carrying a closed, discriminated payload union. Adding a verb
adds a union case, not a wire command.

| Command | Capability | Covers | Payload union |
| --- | --- | --- | --- |
| `draft.edit` | `store.v1` | `new`, `add-set-gloss`, `add-delete-lexeme-form`, `promote-gloss`, `label`, `comment`, `remove-operations` | edit kind |
| `draft.get` | `store.v1` | draft read for rendering | — |
| `proposal.revise` | `store.v1` | `finalize`, `reopen`, `duplicate`, `split` | revision kind |
| `proposal.decide` | `store.v1` | `defer`, `approve`, `reject`, `supersede` | outcome |
| `proposal.query` | `store.v1` | `list`, `show` | query kind |
| `corpus.ingest` | `corpus.v1` | `add-corpus`, `add-document`, `add-corpus-bundle` | source kind |
| `corpus.query` | `corpus.v1` | `corpora`, `show-corpus` | query kind |
| `project.read` | `project.v1` | `open`, `analyses`, `log` | read kind |
| `project.compose` | `project.v1` | both `compose-*` verbs | composer kind |
| `job.submit` | `jobs.v1` | `dry-run`, later `apply` | job kind |

Ten commands, four capabilities. `job.status` already exists.

**Why a closed union is the right idiom here, and not a hedge.** It is what this repo already does. The
change-set contract carries every operation as `{kind, entityId, after, …}` against a closed kind registry —
ADR 0012 established that typed-per-kind surfaces do not scale past a few hundred generated kinds and forced
a generic discriminated path. `WorkerCommands` is itself a closed discriminator registry with a
`RequiredCapability` switch. A closed edit-kind registry is that same idiom one level down, and it inherits
the same property that matters: **the worker can still refuse an unknown kind before dispatch**, exactly as
`WorkerCommands.RequireKnown` refuses an unknown command today.

## 4. What has to move, and what must not

**Moves to the worker:** every mutation and every query, plus the validation that decides whether a mutation
is legal. That is the point of the exercise.

**Stays in the CLI:** argv parsing, flag validation, and rendering. `CommandTextRenderer` and
`ProjectionJson` already split rendering from data, so this line is already drawn.

**The 59 refusals are the real cost.** `Commands.cs` has 59 distinct `Fail(...)` sites, many with messages
that name the store path, list valid alternatives, or suggest the next command. Under Option C the *decision*
to refuse moves to the worker while the *wording* stays in the CLI. That needs a typed refusal payload with
enough structure to render the same message — which is exactly the shape B31's `WorkerRefusalEnvelope`
established, and the reason its reasons are a closed enum carrying no caller text.

This is the single largest piece of work in the migration and the easiest to underestimate. It is not
plumbing; each refusal is a behaviour someone relied on.

## 5. Sequencing

Ordered so that each step is independently gateable, and so the riskiest work happens last.

1. **`draft.*` + `proposal.*`** — 5 commands, `store.v1`, 21 of the 30 verbs. No LibLCM, no new
   infrastructure, and `ProposalRepository` already does most of the persistence. This is where the
   refusal-fidelity problem gets solved once, for everything after it.
2. **`corpus.*`** — 2 commands, `corpus.v1`. Self-contained; `SqliteCorpusStore` moves behind the worker.
3. **`project.read`** — 1 command, `project.v1`. First command needing a cache, so first to face
   "which Baseline, and how stale?" Must display its "as of" token, per the plan's Task 2.
4. **`project.compose`** — 1 command. Composers mutate a draft *and* need a cache, so this is the first
   place both halves meet. Do it after both halves work separately.
5. **`job.submit`** — `dry-run` becomes a job. `DryRunJobHandler` exists but has no production caller;
   this is what would give it one.
6. **Drop `Host` and `Runner` from the CLI.** Only possible once nothing above still needs them. This is
   the step that proves the migration actually happened, and it should be a mechanical no-op if the first
   five are honest.

`apply` is not in this list. It belongs to `apply-reconciliation` (0/20), and it is the one verb that writes
a real project.

## 6. Open decisions

These need a person, not a default.

1. **Draft ownership.** The schema says drafts live in the worker. Confirming that means a draft is no
   longer a local file a user can inspect or hand-edit, and `ProposalStore`'s "never leave this machine"
   guarantee is retired. Worth saying out loud before it is assumed.
2. **Refusal fidelity.** Does every one of the 59 refusals have to survive with its current wording, or is
   a smaller structured set acceptable? This decides whether step 1 is days or a week.
3. **Capability granularity.** Four capabilities (`store.v1`, `corpus.v1`, `project.v1`, `jobs.v1`) or one
   coarse `cli.v1`? Finer capabilities let an old worker refuse precisely; coarser means fewer negotiation
   states to test.
4. **Projection portability.** Multi-target `SIL.Motif.Projection` to `netstandard2.0` now, or keep
   projections CLI-side and define separate wire records? Only matters once the FieldWorks package is
   in scope — which is currently deferred.

## 7. What this changes about the plan

The plan's Task 1 step 3 reads as one step. It is six, and one of them (the refusal surface) is larger than
the other five together. Nothing here contradicts the plan's intent; it makes the wire commitment explicit
before it is made, which is the part that cannot be undone later.
