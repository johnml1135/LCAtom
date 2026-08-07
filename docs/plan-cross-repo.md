# Work in other repositories

*Rewritten 2026-08-01 for [Plan A](plan-motif.md). This file no longer defines milestones — Plan A
does. It lists work that lands outside `motif`, so none of it is our pull request, our review, or our
release train.*

Plan A's dependency surface is small: operations target LibLCM directly, so there is no intermediate
model and no third repository in the critical path. What remains is a few mostly independent asks.

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

**Read against the source 2026-08-06**, replacing two requirements an earlier draft of this section
speculated about ([ADR 0031](adr/0031-collaboration-follows-the-data-not-the-surface.md)). The cheap analysis
loop — add stems, reanalyse the corpus, report coverage and mis-categorisation, without recompiling — is what
makes an AI-centric workflow affordable. Where it actually stands:

- **Incremental stem addition exists and Motif should use it** — see
  [ADR 0032](adr/0032-stem-assessment-is-pangloss-supplied-lexicon.md). The supplied-lexicon overlay
  (`pg_lexicon::SuppliedLexiconRuntime`, on the FFI grammar handle) accepts a batch of stems against
  signatures already present in the official lexicon, with **no grammar reload and no foma recompilation** —
  the implementation plan deletes both deliberately. About thirteen JSON operations
  (`hc_lexicon_add_json`, `_update_json`, `_export_json`, `_import_json`, `_search_json`, `_catalog_json`, …)
  plus a classification matrix and an adaptive guide (`hc_classification_matrix_json`,
  `hc_classification_guide_*_json`) that works out which signature a word belongs to.
  *An earlier draft of this section said no such path existed; that was a grep for the verbs I expected on
  the type I expected, and the mechanism sits beside the grammar rather than in it.*
- **The trust condition is closed** — for a different reason than the earlier draft gave. The overlay does
  not mutate a loaded grammar (an explicit non-goal), and entries are only accepted against existing
  signatures, so there is no divergent fast path to make a coverage report optimistically wrong.
- **The refusal is the classifier.** A stem PanGloss will not accept as a supplied entry is a stem that needs
  grammar work — so lexical-versus-grammar classification is discovered by trying, not by inspection.
- **The assessment layer is already built.** `pg-assess` ships `assess`, `compare`, `golden-diff`,
  `investigate`, and exports `GrammarDelta`, `CaseDelta`, `DeltaCategory` and a versioned `DELTA_SCHEMA` — on
  the same value-not-reference analysis identity Motif uses, chosen for the same reason. This confirms
  [ADR 0028](adr/0028-feeding-reorders-require-a-grammar-delta.md)'s claim that a Grammar Delta needs no new
  machinery. **Motif consumes it.**

~~**Two measurements remain.**~~ **Both taken, 2026-08-06** —
[research note](research/2026-08-06-parser-timing-measured.md). Grammar load on a 1 MB grammar is
**~0.1 s including process startup**, so no incremental-add API is warranted on load-cost grounds, for
either loop. Corpus reanalysis is the opposite of what this section expected: **1 ms per word on Indonesian,
151 ms on Sena, 1,327 ms on Amharic** — a thousandfold spread set by the language's morphology, not by
anything we control. For Sena 3's 6,973 wordforms that is roughly **7.5 minutes** of wall time on 20 cores;
an Amharic-profile grammar would be **~29 minutes with about a sixth of words abandoned at a 5-second cap.**

~~So a grammar author can iterate against a full corpus on a Sena-profile language and cannot on an
Amharic-profile one; sampling is the answer there.~~ **Corrected within the hour by measuring the other
engine.** `--engine=foma` costs a one-off FST compile (0.13 s Indonesian, 4.9 s Amharic, 12.1 s Sena) and
then analyses **12–19× faster with no timeouts at all**: a full 6,973-wordform corpus is **~16 s** on a
Sena-profile grammar and **~29 s** on an Amharic-profile one, against 7.5 and 29 minutes. **The answer is
the engine, not a smaller corpus**, and the compile is the loop's floor rather than its ceiling since every
rule change invalidates the FST anyway.

~~**The open question is agreement, not speed.**~~ **Answered the same day: they agree exactly.**
`pangloss compare` over two `assess` runs on Sena's first 40 words reports `outcomeDigestsAgree: true` and 40
of 40 cases unchanged, with the pipeline name the only difference recorded. The speed-up costs nothing in
agreement on that sample — though agreement on a harder grammar, and specifically on the 7 Amharic words
HermitCrab abandoned, is still unestablished.

**The operational rule that fell out of it.** For 40 words HermitCrab beat the FST pipeline (2.3 s against
12.3 s) because the compile had nothing to amortise. Compile divided by per-word saving gives the crossover:
the FST engine wins above **~87 words on Sena** and above **~4 on Amharic**. So a corpus run uses the FST
engine and a single-word check while authoring uses the default one — and on a hard grammar the FST engine is
right for nearly everything.

