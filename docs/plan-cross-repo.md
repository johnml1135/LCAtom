# Work in other repositories

*Rewritten 2026-08-01 for [Plan A](plan-motif.md). This file no longer defines milestones — Plan A
does. It lists work that lands outside `motif`, so none of it is our pull request, our review, or our
release train.*

Plan A's dependency surface shrank sharply when grammar stopped being routed through the CRDT. The
three-repo ladder (motif / harmony / lexbox) is gone; what remains is four small, mostly independent
asks.

**Scope, added 2026-08-05** ([ADR 0020](adr/0020-cli-first-fieldworks-planned-not-built.md)): scope 1 —
the LibLCM seams proved through the CLI with an AI agent as author — **needs nothing from any other
repository.** Every row below is scope 2 except PanGloss's, which arrives at M5. That is the point of
the CLI-first sequencing: no other team's review cycle is on the critical path.

| Repository | Item | For | Blocking? |
| --- | --- | --- | --- |
| **PanGloss** | `hc_grammar_load_snapshot` FFI entry calling `pg_grammar::compile_project` | `MOT-15` | Yes, for M5 step 2 (scope 1) |
| **PanGloss** | A release pipeline — per-RID `cargo build --release` and artifact publish | `MOT-15` | **Yes, and it does not exist at all** |
| **FieldWorks** | Host the `netstandard2.0` Runner in-process behind a semantic adapter | `MOT-12` | Scope 2 — and gated on the `F26a` seam spike |
| **FieldWorks** | A recordable command-layer seam for observed intent (`F26a`) | `MOT-12`, [ADR 0019](adr/0019-observed-intent-and-proposal-edit-mode.md) | Scope 2 — **unverified that it exists** |
| **FieldWorks** | Avalonia review/proposal/diff modules on the migration spine | `MOT-10` | Scope 2 |
| **lexbox** | Proposal and Receipt object store, optional per project | `MOT-14` | Scope 2 (`M4b`). Scope 1 needs receipts *durable*, not *shared* |
| **lexbox / FwHeadless** | The `E19` Chorus applied-log merge experiment | `E19` | **Deferred by owner decision** — Chorus is known-imperfect and not a near-term concern |
| **liblcm** | Make `Rollback` run the refresh hooks `Undo` runs, or expose a non-committing invalidation | `MOT-11` | No — ADR 0016 routes around it |
| **harmony** | *nothing* | — | — |

## FieldWorks

The `net48` seam is the only hard integration. `SIL.Motif.Runner` already multi-targets
`netstandard2.0;net10.0` and operates on a cache it does not own, so FieldWorks supplies the cache, the
UI thread, the applier identity, `Save`, and parser/UI invalidation. FieldWorks is SDK-style with
`PackageReference` and central package management, so both the managed package and the PanGloss native
package resolve normally.

The UI belongs in FieldWorks-owned Avalonia modules on the existing migration spine — not a parallel
React, Blazor, or WebView surface. Gates 0 and 1 in
[fieldworks-crdt-integration-research.md](fieldworks-crdt-integration-research.md) still define
acceptance.

## PanGloss

Two asks, and the second is larger than it sounds.

`hc_grammar_load` already accepts HC XML bytes in memory, which is enough for step 1 of `MOT-15` with
no PanGloss change at all. Step 2 needs one new entry point taking a `pangloss-project` snapshot
document, because `pg_grammar::compile_project` is not currently exposed over the FFI.

**The release pipeline is the real blocker.** `rust-ci.yml` runs `fmt`, `clippy`, `test`, and
`coverage` on `ubuntu-latest` only — no Windows or macOS runner, and **no release, publish, or
artifact-upload job of any kind**. There is no downloadable `pangloss` binary and no packaged
`pangloss_ffi` for any platform. That gap also blocks the smallest clean local install independently
of Motif, so it pays for itself twice.

## liblcm

One optional upstream improvement, worth raising while the Avalonia and `net10.0` migration already
has people in that codebase.

`UndoStack.Rollback` skips the forward-only setter hooks `Undo`/`Redo` run, so `LexEntry`
headword/homograph and `MoStemAllomorph` monomorphemic derived caches go stale after a rollback, and
no non-committing invalidation is reachable from a consumer — `ILexEntryRepository.ResetHomographs`,
the only candidate, is not safe to call there. Either fix closes the problem class permanently.

[ADR 0016](adr/0016-scratch-cache-copy-not-undo.md) routes around this by never reverting anything, so
it is **not blocking**. It is still the correct fix, and it would let the manifest's
`AssessPoisonsCache` column retire.

Also worth confirming rather than assuming: `LcmCache.CreateCacheCopy` and
`BackendProviderType.kMemoryOnly` are public and are what ADR 0016 depends on, but `CreateCacheCopy`
has **zero callers** anywhere in liblcm or FieldWorks. Plan A's `MOT-11` measures it before building
on it.

## lexbox

Proposals and Receipts are immutable, content-addressed documents with frozen identities. Sharing them
needs an object store and an HTTP API — **not a merge engine, and not a CRDT**. Lexbox already has
organisations, projects, users, and a permission service, and is already FwLite's sync authority.

Sharing must be **optional per project**: a linguist working alone is never obliged to publish.

Review state — comments, approvals, decisions — is the mutable part and is an ordinary server database
unless offline review becomes a requirement. Note that FwLite already ships comment threads in
`LcmCrdt/Changes/Comments/`; building a second review surface is a duplication to make deliberately
rather than by accident.

## harmony

**No asks.** `HAR-2`, `HAR-3`, `HAR-5`, `HAR-6`, and `HAR-7` were necessitated by routing grammar
through the CRDT and are withdrawn with that routing — see
[harmony-adoption-report.md](harmony-adoption-report.md) and [plan-lcmcrdt.md](plan-lcmcrdt.md). They
remain available as genuine FwLite improvements if a FwLite requirement ever asks for them.

Harmony continues to be the right substrate for FwLite, unchanged.
