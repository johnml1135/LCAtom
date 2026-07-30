# Should the contract be an IDL? — notes and the open decision

*2026-07-30. Records a design thread that until now existed only in a Codex session rollout and
nowhere in this repository.*

## Provenance

The Smithy proposal was made in a **Codex** session (`gpt-5.6-sol`), not in this repo and not in
Claude Code — which is why nothing here or in git history mentions it. Session
`019fa903-3a11-7c90-a115-36f9931857f5`, rollout under `~/.codex/sessions/2026/07/28/`, final two
turns timestamped 2026-07-30 00:27 and 00:43 UTC. **No files were produced.** The only artifact from
that whole thread is [grammar-workbench-overall-plan.md](grammar-workbench-overall-plan.md)
(commit `af79e6c`), written earlier in the session, before the IDL turn.

The prompt was: *"we should have some sort of API contract we can inspect and interrogate before we
start writing code… traceability between levels, inputs and outputs, error cods, and a mapping to the
LibLCM and HCLoader interfaces. Is there some 'API language' that can handle this?"*

## What was proposed

**Smithy plus custom traits**, evaluated against TypeSpec (strongest alternative) and JSON Schema plus
a traceability manifest (safest, weakest as a design environment). Concretely:

- Custom traits `@libLcm(types, coverageIds)`, `@hcLoader(symbols, fixtures)`,
  `@panGloss(concepts, fixtures)`, `@lowersTo([...])` attached to each operation.
- Two namespaces — `org.sil.lcatom.grammar.v1` (semantic intent) and `org.sil.harmony.grammar.v1`
  (state transitions) — with LCAtom *declaring* which Harmony changes it may lower into.
- A runtime **lowering trace** correlating each semantic operation with its generated changes.
- Modeled domain errors with stable codes; process exit codes as a coarse CLI adapter only.
- A **contract workbench before runtime code**: `contract operations | show | input-schema | errors |
  lowering | coverage | gaps | trace`.
- Generate JSON Schema and documentation first, **not** production C#. Generated files never canonical.

Compatible with [ADR 0013](adr/0013-harmony-is-the-change-mechanism.md): the lowering plan is
output-only, generic Harmony work stays in the Harmony repo, and no fork is implied.

## Two corrections

**1. It reintroduced preconditions.** The proposed input carried `baseline:
NaturalClassBaselineEvidence` and the error list carried `BaselineMismatch`; the follow-up listed
"preconditions" among what LCAtom owns. That is correct design for an RPC API and wrong for anything
that merges — see **D5** in [grill-decisions.md](grill-decisions.md). Baseline evidence is an
observation carried by the proposal, evaluated at review time. It is not a guard inside a change.

**2. `@libLcm(coverageIds:)` restates data we already hold.** Per **D4** and
[ADR 0014](adr/0014-generate-the-crdt-layer-from-masterlcmodel.md), the manifest is a 1:1 projection
of `MasterLCModel.xml` — 898 rows, 898 field declarations. Two files carrying the same fact will
disagree. The contract should carry a **reference** (`lcm:FsFeatStruc.FeatureSpecsOC`) that a build
step validates against the manifest, not a copy of it.

That validation is the **coverage gate**, and it is the most valuable thing an IDL buys here: it turns
"do we support grammar yet?" from an opinion into a build failure, which is precisely the honest
answer an AI client needs to "what can I actually do?"

## Rules that hold regardless of medium

1. Preconditions live in the proposal envelope as observations, evaluated at review/apply. Never in an
   `IChange`.
2. Operation kinds are stable versioned symbols; additive-only; deprecate, never rename. They become
   stored identity — labels, reports, coverage deltas, and approvals all hang off them.
3. Inputs are closed types with required/optional explicit.
4. Errors are enumerated per operation, with stable codes and a retryability classification. An AI that
   cannot distinguish "re-anchor and retry" from "stop and ask a human" will loop or give up.
5. Coverage references are validated against the manifest; unmapped in-scope rows fail the build.
6. The lowering trace is a first-class output artifact, not a log line. Without it a reviewer sees N
   anonymous field edits and no story.
7. Source symbols *locate* behavior; only fixtures *prove* it.
8. Write down what counts as a breaking change to an operation before there are fifty of them.

## The open decision

**Whether to adopt an IDL at all.** Gate it on a three-operation spike — a scalar set, an ordered
structural insert, and one genuinely hard operation (`MergeLexicalEntries`, or the natural-class
feature constraint).

Race Smithy against **annotated C# records plus generated JSON Schema**, not against TypeSpec. The
C# option is the incumbent and is free: there is one producer (.NET) and two consumers that both
speak JSON (an AI client, and a web/Avalonia UI). PanGloss is Rust but reads `.fwdata`, not this API —
so language-neutrality, the main thing an IDL sells, is worth less here than usual.

What an IDL still buys that C# does not: a review artifact that is not code, which matters for
getting team agreement on where the contract sits. What it costs: a JVM toolchain (Smithy) or npm
(TypeSpec), plus a custom plugin in Java or TypeScript, for a .NET/SIL team. The failure mode is a
model that is 80% custom traits and a bespoke compiler — a DSL wearing an IDL's name.
