# Plan A — Motif

*The live plan. Adopted 2026-08-01 from
[harmony-adoption-report.md](harmony-adoption-report.md) proposal 2. This file owns both the
milestones and the `MOT-*` items; nothing else defines milestones.*

## Delivery

**Motif delivers exactly two user-facing things: the `motif` CLI and a FieldWorks integration.** One
on-demand local worker supports both, but it is internal infrastructure rather than a third product.

| | |
| --- | --- |
| `motif` CLI | `net10.0` worker client for batch, automation, and AI-agent use; its Host owns live projects only while FieldWorks is closed |
| FieldWorks integration | `netstandard2.0` Runner and worker client hosted in-process, behind FieldWorks-owned Avalonia surfaces |

Everything else Plan A touches is infrastructure or a dependency: the worker ships with Motif, the Lexbox
receipt store (`MOT-14`) is server work in another repository, PanGloss is a subprocess, and
`SIL.Motif.Contract` is a published contract. There is no Motif web app, network service, or mobile surface.

**Everything scoped gets built. The first slice is what the parser touches** —
[ADR 0025](adr/0025-parser-first-build-order.md): the 150 parser-read fields (113 grammar, 32 lexical, 5
other) plus the analysis fields that carry a human judgement. The 323 fields no parser reads — bibliographies,
pictures, pronunciations, publication settings — are slice 2. **Grammar is not deferred**; it is 113 of the
150 and it is where the value is.

**Linked media authoring is explicitly outside the initial scope.** A delete follows FieldWorks' ordinary
LibLCM ownership cascade, including deletion of owned media-reference objects, but Motif does not copy,
archive, restore, create, replace, move, or delete linked bytes. Storage for held picture, audio, video, and
other external files needs its own design before those operations can be admitted.

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
One live authority and one live writer remain; a paired Motif database owns workflow state, not language data.

```
CLI / FieldWorks ──named pipe──▶ per-user Motif worker ──▶ Project.motif.db
                                      │
                              saved file-backed Baseline
                                      │
Proposal ──▶ Dry Run scratch ──▶ effects + optional PanGloss Assessment
                                      │ exact Decision and evidence
                                      ▼
live host Preflight + Apply, one UOW ──▶ Receipt + applied-log entry
```

**One authority, one writer.** The process holding the loaded `LcmCache` is the only writer; Chorus moves
projects between people as it already does. There is no second merge engine and no replicated copy of the
language data. The worker coordinates saved Baselines and workflow records without retaining an idle live
cache. [ADR 0039](adr/0039-one-worker-baseline-and-live-host-authority.md) and the
[detailed design](superpowers/specs/2026-08-20-baseline-dry-run-session-design.md) bind this boundary. Why an
alternative merge design was considered and rejected is recorded once, in the
[adoption report](harmony-adoption-report.md) — it is history, not a plan, and nothing below depends on it.

## Two scopes

[ADR 0020](adr/0020-cli-first-fieldworks-planned-not-built.md), as amended by ADR 0039. **Scope 1 establishes
the LibLCM seams and the shared worker-backed workflow through the CLI with an AI agent as the author. Scope 2
adds the `net48` FieldWorks adapter to the same worker and Runner contracts.**

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
| **M1** ✅ | The generator reads and joins the model without a liblcm checkout | `MOT-2`, `MOT-3` | 1 — **met 2026-08-05** |
| **M2** | One generated operation family applies end to end, driven from the CLI, and its effects are read back | `MOT-4`, `MOT-11`, `MOT-16`, `MOT-19`, `MOT-24` | 1 |
| **M4** | A Proposal is authored by an agent, reviewed, approved, applied, and its Receipt is durable | `MOT-9`, `MOT-10`, `MOT-17`, `MOT-18` | 1 |
| **M5** | One grammar construct authored, reviewed, applied, and parsed | `MOT-6`, `MOT-15` | 1 |
| **M6** | The remaining constructs | `MOT-7`, `MOT-8` | 1 |
| **M3** | FieldWorks hosts DryRun and Apply in-process, on `net48` | `MOT-12`, `MOT-13` | **2 — planned, not built** |
| **M4b** | Receipts shared between people | `MOT-14` | **2** |

Execution order: `M1 → M2 → M4 → M5 → M6`, then M3 and M4b.

M1 and M2 are mechanical. **M4 is the product**, and under scope 1 it is AI-facing first — the agent is
the first author, not the last. M5 is the first thing a linguist would recognise as the point.

## Status summary

This table shows which user-visible capabilities work today and which still require implementation.

