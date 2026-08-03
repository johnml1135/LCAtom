# Prior art for three open design questions — `F23`, `B9`, `J42`

*Research of 2026-08-03. Purpose: turn three open-ended grill questions into **named options with
trade-offs**, so the decision is a choice between documented positions rather than an invention.*

All four claims this note makes about motif's own documents were checked against source before being
used:

| Claim | Verified at |
| --- | --- |
| Effect digests must be stable under lowering optimization | `change-set-contract.md:548`, `adr/0002:75` |
| Composer parameters ride as **non-hashed, re-runnable** provenance | `adr/0009:38-40` |
| A whole-object replace verb is the rejected `managedFields` anti-pattern | `adr/0009:122`, `change-set-contract.md:294` |
| Re-running a composer against a new baseline yields a new Change Set | `adr/0009:39-40` |

---

## `F23` — is the whole diff canonical, or only its ordered-sequence part?

### The standards do not help

**RFC 6902 (JSON Patch)** and **RFC 7386 (JSON Merge Patch)** specify only *application* semantics.
Neither says how to **generate** a patch from two documents — RFC 7386 explicitly pushes that onto the
caller. There is no off-the-shelf wire format for canonical diff *generation*. `change-set-contract.md`
is right to treat this as something to freeze from scratch.

### Four named strategies

| # | Strategy | Where it comes from | What it buys |
| --- | --- | --- | --- |
| 1 | **State identity, never edit-script identity** | Git — commits are snapshots, not diffs; rename detection is a similarity heuristic used *only for display*, never for identity | Two people making the same edit converge on the same hash **regardless of the path taken** |
| 2 | **Accept non-uniqueness; freeze *a* deterministic answer** | GumTree, difftastic, Autochrome, sdiff | Minimal edit scripts are provably non-unique (`ab`→`ba`: either `a` or `b` is a valid LCS). Every serious tool picks one heuristic and documents it as *a* choice, not *the* canonical one |
| 3 | **Diff as identity-keyed rows, not an edit script** | Dolt `dolt_diff()` / `DOLT_PATCH()` | Structurally identical to motif's existing `expectedEffects` `{canonicalId, field, before, after}` |
| 4 | **Skip diffing for identity entirely** | Unison — hashes code by AST structure, not name or history | Evidence *for* putting identity on the state side; not adoptable, because motif's operations are also the execution mechanism |

X-Diff (Wisconsin) adds the supporting argument that an **unordered** tree model should be preferred
wherever order is not semantically load-bearing — which maps exactly onto motif's existing `col`
(unordered) versus `seq` (ordered) comparison-class split.

### Recommendation

**The answer splits in two, and one half is already decided.**

**Half 1 — content equality should key on the *effect* digest, not the intent digest.** The contract
already makes the effect set state-based, read-back rather than replayed, identity-keyed, and required
to be *stable under lowering optimization* (`change-set-contract.md:548`). That is precisely the
Git/Dolt property: hash the result, not the path. `F24` already notes a diff-derived proposal's effects
equal the observed delta by construction. **"Did two people make the same edit" is therefore already
canonical, with no new machinery** — it just has not been *stated* as the dedup key.

**Half 2 — the intent digest still needs freezing beyond LIS,** because it hashes the chosen
decomposition into Layer-0 verbs, and several decompositions can produce one effect set. Four concrete
rules, all consistent with what is already frozen:

- **One total order across all emitted operations**, not just within a sequence. Reuse the trick ADR
  0007 already mandates for unordered-collection members: byte-ordinal UTF-8 comparison. Sort by
  target canonical ID, then manifest-declared field order, with `move` keeping its frozen LIS/anchor
  order *inside* that bucket.
- **One fixed decomposition rule per comparison class.** `col` is pure set difference and already
  unambiguous. Positional `seq` uses the existing LIS algorithm unchanged. `feeding` **must never claim
  a static LIS-anchor result at all** — consistent with the contract's own statement that identity and
  adjacency checks there can only pre-filter, never conclude "clean."
- **Discovered-footprint operations get a fixed dispatch, not an algorithmic choice.** The contract has
  already foreclosed the main ambiguity itself — *never delete-plus-create*, `reparent` mandatory for
  cross-owner moves. Worth restating as **already closed** rather than leaving open.
