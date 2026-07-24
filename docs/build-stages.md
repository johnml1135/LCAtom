# Build stages

The core is built as a **walking skeleton** — the thinnest end-to-end slice first, then thicken.
Sonnet subagents do the implementation; each stage is reviewed, verified (build + tests run), and
committed before the next begins.

## In scope (this repo)

The LibLCM change-set runner (`Contract`, `Model`, `Runner`, `Host`) and a **thin CLI** that is both
the integration-test harness and the seed of a local, git-style, files-based change-package store.

## Explicitly out of scope (future, likely a separate repo)

The full change-management application: PR-style review workflow, HermitCrab-grammar build and PanGloss
orchestration, text reports as attachments, an Avalonia / FieldWorks-embedded UI, LexBox sync of change
sets, and any cloud collaboration substrate (e.g. Dolt/DoltHub). The local store is git-style files
(immutable content-addressed objects + mutable manifests); **no database in v1** — SQLite only ever as
a later, disposable, rebuildable cache, never the source of truth and never synced.

## Environment (verified)

- .NET SDKs 8/9/10 present; `SIL.LCModel 11.0.0-beta0150` in the local NuGet cache (no need to build
  LibLCM from source).
- Test projects available read-only: `FieldWorks/TestLangProj` (populated), LibLCM `NewLangProj`
  template. Always copy to scratch before opening; never mutate a shared project.

## Stages

- **A — Scaffold + headless load.** Solution and package shape; reference `SIL.LCModel`; adapt the
  headless project-load plumbing from `FwDataMiniLcmBridge` (MIT); `lcatom open` prints project name +
  entry count; a test opens a real project. (Phase 0.)
- **B — Contract kernel.** Immutable change-set DTOs, strict closed JSON parsing, RFC 8785 canonical
  JSON, intent digest, canonical/GUID IDs — no LibLCM dependency. (Phase 1; ADR 0004, 0007.)
- **C — Snapshot + effect capture.** Footprint-scoped before/after semantic-snapshot diff via the
  public `AllOwnedObjects`/`ReferringObjects`; assessment with expected effects. (Phases 2–3; ADR 0003,
  0006.)
- **D — Apply slice.** One operation (`set` a sense gloss): one outer unit of work, read-back, receipt,
  and the applied-change log entry. (Phase 4; ADR 0005, 0006.)
- **E — Thin CLI.** `new / add / label / comment / finalize / list / show / assess / apply / log` over
  the files store, driving the real core end to end.

Status: **A in progress.**