| Item | M | Size | Status |
| --- | --- | --- | --- |
| `MOT-2` — the `(Class, Field)` join, failing the build on any unmatched key | M1 | Small | ✅ **Built 2026-08-05** — `src/SIL.Motif.Generator`, 898=898, zero orphans; found two exceptions the spec denied |
| `MOT-3` — generator skeleton: read `MasterLCModel.xml`, emit nothing yet | M1 | Medium | ✅ **Built 2026-08-05** — model 7000072 from the NuGet cache, no liblcm checkout; label harvest done |
| `MOT-4` — emit the operation catalog for one family | M2 | Medium | **Slices A and B built 2026-08-06**, plus a third slice the same day that carries the same three shapes past the lexical-entry family into ADR 0025's parser-first slice — 93 kinds emitted in total (15 lexEntry/moForm + 78 grammar/lexical rows), every one round-tripping against a real project. Owning/atomic beyond the one hand-written `LexemeForm` field, and every owning/col and owning/seq row, still needs its own creation-validity logic and is not emitted |
| `MOT-11` — scratch-cache DryRun, replacing mutate-then-rollback | M2 | Medium | **Built 2026-08-06.** Run takes a single-use `DryRunScratch` and never rolls back; the four poisoning items and the manifest column are deleted; the CLI saves, copies, and holds the project lock. Two real defects surfaced on the way: LibLCM's save is asynchronous, and `classify.ps1` has fallen behind the manifest (`D7`). The DAG closure is also built: each un-applied prerequisite executes once in deterministic topological order, while the Dry Run reports only the requested Proposal's effects. Scratch adoption rejects memory-only backends before taking ownership, closing the writing-system fidelity gap. |
| `MOT-16` — reusable file-backed Dry Run state | M2 | Small–medium | **Prototype built** — `CliSession` proves one pristine file-backed scratch can fan out stable Dry Runs and that memory-only caches are invalid. ADR 0039 replaces the long-lived CLI cache with a durable minimal Baseline owned by the worker; `CliSession` is measurement scaffolding, not the final process boundary |
| `MOT-19` — the CLI as the full product surface, text and JSON | M2/M4 | Large, and grows with every other item | **Partly built** — current commands render shared text/JSON projections, including stored Assessment provenance. The next surface is a named-pipe client with async job status/wait/cancel, `--wait`, refresh requests, old-Baseline warnings, archive/conflict views, and synchronous Apply; PanGloss execution remains separate from the `analyses` query |
| `MOT-9` — Baseline Token, Dry Run binding, Apply Authorization, Receipt | M4 | Medium, correctness-critical | **Partly built** — Dry Run binding exists. ADR 0039 settles reusable Baseline identity, final live Preflight, exact approved Decision, advisory Assessment semantics, `--force` only for unavailable parser evidence, one-use authorization, and no automatic Apply retry |
| `MOT-10` — Proposal revisions, Check Runs, Reviews, Decisions | M4 | Medium, the PR-like product core | **Statuses, the Decision loop, and the rationale presence gate are built.** Apply requiring the exact current approved Decision is now decided by ADR 0039. Remaining: immutable rationale history and Receipt projection, typed Check Runs with exact-input binding, semantic owner routing, policy-versioned autonomous approval, terminal archive/withdrawal, and derived Conflict presentation |
| `MOT-17` — Layer-1 semantic and batch authoring for agents | M4 | Medium, and **expected to churn** | **Slice 1 built 2026-08-13** — the CLI's first Layer-1 authoring surface: `compose-author-lexeme-form` resolves one authored intent against a live project into `AuthorLexemeFormComposer`'s operations (up to three, already built for `MOT-4`'s hand-written `LexemeForm` field) and appends them to a draft the agent never enumerated by hand. The authored intent round-trips as non-hashed `extensions` provenance — verified byte-for-byte excluded from the intent digest — and survives `reopen`/`duplicate` rather than being silently dropped. **Found and fixed on the way:** `DraftOperation.After` was `Dictionary<string,string>`, so any composer emitting a non-string payload (e.g. `setIsAbstract`'s `{"value":true}`) would have thrown or silently stringified a boolean; it is now `Dictionary<string,JsonElement>` everywhere a draft operation is built or replayed. **Not yet built:** batch reads/updates over a live query (this slice batches one intent into many operations, not one query into many targets), and further composers beyond `AuthorLexemeForm` |
| `MOT-18` — selective Proposal editing: duplicate, remove, split | M4 | Small, and required by the agent loop | **Built 2026-08-06** — `duplicate`, `remove-operations`, `split`; declared-dependency closure names every orphaned operation at any depth; every editing path clears the bound anchor. 309 tests pass. One semantic question left open for the owner: `B25` |
| `MOT-6` — semantic + lowering layer for grammar construct 1 | M5 | Medium — **the first product family** | **Slice 1 built 2026-08-13**: `AuthorFeatureStructure`, alongside the lexical `AuthorLexemeForm` — one construct, one `grammar/moStemMsa/createMsFeatures` operation, the first hand-written *grammar* owning/atomic creation-validity answer (ADR 0022 §4). Authored, dry-run, applied, and saved on a real project; refuses closed against an already-occupied slot, a nonexistent MSA, and a wrong-typed target. **Not yet done:** the "parsed" leg of M5's acceptance test (needs the external PanGloss executable this environment does not run) and populating actual feature values (`FsFeatStruc.FeatureSpecs`, `owning/col`, a separate operation against the structure's own identity) |
| `MOT-15` — the parser seam and job orchestration | M5 | Medium | **Direct seam built** — GUID-keyed analyses and real-project correlation are proven. The target handoff is a fresh PanGloss-owned export from each candidate scratch. Remaining: async orchestration, global two-job FIFO, a 25-percent CPU cap per process tree, result/log persistence, cancellation, and immediate/startup workspace cleanup; engines are never persisted |
| `MOT-7` — the remaining 29 constructs | M6 | Large | Not started — gated on `MOT-6` by the plan's own execution order (M1 → M2 → M4 → M5 → M6), and `MOT-6` has only slice 1. The literal "29" is stale: `Construct` (manifest column) stopped naming operation kinds under ADR 0023, and all 495 `Scope=in` rows already carry one; the real remaining work is per-family creation-validity composers like `MOT-6`'s, not a count against the manifest |
| `MOT-8` — ordered-grammar review proof | M6 | Medium | Not started |
| `MOT-12` — FieldWorks in-process adapter | M3 | Medium | **Scope 2, specified** — the `netstandard2.0` package hosts Runner Apply on FieldWorks' `LcmCache`, starts and negotiates with the worker, streams saved Baselines, reports applied-log deltas, and exposes refresh requests; the adapter itself is not built |
| `MOT-13` — `System.Text.Json` on `net48` proof | M3 | Small | **Scope 2** — research says clean (`A4`); the proof itself remains |
| `MOT-14` — Receipt store and sync in Lexbox | M4b | Medium | **Scope 2** |
| `MOT-20` — the Motif store | M2 | Medium, and newly load-bearing | **File ingestion built; target layout settled by ADR 0039** — one sibling `Project.motif.db` holds Proposals and all workflow/bulk records. Terminal archive defaults to 30 days; stale work may live forever; ephemeral PanGloss work is deleted immediately or at startup; applied minima remain in both project log and database. Migration is not built |
| `MOT-24` — per-user worker, protocol, jobs, and scheduler | M2/M4 | Large, architecture foundation | **Specified, not built** — one on-demand `net10.0` worker; named-pipe JSON plus binary Baseline transfer; protocol/capability negotiation; newest-compatible launcher; one database migrator; durable jobs; per-project live lanes; machine-wide PanGloss limits; project-host leases; startup recovery and cleanup. Execute the staged implementation plan before expanding workflow surfaces |
| `MOT-21` — promotion: pulling a curated subset into FieldWorks | M4 | Medium | **Slice 1 built 2026-08-13** — `promote-gloss` is the only sanctioned route from the Motif store into the language project ([ADR 0036](adr/0036-motif-has-its-own-data-store.md) decision 2): an ordinary `lexical/lexSense/setGloss` operation, reviewable and applied like any other, whose evidencing corpus's origin (description, licence, retrieval date) travels as non-hashed `extensions.promotions` provenance — verified excluded from the intent digest, and surfaced in `show`/`ShowJson` so a reviewer never has to open the store's raw files to see it. **Not yet built:** promoting a genuinely *new* stem (a `LexEntry`) has no route at all, because entry creation has no Layer-0 primitive yet ([ADR 0022](adr/0022-structure-is-derived-policy-is-five-rows.md) — "Creation validity" is still hand-authored, per-family, unbuilt for `LexEntry`); promoting a word form worth interlinearising or an analysis worth keeping, similarly unbuilt |
| `MOT-22` — mark a word form as not correctly spelled | M3 | Small | **Built 2026-08-10** — `analysis/wfiWordform/set`/`clearSpellingStatus` are generated from a fourth emit shape (basic `Integer` enum, range-checked payload). Writing it moves the Hunspell dictionary, so the dry run must say so |
| `MOT-23` — the analysis aggregate read API | M3 | Medium | **Built 2026-08-13** — [ADR 0038](adr/0038-expectations-are-fieldworks-approved-analyses.md). Per word form: manual and automatic analyses, counts and instances, links through to the words. "What changed" is the diff between two responses, so there is no separate change-tracking type. It never parses: a missing or stale Assessment is reported, not repaired, which is what let it ship ahead of the rest of `MOT-20`. Three rules are structural rather than careful — the reader holds no parser reference; a reflection test rejects any numeric field on the manual diff that could net removals against passes; another rejects any property name implying Motif knows which change caused which. The unanalysed-reach figure has no bare-number rendering, so "reach, not correctness" travels with it. **Open interpretation**: a word the stored Assessment never covered counts as not parsed, making the figure a floor on reach — the decision record does not say this either way |

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
**494 in-scope rows across 100 in-scope classes** (473 + the 21 analysis rows brought in by
[ADR 0025](adr/0025-parser-first-build-order.md)), 100% classified for every in-scope row. The
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
everything else → `unordered`, with **seven exceptions in two opposite categories**: five where order carries
*more* than position (`LexEntry.AlternateForms`, `PhPhonData.PhonRules`, and the three `PhSegRuleRHS`
alpha-variable fields) and two where a `seq` carries *nothing* — `PhPhonData.Contexts` and `.FeatConstraints`
are pooled storage, not an order (issue B9).

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
- A kind with no description fails the build — **and so does one whose description merely restates its
  label** ([ADR 0023](adr/0023-derived-kind-names-required-descriptions.md) decision 5, as amended). Labels
  live in `manifest/fieldworks-labels.tsv` (harvested, 39% covered); descriptions live in
  `manifest/kind-descriptions.tsv` (hand-written per family, `Reviewed` column tracks linguist sign-off).
