# Research index

- [PR-like collaboration architecture synthesis](2026-08-01-pr-like-collaboration-synthesis.md)

Research notes preserve evidence and independent reviews. They are not decisions; current ADRs,
`CONTEXT.md`, and the live plans remain authoritative.

**Dated 2026-08-01 or earlier, these reviews predate [Plan A](../plan-motif.md).** Where they name
`HAR-*` items, a Harmony materialization step, or a MiniLcm↔LibLCM crosswalk, those belong to the
superseded plan — see [harmony-adoption-report.md](../harmony-adoption-report.md). Their findings
about determinism, refusal, baselines, and Receipts carry over unchanged and are `MOT-9` and
[ADR 0016](../adr/0016-scratch-cache-copy-not-undo.md).

## 2026-08-01 cross-repository reviews

- [MiniLcm ↔ LibLCM terminology audit](2026-08-01-minilcm-liblcm-terminology-audit.md) — more than
  twenty concrete mappings, classification of why names/shapes diverge, compatibility costs, and a
  recommendation against a premature breaking rename.
- [Harmony selective-materialization review](2026-08-01-harmony-selective-materialization-review.md)
  — separation of history convergence from materialization, deterministic refusal, diagnostics,
  policy options, and ordered-grammar consequences.
- [Dry Run baseline and state-control review](2026-08-01-dry-run-state-control-review.md) — immutable
  agent workspaces, whole-project baseline tokens, short exclusive apply capabilities, Drift,
  Receipts, and crash recovery.

The plan consequences are preserved in
[Plan A](../plan-motif.md) (`MOT-9`) and [ADR 0016](../adr/0016-scratch-cache-copy-not-undo.md).
Unresolved choices are queued in [the Plan A grill](../grill-plan-a.md).
