# Stage-2 change management (vision)

A local-first change-management layer over the Motif runner: author, review, and apply change
packages, with a thin CLI now and an elaborate application later. This is the vision and the settled
decisions; the elaborate application is **out of scope for the current core build** and will likely be
a separate repo. Each decision below is tagged **[core]** (in this repo now) or **[stage-2]** (later,
separate).

## S1 — Store: files, not a database

The store is **git-style files**: finalized change sets are immutable content-addressed objects;
mutable working state (review status, label, comment, attachment pointers) is a small per-package
manifest. **No database in v1.** SQLite may appear *only later, as a disposable, rebuildable local
cache/index* (never the source of truth, never synced) once enumeration/search is measurably slow.
Dolt/DoltHub is considered only for the eventual cloud layer, never the local critical path.
**[core]** minimal store in the CLI; **[stage-2]** the elaborate store.

**Keying — content-addressed objects, id-keyed manifest pointer.** The object key is the
**`intentDigest`** (`objects/<intentDigest>.json`, write-once, never revisited); the manifest key is
the frozen **`changeSetId`** (`manifests/<changeSetId>.json`), holding a movable `currentIntentDigest`
— exactly a git ref pointing at a commit hash. `commit` writes a new object and either creates the
manifest or moves its pointer (amend). Prior versions are retained for audit. Keying objects by
`changeSetId` — as the Stage E skeleton originally did — cannot express amend without mutating a
supposedly immutable object; that was **issue A1**, now fixed (see [issues.md](issues.md)) — the store
already implements the `intentDigest`/`changeSetId` split described above.

## S2 — Two merges; only one is forbidden

The `.fwdata` **merges** on every LexBox Send/Receive (Chorus 3-way, element/GUID-level) — that is
where two operators' actual data reconciles, and it is desired. "Never merge" means only: **Motif
never merges change-set stores/histories against each other and never three-way-merges proposed
intent.** Chorus-induced baseline movement is ordinary [drift](conflicts-and-rebase.md), handled by
effect-comparison. There is no "merge change sets" operation anywhere. **[core]** semantics.

## S3 — Draft in memory; commit is the write

A **draft is in-memory only** — a builder the calling code drives (`new → add → label → commit`), the
Flexicon pattern, from Python or C#. `commit` serializes it to the immutable object; drafts never touch
disk and so can never clash or sync. `changeSetId` is **uniquely minted at creation, content-independent,
frozen** (ADR 0004); `intentDigest` tracks content. Amend keeps the id, moves the digest. **[core]**.

**Reopen for editing** loads a committed envelope's content into a *new* in-memory draft carrying the
same frozen `changeSetId` as data; re-committing produces a new `intentDigest` under that id and resets
review status to proposed (approval is effect-digest-scoped, so any content change invalidates it).

**The CLI's `drafts/<name>.json` file is a deliberate session shim, not "the draft."** Each CLI verb is
a separate OS process with no shared memory, so the draft must be persisted between invocations. A
library or daemon consumer keeps drafts purely in memory; nothing should copy the file as if it were
part of the store's data model.

## S4 — Review-gated local apply (not a server-branch PR)

There is no shared target branch — the shared thing is the `.fwdata`. A committed change set carries a
mutable manifest with `status` (proposed / approved / applied / rejected). **Review** is of the
assessment's effect delta; **approval is per-effect-digest and drift-invalidated** (re-review on drift;
host may override). **"Landing" = applying locally**, recorded in the applied-log. Reconciliation is
Chorus-on-fwdata plus idempotent apply by `changeSetId` — two operators can apply the same shared change
set independently and the log unions to one entry. Multi-user discussion is a later cloud surface.
**[core]** apply + review semantics; **[stage-2]** the PR UI/workflow.

## S5 — Attachments: derived, provenance-stamped, store-not-interpret

The CLI **exports `.fwdata`** (N change sets applied to a scratch copy) and **accepts** report blobs; the
external tool (PanGloss) is run by a thin orchestration script, never by Motif. Attachments are
**derived/regenerable views**, stored as content-addressed blobs with provenance `(changeSetId,
intentDigest, the whole-grammar state digest they ran against, tool + version, timestamp)`,
**staleness-flagged** when the state moves (never shown as current when stale), and **selectively
synced**. Motif **stores and lists by provenance; it never interprets** tool output (it can diff blobs,
but the meaning stays external). **[core]** export/accept/store; **[stage-2]** orchestration +
comparison UI.

