# Plan A — Motif

*The live plan. Adopted 2026-08-01 from
[harmony-adoption-report.md](harmony-adoption-report.md) proposal 2. This file owns both the
milestones and the `MOT-*` items; nothing else defines milestones.*

## Delivery

**Motif delivers exactly two things: the `motif` CLI, and a FieldWorks integration.** Nothing else is
a Motif product.

| | |
| --- | --- |
| `motif` CLI | `net10.0` executable. Batch, automation, and AI-agent use, against a `.fwdata` project it opens itself |
| FieldWorks integration | `netstandard2.0` Runner hosted in-process, behind FieldWorks-owned Avalonia surfaces |

Everything else Plan A touches is a **dependency, not a deliverable**: the Lexbox receipt store
(`MOT-14`) is server work in someone else's repository, PanGloss is a subprocess or native library,
and `SIL.Motif.Contract` is a published contract that other runners consume — not an application we
ship. There is no Motif web app, no Motif service, no Motif mobile surface, and no Motif presence inside
any other product.

**Everything scoped gets built. The first slice is what the parser touches** —
[ADR 0025](adr/0025-parser-first-build-order.md): the 150 parser-read fields (113 grammar, 32 lexical, 5
other) plus the analysis fields that carry a human judgement. The 323 fields no parser reads — bibliographies,
pictures, pronunciations, publication settings — are slice 2. **Grammar is not deferred**; it is 113 of the
150 and it is where the value is.

**Analysis comes in; occurrence assignment does not** (ADR 0025 decision 3). A wordform, its analyses and who
approved them hang off GUID-bearing objects in unordered collections, so they are durable and addressable.
*Which word position in which sentence* uses an analysis is a sequence index into a `Segment` and breaks when
the text is edited — so "this analysis is human-approved" is available now (the test suite) and "every word has
an analysis" is not (the coverage metric, still a research track). Earlier framing in
[ADR 0017](adr/0017-text-and-analysis-destination-scope.md) should be read through ADR 0025: its reasoning
holds — coverage gaps are the feeding ground for new and refined rules, which makes them raw material rather
than a reporting metric — but it staged *all* of analysis out of v1, and the approval half turned out to be
durable. Tests before coverage, and the tests are available now.

**Re-scoping is the first real work this creates.** The manifest still marks `Segment`, `WfiAnalysis`,
`WfiWordform`, `WfiMorphBundle`, `Text`, `CmAgent` and `StTxtPara` as `out` / `not-domain-reachable` — 48 rows.
Flipping `Scope` on the analysis half is mechanical; deciding `Construct` for them and classifying
`WfiAnalysis.Stems`/`.Derivation`/`.CompoundRuleApps`/`.InflTemplateApps` (expected to be read-only parser
output) is judgement.

> **No longer time-sensitive.** An earlier note here said ADR 0017 decisions 3 and 4 had to be taken
> before the canonical JSON form froze. **Both were withdrawn on 2026-08-05.** Motif never addresses an
> occurrence — it addresses a `Segment`, which is a GUID-bearing `CmObject`, and edits
> `Segment.Analyses`, an ordinary `rel`/`seq` field. The index lives inside the value, not in the
> target, so the addressing model needs no change and the canonical form can freeze without text in
> view.

> **Bidirectional diff is settled, and it is not the drafting path.**
> [ADR 0019](adr/0019-observed-intent-and-proposal-edit-mode.md): FieldWorks **records** what a human
> did rather than Motif inferring it from a delta. Diff keeps project-to-project comparison, where it
> must refuse loudly on `merge`, `replace`, and index-as-identity `move`. The snapshot substrate is
> de-urgentized, not cancelled. `F24` (three provenance classes: observed, diffed, authored) is still
> open in [grill-plan-a.md](grill-plan-a.md).

## The shape of Plan A

Motif authors **Proposals** against **LibLCM objects**, dry-runs them on a scratch cache copy, applies
them through one LibLCM unit of work in whatever process owns the live cache, and records a Receipt.
One authority, one writer, one process — no second store and nothing to merge.

```
Proposal (JSON, public contract, intent digest)
   │  generated from MasterLCModel.xml ⋈ manifest
   ▼
DryRun on a scratch LcmCache copy  ──▶ expected effects + BoundDryRunAnchor
   │
   ▼
Apply on the live LcmCache, one UOW ──▶ Receipt + applied-log entry
   │
   ▼
Receipt synced to Lexbox (optional per project)
```

**One authority, one writer.** The process holding the loaded `LcmCache` is the only writer; Chorus moves
projects between people as it already does. There is no second merge engine and no replicated copy of the
data. Why an alternative design was considered and rejected is recorded once, in the
[adoption report](harmony-adoption-report.md) — it is history, not a plan, and nothing below depends on it.

## Two scopes

[ADR 0020](adr/0020-cli-first-fieldworks-planned-not-built.md), 2026-08-05. **Scope 1 establishes the
LibLCM seams and proves them through the CLI with an AI agent as the author. Scope 2 is the FieldWorks
integration, which is planned in full and not built yet.**

