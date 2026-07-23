# ADR 0001 — HermitCrab intent is a projection, not the canonical contract

Status: accepted (2026-07-23)

## Context

Grammar-editing consumers author edits in HermitCrab (HC) constructs. The HC grammar is derived from
LibLCM `Ph*`/`Mo*`/`Fs*` objects by HCLoader and is not 1:1 with them; HCLoader semantics drift
across FieldWorks/HC versions. Under the single-canonical-contract decision, one owner must define
what a grammar edit *is*.

## Decision

1. The canonical operation vocabulary targets **LibLCM objects**, engine-neutral. HC-shaped intent is
   a **versioned projection layer** (a reverse-HCLoader), its own C# project and CLI verbs, kept out
   of the canonical contract, the intent digest, and the conformance surface.
2. HC intent lowers via a baseline-dependent `Expand(hcIntent, baseline) -> canonical change set`.
   The **hashed/applied/diffed/rebased unit is the expanded LibLCM-object operations**; the HC
   one-liner is retained only as provenance.
3. **Fill, never frame.** Expansion furnishes only the owned interior of an explicitly-authored root.
   Creating, redefining, or deleting shared/referenced structure is explicit-only. Ownership is the
   test. Missing referents fail closed with an actionable error.

## Consequences

- One semantic identity per realized change, regardless of how it was authored.
- HCLoader drift touches only the adapter, never the immutable contract.
- The reverse-HCLoader is one owned library with its own tests, not a normative runner obligation.
- Diff, rebase, and the round-trip invariant live only at the LibLCM-object level.
- Structure changes only ever occur with explicit, reviewable intent.

See [HermitCrab projection](../hermitcrab-projection.md).
