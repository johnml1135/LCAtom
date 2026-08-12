# AGENTS.md

## Build and test with these, not with `dotnet` directly

```
./build.ps1     # comment hygiene, then compile
./test.ps1      # the above, then the full suite
```

**Use them every time.** `./build.ps1` runs the comment gate before it compiles, so a violation fails
in seconds rather than surviving until someone remembers to look. A bare `dotnet build` skips that
gate entirely, which is how the rules below decay into suggestions. `./test.ps1` runs the build gate
first, so one green run means clean comments, a clean compile, and a passing suite.

CI runs the same two scripts (`.github/workflows/ci.yml`), so anything they reject locally is
rejected there too — and anything they let through is not a CI surprise.

**`./test.ps1` needs a FieldWorks sibling checkout.** Much of the suite reads `../FieldWorks` —
`TestLangProj` for a live LibLCM cache, `DistFiles` for `ContextHelp.xml`. Those tests fail rather
than skip when it is absent, on purpose: a suite that quietly shrinks reports success for work it
never did. They carry `[Trait("Fixture", "FieldWorks")]`; CI runs `-Filter 'Fixture!=FieldWorks'` and
says so in the step name. **A test needing anything outside this repo must carry that trait**, or it
passes here and fails in CI.

Read `CONTEXT.md` first — it is the canonical glossary, and its terms are binding in code, comments,
CLI verbs, and prose. Then `README.md` and the documents in `docs/`.

**The vocabulary changed on 2026-07-31** ([ADR 0015](docs/adr/0015-proposal-assessment-dry-run-vocabulary.md)).
`Proposal` replaces *change set*; `Dry Run` is the LibLCM-side evaluation; `Assessment` means a PanGloss
run and nothing else. Documents written before that date use the old words — they are historical
records, not counter-examples.

## Comments

**Authoritative rules: `.claude/skills/code-comments/SKILL.md`. Enforced by
`tools/comment-hygiene.ps1`.** Ported from PanGloss, where the same rot was measured and corrected;
the intent is identical and the mechanics are adapted for C#.

A comment explains what the code cannot: why this, why not the obvious alternative, what breaks if you
change it. Code says what it does, git says when, and `docs/plan-motif.md` and `docs/issues.md` say
where the project is — a comment duplicating any of those three will eventually contradict it.

| Check | Standard |
|---|---|
| Implementation comment (`//`, or `///` on a `private` member) | **one line, at most 110 characters** — reflowing a paragraph onto one long line is not compliance |
| API doc (`///` on a `public`/`internal` type or member, an interface member, or an enum member) | long form as appropriate, and **complete**: no repo-relative `…md` path — `manifest/README.md` no more than `docs/…` — because a tooltip cannot open one. Cite an ADR by number, name a contract in prose, inline the fact — or give a URL |
| Plan and issue references — `MOT-22`, `docs/issues.md D8`, and the bare `D8`, `A1`, `J44` | **banned**, in string literals as well as comments; state the constraint instead |
| ADR citations | **allowed** — an ADR number is immutable; cite it for a decision, never for a status |
| Dates, slice/wiring status, history narrative, agent attribution | **banned** |
| A claim about another entity's behaviour | cite the pinning test — ``pinned by `TestName` `` — or reword |
| A `//` inside an emitter's raw-string template | scanned for banned references; exempt from the length rule, being a generated file's banner |
| A `///` inside an emitter's raw-string template | length-exempt, but **completeness still applies**: it lands in a public class and is read from a tooltip |
| A trailing `// note` sharing a line with code | scanned like any implementation comment — one line by construction, so **at most 110 characters** |
| Comment text assembled in a literal — `$"/// …"` | scanned as the comment it becomes. Only the interpolated *value* is beyond a static pass |
| `<see cref="X"/>` | keep; the C# compiler resolves it (CS1574), unlike Rust's intra-doc links |

Zero tolerance, no baseline: a baseline records the current count as acceptable, and re-baselining
after a rule change relabels old debt as the new normal. `src/`, `tests/`, `spikes/` and `tools/` are
all scanned on the same terms — exempting a directory is a baseline wearing a different hat.

Run `tools/verify-comment-only.ps1` after a comment sweep. It requires every line the diff **adds and
removes** to be a comment — the symmetric check, because a pure deletion satisfies the obvious
one-sided version while removing the `using` block along with the comment above it.

## Non-negotiable design rules

1. The canonical input is semantic CRUD+ intent, never a low-level property script or reflection
   plan.
2. A generated LibLCM Mutation Plan is output-only. It may be previewed and recorded, but never
   accepted as canonical input.
3. The caller supplies an already-loaded `LcmCache` and owns project lifecycle and persistence.
4. One complete Change Set is one atomic LibLCM unit of work. Individual operation methods never
   commit or open independent transactions.
5. **Order is authoritative only where it is declared.** The runner honours declared dependencies
   (`requires`, `dependsOn`) and never infers one from array position. **No two operations in a finalized
   Proposal may address the same slot** — `(target, field, discriminator)`, where the discriminator is the
   writing system for `Multi*` fields and the member id for collections — so there is nothing else for
   position to mean. Amended by [ADR 0026](docs/adr/0026-order-is-declared-not-positional.md); previously
   read "operation array order is authoritative, never silently reorder."
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
17. **Every ADR and every plan section opens with one or two sentences a non-specialist can read**,
    before any internal detail. Say what changes for someone using the product and why it matters;
    then use the internal vocabulary freely for the rest. The identifiers, cross-references, and
    class names below that opener are the point of these documents and must stay. This is a
    two-register rule, not a simplification pass: the opener is for the owner deciding whether to
    read on, the body is for whoever implements or audits it. A status line like "Slice A built;
    the poisoning guard now fires for real" fails the rule — it names machinery only.

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
