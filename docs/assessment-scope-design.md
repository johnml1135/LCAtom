# Assessment scope — the design problem, and what is already settled

*2026-08-29. A working design document, not a decision. It exists so the next grilling session starts from
what is known rather than from a blank page.*

**In plain terms:** before Motif can tell a linguist whether a grammar change helped, it has to have measured
the same thing twice. That sounds obvious and is the whole difficulty: two runs of the parser can differ in
which words they tried, which engine they used, how long they were willing to wait, and how much detail they
recorded — and two numbers produced under different conditions cannot be subtracted. This document names the
thing that must be held equal (a **scope**), records what has already been decided about measurement, and
sets out what has not.

---

## 1. What is already settled

Each of these is recorded elsewhere; this section exists so the design does not relitigate them.

### From the ADRs

| Decision | Source |
| --- | --- |
| PanGloss answers *did this word parse*; FieldWorks defines *does the parser agree with a human*; Motif owns *did our words exercise the grammar we declared*; the **linguist** answers *what would make this better* | ADR 0033 |
| Motif reimplements FieldWorks' counts keeping its field names and JSON shape, rather than inventing a parallel vocabulary | ADR 0033 decision 1 |
| Motif reports and does not answer *what would make this better* — it supplies what failed, what a human rejected, what nobody has judged, what was never exercised, and **what changed since the last run** | ADR 0033 decision 4 |
| Assessments are **stored**, because a full parse is slow; every report is a cheap query over the stored result | ADR 0035 |
| Reports are **advisory** and do not gate | ADR 0035 decision 1 — *now amended, see below* |
| A Proposal's report **pins** the Assessment it was computed against | ADR 0035 decision 3 |
| Which words a report covered is recorded on the Proposal as **non-hashed provenance**, so a reviewer may run a quick check or a full one without disturbing approvals | ADR 0035 decision 5 |
| Expectations are FieldWorks-approved analyses | ADR 0038 |
| Feeding-order reorders require a Grammar Delta | ADR 0028 |
| Agents address Layer 1 (the verb surface); Layer 0 (the hashed operation vocabulary) must not churn | ADR 0021, ADR 0029 |
| Anything only Motif reads lives in a database; anything another program must open stays a file, and the database records its path and digest | ADR 0041 decision 9 |

### Settled in this grilling session

1. **A project has a current Assessment, and applying a Proposal promotes that Proposal's candidate
   Assessment to current.** Promotion is bookkeeping only — the Baseline refreshes lazily when something next
   needs it, and a promoted Assessment carries the Baseline token it was measured against so it can never
   silently describe a project that has moved.
2. **A regression may gate, per project, and the policy is a human-readable file** — `<project>.motif.toml`
   beside the project, editable and diffable. This **amends ADR 0035 decision 1**, which said a configured
   target does not gate. The model is a failing check on a pull request: it blocks, a human may override, and
   the override is recorded. Two things make a regression: coverage dropping, and an approved analysis no
   longer being produced.
3. **Motif never holds a verdict.** PanGloss generates the data, Motif orchestrates PanGloss, stores what
   comes back, and presents it. *"This is better"* is the linguist's sentence, human or AI.
4. **The AI loop lives outside Motif.** `../linguistic-assistant` is the AI linguist ADR 0033 already
   describes. Its MDL scorer, its accept gate and its stall limit are answers to *what would make this
   better*, and belong on its side of the line. Motif's job is to be a good enough API that the loop needs
   nothing else.
5. **Per-rule and per-morpheme cost belongs to PanGloss**, which has built it: a SQLite stats cache whose
   `fact` table books `self_time_ns` at each object boundary, keyed by word, object, stratum, allomorph and
   direction. Motif stores and reports it; Motif does not derive it.
6. **Word statistics are a Report over an Assessment, not a second kind of Assessment.** One PanGloss run is
   one Assessment; coverage, never-fires and per-rule cost are all queries over it.

---

## 2. What is deferred, deliberately

- **FieldWorks integration.** Out of scope entirely for this work.
- **Which engine the fast loop runs.** Open — see the constraint in §4.
- **Who owns the stats cache files and how they are keyed.** Open.
- **Build time and FST size**, which PanGloss emits on stderr and in a separate build-evidence type rather
  than in the stats cache. Open, and a PanGloss-side dependency.
- **The Layer 1 intent catalogue.** Two Intents exist as composers; the other ~186 operations have no
  intent-level verb. Real design work, deferred until the assessment loop can tell anyone whether a change
  helped — there is no point authoring changes faster than they can be judged.
- **Retention of Assessments and stats caches.** Jobs are capped at 500 per project; nothing decides how many
  Assessments or caches a project keeps.

---

## 3. The problem: two numbers are only comparable if the same thing was measured

A PanGloss run varies along at least five axes:

