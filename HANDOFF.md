# Handoff for the implementation session

> **STOP — this handoff is obsolete as of 2026-07-27.**
>
> Read [ADR 0013](docs/adr/0013-harmony-is-the-change-mechanism.md) first. The plan below describes
> building out a change-set contract and operation catalog. **That work is cancelled.** Harmony's
> `Commit`/`IChange` already provides the mechanism, and LibLCM's unit of work is the second; a third
> is not justified.
>
> Do **not** implement the phases, stages, or catalog below. They are retained as a record of what
> was planned before `SIL.Harmony` was read.
>
> **For the live plan, read [docs/plan-cross-repo.md](docs/plan-cross-repo.md)** and the three plans it
> aligns: [Plan A](docs/plan-motif.md), [other repositories](docs/plan-cross-repo.md),
> [LcmCrdt](docs/plan-lcmcrdt.md). Two of the questions this handoff left open are now answered there:
> what additions Harmony needs to carry grammar (`HAR-*`), and where each artifact lands
> ([ADR 0014](docs/adr/0014-generate-the-crdt-layer-from-masterlcmodel.md), D6). Still open: **who
> staffs any of it** — D6 says where code lands, not who writes it.
>
> The CLI question is settled: it is `motif`, it belongs in this repository, and it does **not** belong
> in Harmony (grill-decisions D7).

---

Continue implementing the Motif plan in this repository.

Start by reading, in order:

1. `README.md` (see its Status section for the current headline)
2. `AGENTS.md`
3. `docs/build-stages.md` — what is built and verified, stage by stage
4. `docs/implementation-plan.md` — per-phase status against the full plan
5. `docs/operation-catalog-plan.md` — the roadmap from the one shipped operation to lexical + grammar
   completeness
6. the rest of `docs/`, including `docs/issues.md` for open and fixed issues

**The repository is not a blank plan.** Stages A–E of the walking skeleton
([build-stages.md](docs/build-stages.md)) are complete and verified: 82/82 tests pass
(`dotnet test`) against a real `LcmCache` opened on a real copied FieldWorks project. A change set can
be authored, assessed, applied atomically, read back, and logged end to end through the real
`Contract`/`Model`/`Runner`/`Host`/`Cli` projects. The catalog is exactly **one operation deep**
(`lexical/sense/setGloss`) — no create, delete, sequence, or grammar operations exist yet, and there
is no HermitCrab projection code at all. Treat the prose contract as normative. Do not redesign
settled decisions unless source evidence makes one impossible; if that happens, report the exact
evidence and propose the smallest amendment.

