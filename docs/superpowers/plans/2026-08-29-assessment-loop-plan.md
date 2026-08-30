# The assessment loop — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Deliver [ADR 0042](../../adr/0042-a-job-produces-assessments-an-assessor-makes-them.md) and
[the Proposal lifecycle](../../proposal-lifecycle.md). A linguist — human or AI — can trial a Proposal, see
what it would do and how well the result parses, compare trials to each other and to the Baseline, and apply
with the evidence attached.

**Architecture:** `codebase-design` vocabulary — module, interface, depth, seam, adapter, leverage, locality.
Domain terms are `CONTEXT.md`'s, including the five added with ADR 0042: **Assessor**, **Assessment kind**,
**Assessment scope**, **Trial**, and the corrected **Assessment**.

**Tech Stack:** `net10.0` except `SIL.Motif.Contract`; LibLCM, SQLite, xUnit. Gate is `./test.ps1`, which
now refuses to start while a stale `testhost` holds the build output.

**Standing rules:**
- Run `./test.ps1` in the **foreground** before every commit and state the test count and its delta. Never
  background it and poll — an agent lost an hour to that (`issues.md` D14).
- Deletions are as much a deliverable as additions. Name what was deleted, with line counts.
- A measurement that cannot be reproduced is not a measurement. Every Assessment records what it ran
  against, by content.

**What already exists and must not be rebuilt.** `IPanGlossAssessor`, `PanGlossParser` with its FST-refusal
fallback, `PanGlossCandidateExporter`, `PanGlossWorkspace`, `AssessReport`, `GrammarCoverageFigure` (which
already refuses to report a percentage when nothing was adjudicated, and marks itself a lower bound when
words timed out), and five tables. None of it has a production caller. This plan wires it, and rebuilds none
of it.

**Order:** Task 1 is configuration and blocks nothing. Task 2 is the schema and blocks 3–7. Task 3 is the
Assessor seam and blocks 4. Tasks 5–7 are the reading surface. Task 8 is independent.

---

### Task 1: The project configuration file

ADR 0042 decisions 3 and 5. `<project>.motif.toml` beside the project, human-readable and diffable, holding
declared Assessment scopes, the regression policy, and whether applying sweeps a Proposal's working
artifacts.

**Files:**
- Create: `src/SIL.Motif.Host/Config/ProjectConfiguration.cs`, `ProjectConfigurationFile.cs`
- Test: `tests/SIL.Motif.Tests/Config/`

- [x] **Step 1: Write the tests** — an absent file yields documented defaults; a malformed file refuses
  **naming the line**; an unknown key refuses rather than being ignored, because a silently-ignored policy is
  worse than a missing one; a declared scope round-trips.
- [x] **Step 2: Run red**
- [x] **Step 3: Implement.** Defaults: one scope named `default`, words = all with a manual analysis, engine
  = the fast one, per-word limit 1s, regression gating **off**, purge-on-apply **on**.
- [x] **Step 4: `motif config show --project P [--json]`**, so a caller can see the resolved configuration
  rather than guessing which defaults applied.
- [x] **Step 5: Run green and commit**

---

### Task 2: Schema generation 10

The tables exist and are close. `Assessments` already carries `CorpusWordsJson` (a resolved Selection),
`CorpusSha256`, `GrammarSourceSha256`, `ModelFingerprint` and `Pipeline`. What is missing is everything ADR
0042 added.

**Files:**
- Modify: `src/SIL.Motif.Host/Store/MotifSchema.cs`, `src/SIL.Motif.Worker/Store/` (a new repository)
- Test: `tests/SIL.Motif.Tests/Store/MotifDatabaseMigrationTests.cs`

- [x] **Step 1: Write the migration tests** — every existing row survives; the new columns are populated for
  old rows in a way that is honest about not knowing (see below).
- [x] **Step 2: Run red**
- [x] **Step 3: Migrate.** `Assessments` gains `Assessor`, `Kind`, `ScopeJson`, `ScopeDigest`,
  `TokeniserName`, `TokeniserVersion`, `BaselineToken`, `ProposalIntentDigest` (nullable — a Trial of an
  uncommitted draft cites content, not a revision). `Reports` gains `Kind` and `RenderedText`. A project-level
  **current Assessment pointer**.

  **There are no existing rows in the wild** — nothing has ever written this table — so the migration should
  assert that rather than invent values for columns it cannot know. If a row exists, refuse and say so.
