# ADR 0021 — The CLI is the full product surface; Layer 1 churns, Layer 0 does not

**Status:** accepted, 2026-08-05. Refines [ADR 0020](0020-cli-first-fieldworks-planned-not-built.md)'s
scope 1 and constrains [ADR 0009](0009-layered-api-primitives-and-composers.md)'s Layer 1. Sets the
`J43` removal rule.

## Context

ADR 0020 made scope 1 "prove the LibLCM seams through the CLI with an AI agent as author." That left the
CLI's *breadth* unstated, and the owner's framing settles it:

> *Basically "what will be in FieldWorks, but AI will be doing it all through the CLI" — the same lists,
> diagnostics, reports, summaries, actions, workflows, everything, but through text and JSON. It should
> be fully transferable to FieldWorks, but faster to iterate on.*

And on the authoring API:

> *They are a contract that can change. We will figure it out as we go along, and churn is fine,
> especially before we pull in FieldWorks. We WANT refinement — "oh, this command would be helpful",
> "let's merge these two batch operations", "I really need to see this report, which would make 4 API
> calls 1".*

Both are right, and together they create one hazard: **"churn is fine" is true of the agent-facing
surface and false of the hashed contract.** If churn reaches the operation vocabulary or the canonical
JSON form, every stored intent and effect digest changes meaning, which is
[ADR 0007](0007-cross-language-digest-determinism.md)'s entire subject. This ADR draws that line while
the churn is still cheap.

## Decision

### 1. The CLI is the whole product, rendered as text and JSON

Not an authoring tool with a dry run bolted on. The full surface — entity and proposal **lists**,
**diagnostics**, **reports**, **summaries**, **actions**, **workflows** — is CLI-first, with the AI agent
as its first user. FieldWorks later renders the same material to a human.

### 2. Transferability is structural, not aspirational

"Fully transferable to FieldWorks" only holds if the CLI is a *renderer*, never the place the answer is
computed:

```
      query / projection layer          ← where lists, diagnostics, reports, summaries live
        │                    │
   CLI renderer         Avalonia view models        ← two thin renderers, one source
   (text + JSON)        (scope 2)
```

Every report must be reachable as structured data with the text form a formatting pass over it. This is
the same rule the FieldWorks integration research already sets from the other side — *keep UI projects
LCModel-free; put projectors in the integration layer.* A report whose logic lives in a CLI command
handler is a report FieldWorks has to rebuild, and it will not be rebuilt identically.

The tell that this is being violated: a summary that can be printed but not emitted as JSON.

### 3. Three tiers, not two — and only the hashed tier is constrained

The owner's second condition draws the line more usefully than "Layer 1 versus Layer 0" does:

> *Some API surface will be AI-only, and they only care what is now — they will query the API for what
> is current.*

That is a genuinely **stateless** tier. Nothing is stored, so nothing can go stale, so it carries no
compatibility obligation at all:

| Tier | Churn | Why |
| --- | --- | --- |
| **Ephemeral read** — lists, diagnostics, reports, summaries answering *"what is true now"* | **Free, permanently** — not just pre-FieldWorks | Nothing is stored and nothing replays. The agent asks again. There is no old response to keep valid. |
| CLI verbs, flags, output shapes | **Free** while FieldWorks does not depend on them | Nothing hashes them. |
| Layer 1 composers — batch reads, batch updates, multi-rule creation, composite reports | **Free and wanted** | "Four API calls become one" is the refinement loop working. Composers resolve to operations; the *resolution* is what gets stored, not the composer. |
| Layer 0 operation `kind` strings and payload schemas | **Constrained** | Hashed into `intentDigest`. |
| Canonical JSON form, digest algorithm, effect tuple | **Frozen** | ADR 0007. A change here invalidates every stored digest in every runtime. |

The boundary is mechanical: **if a change alters bytes that get hashed, or invalidates something already
stored, it is not churn.** A new report is free; a new composer is free; a new `kind` is additive and
minor-safe under `B9`; renaming an existing `kind` is not churn at all.

**The one place the ephemeral tier stops being ephemeral.** A query result cited as *evidence* in a review
is no longer a question about now — it becomes a **Check Run**, and inherits `MOT-10`'s exact-input and
stale-binding rules: any change to the Proposal, baseline, artifact, tool contract, or policy revision
invalidates it. So the same computation has two modes, and the transition is explicit:

