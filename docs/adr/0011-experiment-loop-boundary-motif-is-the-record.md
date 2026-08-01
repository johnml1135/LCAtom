# ADR 0011 — The experiment loop boundary: Motif is the record, not the orchestrator

Status: **superseded (2026-07-27)**
> **SUPERSEDED (2026-07-27) by [ADR 0013](0013-harmony-is-the-change-mechanism.md).** This ADR
> describes how to grow a change-management mechanism that ADR 0013 declines to build, on the
> grounds that Harmony's `Commit`/`IChange` already provides it. Retained as a record of the
> reasoning, not as a live decision.


Amends [ADR 0010](0010-hermitcrab-experimentation-is-the-primary-purpose.md). Settles a three-way
disagreement between [HC surface scope](../hc-surface-scope.md) §6,
[stage-2 change management](../stage2-change-management.md) S5, and
[build stages](../build-stages.md).

## Context

ADR 0010 made the HermitCrab experimentation loop Motif's primary purpose but did not say where
Motif stops and the rest of the loop begins. Three documents then answered differently:

- **HC surface scope §6** claimed Motif "owns comparison and the verdict", and that *"did it parse
  better" = diffing gloss signatures and confirmed counts across runs — Motif's job*.
- **Stage-2 S5** claimed the opposite: PanGloss "is run by a thin orchestration script, **never by
  Motif**", and Motif "**stores and lists by provenance; it never interprets** tool output."
- **Build stages** excluded both PanGloss orchestration *and* "text reports as attachments" from this
  repository entirely.

Two were `[core]` decisions in direct conflict, and the third excluded a capability S5 marked `[core]`.

## Decision

**Motif is the record. It never runs the parser and never renders a verdict.**

The loop divides like this:

```
Motif                         │ external infrastructure
───────────────────────────────┼──────────────────────────────
author a change set            │
assess (state delta)           │
export .fwdata, N change sets  │
applied to a scratch copy    ──┼─► build the PanGloss FST
                               │   run it over a corpus
                               │   produce reports + metrics
receive labelled attachments ◄─┼──
and typed metrics              │
store, list, diff, render      │
apply for real (or discard)    │
```

### 1. Motif stores and displays; it never interprets

S5 holds; HC surface scope §6 is wrong and is corrected. Parsing a tool's report and concluding
something from it is forbidden. Displaying a number the tool *declared*, and showing how it moved
between two change sets, is not interpretation — it is the record doing its job.

### 2. Forward projection is deleted, reverse `Expand` is not

Confirmed from [HC surface scope](../hc-surface-scope.md) "Scope consequences" §1: PanGloss reads
`.fwdata` directly and labels the XML path *"legacy … being sunset"*. **PanGloss is the projection.**
There is no forward-projection component to build, and the plan's
harvest-`GenerateHCConfig` work is deleted.

This does **not** delete `SIL.Motif.HermitCrab`. Reverse `Expand` — authoring change sets in
HC-friendly grammar terms rather than raw LibLCM field terms — remains the flagship composer and the
primary authoring surface for grammar work ([ADR 0001](0001-hermitcrab-projection-not-canonical.md),
[ADR 0009](0009-layered-api-primitives-and-composers.md) §6). What is deleted is only the *forward*
direction, and with it the round-trip oracle that forward projection was going to provide. Reverse
`Expand` must therefore be validated another way — against HC's own conformance suite (see
[HC surface scope](../hc-surface-scope.md), "The oracle"), not against a projection we no longer build.

### 3. Export is hypothetical, on a scratch copy

`export` copies the project, opens a fresh `LcmCache` on the copy, applies N change sets in
authoritative order, saves, and emits the resulting `.fwdata` together with its state digest. The
copy and the cache are then discarded. **The real project is never touched.**

Measured on `TestLangProj` (43 MB, 61 entries): the copy costs **0.05s**; a cache load costs **10.1s
cold, 3.6s warm**. The copy is free; the cache load is the real cost, and it is paid on the scratch
project rather than by disturbing the user's.

This is not merely convenient. It is the only safe way to experiment:

- **[C15](../issues.md)** — a rolled-back assess poisons the headword and homograph caches, and no
  bulk-invalidation hook exists, so a poisoned cache *cannot be repaired*, only discarded. Experiment
  by apply-then-rollback is therefore structurally unsafe however carefully written. On a scratch
  copy the cache is discarded regardless, so the hazard cannot fire.
- **[C2](../issues.md)** — single-writer is unenforced. Nobody else holds the scratch copy, so
  exclusive write is guaranteed by construction for every experiment. Real contention narrows to
  actual apply.
- **ADR 0010 §6** made reversibility a product requirement. Discarding an experiment must cost
  nothing and leave nothing behind. Deleting a directory satisfies that; rollback does not.

Export may combine change sets that have never been applied and may never be, which is the point:
"what would A and B together do?" is answerable without committing to either.

### 4. Attachments and metrics are labelled, typed, and bound to the intent digest

Extending S5, which specified content-addressed blobs with provenance but no label taxonomy and no
structured metrics:

- **Attachments** are report blobs carrying a configuration-declared **label** — `"PanGloss Report"`,
  `"Changed Word Analysis from Corpus A"`. Configuration may declare a label's payload as Markdown,
  in which case the CLI pretty-prints it (`motif show <id> --attachment pangloss-report`).
- **Metrics** are configuration-declared typed values — `corpus-a-coverage: percent`,
  `regression-status: enum[pass,fail]`. Motif stores them, lists them, and shows how one moved
  between change sets. Types are declared now so that a future gate can be added without migrating
  stored data.
- **Both bind to the `intentDigest`, not to the `changeSetId` alone.** A change set that is amended
  gets a new intent digest, and any report gathered against the old one must be flagged stale rather
  than silently presented as describing the new content. This falls out of S1's keying and is S5's
  staleness flag made precise.
- **Motif evaluates nothing.** No metric gates an apply in v1. Whoever runs the loop reads the
  numbers and decides.

The last point is deliberately in tension with [S8](../stage2-change-management.md#s8--two-mode-agent-loop),
which requires a "defined objective acceptance check" before an autonomous apply. That check is real
and still wanted, but it is **not built in v1** — until it is, autonomous apply falls back to human
review exactly as S8 already prescribes. When it is built, the argument that config-declared
predicates over declared metrics are *the operator's policy* rather than *Motif's judgment* is the
one to make; it does not need making yet.

## Consequences

- The "no HermitCrab projection code exists" gap reported against this repository is **not a gap**.
  There is no projection layer to build.
- `export` becomes a core CLI verb and a core capability, alongside the existing 12.
- The attachment/metric store extends the mutable per-package manifest (S1), never the immutable
  content-addressed object. Attachments and metrics can never move an intent digest.
- Configuration becomes a real surface: label taxonomy, render modes, metric names and types.
- Build stages must stop excluding "text reports as attachments"; it is `[core]`.

## What this does not change

PanGloss orchestration itself — building the FST, invoking the parser, scheduling corpus runs —
remains **out of scope for this repository**, exactly as build stages says. So does the PR-workflow
UI, Avalonia, LexBox sync, and the cloud substrate. The boundary moved by exactly one step: Motif
now owns *receiving and rendering* the loop's outputs, not producing them.

The cross-process protocol by which those outputs arrive — framing, error and exit-code contract,
one-shot versus daemon — is **[issue B13](../issues.md), still open** and deliberately not decided
here.