- **Both exception categories are asserted explicitly**, so silently losing one is a test failure rather than
  a quieter grammar — and an injected eighth exception belonging to neither category is rejected, because the
  point of the table is that it is closed.
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
(`lexical/lexSense/setGloss`) is regenerated and its existing tests pass **unmodified** — correctness is
established by regenerating code that already passes, not by the design being elegant.

**What passing does not license.** The slice is lexical and almost entirely `unordered`: it exercises
`set|clear`, `addRef|removeRef`, and `create|delete`, and touches **no** `feeding` or `index-as-identity` row.
It licenses the mechanical majority and says nothing about ordered grammar — which is `MOT-8`, deliberately.
Only 4 of the `lexSense` rows and none of the `lexEntry` rows are HermitCrab-reachable, so it also says
nothing about whether a generated kind can change a parse; that is `MOT-6` and `MOT-15`.

### Built 2026-08-06 — a third slice widens the same shapes past lexEntry/moForm

An agent can now edit far more of the grammar than one entry's headword and lexeme form: parts of
speech, inflection classes, inflectional affix templates and slots, natural classes, phonological
rule contexts, feature structures, and more of the lexical-entry family besides — 78 more kinds, on
top of the 15 the family above already had, for a total of 93.

**Nothing new was built to get there — the same three templates slices A and B already proved
(basic `set|clear` over `MultiUnicode`/`MultiString`/`Boolean`, `rel/atomic` `set|clear`, `rel/col`/
`rel/seq` `addRef|removeRef`) were pointed at more rows.** Those templates were never
`LexEntry`/`MoForm`-specific — they read `DeclaringClass`/`FieldName`/`Sig` off the manifest row and
build LibLCM's own interface and accessor names from it — so the only change was which rows a new
selector (`Slice3FieldSelector`) hands them. Every row it selects satisfies two tests, not one:
`Scope=in` (the manifest says this field is in scope) and `HcReachable=yes` (ADR 0025's own authority
for "the parser touches this"), which is what makes this slice count as *the parser-first slice*
rather than an arbitrary 78 rows. Before generating anything, every `(interface, accessor, referenced
interface)` triple these 78 rows would produce was checked by reflection against the real pinned
`SIL.LCModel` assembly — not assumed — so the widening carried the same "verified, not assumed"
discipline ADR 0023 used for the first family.

**What is still missing, named rather than silently dropped.** Owning/atomic beyond `LexEntry.LexemeForm`
needs the same per-field, hand-written entity-construction-validity logic that field itself needed (ADR
0022: "the model file does not encode validity"); it does not fit an existing template, so none of the
remaining owning/atomic rows are emitted. Owning/col and owning/seq (`create|delete|move|reparent`) are
untouched shapes entirely — `MOT-8`'s territory, since several of the fields they would cover are the
`feeding`/`index-as-identity` rows this family has always excluded. And 147 further rows share this
slice's three shapes but sit outside `HcReachable=yes`; they are deferred by priority, not by a missing
capability, and the same three selectors would pick them up unchanged if a later slice asks for them.

Ninety-three sentences now live in `manifest/kind-descriptions.tsv`, all marked `draft` pending a
linguist's review, and all of them pass `Checks/DescriptionCheck.cs`'s bar against restating a label or
field name. Five of the new kinds — one per shape, including the `rel/seq` shape this family had never
exercised before — round-trip author → DryRun → Apply → read-back against a real project in
`tests/SIL.Motif.Tests/Runner/GeneratedSlice3OperationsTests.cs`.

## `MOT-11` — scratch-cache DryRun — M2

Dry Runs must exercise real LibLCM behavior without risking the open project or changing the reusable source
state.

> **This is now the blocker, on evidence rather than principle.** ADR 0016 predicted the cache-poisoning guard
> would stop being dormant "the moment you add a second operation kind." `MOT-4` slice A added nine, two of them
> `AssessPoisonsCache=yes` (`LexEntry.CitationForm`, `MoForm.Form`), and the round-trip test for `MoForm.Form`
> now **disposes and reloads the entire project twice in a single test** to survive its own Dry Run
> (`tests/SIL.Motif.Tests/Runner/GeneratedBasicFieldOperationsTests.cs:74`, `:97`).
>
> Tolerable in a test on a small fixture. Fatal to the product: a reload is ~1.8 s at ~152k objects, and the whole
> premise of the agent loop is many Dry Runs against one open project. The measured XML-path scratch is ~600 ms
> **and does not poison the live cache at all**, so the live cache stays usable and no reload is forced. That
> makes this the next thing to build, not a later cleanup.

Implement [ADR 0016](adr/0016-scratch-cache-copy-not-undo.md) **as amended 2026-08-05: one canonical path —
copy the project's files and open the copy** (~50 ms + ~550 ms at ~152k-object scale). A live-cache footprint probe
gates rebuilding it. Apply stays on the live cache.

