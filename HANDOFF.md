# Handoff for the implementation session

Copy the block below into a fresh session.

---

Continue implementing the LCAtom plan in this repository.

Start by reading, in order:

1. `README.md` (see its Status section for the current headline)
2. `AGENTS.md`
3. `docs/build-stages.md` — what is built and verified, stage by stage
4. `docs/implementation-plan.md` — per-phase status against the full plan
5. `docs/operation-catalog-plan.md` — the roadmap from the one shipped operation to lexical + grammar
   completeness
6. the rest of `docs/`, including `docs/issues.md` for open and fixed issues

**The repository is not a blank plan.** Stages A–E of the walking skeleton
([build-stages.md](docs/build-stages.md)) are complete and verified: 82/82 tests pass
(`dotnet test`) against a real `LcmCache` opened on a real copied FieldWorks project. A change set can
be authored, assessed, applied atomically, read back, and logged end to end through the real
`Contract`/`Model`/`Runner`/`Host`/`Cli` projects. The catalog is exactly **one operation deep**
(`lexical/sense/setGloss`) — no create, delete, sequence, or grammar operations exist yet, and there
is no HermitCrab projection code at all. Treat the prose contract as normative. Do not redesign
settled decisions unless source evidence makes one impossible; if that happens, report the exact
evidence and propose the smallest amendment.

Do not restart at Phase 0/1. Phase 1 (contract kernel) is done. Phase 0 is **partial**: everything
needed to run has been proven except item 3, the multi-target compatibility spike — nothing in this
repo multi-targets today (see the per-project TFM table in `docs/implementation-plan.md`'s Phase 0
status and `AGENTS.md`'s Compatibility targets). Phases 2–4 are each partial in different, specific
ways — read `docs/implementation-plan.md`'s per-phase status markers before assuming what's left,
rather than guessing from the numbered requirements alone.

**Four sequencing decisions are still genuinely open** and are not yours to resolve by default —
identify them, but pick up only what the repository owner has actually approved:

- HermitCrab-projection scope and timing — when projection work starts relative to finishing more of
  the lexical catalog (see `docs/hc-surface-scope.md` and `docs/operation-catalog-plan.md`'s "machinery
  the skeleton lacks" item 13).
- Lexical-track vs. grammar-track build order — `docs/operation-catalog-plan.md`'s "Staged roadmap"
  lists L1–L5 then G0–G3, but does not mandate that L must finish before G starts.
- The cross-process protocol for non-.NET consumers (framing, error/exit-code contract,
  one-shot-vs-daemon) — recorded open as issue B13 in `docs/issues.md`.
- Manifest classification confidence — roughly 300 of 473 in-scope rows were classified by heuristic
  rather than an explicit citation; whether/how to verify them is open (issues B17, B18 in
  `docs/issues.md`).

Work test-first and make small, reviewable commits. Before changing files, inspect the current
branch/status and the pinned/current LibLCM package surface. Use an isolated branch/worktree for
non-trivial work.

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

The transaction boundary (Phase 0 item 5) and the contract kernel (Phase 1 — schemas, canonical ID
utility, canonical JSON/intent hash, diagnostics/Assessment/Receipt DTOs, fixed conformance fixtures)
are both delivered and covered by tests. The one Phase 0 gate still open is the multi-target proof
(item 3) — decide, with the repository owner, whether to close it now or defer it to Phase 9 (which
already carries the `net48` FieldWorks adapter as a release-time obligation) before investing further
in broad domain CRUD.

At each checkpoint, report:

- files and behavior added;
- tests run and exact results;
- coverage against the phase exit criteria;
- any discrepancy between the plan and actual LibLCM behavior;
- the next smallest implementation slice.

---
