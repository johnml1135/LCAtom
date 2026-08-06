# AGENTS.md

Read `CONTEXT.md` first — it is the canonical glossary, and its terms are binding in code, comments,
CLI verbs, and prose. Then `README.md` and the documents in `docs/`.

**The vocabulary changed on 2026-07-31** ([ADR 0015](docs/adr/0015-proposal-assessment-dry-run-vocabulary.md)).
`Proposal` replaces *change set*; `Dry Run` is the LibLCM-side evaluation; `Assessment` means a PanGloss
run and nothing else. Documents written before that date use the old words — they are historical
records, not counter-examples.

## Non-negotiable design rules

1. The canonical input is semantic CRUD+ intent, never a low-level property script or reflection
   plan.
2. A generated LibLCM Mutation Plan is output-only. It may be previewed and recorded, but never
   accepted as canonical input.
3. The caller supplies an already-loaded `LcmCache` and owns project lifecycle and persistence.
4. One complete Change Set is one atomic LibLCM unit of work. Individual operation methods never
   commit or open independent transactions.
5. **Order is authoritative where it is declared or where two operations share a target.** The runner
   honours declared dependencies (`requires`, `dependsOn`) and same-target authored order, and never infers
   a dependency from array position. Amended by
   [ADR 0026](docs/adr/0026-order-is-declared-not-positional.md); previously read "operation array order is
   authoritative, never silently reorder."
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

**Two runtimes only: `net10.0` and `net48`. `net8.0` is not a target anywhere in this repository.**
Where one assembly must load in both, it targets `netstandard2.0`, which `net48` can consume.

**Actual current targets (measured from the `.csproj` files):**

| Project | Target | Why |
| --- | --- | --- |
| `SIL.Motif.Contract` | `netstandard2.0` | LibLCM-free wire contract; also consumed by non-.NET runners |
| `SIL.Motif.Model` | `netstandard2.0` | LibLCM-free |
| `SIL.Motif.Runner` | `netstandard2.0;net10.0` | **Must load in-process in `net48` FieldWorks** — see below |
| `SIL.Motif.Host` | `net10.0` | Opens/saves projects; FieldWorks is its own host and never loads this |
| `SIL.Motif.Cli` | `net10.0` | |
| `SIL.Motif.Tests` | `net10.0` | |

All LibLCM-dependent projects pin `SIL.LCModel 11.0.0-beta0150`.

**Why the Runner multi-targets.** There is no separate companion process. Assessment and apply both
need read-back from a live `LcmCache` ([ADR 0006](docs/adr/0006-engine-reality-apply-readback-preflight.md)),
so the Runner runs in-process in whatever host owns the cache: FieldWorks during the `net48`
coexistence window, and the `net10.0` host afterwards. `netstandard2.0` is the only target both can
load. `SIL.Motif.Runner/Compatibility/ModuleInitializerAttribute.cs` polyfills the one framework type
`netstandard2.0` lacks; the multi-target build and the full test suite pass.

**Package versions are not target frameworks.** `SIL.Motif.Contract` references
`System.Text.Json 8.0.5` because that is the current `netstandard2.0`-compatible release line, not
because anything targets `net8.0`. Do not "fix" it to match a TFM.

**Not yet built:** the `net48` FieldWorks-side adapter that hosts the Runner (UI-thread marshalling,
one undoable UOW, save and invalidation). The Runner is now *loadable* by it; the adapter itself is
Phase 9 ([implementation-plan.md](docs/implementation-plan.md)).

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