The acceptance question for scope 1: *can an AI agent, through the CLI alone, author a Proposal against
a real project, see its dry-run effects, and apply it — repeatedly, with drift refused?*

**And the CLI is the whole surface, not a test harness** ([ADR 0021](adr/0021-cli-is-the-full-surface-layer-1-churns.md)):
the same lists, diagnostics, reports, summaries, actions, and workflows FieldWorks will eventually render
for a human, rendered as text and JSON for an agent. It is faster to iterate on than a UI, which is the
point — the agent-facing Layer 1 is **expected to churn** while FieldWorks is not yet depending on it.
What may not churn is anything hashed: Layer 0 `kind` strings, payload schemas, the canonical JSON form,
and the digest algorithm. That boundary is ADR 0021 decision 3.

Scope 2 is planned now precisely so scope 1 does not make it more expensive. The obligations that must
hold throughout — `netstandard2.0` on Contract/Model/Runner, one JSON stack, the Runner never owning a
cache, apply never calling `Save`, the Layer 0/Layer 1 split, the applied log's unresolved Chorus
caveat — are listed as decision 3 of ADR 0020 and are build-time invariants, not intentions.

## Milestones

Ids are **not** renumbered; the order changed.

| | Gate | Items | Scope |
| --- | --- | --- | --- |
| **M1** | The generator reads and joins the model without a liblcm checkout | `MOT-2`, `MOT-3` | 1 |
| **M2** | One generated operation family applies end to end, driven from the CLI, and its effects are read back | `MOT-4`, `MOT-11`, `MOT-16`, `MOT-19` | 1 |
| **M4** | A Proposal is authored by an agent, reviewed, approved, applied, and its Receipt is durable | `MOT-9`, `MOT-10`, `MOT-17`, `MOT-18` | 1 |
| **M5** | One grammar construct authored, reviewed, applied, and parsed | `MOT-6`, `MOT-15` | 1 |
| **M6** | The remaining constructs | `MOT-7`, `MOT-8` | 1 |
| **M3** | FieldWorks hosts DryRun and Apply in-process, on `net48` | `MOT-12`, `MOT-13` | **2 — planned, not built** |
| **M4b** | Receipts shared between people | `MOT-14` | **2** |

Execution order: `M1 → M2 → M4 → M5 → M6`, then M3 and M4b.

M1 and M2 are mechanical. **M4 is the product**, and under scope 1 it is AI-facing first — the agent is
the first author, not the last. M5 is the first thing a linguist would recognise as the point.

## Status summary

| Item | M | Size | Status |
| --- | --- | --- | --- |
| `MOT-2` — the `(Class, Field)` join, failing the build on any unmatched key | M1 | Small | Not started |
| `MOT-3` — generator skeleton: read `MasterLCModel.xml`, emit nothing yet | M1 | Medium | Not started |
| `MOT-4` — emit the operation catalog for one family | M2 | Medium | Not started — family decided (`B5`): the lexical entry. Includes renaming the one shipped kind to `lexical/lexSense/setGloss` and regenerating its conformance vectors (ADR 0023) |
| `MOT-11` — scratch-cache DryRun, replacing mutate-then-rollback | M2 | Medium | Not started — **ADR 0016**, gated on the `A1` spike |
| `MOT-16` — long-lived CLI session over a warm cache | M2 | Small–medium | Not started |
| `MOT-19` — the CLI as the full product surface, text and JSON | M2/M4 | Large, and grows with every other item | Not started — **ADR 0021** |
| `MOT-9` — Baseline Token, Dry Run binding, apply authorization, Receipt | M4 | Medium, correctness-critical | **Partly built** |
| `MOT-10` — Proposal revisions, Check Runs, Reviews, Decisions | M4 | Medium, the PR-like product core | Not started |
| `MOT-17` — Layer-1 semantic and batch authoring for agents | M4 | Medium, and **expected to churn** | Not started |
| `MOT-18` — selective Proposal editing: duplicate, remove, split | M4 | Small, and required by the agent loop | Not started — `J43` decided |
| `MOT-6` — semantic + lowering layer for grammar construct 1 | M5 | Medium — **the first product family** | Not started |
| `MOT-15` — PanGloss snapshot producer and FFI | M5 | Medium | Not started |
| `MOT-7` — the remaining 29 constructs | M6 | Large | Not started |
| `MOT-8` — ordered-grammar review proof | M6 | Medium | Not started |
| `MOT-12` — FieldWorks in-process adapter | M3 | Medium | **Scope 2** — gated on the `F26a` spike |
| `MOT-13` — `System.Text.Json` on `net48` proof | M3 | Small | **Scope 2** — research says clean (`A4`); the proof itself remains |
| `MOT-14` — Receipt store and sync in Lexbox | M4b | Medium | **Scope 2** |

