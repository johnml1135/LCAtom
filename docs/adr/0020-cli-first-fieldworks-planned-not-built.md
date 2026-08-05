# ADR 0020 — Two scopes: prove the LibLCM seams through the CLI, plan FieldWorks without building it

**Status:** accepted, 2026-08-05. Re-sequences [Plan A](../plan-motif.md)'s milestones. Does not
change the delivery statement, the contract, or any decision in ADRs 0016–0019.

## Context

Plan A's milestone ladder put the FieldWorks in-process adapter at **M3**, between "one generated
family works" (M2) and "a Proposal is reviewed and applied" (M4). That ordering was inherited from the
Harmony-era plan, where reaching a modern runtime inside `net48` was the hard unknown and everything
else waited on it.

Three of the last session's decisions moved weight further toward FieldWorks —
[ADR 0019](0019-observed-intent-and-proposal-edit-mode.md) especially, which makes editing inside
FieldWorks the primary *human* authoring path. But ADR 0019 also carries an unverified premise
(`F26a`: does a usable seam exist in FieldWorks' command layer?), and it says so.

Meanwhile the thing that can be validated today, in this repository, with no other checkout and no
other team's review cycle, is the other half of the product: **an AI agent authoring Proposals through
the CLI against a live LibLCM model.**

## Decision

### 1. Scope 1 — the LibLCM seams, exercised by the CLI and an AI agent

Establish and prove, in this repository:

- the scratch-cache seam ([ADR 0016](0016-scratch-cache-copy-not-undo.md)) — one expensive copy, cheap
  fan-out, footprint-gated re-copy;
- generated operation kinds lowering to LibLCM and reading effects back;
- a long-lived CLI session that holds a warm cache instead of paying a project load per command;
- the higher, semantic authoring surface an agent uses ([ADR 0009](0009-layered-api-primitives-and-composers.md)
  Layer 1) plus its at-rest resolved form;
- Proposal editing — duplicate an operation, remove one, split a change set.

The acceptance question for scope 1 is: **can an AI agent, through the CLI alone, author a Proposal
against a real project, see its dry-run effects, and apply it — repeatedly, with drift refused?**

### 2. Scope 2 — FieldWorks integration is planned, not built

`MOT-12` (the in-process adapter) and `MOT-13` (the `System.Text.Json` `net48` proof) leave the
critical path. So do the `E19` Chorus experiment and the `F26a` command-layer spike. They remain fully
planned, and the potholes stay documented, because the point of planning them now is that scope 1 must
not make scope 2 more expensive.

**This is not a downgrade of ADR 0019.** Observed intent in a constrained proposal-edit mode remains
the intended primary human path. It is scheduled after scope 1 is shown to work, and after `F26a`
confirms the seam exists.

### 3. The obligations scope 1 must honour anyway

These cost nothing now and are expensive to retrofit, so they are not deferred with scope 2:

| Obligation | Why it cannot wait |
| --- | --- |
| `SIL.Motif.{Contract,Model,Runner}` keep targeting `netstandard2.0` | The only reason FieldWorks can host the Runner at all. Losing it is invisible until scope 2 starts. |
| One JSON stack, `System.Text.Json`, everywhere | [ADR 0007](0007-cross-language-digest-determinism.md). Canonical bytes must match across runtimes; the `net48` graph is already known clean (`A4`), so nothing is being deferred except the proof itself. |
| The Runner never loads, saves, or owns a cache | Written into `SIL.Motif.Runner.csproj`. It is what makes the FieldWorks host a drop-in. |
| Apply stays one LibLCM unit of work, and never calls `Save` | Save is the host's job. A CLI that saves inside apply would encode a CLI-only assumption. |
| Layer 0 stays the diff's output vocabulary; Layer 1 stays the agent's input vocabulary | `J41`. Scope 1 builds Layer 1 first, which is exactly when the split is easiest to blur. |
| The applied log keeps its Chorus caveat | `E19` is unresolved, not resolved. A single-machine CLI never triggers it; do not read that silence as evidence. |

### 4. Execution order

Milestone **ids are not renumbered** — they are referenced elsewhere. The order changes:

```
M1 → M2 → M4 → M5 → M6      (scope 1)
                 └─ M3 and the FieldWorks/Chorus spikes follow  (scope 2)
```

## Consequences

- **Three items are added** to carry scope 1's actual surface, which Plan A did not itemise: `MOT-16`
  (long-lived CLI session over a warm cache), `MOT-17` (Layer-1 semantic and batch authoring for
  agents), `MOT-18` (Proposal editing: duplicate, remove, split).
- **`A1` is the only spike on the critical path**, and it is in this repository. `A3` is its ~15-line
  prerequisite.
- **`B5` changes criterion.** M2's first generated family is now judged by whether an agent has a real
  reason to author it, not by which family is mechanically cheapest.
- **M4's review domain becomes AI-facing first.** `MOT-10`'s AI-actor rules — labelled actors, no human
  role satisfied by implication, versioned autonomous-approval policy — are scope 1 concerns now, not
  later ones, because the agent is the first author rather than the last.
- **`MOT-14` (Lexbox receipt store) is not required to validate scope 1.** A single machine needs
  durable receipts, not shared ones. Local durability first; sync when a second person exists.
- Risk accepted: scope 2 gets its first real integration feedback later. Mitigated by keeping the
  obligations in decision 3 as build-time invariants rather than intentions, and by `F26a` being spiked
  before any FieldWorks code is written.
