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
ship. There is no Motif web app, no Motif service, no Motif mobile surface, and no Motif presence in
FwLite.

**Scope: lexical and grammar. Text and analysis are out**, per the manifest's own classification —
`Segment`, `WfiAnalysis`, `WfiWordform`, `WfiMorphBundle`, `Text`, `CmAgent`, and `StTxtPara` are all
marked `out` / `not-domain-reachable`, leaving eight text-adjacent rows in scope. Text needs its own
bounded context and an occurrence-anchor contract. Neither is planned here.

> **Under challenge.** The
> [2026-08-03 proposal](proposal-2026-08-03-bidirectional-and-test-coverage.md) argues for bringing
> text and manual word analysis in as change classes 3 and 4, so that analyses act as unit tests and
> text coverage as code coverage — and for making **bidirectional diff** (compare two LibLCM projects,
> emit the operations between them) a foundation rather than a downstream feature. Neither is adopted.
> Both would change this plan's scope and its item list. Open questions are `F`–`J` in
> [grill-plan-a.md](grill-plan-a.md).

## The shape of Plan A

Motif authors **Proposals** against **LibLCM objects**, dry-runs them on a scratch cache copy, applies
them through one LibLCM unit of work in whatever process owns the live cache, and records a Receipt.
There is no CRDT in this path, no second process, and no second authority.

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

**What this plan removed, and why.** The previous plan routed grammar through Harmony's CRDT. That
required a MiniLcm↔LibLCM crosswalk, converging sequence types, reference-set policy, and a
cross-owner move rule — none of which any FwLite requirement asked for. Removing the routing removes
the need. See the [adoption report](harmony-adoption-report.md) for the full argument, and
[plan-lcmcrdt.md](plan-lcmcrdt.md) for what was withdrawn.

## Milestones

| | Gate | Items |
| --- | --- | --- |
| **M1** | The generator reads and joins the model without a liblcm checkout | `MOT-2`, `MOT-3` |
| **M2** | One generated operation family applies end to end and its effects are read back | `MOT-4`, `MOT-11` |
| **M3** | FieldWorks hosts DryRun and Apply in-process, on `net48` | `MOT-12`, `MOT-13` |
| **M4** | A Proposal is reviewed, approved, applied, and its Receipt is shareable | `MOT-9`, `MOT-10`, `MOT-14` |
| **M5** | One grammar construct authored, reviewed, applied, and parsed | `MOT-6`, `MOT-15` |
| **M6** | The remaining constructs | `MOT-7`, `MOT-8` |

M1 and M2 are mechanical. M3 is integration. **M4 is the product.** M5 is the first thing a linguist
would recognise as the point.

## Status summary

| Item | M | Size | Status |
| --- | --- | --- | --- |
| `MOT-2` — the `(Class, Field)` join, failing the build on any unmatched key | M1 | Small | Not started |
| `MOT-3` — generator skeleton: read `MasterLCModel.xml`, emit nothing yet | M1 | Medium | Not started |
| `MOT-4` — emit the operation catalog for one family | M2 | Medium | Not started |
| `MOT-11` — scratch-cache DryRun, replacing mutate-then-rollback | M2 | Medium | Not started — **ADR 0016** |
| `MOT-12` — FieldWorks in-process adapter | M3 | Medium | Not started |
| `MOT-13` — `System.Text.Json` on `net48` proof | M3 | Small, and a possible blocker | Not started |
| `MOT-9` — Baseline Token, Dry Run binding, apply authorization, Receipt | M4 | Medium, correctness-critical | **Partly built** |
| `MOT-10` — Proposal revisions, Check Runs, Reviews, Decisions | M4 | Medium, the PR-like product core | Not started |
| `MOT-14` — Receipt store and sync in Lexbox | M4 | Medium | Not started |
| `MOT-6` — semantic + lowering layer for grammar construct 1 | M5 | Medium — **the first product family** | Not started |
| `MOT-15` — PanGloss snapshot producer and FFI | M5 | Medium | Not started |
| `MOT-7` — the remaining 29 constructs | M6 | Large | Not started |
| `MOT-8` — ordered-grammar review proof | M6 | Medium | Not started |