**`CreateCacheCopy` and the in-memory fan-out are withdrawn.** They were ~5× faster and measurably lossy:
every `kMemoryOnly` cache re-synthesizes its writing systems from the bare language tag, so the larger
project's vernacular lost its collation rules and all four writing systems lost their valid-character sets — including
when the copy was taken from a file-loaded scratch whose writing systems were intact. One path, no
per-operation judgement call about whether collation matters.

**The sequence, and the host's part in it** ([ADR 0016](adr/0016-scratch-cache-copy-not-undo.md) as amended
2026-08-06): **save, copy the file, open the copy, apply to the scratch, read effects back, discard the scratch,
bind the anchor**; then at apply time re-check the anchor, and **on failure reload from the saved file** rather
than relying on the rollback.

The save must precede the *Dry Run*, not the apply, because the scratch is a copy of the saved file — otherwise
uncommitted edits are invisible to validation and apply reports drift that did not happen. Reload is also
*stronger* than rollback: it discards the non-undoable schema phase that
[ADR 0005](adr/0005-schema-operations-non-undoable-uow.md) leaves behind, which rollback cannot.

`ProposalApplier` already never saves, so "save first" is a **host precondition** rather than Runner code. The
one thing it needs is to be **visible to the user** — a save commits in-flight edits at a moment Motif chose.

**Deliverables**

1. ~~The scratch lifecycle, including a prerequisite-DAG mode that applies a topologically-sorted
   closure of un-applied Proposals to one derived scratch.~~ **Done** — file and session Dry Runs
   resolve the same finalized closure, consult the live applied log, and prepare one single-use scratch.
