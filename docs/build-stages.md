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
  entry count; a test opens a real project. (Phase 0.) **Done.**
- **B — Contract kernel.** Immutable change-set DTOs, strict closed JSON parsing, RFC 8785 canonical
  JSON, intent digest, canonical/GUID IDs — no LibLCM dependency. (Phase 1; ADR 0004, 0007.) **Done.**
- **C — Snapshot + effect capture.** Footprint-scoped before/after semantic-snapshot diff via the
  public `AllOwnedObjects`/`ReferringObjects`; assessment with expected effects. (Phases 2–3; ADR 0003,
  0006.) **Done** for the one shipped operation — snapshotting exists only for `LexSense`
  (`Snapshotting/LexSenseSnapshotter.cs`); the rest of Phase 2/3's per-type breadth is not yet built.
- **D — Apply slice.** One operation (`set` a sense gloss): one outer unit of work, read-back, receipt,
  and the applied-change log entry. (Phase 4; ADR 0005, 0006.) **Done**, and hardened since: `apply` now
  requires a bound `Assessment` and hard-stops on footprint drift (closes issue A2).
- **E — Thin CLI.** `open / new / add-set-gloss / label / comment / finalize / reopen / list / show /
  assess / apply / log` — 12 verbs, dispatched in `src/SIL.LCAtom.Cli/Program.cs` — over the files
  store, driving the real core end to end. **Done.**

## Status

**Stages A–E are all complete and verified.** "Verified" means `dotnet test` passes 82/82
(`Passed! - Failed: 0, Passed: 82, Skipped: 0, Total: 82`) against a real `LcmCache` opened on a real
copied FieldWorks project — not mocks — which is why the suite takes minutes rather than seconds.

The walking skeleton genuinely walks end to end (open → new draft → author → assess → apply → read
back → log), but it walks with exactly **one** operation on it: `lexical/sense/setGloss`. There are
zero create operations, zero delete operations, zero sequence operations, zero grammar operations, and
no HermitCrab projection code at all. "Done" above means *this thin slice* is done, not that the
catalog is complete — see [operation-catalog-plan.md](operation-catalog-plan.md) for what thickening
the skeleton requires next, and [implementation-plan.md](implementation-plan.md) for per-phase status
against the fuller plan.