**Withdrawn:** `MOT-1` (the MiniLcm↔LibLCM crosswalk — not needed once the target is LibLCM) and
`MOT-5` (mapping ordered and reference kinds onto Harmony primitives — there are no Harmony
primitives in this path). Numbers are not reused.

**What already exists and is not re-planned.** `manifest/liblcm-inventory.tsv` — 898 rows, 19 columns,
473 in-scope rows across 95 in-scope classes, 100% classified for every in-scope row. The
HCLoader-derived grammar map and the coverage research are done. `SIL.Motif.{Contract,Model,Runner}`
build, `Runner` multi-targets `netstandard2.0;net10.0`, and 82/82 tests pass — including a working
`open` / `new` / `add-set-gloss` / `finalize` / `dry-run` / `apply` / `log` CLI loop for one operation
kind.

---

## `MOT-2` — the join, failing the build — M1

Structure comes from `MasterLCModel.xml` so it tracks LibLCM upgrades; policy (`Scope`, `Construct`,
`ComparisonClass`, `Verbs`, `AssessPoisonsCache`) comes from the manifest, which is human judgement
and exists nowhere else. They join on `(Class, Field)`, and **a key present in one and absent from the
other fails the build.**

The key set has been checked, not assumed: 445 `<basic>` + 235 `<owning>` + 218 `<rel>` = **898**
field declarations in `MasterLCModel.xml` (424,797 bytes, 5,368 lines, model version `7000072`, 193
classes), against 898 manifest rows, with **zero keys present in one and absent from the other and no
duplicates in either**. A matching count alone would not have shown that.

**Acceptance**

- An injected extra `(Class, Field)` key on either side fails the build with a message naming the key.
- A LibLCM upgrade that adds a field produces a row with structure and no policy, and the build stays
  red until a human classifies it. **For a system where a wrong policy corrupts a language project
  quietly, visible churn beats minimal churn** — that is the intent, not a side effect.
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

**Acceptance:** the generator loads all 898 joined rows, reports its own coverage, and runs in CI
without a liblcm source tree.

## `MOT-4` — emit the operation catalog for one family — M2

The output side of the gate, and the point where this plan diverges most from its predecessor.
**Target LibLCM objects, not MiniLcm types.** Emit, per in-scope field of the chosen family:

- the enumerated `kind` string, `{group}/{construct}/{verb}{Noun}`, one per field — never a runtime
  field-name parameter;
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

**What passing does not license.** The possibility-list family is 37 in-scope rows: 34 `unordered`, 3
`positional`, zero `feeding`, zero `index-as-identity`, zero `AssessPoisonsCache=yes`. It licenses the
mechanical majority and says nothing about the ordered-grammar minority.

## `MOT-11` — scratch-cache DryRun — M2

Implement [ADR 0016](adr/0016-scratch-cache-copy-not-undo.md): one expensive `CreateCacheCopy` from
the live cache into a `kMemoryOnly` pristine scratch, cheap surrogate-level fan-out from that scratch
per dry run and per PanGloss run, and a live-cache footprint probe gating re-copy. Apply stays on the
live cache.

**Deliverables**

1. The scratch lifecycle, including a prerequisite-DAG mode that applies a topologically-sorted
   closure of un-applied Proposals to one derived scratch.
2. **Two measurements, before the design is built on**: `CreateCacheCopy` from a hot Sena-3-scale
   cache into `kMemoryOnly`, and a derived copy from a pristine scratch. The whole value of the
   pristine-scratch layer is the ratio between them, and both are currently asserted from the code
   path rather than measured. `CreateCacheCopy` has zero callers in liblcm or FieldWorks.
3. Retirement of `CacheReusability`, `RollbackCacheInvalidator`, and
   `DerivedCachePoisoningOperationKinds` once the scratch path is the only dry-run path.

**Acceptance:** a dry run never mutates the live cache; a poisoned scratch costs a rebuild, not a
session; the DAG closure produces the same effects as applying the closure serially.

## `MOT-12` — FieldWorks in-process adapter — M3

