# ADR 0029 — Agents address Layer 1 only; an unreachable field is a requirement, not a gap

**Status:** accepted, 2026-08-06. Resolves `J41` by making it concrete. Scopes `MOT-17` and constrains
`MOT-19`. Builds on [ADR 0009](0009-layered-api-primitives-and-composers.md) and
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

### 3. So the wall has to be instrumented, or the signal is lost

A refusal that just errors wastes the information. When an agent asks for something no composer covers, the
system records **what was wanted** — the field or the intent as expressed — and surfaces it as a queue of
Layer 1 gaps.

This is the same instrument ADR 0021 decision 4 already requires for a different purpose: logging which
reports the agent actually calls, to learn which FieldWorks screens are worth building. One usage log, two
questions — *what does the agent use* and *what does the agent reach for and fail to find*. The second is the
more valuable of the two, because it is a requirement rather than a preference.

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
- **`MOT-19` gains a required output**: the refused-request log from decision 3. It is cheap now and
  unrecoverable later — a refusal not recorded is a requirement lost.
- **The long tail is deliberately unreachable until someone writes a composer.** Accepted with eyes open: the
  agent cannot do routine field-level data entry, and "set the bibliography" needs a composer before it needs
  an agent. The trade is that every capability is deliberate, described, and reviewable, and that the pressure
  to add one arrives as evidence rather than as a guess.
- **A discipline this creates, stated so it is not discovered as drift:** the temptation will be to write one
  broad composer that effectively *is* the escape hatch — `updateLexEntry(field, value)` in composer's
  clothing. That defeats decision 1 while passing its letter. A composer should name an intent a linguist would
  recognise, not a mechanism.
- **Risk accepted:** early on, the agent will hit the wall often, and the queue will be long before it is
  useful. That is the cost of learning the vocabulary from use instead of inventing it up front, and it is
  cheapest now while the contract is declared unstable (`B9b`).