2. ~~Two measurements before the design is built on.~~ **Done, 2026-08-05** — `A1` is measured against
   a real 152,222-object project. Harness: `spikes/SIL.Motif.Spikes.ScratchCache`; equivalence assertions live in
   `tests/SIL.Motif.Tests/Runner/ScratchCacheEquivalenceTests.cs`. Results and the resulting amendment are
   in [ADR 0016](adr/0016-scratch-cache-copy-not-undo.md) and the
   [research note](research/2026-08-05-createcachecopy-provenance-and-hazards.md#10-measured--the-spike-was-built-and-run).
   **The fan-out holds (140 ms vs 4,445 ms, 31.8×), but build the pristine scratch from the XML path by
   default** — a memory-only copy loses every writing system's collation and valid-character settings, and
   the file path is cheaper once more than ~9% of the live cache is fluffed.
3. **Deletion, not retirement** ([ADR 0016](adr/0016-scratch-cache-copy-not-undo.md) as amended 2026-08-06):
   `DerivedCachePoisoningOperationKinds`, `RollbackCacheInvalidator`, `CacheReusability`, and the manifest's
   `AssessPoisonsCache` column all go. Because the only surviving rollback is the failure path of an atomic
   apply, **nothing needs to know which fields poison** — the rule is unconditional: if an apply throws, the
   live cache is suspect, discard it. That removes the hand-maintained field list whose upkeep would otherwise
   recur for every new field in the catalog. This answers `C10a`.
4. The scratch is **single-use — discarded, never reverted.** Rolling back inside a reused scratch would
   recreate the same staleness inside the scratch and the next Dry Run would read back wrong: a smaller copy of
   the original bug.
5. ~~A guard so a Dry Run cannot silently use a memory-only scratch.~~ **Done** — adoption rejects the
   backend because it loses project-specific collation and valid-character definitions, and directs callers
   to a file-copy scratch.

**Acceptance:** a dry run never mutates the live cache; a poisoned scratch costs a rebuild, not a
session; the DAG closure produces the same effects as applying the closure serially.

### Built 2026-08-06 — what shipped, and the two things it uncovered

Deliverables 3 and 4 are done, and the type system carries rule 4 rather than a comment: `Run` takes a
`DryRunScratch` (`src/SIL.Motif.Runner/DryRun/DryRunScratch.cs`) instead of an `LcmCache`, refuses a second
use, and mutates inside a *non-undoable* unit of work with `RollBack` cleared **before** the first mutation —
so even a failing dry run ends its task rather than reverting it. Discarding the scratch is the undo.
`DerivedCachePoisoningOperationKinds`, `RollbackCacheInvalidator`, `CacheReusability`,
`CachePoisonedException` and the `AssessPoisonsCache` column are deleted; net **negative** code, and the
operation round-trip tests lost their dispose-and-reload dances entirely.

The CLI performs the full sequence — **open the live project (which takes the lock), save, copy, open the
copy, run, discard, release** — per [ADR 0030](adr/0030-one-writer-cli-locks-like-fieldworks.md). An earlier
attempt skipped the live open on the grounds that the dry run no longer needs the live cache; that was wrong,
because the anchor is only meaningful if nobody else can edit while it is being measured.

**Two defects found by building it, both worth more than the feature.**

1. **LibLCM has no synchronous save.** `Commit()` enqueues the write on a background thread and returns, so
   the copy read a file one operation stale and Apply reported *footprint drift on a project nobody had
   touched* — twelve tests, with a diagnostic that accused the wrong component. `FwDataProjectLoader.Save`
   now waits on `CompleteAllCommits()`, matching what liblcm's own `ProjectLockingService` does, and
   `SaveIsSynchronousTests` pins it at file level. The barrier is only reachable by reflection, so **a
   liblcm PR exposing a synchronous save is an upstream ask.** Details in ADR 0016.
2. **`classify.ps1` no longer reproduces the manifest** — rerunning it reverts 26 hand-authored ADR 0025
   rows. Recorded as `D7`; the README now calls it a first-pass tool rather than the producer.

## `MOT-20` — the Motif store — M2

**What this is for:** so the data Motif needs in order to say anything useful about a grammar has somewhere to
live that is not the linguist's project file. Measuring how much of a language a grammar reaches means real
running text — tens to hundreds of megabytes of it once spelling correction and word prediction are drawing on
the same material — and none of that belongs in a `.fwdata` that people copy, back up and sync.

ADR 0036 established the separate store; [ADR 0039](adr/0039-one-worker-baseline-and-live-host-authority.md)
settles its shape and lifecycle. Every project has one sibling `Project.motif.db` containing Proposals,
revisions, Decisions, jobs, Corpora, Assessments, Reports, Receipts, and the applied index. Content digests
remain the identity of immutable intent and evidence even though their container is SQLite. Only the selected
worker opens and migrates it; the `net48` FieldWorks adapter never loads the database library.

Terminal Proposals archive immediately and default to 30-day local retention, configurable through forever.
Stale work is nonterminal and may remain indefinitely. PanGloss exports and workspaces are never archived:
they disappear immediately on completion and unconditionally on startup. A minimal applied record survives
archive deletion in both the project applied log and the database. FieldWorks-managed moves carry the sibling
database; managed duplicates start fresh. Derived Baselines and work live under a full-path-plus-project-id
key and abandoned path folders are evicted after 30 days on worker startup.

**Explicitly deferred, decided 2026-08-09: spelling corrections are a separate data type and are not being
designed now.** A mistyped form paired with its correction — *what someone typed* against *what they meant* —
is not a wordform, not an analysis, and has no FieldWorks counterpart, so it will need a Motif type of its
own. It is deliberately out of scope until spelling correction is actually being built, which may also bring
its own interface. Recorded so that the absence reads as a decision rather than an oversight.

Distinct from **misspelled words Motif wants to represent**, which is a nearer-term need and *does* have a
FieldWorks home: `WfiWordform.SpellingStatus`, tracked as `MOT-22`.

## `MOT-23` — the analysis aggregate read API — M3

**What this is for:** so a person can ask one question — *what do we assert about this word, what does the
parser say about it, and how many places does it appear* — and get an answer they can act on, for one word or
for all of them.

Established by [ADR 0038](adr/0038-expectations-are-fieldworks-approved-analyses.md) decision 5. Per word
form: the aggregate of its **manual** analyses (what a human approved — the tests) and its **automatic** ones
(what the parser produced), with counts and instances per manual analysis, links through to the words
themselves, and an option to run the parser over all of them and compare against what is recorded.

**Why this replaces a change-tracking design rather than adding to one.** *What changed* is the difference
between two responses to this query. Differences in the automatic analyses are grammar coverage moving;
differences in counts are text churn; what is left — differences in the manual analyses — is a test being
established, updated or removed. There is nothing separate to build and nothing to keep in sync.

**Two constraints it must honour.** Established, updated and removed are reported separately and never netted
against passing, because removing the last approved analysis on a word form improves every number while
reducing what is checked. And Motif does not attribute cause: when a proposal changes both the rules and the
analyses, we know the analysis changed and we know it passes now, and which caused which is not visible.

**One counted figure for the unanalysed.** Of the correctly-spelled word forms with no manual analysis, how
many does the grammar parse. It is deliberately the *only* thing reported about them — a word nobody has
analysed carries no expectation. And it is weak evidence in a direction that matters: nobody checked these
words, so a rising number is equally consistent with the grammar improving and with it getting looser. It
supports a claim about reach and never about correctness, and the report says so
([ADR 0038](adr/0038-expectations-are-fieldworks-approved-analyses.md) decision 7).

**It never parses, and that is what unblocks it.** Decided 2026-08-09
([ADR 0038](adr/0038-expectations-are-fieldworks-approved-analyses.md) decision 5): reading the aggregate
reads whatever Assessment exists and reports a missing or stale one rather than repairing it. Producing an
Assessment is a separate, explicitly-invoked, slow verb. So **this task does not wait on `MOT-20`** — with no
Assessment stored it still returns the manual analyses, which are the test suite and need no parser.

**The dependency that makes it possible at all:** FieldWorks deletes the previous approved analysis when a
human edits a breakdown, so the before-state has to be in the change set's comparison footprint or the
question is unanswerable after the fact ([ADR 0038](adr/0038-expectations-are-fieldworks-approved-analyses.md)
decision 4).

## `MOT-22` — Motif can mark a word form as not correctly spelled — M3

**What this is for:** so a person working through a list of parse failures can say "that one isn't a word" and
have it stick, in the same change that fixes the rules for the ones that are.

**Decided 2026-08-09.** `WfiWordform.SpellingStatus` is a real, human-settable, durable three-state flag
(`undecided` / `correct` / `incorrect`) with a live Bulk Edit Wordforms column, and FieldWorks protects it —
`DeleteIfSpurious` refuses to delete a wordform whose status is not `undecided`, because *"we know something
about it that we don't want to forget"*. Motif both **reads and writes** it.

**Reading it is the free half.** A wordform a linguist already marked `incorrect` that the grammar cheerfully
analyses is over-generation evidence, using judgement they recorded in the place they normally record it.

**Writing it has a consequence that must be shown.** A `SpellingStatus` change routes through
`MorphologyListener.PropChanged` into `SpellingHelper.SetSpellingStatus`, which adds or removes the word from
the Hunspell dictionary — so a word Motif marks `incorrect` starts showing a red squiggle in the linguist's
texts. That is a visible change to a different tool caused by an operation about grammar, and it belongs in
the dry run's expected effects rather than as a surprise after apply.

**The work.** The manifest row exists and is switched off — `Scope="out"`, no `Group`, no `Verbs`. Give it the
same hand-authored [ADR 0025](adr/0025-parser-first-build-order.md) treatment `Analyses` and `Form` already
carry on that class: a group, the derived `set|clear` verbs, and `EnumValues="0=Undecided;1=Correct;2=Incorrect"`
following the `CmPicture.LayoutPos` precedent. Then regenerate the kind and extend
`ManifestHandAuthoredRowsTests`, which pins those rows against a `classify.ps1` rerun.

**Built 2026-08-10.** `analysis/wfiWordform/setSpellingStatus` and `analysis/wfiWordform/clearSpellingStatus`
are generated, from a fourth emit shape: a basic `Integer` standing in for a small closed enum, whose payload
range-checks the value against the manifest's own `EnumValues` rather than trusting liblcm's
`ValidateSpellingStatus` to fix it.

**One thing this task got wrong first, recorded because the correction is the interesting part.** It shipped as
`Verbs="set"` only, on the argument that `clear` would be a synonym for `set 0` given that the zero member is
`Undecided` — and it created the first-ever exception table for `Verbs` in the generator to express that,
against [ADR 0022](adr/0022-structure-is-derived-policy-is-five-rows.md) decision 1's "seven combinations, zero
exceptions". The other eleven in-scope enum fields settle it: every one carries the derived `set|clear`, and
every one of *their* zero members is a substantive value (`CenterInColumn`, `Variant`,
`LeftToRightIterative`). `clear` in this manifest has never meant "erase to nothing" — it means "write the zero
member" — which makes `SpellingStatus`, the one enum whose zero genuinely means *no judgement recorded*, the
best candidate for `clear` rather than the worst. The exception table is gone, ADR 0022's claim stands
unamended, and `ManifestHandAuthoredRowsTests.EveryInScopeEnumField_CarriesTheDerivedSetClear` now fails the
build if any row tries this again.

**What this does not cover, and it is a different claim:** a word that *is* correctly spelled but that the
grammar should not analyse — a borrowed proper noun, a code-switch. FieldWorks has nothing for that, and
neither does this task.

**The property that must be maintained, and it is load-bearing:** everything in the store is either cached or
re-fetchable, which is what makes losing it cost time rather than work. The first genuinely authored thing to
land there breaks that, and it should be resisted or the decision revisited deliberately.

**Built 2026-08-09 — ingestion.** Text can now get in. `add-corpus`, `add-document` (from a file or a URL) and
`add-corpus-bundle` are live, and everything lands with its origin, its tokenisation record, a SHA-256 of the
exact bytes, and what its licence permits. See [corpus ingestion](corpus-ingestion.md) and
[ADR 0037](adr/0037-fetching-lives-outside-motif.md).

Two things this settles that were open. **Fetching is not Motif's job** — an outside tool, in practice
linguistic-assistant, pulls from OPUS and eBible and hands over a bundle; Motif ingests and records. And
**licences are resolved per Document, not per Corpus**, because roughly 805 of eBible's ~1,004 translations
are No-Derivatives while the rest are not: reach figures may be computed over all of it, and an n-gram model
may be built from only part.

It goes through `ICorpusStore`, so moving Corpora into the embedded database above does not reach ingestion.
`FileCorpusStore` is what satisfies it today.

## `MOT-21` — promotion into FieldWorks — M4

**What this is for:** so a linguist can take the useful part of a hundred thousand analysed word forms and put
it in their project, without the other ninety-nine thousand coming too.

The only sanctioned route from the Motif store into the language project
([ADR 0036](adr/0036-motif-has-its-own-data-store.md) decision 2). It is the same act
[ADR 0032](adr/0032-stem-assessment-is-pangloss-supplied-lexicon.md) already assigns to Motif — PanGloss
evaluates stems in a throwaway overlay and declares promotion a non-goal — with a different source.

**Deliverables.** Choosing what crosses: word forms worth interlinearising, stems worth adding to the lexicon,
analyses worth keeping. An ordinary Proposal for the crossing itself, so it is reviewable and leaves a Receipt
like any other change. And the corpus provenance travelling with it, because a stem promoted from a CC-BY-SA
source carries that obligation into the dictionary.

## `MOT-16` — reusable file-backed Dry Run state — M2

**The prototype proved reuse; the accepted product boundary is now the worker-owned Baseline.** This section
records the experiment that established file-backed fidelity and fan-out. ADR 0039 supersedes its proposed
long-lived CLI cache, and `MOT-24` owns the replacement.

Today the CLI is process-per-command: `Commands.DryRun` and `Commands.Apply` each call
`loader.LoadCache(fullFwDataPath)` and dispose, so a dry run followed by an apply pays two full project
loads (`E21`). That was acceptable when the cost did not matter. Under [ADR 0016](adr/0016-scratch-cache-copy-not-undo.md)
it does: the pristine scratch is worth having only if something outlives one command.

An agent loop makes this sharper than a human CLI ever would — author, dry-run, inspect effects, revise,
dry-run again is many round trips against one project. It is also the same shape FieldWorks has for free,
since FieldWorks already holds an open cache for the length of a session: **`MOT-16` is the CLI catching
up to FieldWorks' natural lifecycle, not diverging from it.**

**Prototype deliverables, completed.** A session holding one live cache and one pristine scratch; commands
that operate against the session rather than a path; footprint-gated scratch re-copy; explicit teardown that
never leaves a lock held. Production replaces the warm cache with one saved minimal Baseline supporting many
single-use scratches without holding the live project open.

**Production acceptance:** one saved Baseline supports twenty identical Dry Runs with no linked-media copy,
no live-project lock, no memory-only cache, and no retained scratch after each run.

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
because the agent is the first author. **And under [ADR 0029](adr/0029-agents-address-layer-1-only.md) this is
the agent's *entire* surface** — there is no generic field-level escape hatch, so a composer that does not
exist is a capability the agent does not have. Layer 0 primitives are the diff's *output* vocabulary; Layer 1 is
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

Portability belongs to the Proposal contract, not its container: the canonical Proposal and referenced
immutable evidence can be exported from the paired database as a verified bundle. Copying the database is
project duplication, not Proposal transport.

**Acceptance:** an operation can be removed from a finalized-then-reopened Proposal; a removal that would
orphan a dependent enumerates the consequences and refuses without force; a removal whose consequences
cannot be enumerated refuses even with force.

## `MOT-9` — reviewed world equals applied world — M4

**Partly built.** `BoundDryRunAnchor`, `FootprintProbe.ComputeCurrentFootprintDigest`, the
drift-refusal precondition, `Receipt`, and `ProjectAppliedLog` all exist and are tested for one
operation kind. What remains is the portable Baseline Token, one-use apply authorization, and the
reconciliation state machine across UOW / save / receipt boundaries.

The authored Proposal remains the only semantic input; the LibLCM Mutation Plan is output-only.

**Acceptance:** two agents begin at one Baseline Token; one Proposal applies in declared dependency order and
the other refuses drift before mutation. Injected failures at every UOW/save/receipt boundary produce
rollback or `NeedsReconciliation`, never blind retry.

## `MOT-10` — Proposal review domain — M4

**What this is for:** so that the reason a change was made outlives the person who made it. The grammar
will be written almost entirely by one person or one AI, and that person will eventually leave the
project; a successor inherits a working grammar and no idea why any of it is the way it is. This item
exists to close that gap, not to let several people edit safely — which
[ADR 0031](adr/0031-collaboration-follows-the-data-not-the-surface.md) establishes is not achievable for a
grammar at all.

Immutable Proposal revisions, typed Check Runs, human and AI Reviews, versioned policy Decisions,
semantic owner routing, stale-binding rules.

**Scoped by [ADR 0031](adr/0031-collaboration-follows-the-data-not-the-surface.md).** Concurrent-review
reconciliation is out. In scope, and now the centre of the item:

- **A short description and an extended explanation on every Proposal**, both surviving into the applied
  record. The long form is expected to be AI-written for a human to read — that is its normal origin, not
  a fallback. The short form exists so a human can *skip*; a strong model reads all forty pending
  proposals, a human reads three.
- **A human reply that changes a Proposal produces a new revision**, not a comment attached to a frozen
  document. The amend loop already exists in the CLI (`ReopenAmendTests`); this is its review-side half.
- **Statuses are decisions, and dependency is not one of them.** `proposed`, `deferred`, `approved`,
  `rejected`, `applied`, `superseded`. "Depends on another Proposal" is already `requires`
  ([ADR 0004](adr/0004-prerequisite-graph-stable-ids-bound-apply.md)), a fact about content that governs
  apply order — displayed beside the status, never as one, or a status edit could silently break ordering.
- **`deferred` means "still wanted, needs re-validation", never "frozen and still applicable"** — a
  deferred Proposal's bound anchor goes stale as the project moves underneath it.
- **No network review service and no replicated store.** Review records live with the Proposal in the paired
  local Motif database, and CLI and FieldWorks are two surfaces over that one record. Offline use needs no
  network.
- **Checks are classified by what they need, not by which data changed.** A **recompile** is grammar-only
  and expensive. A **parser run with no recompile** is cheap and applies to stems and texts as well — and
  for a Proposal adding stems it is the whole point: did they get the right category and allomorphs, and do
  previously-unparsed occurrences now parse correctly. A new text earns fresh coverage numbers the same
  way. Everything else needs neither. This makes
  [ADR 0028](adr/0028-feeding-reorders-require-a-grammar-delta.md)'s proportionality principle structural.
- **The class is derived from the current revision, and never declared.** A Proposal that starts as 200 stems
  and grows a rule change becomes a grammar Proposal, inherits the expensive checks, and staleness
  invalidates the cheap results it already had. Adding words is often what *reveals* the grammar problem, so
  a class declared at authoring time would fight the workflow and would have to be corrected by hand.
- **The Grammar Delta is PanGloss's, not ours.** `pg-assess` already exports `GrammarDelta`, `CaseDelta`,
  `DeltaCategory` and a versioned `DELTA_SCHEMA`, built on the same value-not-reference analysis identity
  Motif arrived at. Consume it; do not build a second one.
  See [ADR 0031](adr/0031-collaboration-follows-the-data-not-the-surface.md).
- **There is no human-facing review surface for dictionary or text Proposals** — not deferred, not wanted.
  A human adding a word uses FLEx, which already synchronises that work.

**Acceptance**

- any change to Proposal, baseline, relevant artifact, tool contract, interpretation version, or
  policy revision invalidates the former Check Runs and Decision;
- static-analysis Check Runs are first-class immutable facts with the same exact-input and stale
  binding as Dry Run, conformance, and policy checks;
- **a change class may *require* a particular Check Run**, rather than every Proposal carrying the same set
  ([ADR 0028](adr/0028-feeding-reorders-require-a-grammar-delta.md)). The first case is a `feeding` reorder
  requiring a Grammar Delta; the precedent matters more than the instance, since it is what keeps review
  proportionate instead of demanding a parser run for a spelling fix;
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
- Both of the naming blockers this item once carried are resolved by
  [ADR 0023](adr/0023-derived-kind-names-required-descriptions.md). The kind identifier is derived
  rather than named, and human meaning moved to a required `description`; the multi-construct cell
  turned out to be asking for a runtime fact in a schema-time name, so fields are named where they are
  declared instead. Nothing about construct naming blocks this item now.

**Acceptance:** one construct authored, dry-run, reviewed, applied, saved, and round-tripped through
Chorus Send/Receive without the applied log being corrupted — see
[the standing Chorus risk](harmony-adoption-report.md#standing-risk--chorus-does-not-merge-the-applied-log).

**Slice 1 built 2026-08-13.** `AuthorFeatureStructure` (`src/SIL.Motif.Runner/Composers/`) lowers one
authored intent — an existing `MoStemMsa` to give an (initially empty) feature structure — into
`grammar/moStemMsa/createMsFeatures`, a new hand-written owning/atomic operation
(`src/SIL.Motif.Runner/Operations/MoStemMsaMsFeatures.cs`) alongside the lexical `LexEntry.LexemeForm`
precedent. Its creation validity answer is genuinely trivial rather than deferred: LibLCM imposes no
minimum on `FsFeatStruc.FeatureSpecs`, so an empty feature structure is already a valid one. The composer
refuses closed, before authoring anything, against an already-occupied slot, a nonexistent MSA, and a
wrong-typed target. Authored (through its own closed-schema intent parser), dry-run, applied, and saved
against a real project — not yet run through Chorus Send/Receive, and not yet parsed (needs the external
PanGloss executable this environment does not run; see the skipped `ParserSeamIntegrationTests`).
Populating actual feature values is separate, later work against the created structure's own identity
(`FsFeatStruc.FeatureSpecs`, `owning/col` — no `Placement`/ordering machinery needed, unlike an
`owning/seq` family).

## `MOT-15` — the parser seam and job orchestration — M5

**What this is for:** so that a grammar change can be judged by what it did to parsing, rather than only
recorded. Without it the rationale record that
[ADR 0031](adr/0031-collaboration-follows-the-data-not-the-surface.md) makes the point of review has nothing
to record: a grammar change's justification *is* "coverage moved from here to there, and these forms stopped
parsing."

**Built 2026-08-07, and the plan below was wrong in a useful way.** See
[the seam measurement](research/2026-08-07-parser-seam-goes-through-the-project-file.md).

### What the direct seam proved

`src/SIL.Motif.Host/Parser/` currently hands PanGloss a **project file path** and gets back typed analyses
whose identities are **FieldWorks GUIDs**. `PanGlossParser.AnalyseBatch` answers "did it parse, and how
fast"; `.Assess` answers "what did it parse it *as*". Outcomes distinguish analysed / no-analysis /
**timed-out** / skipped, and a batch containing any timeout reports itself as a lower bound (`D9`). An FST
build refusal is recognised as a *grammar fact* and returned for the caller to fall back on, never conflated
with a missing or broken parser.

Tested at both levels: eight unit tests over **captured real parser output** rather than invented fixtures,
and two integration tests against a real 56 MB project, of which the load-bearing one asserts that
**every morpheme GUID the parser names resolves to an object that project actually contains.** That is the
assertion the whole route was chosen for, and without it correlation could fail silently while coverage
numbers kept working.

### Why the two-step plan below was inverted

~~1. **HC XML now** … 2. **pg-snapshot next.**~~ The cheap first step produces answers Motif cannot use, and
the second step turned out not to need writing.

- **No FieldWorks dependency is required.** `HCLoader.Load(cache, logger)` is `public static` but lives in
  **FieldWorks**, not liblcm (`Src/LexText/ParserCore/HCLoader.cs`), and drags in
  `SIL.Machine.Morphology.HermitCrab`. Taking that route means depending on application code from scope 1, or
  porting and maintaining a fork. **PanGloss reads `.fwdata` directly instead** — 253 ms to compile a grammar
  out of the 56 MB project.
- **HC XML's identities are unusable here, as this plan already suspected — and it is worse than "Hvo drift".**
  Measured: the two routes produce *structurally identical* analyses under different names, HC XML in synthetic
  keys (`mrule128`, `entry1083`) and the project route in GUIDs. So the cheap step yields correct linguistics
  that cannot be tied to any entry a Proposal edited.
- **The C# snapshot producer is unnecessary.** `pg_fwdata::import_file` already does it, in Rust, from the file.

### Accepted production handoff

PanGloss exports every private file it needs from the candidate live scratch and owns that interchange
format. Motif starts a fresh build for every Proposal Assessment, never persists the engine, and retains only
the immutable result plus a bounded log. The candidate export may wait on disk while PanGloss runs, then is
deleted immediately; startup deletes any interrupted workspace.

The worker returns a job id by default. Each user worker schedules its projects FIFO. Machine-wide leases
admit at most two PanGloss process trees per PC and cap each entire build-and-analysis tree at 25 percent of
total CPU; ordering between different Windows users is unspecified. Assessment may overlap later project Dry
Runs after candidate export releases the project's LibLCM lane.

### What remains

The remaining parser work is integration work for FieldWorks and evidence gathering for unusually difficult
projects.

- **One FFI entry point, and it is scope 2's blocker rather than scope 1's.** The C ABI takes HC XML only, so
  the GUID-keyed route is reachable today only by running the executable. Scope 1 shells out, contained to
  `PanGlossExecutable`/`PanGlossParser` so an in-process implementation can replace it without touching
  callers. FieldWorks hosting the parser on `net48` needs `hc_grammar_load_snapshot`.
- **A real project that neither engine could handle**, recorded as a risk rather than solved: `aweti.fwdata`
  overflowed the FST enumeration budget *and* its fallback did not finish one word of fifteen in ten minutes.
  Whether that is a class of project or a curiosity is unknown and worth knowing, because it decides whether
  "fall back to HermitCrab" is an answer or just the next thing to try.

**The comparison this feeds is settled** ([ADR 0027](adr/0027-what-counts-as-the-same-word-analysis.md)): the
pass/fail gate is morphology only — morph count, and per morpheme the allomorph, category record and
inflection type. Sense and word-level part of speech are **reported, not gating**, because the parser cannot
populate them and PanGloss cannot express them. A green result claims *"the parser agrees about the
morphology"*, and whatever surfaces it must say so.

**The comparison this feeds is settled** ([ADR 0027](adr/0027-what-counts-as-the-same-word-analysis.md)): the
pass/fail gate is morphology only — morph count, and per morpheme the allomorph, category record and
inflection type. Sense and word-level part of speech are **reported, not gating**, because the parser cannot
populate them and PanGloss cannot express them. A green result claims *"the parser agrees about the
morphology"*, and whatever surfaces it must say so.

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

**The two halves need different checks** ([ADR 0028](adr/0028-feeding-reorders-require-a-grammar-delta.md)),
because they fail differently. A `feeding` reorder fails *silently* — the grammar quietly accepts different
words — so it **requires a Grammar Delta** before approval: an Assessment before and after, against one
baseline, naming the analyses that changed. An `index-as-identity` edit fails *loudly* — the 24-per-rule
ceiling throws and kills the grammar load — so it needs a cheap pre-apply traversal check, which per `C13`
means calling `IPhRegularRule.FeatureConstraints` rather than reimplementing the walk.

**Acceptance:** a reorder of real phonological rules produces a Grammar Delta bound to one baseline that names
which analyses changed — an empty delta being the useful "this reorder was safe" result — and an
alpha-variable edit that would exceed 24 is refused before apply, not discovered at parse time.

## `MOT-24` — per-user worker, protocol, jobs, and scheduler — M2/M4

**The CLI becomes fast and asynchronous without taking authority away from FieldWorks.** One local worker
keeps durable workflow moving across short-lived commands and several projects, while live LibLCM work still
belongs to whichever host owns the project lock.

Established by [ADR 0039](adr/0039-one-worker-baseline-and-live-host-authority.md), specified in
[the worker and Baseline design](superpowers/specs/2026-08-20-baseline-dry-run-session-design.md), and staged
in [the implementation handoff](superpowers/plans/2026-08-22-motif-worker-baseline-implementation.md).

**Deliverables.** A stable launcher and one on-demand `net10.0` worker per Windows user; a versioned local
named-pipe JSON control contract and binary Baseline-transfer pipe; capability negotiation across differing
FieldWorks and CLI product versions; one database owner and transactional migrator; immediate durable
authoring; async job status/wait/cancel and `--wait`; per-project live lanes with refresh barriers and host
leases; global PanGloss scheduling; restart recovery; archive, cleanup, reconciliation, and Conflict state.

Apply is deliberately outside the job queue. It waits up to five seconds for the project gate, then succeeds
synchronously or refuses busy. The worker issues a one-use exact-bound Apply Authorization, while the live
host performs Preflight, one Runner UOW, save, and Receipt reporting. A missing or failed Assessment needs
`--force`; a completed bad Assessment remains advisory and never blocks by score.

**Acceptance.** Two compatible client versions share one worker and one database owner; an incompatible
client fails without touching the database. One saved Baseline drives twenty stable Dry Runs while FieldWorks
can continue editing. Refresh and Apply ordering is deterministic. Interrupted work restarts safely without
ever retrying Apply. Two PanGloss jobs across all projects and users stay within the machine-wide CPU
envelope. Project-log reconciliation either repairs the store or produces a loud, explainable Conflict.

---

# Scope 2 — planned, not built

[ADR 0020](adr/0020-cli-first-fieldworks-planned-not-built.md). Everything below is fully planned and
deliberately unbuilt until scope 1 is shown to work. The detail is kept because the point of planning
it now is that scope 1 must not make it more expensive — see ADR 0020 decision 3 for the invariants
scope 1 owes it.

## `MOT-12` — FieldWorks in-process adapter — M3 *(scope 2)*

**FieldWorks keeps control of the live project while sharing Motif's durable workflow.** The command/UI seam
still needs the `F26a` spike before observed-intent authoring UI begins. It does not gate the UI-free worker
client, Baseline capture, reconciliation, or direct Runner Apply package: ADR 0039 fixes those boundaries and
the responsibilities on each side.

The `net48` seam. Marshal to the UI thread, pass FieldWorks' own `LcmCache`, supply the applier
identity, call `Save`, invalidate the parser and UI, and invoke the `netstandard2.0` Runner directly. The same
package starts and negotiates with the `net10.0` worker, registers FieldWorks as live host, streams a saved
minimal Baseline over the binary pipe, receives refresh requests, and reports applied-log deltas for
reconciliation. It never opens `Project.motif.db` and never sends an `LcmCache` across the pipe.

**Acceptance** is Gate 1 from [fieldworks-crdt-integration-research.md](fieldworks-crdt-integration-research.md):
one lexical operation Dry Run from a streamed Baseline and Apply through a FieldWorks-owned surface, on the UI
thread, as one undoable UOW; exact authorization and live Preflight reject stale or wrong-type targets; the
saved applied-log entry reconciles a disconnect before or after the worker receives the Receipt.

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

Review state — comments, approvals, decisions — is mutable and remains in the paired local Motif database.
Any future Lexbox sharing contract must preserve that offline authority and reconcile explicitly; this item
does not turn the local database into a network cache.

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

These documents preserve the decisions and evidence that constrain this plan.

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