The `net48` seam. Marshal to the UI thread, pass FieldWorks' own `LcmCache`, supply the applier
identity, call `Save`, invalidate the parser and UI. The Runner is already `netstandard2.0` and takes
a cache it does not own, so no Runner API changes.

**Acceptance** is Gate 1 from [fieldworks-crdt-integration-research.md](fieldworks-crdt-integration-research.md):
one lexical operation previewed and applied through a FieldWorks-owned surface, on the UI thread, as
one undoable UOW, rejecting stale or wrong-type targets, replayable idempotently, surviving
save/reload, and reconciling a crash before and after save.

## `MOT-13` — `System.Text.Json` on `net48` — M3

FieldWorks has **no STJ reference today** — it uses `Newtonsoft.Json 13.0.4`. `SIL.Motif.Contract`
pulls STJ 8.0.5 plus six transitive `System.*` packages into a runtime where binding redirects are
historically painful, and FieldWorks' `Directory.Packages.props` already carries a scar in exactly
that area (`System.Memory 4.6.3`, pinned with a comment about a ParatextData conflict).

Mitigating: FieldWorks sets `AutoGenerateBindingRedirects` and `GenerateBindingRedirectsOutputType`.

**Do not resolve this by using Newtonsoft on `net48`.** RFC 8785 canonical bytes must be identical
across runtimes or every intent and effect digest diverges between FieldWorks and the CLI — that is
[ADR 0007](adr/0007-cross-language-digest-determinism.md)'s entire subject. Same JSON stack
everywhere.

**Acceptance:** a `net48` host loads the Contract and computes an intent digest byte-identical to the
`net10.0` CLI's, for a fixture Proposal.

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

## `MOT-14` — Receipt store and sync — M4

Receipts must be durable and shareable. The applied log is thin by design —
`(proposalId, formatVersion, timestamp, user, intentDigest, description)` — it records *that*
something applied, not what it did. The effects live in the `Receipt`, which today is returned and
never durably stored.

**Lexbox is the home.** It already has organisations, projects, users, and a permission service.
Proposals and Receipts are immutable, content-addressed documents with frozen identities, so they need
an object store and an HTTP API, not a merge engine — no CRDT is required to share them. Sharing is
**optional per project**; a linguist working alone is never obliged to publish.

Review state — comments, approvals, decisions — is mutable, and is an ordinary server database unless
offline review becomes a requirement.

**Acceptance:** a Proposal authored on one machine is visible, with its Receipt and effect digest, to
a permitted collaborator on another; an unshared project never leaves the machine.

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

Sequencing is decided by [ADR 0012](adr/0012-build-order-hc-spine-first-kinds-generated.md): of 150
HermitCrab-reachable in-scope fields, **113 are grammar and only 32 lexical**, so grammar leads. L0
(the ~37 non-grammar fields HCLoader actually reads), then G0–G2, then the lexical backfill driven by
non-HermitCrab consumers rather than by the parser.

**Known blockers that are not generator work:** L0's object-creation closure is uncomputed (B21), and
roughly 300 of 473 in-scope rows were classified by heuristic rather than by citation (B17, B18) —
which matters more under generation, because the generator reads those classifications directly.
Verify-lazily versus dedicated-audit is undecided.

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

## Cross-links

- The decision this plan implements: [harmony-adoption-report.md](harmony-adoption-report.md)
- Work in other repositories: [plan-cross-repo.md](plan-cross-repo.md)
- What was withdrawn from LcmCrdt: [plan-lcmcrdt.md](plan-lcmcrdt.md)
- Product scope and phases: [motif-overall-plan.md](motif-overall-plan.md)
- Architecture: [plan-product-architecture.md](plan-product-architecture.md)
- ADRs: [0016](adr/0016-scratch-cache-copy-not-undo.md) ·
  [0014](adr/0014-generate-the-crdt-layer-from-masterlcmodel.md) ·
  [0012](adr/0012-build-order-hc-spine-first-kinds-generated.md) ·
  [0007](adr/0007-cross-language-digest-determinism.md) ·
  [0006](adr/0006-engine-reality-apply-readback-preflight.md)
- Open issues named above: [issues.md](issues.md) (B17, B18, B19, B20, B21)
- Open questions: [grill-plan-a.md](grill-plan-a.md)
