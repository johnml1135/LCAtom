# ADR 0002 — Version compatibility is detected by effect comparison, not declared

Status: accepted (2026-07-24)

## Context

Two workflows drive this decision:

1. One operator, possibly with an agent, works issues against a local change database backed up
   online much like `.fwdata`. Over time they upgrade FieldWorks, which bundles this runner.
2. A second operator pulls that backup onto a different machine running a **different FieldWorks
   version** and works the same issues.

They impose two requirements. Behavior may legitimately differ across versions — a smarter lowering,
a new endpoint that achieves the same result more efficiently, a changed default — and that
difference must never be applied silently, because a changed default is a semantic change wearing an
upgrade's clothing. Separately, an operator may open a Change Set their build cannot ingest at all,
and must be told to upgrade or rewrite rather than receive a partial application.

A declared compatibility algebra — per-group additive/non-additive classification plus a maintained
support matrix — serves neither well. It is a human *claim* about behavior, and the failure it most
needs to catch is a change misclassified as additive, which is precisely the failure a claim cannot
catch.

The contract already contains the mechanism. Assessment generates baseline-relative
`expectedEffects`, its determinism is already conditioned on the runner/version matrix, and delete
cascade drift is already specified as: compare re-assessment against the prior Assessment, emit the
full delta, let the application or user decide. Version drift is the same class of event as baseline
drift — the world moved under a recorded assessment — and needs no parallel apparatus.

## Decision

1. **Effect comparison is the compatibility oracle.** Generalize the existing cascade-drift rule to
   all drift: when a Change Set is re-assessed or applied against a prior Assessment, any change in
   `expectedEffects` is a typed diagnostic carrying the full delta, resolved by application policy
   and never auto-accepted. This covers baseline drift and version drift identically.

2. **Compare effects, not the Mutation Plan.** An improved lowering produces a different plan and
   identical effects. Treating plan inequality as drift would warn on every upgrade and train
   operators to click through the warnings that matter. The Mutation Plan remains diagnostic output;
   `expectedEffects` and their digests are normative.

3. **Strict closed parsing is the capability gate.** Unknown operation kinds and semantic properties
   are already rejected. That is what protects an older build, and it is the only mechanism that can:
   an operation the runner does not understand cannot be lowered, so no trace exists to compare.

4. **Declared group versions exist to make refusal actionable, not to compute compatibility.** A
   Change Set declares `contractVersions`, mapping each endpoint group it uses to the version it was
   authored against; the group is the leading segment of `kind`. On rejection the runner names the
   group, the version required, and the version it carries, so the operator can upgrade or rewrite.
   The map declares exactly the groups the operations use and is hashed as authored content.

5. **Interface versions may enter digests; implementation versions never do.** Contract group
   versions enter the intent digest and `projectionVersion` enters the semantic digest preimage.
   Runner version, LibLCM assembly/model version, and coverage-manifest version are provenance only,
   because a runner patch release must not change any identity.

6. **The projection is additive-stable and `projectionVersion` is folded into the semantic digest
   preimage.** A digest mismatch across versions is not a broken lineage; it is the trigger to
   re-assess and compare effects. Additive stability exists to keep that re-assessment rare, and
   folding the version in exists so two different projections cannot yield equal hex.

7. **No maintained compatibility matrix, no additive/non-additive governance, and no per-group
   applicability algebra in v1.** They are declarative substitutes for an oracle this design already
   has.

## Consequences

- A change misclassified as compatible is still caught, because the oracle observes behavior rather
  than trusting a version bump.
- Workflow 2 becomes concrete: re-assess against the local build, review the effect delta, decide.
- Older builds fail closed with an actionable upgrade instruction instead of a partial application.
- Learning whether a Change Set is still safe requires running assessment, which is required before
  apply regardless.
- Effect digests must be stable under lowering optimization, or every upgrade produces spurious
  drift. This becomes a conformance obligation with fixtures.
- Should independently released third-party conforming runners ever appear, a declared matrix may
  need revisiting. Deferred deliberately: v1 has one runner, bundled with FieldWorks.

See [architecture](../architecture.md#versioning) and
[normative change-set contract](../change-set-contract.md#assessment).