| Axis | Example values | Consequence of differing |
| --- | --- | --- |
| **Which words** | all texts; one text; every word carrying a manual analysis | Coverage over different denominators is not a delta |
| **Which engine** | `foma`, `default` (HermitCrab) | Different questions answered — see §4 |
| **What is collected** | parse outcomes only; outcomes plus per-object counters | A report may be unanswerable from the run |
| **Limits** | per-word timeout, step cap | A timeout is *we stopped waiting*, not *it does not parse* |
| **Grammar** | the digest of the grammar actually parsed | The point of the comparison |

Only the last should differ between a Baseline Assessment and a candidate's. **Everything else held equal is
what a scope is.**

### The proposed shape

**Assessment Scope** — a named, declared description of what to measure: which words, which engine, what to
collect, and what limits apply. Declared per project, with a default.

Three properties it has to have:

1. **A Baseline runs the superset.** The Baseline's Assessment is measured under the union of every scope any
   Proposal will use, so a candidate measured under a narrower scope can be compared without re-running the
   Baseline. This is the owner's insight and it inverts the naive design: the expensive run happens once, on
   the widest scope, ahead of time.
2. **Comparability is a subset relation, not equality.** A candidate Assessment is comparable to a Baseline
   Assessment when its scope is contained in the Baseline's along every axis, and the comparison is computed
   over the intersection. Requiring equality would force every Proposal to pay for the full scope.
3. **Reports fall out of the scope.** A report is answerable only if the run collected what it needs — which
   is why an Assessment must record its engine and its collection settings, not just its results. Asking a
   `foma` Assessment which rules were slow must fail with *this scope did not collect per-object counters*,
   naming the reason, rather than returning zeros.

### Where the pieces live

Following ADR 0041 decision 9:

- The **scope declaration** is configuration a human edits — the `.motif.toml` beside the project.
- The **resolved scope** is embedded in the Assessment row, by content and digest, so an Assessment can never
  be reinterpreted by a later edit to the file.
- The **stats cache** is a SQLite file PanGloss owns the format of; Motif records its path and digest.
- The **Assessment** is a row in the project's paired database.

---

## 4. The constraint that shapes everything

`--engine=foma` **records word-level timing only.** Per-object counters — the ones that say which rules and
morphemes cost the time — are collected by the HermitCrab engine alone. And HermitCrab is slow: Motif's own
`PanGlossParser` records *zero of fifteen words finishing in ten minutes* on it, and `linguistic-assistant`
drives it with 45-second per-word timeouts.

The stated goal is three proposals assessed in about three minutes. **That is a foma budget.** So the fast
loop and the diagnostic cannot be the same run, and a scope has to be able to say which it is.

This is the single fact most likely to be designed around wrongly, because both runs are called "an
assessment" and produce something called "coverage".

---

## 5. The two workflows

```mermaid
flowchart TD
    A[Baseline captured] --> B[assess under the superset scope]
    B --> C[(Baseline Assessment: current)]
    C --> D{linguist asks a question}
    D --> E[Report: coverage / never-fires / rule cost]
    E --> F[author 1..n Proposals]
    F --> G[dry-run each on its own scratch]
    G --> H[assess each candidate under a narrower scope]
    H --> I[Grammar Delta vs the Baseline Assessment]
    I --> J{linguist chooses}
    J -->|apply| K[apply to the project]
    K --> L[promote that candidate Assessment to current]
    L --> M[Baseline refreshes lazily when next needed]
    J -->|discard| F
```

Who owns each step:

```mermaid
flowchart LR
    subgraph PanGloss
        P1[parse words]
        P2[stats cache: per-rule cost]
    end
    subgraph Motif
        M1[orchestrate runs]
        M2[store Assessments]
        M3[Reports and Grammar Delta]
        M4[apply, receipts, the applied log]
    end
    subgraph Linguist
        L1[choose what to change]
        L2[judge whether it is better]
    end
    M1 --> P1
    P1 --> P2
    P2 --> M2
    M2 --> M3
    M3 --> L1
    M3 --> L2
    L2 --> M4
```

The line between the second and third box is ADR 0033 decision 4, and it is the one most likely to erode:
every convenience that reads *"tell me which proposal is best"* moves a verdict into Motif.

---

## 6. What the next session has to decide

1. **What exactly is an axis of a scope**, and is the list closed? The five in §3 are observed, not derived.
2. **How is the superset computed?** Declared by hand in the `.motif.toml`, or derived from the scopes a
   project actually uses — and what happens when a Proposal wants a scope the Baseline never ran.
3. **Is a Report stored or recomputed?** ADR 0035 says reports are cheap queries, implying recomputation; a
   Check Run freezes its inputs, implying storage. Both may be true, and the rule needs stating.
4. **What happens when the `.motif.toml` changes** and existing Assessments reference a scope that no longer
   exists as declared.
5. **How a scope names its words.** A **Selection** ("all words carrying a manual analysis" is the proposed
   default) is a `CONTEXT.md` term already; is a scope's word set always a Selection?
6. **Report tailoring.** *"Skim it down to one word, or one text"* — is that a filter argument on a report, or
   a narrower scope? They are different: one re-queries stored data, the other implies a new run.
7. **Retention.** How many Assessments and stats caches a project keeps.