- [x] **Step 4: An `AssessmentRepository`** with the interface the later tasks need: record an Assessment
  with its scope and kind, read one, list by Proposal, promote one to current.
- [x] **Step 5: Run green and commit.** State what the compatibility floor moved to.

---

### Task 3: The Assessor seam

ADR 0042 decisions 1 and 8. An **Assessor** produces Assessments of declared kinds. PanGloss is the first;
the seam exists so a C# HermitCrab or an alignment model is an addition rather than a redesign.

**Files:**
- Create: `src/SIL.Motif.Host/Assess/IAssessor.cs`, `PanGlossAssessor.cs`, `AssessorCatalog.cs`
- Create: `src/SIL.Motif.Worker/Assess/StatsCacheStore.cs`
- Test: `tests/SIL.Motif.Tests/Assess/`

- [x] **Step 1: Design the interface first and write it down.** An Assessor declares **which kinds it can
  produce** and produces them for a scope. That declaration is what lets a Report refuse with *this scope did
  not collect per-object counters* rather than returning zeros.
- [x] **Step 2: Write the tests against a fake Assessor**, not against PanGloss. The seam is the test
  surface; `FakePanGloss` already exists for the process-level contract.
- [x] **Step 3: Run red**
- [x] **Step 4: Implement `PanGlossAssessor`** over `pangloss batch --stats --cache <path>`. Stats caches
  live under the worker root beside Baselines, keyed by grammar digest + Assessor + engine; the Assessment
  records the path and digest (ADR 0041 decision 9). A cache refuses to mix engines — respect that rather
  than discovering it.
- [x] **Step 5: Run green and commit**

---

### Task 4: `motif trial`

The composite: one scratch, one Baseline load, producing a Dry Run **and** the candidate's Assessments.

**Files:**
- Modify: `src/SIL.Motif.Worker/Jobs/DryRunJobHandler.cs` or a sibling handler, `Program.cs`
- Modify: `src/SIL.Motif.Cli/JobCommands.cs`, `Program.cs`

- [x] **Step 1: Write the argv test** — `motif trial <proposalId> --project P` prints a job id; the job
  produces one Dry Run and at least one Assessment; a Trial of an **uncommitted draft** succeeds and its
  Assessments cite the draft's intent digest.
- [x] **Step 2: Run red**
- [x] **Step 3: Implement.** A Trial is a kick-off: **no Proposal state changes.** Reuse the existing
  single-open scratch — B32's fix must not be undone by opening the Baseline again for the parse.
- [x] **Step 4: Run green and commit.** `dry-run` survives unchanged as the cheap question.

---

**Resolved in Task 5.** `WordQueryResolver` exists and `TrialJobHandler` calls it, so a Trial now measures
the declared Selection. The note is kept below as written, because the reasoning in it is why the resolver
had to land before Task 6 rather than after.

**Carried forward from Task 4, and it must be answered before Task 6 compares anything.** A scope declares
its words as a *query* — the default being "all words carrying a manual analysis" — and **nothing resolves
that query yet**. A Trial currently measures every non-empty wordform in the project, which is broader than
the declared default. Two Assessments joined on the word are still honest about the words they share, so this
is not a correctness bug in comparison; it is a measurement that says it covered a Selection it did not. The
resolver belongs here, and until it exists a scope's `Query` is decorative.

### Task 5: Reports

ADR 0042 decision 4, and decision 4's amendment: an Assessor returns raw material, a Report is its
presentation, and computing one may mean asking the Assessor to read its own format.

**Files:**
- Create: `src/SIL.Motif.Host/Assess/ReportCatalog.cs`
- Modify: `src/SIL.Motif.Cli/`

- [x] **Step 1: Tests** — a report over a scope that did not collect what it needs refuses **naming the
  reason**; a rendered report is stored at measurement time and readable when the Assessor binary is absent
  (ADR 0042's Q19 answer — yesterday's evidence must not become unreadable when a binary moves).
- [x] **Step 2: Run red**
- [x] **Step 3: Implement** `motif report --project P --assessment A --kind <k> [--word W] [--text T]` and
  `--list-kinds`. `--kind` is a registry, not a switch: adding a kind must not need a new verb.