- **Normalize before diffing, not after.** Run the Canonical Semantic Snapshot's NFC/NFSC/run-boundary
  normalization on both sides first, so representational variance of the same value is never diffed as
  a change.

---

## `B9` — versioning contract for `contractVersions`

### Precedents

| System | Minor-safe | Forces major | Support window |
| --- | --- | --- | --- |
| **SemVer** | Backward-compatible functionality | Any incompatible change | — (vocabulary only) |
| **Protobuf** | New fields; new enum values; new extension | Renumbering/reusing field numbers; moving a field into an existing `oneof`; wire-incompatible type change | — (per-change, not per-release) |
| **Avro** | New reader field **with a default**; reordering (matched by name) | Removing a writer field the reader requires with no default; rename without alias | Pairwise reader/writer relation, not a window |
| **JSON Schema** | — | — | Forward-compatible only within one dialect; `$schema` pins it |
| **OpenAPI / REST practice** | New endpoints, new response fields, new optional params | Removing/renaming fields; changing required-ness; changing behaviour of an existing path | 6–24 months, by convention |
| **Kubernetes** | New API version; new optional field with a defined default | Removing or changing behaviour of anything shipped; **any change breaking round-trip conversion between served versions** | Beta: deprecated no sooner than **9 months or 3 minor releases** after introduction; removed no sooner than 9 months or 3 releases after deprecation — **whichever is longer**. GA: never removed within a major |

**Kubernetes is the strongest match** — and motif already took the `group/construct/verb` naming from
k8s's `(apiGroup, resource, verb)` triple in ADR 0009 §1. Versioning per group is the natural
continuation of a precedent already adopted. Two of its rules transplant directly:

- **The round-trip rule.** Any two served versions must convert losslessly into each other. For motif:
  a Change Set authored against group version N must deserialize losslessly under N+1 within the same
  major, or the bump was not really minor.
- **Machine-readable refusal, not silent degradation.** k8s emits a `Warning` header, an audit
  annotation, and a `removed_release`-labelled metric — never just a changelog entry.

### Recommended policy

- **Minor-safe** — adding a new `kind` (new construct, or new verb×construct combination) to a group;
  adding an **optional** field to an operation schema, because the contract already guarantees
  *omission always means leave untouched*, so old authors are unaffected by a field they do not know
  about. (Protobuf's additive rule plus Avro's default-required rule.)
- **Major-forcing** — removing or renaming a `kind`; changing an existing field's type, required-ness,
  or meaning; changing what a verb *does* for a construct; **anything that would silently change what
  a previously hashed intent digest means** for previously authored content.
- **Support window** — adopt the *shape* of the k8s rule (a **dual floor**: N minor versions **or** M
  months, whichever is longer) rather than a single number. Motif has three consumer runtimes on
  independent cadences plus AI-agent callers who cannot be assumed to read release notes at all. The
  numbers get calibrated once a real cadence exists; the dual floor is the transplantable part.
