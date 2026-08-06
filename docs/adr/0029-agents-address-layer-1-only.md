# ADR 0029 — Agents address Layer 1 only; an unreachable field is a requirement, not a gap

**Status:** accepted, 2026-08-06. Resolves `J41` by making it concrete. Scopes `MOT-17`. Builds on [ADR 0009](0009-layered-api-primitives-and-composers.md) and
[ADR 0021](0021-cli-is-the-full-surface-layer-1-churns.md).

## Context

Around 480 field-level operations are coming. An agent cannot be handed 480 tools: tool selection degrades
well before that, and Anthropic's own guidance treats namespacing as the thing that keeps selection
unambiguous as a surface grows. So the agent-facing surface has to be something other than "one tool per
field", and the question nobody had asked is **what**.

ADR 0009 split the vocabulary into Layer 0 primitives and Layer 1 composers, and ADR 0021 said Layer 0 is the
diff's *output* vocabulary while Layer 1 is the agent's *input* vocabulary. Taken literally that answers the
tool-surface question — the surface is the number of composers, not the number of fields — but it left an
escape hatch open: what happens when an agent needs a field no composer covers?

## Decision

### 1. The agent's vocabulary is Layer 1. There is no generic field-level escape hatch

No `set this arbitrary field` tool. If no composer expresses what the agent needs, the agent cannot do it.

### 2. An unreachable field is a signal that Layer 1 is incomplete

The owner's framing, and the reason decision 1 is not merely a restriction:

> *Layer 1 only. When we can't reach a specific field we need, it exposes a need to expand Layer 1.*

This inverts what would otherwise be a cost. A generic escape hatch would let the agent route around a missing
composer, and the routing would be invisible — the surface would look complete while the semantic vocabulary
Motif is named for quietly failed to materialise. Without the hatch, the agent hitting a wall **is the
requirement being discovered**, at the moment and in the words of whoever needed it.

### 3. The agent closes the gap itself, in this repository

*Revised 2026-08-06, before implementation.* This decision originally required logging refused requests so a
human could later read the queue. **That was wrong, and it imagined a deployment that does not exist.** It
pictured an agent as a runtime consumer hitting an API, being refused, and filing a request. The owner's
correction:

> *Logging refusals for Layer 0 makes no sense — the AI agent will not see it. The agent will have access to
> this repository while it is being built up, and propose and add more to Layer 1. And the same when we are
> building up FieldWorks.*

The agent is not petitioning for a capability; it is **a contributor that adds one**. When it needs a field no
composer covers, it writes the composer — with its description — as an ordinary reviewed change in this repo.
The signal is a commit, not a log entry, and git history is already the record.

This makes decision 1 **cheaper rather than costlier**: the reason no escape hatch is needed is that the wall
is trivially removable by whatever hit it. A missing composer is a half-hour of work by the party that
discovered the need, not a ticket waiting on someone else's sprint.

**And the same pattern holds in scope 2.** When the FieldWorks surface is built, the agent contributes to it
rather than merely consuming it — so "the agent cannot do X yet" is a statement about what has been written so
far, never about what is reachable in principle.

**Where the discipline actually lives, therefore: code review.** Nothing mechanical stops a broad
`updateLexEntry(field, value)` composer that is the escape hatch in disguise. What stops it is a reviewer
declining it, which makes the note in this ADR's consequences a **review criterion for agent-authored
composers** rather than general advice.

### 4. Descriptions have two audiences, and now it is clear which is which

[ADR 0023](0023-derived-kind-names-required-descriptions.md) requires a description per kind and says it exists
"for the human reviewing the manifest and approving a Proposal, not for the agent." Decision 1 makes that
literal rather than aspirational:

| | Read by | Says |
| --- | --- | --- |
| **Layer 0 kind** description | the human reviewing a Proposal | what this field change means |
| **Layer 1 composer** description | the agent choosing an operation | when to reach for this |

Both are required and neither substitutes for the other. Anthropic's guidance that description drives tool
performance applies to the composer descriptions specifically — those are the ones in a `tools` array.

## Consequences

- **`MOT-17` is composers, not a field-level API.** Its scope narrows and clarifies: batch reads, batch
  updates, multi-rule creation, composite reports — each one a named semantic operation with a description
  written for an agent. The at-rest form remains resolved Layer 0 operations with the query as non-hashed
  provenance (ADR 0009 §1).
- **`MOT-19` gains nothing from this ADR.** An earlier draft added a refused-request log; decision 3 withdrew
  it. ADR 0021 decision 4's usage log stands on its own merits — it records which reports the agent *uses*, to
  learn which FieldWorks screens are worth building — and that is a different question from what the agent
  cannot reach.
- **The long tail is deliberately unreachable until someone writes a composer.** Accepted with eyes open: the
  agent cannot do routine field-level data entry, and "set the bibliography" needs a composer before it needs
  an agent. The trade is that every capability is deliberate, described, and reviewable, and that the pressure
  to add one arrives as evidence rather than as a guess.
- **The one review criterion this creates**, and it is the whole enforcement mechanism: a broad composer that
  is the escape hatch in disguise — `updateLexEntry(field, value)` in composer's clothing — defeats decision 1
  while passing its letter. **A composer must name an intent a linguist would recognise, not a mechanism.**
  Since the agent writes composers (decision 3) and nothing mechanical can check this, it belongs on the review
  checklist for any new composer, agent-authored or not.
- **Risk accepted, and it moved:** the risk is no longer a long queue of unmet requests. It is that Layer 1
  grows *fast*, one composer at a time, in whatever shape each need suggested — so the vocabulary could sprawl
  before anyone looks at it whole. The mitigation is the review criterion above plus a periodic consolidation
  pass, which is cheap while the contract is declared unstable (`B9b`) and expensive after.
