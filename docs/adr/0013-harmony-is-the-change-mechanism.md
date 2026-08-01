# ADR 0013 — Harmony is the change mechanism; Motif's contract layer is redundant

Status: accepted (2026-07-27)

**Reverses the central premise of [ADR 0011](0011-experiment-loop-boundary-motif-is-the-record.md)
and [ADR 0012](0012-build-order-hc-spine-first-kinds-generated.md), and supersedes the
"Motif is the one change-authorship API" recommendation in
[minilcm-evaluation.md](../minilcm-evaluation.md), [one-api-problem.md](../one-api-problem.md), and
[three-paths-report.md](../three-paths-report.md).**

## Context

Every prior document in this repository was written without anyone having read
`SIL.Harmony` — the CRDT/commit-log engine underneath `LcmCrdt`. It was not checked out locally. A
research subagent flagged it as "could not verify," and rather than obtaining it, the analysis
reasoned around the gap and continued. One report explicitly *downgraded* that caveat.

That was the methodological error that invalidates the earlier conclusions. Harmony has now been read
(`C:\…\repos\harmony`, HEAD `c858cb4`).

## The burden of proof, correctly stated

The repository owner's framing, which this ADR accepts:

> Motif is adding a new method of change-set management to a complex system that already has two
> supported methods of doing that (UOW in liblcm, and Commit in harmony). Adding a third would need
> to provide a very strong case that the existing methods are not up to the task to justify its
> existence.

Motif does not meet that bar.

## Evidence — what Harmony already provides

| Capability | Harmony | Motif's `Contract`/`Model` |
| --- | --- | --- |
| Semantic change objects, never raw property mutation | `Changes/Change.cs` — `IChange` / `Change<T>` | operation envelopes |
| Polymorphic JSON with `$type` discrimination | `Changes/PeekThenConcreteChangeConverter.cs` | `Parsing/OperationKindRegistry.cs` |
| Hash-chained history | `Commit.cs` — `Hash`, `ParentHash`, `SetParentHash` | `Canonicalization/IntentDigest.cs` |
| Per-object snapshots | `Db/ObjectSnapshot.cs`; `DataModel.GetSnapshotsAtCommit` | `Model/ObjectSnapshot.cs` |
| State at, or before, any commit | `DataModel.GetAtCommit`, `GetBeforeCommit`, `GetAtTime` | `Model/ExpectedEffect.cs` |
| Commit validation | `DataModel.ValidateCommits` | `Assessment` |
| **Carrying changes the client cannot interpret** | **`Changes/OpaqueChange.cs`** — preserves raw JSON, round-trips, and *becomes a real change once the type is known* | — (proposed in `one-api-problem.md` as novel; it already existed) |

`GetBeforeCommit` + `GetAtCommit` means "what did this change do" is already computable. The change
envelope, digest scheme, snapshot model, and operation-vocabulary machinery in Motif are
reimplementations of shipped, maintained, tested equivalents.

## Corrections to earlier claims in this repository

1. **"Two stores means two truths."** False as stated. `LcmCrdt/CrdtProjectsService.cs` —
   `CreateProject` / `CreateProjectFromTemplate` — creates CRDT-native projects with no `.fwdata`
   equivalent at all. The CRDT store is a first-class source of truth, not a cache in front of LibLCM.
2. **"You cannot build one tool with both concurrency models."** False. Google Docs suggestion mode is
   propose → review → accept inside a live collaborative editor. A real distinction was overstated
   into a false impossibility, and it was load-bearing for the "two rings" recommendation.
3. **"CRDT sync is not a Chorus replacement."** `CrdtFwdataProjectSyncService`'s hand-written
   reconciliation was cited as evidence of a permanent architecture. It is a concession to the fact
   that not all change classes exist yet. CRDTs are intended to replace Send/Receive.
4. **Layering.** How changes are applied, and when others' changes are integrated, are *system-level*
   integration concerns. Harmony provides the mechanism for applying changes to the model. Motif
   conflated the change vocabulary with the integration policy.

## Decision

1. **Harmony's `Commit`/`IChange` is the change mechanism.** Motif does not ship a competing change
   format, digest scheme, snapshot model, or operation registry.
2. **Review/approval is an application-level state machine over Harmony commits**, not a new change
   format. `OpaqueChange` already provides the forward-compatibility property that motivated the
   "opaque synced payload" proposal.
3. **Motif's contract layer is not extracted, not published as a schema, and not split into its own
   repository.** The sequencing proposed in the preceding conversation (move two files → publish
   schema and vectors → split repo) is cancelled at step one.

## What survives, and is worth keeping

Not a mechanism — three analysis artifacts, which are the expensive part and remain valid:

1. **The coverage manifest** (`manifest/liblcm-inventory.tsv`) — 898 rows, 473 in scope, 100%
   classified; grammar 230 / lexical 157 / lists 39 / system 47.
2. **The HCLoader-derived grammar map** (`docs/hc-grammar-map.md`, `docs/api-surface-hc.md`) — what
   FieldWorks actually reads, which is what any grammar change classes must cover.
3. **The finding that ordered grammar breaks scalar-order CRDTs**, with mechanisms named: feeding /
   bleeding rule order, index-as-identity alpha variables (24-per-rule ceiling), and
   `MoAffixProcess.Output` resolving against `Input` by position.

Artifact 3 is not an argument for Motif. It is a **defect report against Harmony**:
`Changes/SetOrderChange.cs` merges order as a last-writer-wins scalar, and if CRDTs are the
destination for grammar, that must be solved in Harmony.

## Consequences

- The one shipped operation (`lexical/sense/setGloss`), the Runner, the Host, and the CLI are no
  longer on a path to being the API. Whether any of them survives in another form — in particular
  whether a CLI belongs in Harmony — is deliberately left open here.
- ADR 0011's "Motif is the record" and ADR 0012's build order and generated-kind namespace are moot
  as stated: they describe how to grow a mechanism this ADR declines to build.
- The open issues that gated that mechanism (B13 cross-process protocol, B19–B23) are moot with it.
  B17/B18 (manifest classification confidence) survive, because the manifest survives.
- **The unresolved question that dominates all of this: who maintains Motif.** 40 commits, one
  operation, no CI, one author, against Harmony and MiniLcm's team, CI, and release train. If the
  answer is "one person, part-time," the analysis-artifacts framing above is the only version of this
  repository that survives contact with reality.

## Status of the preceding reports

`minilcm-evaluation.md`, `one-api-problem.md`, `path-1`/`path-2`/`path-3`, and
`three-paths-report.md` remain in the repository as a record of how the conclusion was reached and
where it went wrong. Their **platform findings stand** — Linux is already solved; the mobile boundary
is real; the custom-ICU normalization divergence (`liblcm` `CustomIcu.cs:224-247,409-419`) is a
genuine hazard for any system, and is *not* recorded in `issues.md`. Their **architectural
recommendations do not stand**, and are superseded by this ADR.
