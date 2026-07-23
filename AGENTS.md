# AGENTS.md

Read `README.md` and all documents in `docs/` before implementation.

## Non-negotiable design rules

1. The canonical input is semantic CRUD+ intent, never a low-level property script or reflection
   plan.
2. A generated LibLCM Mutation Plan is output-only. It may be previewed and recorded, but never
   accepted as canonical input.
3. The caller supplies an already-loaded `LcmCache` and owns project lifecycle and persistence.
4. One complete Change Set is one atomic LibLCM unit of work. Individual operation methods never
   commit or open independent transactions.
5. Operation array order is authoritative. Never silently reorder.
6. Rebase may refresh baseline-relative evidence or unambiguous placement anchors. It may not
   alter target, verb, value, identity, create/delete intent, or operation order.
7. Unknown operation kinds and semantic properties are rejected. Tool metadata belongs only in
   explicit non-semantic `extensions`.
8. Portable entity IDs use a 22-character unpadded base64url suffix encoding exactly 128 bits.
   Prefix and provenance are optional and unenforced.
9. Never use `Guid.ToByteArray()` or `new Guid(byte[])` for canonical ID conversion. Use textual
   GUID/network byte order as specified in `docs/change-set-contract.md`.
10. Same-type GUID collisions always warn about overwrite/reuse. Wrong-type collisions are genuine
    semantic conflicts unless an explicit storage-GUID override is authored.
11. Custom-field `flid` values are cache-local implementation details, never portable identity.
12. Diff is mechanical and linguistically unaware. Match entities only by exact canonical
    identity/GUID. Do not infer matches by forms, glosses, labels, fingerprints, or similarity.
13. Every LibLCM model member must be classified by a generated coverage inventory. Unclassified
    model changes fail CI.
14. Semantic snapshots must follow LibLCM normalization conventions, including NFD for plain
    Unicode and NFSC for rich strings.
15. The runner is storage-agnostic. Do not add Git, database, review, permission, or hosting logic.
16. Do not vendor or submodule LibLCM. Use pinned packages with a documented local-package override.

## Compatibility targets

- Contract library: `netstandard2.0`.
- LibLCM-dependent libraries: initially `netstandard2.0;net462;net8.0`.
- CLI/worker: initially `net8.0`.
- FieldWorks adapter/conformance host: compatible with current `net48` FieldWorks.

Verify the actual supported target matrix against the pinned LibLCM release during implementation.

## Definition of done for each operation family

An operation family is incomplete until it has:

- a closed schema;
- prose semantics;
- validation and lowering;
- preview effects;
- apply and read-back behavior;
- conflict/rebase behavior;
- semantic snapshot and diff support;
- positive, negative, rollback, round-trip, and conformance fixtures;
- a coverage-manifest mapping to the relevant LibLCM surface.