- **Refusal contract** — on an unhonorable version, return a **structured** payload naming
  `{group, requiredVersion, carriedVersion}`, not prose. The contract already commits to the posture
  ("a runner which cannot honor one can say which group, which version was required, and which it
  carries"); this makes it programmatically actionable for a Python or Rust runner, or an agent.

---

## `J42` — what does a batch composer store at rest?

### Precedents

| System | Stores at rest | Retains the query? | Staleness |
| --- | --- | --- | --- |
| **Terraform `plan -out`** | Fully resolved plan plus the whole config | Yes, embedded | **Hard gate** via state `lineage` + `serial`; a stale plan **errors** rather than silently re-resolving |
| **Flyway** | Resolved immutable SQL plus a checksum | No | Checksum mismatch on an applied migration is a hard validation error |
| **Liquibase** | Same — MD5 of resolved changeset content | No | Mismatch halts execution |
| **EF Core migrations** | Resolved `Up`/`Down` **plus** a full model snapshot used as the next diff's baseline | Effectively, as a structural snapshot | Re-diffs live model against stored snapshot |
| **Kubernetes SSA** | The **intent**, tracked per-field per-manager, continuously re-resolved | The query *is* the artifact | No staleness concept — built for perpetual reconciliation |
| **Sourcegraph Batch Changes** | `preview` computes concrete per-repo patches; `apply` materializes exactly those | Yes, the batch spec is kept separately and re-run for a *new* preview | Applying uses the previous preview, not a fresh run |

### The trade-off

- **Resolved only, no provenance** (Flyway, Liquibase) — simplest and checksum-verifiable, but the
  *reason* is gone and "catch the new matches" can only be hand-authored from scratch.
- **Query as truth** (Kubernetes SSA) — always current, but reopens exactly the problem this contract
  exists to close: a reviewer cannot approve effects for an unresolved query, and resolving at apply
  time means the reviewed thing and the applied thing can silently diverge. **Motif already rejected
  this pattern one layer down** (ADR 0009 §1 on `managedFields`); `J42` is the same trade-off one layer
  up, and the same answer applies.
- **Resolved plus non-hashed query** (Terraform, EF Core, Sourcegraph) — pay a little bookkeeping so
  that "what was reviewed" and "what can be re-run" are never conflated.

### Recommendation — `J42` is largely already decided

Resolved Layer-0 operations at rest, originating query retained as **non-hashed provenance**, is
**verbatim what ADR 0009 §1 already specifies for composers generally** (`adr/0009:38-40`: *"the
composer and its parameters ride as provenance on the emitted Change Set — non-hashed, re-runnable.
Re-running a composer against a new baseline yields a new Change Set"*). `J42` does not need new
machinery; it needs naming as a direct instance of a decision already taken.

**One genuinely new thing the precedent surfaces: a staleness gate.** Terraform errors on a saved plan
whose state lineage has moved. Motif has the identical mechanism already — the pre-flight/re-anchoring
footprint-digest-plus-engine-version check. **Re-reviewing or applying a resolved batch against a moved
baseline should be forced through that same drift path**, exactly like any other Change Set, rather
than silently re-resolving the query against new data. That is the residual decision.

---

## Sources

RFC [6902](https://www.rfc-editor.org/rfc/rfc6902) · RFC [7386](https://www.rfc-editor.org/rfc/rfc7386) ·
[GitHub — commits are snapshots, not diffs](https://github.blog/open-source/git/commits-are-snapshots-not-diffs/) ·
[gitdiffcore](https://git-scm.com/docs/gitdiffcore) ·
[Unison](https://www.unison-lang.org/docs/the-big-idea/) ·
[Dolt three-way merge](https://www.dolthub.com/blog/2024-06-19-threeway-merge/) ·
[Dolt SQL functions](https://docs.dolthub.com/sql-reference/version-control/dolt-sql-functions) ·
[difftastic tree-diffing survey](https://difftastic.wilfred.me.uk/tree_diffing.html) ·
[X-Diff (Wisconsin)](https://research.cs.wisc.edu/niagara/papers/xdiff.pdf) ·
[protobuf updating rules](https://protobuf.dev/programming-guides/proto3/#updating) ·
[Avro spec](https://avro.apache.org/docs/1.11.1/specification/) ·
[k8s deprecation policy](https://kubernetes.io/docs/reference/using-api/deprecation-policy/) ·
[k8s server-side apply](https://kubernetes.io/docs/reference/using-api/server-side-apply/) ·
[SemVer](https://semver.org/) ·
[JSON Schema dialects](https://json-schema.org/understanding-json-schema/reference/schema) ·
[Terraform `plan`](https://developer.hashicorp.com/terraform/cli/commands/plan) ·
[Terraform `apply`](https://developer.hashicorp.com/terraform/cli/commands/apply) ·
[Terraform stale-plan behaviour](https://discuss.hashicorp.com/t/question-on-error-saved-plan-is-stale/52912) ·
[Flyway migrations](https://github.com/flyway/flywaydb.org/blob/gh-pages/documentation/concepts/migrations.md) ·
[Liquibase checksums](https://docs.liquibase.com/oss/user-guide-4-33/what-is-a-changeset-checksum) ·
[EF Core migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/) ·
[EF Core model snapshot](https://www.learnentityframeworkcore.com/migrations/model-snapshot) ·
[IntelliJ rename](https://www.jetbrains.com/help/idea/rename-refactorings.html) ·
[Sourcegraph batch specs](https://docs.sourcegraph.com/batch_changes/explanations/how_src_executes_a_batch_spec)