**One confirmation worth noting:** `compare` emits schema `pangloss.grammar-delta`, so the Grammar Delta
[ADR 0028](adr/0028-feeding-reorders-require-a-grammar-delta.md) requires exists as a versioned artifact, not
merely as shared vocabulary.

**And a constraint on which mode Motif may use at all.** PanGloss has three analysis modes: HC only, FST
only, and FST pruned with HC. **FST only over-generates** — it can report a non-word as a word — so it can
never answer Motif's question, which is whether the grammar rules are being applied properly. Checking the
FST engine against itself for regressions is legitimate inside PanGloss; it is not a substitute here.
**Motif uses FST-pruned-by-HC** (`--engine=foma` / `--pipeline foma-confirm`, the same mode through two
commands), with a fallback to HC only for odd cases including a grammar that will not compile to an FST. The
figures above are the pruned mode. See the [timing note](research/2026-08-06-parser-timing-measured.md) for
the source citations and for the over-generation counters worth surfacing in a coverage report.

~~**One conformance question worth answering before trusting overlay coverage**, about compounds of a
supplied stem with a base stem.~~ **Withdrawn** — see
[ADR 0032](adr/0032-stem-assessment-is-pangloss-supplied-lexicon.md), "The compounding worry, withdrawn". The
caveat came from an unbuilt on-device research track rather than the API we call, and the built design states
that the overlay recognises compound forms because morphology is unapplied before trie lookup.

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
it is **not blocking** — and as of 2026-08-06 the route-around is built and the manifest's
`AssessPoisonsCache` column has retired, along with every type that consumed it. An upstream fix would
still be the right thing for other consumers; Motif no longer needs it.

**A second, better-evidenced upstream ask, found by measurement 2026-08-05:** a `kMemoryOnly` cache
re-synthesizes its writing systems from the bare language tag, losing collation rules, valid-character
sets, fonts and keyboards — 0 of 4 value-equal on Sena 3. `useMemoryWsManager` is hardwired true for that
backend (`BackendProvider.cs:263-265`), so no caller can opt out. **An in-memory backend that can carry the
source's writing systems would make a cheap in-memory scratch viable**; without it, ADR 0016 pays ~600 ms
per scratch instead of ~120 ms. Not blocking, well characterised, and cheap to describe upstream — see
[the findings](research/2026-08-05-createcachecopy-provenance-and-hazards.md).

**A third ask, and the most concrete of the three — found 2026-08-06 by a failing test, not by reading:**
**there is no publicly reachable synchronous save.** `IActionHandler.Commit()` and
`IUndoStackManager.Save()` both end at `XMLBackendProvider.PerformCommit`, which enqueues a `CommitWork`
item on a background `ConsumerThread` and returns; the `.fwdata` file lands later. The barrier that waits
for it, `CompleteAllCommits()`, is declared on the **`internal`** `IDataStorer`, so liblcm's own
`ProjectLockingService.UnlockCurrentProject` and `ProjectBackupService` can pair save-then-barrier and an
outside consumer cannot.

Any consumer that saves and then reads the file — a copy, a backup, a Send/Receive, a sync tool — is
racing, and the symptom is silent and misattributed: Motif's scratch copy read a file one operation stale
and the failure surfaced as *"footprint drift"*, accusing the drift check rather than the save. Motif's
workaround is to reach `CompleteAllCommits` by reflection through the public
`ILcmServiceLocator.DataSetup`, which returns the same backend-provider instance.

**The ask is small:** either make `CompleteAllCommits()` public, or add a save verb that does not return
until the bytes are on disk. Either one deletes Motif's reflection and closes a race for every other
consumer. This is the highest-value of the three: a one-line visibility change against a defect class that
is invisible until something reads the file.

## lexbox

**Scoped 2026-08-06 by [ADR 0031](adr/0031-collaboration-follows-the-data-not-the-surface.md): none of this
is for grammar work.** A grammar is ~220 hand-authored objects written almost entirely by one person, and
its rules are interdependent, so there is nothing to distribute and no merge that would be safe. And the naturally
parallel part — dictionary entries, spelling reports, text analyses — **already has a sync story that is not
ours**: the dictionary through FLEx Lite's CRDT layer, texts through Chorus. Motif should not offer a second
one. Review itself needs no server at all.

Proposals and Receipts are immutable, content-addressed documents with frozen identities. Sharing them
needs **an object store and an HTTP API — not a merge engine**. Lexbox already has organisations, projects,
users, and a permission service.

Sharing must be **optional per project**: a linguist working alone is never obliged to publish.

~~Review state — comments, approvals, decisions — is the mutable part, and is an ordinary server database
unless offline review becomes a requirement.~~ **Answered 2026-08-06 (ADR 0031), and the answer is that
none of it is server-side.** Review lives with the project, so it works offline because it never needed a
network; we build neither a second comment system nor a dependency on another team's roadmap (`D14`,
`D15`, `D16` all resolved). What remains for lexbox is one narrow, genuinely useful thing: somewhere to
*publish* an immutable grammar Proposal or Receipt so that a forum, an email, or another project can
reference it by digest. Not dictionary or text proposals — those sync elsewhere already.
