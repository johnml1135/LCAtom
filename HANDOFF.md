# Handoff for the implementation session

Copy the block below into a fresh session.

---

Implement the approved LibLCM Change Sets plan in this repository.

Start by reading, in order:

1. `README.md`
2. `AGENTS.md`
3. every document in `docs/`

The repository currently contains the approved design and phased implementation plan, but no
implementation. Treat the prose contract as normative. Do not redesign settled decisions unless
source evidence makes one impossible; if that happens, report the exact evidence and propose the
smallest amendment.

Begin with Phase 0 and Phase 1 from `docs/implementation-plan.md`. Work test-first and make small,
reviewable commits. Before changing files, inspect the current branch/status and the pinned/current
LibLCM package surface. Use an isolated branch/worktree for non-trivial work.

Non-negotiable constraints:

- semantic closed CRUD+ operations, never arbitrary reflection/property/script input;
- caller supplies and owns an already-loaded `LcmCache`;
- the whole Change Set is one atomic LibLCM unit of work;
- operation array order is authoritative;
- 22-character unpadded base64url 128-bit suffix IDs with textual/network-order GUID mapping;
- RFC 8785 + SHA-256 canonical intent and semantic hashes;
- Assessment and Application Receipt are separate from portable intent;
- a later saved-file digest is a separate Artifact Attestation, not a mutation of the Receipt;
- output-only LibLCM Mutation Plan;
- exact-ID, linguistically unaware mechanical diff;
- LibLCM NFD/NFSC normalization;
- custom fields resolve by `(ownerClass, internalName)` and never serialize project-local `flid`;
- generated 100%-classified LibLCM model coverage manifest;
- storage, review, Git/database, permissions, UI, and fuzzy matching remain out of core scope.

An explicit storage-GUID override requires the caller to retain and later supply the
canonical-to-storage identity mapping; LibLCM does not persist it. Reassessment preserves intent;
an authored anchor rewrite is an explicit rebase producing a new Change Set and digest.

One question remains intentionally open: whether a namespaced logical contract key for shared
custom fields deserves a named v1 property. For now keep such keys in non-semantic `extensions`;
do not make them part of resolution or hashing without an explicit design decision.

For Phase 0, first prove the multi-target compatibility and transaction boundary against the chosen
pinned LibLCM version. For Phase 1, deliver the strict contract kernel, schemas, canonical ID
utility, canonical JSON/intent hash, diagnostics/Assessment/Receipt DTOs, and fixed conformance
fixtures. Do not begin broad domain CRUD until these gates pass.

At each checkpoint, report:

- files and behavior added;
- tests run and exact results;
- coverage against the phase exit criteria;
- any discrepancy between the plan and actual LibLCM behavior;
- the next smallest implementation slice.

---