- [x] **Step 4: Run green and commit**

---

### Task 6: Comparison

ADR 0042's amendment: two Assessments compare by **joining on the word**; what must match is the word and the
kind. Assessor, engine, limits and corpus are context that annotates.

**Files:**
- Modify: the report catalog; `src/SIL.Motif.Cli/`

- [x] **Step 1: Tests** — two Assessments over different word sets compare on the intersection; a differing
  tokeniser **warns and still compares**; analysis equality is `MatchesIWfiAnalysis`'s shape
  ([ADR 0027](../../adr/0027-what-counts-as-the-same-word-analysis.md)) and is not reinvented.
- [x] **Step 2: Run red**
- [x] **Step 3: Implement** as an Assessment of the **difference** kind, stored and citable — `compare` is
  sugar over producing one, not a separate mechanism.
- [x] **Step 4: Run green and commit**

---

### Task 7: Promotion, gating, and the sweep

ADR 0042 decisions 5 and 6, and the amendment's trap.

- [x] **Step 1: Tests first, and one of them is the trap.** Applying promotes the candidate Assessment to
  current; the sweep then removes the Proposal's other Trials, Dry Runs and Assessments **and the promoted
  one survives**. Assert it by identity after a sweep, because the failure is invisible until a later Trial
  cannot produce a delta.
- [x] **Step 2: Run red**
- [x] **Step 3: Implement.** Promotion happens **before** the sweep and the promoted Assessment is excluded
  by identity, not by ordering. A regression gates only when the configuration says so; an override is a
  Decision with an actor and a comment.
- [x] **Step 4: Run green and commit**

---

### Task 8: `motif jobs assessments <jobId>`

The one genuinely missing lookup: an agent that enqueued three Trials has three job ids and no way to reach
what they produced.

- [x] **Step 1: Argv test, implement, run green and commit**

---

## Out of scope

- **Build time and FST size.** PanGloss emits them on stderr and in a separate build-evidence type rather
  than in the stats cache. A PanGloss-side ask, recorded rather than worked around by scraping stderr.
- **The Layer 1 intent catalogue.** ~186 operations have no intent-level verb. Deferred deliberately: there
  is no point authoring changes faster than they can be judged.
- **FieldWorks integration.**

---

## 2026-08-29 — delivered

All eight tasks are in `main`. What the plan did not anticipate is recorded here rather than edited above.

| Task | Commit | Gate |
| --- | --- | --- |
| 1 — the configuration file | `72b5318` | 1228 |
| 2 — schema generation 10 | `496b656` | 1237 |
| 3 — the Assessor seam | `f855159`, corrected `361c532` | 1258 |
| 4 — `motif trial` | `f79062c` | 1266 |
| 5 — the resolver and Reports | `54b1b4e` | 1285 |
| 6 — comparison by word join | `f1bcb83` | 1302 |
| 7 and 8 — promotion, sweep, lookup | this commit | 1317 |

**The seam needed two corrections, and both were found by looking rather than by building.** A fable review
run while nothing depended on it found that `ProduceAsync` computed each measurement and returned only its
kind name — three of the four remaining tasks would have hit that wall. Its first real consumer then found
the same interface discarding provenance. Reviewing an interface before its callers exist is cheap; the two
corrections together cost an afternoon against a redesign under four dependent tasks.

**One correction was to a claim of mine.** `KindsFor(scope)` was scope-dependent because I said Reports would
refuse from it. They do not — they refuse from the *stored* scope and kinds, because the Assessor's binary
may be gone by then. `SupportedKinds` is scope-independent as a result.

**Three tasks in a row were shaped by one constraint.** PanGloss's and FieldWorks' identity digests are not
comparable, so correctness reports whether a word is still analysed at all, and comparison declines to
re-derive a verdict across the two identity spaces. Each task found it independently; front-loading it in the
brief is what stopped the third from claiming the stronger thing.

**Two gaps are open and recorded rather than hidden**: a Proposal's job rows are not swept on apply, because
`Jobs` has no `ProposalId` (`issues.md` B36); and `apply` enforces no status, which this plan's own lifecycle
document wrongly claimed it did (B37).