```
agent asks "what is true now?"        → ephemeral. Free to churn. Nothing retained.
that answer is attached as evidence   → Check Run. Bound to exact inputs, invalidated by drift.
```

Getting this wrong in either direction is expensive: a churning surface that silently backs Check Runs
produces approvals bound to reports nobody can reproduce, and treating every agent query as durable
evidence would make the read surface unchangeable for no benefit.

### 4. The churn is a design instrument, so instrument it

> *The churn will help us determine which UI screens would be most helpful for looking at stuff.*

This makes CLI iteration **evidence for scope 2's screen list**, not merely a faster way to build scope 1.
Which reports the agent actually calls, how often, and in what sequence is the closest thing to a
requirements document FieldWorks will ever get — and it is free if it is captured, and gone if it is not.

So: **log the surface's own usage** — command, arguments' shape (not their content), call counts, and
which reports get called together or in immediate succession. Two consumers that reliably run
back-to-back are a candidate composite report; a report the agent calls constantly is a candidate screen;
a report nobody calls after a month is a candidate deletion. Retrospective guessing cannot recover any of
that.

This is cheap, local, and needs no telemetry infrastructure — a session-local log the owner can read.
It carries no project data, so it raises no privacy question.

### 5. Churn's one honest cost — stored Proposals

An unstable Layer 1 is free. An unstable Layer 0 is not, because `MOT-9`'s whole premise is that a
Receipt binds what was approved. During the churn window, a Proposal authored last month may not replay.

**Accepted for scope 1:** stored Proposals are **not guaranteed portable across the churn window**, and
regeneration is the remedy. This is tolerable only while the author is an agent that can re-author on
demand and there is one machine. It must end before a human's approval is recorded against a stored
Proposal, and certainly before scope 2. `B9`'s versioning policy applies from the first
declared-stable version; until then the intent surface is **unstable by declaration**, and it says so in
its own contract-version metadata rather than by omission.

### 6. Removing operations warns, and cascades require force — `J43`

The owner's requirement:

> *We should be able to remove specific items piece by piece — "don't add that lexeme", "only rules 1
> and 4, not 5." Obviously there are dependencies and those should be warned — say, need a force if
> there are other deletes that happen from deleting an item.*

So the rule is neither "refuse" nor "silently cascade":

1. **Removal is per-operation**, addressable individually — this is also a core FieldWorks review
   workflow, not a CLI convenience.
2. **A removal with no dependents just happens.**
3. **A removal that severs a `requires` edge or orphans a dependent operation warns and names every
   consequence**, then requires an explicit force to proceed.
4. **Force never means "guess."** It means the caller accepted an enumerated consequence set. If the
   consequences cannot be enumerated, the removal is refused, not forced.
5. `proposalId` stays frozen; the intent digest moves. Removal produces a new revision, never a mutation
   of an approved one.

## Consequences

- **`MOT-16` splits.** The session mechanism (warm cache, one project load) and the surface itself are
  different work; the surface becomes `MOT-19`.
- **`J43` is decided** (decision 6) — warn, enumerate, force; refuse only when consequences are not
  enumerable. `J44` (the unit of splitting) is answered as *the individual operation*, subject to
  `requires`.
- **A usage log is now a deliverable of `MOT-19`**, not a nice-to-have: decision 4 only pays off if the
  data exists when scope 2 starts choosing screens.
- **`MOT-17` is explicitly allowed to churn**, and that is recorded as intent rather than tolerated as
  drift — with the Layer 0 boundary in decision 3 as the guard.
- **A new obligation on every report:** JSON emission is part of the definition of done, not a later
  `--json` flag. Otherwise decision 2 is violated silently.
- **A new grill item, `B9b`:** when does the intent surface declare itself stable, and what is the exit
  criterion for the churn window? Recommended trigger: the first human approval recorded against a
  stored Proposal.
- Risk accepted: a fast-churning CLI surface can accumulate incoherent verbs. The mitigation is that the
  agent is the primary consumer and can be re-pointed cheaply — but a periodic consolidation pass is
  cheaper than a rename after FieldWorks depends on it.