Two amendments from [ADR 0011](adr/0011-experiment-loop-boundary-motif-is-the-record.md):

- **Export is `.fwdata`, not HC grammar XML.** This section originally said the CLI "produces HC grammar
  XML". PanGloss reads `.fwdata` directly and calls the XML path legacy, so XML survives only as the C#
  conformance oracle. Export applies change sets to a **scratch copy** and leaves the real project
  untouched — the only structurally safe way to experiment, given [C15](issues.md)'s unrepairable
  cache poisoning.
- **Labels and typed metrics.** Attachments carry a configuration-declared **label** (`"PanGloss
  Report"`, `"Changed Word Analysis from Corpus A"`), and a label may be declared Markdown for CLI
  pretty-printing. Configuration also declares typed **metrics** (`corpus-a-coverage: percent`,
  `regression-status: enum[pass,fail]`) which Motif stores, lists, and diffs across change sets.
  Both bind to the **`intentDigest`**, not `changeSetId` alone, so amending a change set marks prior
  reports stale rather than silently re-attributing them. Motif evaluates no metric and gates no
  apply in v1.

## S6 — HermitCrab round-trip

**Amended by [ADR 0011](adr/0011-experiment-loop-boundary-motif-is-the-record.md): forward projection is
deleted.** This section originally made forward projection (harvest `HCLoader`) the killer workflow's
producer, with reverse `Expand` built after it and validated against it. PanGloss reads `.fwdata`
directly — **PanGloss is the projection** — so there is no forward component to build.

Reverse **`Expand`** survives and is now the *primary* grammar authoring surface: structured HC-friendly
commands, no compiler, the inverse of `HCLoader`. Still **[core]**, still in `SIL.Motif.HermitCrab`.
Losing forward projection costs `Expand` its intended round-trip oracle, so it must instead be validated
against HC's own conformance suite — see [HC surface scope](hc-surface-scope.md), "The oracle" — whose
fixtures mechanically re-derive ground truth from `grammar.xml` rather than trusting hand-authored
metadata. See also [HermitCrab projection](hermitcrab-projection.md#authoring-input-and-round-trip).

## S7 — Repo & package boundary

**[core] this repo, all C#:** `Contract`, `Model`, `Runner`, `Host`, `Diff`, `HermitCrab` (optional
package), `Cli` (thin CLI + minimal files store), `Tests`. `Contract`/`Model`/`Runner` stay HC-free and
store-free; the store lives in the CLI/Host layer. One-way dependencies; nothing depends inward.
Cross-language consumers (Python: Linguistic Assistant/FlexTools; Rust: PanGloss) go through the CLI's
process/JSON protocol. **[stage-2]:** PR/review and grammar/corpus UI is implemented as native
FieldWorks-owned Avalonia modules on the active net48-to-net10 migration spine—not as web/React
components. Orchestration, LexBox/cloud sync, and any Dolt/DoltHub substrate may remain in a separate
modern companion/service repository.

As shipped today, only `SIL.Motif.Contract`, `.Model`, `.Runner`, `.Host`, `.Cli`, and `.Tests` exist
as projects; `Diff` and `HermitCrab` are boundary decisions for work not yet started — there is no
HermitCrab projection code of any kind in the tree.

## S8 — Two-mode agent loop

Agent-authored change sets are **untrusted input** (strict closed parsing, validation, resource/DoS
bounds — numbers still to pin). The **deterministic artifacts (effect delta, reports, regression /
golden-set checks) are the trust anchor in both modes**; the difference is only who reads them.

- **AI alone (autonomous):** the AI authors and gates on the objective outputs, then applies.
  **Autonomous apply requires a defined objective acceptance check** (regression / golden-set pass); with
  no such check it falls back to human review — otherwise "AI alone" degrades into "trust the AI."
- **AI supporting human (assisted):** iterative — human sets parameters → AI proposes → human reviews the
  deterministic preview → "try again with these changes" → AI re-authors.

Provenance splits **author** (the agent) from **applier** (human, or the agent for a deliberate
unattended apply), so the applied-log records who *wrote* and who *sanctioned* — making autonomous
changes fully auditable. **[core]** the untrusted-input + trust-anchor semantics; **[stage-2]** the loop
UX and orchestration.