**Withdrawn:** `MOT-1` and `MOT-5`. Both existed only to serve a merge layer that is not on this path;
operations target LibLCM directly, so neither a type crosswalk nor a mapping onto merge primitives has
anything to do. Numbers are not reused.

## The one spike on the critical path

**`A1` — what does `LcmCache.CreateCacheCopy` cost?** In this repository: a harness calling liblcm's
public API, timing one copy from a hot cache and one from a pristine scratch. `CreateCacheCopy` has
zero callers in liblcm or FieldWorks, and ADR 0016's whole value is the ratio between those two
numbers. Nothing in `MOT-11` should be designed before it exists. Half a day, plus `A3`'s ~15-line
`IProjectIdentifier` implementation reporting `kMemoryOnly`.

The other two spikes — `E19` (Chorus merges the applied log) and `F26a` (does a usable seam exist in
FieldWorks' command layer?) — are **both scope 2 and both outside this repository**. `E19` needs
`FwHeadless` plus Chorus packages that are not in the local NuGet cache; `F26a` needs a FieldWorks
checkout. Neither blocks scope 1: a single-machine CLI never triggers a Chorus merge. **That silence is
not evidence** — `E19` stays open.

**What already exists and is not re-planned.** `manifest/liblcm-inventory.tsv` — 898 rows, 19 columns,
473 in-scope rows across 95 in-scope classes, 100% classified for every in-scope row. The
HCLoader-derived grammar map and the coverage research are done. `SIL.Motif.{Contract,Model,Runner}`
build, `Runner` multi-targets `netstandard2.0;net10.0`, and 82/82 tests pass — including a working
`open` / `new` / `add-set-gloss` / `finalize` / `dry-run` / `apply` / `log` CLI loop for one operation
kind.

---

## `MOT-2` — the join, failing the build — M1

Structure comes from `MasterLCModel.xml` so it tracks LibLCM upgrades. They join on `(Class, Field)`, and
**a key present in one and absent from the other fails the build.**

**What the manifest is actually an authority on** ([ADR 0022](adr/0022-structure-is-derived-policy-is-five-rows.md)):
`Scope` and `Construct`, and nothing else. `Verbs` is a **pure function** of `Kind`/`Card` — seven
combinations, zero exceptions across all 412 authorable rows — and `ComparisonClass` is `seq` → `positional`,
everything else → `unordered`, with **exactly five exceptions** where order carries linguistic meaning
(`LexEntry.AlternateForms`, `PhPhonData.PhonRules`, and the three `PhSegRuleRHS` alpha-variable fields).

So this item gains a second check: **derive both columns, compare against the manifest, and fail the build
naming any row that departs from the derivation without appearing in the five-row exception table.** That
replaces `B7a`'s proposed hand-audit of 61 rows — deriving fixes every row, including rows LibLCM has not
shipped yet.

The key set has been checked, not assumed: 445 `<basic>` + 235 `<owning>` + 218 `<rel>` = **898**
field declarations in `MasterLCModel.xml` (424,797 bytes, 5,368 lines, model version `7000072`, 193
classes), against 898 manifest rows, with **zero keys present in one and absent from the other and no
duplicates in either**. A matching count alone would not have shown that.

**Acceptance**

- An injected extra `(Class, Field)` key on either side fails the build with a message naming the key.
- A LibLCM upgrade that adds a field produces a row whose verbs and comparison behaviour are **derived**,
  and the build stays red only until a human decides its `Scope` and `Construct` — the two things nobody
  can compute. **For a system where a wrong decision degrades a language project quietly, visible churn
  beats minimal churn** — that is the intent, not a side effect.
- An injected row whose `Verbs` or `ComparisonClass` disagrees with the derivation, and which is not one of
  the five cited exceptions, fails the build naming the row.
- The kind name is `lowerFirst(DeclaringClass)` and is checked the same way
  ([ADR 0023](adr/0023-derived-kind-names-required-descriptions.md)); a `create` or `delete` naming an
  `abstract` class fails the build, since no such object can exist.
- The kind's **group** is derived from the declaring class's prefix family
  ([ADR 0024](adr/0024-group-is-derived-domain-is-editorial.md)), and a class whose prefix is not in the
  closed table fails the build. The hand-authored **`domain`** column is *not* checked against it — the two
  answer different questions and disagree on 53 rows by design.
- A kind with no description fails the build.
- The five exceptions are asserted explicitly, so silently losing one is a test failure rather than a
  quieter grammar.
- `MasterLCModel.xml` is obtained without a liblcm source checkout. `SIL.LCModel.csproj:125` packs
  `MasterLCModel.*` into the NuGet package under `contentFiles/`, but not in the conventional
  `contentFiles/{lang}/{tfm}/` layout, so it may not flow into a `PackageReference` consumer
  automatically. Reading it from the package or the global package cache is the fallback, and which
  path is used must be recorded rather than left to whoever runs the build.

## `MOT-3` — generator skeleton — M1

Read the joined model, emit nothing. Separating "can we read and join this" from "is the emitted code
right" keeps M2's gate about the output.

This is ordinary rather than novel: **LibLCM already generates the majority of itself from this
file** — 33 NVelocity templates in `LcmGenerate/*.vm.cs`, `<Compile Remove>`'d at
`SIL.LCModel.csproj:12`, driven by an MSBuild task whose `GenerateModel` target declares
`Inputs="MasterLCModel.xml"`. Output: ~154,000 generated lines against ~149,000 hand-written lines in
the same project.

**Also here: harvest FieldWorks' own label vocabulary** to seed descriptions
([ADR 0023](adr/0023-derived-kind-names-required-descriptions.md) decision 4) — `strings-en.xml`, the
`.fwlayout` slice labels, and the tool config keyed by `(ownerClass, ownerField)`. Coverage is roughly a
third to under half of in-scope rows and labels are per-view rather than canonical, so where a field has
several labels the harvest **records them all for a human to choose from** rather than picking silently.

**Acceptance:** the generator loads all 898 joined rows, reports its own coverage, and runs in CI
without a liblcm source tree.

## `MOT-4` — emit the operation catalog for one family — M2

The output side of the gate. **Operations target LibLCM objects directly** — there is no intermediate
model to translate through.

**The family is the lexical entry** (`B5`, decided 2026-08-05): start where an agent's work starts. Concretely
the `lexEntry` construct's 10 authorable rows, **plus** `LexEntry.LexemeForm` and the `MoForm` rows that bring
an entry into existence — because `lexEntry` alone contains **zero `create|delete`** and a generator that can
edit entries but not create one does not test the gate. `LexEntry.AlternateForms` is excluded: it is a
`feeding` row and belongs to `MOT-8`. Cache-poisoning flags are not a selection criterion any more, since a
Dry Run runs on a throwaway scratch and Apply never rolls back.

Emit, per in-scope field of that family:

- the enumerated `kind` string, `{group}/{construct}/{verb}{Noun}`, one per field — never a runtime
  field-name parameter, with `{construct}` **derived** as `lowerFirst(DeclaringClass)`
  ([ADR 0023](adr/0023-derived-kind-names-required-descriptions.md));
- a **required description** for that kind, seeded from FieldWorks' harvested labels where one exists and
  hand-written otherwise. Never hashed, so it can improve forever;
- the closed payload schema for that kind;
- the LibLCM lowering;
- the read-back snapshotter that produces the effect for that field;
- registration into `OperationKindRegistry`.

**Not emitted, because the manifest cannot know it:** entity-construction validity for `create`,
HCLoader validation rules, enum members (they live outside `MasterLCModel.xml`; only a type-name
override file exists), and custom fields (a pure runtime concept, `AddCustomField`, absent from the
model).

**Acceptance:** every generated kind for the family round-trips author → DryRun → Apply → Receipt
against a real project, with effects read back rather than replayed. The one hand-written kind
(`lexical/sense/setGloss`) is regenerated and its existing tests pass **unmodified** — correctness is
established by regenerating code that already passes, not by the design being elegant.

**What passing does not license.** The slice is lexical and almost entirely `unordered`: it exercises
`set|clear`, `addRef|removeRef`, and `create|delete`, and touches **no** `feeding` or `index-as-identity` row.
It licenses the mechanical majority and says nothing about ordered grammar — which is `MOT-8`, deliberately.
Only 4 of the `lexSense` rows and none of the `lexEntry` rows are HermitCrab-reachable, so it also says
nothing about whether a generated kind can change a parse; that is `MOT-6` and `MOT-15`.

## `MOT-11` — scratch-cache DryRun — M2

Implement [ADR 0016](adr/0016-scratch-cache-copy-not-undo.md) **as amended 2026-08-05: one canonical path —
copy the project's files and open the copy** (~50 ms + ~550 ms at Sena-3 scale). A live-cache footprint probe
gates rebuilding it. Apply stays on the live cache.

**`CreateCacheCopy` and the in-memory fan-out are withdrawn.** They were ~5× faster and measurably lossy:
every `kMemoryOnly` cache re-synthesizes its writing systems from the bare language tag, so Sena 3's
vernacular lost its collation rules and all four writing systems lost their valid-character sets — including
when the copy was taken from a file-loaded scratch whose writing systems were intact. One path, no
per-operation judgement call about whether collation matters.

**Deliverables**

1. The scratch lifecycle, including a prerequisite-DAG mode that applies a topologically-sorted
   closure of un-applied Proposals to one derived scratch.
2. ~~Two measurements before the design is built on.~~ **Done, 2026-08-05** — `A1` is measured against real
   Sena 3 (152,222 objects). Harness: `spikes/SIL.Motif.Spikes.ScratchCache`; equivalence assertions live in
   `tests/SIL.Motif.Tests/Runner/ScratchCacheEquivalenceTests.cs`. Results and the resulting amendment are
   in [ADR 0016](adr/0016-scratch-cache-copy-not-undo.md) and the
   [research note](research/2026-08-05-createcachecopy-provenance-and-hazards.md#10-measured--the-spike-was-built-and-run).
   **The fan-out holds (140 ms vs 4,445 ms, 31.8×), but build the pristine scratch from the XML path by
   default** — a memory-only copy loses every writing system's collation and valid-character settings, and
   the file path is cheaper once more than ~9% of the live cache is fluffed.
3. Retirement of `CacheReusability`, `RollbackCacheInvalidator`, and
   `DerivedCachePoisoningOperationKinds` once the scratch path is the only dry-run path.
4. A guard so a dry run that depends on collation, valid characters, or sort order cannot silently run on a
   memory-only scratch. Inert for `setGloss`; live from the first collation-sensitive operation.

**Acceptance:** a dry run never mutates the live cache; a poisoned scratch costs a rebuild, not a
session; the DAG closure produces the same effects as applying the closure serially.

## `MOT-16` — long-lived CLI session over a warm cache — M2

Today the CLI is process-per-command: `Commands.DryRun` and `Commands.Apply` each call
`loader.LoadCache(fullFwDataPath)` and dispose, so a dry run followed by an apply pays two full project
loads (`E21`). That was acceptable when the cost did not matter. Under [ADR 0016](adr/0016-scratch-cache-copy-not-undo.md)
it does: the pristine scratch is worth having only if something outlives one command.

An agent loop makes this sharper than a human CLI ever would — author, dry-run, inspect effects, revise,
dry-run again is many round trips against one project. It is also the same shape FieldWorks has for free,
since FieldWorks already holds an open cache for the length of a session: **`MOT-16` is the CLI catching
up to FieldWorks' natural lifecycle, not diverging from it.**

**Deliverables.** A session holding one live cache and one pristine scratch; commands that operate against
the session rather than a path; footprint-gated scratch re-copy; explicit teardown that never leaves a
lock held. The Runner is unchanged — it already takes a cache it does not own, which is the same property
that lets FieldWorks host it in scope 2.

**Acceptance:** N dry runs across one session cost one project load, and the second dry run's effects are
identical to the first's when nothing changed.

## `MOT-19` — the CLI is the full product surface, in text and JSON — M2/M4

[ADR 0021](adr/0021-cli-is-the-full-surface-layer-1-churns.md) decision 1. Not an authoring tool with a
dry run attached: **everything FieldWorks will eventually show a human, the CLI shows an agent** — entity
and proposal lists, diagnostics, reports, summaries, actions, workflows. Faster to iterate on than a UI,
and the agent is a demanding first user.

**The structural requirement that makes it transferable.** Every list, report, and summary is computed in
a query/projection layer and *rendered* by the CLI — never computed inside a command handler:

```
      query / projection layer
        │                    │
   CLI renderer         Avalonia view models  (scope 2)
   (text + JSON)
```

The same rule the FieldWorks side already states from its own direction — keep UI projects LCModel-free,
put projectors in the integration layer. A report whose logic lives in a CLI handler is a report
FieldWorks must rebuild, and it will not be rebuilt identically.

**Definition of done includes JSON.** Structured emission is part of each report, not a later `--json`
flag. A summary that can be printed but not emitted is the tell that the projection layer was skipped.

**Log the surface's own usage** (ADR 0021 decision 4). The churn is not just faster iteration — it is
**evidence for which FieldWorks screens are worth building.** Which reports the agent calls, how often, and
which ones run back-to-back is the closest thing to a requirements document scope 2 will get, and it is
free if captured and unrecoverable if not. A session-local log of command, argument *shape*, and call
counts is enough; it carries no project data.

**Most of the read surface is deliberately ephemeral.** An agent asking *"what is true now"* stores
nothing and replays nothing, so those queries carry **no compatibility obligation at all** and may churn
permanently (ADR 0021 decision 3). The exception is sharp: **the moment a query's output is cited as
evidence on a Proposal it becomes a Check Run** and inherits `MOT-10`'s exact-input and stale-binding
rules. Both directions of that boundary are expensive to get wrong.

**Acceptance:** every surface an agent uses is available as structured data; the text form is
reproducible from that data alone; nothing a reviewer would need is computable only by parsing prose.

## `MOT-17` — Layer-1 semantic and batch authoring for agents — M4

[ADR 0009](adr/0009-layered-api-primitives-and-composers.md)'s Layer 1, which scope 1 makes urgent
because the agent is the first author. Layer 0 primitives are the diff's *output* vocabulary; Layer 1 is
the agent's *input* vocabulary (`J41`), and building Layer 1 first is exactly when that split is
easiest to blur.

Batch reads and batch updates, multi-rule creation, and composers that resolve a query into concrete
operations. **The at-rest form is the resolved operations, with the originating query carried as
non-hashed provenance** — verbatim ADR 0009 §1, and the reason is that a reviewer cannot approve effects
for an unresolved query. `J42a` proposes that re-applying a resolved batch against a moved baseline go
through the existing pre-flight drift path rather than silently re-resolving.

**This item is expected to churn, and that is the plan** ([ADR 0021](adr/0021-cli-is-the-full-surface-layer-1-churns.md)
decision 3). Merging two batch operations, adding a verb because it turned out to be useful, collapsing
four calls into one report — that is the refinement loop working, and it is far cheaper now than after
FieldWorks depends on it. **The boundary is mechanical: if a change alters the bytes that get hashed, it
is not Layer 1 churn.** Composers are free; a new `kind` is additive and minor-safe; renaming an existing
`kind` or touching the canonical form is not churn at all.

The accepted cost is that stored Proposals are not guaranteed portable across the churn window —
tolerable while the author is an agent that can re-author on demand, and it must end before a human's
approval is recorded against a stored Proposal (`B9b`).

**Acceptance:** an agent authors a Proposal it did not enumerate operation by operation; the stored form
replays to byte-identical operations; the query round-trips as provenance without entering any digest.

## `MOT-18` — selective Proposal editing: duplicate, remove, split — M4

Required by the agent loop, and **a core FieldWorks review workflow in scope 2** — the same mechanism
serves *"don't add that lexeme"* and *"only rules 1 and 4, not 5."* Removal is per-operation and
individually addressable; the unit of splitting is the individual operation, subject to `requires`
(`J44`).

**The dependency rule, decided** — [ADR 0021](adr/0021-cli-is-the-full-surface-layer-1-churns.md)
decision 6, answering `J43`:

1. A removal with no dependents just happens.
2. A removal that severs a `requires` edge or orphans a dependent **warns and names every consequence**,
   then requires an explicit force.
3. **Force never means "guess."** It means the caller accepted an enumerated consequence set — if the
   consequences cannot be enumerated, the removal is refused rather than forced.
4. `proposalId` stays frozen and the intent digest moves; removal produces a new revision, never a
   mutation of an approved one.

Consequence enumeration is not free for `delete`: deleting an owner cascades, and de-referencing does
not — LibLCM leaves an orphan. That is discovered-footprint territory, so a removal whose consequence set
depends on discovered reach must recompute it rather than reason from the declared footprint.

Portability is mostly already there: `ProposalStore` is content-addressed objects plus manifests, so
duplicate, zip, and transport are packaging.

**Acceptance:** an operation can be removed from a finalized-then-reopened Proposal; a removal that would
orphan a dependent enumerates the consequences and refuses without force; a removal whose consequences
cannot be enumerated refuses even with force.

## `MOT-9` — reviewed world equals applied world — M4

**Partly built.** `BoundDryRunAnchor`, `FootprintProbe.ComputeCurrentFootprintDigest`, the
drift-refusal precondition, `Receipt`, and `ProjectAppliedLog` all exist and are tested for one
operation kind. What remains is the portable Baseline Token, one-use apply authorization, and the
reconciliation state machine across UOW / save / receipt boundaries.

The authored Proposal remains the only semantic input; the LibLCM Mutation Plan is output-only.

**Acceptance:** two agents begin at one Baseline Token; one Proposal applies in authored order and the
other refuses drift before mutation. Injected failures at every UOW/save/receipt boundary produce
rollback or `NeedsReconciliation`, never blind retry.

## `MOT-10` — Proposal review domain — M4

Immutable Proposal revisions, typed Check Runs, human and AI Reviews, versioned policy Decisions,
semantic owner routing, stale-binding rules.

**Acceptance**

- any change to Proposal, baseline, relevant artifact, tool contract, interpretation version, or
  policy revision invalidates the former Check Runs and Decision;
- static-analysis Check Runs are first-class immutable facts with the same exact-input and stale
  binding as Dry Run, conformance, and policy checks;
- AI actors are labeled, may recommend or abstain, and cannot satisfy a human or native-speaker role
  by implication; permitted AI roles are declared per operation family, and any autonomous approval
  policy is versioned, independently checked, provenance-bound, least-privileged, expiring, audited;
- granular operation and effect comments coexist with Proposal-level atomic apply;
- payload and provenance digests bind the approved candidate through its Receipt;
- generated-output provenance binds the LibLCM model, manifest, generator, dependency lock, build
  environment, and output digests.

## `MOT-6` — semantic + lowering layer, construct 1 — M5

**The actual design work.** Everything above is mechanical; this is not. One grammar construct: the
named, reusable unit of intent (the sense in which the product is called *Motif*) and its lowering
into the generated operations `MOT-4` emits.

Two inherited constraints:

- **Preconditions live in the Proposal, never in the operation.** Baseline evidence is an observation
  carried by the envelope, evaluated at review and apply time and surfaced as drift. What crosses into
  the applied record is unconditional.
- Construct naming is **not mechanical** (issue B19), and 17 manifest rows are multi-construct (B20).
  Both block this item and neither is resolved by the generator.

**Acceptance:** one construct authored, dry-run, reviewed, applied, saved, and round-tripped through
Chorus Send/Receive without the applied log being corrupted — see
[the standing Chorus risk](harmony-adoption-report.md#standing-risk--chorus-does-not-merge-the-applied-log).

## `MOT-15` — PanGloss snapshot producer and FFI — M5

Two halves already exist and have never been connected. `HCLoader.Load(cache, logger)` takes a live
cache and returns a HermitCrab `Language`; `pg-ffi` is a `cdylib` annotated "for P/Invoke from net48"
whose `hc_grammar_load` accepts HC XML bytes in memory.

**Do both, in this order.**

1. **HC XML now.** `HCLoader.Load` → `XmlLanguageWriter` → `hc_grammar_load` → `hc_parse_batch`. No
   `.fwdata`, no copy, no save, no lock. Zero new code on either side beyond a possible
   stream overload. This is what makes reparse-after-apply feel real in the UI.
2. **pg-snapshot next.** HC XML uses session-scoped `Hvo` integers that "drift across FieldWorks
   sessions and are therefore unusable as a durable interchange key", where the snapshot format uses
   FieldWorks GUIDs. Motif's effects are keyed by canonical ID, so an `Hvo`-keyed parser result cannot
   be correlated with a Proposal's effect set. Write the producer in **C#** — Rust cannot read a
   managed `LcmCache`; `pg-fwdata` is a file reader — and add one FFI entry,
   `hc_grammar_load_snapshot`, calling `pg_grammar::compile_project`.

**Acceptance:** a byte-equality conformance test proving the C# producer and `pg-fwdata` emit
identical snapshots for the same project. The format is deterministic by construction, so this is a
legitimate assertion, and it is the only thing preventing two producers diverging into grammars that
parse differently. Build it with the producer, not after it.

**Blocker outside this repo:** PanGloss has no release pipeline — CI is `ubuntu-latest` only, no
artifact upload, no publish job. See [plan-cross-repo.md](plan-cross-repo.md).

## `MOT-7` — the remaining 29 constructs — M6

30 constructs, 75 reference fields, 38 classes. The point of the generator is that this is **30
reviewed diffs rather than 30 hand-built constructs.**

Sequencing is **[ADR 0025](adr/0025-parser-first-build-order.md)**, which replaced ADR 0012's
L0 → G0–G2 → backfill order. Of 150 parser-read in-scope fields, **113 are grammar and only 32 lexical**, so
grammar leads — not as a later stage but as the bulk of slice 1. ADR 0012's "non-grammar first" boundary was
unachievable: 13 of its 37 fields are never read by `HCLoader`, and populating the surviving 24 requires
creating objects of five grammar classes anyway.

**Known blockers that are not generator work:** the 48 text and analysis rows need `Scope` flipped and
`Construct` decided, and `WfiAnalysis.Stems`/`.Derivation`/`.CompoundRuleApps`/`.InflTemplateApps` must be
classified — several are expected to be read-only, being parser output rather than human intent. The old
classification worry (B17, B18) is retired: nothing the generator trusts is hand-authored any more
([ADR 0022](adr/0022-structure-is-derived-policy-is-five-rows.md)).

## `MOT-8` — ordered-grammar review proof — M6

**Re-scoped by Plan A, and smaller than it was.** The old item asked whether two people concurrently
reordering phonological rules converge. Under single-writer LibLCM with Chorus between people, that is
Chorus's question, not ours.

What survives is a review problem, and it is still the highest-risk item here:

- Two `feeding` fields — phonological rule order, where order encodes feeding and bleeding. A reorder
  is a small diff with a large semantic consequence. **Does a reviewer see that?** Effects are keyed
  by identity and carry explicit moves rather than positional rewrites, which is necessary but may not
  be sufficient.
- Three `index-as-identity` fields — alpha variables, where position *is* the identifier. Assigned by
  first-appearance traversal with a hard **24-per-rule ceiling that throws and kills the whole grammar
  load**. A pre-apply check must simulate the exact traversal rather than counting distinct
  constraints.

**Acceptance:** a reorder of real phonological rules from a real project produces a review a linguist
can judge, and an alpha-variable edit that would exceed 24 is refused before apply, not discovered at
parse time.

---

# Scope 2 — planned, not built

[ADR 0020](adr/0020-cli-first-fieldworks-planned-not-built.md). Everything below is fully planned and
deliberately unbuilt until scope 1 is shown to work. The detail is kept because the point of planning
it now is that scope 1 must not make it more expensive — see ADR 0020 decision 3 for the invariants
scope 1 owes it.

## `MOT-12` — FieldWorks in-process adapter — M3 *(scope 2)*

**Gated on the `F26a` spike, which is deferred.** [ADR 0019](adr/0019-observed-intent-and-proposal-edit-mode.md)'s
authoring path rests on a seam in FieldWorks' command layer whose existence and stability are
unverified. Nothing here should be built before that spike runs.

The `net48` seam. Marshal to the UI thread, pass FieldWorks' own `LcmCache`, supply the applier
identity, call `Save`, invalidate the parser and UI. The Runner is already `netstandard2.0` and takes
a cache it does not own, so no Runner API changes.

**Acceptance** is Gate 1 from [fieldworks-crdt-integration-research.md](fieldworks-crdt-integration-research.md):
one lexical operation previewed and applied through a FieldWorks-owned surface, on the UI thread, as
one undoable UOW, rejecting stale or wrong-type targets, replayable idempotently, surviving
save/reload, and reconciling a crash before and after save.

## `MOT-13` — `System.Text.Json` on `net48` — M3 *(scope 2)*

**Research says this is clean** (`A4`): FieldWorks already resolves `System.Text.Json 9.0.14` in its
`net48` graph, above Motif's 8.0.5 floor, arriving transitively through
`Microsoft.Extensions.DependencyModel` which `Directory.Packages.props:44` pins for an unrelated ICU
reason, propagated by `CentralPackageTransitivePinningEnabled`. Every floor in Motif's `net462`
dependency group is met or exceeded, and `AutoGenerateBindingRedirects` is on repo-wide. No new pins
are expected. Only the proof remains.

**Do not resolve any residual friction by using Newtonsoft on `net48`.** RFC 8785 canonical bytes must
be identical across runtimes or every intent and effect digest diverges between FieldWorks and the CLI
— that is [ADR 0007](adr/0007-cross-language-digest-determinism.md)'s entire subject. Same JSON stack
everywhere. This is one of ADR 0020's scope-1 invariants, not a scope-2 decision.

**Acceptance:** a `net48` host loads the Contract and computes an intent digest byte-identical to the
`net10.0` CLI's, for a fixture Proposal.

## `MOT-14` — Receipt store and sync — M4b *(scope 2)*

**Scope 1 needs receipts to be *durable*, not *shared*.** One machine, one author. Local durability is
scope 1; this item is the second-person half.

The applied log is thin by design — `(proposalId, formatVersion, timestamp, user, intentDigest,
description)` — it records *that* something applied, not what it did. The effects live in the
`Receipt`, which today is returned and never durably stored.

**Lexbox is the home.** It already has organisations, projects, users, and a permission service.
Proposals and Receipts are immutable, content-addressed documents with frozen identities, so they need
an object store and an HTTP API rather than a merge engine. Sharing is
**optional per project**; a linguist working alone is never obliged to publish.

Review state — comments, approvals, decisions — is mutable, and is an ordinary server database unless
offline review becomes a requirement.

**Acceptance:** a Proposal authored on one machine is visible, with its Receipt and effect digest, to
a permitted collaborator on another; an unshared project never leaves the machine.

## The two deferred spikes

Both are outside this repository and both are deferred by owner decision, 2026-08-05.

**`E19` — Chorus merges the applied log and does not understand it.** Deferred deliberately: *"we don't
care about Chorus right now. It's not great, and we know it will fail in some ways."* The distinct-GUID
case (every reviewer's independent apply) is safe regardless; the same-`proposalId` collision is the
open one, and if the guid-keyed merge registration is missing it produces duplicate-GUID `.fwdata`.
Phase 0 item 8's FLExBridge re-confirmation **was never done** and the caveat stands in
[ADR 0003](adr/0003-feasibility-findings.md) and [implementation-plan.md](implementation-plan.md).
A single-machine CLI never triggers a Chorus merge — **do not read that silence as evidence.**

**`F26a` — does a usable seam exist in FieldWorks' command layer?** Deferred with scope 2. ADR 0019's
entire authoring path depends on it, so it runs before any `MOT-12` code, not after.

---

## Cross-links

- Why the rejected alternative was rejected (history, not a plan): [harmony-adoption-report.md](harmony-adoption-report.md)
- Work in other repositories: [plan-cross-repo.md](plan-cross-repo.md)
- Product scope and phases: [motif-overall-plan.md](motif-overall-plan.md)
- Architecture: [plan-product-architecture.md](plan-product-architecture.md)
- ADRs: [0021](adr/0021-cli-is-the-full-surface-layer-1-churns.md) ·
  [0020](adr/0020-cli-first-fieldworks-planned-not-built.md) ·
  [0019](adr/0019-observed-intent-and-proposal-edit-mode.md) ·
  [0018](adr/0018-change-class-is-two-axes-not-one.md) ·
  [0017](adr/0017-text-and-analysis-destination-scope.md) ·
  [0016](adr/0016-scratch-cache-copy-not-undo.md) ·
  [0014](adr/0014-generate-the-crdt-layer-from-masterlcmodel.md) ·
  [0012](adr/0012-build-order-hc-spine-first-kinds-generated.md) ·
  [0007](adr/0007-cross-language-digest-determinism.md) ·
  [0006](adr/0006-engine-reality-apply-readback-preflight.md)
- Open issues named above: [issues.md](issues.md) (B17, B18, B19, B20, B21)
- Open questions: [grill-plan-a.md](grill-plan-a.md)