Do not restart at Phase 0/1. Phase 1 (contract kernel) is done. Phase 0 is **partial**: everything
needed to run has been proven except item 3, the multi-target compatibility spike — nothing in this
repo multi-targets today (see the per-project TFM table in `docs/implementation-plan.md`'s Phase 0
status and `AGENTS.md`'s Compatibility targets). Phases 2–4 are each partial in different, specific
ways — read `docs/implementation-plan.md`'s per-phase status markers before assuming what's left,
rather than guessing from the numbered requirements alone.

**Five decisions were settled on 2026-07-27** and are recorded in
[ADR 0011](docs/adr/0011-experiment-loop-boundary-motif-is-the-record.md) and
[ADR 0012](docs/adr/0012-build-order-hc-spine-first-kinds-generated.md). Read both before planning
anything; they amend ADR 0010 and supersede parts of `hc-surface-scope.md`, `stage2-change-management.md`,
and `operation-catalog-plan.md`:

1. **Motif is the record, not the orchestrator.** It exports `.fwdata` and receives labelled reports
   and typed metrics back. It never runs the parser and never renders a verdict.
2. **Forward HermitCrab projection is deleted** — PanGloss reads `.fwdata` directly. Reverse `Expand`
   survives as the primary grammar authoring surface.
3. **Export is hypothetical**, applying N change sets to a scratch copy; the real project is untouched.
4. **Build order is L0 → G0–G2 → backfill L1–L5**, where L0 is the ~37 non-grammar fields `HCLoader`
   actually reads.
5. **Kinds are generated from the manifest from day one** — 332 for the HC surface, 915 in total,
   against ~12 hand-written type handlers.

**Two decisions remain genuinely open** and are not yours to resolve by default:

- **The cross-process protocol** for non-.NET consumers (framing, error/exit-code contract,
  one-shot-vs-daemon) — issue B13. Two constraints are now fixed: generated kinds force a generic
  change-set-JSON path, and scratch-copy export weakens the daemon's exclusive-write case while
  strengthening its cache-reuse case (measured: 3.6s warm cache load on a 61-entry project, against
  0.05s to copy the project).
- ~~**Manifest classification confidence**~~ — **closed; this bullet was stale.** B17 was corrected
  2026-08-03 (the guidance it said did not exist is at `MasterLCModel.xml:3578-3584`) and B18 was
  largely retired 2026-08-05 by [ADR 0022](docs/adr/0022-structure-is-derived-policy-is-five-rows.md),
  which made `Verbs` and `ComparisonClass` derived so a missing citation on a computed value stopped
  being a risk. What actually survives is narrower and is now measured rather than estimated: **64
  in-scope rows claim order carries meaning, and 32 of them rest on `card=seq` alone** — see
  `manifest/ordering-evidence.tsv` and
  [the census](docs/research/2026-08-11-ordering-claims-census.md). Those 32 are a bounded review,
  not an audit.

Four new issues fall out of the settled decisions and block the generator or L0: **B19** (construct
naming is not mechanical), **B20** (17 multi-construct rows), **B21** (L0's object-creation closure is
uncomputed), **B23** (attachment/metric config surface unspecified).

Work test-first and make small, reviewable commits. Before changing files, inspect the current
branch/status and the pinned/current LibLCM package surface. Use an isolated branch/worktree for
non-trivial work.

Non-negotiable constraints:

- semantic closed CRUD+ operations, never arbitrary reflection/property/script input;
- caller supplies and owns an already-loaded `LcmCache`;
- the whole Change Set is one atomic LibLCM unit of work;
- operation array order is authoritative;
- 22-character unpadded base64url 128-bit suffix IDs with textual/network-order GUID mapping;
- RFC 8785 + SHA-256 canonical intent and semantic hashes;
- Assessment and Application Receipt are separate from portable intent;
- a later saved-file digest is a separate Artifact Attestation, not a mutation of the Receipt;
- output-only LibLCM Mutation Plan;
- exact-ID, linguistically unaware mechanical diff;
- LibLCM NFD/NFSC normalization;
- custom fields resolve by `(ownerClass, internalName)` and never serialize project-local `flid`;
- generated 100%-classified LibLCM model coverage manifest;
- storage, review, Git/database, permissions, UI, and fuzzy matching remain out of core scope.

An explicit storage-GUID override requires the caller to retain and later supply the
canonical-to-storage identity mapping; LibLCM does not persist it. Reassessment preserves intent;
an authored anchor rewrite is an explicit rebase producing a new Change Set and digest.

One question remains intentionally open: whether a namespaced logical contract key for shared
custom fields deserves a named v1 property. For now keep such keys in non-semantic `extensions`;
do not make them part of resolution or hashing without an explicit design decision.

The transaction boundary (Phase 0 item 5) and the contract kernel (Phase 1 — schemas, canonical ID
utility, canonical JSON/intent hash, diagnostics/Assessment/Receipt DTOs, fixed conformance fixtures)
are both delivered and covered by tests. The one Phase 0 gate still open is the multi-target proof
(item 3) — decide, with the repository owner, whether to close it now or defer it to Phase 9 (which
already carries the `net48` FieldWorks adapter as a release-time obligation) before investing further
in broad domain CRUD.

At each checkpoint, report:

- files and behavior added;
- tests run and exact results;
- coverage against the phase exit criteria;
- any discrepancy between the plan and actual LibLCM behavior;
- the next smallest implementation slice.

---
