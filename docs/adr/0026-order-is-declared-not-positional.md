# ADR 0026 — Order is declared, not positional: dependencies in the DAG, canonical order for the rest

**Status:** accepted, 2026-08-05. **Amends `AGENTS.md` non-negotiable rule 5** and
`docs/change-set-contract.md`'s "authoritative execution order". Resolves `F23a`.

## Context

`F23`'s proposed rule 1 was *"one total order across all operations, byte-ordinal by canonical ID"*, so that
the same edit always produces the same intent digest and a diff is reproducible. That collided head-on with
two rules stated as non-negotiable:

```
AGENTS.md rule 5           "Operation array order is authoritative. Never silently reorder."
change-set-contract.md:46  "The operation array is authoritative execution order.
                            The runner never silently reorders it."
```

Both cannot hold for the same array. The owner's framing broke the deadlock:

> *The order matters in some places but not others. Adding something and then linking it, or deleting a link
> and an item. I am less worried about two proposals that do the same thing being applied sequentially than
> you are. I also agree that creating a diff should be consistent in general.*

Both examples are **causal dependencies** — create before link, unlink before delete — and Motif already has
an explicit mechanism for those: `requires` at the Proposal level and `dependsOn` per operation, both present
in the frozen conformance vectors. So position was never the right carrier for that information; it was
standing in for a dependency that should have been declared.

## Decision

### 1. A dependency is declared, never inferred from position

If operation B must follow operation A, B declares `dependsOn: [A]`. **The runner honours declared
dependencies and does not infer any from array position.** Create-then-link and unlink-then-delete are
declared, not implied.

### 2. There is no second ordering rule: same-slot operations are a normalisation defect

*Revised 2026-08-05, before implementation.* This decision originally said that operations sharing a target
keep their authored relative order, because two `set`s on one field are order-sensitive. The owner's
counter-argument is stronger and is adopted:

> *If two changes change the same field, that is a conflict. Changes are always added in order, so the last
> one would win, and the first one would be removed. So there is no ordering beyond dependency, because the
> first change is superseded by the second in the same proposal, meaning the first can be completely removed.*

That is right, and it generalises past `set`:

| Pair on the same slot | Collapse |
| --- | --- |
| `set` then `set` | keep the last — the first was never a separate fact |
| `set` then `clear` | keep the `clear` |
| `addRef(X)` then `removeRef(X)` | **remove both** — they cancel |
| several `move`s on one sequence field | one minimal set achieving the final arrangement — which is what the contract's LIS-minimal sequence diff already promises |

So **order is purely dependency**, and a Proposal containing two operations on the same slot is not an
ordering question — it is a Proposal that was never normalised.

**The slot is `(target, field, discriminator)`, not `(target, field)`.** This is the part that would break an
ordinary edit if it were got wrong, because some fields hold several independent values:

```
setGloss(sense, en, "run")  +  setGloss(sense, en, "sprint")   -> same slot, collapse
setGloss(sense, en, "run")  +  setGloss(sense, fr, "courir")   -> different slots, both legitimate
addRef(sense.domains, X)    +  addRef(sense.domains, Y)        -> different slots, both legitimate
```

The discriminator is the **writing system** for `Multi*` fields, the **member id** for collections and
reference sets, and **nothing** for scalars and atomic references. `SetGlossPayload` already parses
`(writingSystemTag, text)`, so the multilingual case is live with the single operation that exists today.

**Collapse automatically, and report it.** Unlike removing an operation — which changes authored intent and
therefore warns and requires force (`J43`) — collapsing two operations on one slot is provably
semantics-preserving: the applied outcome is identical either way. So normalisation happens at authoring or
finalize time, the stored Proposal is already canonical, and what collapsed is reported rather than silently
swallowed. A diff that emits same-slot duplicates has a **bug**, and should assert rather than rely on the
normaliser.

### 3. Canonical order is a stable topological sort, used for hashing and for diff output

The order is: honour the dependency DAG; break remaining ties byte-ordinally by canonical ID, then by
manifest field order. **No per-target clause is needed**, because after normalisation no two operations share
a slot (decision 2).

- **A diff emits operations in this order**, so comparing the same two projects twice produces the same
  Proposal. That is the consistency the owner asked for.
- **The intent digest is computed over this order.** For an authored Proposal the stored array keeps the
  order the author wrote; the digest is computed over the canonical view of it. Canonical bytes are therefore
  not always literally the stored bytes, and the sort must be specified precisely enough for a Python or Rust
  runner to reproduce — that obligation is [ADR 0007](0007-cross-language-digest-determinism.md)'s, extended.

### 4. Duplicate detection is explicitly *not* what this is for

> *I am less worried about two proposals that do the same thing being applied sequentially than you are.*

Accepted, and it lowers the bar usefully. Canonical ordering exists so that **a diff is reproducible**, not so
that two independently authored Proposals hash alike. Content equality — "has someone already proposed
this?" — keys on the **effect** digest, which the contract already makes stable under lowering
(`change-set-contract.md:548`). Nothing here needs to make intent digests collide.

### 5. `AGENTS.md` rule 5 is restated

From *"Operation array order is authoritative. Never silently reorder."* to:

> **Order is authoritative only where it is declared. The runner honours declared dependencies and never
> infers one from array position. No two operations in a finalized Proposal may address the same slot —
> `(target, field, discriminator)` — so there is nothing else for position to mean.**

The safety the original rule protected is intact: the runner still cannot reorder anything whose order
carries meaning. What changed is that meaning is carried explicitly — as a declared dependency — rather than
by position, and the one case where position *looked* meaningful turned out to be a Proposal that needed
normalising rather than ordering.

## Consequences

- **`F23`'s other three rules stand** — one fixed decomposition per comparison class with `feeding` never
  claiming a static anchor result; a fixed dispatch for discovered-footprint operations, where the contract
  already forecloses delete-plus-create; and normalise before diffing rather than after.
- **A validation obligation appears:** a Proposal whose declared dependencies contradict its authored order
  (B before A in the array, but A `dependsOn` B) is incoherent and must be refused at parse time rather than
  silently sorted into shape.
- **A cycle in the DAG is now a parse error**, since the canonical order is a topological sort and cannot be
  computed at all for a cyclic graph. Worth an explicit diagnostic.
- **`change-set-contract.md` needs its ordering section rewritten** to match; until it is, this ADR governs.
- **Risk accepted:** authors who previously relied on position to sequence two operations now have to declare
  the dependency. That is a real behaviour change and the intended one — the reliance was invisible and
  unverifiable. Cheap today: one operation kind, and the vocabulary is declared unstable (`B9b`).
- **A validation rule is added, and it is cheap to check:** no two operations in a finalized Proposal may
  share a slot. It is a single grouping over the operation list, and it makes the canonical order fully
  determined by the DAG.
- **What this does *not* cover:** two operations whose relative order matters through the *engine* rather than
  through a slot or a declared dependency — for example a `delete` whose ownership cascade removes the target
  of a later operation. Those are genuine dependencies and must be declared; the discovered-footprint
  machinery is what surfaces them, and a Proposal that omits the declaration fails at Dry Run rather than
  silently misapplying.
