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

### 2. Operations on the same target keep their authored relative order

**This is the case the framing did not cover, and it is not optional.** Two `set` operations on the same
field are order-sensitive with no dependency between them — last one wins. Sorting them would change the
outcome silently, which is the worst available failure.

So: among operations whose target is the same canonical ID, **authored relative order is preserved and is
authoritative.** Across operations on different targets, order carries no meaning beyond declared
dependencies.

### 3. Canonical order is a stable topological sort, used for hashing and for diff output

The order is: honour the dependency DAG; within that, preserve authored relative order per target; break
remaining ties byte-ordinally by canonical ID, then by manifest field order.

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

> **Order is authoritative where it is declared or where two operations share a target. The runner honours
> declared dependencies and same-target authored order, and never infers a dependency from array position.**

The safety the original rule protected is intact: the runner still cannot reorder anything whose order
carries meaning. What changes is that meaning is now carried explicitly rather than by position, so the
positions that mean nothing can be normalised.

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
- **Risk accepted:** authors who previously relied on position to sequence two operations on *different*
  targets now have to declare it. That is a real behaviour change, and it is the intended one — the reliance
  was invisible and unverifiable. It is cheap today because there is one operation kind and the vocabulary is
  declared unstable (`B9b`).
